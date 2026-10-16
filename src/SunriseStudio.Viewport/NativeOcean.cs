using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using Vortice.Direct3D;

namespace SunriseStudio.Viewport;

/**
 * One water settings tag, class 0x80806D49: the wave block the ocean programs read as extern 91.
 * @param WaveNoise 3D noise at +0x08, extern 91 resource 0.
 * @param DetailNoise 3D noise at +0x0C, extern 91 resource 1.
 * @param Floats Extern 91 float elements 4 and 6 to 12, from +0x14 and +0x18 to +0x30.
 */
public sealed record NativeOceanSettings(uint Tag, uint WaveNoise, uint DetailNoise, IReadOnlyDictionary<byte, float> Floats)
{
    public const uint SettingsClass = 0x80806D49;
    // fnv1 name the map's name table files the settings under; 0x7FF742E1BE10.
    public const uint NameHash = 0x1C03307A;
    private const uint MapRootClass = 0x808091DE, NameMapClass = 0x80809780, NamePairClass = 0x80809252;
    private const int RenderGlobalsField = 0x38, MapNameTableField = 0x0C, SettingsBytes = 0x34;

    /** Reads a settings tag; null when either noise texture is unset, as 0x7FF742E1BE10 rejects it. */
    public static NativeOceanSettings? Read(PackageCatalog catalog, uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != SettingsClass) return null;
        var bytes = catalog.Read(tag);
        if (bytes.Length < SettingsBytes) throw new InvalidDataException("Water settings are shorter than 0x34 bytes.");
        uint wave = U32(bytes, 0x08), detail = U32(bytes, 0x0C);
        if (wave == uint.MaxValue || detail == uint.MaxValue) return null;
        var floats = new Dictionary<byte, float> { [4] = F32(bytes, 0x14) };
        // Element 5 is a frame value no ocean program reads; it is left unset.
        for (byte element = 6; element <= 12; element++) floats[element] = F32(bytes, element * 4);
        return new(tag, wave, detail, floats);
    }

    /**
     * The settings the game draws a map with: the map's name table entry, else the render globals default.
     * @param mapTag A map root, class 0x808091DE; any other tag takes the default.
     * @return Null when neither names usable settings, as in an installation without render globals.
     */
    public static NativeOceanSettings? Resolve(PackageCatalog catalog, uint mapTag, uint renderGlobalsTag)
    {
        if (catalog.TryGetEntry(mapTag, out var map) && map.Reference == MapRootClass
            && catalog.Read(mapTag) is { Length: >= MapNameTableField + 4 } root
            && NamedSettings(catalog, U32(root, MapNameTableField)) is { } named
            && Read(catalog, named) is { } settings)
            return settings;
        if (!catalog.TryGetEntry(renderGlobalsTag, out _)) return null;
        var globals = catalog.Read(renderGlobalsTag);
        return globals.Length >= RenderGlobalsField + 4 ? Read(catalog, U32(globals, RenderGlobalsField)) : null;
    }

    /** The tag filed under NameHash in a name table, class 0x80809780; pairs are {fnv1 name, tag}. */
    private static uint? NamedSettings(PackageCatalog catalog, uint table)
    {
        if (!catalog.TryGetEntry(table, out var entry) || entry.Reference != NameMapClass) return null;
        var bytes = catalog.Read(table);
        long count = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8));
        long header = 16 + BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16));
        if (count <= 0 || header < 4 || header + 16 + count * 8 > bytes.Length
            || U32(bytes, (int)header + 8) != NamePairClass)
            return null;
        for (int row = (int)header + 16, end = row + (int)count * 8; row < end; row += 8)
            if (U32(bytes, row) == NameHash) return U32(bytes, row + 4);
        return null;
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static float F32(byte[] bytes, int offset) => BitConverter.UInt32BitsToSingle(U32(bytes, offset));
}

/** Extern 91, the ocean wave block that 0x7FF742E1F940 pushes before each water draw. */
public static class NativeOcean
{
    public const byte Extern = 91;
    public static readonly TfxResource WaveNoise = new("ocean wave noise", ShaderResourceViewDimension.Texture3D);
    public static readonly TfxResource DetailNoise = new("ocean detail noise", ShaderResourceViewDimension.Texture3D);

    // The renderer builds its frame inputs from a plan's parameter snapshot, so the settings are keyed by it.
    private static readonly ConditionalWeakTable<TfxParameterLookup, NativeOceanSettings> Settings = new();

    /** Binds the wave and detail noise textures and the ocean floats into the inputs. */
    public static void Apply(TfxInputs inputs, NativeOceanSettings settings)
    {
        inputs.Resources[new(Extern, 0)] = WaveNoise;
        inputs.Resources[new(Extern, 1)] = DetailNoise;
        foreach (var (element, value) in settings.Floats) inputs.Floats[new(Extern, element)] = value;
    }

    /** Adds extern 91 when the plan that owns these parameters resolved ocean settings. */
    public static void ApplyRegistered(TfxInputs inputs, TfxParameterLookup parameters)
    {
        if (Settings.TryGetValue(parameters, out var settings)) Apply(inputs, settings);
    }

    /**
     * Resolves the map's ocean settings and keys them to the plan's parameter snapshot.
     * @return Null when the settings are unset; extern 91 then stays missing and ocean parts are skipped.
     */
    public static NativeOceanSettings? Register(PackageCatalog catalog, uint mapTag, TfxParameterLookup parameters)
    {
        var settings = NativeOceanSettings.Resolve(catalog, mapTag, NativeScenePlan.RenderGlobalsTag);
        if (settings is not null) Settings.AddOrUpdate(parameters, settings);
        return settings;
    }

    /** True when a vertex or pixel program reads extern 91. */
    public static bool Reads(MaterialExecutionData execution) =>
        new[] { execution.Vertex, execution.Pixel }.Any(stage =>
            TfxProgramInspection.Read(stage.TfxBytecode).Dependencies.Any(d => d.Extern == Extern));

    /** Decodes both noise textures; each must be a 3D texture, as the programs sample them. */
    public static IEnumerable<(uint Tag, TextureData Data)> ReadTextures(TextureDataReader reader, NativeOceanSettings settings)
    {
        foreach (uint tag in new[] { settings.WaveNoise, settings.DetailNoise }.Distinct())
        {
            var data = reader.Read(tag);
            if (!data.IsVolume) throw new InvalidDataException($"Ocean noise 0x{tag:X8} is not a 3D texture.");
            yield return (tag, data);
        }
    }

    /** The views extern 91 binds; empty until both noise textures are on the GPU. */
    public static IEnumerable<(TfxResource Resource, T View)> Views<T>(NativeOceanSettings? settings, Func<uint, T?> view)
        where T : class
    {
        if (settings is null || view(settings.WaveNoise) is not { } wave || view(settings.DetailNoise) is not { } detail) yield break;
        yield return (WaveNoise, wave);
        yield return (DetailNoise, detail);
    }
}
