using Vortice.Direct3D11;

namespace SunriseStudio.Rendering;

/** GPU milliseconds between named marks, read back a few frames late so the CPU never waits. */
public sealed class GpuFrameTimer : IDisposable
{
    // Results are read this many frames after they were issued.
    private const int Latency = 4;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11Query[] disjoint = new ID3D11Query[Latency];
    private readonly ID3D11Query[][] stamps = new ID3D11Query[Latency][];
    private readonly int[] used = new int[Latency];
    private readonly bool[] issued = new bool[Latency];
    private int slot = -1;

    public IReadOnlyList<string> Sections { get; }
    /** Summed milliseconds per section since the last Reset. */
    public double[] Totals { get; }
    public int Frames { get; private set; }

    public GpuFrameTimer(ID3D11Device device, IReadOnlyList<string> sections)
    {
        context = device.ImmediateContext;
        Sections = sections;
        Totals = new double[sections.Count];
        for (int i = 0; i < Latency; i++)
        {
            disjoint[i] = device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
            stamps[i] = Enumerable.Range(0, sections.Count + 1)
                .Select(_ => device.CreateQuery(new QueryDescription(QueryType.Timestamp))).ToArray();
        }
    }

    /** Clears the accumulated totals and frame count. */
    public void Reset() { Array.Clear(Totals); Frames = 0; }

    /** Starts a frame; the first stamp opens section 0. */
    public void Begin()
    {
        slot = (slot + 1) % Latency;
        if (issued[slot]) Collect(slot);
        context.Begin(disjoint[slot]);
        context.End(stamps[slot][0]);
        used[slot] = 1;
    }

    /** Ends the section with this index; skipped sections read as zero. */
    public void Mark(int section)
    {
        if (slot < 0 || section + 1 >= stamps[slot].Length) return;
        for (int i = used[slot]; i <= section + 1; i++) context.End(stamps[slot][i]);
        used[slot] = Math.Max(used[slot], section + 2);
    }

    /** Ends the frame; when record is true its timings are collected later, otherwise they are discarded. */
    public void End(bool record)
    {
        if (slot < 0) return;
        context.End(disjoint[slot]);
        issued[slot] = record;
    }

    private void Collect(int index)
    {
        issued[index] = false;
        if (!context.GetData(disjoint[index], AsyncGetDataFlags.None, out QueryDataTimestampDisjoint timing)
            || timing.Disjoint) return;
        var values = new ulong[used[index]];
        for (int i = 0; i < values.Length; i++)
            if (!context.GetData(stamps[index][i], AsyncGetDataFlags.None, out values[i])) return;
        for (int i = 1; i < values.Length; i++)
            Totals[i - 1] += (values[i] - values[i - 1]) * 1000.0 / timing.Frequency;
        Frames++;
    }

    /** Releases the timestamp queries. */
    public void Dispose()
    {
        foreach (var query in disjoint) query.Dispose();
        foreach (var set in stamps) foreach (var query in set) query.Dispose();
    }
}
