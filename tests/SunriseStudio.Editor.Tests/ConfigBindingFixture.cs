using System.Buffers.Binary;

/** Builds a synthetic config blob (arrays, types, slots) used by the config binding tests. */
internal static class ConfigBindingFixture
{
    /** Builds the synthetic config blob. */
    public static byte[] Create()
    {
        var b = new Bytes(344);
        int names = b.Array(24, 0x80804618, 4, 2);
        int types = b.Array(56, 0x80804622, 16, 2);
        for (int i = 0; i < 2; i++) { b.U32(names + i * 4, (uint)(1 + i)); b.U32(types + i * 16, 0x0CE30D0B); b.U16(types + i * 16 + 10, (ushort)(i + 1)); }
        int slots = b.Array(120, 0x80804943, 16, 1);
        int flags = b.Array(136, 0x8080468D, 1, 1);
        int floats = b.Array(184, 0x80804694, 4, 4);
        float[] values = [10, 20, 100, 50];
        for (int i = 0; i < 4; i++) b.U32(floats + i * 4, BitConverter.SingleToUInt32Bits(values[i]));
        int objects = b.Array(72, 0x8080462A, 64, 2);
        for (int i = 0; i < 2; i++)
        {
            int owner = objects + i * 64;
            b.U64(owner, 0x701CFA3E7F9B89AF); b.U64(owner + 8, 0xECBABAE340FB9D24);
            b.U64(owner + 16, 0xAD52C0B9); b.U64(owner + 24, 0xB93A719D);
            int component = b.Array(owner + 32, 0x808046D4, 24, 1); b.U64(component, (ulong)(i + 1));
            int properties = b.Array(component + 8, 0x80804858, 24, 5);
            for (int j = 0; j < 5; j++)
            {
                int row = properties + j * 24;
                b.I64(row, slots - row); b.U64(row + 8, j == 4 ? 0x41AUL : (ulong)(0x206 + j));
                b.I64(row + 16, (j == 4 ? flags : floats + j * 4) - row - 16);
            }
        }
        int bindings = b.Array(88, 0x808046D8, 56, 3);
        int integer = b.Reserve(16) + 8, real = b.Reserve(16) + 8;
        b.U32(integer - 4, 0x808046DC); b.U8(integer, 1); b.U8(integer + 1, 4); b.U32(integer + 4, 7);
        b.U32(real - 4, 0x808046DB); b.U8(real, 2); b.U8(real + 1, 2); b.U32(real + 4, BitConverter.SingleToUInt32Bits(.75f));
        for (int i = 0; i < 3; i++)
        {
            int row = bindings + i * 56;
            b.U16(row, 1); b.U16(row + 2, 1); b.U16(row + 24, 2); b.U16(row + 26, 2);
            b.U64(row + 16, i == 2 ? 0x206UL : 0x420UL); b.U64(row + 40, 0x201);
            b.I64(row + 48, (i == 2 ? real : integer) - row - 48);
        }
        return b.Finish();
    }

    private sealed class Bytes(int count)
    {
        private byte[] bytes = new byte[count];
        /** Grows the buffer by a 16-byte-aligned block and returns its offset. */
        public int Reserve(int size) { int at = checked((bytes.Length + 15) & ~15); System.Array.Resize(ref bytes, at + size); return at; }
        /** Writes an array header (count, relative offset, marker, type) at the field and returns the offset of its data. */
        public int Array(int field, uint type, int stride, int count)
        {
            int at = Reserve(32 + stride * count), data = at + 32;
            U32(at + 12, 0x80809FBD); I64(at + 16, count); U64(at + 24, type);
            I64(field, count); I64(field + 8, at + 16 - field - 8); return data;
        }
        /** Writes a byte at the offset. */
        public void U8(int at, byte value) => bytes[at] = value;
        /** Writes a little-endian 16-bit value at the offset. */
        public void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
        /** Writes a little-endian 32-bit value at the offset. */
        public void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
        /** Writes a little-endian 64-bit value at the offset. */
        public void U64(int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), value);
        /** Writes a little-endian signed 64-bit value at the offset. */
        public void I64(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
        /** Stamps the total length at offset 0 and returns the bytes. */
        public byte[] Finish() { I64(0, bytes.Length); return bytes; }
    }
}
