using System.Numerics;

namespace SunriseStudio.Rendering;

/** Builds the per-frame TFX inputs (simulation clock and time of day) the way the native clock does. */
public static class NativeFrameInputs
{
    // The native clock converts unsigned simulation ticks at this rate.
    public const ulong TicksPerSecond = 673200;
    // The material clock wraps after the native 28,800-second period.
    public const uint WrapSeconds = 28800;
    private const ulong WrapTicks = TicksPerSecond * WrapSeconds;
    // Time-of-day system reset value 0x3E4CCCCD; the running game advances it.
    public const float TimeOfDayReset = 0.2f;
    // Viewer default where no start time is authored: mid-morning daylight.
    public const float ViewerTimeOfDay = 0.35f;

    /** @param timeOfDay Fraction of a day; sky and atmosphere materials read it as extern 1 float 4. */
    public static TfxInputs Create(double simulationSeconds, bool simulationActive = true,
        float timeOfDay = ViewerTimeOfDay)
    {
        var inputs = new TfxInputs();
        ApplySimulationTime(inputs, simulationSeconds, simulationActive, timeOfDay);
        return inputs;
    }

    /** Pause by holding the supplied time; an inactive simulation selects the native zero branch. */
    public static void ApplySimulationTime(TfxInputs inputs, double simulationSeconds,
        bool simulationActive = true, float timeOfDay = ViewerTimeOfDay)
    {
        if (!double.IsFinite(simulationSeconds) || simulationSeconds < 0
            || simulationSeconds >= (double)ulong.MaxValue / TicksPerSecond)
            throw new ArgumentOutOfRangeException(nameof(simulationSeconds));
        ulong ticks = checked((ulong)Math.Floor(simulationSeconds * TicksPerSecond));
        ApplySimulationTicks(inputs, ticks, simulationActive, timeOfDay);
    }

    /** Gives the clock floats time terms, so evaluation also returns outputs as functions of time. */
    public static void ApplyTimeTerms(TfxInputs inputs)
    {
        var time = TfxTerm.Time;
        // The 28,800-second wrap of floats 0 and 1 is left out.
        inputs.FloatTerms[new(1, 0)] = time;
        inputs.FloatTerms[new(1, 1)] = time;
        inputs.FloatTerms[new(1, 2)] = TfxTerm.Op("frc", time);
        inputs.FloatTerms[new(1, 3)] = TfxTerm.Sub(time, TfxTerm.Mul(TfxTerm.Op("round_ni",
            TfxTerm.Op("div", time, TfxTerm.Constant(420))), TfxTerm.Constant(420)));
    }

    /** The native frame writer reduces integer ticks before float conversion and division. */
    public static void ApplySimulationTicks(TfxInputs inputs, ulong simulationTicks,
        bool simulationActive = true, float timeOfDay = ViewerTimeOfDay)
    {
        inputs.Floats[new(1, 0)] = simulationActive
            ? (float)(simulationTicks % WrapTicks) / TicksPerSecond : 0;
        // Floats 1 to 3 are clock block +0x28, +0x24 and +0x20: the same wrapped time, then ticks
        // modulo one second and modulo 420 seconds. The inactive branch zeroes them.
        inputs.Floats[new(1, 1)] = inputs.Floats[new(1, 0)];
        inputs.Floats[new(1, 2)] = simulationActive ? (float)(simulationTicks % TicksPerSecond) / TicksPerSecond : 0;
        inputs.Floats[new(1, 3)] = simulationActive ? (float)(simulationTicks % (TicksPerSecond * 420)) / TicksPerSecond : 0;
        // Float 4 is clock block +0x2C, the time-of-day system value.
        inputs.Floats[new(1, 4)] = simulationActive ? timeOfDay : 0;
        // The frame writer stores zero at float 28 in both branches.
        inputs.Floats[new(1, 28)] = 0;
        inputs.Vectors[new(1, 26)] = Vector4.Zero;
        inputs.Vectors[new(1, 27)] = Vector4.One;
        inputs.Vectors[new(1, 28)] = new Vector4(1, 1, 0, 1);
    }
}
