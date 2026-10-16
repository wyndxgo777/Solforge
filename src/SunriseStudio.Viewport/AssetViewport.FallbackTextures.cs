using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using Vortice.DXGI;

namespace SunriseStudio.Viewport;

/** Colour textures for parts the native renderer cannot draw; they stand in for the game shader. */
public sealed partial class AssetViewport
{
    /** Source is null when the material has no usable colour texture; FirstMip is the top uploaded mip. */
    private sealed record FallbackTextureData(uint Material, int Generation, TextureData? Source, int FirstMip, bool Cutout);
    /** Compared by reference; geometry batches key on it. */
    private sealed class FallbackTexture(GpuTexture texture, bool cutout)
    {
        public GpuTexture Texture { get; } = texture;
        public bool Cutout { get; } = cutout;
    }

    /** Stand-in textures use mips no wider than this; the host lowers it in Low memory mode. */
    public int FallbackTextureMaxEdge { get; set; } = 1024;
    // Uploads per frame; the rest wait for the next frame.
    private const int FallbackUploadsPerFrame = 16;
    // Decoded stand-ins waiting for upload; the worker pauses past this so RAM holds only a few frames of uploads.
    private const int FallbackReadyLimit = 4 * FallbackUploadsPerFrame;
    // A null value means the material has no usable colour texture and the part stays grey.
    private readonly Dictionary<uint, FallbackTexture?> fallbackTextures = [];
    private readonly HashSet<uint> fallbackRequested = [];
    private readonly Queue<uint> fallbackPending = [];
    private readonly ConcurrentQueue<FallbackTextureData> fallbackReady = new();
    private readonly CancellationTokenSource fallbackLifetime = new();
    private Task? fallbackLoad;
    private PackageCatalog? fallbackCatalog;
    private long fallbackRevision = -1;
    // Bumped when the cache is dropped; a worker result from an older generation is discarded.
    private int fallbackGeneration;
    private IReadOnlyList<string>? diagnosticsView;
    private (IReadOnlyList<string>? Source, int Count, int Textured, int Grey) diagnosticsKey;

    /** Fallback parts drawn with their material's colour texture in the last frame. */
    public int FallbackTexturedPartCount { get; private set; }
    /** Fallback parts drawn plain grey in the last frame, including those whose texture is still loading. */
    public int FallbackGreyPartCount { get; private set; }

    /** Native diagnostics, then one line with the textured and grey fallback counts of the last frame. */
    public IReadOnlyList<string> NativeDiagnostics
    {
        get
        {
            var source = nativeScene?.Diagnostics;
            var key = (source, source?.Count ?? 0, FallbackTexturedPartCount, FallbackGreyPartCount);
            if (diagnosticsView is not null && diagnosticsKey == key) return diagnosticsView;
            var lines = new List<string>(source ?? []);
            if (FallbackTexturedPartCount + FallbackGreyPartCount > 0)
                lines.Insert(0, $"Fallback parts: {FallbackTexturedPartCount} textured, {FallbackGreyPartCount} grey.");
            diagnosticsKey = key;
            return diagnosticsView = lines;
        }
    }

    /** Material of a fallback part; zero when the part names none. */
    private static uint FallbackMaterial(MeshInstance instance)
    {
        uint tag = instance.Mesh.Native?.MaterialTag ?? instance.Mesh.MaterialTag;
        return tag is uint.MaxValue ? 0 : tag;
    }

    /** The texture to draw a fallback part with, or null for grey; queues a first request. */
    private FallbackTexture? FallbackTextureFor(MeshInstance instance)
    {
        uint material = FallbackMaterial(instance);
        if (material == 0 || fallbackCatalog is null) return null;
        if (fallbackTextures.TryGetValue(material, out var texture)) return texture;
        if (fallbackRequested.Add(material)) fallbackPending.Enqueue(material);
        return null;
    }

