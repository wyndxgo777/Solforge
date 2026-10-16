using System.Text.Json.Serialization;

namespace SunriseStudio.Projects;

/** Row identity stays fixed while typed value-pool pointers may relocate. */
public sealed record ConfigBindingSite(string Profile, int RowOffset, int OwnerIndex, int ComponentIndex,
    int PoolOffset, string GuardSha256, uint LiteralBits, string Name)
{
    [JsonIgnore] public string Identity => $"{Profile}/{OwnerIndex}/{ComponentIndex}/{RowOffset}";
}

/** A place in an asset where a config setting could be bound: site, value type and offset. */
public sealed record ConfigBindingCandidate(ConfigBindingSite Site, ModConfigType Type, int Offset);

/** Original assets retain a base fingerprint; added assets retain their allocated project identity. */
public sealed record ProjectConfigBinding(string Key, ModConfigType Type, uint TagHash, string? AddedAssetId,
    uint ClassHash, string? OriginalSha256, int? OriginalSize, ConfigBindingSite Site)
{
    [JsonIgnore] public string Identity => $"{TagHash:X8}/{Site.Identity}";
}

/** Source identity describes the exact shipped entry before startup config substitution. */
public sealed record ModConfigSite(string Key, string Profile, uint Tag, uint Class, int Size,
    string SourceSha256, int Offset, ModConfigType Type, int? PropertyOffset = null,
    int? PoolOffset = null, int? BindingOffset = null);

/** Result of compiling config bindings: the rewritten asset bytes and the bound sites. */
public sealed record ConfigBindingCompilation(IReadOnlyDictionary<uint, byte[]> Assets,
    IReadOnlyList<ModConfigSite> Sites)
{
    public static ConfigBindingCompilation Empty { get; } = new(new Dictionary<uint, byte[]>(), []);
}

/** The authoring layer verifies native sites and isolates their constants before applying defaults. */
public interface IConfigBindingCompiler
{
    IReadOnlyList<ConfigBindingCandidate> FindSites(uint tag, uint classHash, ReadOnlyMemory<byte> bytes);
    ConfigBindingCompilation Compile(IReadOnlyList<ProjectConfigBinding> bindings,
        IReadOnlyList<ModConfigEntry> settings, Func<uint, byte[]> readSource);
}

/** Limits on config binding sites and entries, mirroring the native startup baker. */
public static class ConfigBindingLimits
{
    // These bounds match the native startup baker's fixed admission limits.
    public const int MaximumSites = 256, MaximumEntryBytes = 64 * 1024 * 1024;
    // Native package registration admits original package ids through 0x19FF.
    public const uint MaximumPackageId = 0x19FF;
    public const string PropertyProfile = "cui-property-v1", ComparisonProfile = "cui-comparison-v1";
}
