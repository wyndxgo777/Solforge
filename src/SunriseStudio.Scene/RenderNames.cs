using System.Collections.ObjectModel;
using System.Text;
using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

/** Kind of renderer name. This file holds the renderer-name records and lookup. */
public enum RendererNameKind { Scope, Technique }

/** A scope or technique name with the tag and row it was read from. */
public sealed record RendererNameEntry(uint TagHash, uint ClassHash, string Name,
    RendererNameKind Kind, uint RootTag, int Ordinal, int RowOffset, int NameOffset);

/** A tag that more than one name claims. */
public sealed record RendererNameConflict(uint TagHash, IReadOnlyList<string> Names,
    string? PackageName);

/** Renderer row names remain separate from package names and ambiguous tag aliases. */
public sealed partial class RenderNames
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Dictionary<uint, string> names = [];
    public IReadOnlyDictionary<uint, string> Names { get; }
    public IReadOnlyList<RendererNameEntry> Entries { get; }
    public IReadOnlyList<RendererNameConflict> Conflicts { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public int UnsetRowCount { get; }

    // Native stage rows use indexes 0 through 22 in this build.
    public static IReadOnlyList<string> StageNames { get; } = Array.AsReadOnly(new[]
    {
        "generate_gbuffer", "decals", "investment_decals", "shadow_generate", "lighting apply",
        "light probe apply", "decals_additive", "transparents", "distortion",
        "light_shaft_occlusion", "skin_prepass", "lens_flares", "depth_prepass",
        "water_reflection", "postprocess_transparent_stencil", "impulse", "reticle",
        "water_ripples", "mask_sun_light", "volumetrics", "cubemaps", "postprocess_screen",
        "world_forces"
    });

    /** Name of a render stage; null when out of range. */
    public static string? GetStageName(int stage) =>
        (uint)stage < StageNames.Count ? StageNames[stage] : null;

    /** Name of the tag; null when unknown. */
    public string? GetTagName(uint tag) => names.GetValueOrDefault(tag);

    /** Each accepted row must target the live class required by its native table. */
    public RenderNames(PackageCatalog catalog, uint? rootTag = null, CancellationToken token = default)
    {
        var entries = new List<RendererNameEntry>();
        var diagnostics = new List<string>();
        var unset = 0;
        var roots = rootTag.HasValue ? [rootTag.Value]
            : catalog.Entries.Where(e => e.Reference == 0x80806CB1).Select(e => e.TagHash).ToArray();
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            if (!catalog.TryGetEntry(root, out var rootEntry) || rootEntry.Reference != 0x80806CB1)
                throw new InvalidDataException($"Renderer root 0x{root:X8} has the wrong live class.");
            var data = new TagData(catalog.Read(root));
            var strings = StringRegions(data);
            foreach (var (kind, field, element, targetClass) in new[]
            {
                (RendererNameKind.Scope, 0x10, 0x80806CB6U, 0x808071F3U),
                (RendererNameKind.Technique, 0x20, 0x80806CB5U, 0x808071E8U)
            })
            {
                var rows = data.Rows(field, element, 16);
                for (var ordinal = 0; ordinal < rows.Length; ++ordinal)
                {
                    token.ThrowIfCancellationRequested();
                    var row = rows[ordinal];
                    var tag = data.U32(row + 12);
                    if (tag is 0 or uint.MaxValue) { unset++; continue; }
                    if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != targetClass)
                    {
                        diagnostics.Add($"Renderer {kind} row {ordinal} in 0x{root:X8} has an invalid tag 0x{tag:X8}.");
                        continue;
                    }
                    if (kind == RendererNameKind.Scope)
                    {
                        var scope = new TagData(catalog.Read(tag));
                        if (scope.U32(0x10) != ordinal)
                        {
                            diagnostics.Add($"Renderer scope 0x{tag:X8} disagrees with row index {ordinal}.");
                            continue;
                        }
                    }
                    var offset = data.Relative(row);
                    var name = ReadName(data, strings, offset);
                    entries.Add(new RendererNameEntry(tag, targetClass, name, kind, root,
                        ordinal, row, offset));
                }
            }
        }
        var conflicts = new List<RendererNameConflict>();
        foreach (var group in entries.GroupBy(e => e.TagHash))
        {
            var aliases = group.Select(e => e.Name).Distinct(StringComparer.Ordinal).ToArray();
            var packageName = catalog.GetName(group.Key);
            if (aliases.Length != 1 || packageName != null && packageName != aliases[0])
                conflicts.Add(new RendererNameConflict(group.Key, Array.AsReadOnly(aliases), packageName));
            else names.Add(group.Key, aliases[0]);
        }
        Names = new ReadOnlyDictionary<uint, string>(names);
        Entries = entries.AsReadOnly();
        Conflicts = conflicts.AsReadOnly();
        Diagnostics = diagnostics.AsReadOnly();
        UnsetRowCount = unset;
    }

    /** Name pointers must stay inside a bounded package string block. */
    private static (int Start, int Length)[] StringRegions(TagData data)
    {
        var result = new List<(int, int)>();
        for (var offset = 0; offset <= data.Length - 12; offset += 4)
        {
            if (data.U32(offset) != 0x80800065) continue;
            var length = data.U64(offset + 4);
            if (length != 0 && length <= (ulong)(data.Length - offset - 12))
                result.Add((offset + 12, (int)length));
        }
        return result.ToArray();
    }

    /** Authored pointers may share a string suffix but must end inside the owning block. */
    private static string ReadName(TagData data, (int Start, int Length)[] strings, int offset)
    {
        var owners = strings.Where(s => offset >= s.Start && offset < s.Start + s.Length).ToArray();
        if (owners.Length != 1)
            throw new InvalidDataException("Renderer name pointer has no unique string block.");
        var tail = data.Bytes.AsSpan(offset, owners[0].Start + owners[0].Length - offset);
        var end = tail.IndexOf((byte)0);
        if (end <= 0 || end > 1024)
            throw new InvalidDataException("Renderer name is empty or has no bounded terminator.");
        var name = StrictUtf8.GetString(tail[..end]);
        if (name.Any(char.IsControl)) throw new InvalidDataException("Renderer name has control bytes.");
        return name;
    }
}
