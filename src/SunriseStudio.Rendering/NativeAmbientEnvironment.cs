using System.Numerics;

namespace SunriseStudio.Rendering;

/** Extern 41 values read by global_lighting_ambient_only; other extern 41 words stay unsupplied. */
public sealed record NativeAmbientEnvironment(Vector4 Vector5, Vector4 Vector7, Vector4 Vector8,
    float Float36, float Float37, float Float40, int ProfileCategory, string Float40Source)
{
    // Native writer 0x7FF742EE2820 reads these authored keys through the frame parameter block.
    public const uint UpVectorKey = 0x6E22B25E;
    public const uint Vector7ColorKey = 0x7C7F9077;
    public const uint Vector7ScaleKey = 0x52717901;
    public const uint Float36Key = 0xF20B0FF3;
    public const uint Vector8ColorKey = 0x603EF86C;
    public const uint Vector8ScaleKey = 0xCD02F312;
    public const uint Float37Key = 0x74FC62C4;
    // Profile float 40 defaults to 0.2 in both native categories; the package profile name does not resolve.
    public const float ProfileFloat40Default = 0.2f;

    public static IReadOnlyList<uint> RequiredLookupKeys { get; } = Array.AsReadOnly(new[]
    {
        UpVectorKey, Vector7ColorKey, Vector7ScaleKey, Float36Key, Vector8ColorKey, Vector8ScaleKey, Float37Key
    });

    /** SSAO mode 2 selects profile category 18; every other mode selects category 4. */
    public static NativeAmbientEnvironment FromDefaults(TfxParameterDefaults defaults, int ssaoMode,
        IReadOnlyDictionary<uint, Vector4>? parameterOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        var snapshot = defaults.CreateSnapshot(parameterOverrides);
        Vector4 Read(uint key) => ReadFinite(defaults, snapshot, key, "Ambient");
        var up = Read(UpVectorKey);
        up.W = 0;
        int category = ssaoMode == 2 ? 18 : 4;
        return new(up, Read(Vector7ColorKey) * Read(Vector7ScaleKey).X, Read(Vector8ColorKey) * Read(Vector8ScaleKey).X,
            Read(Float36Key).X, Read(Float37Key).X, ProfileFloat40Default, category,
            $"native category {category} default; package profile 0x1E68B047 is not installed");
    }

    // Extern 41 vector 1 is frame +0x36F20 times frame +0x36F30 X; the frame block reads these keys.
    public const uint SunColorKey = 0x90745795;
    public const uint SunScaleKey = 0x615E07A3;

    /** Extern 41 vector 1, the sun colour technique 37 reads, from the authored defaults. */
    public static Vector4 ReadSunColor(TfxParameterDefaults defaults, IReadOnlyDictionary<uint, Vector4>? parameterOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        var snapshot = defaults.CreateSnapshot(parameterOverrides);
        return ReadFinite(defaults, snapshot, SunColorKey, "Sun") * ReadFinite(defaults, snapshot, SunScaleKey, "Sun").X;
    }

    /** One authored key's snapshot value; throws when the key is missing or not finite. */
    private static Vector4 ReadFinite(TfxParameterDefaults defaults, TfxParameterLookup snapshot, uint key, string kind)
    {
        var entry = defaults.Entries.FirstOrDefault(e => e.LookupKey == key)
            ?? throw new InvalidDataException($"{kind} parameter key 0x{key:X8} is missing from the authored table.");
        var value = snapshot.Read(checked((byte)entry.Index));
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || !float.IsFinite(value.W))
            throw new InvalidDataException($"{kind} parameter key 0x{key:X8} is not finite.");
        return value;
    }

    /** Writes the ambient vectors and floats into the TFX inputs (extern 41). */
    public void Apply(TfxInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        inputs.Vectors[new(41, 5)] = Vector5;
        inputs.Vectors[new(41, 7)] = Vector7;
        inputs.Vectors[new(41, 8)] = Vector8;
        inputs.Floats[new(41, 36)] = Float36;
        inputs.Floats[new(41, 37)] = Float37;
        inputs.Floats[new(41, 40)] = Float40;
    }
}
