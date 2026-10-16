using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/** The entity placement an effect belongs to; channels and sequence plays are per placement. */
public readonly record struct EffectPlacementKey(uint SourceTag, int SourceOffset, ulong Identity)
{
    /** Key for a marker's placement. */
    public static EffectPlacementKey Of(SceneMarker marker) => new(marker.SourceTag, marker.SourceOffset, marker.PlacementIdentity);
}

/** Viewer choices that decide which effects run: channel values, sequence plays and overrides. */
public sealed class EffectControls
{
    private readonly Dictionary<EffectPlacementKey, Dictionary<uint, float>> channels = [];
    private readonly Dictionary<(EffectPlacementKey, uint), (bool Playing, int Generation)> plays = [];
    private readonly HashSet<(EffectPlacementKey, uint, int)> forced = [];

    /** Runs every particle effect as continuous, ignoring triggers. */
    public bool ShowAll { get; set; }

    /** An object channel of a placement; an unset one reads 0, assumed to be its start value. */
    public float Channel(EffectPlacementKey key, uint channel) =>
        channels.TryGetValue(key, out var values) ? values.GetValueOrDefault(channel) : 0;

    /** Sets one channel value for a placement. */
    public void SetChannel(EffectPlacementKey key, uint channel, float value)
    {
        if (!channels.TryGetValue(key, out var values)) channels[key] = values = [];
        values[channel] = value;
    }

    /** Whether the sequence plays now, and how many times Play started it. */
    public (bool Playing, int Generation) Sequence(EffectPlacementKey key, uint sequence, bool autoStart) =>
        plays.TryGetValue((key, sequence), out var state) ? state : (autoStart, 0);

    /** Starts the sequence again, as gameplay code does. */
    public void Play(EffectPlacementKey key, uint sequence, bool autoStart) =>
        plays[(key, sequence)] = (true, Sequence(key, sequence, autoStart).Generation + 1);

    /** Records a stop for a sequence; autoStart is the sequence's default start state. */
    public void Stop(EffectPlacementKey key, uint sequence, bool autoStart) =>
        plays[(key, sequence)] = (false, Sequence(key, sequence, autoStart).Generation);

    /** A forced effect runs as continuous whatever its trigger. */
    public bool Forced(SceneMarker marker) => marker.Effect is { } effect
        && forced.Contains((EffectPlacementKey.Of(marker), effect.SourceTag, effect.SourceOffset));

    /** Forces a marker's effect on or off. */
    public void SetForced(SceneMarker marker, bool value)
    {
        if (marker.Effect is not { } effect) return;
        var key = (EffectPlacementKey.Of(marker), effect.SourceTag, effect.SourceOffset);
        if (value) forced.Add(key); else forced.Remove(key);
    }
}
