namespace SunriseStudio.Scene;

/** Owner part of StudioNameIndex: reverse links from tags to the assets that reference them. */
public sealed partial class StudioNameIndex
{
    /** Known owners may be partial; readers must revalidate them against the current catalog. */
    public IReadOnlyList<uint> GetAssetOwners(uint tag) => owners.TryGetValue(tag, out var links)
        ? links.Select(link => link.Tag).Distinct().ToArray() : [];

    /** Reverse links follow the same typed arrays as scene and material readers. */
    private void LoadOwners(CancellationToken token, Action<string>? progress)
    {
        var entries = catalog.Entries.Where(x => x.Type is 32 or 33 or 34 || x.Type is 8 or 16
            && x.Reference is 0x80809C0F or 0x808071A7 or 0x80807194 or 0x808073A5 or 0x808071E8).ToArray();
        var modelTags = new Dictionary<uint, uint>();
        for (int i = 0; i < entries.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = entries[i];
            if (entry.Type is 32 or 33 or 34)
            {
                if (catalog.TryGetEntry(entry.Reference, out var payload)
                    && payload.Type == entry.Type + 8 && payload.SubType == entry.SubType)
                    LinkAny(entry.Reference, entry.TagHash, "Data", 0);
                continue;
            }
            TryRead($"Asset owners {entry.TagHash:X8}", () =>
            {
                var data = new TagData(catalog.Read(entry.TagHash));
                switch (entry.Reference)
                {
                    case 0x80809C0F:
                        foreach (int p in data.Rows(0x10, 0x80809C04, 12))
                        {
                            uint configTag = data.U32(p);
                            if (!catalog.TryGetEntry(configTag, out var configEntry) || configEntry.Reference != 0x80809C36) continue;
                            if (modelTags.TryGetValue(configTag, out uint modelTag)
                                || GameNames?.Components?.TryGetModelTag(configTag, out modelTag) == true)
                            { if (modelTag != 0) Link(modelTag, entry.TagHash, "Model", 0, 0x808073A5); continue; }
                            var config = new TagData(catalog.Read(configTag));
                            if (!config.Has(0x18, 8) || config.I64(0x18) == 0) { modelTags[configTag] = 0; continue; }
                            int resource = config.Relative(0x18);
                            if (!config.Has(resource - 4, 0x1E4) || config.U32(resource - 4) is not (0x808072BD or 0x80807286)
                                || config.U32(resource) != configTag) { modelTags[configTag] = 0; continue; }
                            modelTags[configTag] = modelTag = config.U32(resource + 0x1DC);
                            Link(modelTag, entry.TagHash, "Model", 0, 0x808073A5);
                        }
                        break;
                    case 0x808071A7:
                        Link(data.U32(8), entry.TagHash, "Mesh data", 0, 0x80807194);
                        int n = 0;
                        foreach (int p in data.Rows(0x10, 0x80800014, 4))
                            Link(data.U32(p), entry.TagHash, "Material", n++, 0x808071E8);
                        break;
                    case 0x808073A5:
                        int ordinal = 0;
                        foreach (int mesh in data.Rows(0x10, 0x80807378, 0x88))
                            foreach (int p in data.Rows(mesh + 0x18, 0x8080737E, 0x20))
                                Link(data.U32(p), entry.TagHash, "Material", ordinal++, 0x808071E8);
                        break;
                    case 0x80807194:
                        int group = 0;
                        foreach (int p in data.Rows(0x28, 0x80807199, 16))
                        {
                            BufferLink(data.U32(p), entry.TagHash, "Indices", group, 6);
                            BufferLink(data.U32(p + 4), entry.TagHash, "Positions", group, 4);
                            BufferLink(data.U32(p + 8), entry.TagHash, "Attributes", group++, 4);
                        }
                        break;
                    case 0x808071E8:
                        foreach (var (stage, field, shaderSubtype) in new[] { ("Vertex", 0x50, 1), ("Pixel", 0x2D0, 0), ("Compute", 0x370, 6) })
                        {
                            uint shader = data.U32(field - 8);
                            if (catalog.TryGetEntry(shader, out var shaderEntry) && shaderEntry.Type == 33 && shaderEntry.SubType == shaderSubtype)
                                LinkAny(shader, entry.TagHash, stage + " shader", 0);
                            foreach (int p in data.Rows(field, 0x80807211, 8))
                                Link(data.U32(p + 4), entry.TagHash, stage + " texture", checked((int)data.U32(p)), 0);
                        }
                        break;
                }
            });
            if ((i & 2047) == 0)
            {
                Stage("Asset owners", progress, i + 1, entries.Length);
            }
        }
        Stage("Asset owners", progress, entries.Length, entries.Length);
    }

    private void Link(uint child, uint owner, string role, int ordinal, uint expectedClass)
    {
        if (!catalog.TryGetEntry(child, out var entry) || child == owner
            || expectedClass != 0 && entry.Reference != expectedClass
            || expectedClass == 0 && (entry.Type != 32 || entry.SubType is < 1 or > 3)) return;
        LinkAny(child, owner, role, ordinal);
    }

    private void BufferLink(uint child, uint owner, string role, int ordinal, int subtype)
    {
        if (catalog.TryGetEntry(child, out var entry) && entry.Type == 32 && entry.SubType == subtype)
            LinkAny(child, owner, role, ordinal);
    }

    private void LinkAny(uint child, uint owner, string role, int ordinal)
    {
        if (child == owner) return;
        var link = new OwnerLink(owner, role, ordinal);
        owners.AddOrUpdate(child, [link], (_, prior) => prior.Contains(link) || prior.Length >= 16 ? prior : [.. prior, link]);
    }
}
