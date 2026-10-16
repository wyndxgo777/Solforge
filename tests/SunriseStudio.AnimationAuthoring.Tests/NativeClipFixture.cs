using System.Buffers.Binary;

/** Builds synthetic native animation clip blobs (dense layout, explicit control maps) so authoring round-trip tests need no installation. */
internal static class NativeClipFixture
{
    /** Fixtures use existing dense layouts and explicit control maps. */
    internal static byte[] Create(int codec)
    {
        var bytes = new List<byte>(new byte[0x190]);
        void Put16(int offset, ushort value)
        {
            byte[] word = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(word, value);
            for (int i = 0; i < 2; i++) bytes[offset + i] = word[i];
        }
        void Put32(int offset, uint value)
        {
            byte[] word = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(word, value);
            for (int i = 0; i < 4; i++) bytes[offset + i] = word[i];
        }
        void Put64(int offset, long value)
        {
            byte[] word = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(word, value);
            for (int i = 0; i < 8; i++) bytes[offset + i] = word[i];
        }
        int Add(int count)
        {
            while (bytes.Count % 16 != 0) bytes.Add(0);
            int result = bytes.Count;
            bytes.AddRange(new byte[count]);
            return result;
        }
        void Array(int field, uint type, byte[] data, int stride)
        {
            int header = Add(32 + data.Length);
            Put32(header + 12, 0x80809FBD);
            Put64(header + 16, data.Length / stride);
            Put32(header + 24, type);
            Put64(field, data.Length / stride);
            Put64(field + 8, header + 16 - field - 8);
            for (int i = 0; i < data.Length; i++) bytes[header + 32 + i] = data[i];
        }
        Put16(0x13C, 3);
        Put16(0x13E, 1);
        Put16(0x140, 1);
        int resourceHeader = Add(16 + 0x48), resource = resourceHeader + 16;
        Put32(resource - 4, codec == 2 ? 0x80808F71U : 0x80808F6FU);
        Put64(0x18, resource - 0x18);
        Put16(resource, (ushort)codec);
        Put16(resource + 2, 1);
        Put16(resource + 4, 1);
        Put16(resource + 6, 1);
        Put32(resource + 0x10, 3);
        if (codec == 3)
        {
            Put32(resource + 0x14, BitConverter.SingleToUInt32Bits(2));
            for (int i = 0; i < 3; i++)
            {
                Put32(resource + 0x1C + i * 4, BitConverter.SingleToUInt32Bits(2));
                Put32(resource + 0x28 + i * 4, BitConverter.SingleToUInt32Bits(-1));
            }
        }
        ushort[] words = [32768, 32768, 32768,
            32768, 32768, 32768, 65535, 32768, 32768, 32768, 65535, 32768, 32768, 32768, 65535,
            16384, 32768, 32768, 32768, 32768, 32768, 49151, 32768, 32768];
        Array(resource + (codec == 3 ? 0x38 : 0x18), 0x8080000A,
            words.SelectMany(BitConverter.GetBytes).ToArray(), 2);
        if (codec == 2)
        {
            Array(resource + 0x28, 0x8080000F, Enumerable.Repeat(2f, 8).SelectMany(BitConverter.GetBytes).ToArray(), 4);
            Array(resource + 0x38, 0x8080000F, new[] { 0f, -1, -1, -1, -1, -1, -1, -1 }
                .SelectMany(BitConverter.GetBytes).ToArray(), 4);
        }
        for (int i = 0; i < 3; i++) Array(0xD8 + i * 16, 0x8080000A, [0, 0], 2);
        byte[] group = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(group, 0xAABBCCDD);
        BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(4), 1);
        Array(0x170, 0x8080907F, group, 8);
        return bytes.ToArray();
    }
}
