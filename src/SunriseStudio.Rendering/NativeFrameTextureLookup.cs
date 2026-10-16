namespace SunriseStudio.Rendering;

/** Frame-texture part of GameMaterialReader: resolves the render-global texture table. */
public sealed partial class GameMaterialReader
{
    /** Frame extern resource 24 comes from the render-global texture table's field 0x14. */
    public uint ReadFrameTexture24(uint renderGlobalsTag)
    {
        if (!catalog.TryGetEntry(renderGlobalsTag, out var root) || root.Reference != 0x80806CB1)
            throw new InvalidDataException("Frame textures need a render-global root.");
        byte[] globals = catalog.Read(renderGlobalsTag);
        if (globals.Length < 0x34 || U64(globals, 0) != (ulong)globals.Length)
            throw new InvalidDataException("Render-global header size is invalid.");
        uint tableTag = U32(globals, 0x30);
        if (!catalog.TryGetEntry(tableTag, out var tableEntry) || tableEntry.Reference != 0x80806B99)
            throw new InvalidDataException("Frame texture table has the wrong class.");
        byte[] table = catalog.Read(tableTag);
        if (table.Length < 0x18 || U64(table, 0) != (ulong)table.Length)
            throw new InvalidDataException("Frame texture table size is invalid.");
        uint texture = U32(table, 0x14);
        if (!catalog.TryGetEntry(texture, out var entry) || entry.Type != 32)
            throw new InvalidDataException("Frame texture 24 does not reference a texture header.");
        return texture;
    }
}
