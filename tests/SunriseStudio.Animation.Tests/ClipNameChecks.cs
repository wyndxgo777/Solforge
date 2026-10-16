using System.Buffers.Binary;
using System.Text;
using SunriseStudio.Animation;
using SunriseStudio.Packages;

/** Checks that animation clip names and event paths are resolved from the right string bank owner, never from an unrelated clip or directory; guards the clip-browser labels. */
internal static class ClipNameChecks
{
    private const uint ClipA = 0x81000001, ClipB = 0x81000002, Audio = 0x81000003;
    private const string Prefix = "content\\audio\\wwise_events\\test\\";

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run()
    {
        Require(AnimationClipNames.Hash("idle") == 0x6FB760FF, "FNV-1 changed.");
        byte[] clip = Clip(Prefix + "fixture_walk_forward_first.wwise_event",
            Prefix + "fixture_walk_forward_second.wwise_event");
        var paths = AnimationClipNames.ReadPaths(clip, AudioClass);
        Require(paths.Count == 2 && paths[0].Path.EndsWith("first.wwise_event", StringComparison.Ordinal)
            && paths[1].Path.EndsWith("second.wwise_event", StringComparison.Ordinal),
            "Interior string-bank pointers did not preserve separate event paths.");
        var labels = AnimationClipNames.Resolve(Definition(("walk_forward", 0)), [ClipA, ClipB],
            tag => tag == ClipA ? paths : []);
        Require(labels[0].Name == "walk_forward" && labels[0].Evidence.Count == 2
            && labels[0].Evidence.All(value => value.ClipListIndex == 0 && value.DefinitionRow == 0
                && value.AudioEventTag == Audio && value.NameHash == AnimationClipNames.Hash(value.Name)),
            "Owner-specific evidence was lost.");

        var unrelated = AnimationClipNames.ReadPaths(Clip(Prefix + "unrelated.wwise_event"), AudioClass);
        labels = AnimationClipNames.Resolve(Definition(("walk_forward", 0)), [ClipA, ClipB],
            tag => tag == ClipA ? unrelated : paths);
        Require(labels.Count == 0, "A different clip supplied a name.");
        Require(AnimationClipNames.Candidates("content\\audio\\wwise_events\\walk_forward\\other.wwise_event")
            .All(value => value != "walk_forward"), "A directory supplied a clip name.");
        Require(AnimationClipNames.Candidates("unrelated_walk_forward").Count == 0,
            "An untyped string supplied a name.");
        Require(AnimationClipNames.ReadPaths(clip, _ => 0).Count == 0,
            "A non-audio tag supplied an audio path.");

        byte[] unsupported = Clip(Prefix + "walk_forward.wwise_event");
        int eventOffset = EventOffset(unsupported);
        U32(unsupported, eventOffset - 4, 0x80809032);
        Require(AnimationClipNames.ReadPaths(unsupported, AudioClass).Count == 0,
            "An unrelated typed event supplied a name.");
        byte[] hidden = Clip(Prefix + "unrelated.wwise_event");
        byte[] tail = Encoding.ASCII.GetBytes(Prefix + "walk_forward.wwise_event\0");
        int originalLength = hidden.Length;
        System.Array.Resize(ref hidden, originalLength + tail.Length + 12);
        U32(hidden, originalLength, 0x80800065); I64(hidden, originalLength + 4, tail.Length);
        tail.CopyTo(hidden, originalLength + 12);
        Require(AnimationClipNames.ReadPaths(hidden, AudioClass).Single().Path.Contains("unrelated", StringComparison.Ordinal),
            "An unreferenced string bank supplied a name.");

        const string collisionA = "zo1p1s5gsp", collisionB = "hlpjtlmy7n";
        Require(AnimationClipNames.Hash(collisionA) == 0xC9AC16B6
            && AnimationClipNames.Hash(collisionB) == 0xC9AC16B6, "Collision fixture changed.");
        paths = AnimationClipNames.ReadPaths(Clip(Prefix + collisionA + ".wwise_event",
            Prefix + collisionB + ".wwise_event"), AudioClass);
        labels = AnimationClipNames.Resolve(Definition((collisionA, 0)), [ClipA], _ => paths);
        Require(labels[0].Name is null && labels[0].Evidence.Select(value => value.Name).Distinct().Count() == 2,
            "An FNV collision chose an arbitrary spelling.");
        paths = AnimationClipNames.ReadPaths(Clip(Prefix + "idle.wwise_event", Prefix + "walk.wwise_event"), AudioClass);
        labels = AnimationClipNames.Resolve(Definition(("idle", 0), ("walk", 0)), [ClipA], _ => paths);
        Require(labels[0].Name is null, "Distinct aliases chose an arbitrary owner label.");
        labels = AnimationClipNames.Resolve(Definition(("idle", 0), ("idle", 1)), [ClipA, ClipB], _ => paths);
        Require(labels.Count == 0, "A repeated native lookup hash named an ambiguous clip-list row.");
        labels = AnimationClipNames.Resolve(Definition(("idle", -1), ("walk", 20)), [ClipA], _ => paths);
        Require(labels.Count == 0, "An invalid signed clip-list slot was accepted.");

        byte[] malformed = Definition(("idle", 0));
        I64(malformed, 0x68, long.MaxValue);
        Throws(() => AnimationClipNames.Resolve(malformed, [ClipA], _ => paths));
        malformed = Clip(Prefix + "idle.wwise_event");
        I64(malformed, 0x168, long.MaxValue);
        Throws(() => AnimationClipNames.ReadPaths(malformed, AudioClass));
        var left = new EntityAnimationReference(1, 2, 0, ClipA)
        { Name = "idle", NameEvidence = [new(1, 0, 0, Audio, 0, "idle", "left")] };
        var right = left with { DefinitionTag = 3, Name = "walk",
            NameEvidence = [new(2, 0, 0, Audio, 0, "walk", "right")] };
        Require(EntityAnimationCatalog.UniqueName([left, right]) is null
            && EntityAnimationCatalog.UniqueName([left, left]) == "idle",
            "Grouped clips lost owner-specific ambiguity.");
        Console.WriteLine("animation_names synthetic=pass typed_paths=1 collisions=refused unrelated=refused");
    }

