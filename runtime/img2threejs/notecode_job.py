#!/usr/bin/env python3
"""One img2threejs job, as notecode runs it.

notecode's /img2threejs command calls this with the picture the user attached and
the folder the job goes in. It does the part of the img2threejs pipeline that a
script can do on its own, and leaves the part that needs a model's eyes - reading
the picture and authoring the sculpt spec - ready for the next step:

  1. intake    the picture is copied in as reference.png and probed
               (forge/stage1_intake/probe_image.py)
  2. state     the local checklist is initialised (forge/state.py init)
  3. spec      the pre-spec assessment and a starter ObjectSculptSpec are written
               (forge/stage2_spec/new_pre_spec_assessment.py, new_sculpt_spec.py)
  4. relief    a first model is built straight from the pixels: the silhouette
               becomes a solid plate and every part that stands out from the
               plate's colour is raised from it. It is a 2.5D relief, which is the
               right shape for a logo, an icon or a badge, and an honest first
               blockout for anything else.
  5. outputs   src/createObjectModel.js (procedural Three.js, ES module),
               model.glb (for Blender and Unity), viewer.html (opens offline,
               three.js is copied beside it) and job.json (what was done).

The relief is deliberately stated as approximate: a single picture does not show
the back or the depth of anything. The spec files are there so a model can carry
the job on through the full staged pipeline (SKILL.md) when the relief is not
enough.

Only the standard library is required. Pillow is used when it is installed
because it resizes better; without it the forge's own PNG/JPEG reader is used.

Prints one JSON object on its last line of stdout: the job manifest.
"""

from __future__ import annotations

import argparse
import base64
import datetime as _dt
import json
import math
import re
import shutil
import struct
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
FORGE = ROOT / "forge"
VENDOR_THREE = ROOT / "vendor" / "three.min.js"

# The workspace asset tree. notecode creates it too; it is repeated here so the
# script makes a usable layout when it is run by hand.
ASSET_FOLDERS = ["blender", "unity", "img2threejs", "threejs", "textures", "logos", "REA_outputs"]

MODEL_SPAN = 2.0          # the long edge of the model, in scene units
PLATE_THICKNESS = 0.14    # the plate everything stands on
FEATURE_HEIGHT = 0.16     # how far the most distinct colour rises above the plate
BEVEL_CELLS = 3           # how many grid cells the plate's edge rounds over


# ----------------------------------------------------------------- utilities
def slug(text: str) -> str:
    s = re.sub(r"[^A-Za-z0-9_-]+", "-", text.strip()).strip("-").lower()
    return s[:48] or "job"


def run_forge(args: list[str], cwd: Path, log: list[dict], step: str, timeout: int = 180) -> dict:
    """Runs one forge script. A failure is logged, not raised: the relief does not depend on it."""
    cmd = [sys.executable, "-I", *args]
    try:
        proc = subprocess.run(cmd, cwd=str(cwd), capture_output=True, text=True, timeout=timeout)
        entry = {
            "step": step,
            "ok": proc.returncode == 0,
            "returncode": proc.returncode,
            "stdout": proc.stdout[-3000:],
            "stderr": proc.stderr[-2000:],
        }
    except Exception as exc:  # timeout, missing interpreter
        entry = {"step": step, "ok": False, "error": str(exc)}
    log.append(entry)
    return entry


def srgb_to_linear(c: float) -> float:
    c /= 255.0
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


