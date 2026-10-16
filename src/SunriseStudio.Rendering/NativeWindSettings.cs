using System.Buffers.Binary;
using System.Numerics;
using Vortice.Direct3D;

namespace SunriseStudio.Rendering;

/**
 * A class-0x80806D33 wind settings tag, the render-global wind source copied by 0x7FF742ED57F0.
 * +0x08 is the direction expression in degrees, +0x38 the strength one, +0x68 the cycle in seconds.
 * +0x6C names the noise texture resource, +0x70 is the scroll-speed expression and +0xA0 the noise scale.
 */
public sealed class NativeWindSettings
{
    public const uint TagClass = 0x80806D33;
    /** Name hash 0x7FF742DCDB50 looks up in the root tables of class 0x80809780. */
    public const uint NameHash = 0xC6B22D98;
    // 0x7FF742ED8B80 falls back to these when an expression is absent.
    private static readonly float DefaultDirection = BitConverter.UInt32BitsToSingle(0x3FC90CB3);
    private const float DefaultStrength = 5f;
    private readonly NativeExpression? direction, strength, scroll;
    private readonly float period, scale;

    private NativeWindSettings(NativeExpression? direction, NativeExpression? strength, float period,
        NativeExpression? scroll, float scale, uint noiseResource)
    {
        this.direction = direction; this.strength = strength; this.period = period;
        this.scroll = scroll; this.scale = scale; NoiseResource = noiseResource;
    }

    /** Handle at +0x6C of the class-0x80806D36 resource whose +0x08 is the noise texture. */
    public uint NoiseResource { get; }

    /** Reads the wind settings tag; throws if it is shorter than 0xA4 bytes. */
    public static NativeWindSettings Parse(ReadOnlySpan<byte> tag)
    {
        if (tag.Length < 0xA4) throw new InvalidDataException("Wind settings tag is truncated.");
        return new(NativeExpression.Read(tag, 0x08), NativeExpression.Read(tag, 0x38),
            BinaryPrimitives.ReadSingleLittleEndian(tag[0x68..]), NativeExpression.Read(tag, 0x70),
            BinaryPrimitives.ReadSingleLittleEndian(tag[0xA0..]), BinaryPrimitives.ReadUInt32LittleEndian(tag[0x6C..]));
    }

    /**
     * The block 0x7FF742ED5580 writes after time seconds of wind.
     * @return Direction is (sin, cos, 0) of the angle in degrees; Strength is block +32.
     */
    public (Vector3 Direction, float Strength) Sample(double seconds)
    {
        // 0x7FF742ED6230 uses one when the cycle is unset.
        float cycle = period == -1f ? 1f : period;
        float position = (float)seconds / cycle;
        position -= MathF.Floor(position);
        var input = new Vector4(position);
        float degrees = direction?.Evaluate(input).X ?? DefaultDirection;
        float radians = degrees * MathF.PI / 180f;
        float force = strength?.Evaluate(input).X ?? DefaultStrength;
        return (new(MathF.Sin(radians), MathF.Cos(radians), 0), force);
    }

    /** Scroll speed at block strength / 80; one when the expression is absent. */
    public float ScrollSpeed(float strength) => scroll?.Evaluate(new Vector4(strength / 80f)).X ?? 1f;

    /** 0x7FF742ED6570: the noise scale, one when unset. */
    public float Scale => scale == -1f ? 1f : scale;
}

/**
 * The per-viewer wind state: the settings' block, and the extern 1 vectors and noise texture
 * 0x7FF742ED87F0 copies from it each frame.
 */
public sealed class NativeWindFrame(NativeWindSettings? settings)
{
    /** Extern 1 vector 22 before the settings load. */
    public static readonly Vector4 DefaultScroll = new(0, 0, 1, 1);
    /** Extern 1 resource 15, the noise texture; the renderer binds its view. */
    public static readonly TfxResource Noise = new("wind noise", ShaderResourceViewDimension.Texture2D);
    /** Extern 1 resources 16 and 17, which the frame writer leaves null; they read zero. */
    public static readonly TfxResource Unset = new("unset frame resource", ShaderResourceViewDimension.Texture2D);
    // 0x7FF742ED8B80 keeps the scroll as 20-bit fixed point.
    private const float ScrollUnit = 1048576f;
    private int scrollX, scrollY;
    private double last = double.NaN;

    public NativeWindSettings? Settings { get; } = settings;
    public Vector3 Direction { get; private set; }
    public float Strength { get; private set; }

    /** One tick: the block at seconds, then the noise scroll moved by the elapsed time. */
    public void Tick(double seconds)
    {
        if (Settings is null) return;
        float elapsed = double.IsNaN(last) ? 0 : (float)(seconds - last);
        last = seconds;
        (Direction, Strength) = Settings.Sample(seconds);
        float speed = Settings.ScrollSpeed(Strength);
        scrollX = (scrollX + (int)(ScrollUnit - speed * Direction.X * elapsed * ScrollUnit)) & 0xFFFFF;
        scrollY = (scrollY + (int)(ScrollUnit - speed * Direction.Y * elapsed * ScrollUnit)) & 0xFFFFF;
    }

    /** Extern 1 vector 21: the direction with strength / 80 in W; zero before the settings load. */
    public Vector4 Vector21 => Settings is null ? Vector4.Zero : new(Direction, Strength / 80f);

    /** Extern 1 vector 22: the noise scroll, one over the noise scale, then one. */
    public Vector4 Vector22 => Settings is null ? DefaultScroll
        : new(scrollX * (1f / ScrollUnit), scrollY * (1f / ScrollUnit), 1f / Settings.Scale, 1);

    /** Writes the frame values every material may read. */
    public void Apply(TfxInputs inputs)
    {
        inputs.Vectors[new(1, 21)] = Vector21;
        inputs.Vectors[new(1, 22)] = Vector22;
        inputs.Vectors[new(1, 23)] = Vector4.Zero;
        inputs.Resources[new(1, 15)] = Noise;
        inputs.Resources[new(1, 16)] = Unset;
        inputs.Resources[new(1, 17)] = Unset;
    }
}
