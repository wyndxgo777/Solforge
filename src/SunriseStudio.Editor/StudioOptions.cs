namespace SunriseStudio.Editor;

/** Command line options of the Studio executable: what to open and the smoke test, capture and profiling switches. */
public sealed record StudioOptions(string? Game = null, string? Project = null,
    uint? Tag = null, bool Warp = false, bool Smoke = false, int Frames = 12,
    string? Capture = null, string? SettingsPath = null, string? KeySource = null,
    bool NativeScene = false, uint? AnimationClip = null, string? Category = null,
    uint? Map = null, uint? Mission = null, bool WaitForNames = false,
    bool PickCenter = false, bool MissionView = false, string? Page = null, string? CaptureLoading = null,
    string? ProfileFrames = null, string? MissionScript = null);
