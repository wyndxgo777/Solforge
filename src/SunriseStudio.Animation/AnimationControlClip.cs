/** File: Decoded control-channel samples of an animation clip, grouped by control name. */
using System.Numerics;

namespace SunriseStudio.Animation;

/** One control's value at a frame; the Has flags say which components the clip animates. */
public readonly record struct AnimationControlSample(int ControlIndex, float Scale,
    Quaternion Rotation, Vector3 Translation, bool HasScale, bool HasRotation, bool HasTranslation);

/** A group of controls sharing a name hash and how many it holds. */
public sealed record AnimationControlGroup(uint NameHash, int ControlCount);

/** Control indices remain distinct from skeleton bone indices until their binding is resolved. */
public sealed class AnimationControlTrack
{
    private readonly Func<float, float>? scaleCurve;
    private readonly Func<float, Quaternion>? rotationCurve;
    private readonly Func<float, Vector3>? translationCurve;
    public int ControlIndex { get; }
    public IReadOnlyList<float> ScaleKeys { get; }
    public IReadOnlyList<Quaternion> RotationKeys { get; }
    public IReadOnlyList<Vector3> TranslationKeys { get; }

    /** Curve samplers retain compressed timing between the exposed whole-frame keys. */
    internal AnimationControlTrack(int index, float[] scales, Quaternion[] rotations, Vector3[] translations,
        Func<float, float>? scaleCurve = null, Func<float, Quaternion>? rotationCurve = null,
        Func<float, Vector3>? translationCurve = null)
    {
        ControlIndex = index;
        ScaleKeys = Array.AsReadOnly(scales);
        RotationKeys = Array.AsReadOnly(rotations);
        TranslationKeys = Array.AsReadOnly(translations);
        this.scaleCurve = scaleCurve;
        this.rotationCurve = rotationCurve;
        this.translationCurve = translationCurve;
    }

    /** Quaternion sampling uses the native shortest-path normalized linear interpolation. */
    internal AnimationControlSample Sample(int first, int second, float amount)
    {
        float scale = 1;
        Quaternion rotation = Quaternion.Identity;
        Vector3 translation = Vector3.Zero;
        if (ScaleKeys.Count != 0)
        {
            float a = ScaleKeys[ScaleKeys.Count == 1 ? 0 : first];
            float b = ScaleKeys[ScaleKeys.Count == 1 ? 0 : second];
            scale = b * amount + a * (1 - amount);
        }
        if (RotationKeys.Count != 0)
        {
            var a = RotationKeys[RotationKeys.Count == 1 ? 0 : first];
            var b = RotationKeys[RotationKeys.Count == 1 ? 0 : second];
            if (Quaternion.Dot(a, b) < 0) b = new Quaternion(-b.X, -b.Y, -b.Z, -b.W);
            var value = new Quaternion(a.X + (b.X - a.X) * amount, a.Y + (b.Y - a.Y) * amount,
                a.Z + (b.Z - a.Z) * amount, a.W + (b.W - a.W) * amount);
            if (value.LengthSquared() < 1e-12f)
                throw new InvalidDataException("Animation quaternion has no finite direction.");
            rotation = Quaternion.Normalize(value);
        }
        if (TranslationKeys.Count != 0)
        {
            var a = TranslationKeys[TranslationKeys.Count == 1 ? 0 : first];
            var b = TranslationKeys[TranslationKeys.Count == 1 ? 0 : second];
            translation = a + (b - a) * amount;
        }
        float frame = first + amount;
        if (scaleCurve is not null) scale = scaleCurve(frame);
        if (rotationCurve is not null) rotation = rotationCurve(frame);
        if (translationCurve is not null) translation = translationCurve(frame);
        return new AnimationControlSample(ControlIndex, scale, rotation, translation,
            ScaleKeys.Count != 0, RotationKeys.Count != 0, TranslationKeys.Count != 0);
    }
}

/** Decoded SRT controls are game data; auxiliary clip resources are reported separately. */
public sealed class AnimationControlClip
{
    public uint TagHash { get; }
    public int FrameCount { get; }
    public int NodeCount { get; }
    public int ControlCount { get; }
    public int PoseMode { get; }
    public IReadOnlyList<AnimationControlGroup> ControlGroups { get; }
    public float DurationSeconds => (FrameCount - 1) / 30f;
    public IReadOnlyList<AnimationControlTrack> Tracks { get; }
    public IReadOnlyList<AnimationPayloadSection> AuxiliarySections { get; }

    /** Set when the clip's frames are poses picked by a parameter rather than played by time. */
    public AnimationPoseSpace? PoseSpace { get; }

    /** Hashes that pick this clip's row in the rig's retarget table. */
    public IReadOnlyList<uint> RetargetKeys { get; init; } = [];

    /** Track arrays and payload diagnostics remain immutable after decoding. */
    internal AnimationControlClip(uint tag, int frames, int nodes, int controls, int poseMode,
        AnimationControlTrack[] tracks, AnimationPayloadSection[] auxiliary,
        AnimationControlGroup[] groups, AnimationPoseSpace? poseSpace = null)
    {
        PoseSpace = poseSpace;
        TagHash = tag;
        FrameCount = frames;
        NodeCount = nodes;
        ControlCount = controls;
        PoseMode = poseMode;
        ControlGroups = Array.AsReadOnly(groups);
        Tracks = Array.AsReadOnly(tracks);
        AuxiliarySections = Array.AsReadOnly(auxiliary);
    }

    /** Time selects adjacent authored samples at the native 30 Hz rate. */
    public AnimationControlSample[] Sample(float seconds, bool loop = false)
    {
        if (!float.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        float frame;
        if (loop && DurationSeconds > 0)
            frame = ((seconds % DurationSeconds + DurationSeconds) % DurationSeconds) * 30;
        else frame = Math.Clamp(seconds * 30, 0, FrameCount - 1);
        int first = Math.Min((int)frame, FrameCount - 1);
        int second = Math.Min(first + 1, FrameCount - 1);
        float amount = Math.Clamp(frame - first, 0, 1);
        return Tracks.Select(track => track.Sample(first, second, amount)).ToArray();
    }
}
