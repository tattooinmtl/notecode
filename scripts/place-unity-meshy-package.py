"""Place the Meshy DCC Unity adapter into a Unity project.

Why this script exists
----------------------

`meshy-dcc install --dcc-type unity` refuses to run on a machine with no
Unity Editor binary: the sidecar's preflight resolves the editor path and
fails with

    Unity Editor 2022.3.40f1 was not found; pass --dcc-path or set UNITY_EDITOR_PATH.

That gate exists because the sidecar also *launches* Unity to import the
package and let it self-register. The file placement itself does not need the
editor - it is a tree copy into `Packages/ai.meshy.dcc` plus a receipt and a
launch plan.

So this script calls the vendor's own placement functions directly
(`_replace_package` and `_write_launch_files`), skipping only the editor
preflight. Everything written is byte-for-byte what the sanctioned installer
would have written: same tree copy, same receipt schema, same launch plan.

What this does NOT do
---------------------

It does not make the adapter live. The Unity Editor package publishes a bridge
that the sidecar connects to, so the adapter only becomes usable when Unity
opens the project. Until Unity is installed and the project is opened, the
package sits in the project ready for it.

Usage:
    meshy-dcc-unity-sidecar-python place-unity-package.py <project> <dcc-path>
"""

import sys
from pathlib import Path

from meshy_dcc_unity import install as I


def main() -> int:
    project = Path(sys.argv[1]).resolve()
    dcc_path = Path(sys.argv[2])

    report = {
        "core_version": I.MIN_CORE_VERSION,
        "adapter_version": I.__version__,
        "unity_version": I.read_unity_version(project),
        "dcc_path": str(dcc_path),
        "python": sys.executable,
        "sidecar": str(Path(sys.executable).parent.parent / "bin" / "meshy-dcc-unity-sidecar.cmd"),
    }

    # The package tree: copy into Packages/ai.meshy.dcc, write the receipt.
    I._replace_package(project, report)
    # The launch plan and the sidecar config, so `meshy-dcc start-instance`
    # knows how to open the project and where the sidecar lives.
    I._write_launch_files(project, report)

    target = project / "Packages" / I.PACKAGE_NAME
    print(f"package : {target}")
    print(f"receipt : {project / I.RECEIPT_RELATIVE_PATH}")
    print(f"plan    : {project / I.LAUNCH_PLAN_RELATIVE_PATH}")
    print(f"config  : {project / I.SIDECAR_CONFIG_RELATIVE_PATH}")
    print(f"files   : {sum(1 for _ in target.rglob('*') if _.is_file())}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
