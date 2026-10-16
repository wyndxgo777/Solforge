using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Rendering;
using Vortice.Direct3D11;
using Vortice.DXGI;

/** Checks auto-exposure metering: reset, first-frame synchronous read and handling of black frames. */
internal static partial class Program
{
    /** Exposure sampling preserves coverage and FIFO ownership without waiting during steady polling. */
    private static void Exposure(bool warp)
    {
        using var renderer = new Dx11Renderer(IntPtr.Zero, 32, 32, warp);
        using var geometry = Depth(false);
        using var skyDepth = Depth(true);
        using var quarter = Color(new(.25f, .25f, .25f, 0));
        using var red = Color(new(1, 0, 0, 1));
        using var black = Color(new(0, 0, 0, 1));
        using (var meter = new NativeSceneComposite(renderer))
        {
            Require(meter.MeanLogLuminance is null && meter.PendingReadbacks == 0, "A new exposure meter retained old samples.");
            meter.MeasureLuminance(quarter, geometry.View, immediate: true);
            Mean(meter, -2); Require(meter.ReadbackCount == 1 && meter.PendingReadbacks == 0 && meter.BlackFraction == 0,
                "First-frame exposure was not read synchronously.");
            meter.MeasureLuminance(red, geometry.View, immediate: true);
            Mean(meter, MathF.Log2(.2126f));
            meter.MeasureLuminance(black, geometry.View, immediate: true);
            Mean(meter, MathF.Log2(.2126f));
            Require(MathF.Abs(meter.BlackFraction - 1) < .001f, "Black pixels did not preserve the last valid mean.");
        }
        using (var meter = new NativeSceneComposite(renderer))
        {
            meter.MeasureLuminance(black, geometry.View, immediate: true);
            Require(meter.MeanLogLuminance is null && meter.BlackFraction == 1, "An all-black first frame invented a luminance mean.");
            meter.MeasureLuminance(quarter, skyDepth.View, immediate: true);
            Require(meter.MeanLogLuminance is null && meter.BlackFraction == 0, "Geometry exposure included sky pixels.");
            meter.MeasureLuminance(quarter, skyDepth.View, sky: true, immediate: true);
            Mean(meter, -2);
        }
        using (var dark = Color(new(1e-5f, 1e-5f, 1e-5f, 1)))
        using (var light = Color(Vector4.One))
        using (var meter = new NativeSceneComposite(renderer))
        {
            meter.MeasureLuminance(dark, geometry.View, immediate: true);
            Mean(meter, MathF.Log2(1e-5f));
            meter.MeasureLuminance(dark, geometry.View, immediate: true, illumination: light);
            Mean(meter, MathF.Log2(NativeSceneComposite.MinimumReflectance));
            meter.MeasureLuminance(dark, geometry.View, immediate: true, uniformLight: 1);
            Mean(meter, MathF.Log2(NativeSceneComposite.MinimumReflectance));
            meter.MeasureLuminance(quarter, geometry.View, immediate: true, uniformLight: 1);
            Mean(meter, -2);
            meter.MeasureLuminance(quarter, geometry.View, immediate: true, illumination: light);
            Mean(meter, -2);
            meter.MeasureLuminance(black, geometry.View, immediate: true, illumination: light);
            Mean(meter, -2);
            Require(meter.BlackFraction == 1, "The reflectance floor lifted black pixels into the mean.");
        }
        var coverage = new float[32 * 32];
        var colors = new Vector4[coverage.Length];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                coverage[y * 32 + x] = x < 16 ? .3f : 1;
                colors[y * 32 + x] = x >= 16 ? Vector4.One : y < 16 ? new(.25f, .25f, .25f, 1) : new(0, 0, 0, 1);
            }
        using (var depth = renderer.CreateTexture(32, 32, Format.R32_Float, MemoryMarshal.AsBytes(coverage.AsSpan()), 32 * 4))
        using (var color = renderer.CreateTexture(32, 32, Format.R32G32B32A32_Float, MemoryMarshal.AsBytes(colors.AsSpan()), 32 * 16))
        using (var meter = new NativeSceneComposite(renderer))
        {
            meter.MeasureLuminance(color, depth.View, immediate: true);
            Mean(meter, -2); Require(MathF.Abs(meter.BlackFraction - .5f) < .001f, "Coverage weighting counted pixels outside the chosen surface.");
            meter.MeasureLuminance(color, depth.View, sky: true, immediate: true);
            Mean(meter, 0); Require(meter.BlackFraction == 0, "Sky sampling retained geometry black coverage.");
        }
        var movingColors = Enumerable.Range(0, 32 * 32)
            .Select(index => new Vector4(new Vector3(index % 32 < 16 ? .125f : 2), 1)).ToArray();
        var leftCoverage = Enumerable.Range(0, 32 * 32).Select(index => index % 32 < 16 ? .3f : 1).ToArray();
        var rightCoverage = leftCoverage.Select(value => value < 1 ? 1 : .3f).ToArray();
        using (var color = renderer.CreateTexture(32, 32, Format.R32G32B32A32_Float,
            MemoryMarshal.AsBytes(movingColors.AsSpan()), 32 * 16))
        using (var left = renderer.CreateTexture(32, 32, Format.R32_Float, MemoryMarshal.AsBytes(leftCoverage.AsSpan()), 32 * 4))
        using (var right = renderer.CreateTexture(32, 32, Format.R32_Float, MemoryMarshal.AsBytes(rightCoverage.AsSpan()), 32 * 4))
        using (var geometryMeter = new NativeSceneComposite(renderer))
        using (var skyMeter = new NativeSceneComposite(renderer))
        {
            var geometryQueue = new Queue<float>(); var skyQueue = new Queue<float>();
            bool geometryDark = false, geometryBright = false, skyDark = false, skyBright = false;
            for (int frame = 0; frame < 32; frame++)
            {
                Drain();
                bool rightVisible = (frame / 4 & 1) != 0;
                var depth = rightVisible ? right.View : left.View;
                ExposureStep(geometryMeter, color, depth, geometryQueue, rightVisible ? 1 : -3);
                ExposureStep(skyMeter, color, depth, skyQueue, rightVisible ? -3 : 1, sky: true);
                geometryDark |= geometryMeter.MeanLogLuminance is < -2.9f;
                geometryBright |= geometryMeter.MeanLogLuminance is > .9f;
                skyDark |= skyMeter.MeanLogLuminance is < -2.9f;
                skyBright |= skyMeter.MeanLogLuminance is > .9f;
            }
            Require(geometryDark && geometryBright && skyDark && skyBright,
                "Asynchronous exposure did not respond to moving geometry and sky coverage.");
            geometryMeter.MeasureLuminance(color, right.View, immediate: true); Mean(geometryMeter, 1);
            skyMeter.MeasureLuminance(color, right.View, sky: true, immediate: true); Mean(skyMeter, -3);
        }
        using var a = Color(new(.125f, .125f, .125f, 1));
        using var b = Color(new(.5f, .5f, .5f, 1));
        using var c = Color(new(2, 2, 2, 1));
        using var reset = Color(new(4, 4, 4, 1));
        GpuTexture[] samples = [a, b, c];
        float[] expected = [-3, -1, 1];
        using (var meter = new NativeSceneComposite(renderer))
        {
            var queued = new Queue<float>();
            for (int index = 0; index < 24; index++)
            {
                Drain();
                ExposureStep(meter, samples[index % 3], geometry.View, queued, expected[index % 3]);
            }
            DrainAccepted(meter, queued);
            int beforeReset = meter.ReadbackCount;
            meter.MeasureLuminance(reset, geometry.View, immediate: true);
            Mean(meter, 2);
            Require(meter.PendingReadbacks == 0 && meter.ReadbackCount == beforeReset + 1,
                "An immediate measurement did not replace all pending samples.");
            queued.Clear();
            ExposureStep(meter, a, geometry.View, queued, -3);
            ExposureStep(meter, b, geometry.View, queued, -1);
            Mean(meter, 2);
            int resetReads = meter.ReadbackCount;
            for (int retry = 0; retry < 128 && meter.ReadbackCount < resetReads + 2; retry++)
            { Drain(); ExposureStep(meter, c, geometry.View, queued, 1); Thread.Yield(); }
            Require(meter.ReadbackCount >= resetReads + 2, "A reset exposure queue never became readable.");

        }
        using (var meter = new NativeSceneComposite(renderer))
        {
            var queued = new Queue<float>();
            for (int index = 0; index < 96; index++)
                ExposureStep(meter, samples[index % 3], geometry.View, queued, expected[index % 3]);
            DrainAccepted(meter, queued);
            Console.WriteLine($"exposure_queue warp={warp} reads={meter.ReadbackCount} busy={meter.ReadbackBusyCount} skipped={meter.SkippedMeasurements} fifo=pass resumed=pass");
        }
        var disposed = new NativeSceneComposite(renderer);
        disposed.MeasureLuminance(a, geometry.View); disposed.MeasureLuminance(b, geometry.View); disposed.Dispose();
        try { disposed.MeasureLuminance(c, geometry.View); throw new InvalidDataException("Disposed exposure meter accepted a sample."); }
        catch (ObjectDisposedException) { }
        using (var fresh = new NativeSceneComposite(renderer))
        {
            Require(fresh.PendingReadbacks == 0 && fresh.MeanLogLuminance is null, "A fresh meter inherited disposed queue state.");
            fresh.MeasureLuminance(c, geometry.View, immediate: true); Mean(fresh, 1);
        }
        Console.WriteLine($"exposure warp={warp} first_frame=pass color=pass black=pass reflectance_floor=pass coverage=pass sky=pass moving_coverage=pass fifo=pass immediate_reset=pass dispose=pass");