    /** Runs on the graphics thread before drawing; a project edit drops every cached texture. */
    private void PollFallbackTextures()
    {
        long revision = Catalog?.OverlayRevision ?? -1;
        if (!ReferenceEquals(fallbackCatalog, Catalog) || revision != fallbackRevision)
        {
            ReleaseFallbackTextures();
            fallbackGeneration++;
            fallbackCatalog = Catalog;
            fallbackRevision = revision;
        }
        for (int uploads = 0; uploads < FallbackUploadsPerFrame && fallbackReady.TryDequeue(out var ready); uploads++)
        {
            if (ready.Generation != fallbackGeneration) continue;
            FallbackTexture? texture = null;
            if (ready.Source is { } data)
            {
                try { texture = new(UploadFallback(data, ready.FirstMip), ready.Cutout); fallbackTextureBytes += data.ByteCount; }
                catch (Exception error) when (error is ArgumentException or NotSupportedException
                    or SharpGen.Runtime.SharpGenException) { }
            }
            fallbackTextures[ready.Material] = texture;
        }
        if (fallbackLoad is { IsCompleted: false } || fallbackCatalog is not { } catalog || fallbackPending.Count == 0) return;
        var batch = fallbackPending.ToArray();
        fallbackPending.Clear();
        var token = fallbackLifetime.Token;
        int generation = fallbackGeneration, edge = Math.Max(1, FallbackTextureMaxEdge);
        fallbackLoad = Task.Run(() =>
        {
            foreach (uint material in batch)
            {
                // The graphics thread takes FallbackUploadsPerFrame a frame; decoding further ahead only holds RAM.
                while (fallbackReady.Count >= FallbackReadyLimit && !token.IsCancellationRequested
                    && generation == Volatile.Read(ref fallbackGeneration)) Thread.Sleep(5);
                if (token.IsCancellationRequested || generation != Volatile.Read(ref fallbackGeneration)) return;
                fallbackReady.Enqueue(ReadFallbackTexture(catalog, material, generation, edge));
            }
        }, token);
    }

    /** sRGB data uploads as UNORM so the sampled value is the stored display value, as the grey tint is. */
    private GpuTexture UploadFallback(TextureData data, int firstMip)
    {
        var top = data.Subresources.First(s => s.MipLevel == firstMip && s.ArraySlice == 0);
        var mips = data.Subresources.Where(s => s.ArraySlice == 0 && s.MipLevel >= firstMip)
            .Select(s => new TextureSubresourceUpload(s.MipLevel - firstMip, 0, s.Data, s.RowPitch, s.SlicePitch)).ToArray();
        var format = (Format)data.Format switch
        {
            Format.BC1_UNorm_SRgb => Format.BC1_UNorm, Format.BC2_UNorm_SRgb => Format.BC2_UNorm,
            Format.BC3_UNorm_SRgb => Format.BC3_UNorm, Format.BC7_UNorm_SRgb => Format.BC7_UNorm,
            Format.R8G8B8A8_UNorm_SRgb => Format.R8G8B8A8_UNorm, Format.B8G8R8A8_UNorm_SRgb => Format.B8G8R8A8_UNorm,
            Format.B8G8R8X8_UNorm_SRgb => Format.B8G8R8X8_UNorm, var other => other
        };
        return renderer.CreateTextureResource(top.Width, top.Height, 1, 1, mips.Length, format, false, false, mips);
    }

    /** Picks the first sRGB 2D colour texture the pixel shader declares; runs on a worker. */
    private static FallbackTextureData ReadFallbackTexture(PackageCatalog catalog, uint material, int generation, int edge)
    {
        try
        {
            var info = new GameMaterialReader(catalog).Read(material);
            var binding = info.Textures.Where(t => t.IsPreviewCandidate && t.IsSrgb).OrderBy(t => t.Slot).FirstOrDefault();
            if (binding is null) return new(material, generation, null, 0, false);
            var reader = new TextureDataReader(catalog);
            // The resident mips hold the chosen mip whenever their top is at least the edge, so the streamed mips
            // (most of the bytes) are read only when the resident top is smaller; the uploaded image is the same.
            var data = reader.Read(binding.TextureTag, residentOnly: true);
            if (data.Subresources.Count == 0
                || Math.Max(data.Width, data.Height) < edge && reader.StreamedByteCount(binding.TextureTag) > 0)
                data = reader.Read(binding.TextureTag);
            if (data.IsCube || data.IsVolume) return new(material, generation, null, 0, false);
            int first = data.Subresources.Where(s => s.ArraySlice == 0)
                .OrderBy(s => Math.Max(s.Width, s.Height) <= edge ? 0 : 1).ThenBy(s => s.MipLevel).First().MipLevel;
            bool cutout = info.PixelShader is { } shader && AlphaTestReadsAlpha(shader.Disassembly, binding.Slot);
            // Only the uploaded mips stay in RAM until the graphics thread takes them.
            data = data with { Subresources = data.Subresources.Where(s => s.ArraySlice == 0 && s.MipLevel >= first).ToArray() };
            return new(material, generation, data, first, cutout);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException
            or KeyNotFoundException or ArgumentException or ObjectDisposedException)
        { return new(material, generation, null, 0, false); }
    }

