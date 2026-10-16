using System.Collections.ObjectModel;
using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

/** Cache part of RenderNames: reads and writes the name cache. */
public sealed partial class RenderNames
{
    private RenderNames(NameCacheReader reader)
    {
        reader.Into(names, reader.Text);
        Entries = reader.Array(() => new RendererNameEntry(reader.Data.ReadUInt32(), reader.Data.ReadUInt32(),
            reader.Text(), (RendererNameKind)reader.Data.ReadInt32(), reader.Data.ReadUInt32(),
            reader.Data.ReadInt32(), reader.Data.ReadInt32(), reader.Data.ReadInt32()));
        Conflicts = reader.Array(() => new RendererNameConflict(reader.Data.ReadUInt32(),
            reader.Array(reader.Text), reader.Optional()));
        Diagnostics = reader.Array(reader.Text);
        UnsetRowCount = reader.Count();
        Names = new ReadOnlyDictionary<uint, string>(names);
    }

    /** Rebuilds from a cache reader. */
    internal static RenderNames ReadCache(NameCacheReader reader) => new(reader);

    /** Writes the names to a cache writer. */
    internal void WriteCache(NameCacheWriter writer)
    {
        writer.Map(names, writer.Text);
        writer.Array(Entries, entry =>
        {
            writer.Data.Write(entry.TagHash); writer.Data.Write(entry.ClassHash); writer.Text(entry.Name);
            writer.Data.Write((int)entry.Kind); writer.Data.Write(entry.RootTag); writer.Data.Write(entry.Ordinal);
            writer.Data.Write(entry.RowOffset); writer.Data.Write(entry.NameOffset);
        });
        writer.Array(Conflicts, conflict =>
        { writer.Data.Write(conflict.TagHash); writer.Array(conflict.Names, writer.Text); writer.Optional(conflict.PackageName); });
        writer.Array(Diagnostics, writer.Text); writer.Data.Write(UnsetRowCount);
    }
}
