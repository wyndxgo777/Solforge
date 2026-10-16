using System.Diagnostics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

/** Phase counters locate repeated source reads without changing the rule implementations. */
internal static class IsolationProfileChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run(PackageCatalog catalog, string capture)
    {
        ObjectEditCompiler.PrepareInstallation(catalog);
        var project = StudioProject.Open(capture);
        for (int pass = 0; pass < 2; pass++)
        {
            var reads = new ReadProfile(pass);
            var measured = ObjectEditCompiler.DefaultRules.Select(rule => new Measured(rule, pass, reads)).ToArray();
            project.ObjectEditCompiler = new TrackingCompiler(new(measured), reads);
            var timer = Stopwatch.StartNew();
            var compiled = project.CompileObjectEdits(catalog);
            Console.WriteLine($"isolation_profile pass={pass} phase=total ms={timer.Elapsed.TotalMilliseconds:F1} generated={compiled.GeneratedAssets.Count}");
            reads.Print();
        }
    }

    private sealed class TrackingCompiler(ObjectEditCompiler inner, ReadProfile profile) : IObjectEditCompiler
    {
        /** Compiles through the wrapped compiler while profiling the raw reads. */
        public ObjectEditCompilation Compile(IReadOnlyList<ObjectEdit> edits, Func<uint, byte[]> readRaw, PackageCatalog catalog) =>
            inner.Compile(edits, tag => profile.Read(tag, readRaw, catalog), catalog);
        /** Compiles through the wrapped compiler while profiling the raw reads. */
        public ObjectEditCompilation Compile(IReadOnlyList<ObjectEdit> edits, Func<uint, byte[]> readRaw,
            PackageCatalog catalog, ObjectEditCompileOptions options) => inner.Compile(edits,
                tag => profile.Read(tag, readRaw, catalog), catalog, options);
        /** Forwards to the wrapped compiler. */
        public ObjectEdit? Normalize(ObjectEdit edit, Func<uint, byte[]> readRaw, PackageCatalog catalog) =>
            inner.Normalize(edit, readRaw, catalog);
    }

    private sealed class ReadProfile(int pass)
    {
        internal string Phase = "rows";
        private readonly Dictionary<(string Phase, uint Class), (long Count, long Bytes, long Ticks)> reads = [];
        /** Reads a tag through the callback and records the read in the profile. */
        internal byte[] Read(uint tag, Func<uint, byte[]> read, PackageCatalog catalog)
        {
            long start = Stopwatch.GetTimestamp();
            byte[] bytes = read(tag);
            long ticks = Stopwatch.GetTimestamp() - start;
            uint type = catalog.TryGetEntry(tag, out var entry) ? entry.Reference : 0;
            var key = (Phase, type); var previous = reads.GetValueOrDefault(key);
            reads[key] = (previous.Count + 1, previous.Bytes + bytes.Length, previous.Ticks + ticks);
            return bytes;
        }
        /** Writes the recorded read profile to the console. */
        internal void Print()
        {
            foreach (var (key, value) in reads.OrderByDescending(pair => pair.Value.Ticks).Take(12))
                Console.WriteLine($"isolation_sources pass={pass} phase={key.Phase} class={key.Class:X8} entries={value.Count} "
                    + $"payload_bytes={value.Bytes} read_ms={value.Ticks * 1000.0 / Stopwatch.Frequency:F1}");
        }
    }

    private sealed class Measured(IObjectEditRule inner, int pass, ReadProfile reads) : IObjectEditRule
    {
        public ObjectEditLayout Layout => inner.Layout;
        public IReadOnlyCollection<ObjectEditLayout> Layouts => inner.Layouts;
        /** Rule stub used to time an isolation pass. */
        public void Apply(ObjectEditContext context, IReadOnlyList<ObjectEdit> edits)
        {
            var before = context.Catalog.ReadStatistics;
            long allocated = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            reads.Phase = inner.GetType().Name;
            inner.Apply(context, edits);
            timer.Stop();
            var after = context.Catalog.ReadStatistics;
            Console.WriteLine($"isolation_profile pass={pass} phase={inner.GetType().Name} ms={timer.Elapsed.TotalMilliseconds:F1} "
                + $"entries={after.EntryReads - before.EntryReads} stored_bytes={after.StoredBytes - before.StoredBytes} "
                + $"decoded_bytes={after.DecodedBytes - before.DecodedBytes} allocated={GC.GetTotalAllocatedBytes(true) - allocated}");
        }
    }
}