    /**
     * True when a discard in the pixel shader depends on the alpha channel sampled from slot.
     * Walks straight-line code backwards from each discard; branches are read as straight-line code.
     */
    internal static bool AlphaTestReadsAlpha(string disassembly, uint slot)
    {
        var lines = disassembly.Split('\n').Select(line => line.Trim())
            .Where(line => line.Length != 0 && !line.StartsWith("//", StringComparison.Ordinal)
                && !line.StartsWith("dcl_", StringComparison.Ordinal)).ToArray();
        var live = new HashSet<(string Register, char Lane)>();
        for (int index = lines.Length - 1; index >= 0; index--)
        {
            string line = lines[index];
            int space = line.IndexOf(' ');
            string opcode = space < 0 ? line : line[..space];
            string[] operands = space < 0 ? [] : DxbcText.SplitOperands(line[(space + 1)..]);
            if (opcode.StartsWith("discard", StringComparison.Ordinal))
            {
                if (operands.Length > 0) AddSource(operands[0], "x");
                continue;
            }
            if (operands.Length < 2 || DestinationOperand().Match(operands[0]) is not { Success: true } destination) continue;
            string register = destination.Groups["reg"].Value;
            string mask = destination.Groups["mask"].Success ? destination.Groups["mask"].Value : "xyzw";
            // A write ends the earlier value's life; only lanes read later carry the dependency on.
            string written = "";
            foreach (char lane in mask)
                if (live.Remove((register, lane))) written += lane;
            if (written.Length == 0) continue;
            bool texture = opcode.StartsWith("sample", StringComparison.Ordinal) || opcode.StartsWith("ld", StringComparison.Ordinal)
                || opcode.StartsWith("gather", StringComparison.Ordinal);
            if (texture)
            {
                var resource = operands.Skip(2).Select(value => TextureOperand().Match(value)).FirstOrDefault(match => match.Success);
                if (resource is null || uint.Parse(resource.Groups["slot"].Value) != slot) continue;
                string swizzle = resource.Groups["swizzle"].Success ? resource.Groups["swizzle"].Value : "xyzw";
                if (written.Any(lane => Component(swizzle, lane) == 'w')) return true;
                continue;
            }
            string op = opcode.Split('_')[0];
            if (op is "dp2" or "dp3" or "dp4")
            {
                string read = "xyzw"[..(op[2] - '0')];
                foreach (string source in operands.Skip(1)) AddSource(source, read);
                continue;
            }
            foreach (string source in operands.Skip(1)) AddSource(source, written);
        }
        return false;

        void AddSource(string operand, string lanes)
        {
            var match = SourceOperand().Match(operand);
            if (!match.Success) return;
            string swizzle = match.Groups["swizzle"].Success ? match.Groups["swizzle"].Value : "xyzw";
            foreach (char lane in lanes) live.Add((match.Groups["reg"].Value, Component(swizzle, lane)));
        }
        static char Component(string swizzle, char lane) => swizzle[swizzle.Length == 1 ? 0 : Math.Min("xyzw".IndexOf(lane), swizzle.Length - 1)];
    }

    [GeneratedRegex(@"^(?<reg>r\d+)(?:\.(?<mask>[xyzw]+))?$")]
    private static partial Regex DestinationOperand();
    [GeneratedRegex(@"^-?\|?(?<reg>r\d+)(?:\.(?<swizzle>[xyzw]+))?\|?$")]
    private static partial Regex SourceOperand();
    [GeneratedRegex(@"^t(?<slot>\d+)(?:\.(?<swizzle>[xyzw]+))?$")]
    private static partial Regex TextureOperand();

    /** Releases uploaded stand-ins; the next frame requests them again. */
    private void ReleaseFallbackTextures()
    {
        // Batch keys hold the released textures; drop them with the textures.
        geometryBatches.Clear();
        foreach (var texture in fallbackTextures.Values) texture?.Texture.Dispose();
        fallbackTextures.Clear();
        fallbackTextureBytes = 0;
        fallbackRequested.Clear();
        fallbackPending.Clear();
    }

    /** Frees every stand-in texture and drops decoded ones still waiting; a closed map calls it. */
    public void ReleaseFallbackCache()
    {
        fallbackGeneration++;
        while (fallbackReady.TryDequeue(out _)) { }
        ReleaseFallbackTextures();
    }

    /** GPU bytes of the uploaded stand-in textures, for the memory report. */
    public long FallbackTextureBytes => fallbackTextureBytes;
    private long fallbackTextureBytes;
    public int FallbackTextureCount => fallbackTextures.Count;

    private void DisposeFallbackTextures()
    {
        fallbackLifetime.Cancel();
        fallbackLifetime.Dispose();
        ReleaseFallbackTextures();
    }
}
