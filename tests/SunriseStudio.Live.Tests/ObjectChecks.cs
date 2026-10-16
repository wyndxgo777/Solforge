using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using SunriseStudio.Live;

/** The object link answers one request once; Studio reads only the answer to its current request. */
internal static class ObjectChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run()
    {
        var type = typeof(LiveConnection).Assembly.GetType("SunriseStudio.Live.LiveObjectConnection")!;
        using var connection = (IDisposable)Activator.CreateInstance(type, Environment.ProcessId)!;
        using var mapping = MemoryMappedFile.OpenExisting(LiveObjectProtocol.MappingName(Environment.ProcessId));
        using var view = mapping.CreateViewAccessor();
        void Poll(ulong time) => type.GetMethod("Poll")!.Invoke(connection, [time]);
        LiveObjectSnapshot? Snapshot() => (LiveObjectSnapshot?)type.GetProperty("Snapshot")!.GetValue(connection);
        ulong now = (ulong)Environment.TickCount64;
        view.Write(LiveObjectProtocol.ClientVersionOffset, LiveObjectProtocol.Version);
        type.GetMethod("Request")!.Invoke(connection, [0x1234u, 7u]);
        uint request = view.ReadUInt32(LiveObjectProtocol.RequestOffset);
        Check(request != 0 && view.ReadUInt32(LiveObjectProtocol.HandleOffset) == 0x1234
            && view.ReadUInt32(LiveObjectProtocol.GenerationOffset) == 7, "The request did not write its object before its id.");
        Poll(now);
        Check(Snapshot() is { Status: LiveObjectStatus.Pending }, "An unanswered request was not pending.");

        byte[] answer = new byte[LiveObjectProtocol.MappingBytes];
        view.ReadArray(0, answer, 0, answer.Length);
        void U(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(answer.AsSpan(at), value);
        void F(int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(answer.AsSpan(at), value);
        U(LiveObjectProtocol.ResponseOffset, request - 1); U(44, 1); U(48, (uint)Environment.ProcessId);
        view.WriteArray(0, answer, 0, answer.Length); Poll(now);
        Check(Snapshot() is { Status: LiveObjectStatus.Pending }, "An answer to an older request was taken.");

        U(LiveObjectProtocol.ResponseOffset, request); BinaryPrimitives.WriteUInt64LittleEndian(answer.AsSpan(56), now);
        U(80, 0x80A00001); U(84, 0x10000); U(88, 0x55); U(92, 1 | 2 | 4 | 16 | 32 | 64);
        U(148, 0x55); U(152, 0x20); U(156, 0x10020);
        F(96, 1); F(100, 2); F(104, 3); F(108, 1); F(124, 1);
        U(128, 1); U(LiveObjectProtocol.ChildrenOffset, 0x66); U(LiveObjectProtocol.ChildrenOffset + 4, 3);
        U(132, 1); U(LiveObjectProtocol.ModelsOffset, 0x80B00001); U(144, 224); answer[LiveObjectProtocol.DatumOffset] = 0xAB;
        view.WriteArray(0, answer, 0, answer.Length); Poll(now);
        var snapshot = Snapshot()!;
        Check(snapshot is { Status: LiveObjectStatus.Complete, Handle: 0x1234, Generation: 7, ClassTag: 0x80A00001,
                DatumFlags: 0x10000, ParentHandle: 0x55, Dynamic: true, PlacedNotHidden: true, TransformAvailable: true,
                RootHandle: 0x55, RootFlags: 0x20, ChainFlags: 0x10020 }
            && snapshot.Position == new System.Numerics.Vector3(1, 2, 3) && snapshot.Children.Single() == new LiveObjectChild(0x66, 3)
            && snapshot.Models.Single() == 0x80B00001 && snapshot.Datum.Length == 224 && snapshot.Datum[0] == 0xAB,
            "The object answer layout differs from the native record.");

        view.Write(LiveObjectProtocol.ClientVersionOffset, LiveObjectProtocol.Version + 1); Poll(now);
        Check(((string)type.GetProperty("Diagnostic")!.GetValue(connection)!).Contains("restart"), "Object bridge mismatch lacks a note.");
        Console.WriteLine("live object_request=checked stale_answer=checked layout=checked");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
