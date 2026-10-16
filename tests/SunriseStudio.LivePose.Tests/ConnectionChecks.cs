using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SunriseStudio.Live;

internal static class ConnectionChecks
{
    /** The publisher opens only this test process's mapping and never attaches to a game. */
    public static void Run()
    {
        int process = Environment.ProcessId;
        string name = LivePoseProtocol.MappingName(process);
        var connection = new Connection(process);
        using (var mapping = MemoryMappedFile.OpenExisting(name))
        using (var publisher = mapping.CreateViewAccessor())
        {
            try
            {
                Check(publisher.ReadUInt32(0) == LivePoseProtocol.Magic && publisher.ReadUInt32(4) == LivePoseProtocol.Version
                    && publisher.ReadInt32(8) == LivePoseProtocol.MappingBytes && publisher.ReadInt32(12) == process,
                    "Pose mapping identity differs from the native protocol.");
                // CreateOrOpen (09ca809) lets a restarted editor reattach to an existing mapping instead of throwing.
                using (var reattached = new Connection(process)) { }
                Check(publisher.ReadUInt32(32) == 0, "Capture was asked for before Studio enabled it.");
                Check(publisher.ReadUInt32(36) == uint.MaxValue, "A player observation was requested before its handle was known.");
                connection.SetPlayer(42); Check(publisher.ReadUInt32(36) == 42, "Player request did not reach the publisher.");
                connection.SetPlayer(null); Check(publisher.ReadUInt32(36) == uint.MaxValue, "Clearing player request retained a handle.");
                connection.Enable(true);
                Check(publisher.ReadUInt32(32) == 1, "Enabling did not ask the game for draws.");
                ulong now = (ulong)Environment.TickCount64;
                publisher.Write(24, LivePoseProtocol.Version); publisher.Write(28, 1u);
                int published = 0;
                Publish(now, 42); connection.Poll(now);
                Check(connection.Draws is { Draws.Count: 1 } first && first.Draws[0].Owner == 42 && first.Draws[0].Palette[0].X == 3
                    && publisher.ReadUInt64(16) == now, "A capture was not adopted or the heartbeat was not refreshed.");
                var retained = connection.Draws!;
                publisher.Write(LivePoseProtocol.ControlBytes, published + 1);
                publisher.Write(LivePoseProtocol.ControlBytes + LivePoseProtocol.PaletteOffset, 999f);
                connection.Poll(now + 1);
                Check(ReferenceEquals(connection.Draws, retained) && retained.Draws[0].Palette[0].X == 3,
                    "An unfinished publication replaced or mutated the held capture.");
                connection.Poll(now + LivePoseProtocol.StaleAfterMilliseconds + 1);
                Check(connection.Draws is null, "A stalled odd-sequence writer kept an expired capture.");
                now += 1000;
                Publish(now, 43); connection.Poll(now);
                Check(connection.Draws?.Draws[0].Owner == 43, "A completed publication did not recover after a stalled writer.");
                Publish(now + 1, 44); connection.Poll(now);
                Check(connection.Draws is null, "A capture from the future was accepted.");
                Publish(now - LivePoseProtocol.StaleAfterMilliseconds - 1, 45); connection.Poll(now);
                Check(connection.Draws is null, "An expired capture was accepted.");

                connection.Enable(false);
                Check(connection.Draws is null && publisher.ReadUInt32(32) == 0, "Disabling kept a capture or the request.");
                Publish(now, 46); connection.Poll(now);
                Check(connection.Draws is null, "A disabled connection adopted a capture.");
                connection.Enable(true); connection.Poll(now);
                Check(connection.Draws?.Draws[0].Owner == 46, "Enabling again did not read the current capture.");
                Publish(now, 47);
                publisher.Write(24, LivePoseProtocol.Version + 1); connection.Poll(now);
                Check(connection.Draws is null && connection.Diagnostic.Contains("version", StringComparison.OrdinalIgnoreCase),
                    "A mismatched bridge version did not report an unavailable capture.");
                publisher.Write(24, 3u); connection.Poll(now);
                Check(connection.Draws is null && connection.Diagnostic.Contains("version", StringComparison.OrdinalIgnoreCase),
                    "Version 3 draws without captured generations were accepted.");
                publisher.Write(24, LivePoseProtocol.Version); publisher.Write(28, 0u); connection.Poll(now);
                Check(connection.Draws is null && connection.Diagnostic.Contains("unavailable", StringComparison.OrdinalIgnoreCase),
                    "A disabled observer did not report an unavailable capture.");
                publisher.Write(28, 1u); Publish(now, 48); connection.Poll(now);
                Check(connection.Draws is not null && connection.Diagnostic.Length == 0, "A ready bridge failed to recover.");

                void Publish(ulong time, uint owner)
                {
                    const int at = LivePoseProtocol.ControlBytes;
                    byte[] header = new byte[LivePoseProtocol.HeaderBytes], draw = new byte[LivePoseProtocol.DrawBytes];
                    void U(byte[] data, int offset, uint number) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), number);
                    U(header, 4, 1); BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), time); U(header, 16, 1); U(header, 20, 6);
                    U(draw, 0, owner); U(draw, 4, 91); U(draw, 8, 0x80800001); U(draw, 12, (uint)LivePaletteKind.Matrix3x4);
                    U(draw, 16, 2); U(draw, 20, 3); U(draw, 24, 0); U(draw, 28, 6);
                    publisher.Write(at, published + 1); Thread.MemoryBarrier();
                    publisher.WriteArray(at + 4, header, 4, header.Length - 4);
                    publisher.WriteArray(at + LivePoseProtocol.DrawsOffset, draw, 0, draw.Length);
                    publisher.Write(at + LivePoseProtocol.PaletteOffset, 3f);
                    Thread.MemoryBarrier(); published += 2; publisher.Write(at, published);
                }
            }
            finally { connection.Dispose(); }
            Check(publisher.ReadUInt64(16) == 0, "Disposal did not revoke the heartbeat while the publisher retained its view.");
        }
        Reject<FileNotFoundException>(() => { using var missing = MemoryMappedFile.OpenExisting(name); });
        using (var reopened = new Connection(process)) { }
        Console.WriteLine("live_pose_connection identity=checked seqlock=checked enable=checked stale=checked disposal=checked");
    }

    private sealed class Connection : IDisposable
    {
        private static readonly Type Type = typeof(LiveConnection).Assembly.GetType("SunriseStudio.Live.LivePoseConnection", true)!;
        private readonly object instance;
        public Connection(int process) => instance = Invoke(() => Activator.CreateInstance(Type, process))!;
        public LiveDraws? Draws => (LiveDraws?)Type.GetProperty("Draws")!.GetValue(instance);
        public string Diagnostic => (string)Type.GetProperty("Diagnostic")!.GetValue(instance)!;
        public void Enable(bool value) => Invoke(() => Type.GetMethod("Enable")!.Invoke(instance, [value]));
        public void SetPlayer(uint? value) => Invoke(() => Type.GetMethod("SetPlayer")!.Invoke(instance, [value]));
        public void Poll(ulong now) => Invoke(() => Type.GetMethod("Poll")!.Invoke(instance, [now]));
        public void Dispose() => ((IDisposable)instance).Dispose();
        private static object? Invoke(Func<object?> action)
        {
            try { return action(); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }

    private static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidDataException("Rejected mapping operation unexpectedly succeeded."); }
}
