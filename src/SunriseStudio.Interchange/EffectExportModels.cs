namespace SunriseStudio.Interchange;

/**
 * Baked particle loops beside a world scene or an asset bundle.
 * @param Rate Bake frames per second; a loop of Frames frames repeats.
 * @param SampleStride Bytes per sample: position as 3 float32, then size, alpha and spin as float16, then frame as uint16.
 */
public sealed record EffectExportSet(string Format, int Version, string Name, string Coordinates, float Rate,
    string DataPath, string DataSha256, long DataLength, int SampleStride, IReadOnlyList<EffectExportSprite> Sprites,
    IReadOnlyList<EffectExportMaterial> Materials, IReadOnlyList<EffectExportEmitter> Emitters,
    IReadOnlyList<EffectExportSkip> Skipped, IReadOnlyList<BundleNotice> Notices, EffectExportStatistics Statistics);

/** A sprite texture as a PNG of the mip the viewer draws. */
public sealed record EffectExportSprite(int Id, uint TextureTag, string Path, string Sha256, int Width, int Height,
    bool SingleChannel, bool Srgb);

/**
 * The output is Source * colour + Destination * background, both with Operation Add.
 * @param Premultiply The viewer multiplies the colour by its alpha first.
 */
public sealed record EffectExportBlend(bool Enabled, string Source, string Destination, string Operation, bool Premultiply);

/** @param Sprite Sprite id; -1 draws the viewer's soft dot. */
public sealed record EffectExportMaterial(int Id, uint TagHash, int Sprite, EffectExportBlend Blend);

/**
 * One emitter instance and its run of samples, sorted by frame.
 * @param Default True when Studio runs it without being told to; the others are baked as Show all runs them.
 * @param Mode "cycle" is one start-to-start run, "wrap" a steady stream, "held" the live particles as they are.
 * @param First Index of the first sample in the data file.
 */
public sealed record EffectExportEmitter(int Id, string Name, uint EffectTag, uint SourceTag, int SourceOffset,
    ulong PlacementIdentity, int System, int Instance, uint SystemTag, uint EmitterTag, int Material, bool Default,
    string Trigger, string Mode, int Frames, bool Gpu, float[] Origin, long First, int Count);

/** A marker or system that stays a marker, and why. */
public sealed record EffectExportSkip(string Name, uint EffectTag, uint SourceTag, int SourceOffset, string Kind, int System,
    string Reason);

/** Counts and timing of an effect export. */
public sealed record EffectExportStatistics(int Emitters, int Skipped, long Samples, long DataBytes, int Sprites, double Seconds);