    /** Runs the same kind of checks against the real packages of the owned installation. */
    public static void Owned(PackageCatalog catalog)
    {
        var reader = new EntityAnimationCatalog(catalog);
        const uint definition = 0x80BC90DB;
        var links = reader.ReadDefinition(definition);
        Require(links.All(value => value.Name is null && value.NameEvidence.Count == 0),
            "Link-only reads unexpectedly resolved names.");
        var named = reader.ReadDefinition(definition, resolveNames: true);
        foreach (var expected in new[] { (0, 0x80BC90D2U, "idle"), (7, 0x80BC90D9U, "scare"), (8, 0x80BC90DAU, "scare_var2") })
        {
            var value = named.Single(clip => clip.Index == expected.Item1);
            Require(value.ClipTag == expected.Item2 && value.Name == expected.Item3
                && value.NameDiagnostic is null && value.NameEvidence.Count > 0,
                $"Owned animation name differs for slot {expected.Item1}.");
            var paths = AnimationClipNames.ReadPaths(catalog.Read(value.ClipTag),
                tag => catalog.TryGetEntry(tag, out var entry) ? entry.Reference : 0);
            Require(value.NameEvidence.All(evidence => evidence.ClipListIndex == value.Index
                && AnimationClipNames.Hash(evidence.Name) == evidence.NameHash
                && paths.Any(path => path.Tag == evidence.AudioEventTag && path.Offset == evidence.EventOffset
                    && path.Path == evidence.AudioPath)), "Owned name has no same-clip typed evidence.");
        }
        Require(named.Select(value => (value.Index, value.ClipTag)).SequenceEqual(
            links.Select(value => (value.Index, value.ClipTag))), "Name recovery changed animation identity.");
        var previous = catalog.ReadOverride;
        try
        {
            foreach (Exception failure in new Exception[]
                { new FileNotFoundException("Name fixture patch missing."), new UnauthorizedAccessException("Name fixture access refused.") })
            {
                catalog.ReadOverride = tag => tag == 0x80BC90D2 ? throw failure : previous?.Invoke(tag);
                var unavailable = reader.ReadDefinition(definition, resolveNames: true);
                Require(unavailable.Select(value => (value.Index, value.ClipTag)).SequenceEqual(
                    links.Select(value => (value.Index, value.ClipTag)))
                    && unavailable.All(value => value.Name is null && value.NameDiagnostic == failure.Message),
                    "An optional name read removed clip links.");
            }
            catalog.ReadOverride = tag => tag == 0x80BC90D2 ? throw new OperationCanceledException() : previous?.Invoke(tag);
            bool cancelled = false;
            try { _ = reader.ReadDefinition(definition, resolveNames: true); }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled, "Name recovery swallowed cancellation.");
        }
        finally { catalog.ReadOverride = previous; }
        Console.WriteLine($"animation_names owned=pass definition={definition:X8} named={named.Count(value => value.Name is not null)} total={named.Count}");
    }

    private static uint AudioClass(uint tag) => tag == Audio ? 0x80809802U : 0;
    private static byte[] Definition(params (string Name, short Slot)[] rows)
    {
        byte[] data = new byte[0x200 + rows.Length * 32];
        Array(data, 0x68, 0x180, rows.Length, 0x80809002);
        for (int i = 0; i < rows.Length; i++)
        {
            int row = 0x190 + i * 32;
            U32(data, row + 16, AnimationClipNames.Hash(rows[i].Name));
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(row + 24), rows[i].Slot);
            data[row + 26] = 1;
        }
        return data;
    }

    /** Multiple event paths share one typed string bank with separate interior pointers. */
    private static byte[] Clip(params string[] paths)
    {
        byte[] data = new byte[0x8000];
        Array(data, 0x160, 0x1A0, paths.Length, 0x8080902B);
        int resource = (0x1B0 + paths.Length * 8 + 11) & ~7;
        int bank = resource + paths.Length * 40;
        int cursor = bank + 12;
        U32(data, bank, 0x80800065);
        for (int i = 0; i < paths.Length; i++)
        {
            int row = 0x1B0 + 8 * i, value = resource + 40 * i;
            I64(data, row, value - row); U32(data, value - 4, 0x80809033);
            data[value + 2] = 3; U32(data, value + 24, Audio);
            I64(data, value + 16, cursor - value - 16);
            cursor += Encoding.ASCII.GetBytes(paths[i] + "\0", data.AsSpan(cursor));
        }
        I64(data, bank + 4, cursor - bank - 12);
        return data[..cursor];
    }

    private static int EventOffset(byte[] clip) => 0x1B0 + checked((int)BinaryPrimitives.ReadInt64LittleEndian(clip.AsSpan(0x1B0)));
    private static void Array(byte[] data, int field, int header, int count, uint type)
    { I64(data, field, count); I64(data, field + 8, header - field - 8); U32(data, header - 4, 0x80809FBD); I64(data, header, count); U32(data, header + 8, type); }
    private static void I64(byte[] data, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset), value);
    private static void U32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Throws(Action action)
    {
        try { action(); throw new Exception("Malformed name metadata was accepted."); }
        catch (Exception error) when (error is InvalidDataException or OverflowException) { }
    }
}
