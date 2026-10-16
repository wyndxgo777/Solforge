/**
 * File: Live material overrides (constant and texture writes) the viewport applies on top of the authored material;
 * the renderer side is Viewport/NativeSceneRenderer.Overrides.cs, producers are the material graph preview and MCP
 * tools.
 */
using System.Numerics;
using SunriseStudio.Packages;

namespace SunriseStudio.Rendering;

/** The authored vector array a constant override writes: the stage's constant template or its TFX expression constants. */
public enum MaterialConstantBank { Template, Expression }

/** One float4 write; the lanes set in Mask (bit 0 = x .. bit 3 = w) take Value's lanes, the others keep the authored value. */
public sealed record MaterialConstantOverride(GameShaderStage Stage, MaterialConstantBank Bank, int Vector, int Mask, Vector4 Value);

/**
 * Points one shader texture slot at another texture. Texture is the decoded payload, for a tag the renderer has not
 * uploaded; null uses the renderer's own copy or skips the write.
 */
public sealed record MaterialTextureOverride(GameShaderStage Stage, uint Slot, uint TextureTag, TextureData? Texture);

/** A preview-only change to one material. Renderers draw it; nothing writes it to a tag. */
public sealed record MaterialOverride(IReadOnlyList<MaterialConstantOverride> Constants, IReadOnlyList<MaterialTextureOverride> Textures);

/** What one renderer did with one material's override: its materials, the parts drawn with them and the writes it left out. */
public sealed record MaterialOverrideReport(int Materials, int Parts, IReadOnlyList<string> Skipped);

/**
 * Live material overrides, by material tag, shared by the renderers that draw them. A change bumps Revision; each
 * renderer compares it with the revision it last applied when its next frame starts, so an edit shows one frame later.
 * Thread safe. An empty set leaves every renderer on its authored data.
 */
public sealed class MaterialOverrideSet
{
    /** Empty snapshot. */
    private static readonly IReadOnlyDictionary<uint, MaterialOverride> None = new Dictionary<uint, MaterialOverride>();
    /** Guards the current snapshot and revision. */
    private readonly object gate = new();
    // Replaced, never changed in place, so a snapshot stays valid after later edits.
    private IReadOnlyDictionary<uint, MaterialOverride> current = None;
    private readonly Dictionary<(object Renderer, uint Material), MaterialOverrideReport> reports = [];
    private int revision;

    /** Counter that changes with every edit, so the renderer knows to rebuild. */
    public int Revision { get { lock (gate) return revision; } }
    /** Number of materials with an override. */
    public int Count { get { lock (gate) return current.Count; } }

    /** The override of one material, or null. */
    public MaterialOverride? Get(uint material) { lock (gate) return current.GetValueOrDefault(material); }

    /** Replaces the override of one material. */
    public void Set(uint material, MaterialOverride value)
    {
        lock (gate)
        {
            if (current.TryGetValue(material, out var previous) && ReferenceEquals(previous, value)) return;
            current = new Dictionary<uint, MaterialOverride>(current) { [material] = value };
            revision++;
        }
    }

    /** Puts one material back on its authored data; false when it had no override. */
    public bool Remove(uint material)
    {
        lock (gate)
        {
            if (!current.ContainsKey(material)) return false;
            var next = new Dictionary<uint, MaterialOverride>(current);
            next.Remove(material);
            current = next;
            revision++;
            return true;
        }
    }

    /** Puts every material back on its authored data. */
    public void Clear()
    {
        lock (gate)
        {
            if (current.Count == 0) return;
            current = None;
            revision++;
        }
    }

    /** The overrides and the revision they belong to; the dictionary never changes afterwards. */
    public (int Revision, IReadOnlyDictionary<uint, MaterialOverride> Overrides) Snapshot()
    {
        lock (gate) return (revision, current);
    }

    /** A renderer records what it applied; null forgets the material for that renderer. */
    public void Report(object renderer, uint material, MaterialOverrideReport? report)
    {
        lock (gate)
        {
            if (report is null) reports.Remove((renderer, material));
            else reports[(renderer, material)] = report;
        }
    }

    /** Drops every report of a renderer, when it is disposed. */
    public void Forget(object renderer)
    {
        lock (gate)
            foreach (var key in reports.Keys.Where(key => ReferenceEquals(key.Renderer, renderer)).ToArray()) reports.Remove(key);
    }

    /** The reports of one material summed over the renderers that draw it. */
    public MaterialOverrideReport Summary(uint material)
    {
        lock (gate)
        {
            var mine = reports.Where(item => item.Key.Material == material).Select(item => item.Value).ToArray();
            return new(mine.Sum(report => report.Materials), mine.Sum(report => report.Parts),
                mine.SelectMany(report => report.Skipped).Distinct(StringComparer.Ordinal).ToArray());
        }
    }

    /**
     * The stage with the constant overrides for it written into copies of its arrays; the same instance when none
     * apply. A template write is left out when the stage reads an external constant buffer, since the material's own
     * template is not what the game draws then, so the preview never shows a value Apply could not.
     * @param skipped Receives one line per write left out.
     */
    public static MaterialStageData Apply(MaterialStageData stage, IEnumerable<MaterialConstantOverride> overrides,
        ICollection<string>? skipped = null)
    {
        Vector4[]? template = null, expressions = null;
        foreach (var write in overrides)
        {
            if (write.Stage != stage.Stage) continue;
            bool isTemplate = write.Bank == MaterialConstantBank.Template;
            string bank = isTemplate ? "constant template" : "expression constants";
            if (isTemplate && stage.ConstantSourceTag != 0)
            {
                skipped?.Add($"{stage.Stage} {bank}: the material reads constant buffer 0x{stage.ConstantSourceTag:X8} instead.");
                continue;
            }
            var authored = isTemplate ? stage.ConstantTemplate : stage.TfxConstants;
            if ((uint)write.Vector >= (uint)authored.Length)
            {
                skipped?.Add($"{stage.Stage} {bank} vector {write.Vector} is past the material's {authored.Length} vectors.");
                continue;
            }
            var target = isTemplate ? (template ??= (Vector4[])authored.Clone()) : (expressions ??= (Vector4[])authored.Clone());
            target[write.Vector] = Masked(target[write.Vector], write.Value, write.Mask);
        }
        return template is null && expressions is null ? stage
            : stage with { ConstantTemplate = template ?? stage.ConstantTemplate, TfxConstants = expressions ?? stage.TfxConstants };
    }

    /** Current with the lanes set in mask taken from value. */
    public static Vector4 Masked(Vector4 current, Vector4 value, int mask) => new(
        (mask & 1) != 0 ? value.X : current.X, (mask & 2) != 0 ? value.Y : current.Y,
        (mask & 4) != 0 ? value.Z : current.Z, (mask & 8) != 0 ? value.W : current.W);

    /** Lane bits of a mask such as "xyz" or "rgba"; other characters are ignored. */
    public static int ParseMask(string mask)
    {
        int bits = 0;
        foreach (char c in mask.ToLowerInvariant())
        {
            int lane = "xyzw".IndexOf(c);
            if (lane < 0) lane = "rgba".IndexOf(c);
            if (lane >= 0) bits |= 1 << lane;
        }
        return bits;
    }
}
