using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using SunriseStudio.Live;

/** Checks signed world scale reading from live rows: sign is kept, hidden or unplaced rows lose their model, and missing scale is never invented. */
internal static class WorldScaleChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run()
    {
        ulong now = (ulong)Environment.TickCount64;
        byte[] bytes = new byte[LiveWorldProtocol.PlacementsBytes];
        void U(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
        void Q(int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), value);
        void F(int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
        // Row 0 starts at 64: handle +16, generation +20, scale +44, orientation W +60, time +64, flags +72.
        U(0, 2); U(4, 3); Q(8, now); U(16, 1); U(20, 2); U(48, 1);
        U(80, 123); U(84, 4); F(124, 1); U(136, 5); Q(128, now);
        foreach (float scale in new[] { 0f, -2.5f, 3.75f })
        {
            F(108, scale);
            Check(LiveWorldProtocol.ReadPlacements(bytes, now)!.Rows[0].Scale == scale, "Signed native scale changed.");
        }
        var counted = LiveWorldProtocol.ReadPlacements(bytes, now)!;
        Check(counted.Excluded == 1 && counted.Exhaustive && counted.Rows[0].PlacedNotHidden is null,
            "Excluded rows or the unread parent chain were misread.");
        U(136, 5 | 8 | 32);
        Check(LiveWorldProtocol.ReadPlacements(bytes, now)!.Rows[0].PlacedNotHidden == true, "The placed bit was lost.");
        U(136, 5 | 32);
        Check(LiveWorldProtocol.ReadPlacements(bytes, now)!.Rows[0] is { PlacedNotHidden: false, ShowsModel: false },
            "An unplaced or hidden row still shows its model.");
        U(136, 1);
        Check(LiveWorldProtocol.ReadPlacements(bytes, now)!.Rows[0].Scale is null, "Missing scale was invented.");
        U(136, 4);
        Check(LiveWorldProtocol.ReadPlacements(bytes, now)!.Rows[0].Scale is null, "Failed pose retained scale.");
        U(136, 5); Q(128, now - 5001);
        Check(LiveWorldProtocol.ReadPlacements(bytes, now)!.Rows[0].Scale is null, "Expired pose retained scale.");
        Q(128, now); F(108, float.NaN);
        try { LiveWorldProtocol.ReadPlacements(bytes, now); throw new Exception("Nonfinite scale was accepted."); }
        catch (InvalidDataException) { }
        F(108, -2);

        var type = typeof(LiveConnection).Assembly.GetType("SunriseStudio.Live.LiveWorldConnection")!;
        using var connection = (IDisposable)Activator.CreateInstance(type, Environment.ProcessId)!;
        using var mapping = MemoryMappedFile.OpenExisting(LiveWorldProtocol.MappingName(Environment.ProcessId));
        using var view = mapping.CreateViewAccessor();
        view.WriteArray(LiveWorldProtocol.PlacementsOffset, bytes, 0, bytes.Length);
        void Poll(ulong time) => type.GetMethod("Poll")!.Invoke(connection, [time]);
        object? Placements() => type.GetProperty("Placements")!.GetValue(connection);
        string Diagnostic() => (string)type.GetProperty("Diagnostic")!.GetValue(connection)!;
        Poll(now);
        Check(Placements() is null, "Unacknowledged bridge data was read.");
        view.Write(24, LiveWorldProtocol.Version); Poll(now);
        Check(Placements() is LivePlacements { Rows.Count: 1 }, "Current bridge data was rejected.");
        view.Write(24, 3u); Poll(now);
        Check(Placements() is null && Diagnostic().Contains("restart"), "Bridge mismatch did not clear data and report update.");
        view.Write(24, 0u); Poll(now + 6000);
        Check(Placements() is null && Diagnostic().Contains("unavailable"), "Missing bridge lacks an actionable note.");
        view.Write(24, LiveWorldProtocol.Version); Poll(now);
        Check(Placements() is not null && Diagnostic().Length == 0, "Valid bridge recovery retained mismatch state.");
        ObjectChecks.Run();
        Console.WriteLine("live scale=signed_zero flags=checked bridge_version=checked");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
