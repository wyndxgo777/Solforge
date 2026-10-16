using System.Buffers.Binary;
using SunriseStudio.Formats;

/** Checks CUI binding edits: unchanged bindings write no bytes, refused edits keep source identity, stale identities and bad indices are rejected. */
internal static class CuiBindingMutationChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run()
    {
        byte[] bytes = Fixture();
        var binding = CuiBindings.Read(bytes).Single();
        Check(CuiBindings.EditComparison(bytes, binding, binding.Transform.Comparison, binding.Transform.Operand)
            .AsSpan().SequenceEqual(bytes), "Unchanged binding wrote new bytes.");
        foreach (int offset in new[] { 0, 24 })
        {
            byte[] changed = bytes.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(changed.AsSpan(binding.Offset + offset), 7);
            Reject(() => CuiBindings.EditComparison(changed, binding, CuiComparison.Greater, 5));
            Check(BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(binding.Offset + offset)) == 7,
                "Refused binding edit changed source identity.");
        }
        Reject(() => CuiBindings.EditComparison(bytes, binding with { Index = -1 }, CuiComparison.Greater, 5));
        Console.WriteLine("cui_binding_mutation authored_identity=checked negative_index=rejected no_op=unchanged");
    }

    private static byte[] Fixture()
    {
        byte[] bytes = new byte[200];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, bytes.Length);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(88), 1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(96), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108), 0x80809FBD);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(112), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(120), 0x808046D8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(128), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(130), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(152), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(154), 2);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(176), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(188), 0x808046DC);
        bytes[192] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(196), 3);
        return bytes;
    }

    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Stale or invalid binding identity was accepted.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