# -------------------------------------------------------------- the picture
def load_rgba(path: Path, long_edge: int) -> tuple[int, int, list[tuple[int, int, int, int]]]:
    """Loads the picture scaled so its long edge is at most long_edge pixels."""
    try:
        from PIL import Image  # type: ignore

        im = Image.open(path).convert("RGBA")
        scale = min(1.0, long_edge / max(im.size))
        if scale < 1.0:
            im = im.resize((max(2, round(im.width * scale)), max(2, round(im.height * scale))), Image.LANCZOS)
        return im.width, im.height, list(im.getdata())
    except ImportError:
        pass

    sys.path[:0] = [str(FORGE / "_shared"), str(FORGE / "stage1_intake")]
    from delight_albedo import load_image  # type: ignore

    w, h, pixels, _warnings = load_image(path)
    factor = max(1, math.ceil(max(w, h) / long_edge))
    if factor == 1:
        return w, h, [tuple(p) for p in pixels]
    gw, gh = max(2, w // factor), max(2, h // factor)
    out = []
    for gy in range(gh):
        for gx in range(gw):
            acc = [0, 0, 0, 0]
            n = 0
            for y in range(gy * factor, min(h, (gy + 1) * factor)):
                row = y * w
                for x in range(gx * factor, min(w, (gx + 1) * factor)):
                    p = pixels[row + x]
                    for i in range(4):
                        acc[i] += p[i]
                    n += 1
            out.append(tuple(v // max(1, n) for v in acc))
    return gw, gh, out


def silhouette(w: int, h: int, px: list[tuple[int, int, int, int]]) -> tuple[list[bool], str]:
    """The pixels that belong to the object.

    A picture with transparency is cut by its alpha. One without is cut by the
    colour at its corners, when the four corners agree on a background; when they
    do not, the whole frame is the object (a photo, a full-bleed icon).
    """
    if any(p[3] < 250 for p in px):
        return [p[3] >= 128 for p in px], "alpha"

    corners = [px[0], px[w - 1], px[(h - 1) * w], px[h * w - 1]]
    ref = tuple(sum(c[i] for c in corners) / 4 for i in range(3))
    spread = max(math.dist(c[:3], ref) for c in corners)
    if spread < 18:
        mask = [math.dist(p[:3], ref) > 40 for p in px]
        if sum(mask) > 0.05 * len(mask):
            return mask, "corner-colour"
    return [True] * len(px), "full-frame"


def distance_to_edge(w: int, h: int, mask: list[bool]) -> list[float]:
    """Chamfer distance, in cells, from each object pixel to the nearest background pixel."""
    big = 1e9
    d = [0.0 if not m else big for m in mask]
    # The frame counts as background, so a full-frame object still gets a bevel.
    for y in range(h):
        for x in range(w):
            if x == 0 or y == 0 or x == w - 1 or y == h - 1:
                if mask[y * w + x]:
                    d[y * w + x] = min(d[y * w + x], 1.0)
    for y in range(h):
        for x in range(w):
            i = y * w + x
            if d[i] == 0:
                continue
            if x > 0: d[i] = min(d[i], d[i - 1] + 1)
            if y > 0: d[i] = min(d[i], d[i - w] + 1)
            if x > 0 and y > 0: d[i] = min(d[i], d[i - w - 1] + 1.4142)
            if x < w - 1 and y > 0: d[i] = min(d[i], d[i - w + 1] + 1.4142)
    for y in range(h - 1, -1, -1):
        for x in range(w - 1, -1, -1):
            i = y * w + x
            if d[i] == 0:
                continue
            if x < w - 1: d[i] = min(d[i], d[i + 1] + 1)
            if y < h - 1: d[i] = min(d[i], d[i + w] + 1)
            if x < w - 1 and y < h - 1: d[i] = min(d[i], d[i + w + 1] + 1.4142)
            if x > 0 and y < h - 1: d[i] = min(d[i], d[i + w - 1] + 1.4142)
    return d


def _fit_plane(w: int, indices: list[int], values: list[float]) -> tuple[float, float, float]:
    """Least-squares a + b*x + c*y through the values at the given pixel indices."""
    n = len(indices)
    sx = sy = sxx = syy = sxy = sv = sxv = syv = 0.0
    for i, v in zip(indices, values):
        x, y = i % w, i // w
        sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y
        sv += v; sxv += x * v; syv += y * v
    m = [[n, sx, sy], [sx, sxx, sxy], [sy, sxy, syy]]
    r = [sv, sxv, syv]
    det = (m[0][0] * (m[1][1] * m[2][2] - m[1][2] * m[2][1])
           - m[0][1] * (m[1][0] * m[2][2] - m[1][2] * m[2][0])
           + m[0][2] * (m[1][0] * m[2][1] - m[1][1] * m[2][0]))
    if abs(det) < 1e-9:
        return (sv / max(n, 1), 0.0, 0.0)
    out = []
    for col in range(3):
        mm = [row[:] for row in m]
        for k in range(3):
            mm[k][col] = r[k]
        d = (mm[0][0] * (mm[1][1] * mm[2][2] - mm[1][2] * mm[2][1])
             - mm[0][1] * (mm[1][0] * mm[2][2] - mm[1][2] * mm[2][0])
             + mm[0][2] * (mm[1][0] * mm[2][1] - mm[1][1] * mm[2][0]))
        out.append(d / det)
    return out[0], out[1], out[2]


def _plate_distance(p, i: int, w: int, coeffs) -> float:
    x, y = i % w, i // w
    return math.sqrt(sum((p[ch] - (a + b * x + c * y)) ** 2 for ch, (a, b, c) in enumerate(coeffs)))


def relief_heights(w: int, h: int, px: list, mask: list[bool]) -> list[int]:
    """A height per pixel, 0..255, where 0 is the plate and 255 the most distinct feature.

    The plate's colour is fitted as a plane over the picture (colour = a + b*x +
    c*y per channel), so a plate with a linear gradient - most app icons - still
    reads as one plate. The fit is repeated with only the pixels close to the
    previous fit, which drops the features out of it. Everything that differs
    from the fitted plate is raised in proportion to how much.
    """
    idx = [i for i in range(w * h) if mask[i]]
    if not idx:
        return [0] * (w * h)
    keep = idx
    coeffs = [(0.0, 0.0, 0.0)] * 3
    for _ in range(4):
        coeffs = [_fit_plane(w, keep, [px[i][ch] for i in keep]) for ch in range(3)]
        dists = sorted(_plate_distance(px[i], i, w, coeffs) for i in idx)
        cut = max(24.0, dists[len(dists) // 2] * 2.0)
        kept = [i for i in idx if _plate_distance(px[i], i, w, coeffs) < cut]
        if len(kept) < 16:
            break
        keep = kept

    raw = [0.0] * (w * h)
    for i in idx:
        raw[i] = _plate_distance(px[i], i, w, coeffs)

    # Soften the steps so a feature has a slope instead of a cliff of pixels.
    smooth = raw[:]
    for _ in range(2):
        nxt = smooth[:]
        for y in range(h):
            for x in range(w):
                i = y * w + x
                if not mask[i]:
                    continue
                acc, n = 0.0, 0
                for dy in (-1, 0, 1):
                    for dx in (-1, 0, 1):
                        xx, yy = x + dx, y + dy
                        if 0 <= xx < w and 0 <= yy < h and mask[yy * w + xx]:
                            acc += smooth[yy * w + xx]
                            n += 1
                nxt[i] = acc / n
        smooth = nxt

    values = sorted(v for v, m in zip(smooth, mask) if m)
    top = values[int(len(values) * 0.995)] if values else 1.0
    floor = 12.0  # colour noise below this is still plate
    span = max(top - floor, 1.0)
    out = []
    for v, m in zip(smooth, mask):
        if not m:
            out.append(0)
            continue
        t = max(0.0, min(1.0, (v - floor) / span))
        t = t * t * (3 - 2 * t)
        out.append(round(t * 255))
    return out


# ----------------------------------------------------------------- the mesh
def build_mesh(w: int, h: int, mask: list[bool], heights: list[int], colors: list[tuple[int, int, int]], edge: list[float]):
    """The same mesh src/createObjectModel.js builds, for the GLB.

    One vertex per pixel on the top and one on the bottom. A cell between four
    object pixels gets a top face and a bottom face; a cell edge with no cell on
    the other side gets a wall.
    """
    cell = MODEL_SPAN / max(w - 1, h - 1)
    ox, oy = (w - 1) * cell / 2, (h - 1) * cell / 2

    def z_top(i: int) -> float:
        bevel = min(1.0, edge[i] / BEVEL_CELLS)
        bevel = math.sin(bevel * math.pi / 2)
        return PLATE_THICKNESS * (0.55 + 0.45 * bevel) + FEATURE_HEIGHT * heights[i] / 255 * bevel

    n = w * h
    pos, col = [], []
    for layer in (0, 1):  # 0 top, 1 bottom
        for y in range(h):
            for x in range(w):
                i = y * w + x
                z = z_top(i) if layer == 0 else 0.0
                pos.append((x * cell - ox, oy - y * cell, z))
                r, g, b = colors[i]
                k = 1.0 if layer == 0 else 0.55
                col.append((srgb_to_linear(r) * k, srgb_to_linear(g) * k, srgb_to_linear(b) * k))

    def in_cell(cx: int, cy: int) -> bool:
        if cx < 0 or cy < 0 or cx >= w - 1 or cy >= h - 1:
            return False
        i = cy * w + cx
        return mask[i] and mask[i + 1] and mask[i + w] and mask[i + w + 1]

    idx: list[int] = []
    for cy in range(h - 1):
        for cx in range(w - 1):
            if not in_cell(cx, cy):
                continue
            a = cy * w + cx
            b, c, d = a + 1, a + w, a + w + 1
            idx += [a, c, b, b, c, d]                      # top, facing +z
            idx += [n + a, n + b, n + c, n + b, n + d, n + c]  # bottom, facing -z
            # walls on edges that face out of the solid
            if not in_cell(cx, cy - 1):  # top edge a-b
                idx += [a, b, n + a, b, n + b, n + a]
            if not in_cell(cx, cy + 1):  # bottom edge c-d
                idx += [d, c, n + d, c, n + c, n + d]
            if not in_cell(cx - 1, cy):  # left edge a-c
                idx += [c, a, n + c, a, n + a, n + c]
            if not in_cell(cx + 1, cy):  # right edge b-d
                idx += [b, d, n + b, d, n + d, n + b]
    return pos, col, idx


def write_glb(path: Path, pos, col, idx, name: str) -> dict:
    """Writes the mesh as a binary glTF 2.0 file, with only the vertices that are used."""
    used = sorted(set(idx))
    remap = {old: new for new, old in enumerate(used)}
    p = [pos[i] for i in used]
    c = [col[i] for i in used]
    ix = [remap[i] for i in idx]

    normals = [[0.0, 0.0, 0.0] for _ in p]
    for t in range(0, len(ix), 3):
        i0, i1, i2 = ix[t], ix[t + 1], ix[t + 2]
        ax, ay, az = p[i0]; bx, by, bz = p[i1]; cx, cy, cz = p[i2]
        ux, uy, uz = bx - ax, by - ay, bz - az
        vx, vy, vz = cx - ax, cy - ay, cz - az
        nx, ny, nz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
        for k in (i0, i1, i2):
            normals[k][0] += nx; normals[k][1] += ny; normals[k][2] += nz
    for nrm in normals:
        length = math.sqrt(nrm[0] ** 2 + nrm[1] ** 2 + nrm[2] ** 2) or 1.0
        nrm[0] /= length; nrm[1] /= length; nrm[2] /= length

    pos_b = b"".join(struct.pack("<3f", *v) for v in p)
    nrm_b = b"".join(struct.pack("<3f", *v) for v in normals)
    col_b = b"".join(struct.pack("<3f", *v) for v in c)
    idx_b = struct.pack(f"<{len(ix)}I", *ix)
    blob = pos_b + nrm_b + col_b + idx_b
    offsets = [0, len(pos_b), len(pos_b) + len(nrm_b), len(pos_b) + len(nrm_b) + len(col_b)]

    mins = [min(v[k] for v in p) for k in range(3)]
    maxs = [max(v[k] for v in p) for k in range(3)]
    gltf = {
        "asset": {"version": "2.0", "generator": "notecode img2threejs relief"},
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": [{"mesh": 0, "name": name}],
        "meshes": [{"name": name, "primitives": [{
            "attributes": {"POSITION": 0, "NORMAL": 1, "COLOR_0": 2},
            "indices": 3, "material": 0}]}],
        "materials": [{"name": f"{name}-vertex-colour",
                       "pbrMetallicRoughness": {"baseColorFactor": [1, 1, 1, 1],
                                                "metallicFactor": 0.0, "roughnessFactor": 0.45}}],
        "buffers": [{"byteLength": len(blob)}],
        "bufferViews": [
            {"buffer": 0, "byteOffset": offsets[0], "byteLength": len(pos_b), "target": 34962},
            {"buffer": 0, "byteOffset": offsets[1], "byteLength": len(nrm_b), "target": 34962},
            {"buffer": 0, "byteOffset": offsets[2], "byteLength": len(col_b), "target": 34962},
            {"buffer": 0, "byteOffset": offsets[3], "byteLength": len(idx_b), "target": 34963},
        ],
        "accessors": [
            {"bufferView": 0, "componentType": 5126, "count": len(p), "type": "VEC3", "min": mins, "max": maxs},
            {"bufferView": 1, "componentType": 5126, "count": len(p), "type": "VEC3"},
            {"bufferView": 2, "componentType": 5126, "count": len(p), "type": "VEC3"},
            {"bufferView": 3, "componentType": 5125, "count": len(ix), "type": "SCALAR"},
        ],
    }
    js = json.dumps(gltf, separators=(",", ":")).encode()
    js += b" " * (-len(js) % 4)
    blob += b"\0" * (-len(blob) % 4)
    total = 12 + 8 + len(js) + 8 + len(blob)
    with path.open("wb") as f:
        f.write(struct.pack("<4sII", b"glTF", 2, total))
        f.write(struct.pack("<I4s", len(js), b"JSON")); f.write(js)
        f.write(struct.pack("<I4s", len(blob), b"BIN\0")); f.write(blob)
    return {"vertices": len(p), "triangles": len(ix) // 3, "bytes": total}


# ------------------------------------------------------------ the three.js
FACTORY_TEMPLATE = """/* createObjectModel - __NAME__
 * Generated by img2threejs (notecode relief pass) from __SOURCE__.
 *
 * A 2.5D relief: the silhouette is a solid plate and every part of the picture
 * that stands out from the plate's colour is raised from it. Approximate by
 * construction - a single picture does not show depth or the back.
 * The geometry is rebuilt procedurally from the height field below.
 */
import * as THREE from 'three';

const FIELD = {
  width: __W__,
  height: __H__,
  span: __SPAN__,
  plate: __PLATE__,
  feature: __FEATURE__,
  bevelCells: __BEVEL__,
  // per pixel, base64: mask (1 byte), height 0..255 (1 byte), edge distance x10 (1 byte), rgb (3 bytes)
  data: '__DATA__'
};

function decode(b64) {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

function srgbToLinear(c) {
  c /= 255;
  return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
}

export function createObjectModel() {
  const { width: w, height: h, span, plate, feature, bevelCells } = FIELD;
  const bytes = decode(FIELD.data);
  const n = w * h;
  const mask = (i) => bytes[i * 6] === 1;
  const cell = span / Math.max(w - 1, h - 1);
  const ox = (w - 1) * cell / 2, oy = (h - 1) * cell / 2;

  const positions = new Float32Array(n * 2 * 3);
  const colors = new Float32Array(n * 2 * 3);
  for (let layer = 0; layer < 2; layer++) {
    for (let y = 0; y < h; y++) {
      for (let x = 0; x < w; x++) {
        const i = y * w + x;
        const o = (layer * n + i) * 3;
        let z = 0;
        if (layer === 0) {
          const bevel = Math.sin(Math.min(1, bytes[i * 6 + 2] / 10 / bevelCells) * Math.PI / 2);
          z = plate * (0.55 + 0.45 * bevel) + feature * bytes[i * 6 + 1] / 255 * bevel;
        }
        positions[o] = x * cell - ox;
        positions[o + 1] = oy - y * cell;
        positions[o + 2] = z;
        const k = layer === 0 ? 1 : 0.55;
        colors[o] = srgbToLinear(bytes[i * 6 + 3]) * k;
        colors[o + 1] = srgbToLinear(bytes[i * 6 + 4]) * k;
        colors[o + 2] = srgbToLinear(bytes[i * 6 + 5]) * k;
      }
    }
  }

  const inCell = (cx, cy) => {
    if (cx < 0 || cy < 0 || cx >= w - 1 || cy >= h - 1) return false;
    const i = cy * w + cx;
    return mask(i) && mask(i + 1) && mask(i + w) && mask(i + w + 1);
  };
  const index = [];
  for (let cy = 0; cy < h - 1; cy++) {
    for (let cx = 0; cx < w - 1; cx++) {
      if (!inCell(cx, cy)) continue;
      const a = cy * w + cx, b = a + 1, c = a + w, d = a + w + 1;
      index.push(a, c, b, b, c, d);
      index.push(n + a, n + b, n + c, n + b, n + d, n + c);
      if (!inCell(cx, cy - 1)) index.push(a, b, n + a, b, n + b, n + a);
      if (!inCell(cx, cy + 1)) index.push(d, c, n + d, c, n + c, n + d);
      if (!inCell(cx - 1, cy)) index.push(c, a, n + c, a, n + a, n + c);
      if (!inCell(cx + 1, cy)) index.push(b, d, n + b, d, n + d, n + b);
    }
  }

  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
  geometry.setAttribute('color', new THREE.BufferAttribute(colors, 3));
  geometry.setIndex(index);
  geometry.computeVertexNormals();

  const material = new THREE.MeshStandardMaterial({
    vertexColors: true,
    roughness: 0.45,
    metalness: 0.0
  });

  const mesh = new THREE.Mesh(geometry, material);
  mesh.name = '__NAME__-relief';
  mesh.castShadow = true;
  mesh.receiveShadow = true;

  const root = new THREE.Group();
  root.name = '__NAME__';
  root.add(mesh);
  return root;
}
"""

VIEWER_TEMPLATE = """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>__NAME__ - img2threejs</title>
<style>
  html, body { margin: 0; height: 100%; background: #14121c; color: #e8e6f0; font: 13px system-ui, sans-serif; overflow: hidden; }
  #panel { position: fixed; left: 16px; top: 14px; pointer-events: none; }
  #panel h1 { margin: 0 0 4px; font-size: 15px; font-weight: 600; }
  #panel div { opacity: .65; }
  #ref { position: fixed; right: 16px; top: 14px; width: 120px; background: #1f1c2b; border-radius: 8px; padding: 6px; }
  #ref img { width: 100%; display: block; border-radius: 4px; }
  #ref span { display: block; text-align: center; opacity: .6; margin-top: 4px; font-size: 11px; }
  #err { position: fixed; inset: 0; display: none; padding: 24px; white-space: pre-wrap; color: #ff8a8a; background: #14121cee; }
</style>
</head>
<body>
<div id="panel"><h1>__NAME__</h1><div>img2threejs relief &middot; drag to orbit &middot; scroll to zoom</div></div>
<div id="ref"><img src="reference.png" alt="reference"><span>reference</span></div>
<div id="err"></div>
<script src="three.min.js"></script>
<script>
__FACTORY__
</script>
<script>
(function () {
  try {
    const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.setSize(innerWidth, innerHeight);
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    renderer.shadowMap.enabled = true;
    document.body.appendChild(renderer.domElement);

    const scene = new THREE.Scene();
    scene.background = new THREE.Color('#14121c');
    const camera = new THREE.PerspectiveCamera(35, innerWidth / innerHeight, 0.05, 100);

    scene.add(new THREE.HemisphereLight('#d8d4ff', '#201a30', 0.9));
    const key = new THREE.DirectionalLight('#ffffff', 2.2);
    key.position.set(2.5, 3, 4);
    key.castShadow = true;
    scene.add(key);
    const rim = new THREE.DirectionalLight('#9fd8ff', 1.1);
    rim.position.set(-3, 1.5, -2);
    scene.add(rim);

    const model = createObjectModel();
    const box = new THREE.Box3().setFromObject(model);
    model.position.sub(box.getCenter(new THREE.Vector3()));
    scene.add(model);
    const radius = box.getSize(new THREE.Vector3()).length() / 2;

    let yaw = 0.45, pitch = 0.28, zoom = 1.0, drag = null;
    function place() {
      // far enough that the bounding sphere fits both the height and the width of the window
      const halfFov = THREE.MathUtils.degToRad(camera.fov) / 2;
      const fit = radius / Math.sin(Math.min(halfFov, Math.atan(Math.tan(halfFov) * camera.aspect)));
      const r = fit * 1.05 * zoom;
      camera.position.set(Math.sin(yaw) * Math.cos(pitch) * r, Math.sin(pitch) * r, Math.cos(yaw) * Math.cos(pitch) * r);
      camera.lookAt(0, 0, 0);
    }
    addEventListener('pointerdown', e => drag = [e.clientX, e.clientY]);
    addEventListener('pointerup', () => drag = null);
    addEventListener('pointermove', e => {
      if (!drag) return;
      yaw -= (e.clientX - drag[0]) * 0.008;
      pitch = Math.max(-1.4, Math.min(1.4, pitch + (e.clientY - drag[1]) * 0.008));
      drag = [e.clientX, e.clientY];
    });
    addEventListener('wheel', e => { zoom = Math.max(0.3, Math.min(4, zoom * (e.deltaY > 0 ? 1.1 : 0.9))); }, { passive: true });
    addEventListener('resize', () => {
      camera.aspect = innerWidth / innerHeight;
      camera.updateProjectionMatrix();
      renderer.setSize(innerWidth, innerHeight);
    });
    (function frame() { place(); renderer.render(scene, camera); requestAnimationFrame(frame); })();
    window.__img2threejsReady = true;
  } catch (e) {
    const box = document.getElementById('err');
    box.style.display = 'block';
    box.textContent = String(e && e.stack || e);
  }
})();
</script>
</body>
</html>
"""


def write_factory(job: Path, name: str, source: str, w: int, h: int, mask, heights, edge, colors) -> str:
    raw = bytearray()
    for i in range(w * h):
        raw.append(1 if mask[i] else 0)
        raw.append(heights[i])
        raw.append(min(255, round(edge[i] * 10)) if mask[i] else 0)
        raw.extend(colors[i])
    safe_name = re.sub(r"[^A-Za-z0-9 _-]", "", name) or "model"
    code = (FACTORY_TEMPLATE
            .replace("__NAME__", safe_name)
            .replace("__SOURCE__", re.sub(r"[^A-Za-z0-9 ._-]", "", source))
            .replace("__W__", str(w)).replace("__H__", str(h))
            .replace("__SPAN__", repr(MODEL_SPAN)).replace("__PLATE__", repr(PLATE_THICKNESS))
            .replace("__FEATURE__", repr(FEATURE_HEIGHT)).replace("__BEVEL__", str(BEVEL_CELLS))
            .replace("__DATA__", base64.b64encode(bytes(raw)).decode()))
    (job / "src").mkdir(exist_ok=True)
    (job / "src" / "createObjectModel.js").write_text(code, encoding="utf-8")

    # The viewer runs from file:// where ES modules cannot load, so the factory is
    # inlined as a classic script against the global THREE.
    classic = code.replace("import * as THREE from 'three';\n", "").replace("export function", "function")
    (job / "viewer.html").write_text(
        VIEWER_TEMPLATE.replace("__NAME__", safe_name).replace("__FACTORY__", classic), encoding="utf-8")
    if VENDOR_THREE.exists():
        shutil.copy2(VENDOR_THREE, job / "three.min.js")
    return safe_name


# ------------------------------------------------------------------- the job
def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description="Run one img2threejs job for notecode.")
    ap.add_argument("--image", required=True, help="the reference picture")
    ap.add_argument("--out", required=True, help="the folder the job folder is created in")
    ap.add_argument("--name", default="", help="the object's name (default: the file name)")
    ap.add_argument("--resolution", type=int, default=160, help="relief grid, long edge in cells")
    ap.add_argument("--job-dir", default="", help="use exactly this job folder instead of a new one")
    args = ap.parse_args(argv)

    image = Path(args.image).resolve()
    if not image.is_file():
        print(json.dumps({"ok": False, "error": f"no such picture: {image}"}))
        return 2

    name = args.name.strip() or image.stem
    stamp = _dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    out_root = Path(args.out).resolve()
    job = Path(args.job_dir).resolve() if args.job_dir else out_root / f"{slug(name)}-{stamp}"
    job.mkdir(parents=True, exist_ok=True)

    log: list[dict] = []

    # 1. intake
    reference = job / "reference.png"
    try:
        from PIL import Image  # type: ignore
        Image.open(image).save(reference)
    except ImportError:
        if image.suffix.lower() != ".png":
            reference = job / f"reference{image.suffix.lower()}"
        shutil.copy2(image, reference)
    (job / "intake").mkdir(exist_ok=True)
    probe = run_forge([str(FORGE / "stage1_intake" / "probe_image.py"), reference.name], job, log, "probe")
    if probe.get("ok"):
        (job / "intake" / "probe.json").write_text(probe["stdout"], encoding="utf-8")

    # 2. state, 3. spec scaffolding - the inputs a model needs to carry the job on
    run_forge([str(FORGE / "stage2_spec" / "new_pre_spec_assessment.py"), name,
               "--image", reference.name, "--complexity", "simple", "--out", "assessment.json", "--force"],
              job, log, "assessment")
    run_forge([str(FORGE / "stage2_spec" / "new_sculpt_spec.py"), name,
               "--image", reference.name, "--assessment", "assessment.json",
               "--out", "object-sculpt-spec.json", "--force", "--no-environment"],
              job, log, "spec")
    run_forge([str(FORGE / "state.py"), "init", "--state", ".img2threejs/state.json",
               "--reference", reference.name, "--profile", "generic", "--spec", "object-sculpt-spec.json"],
              job, log, "state")

    # 4. relief
    w, h, px = load_rgba(image, max(16, min(256, args.resolution)))
    mask, mask_mode = silhouette(w, h, px)
    edge = distance_to_edge(w, h, mask)
    heights = relief_heights(w, h, px, mask)
    colors = [(p[0], p[1], p[2]) for p in px]

    # A transparent pixel's colour is undefined; give it the nearest plate colour so
    # the walls do not pick up black fringes.
    for y in range(h):
        last = None
        for x in range(w):
            i = y * w + x
            if mask[i]:
                last = colors[i]
            elif last is not None:
                colors[i] = last
    safe_name = write_factory(job, name, image.name, w, h, mask, heights, edge, colors)
    log.append({"step": "relief", "ok": True, "grid": [w, h], "silhouette": mask_mode,
                "objectPixels": sum(mask)})

    # 5. outputs
    pos, col, idx = build_mesh(w, h, mask, heights, colors, edge)
    glb = write_glb(job / "model.glb", pos, col, idx, safe_name)
    log.append({"step": "glb", "ok": True, **glb})

    manifest = {
        "ok": True,
        "name": name,
        "job": str(job),
        "source": str(image),
        "createdAt": _dt.datetime.now().isoformat(timespec="seconds"),
        "method": "img2threejs relief (2.5D, approximate: one view, no hidden geometry)",
        "files": {
            "reference": reference.name,
            "threejs": "src/createObjectModel.js",
            "glb": "model.glb",
            "viewer": "viewer.html",
            "spec": "object-sculpt-spec.json",
            "assessment": "assessment.json",
            "state": ".img2threejs/state.json",
        },
        "mesh": glb,
        "steps": [{k: v for k, v in s.items() if k not in ("stdout", "stderr")} for s in log],
        "next": "To go beyond the relief, follow SKILL.md from step 1 in this folder: "
                f"python \"{FORGE / 'next.py'}\" --state .img2threejs/state.json object-sculpt-spec.json",
    }
    (job / "job.json").write_text(json.dumps({**manifest, "log": log}, indent=2), encoding="utf-8")
    print(json.dumps(manifest))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
