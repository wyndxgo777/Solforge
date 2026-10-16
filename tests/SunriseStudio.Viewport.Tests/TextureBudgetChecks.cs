using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** The texture budget only drops streamed mips; every part still prepares. */
internal static class TextureBudgetChecks
{
    // 80FAA33B is the SpeedTree scene of DecoratorChecks; 4 MiB holds its resident mips but not its streamed ones.
    private const uint SceneTag = 0x80FAA33B;
    private const long SmallBudget = 4L * 1024 * 1024;

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string install)
    {
        using var catalog = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        var scene = new AssetSceneLoader(catalog).Load(SceneTag);
        var full = AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: long.MaxValue);
        var small = AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: SmallBudget);
        Check(small.PreparedPartCount == full.PreparedPartCount && full.ResidentOnlyTextureCount == 0
            && small.ResidentOnlyTextureCount > 0 && small.TextureBytes < full.TextureBytes,
            $"Budget {SmallBudget >> 20} MiB: {small.PreparedPartCount} of {full.PreparedPartCount} parts, "
            + $"{small.ResidentOnlyTextureCount} reduced textures, {small.TextureBytes} of {full.TextureBytes} bytes.");
        // A reduced texture is the full one without its first header +0x18 mips.
        var reader = new TextureDataReader(catalog);
        uint tag = small.Textures.First(pair => pair.Value.MipCount < full.Textures[pair.Key].MipCount).Key;
        var whole = full.Textures[tag];
        var resident = reader.Read(tag, residentOnly: true);
        int dropped = whole.LargeMipCount;
        Check(resident.MipCount == whole.MipCount - dropped && resident.Width == Math.Max(1, whole.Width >> dropped)
            && resident.Subresources.Select((s, i) => s.Data.AsSpan().SequenceEqual(whole.Subresources[i + dropped].Data)).All(same => same)
            && reader.StreamedByteCount(tag) == whole.ByteCount - resident.ByteCount,
            $"Texture 0x{tag:X8} resident mips do not match the tail of the full texture.");
        Console.WriteLine($"texture_budget scene=80FAA33B budget_mib={SmallBudget >> 20} prepared={small.PreparedPartCount}/{full.PreparedPartCount} "
            + $"reduced={small.ResidentOnlyTextureCount} bytes={small.TextureBytes}/{full.TextureBytes} sample=0x{tag:X8} dropped_mips={dropped} result=pass");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