        GpuTexture Color(Vector4 value)
        {
            var pixels = Enumerable.Repeat(value, 32 * 32).ToArray();
            return renderer.CreateTexture(32, 32, Format.R32G32B32A32_Float, MemoryMarshal.AsBytes(pixels.AsSpan()), 32 * 16);
        }
        GpuTexture Depth(bool sky)
        {
            var pixels = Enumerable.Repeat(sky ? 1f : .3f, 32 * 32).ToArray();
            return renderer.CreateTexture(32, 32, Format.R32_Float, MemoryMarshal.AsBytes(pixels.AsSpan()), 32 * 4);
        }
        void DrainAccepted(NativeSceneComposite meter, Queue<float> queued)
        {
            int accepted = meter.ReadbackCount + queued.Count;
            for (int retry = 0; retry < 128 && meter.ReadbackCount < accepted; retry++)
            { Drain(); ExposureStep(meter, quarter, geometry.View, queued, -2); Thread.Yield(); }
            Require(meter.ReadbackCount >= accepted, "An accepted exposure sample was lost after draining the GPU.");
        }
        void Drain()
        {
            using var fence = renderer.Device.CreateQuery(new QueryDescription(QueryType.Event));
            renderer.Context.End(fence); renderer.Context.Flush();
            var timeout = Stopwatch.StartNew();
            while (!renderer.Context.GetData(fence, AsyncGetDataFlags.None, out uint done) || done == 0)
            {
                if (timeout.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("GPU exposure fixture did not drain.");
                Thread.Yield();
            }
        }
    }

