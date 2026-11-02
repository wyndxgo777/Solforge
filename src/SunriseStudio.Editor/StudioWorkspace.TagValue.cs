/**
 * File: StudioWorkspace partial: writes one little-endian scalar into a tag's raw bytes (the MCP set_tag_value tool).
 */
using System.Buffers.Binary;
using System.Globalization;

namespace SunriseStudio.Editor;

/** Raw scalar edits (part of StudioWorkspace). */
public sealed partial class StudioWorkspace
{
    /** The scalar types set_tag_value accepts. */
    public static readonly string[] TagValueTypes = ["u8", "u16", "u32", "i32", "f32", "hash"];

    /** Byte width of a set_tag_value type, or 0 when unknown. */
    public static int TagValueSize(string type) => type switch { "u8" => 1, "u16" => 2, "u32" or "i32" or "f32" or "hash" => 4, _ => 0 };

    /**
     * Replaces the scalar at offset of the open tag with value and saves it as one undoable project change; returns the old and new text.
     * Refuses offsets inside the 8-byte size header or past the end.
     */
    public (string Old, string New) SetTagValue(uint tag, int offset, string type, string value)
    {
        int size = TagValueSize(type);
        if (size == 0) throw new ArgumentException("type must be u8, u16, u32, i32, f32 or hash.", nameof(type));
        if (Document?.Entry.TagHash != tag) throw new InvalidOperationException("Select the tag first.");
        byte[] bytes = ReadRawBytes(tag);
        if (offset < 8) throw new InvalidDataException("offset must be 8 or more; the first 8 bytes are the tag's size header.");
        if ((long)offset + size > bytes.Length) throw new InvalidDataException($"offset {offset} + {size} bytes runs past the end of the tag ({bytes.Length} bytes).");
        string old = FormatTagValue(bytes, offset, type);
        byte[] edited = (byte[])bytes.Clone();
        var span = edited.AsSpan(offset, size);
        var invariant = CultureInfo.InvariantCulture;
        string text = value.Trim();
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        string digits = hex ? text[2..] : text;
        var style = hex ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer;
        switch (type)
        {
            case "u8" when byte.TryParse(digits, style, invariant, out byte u8): span[0] = u8; break;
            case "u16" when ushort.TryParse(digits, style, invariant, out ushort u16): BinaryPrimitives.WriteUInt16LittleEndian(span, u16); break;
            case "u32" or "hash" when uint.TryParse(digits, style, invariant, out uint u32): BinaryPrimitives.WriteUInt32LittleEndian(span, u32); break;
            case "i32" when hex && uint.TryParse(digits, style, invariant, out uint bits): BinaryPrimitives.WriteUInt32LittleEndian(span, bits); break;
            case "i32" when !hex && int.TryParse(digits, style, invariant, out int i32): BinaryPrimitives.WriteInt32LittleEndian(span, i32); break;
            case "f32" when float.TryParse(text, NumberStyles.Float, invariant, out float f32) && float.IsFinite(f32):
                BinaryPrimitives.WriteSingleLittleEndian(span, f32); break;
            default: throw new InvalidDataException($"'{value}' is not a valid {type}.");
        }
        if (edited.AsSpan().SequenceEqual(bytes)) { Status = "No changes"; return (old, old); }
        ApplyAssetEdits(new Dictionary<uint, byte[]> { [tag] = edited }, $"Set {type} at 0x{offset:X}");
        return (old, FormatTagValue(edited, offset, type));
    }

    private static string FormatTagValue(byte[] bytes, int offset, string type) => type switch
    {
        "u8" => bytes[offset].ToString(CultureInfo.InvariantCulture),
        "u16" => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset)).ToString(CultureInfo.InvariantCulture),
        "u32" => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)).ToString(CultureInfo.InvariantCulture),
        "hash" => "0x" + BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)).ToString("X8", CultureInfo.InvariantCulture),
        "i32" => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset)).ToString(CultureInfo.InvariantCulture),
        _ => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset)).ToString("R", CultureInfo.InvariantCulture)
    };
}
