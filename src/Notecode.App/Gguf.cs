using System.Text;

namespace Notecode.App;

/// <summary>
/// A minimal reader for the GGUF header.
///
/// A GGUF file starts with the metadata key/value block, and one of those keys is
/// <c>tokenizer.chat_template</c> - the Jinja template the model was trained with.
/// llama.cpp uses it to shape the conversation, and an endpoint that speaks the raw
/// API needs the same template to produce the same prompts.
///
/// Only the header is read. The tensor data is at the end of the file and is never
/// touched, so this works on a 19 GiB model without loading it.
/// </summary>
internal static class Gguf
{
    private const uint Magic = 0x46554747; // "GGUF" little-endian

    /// <summary>The GGUF metadata value types.</summary>
    public enum ValueType
    {
        Uint8 = 0, Int8 = 1, Uint16 = 2, Int16 = 3, Uint32 = 4, Int32 = 5,
        Float32 = 6, Bool = 7, String = 8, Array = 9, Uint64 = 10, Int64 = 11, Float64 = 12
    }

    /// <summary>One metadata entry. The value is rendered as text.</summary>
    public sealed record Entry(string Key, string Value, ValueType Type);

    /// <summary>
    /// Reads the metadata block. Returns an empty map when the file is not a GGUF or
    /// the header cannot be parsed.
    /// </summary>
    public static Dictionary<string, Entry> ReadMetadata(string path)
    {
        var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

            if (br.ReadUInt32() != Magic) return result;
            _ = br.ReadUInt32();          // version
            _ = br.ReadUInt64();          // tensor count
            ulong kvCount = br.ReadUInt64();

            // A corrupt count would otherwise spin here for a very long time.
            if (kvCount > 100_000) return result;

            for (ulong i = 0; i < kvCount; i++)
            {
                string key = ReadString(br);
                var type = (ValueType)br.ReadUInt32();
                string value = ReadValue(br, type, out bool skip);
                if (!skip) result[key] = new Entry(key, value, type);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"GGUF metadata could not be read from {path}: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// The chat template embedded in the model, or null when the file has none.
    /// </summary>
    public static string? ChatTemplate(string path)
    {
        var meta = ReadMetadata(path);
        if (!meta.TryGetValue("tokenizer.chat_template", out var entry)) return null;
        return string.IsNullOrWhiteSpace(entry.Value) ? null : entry.Value;
    }

    /// <summary>The model's own name, if the file carries one.</summary>
    public static string? ModelName(string path)
    {
        var meta = ReadMetadata(path);
        foreach (var key in new[] { "general.name", "general.basename" })
        {
            if (meta.TryGetValue(key, out var e) && !string.IsNullOrWhiteSpace(e.Value)) return e.Value;
        }
        return null;
    }

    // ------------------------------------------------------------------ reading

    private static string ReadString(BinaryReader br)
    {
        ulong len = br.ReadUInt64();
        if (len > 64 * 1024 * 1024) throw new InvalidDataException("implausible string length");
        if (len == 0) return "";
        var bytes = br.ReadBytes((int)len);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Reads one value. Arrays are skipped rather than expanded: the tensor list in a
    /// model's metadata can be huge and nothing in it is interesting here.
    /// </summary>
    private static string ReadValue(BinaryReader br, ValueType type, out bool skip)
    {
        skip = false;
        switch (type)
        {
            case ValueType.Uint8: return br.ReadByte().ToString();
            case ValueType.Int8: return br.ReadSByte().ToString();
            case ValueType.Uint16: return br.ReadUInt16().ToString();
            case ValueType.Int16: return br.ReadInt16().ToString();
            case ValueType.Uint32: return br.ReadUInt32().ToString();
            case ValueType.Int32: return br.ReadInt32().ToString();
            case ValueType.Float32: return br.ReadSingle().ToString();
            case ValueType.Bool: return br.ReadByte() != 0 ? "true" : "false";
            case ValueType.String: return ReadString(br);
            case ValueType.Uint64: return br.ReadUInt64().ToString();
            case ValueType.Int64: return br.ReadInt64().ToString();
            case ValueType.Float64: return br.ReadDouble().ToString();
            case ValueType.Array:
            {
                var element = (ValueType)br.ReadUInt32();
                ulong count = br.ReadUInt64();
                if (count > 1_000_000) throw new InvalidDataException("implausible array length");
                var parts = new List<string>();
                for (ulong i = 0; i < count; i++)
                {
                    // Nested arrays are not expected in a metadata header; stop rather
                    // than guess at the layout.
                    if (element == ValueType.Array) throw new InvalidDataException("nested array");
                    parts.Add(ReadValue(br, element, out _));
                }
                skip = count == 0;
                return string.Join(", ", parts);
            }
            default:
                throw new InvalidDataException($"unknown GGUF value type {type}");
        }
    }
}