    private static void ExposureStep(NativeSceneComposite meter, GpuTexture sample, ID3D11ShaderResourceView depth,
        Queue<float> queued, float expected, bool sky = false)
    {
        int before = meter.ReadbackCount, skipped = meter.SkippedMeasurements, busy = meter.ReadbackBusyCount;
        meter.MeasureLuminance(sample, depth, sky);
        int consumed = meter.ReadbackCount - before;
        Require(consumed <= queued.Count, "Exposure read an unowned staging slot.");
        float latest = 0;
        for (int count = 0; count < consumed; count++) latest = queued.Dequeue();
        if (consumed != 0) Mean(meter, latest);
        int dropped = meter.SkippedMeasurements - skipped;
        Require(dropped is 0 or 1, "An exposure call reported multiple skipped samples.");
        if (dropped == 0) queued.Enqueue(expected);
        else Require(queued.Count == 3 && meter.ReadbackBusyCount > busy,
            "Exposure skipped a sample without a full busy queue.");
        Require(meter.PendingReadbacks == queued.Count && queued.Count <= 3,
            "A busy exposure queue overwrote a pending sample.");
    }

    private static void Mean(NativeSceneComposite meter, float expected) =>
        Require(meter.MeanLogLuminance is { } value && MathF.Abs(value - expected) < .008f,
            $"Exposure mean {meter.MeanLogLuminance} differs from {expected}.");
}
