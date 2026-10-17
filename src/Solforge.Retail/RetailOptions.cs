/**
 * File: Where the retail data lives. Every path and the engine URL can be set by environment variable so another
 * machine (or a test fixture folder) works without code changes.
 */
namespace Solforge.Retail;

/**
 * Locations of the retail extraction (build 245048). Root holds index\ and payloads\ (E:\sol-live-pkg-extract);
 * MediaRoot holds the payloads of video|audio packages (G:\sol-live-pkg-extract). LookupCache is the tiger-pkg
 * lookup cache tiger-mcp writes (hash64 -> tag and named tags). EngineUrl is the tiger-mcp web API. CacheFolder
 * holds Solforge's compact index built from the TSVs.
 */
public sealed record RetailOptions
{
    /** The build the retail extraction comes from. */
    public const string Build = "245048";

    public string Root { get; init; } = @"E:\sol-live-pkg-extract";
    public string? MediaRoot { get; init; } = @"G:\sol-live-pkg-extract";
    public string? LookupCache { get; init; } = DefaultLookupCache();
    public string EngineUrl { get; init; } = "http://127.0.0.1:3033";
    public string CacheFolder { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Solforge", "retail");
    /** Seconds before an engine call gives up; the engine is optional, so this stays short. */
    public double EngineTimeoutSeconds { get; init; } = 20;

    /**
     * Defaults overridden by SOLFORGE_RETAIL_ROOT, SOLFORGE_RETAIL_MEDIA_ROOT, SOLFORGE_RETAIL_LOOKUP,
     * SOLFORGE_RETAIL_ENGINE and SOLFORGE_RETAIL_CACHE (empty MEDIA_ROOT or LOOKUP turns that source off).
     */
    public static RetailOptions FromEnvironment()
    {
        var options = new RetailOptions();
        static string? Env(string name) => Environment.GetEnvironmentVariable(name);
        if (Env("SOLFORGE_RETAIL_ROOT") is { Length: > 0 } root) options = options with { Root = root };
        if (Env("SOLFORGE_RETAIL_MEDIA_ROOT") is { } media) options = options with { MediaRoot = media.Length == 0 ? null : media };
        if (Env("SOLFORGE_RETAIL_LOOKUP") is { } lookup) options = options with { LookupCache = lookup.Length == 0 ? null : lookup };
        if (Env("SOLFORGE_RETAIL_ENGINE") is { Length: > 0 } engine) options = options with { EngineUrl = engine.TrimEnd('/') };
        if (Env("SOLFORGE_RETAIL_CACHE") is { Length: > 0 } cache) options = options with { CacheFolder = cache };
        return options;
    }

    /** %LOCALAPPDATA%\v4nguard\tiger-pkg\cache\lookup_cache_d2_ren_w64.bin (written by tiger-mcp -v d2_ren). */
    public static string DefaultLookupCache() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "v4nguard", "tiger-pkg", "cache", "lookup_cache_d2_ren_w64.bin");
}
