using System.Runtime.InteropServices;

namespace SunriseStudio.Audio;

/** A private waveOut handle keeps playback, seeking and volume independent of other applications. */
public sealed class WavePlayback : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct Format
    {
        public ushort Tag, Channels;
        public uint Rate, BytesPerSecond;
        public ushort BlockAlign, Bits, Extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public IntPtr Data; public uint Length, Recorded; public UIntPtr User;
        public uint Flags, Loops; public IntPtr Next; public UIntPtr Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Time { public uint Type, Value, Reserved; }
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr device, uint id, ref Format format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr device);
    [DllImport("winmm.dll")] private static extern uint waveOutPause(IntPtr device);
    [DllImport("winmm.dll")] private static extern uint waveOutRestart(IntPtr device);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr device);
    [DllImport("winmm.dll")] private static extern uint waveOutGetPosition(IntPtr device, ref Time time, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutSetVolume(IntPtr device, uint volume);
    private IntPtr device, header;
    // The device reads the samples in place, so they stay pinned while loaded.
    private GCHandle pin;
    private double start;
    private float volume = 1;
    private bool paused, playing;
    public PcmWave? Wave { get; private set; }
    public bool Playing => playing && !paused && header != IntPtr.Zero && (Marshal.PtrToStructure<Header>(header).Flags & 1) == 0;
    public bool Paused => paused;
    public double Duration => Wave?.DurationSeconds ?? 0;
    public double Position
    {
        get
        {
            if (Wave is null || device == IntPtr.Zero) return 0;
            if (!playing) return start;
            var time = new Time { Type = 4 };
            Check(waveOutGetPosition(device, ref time, (uint)Marshal.SizeOf<Time>()));
            double elapsed = time.Type switch
            {
                4 => (double)time.Value / (Wave.SampleRate * Wave.BlockAlign),
                2 => (double)time.Value / Wave.SampleRate,
                1 => time.Value / 1000d,
                _ => throw new InvalidOperationException("Audio device returned an unsupported clock.")
            };
            return Math.Min(Duration, start + elapsed);
        }
    }

    /** Loads a WAV from disk for playback. */
    public void Load(string path) => Load(PcmWave.Read(path));

    /** Takes the wave as is; the caller must not change its samples while it is loaded. */
    public void Load(PcmWave wave)
    {
        if (wave.BitsPerSample != 16 || wave.Channels is < 1 or > 2 || wave.SampleRate is < 8000 or > 192000
            || wave.Samples.Length % wave.BlockAlign != 0)
            throw new InvalidDataException("Playback needs mono or stereo PCM16 audio.");
        Dispose(); Wave = wave; pin = GCHandle.Alloc(wave.Samples, GCHandleType.Pinned);
        var format = new Format { Tag = 1, Channels = Wave.Channels, Rate = (uint)Wave.SampleRate,
            BytesPerSecond = (uint)(Wave.SampleRate * Wave.BlockAlign), BlockAlign = (ushort)Wave.BlockAlign, Bits = 16 };
        Check(waveOutOpen(out device, uint.MaxValue, ref format, IntPtr.Zero, IntPtr.Zero, 0));
        SetVolume(volume); start = 0;
    }

    /** Starts or resumes playback. */
    public void Play()
    {
        if (Wave is null || device == IntPtr.Zero) return;
        if (paused) { Check(waveOutRestart(device)); paused = false; return; }
        if (Playing) return;
        if (Position >= Duration) start = 0;
        Queue(start);
    }

    /** Pauses playback. */
    public void Pause() { if (Playing) { Check(waveOutPause(device)); paused = true; } }
    /** Stops playback and rewinds. */
    public void Stop() { if (device == IntPtr.Zero) return; ReleaseBuffer(); start = 0; }

    /** Moves the play position to seconds. */
    public void Seek(double seconds)
    {
        if (Wave is null || !double.IsFinite(seconds)) return;
        bool resume = Playing; ReleaseBuffer(); start = Math.Clamp(seconds, 0, Duration);
        if (resume && start < Duration) Queue(start);
    }

    /** Sets playback volume (0..1). */
    public void SetVolume(float value)
    {
        volume = Math.Clamp(value, 0, 1);
        uint lane = (uint)MathF.Round(volume * ushort.MaxValue);
        if (device != IntPtr.Zero) Check(waveOutSetVolume(device, lane | lane << 16));
    }

    /** Plays from the given time through one prepared waveOut buffer. */
    private void Queue(double seconds)
    {
        ReleaseBuffer(); start = seconds;
        int offset = Math.Min(Wave!.Samples.Length, checked((int)(seconds * Wave.SampleRate) * Wave.BlockAlign));
        int length = Wave.Samples.Length - Math.Min(offset, Wave.Samples.Length);
        if (length == 0) return;
        header = Marshal.AllocHGlobal(Marshal.SizeOf<Header>());
        Marshal.StructureToPtr(new Header { Data = pin.AddrOfPinnedObject() + offset, Length = (uint)length },
            header, false);
        try
        {
            Check(waveOutPrepareHeader(device, header, (uint)Marshal.SizeOf<Header>()));
            Check(waveOutWrite(device, header, (uint)Marshal.SizeOf<Header>())); playing = true;
        }
        catch { ReleaseBuffer(); throw; }
    }

    private void ReleaseBuffer()
    {
        if (device != IntPtr.Zero)
        {
            waveOutReset(device);
            if (header != IntPtr.Zero) waveOutUnprepareHeader(device, header, (uint)Marshal.SizeOf<Header>());
        }
        if (header != IntPtr.Zero) Marshal.FreeHGlobal(header);
        header = IntPtr.Zero; paused = playing = false;
    }

    /** Stops playback and closes the output device. */
    public void Dispose()
    {
        ReleaseBuffer(); if (device != IntPtr.Zero) waveOutClose(device);
        if (pin.IsAllocated) pin.Free();
        device = IntPtr.Zero; Wave = null; start = 0;
    }

    private static void Check(uint result) { if (result != 0) throw new InvalidOperationException($"Audio device error {result}."); }
}
