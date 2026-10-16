using System.Buffers.Binary;
using SunriseStudio.Live;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;

/** Console test runner for live pose: wire offsets, shared publication buffers and capture expiry. */
internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Protocol();
            ConnectionChecks.Run();
            GeometryChecks.Run();
            if (args is ["--game", var game])
            {
                using var catalog = PackageCatalog.Open(game);
                var scope = new GameMaterialReader(catalog).ReadScopeExecution(0x80B356CF);
                Check(scope.ScopeIndex == 7 && scope.Name == "skinning" && scope.Vertex.ConstantBufferSlot == 11,
                    "Installed entity scope has a different VS binding.");
                Console.WriteLine($"live_pose_scope index={scope.ScopeIndex} name={scope.Name} vs={scope.Vertex.ConstantBufferSlot} ps={scope.Pixel.ConstantBufferSlot} rows={scope.Vertex.Data.ConstantTemplate.Length}");
                OwnedPoseGpuChecks.Run(catalog);
                SelectionLifecycleChecks.Run(catalog);
            }
            Console.WriteLine("live_pose result=pass");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Protocol()
    {
        byte[] header = new byte[LivePoseProtocol.HeaderBytes], draws = new byte[2 * LivePoseProtocol.DrawBytes], palette = new byte[16 * 16];
        void U(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
        void F(byte[] data, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset), value);
        U(header, 0, 2); U(header, 4, 1); BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), 1000);
        U(header, 16, 2); U(header, 20, 16); U(header, 24, 3);
        U(header, 32, 1); U(header, 36, 42); U(header, 40, 7);
        U(draws, 0, 42); U(draws, 4, 91); U(draws, 8, 0x80800001); U(draws, 12, (uint)LivePaletteKind.Matrix3x4);
        U(draws, 16, 2); U(draws, 20, 3); U(draws, 24, 0); U(draws, 28, 6); F(draws, 32, 2);
        U(draws, 80, 7); U(draws, 84, 11);
        F(palette, 0, -3); F(palette, 12, 5);
        LiveDraws Read(ulong now = 1100) => LivePoseProtocol.Read(header, draws, palette, now)!;
        var frame = Read(); var draw = frame.Draws.Single();
        Check(frame.Dropped == 3 && frame.Rejected == 0 && draw.Owner == 42 && draw.Component == 91 && draw.ModelBones == 2
            && draw.PoseBones == 3 && draw.Palette.Count == 6 && draw.FixedRows[0].X == 2 && draw.Palette[0].X == -3
            && draw.Anchor.X == 5 && draw.OwnerGeneration == 7 && draw.ComponentGeneration == 11
            && frame.Player == new LivePoseOwner(42, 7),
            "Wire offsets, the empty slot or the consumed prefix differ.");
        F(palette, 0, 100);
        Check(draw.Palette[0].X == -3, "Shared publication bytes escaped as mutable draw rows.");
        F(palette, 0, -3);
        Check(LivePoseProtocol.Read(header, draws, palette, 1501) is null && LivePoseProtocol.Read(header, draws, palette, 999) is null,
            "An expired or future capture was accepted.");
        U(header, 4, 0); Check(LivePoseProtocol.Read(header, draws, palette, 1100) is null, "A cleared capture was accepted."); U(header, 4, 1);
        void Rejected(string message) { var bad = Read(); Check(bad.Draws.Count == 0 && bad.Rejected == 1, message); }
        F(palette, 0, float.NaN); Rejected("A non-finite draw was kept."); F(palette, 0, -3);
        U(draws, 16, 513); Rejected("A draw over the bone limit was kept."); U(draws, 16, 2);
        U(draws, 20, 1); Rejected("A pose shorter than its model was kept."); U(draws, 20, 3);
        U(draws, 28, 5); Rejected("A row count that disagrees with its form was kept."); U(draws, 28, 6);
        U(draws, 24, 12); Rejected("A draw leaving the captured palette was kept."); U(draws, 24, 0);
        U(draws, 12, 99); Rejected("An unknown palette form was kept."); U(draws, 12, 2);
        U(draws, 0, uint.MaxValue); Rejected("An ownerless draw was kept."); U(draws, 0, 42);
        U(draws, 80, uint.MaxValue); Rejected("An invalid owner generation was kept."); U(draws, 80, 7);
        U(draws, 84, uint.MaxValue); Rejected("An invalid component generation was kept."); U(draws, 84, 11);
        Check(Read().Draws.Count == 1, "Restored fixture no longer reads.");
        U(header, 40, 8); Check(Read().Player == new LivePoseOwner(42, 8) && Read().Draws[0].OwnerGeneration == 7,
            "Current player identity was inferred from the captured draw.");
        U(header, 32, 0); Check(Read().Player is null, "Absent player identity stayed available."); U(header, 32, 1);
        U(header, 40, uint.MaxValue); Check(Read().Player is null, "Invalid player generation was accepted."); U(header, 40, 7);
        U(header, 16, LivePoseProtocol.DrawCapacity + 1); Reject(() => LivePoseProtocol.ReadHeader(header)); U(header, 16, 2);
        U(header, 20, LivePoseProtocol.VectorCapacity + 1); Reject(() => LivePoseProtocol.ReadHeader(header)); U(header, 20, 16);
        U(header, 0, 3); Reject(() => LivePoseProtocol.Read(header, draws, palette, 1100));
        Console.WriteLine("live_pose_protocol bounds=checked immutable=checked time=checked empty_slots=skipped malformed=counted finite=checked");
    }

    private static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid pose was accepted."); }
}
