using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SunriseStudio.Interchange;

/** Bounded external shards keep geometry out of JSON and avoid whole-scene memory copies. */
internal sealed class WorldBinaryWriter(string root, CancellationToken token) : IDisposable
{
    // Each view stays contiguous; shards remain below the reader's 256 MiB file limit.
    private const int ShardBytes = 128 * 1024 * 1024;
    private FileStream? stream;
    private IncrementalHash? hash;
    private string path = "";
    private long bytes;
    private readonly List<WorldBuffer> buffers = [];
    public IReadOnlyList<WorldBuffer> Buffers => buffers;

    /** Writes count x components floats, taken from value(item, component), and returns its buffer view. */
    internal WorldBufferView Floats(int count, int components, Func<int, int, float> value)
    {
        var view = Begin(count, components, 4, "Float32");
        Span<float> block = stackalloc float[4096];
        int used = 0;
        for (int row = 0; row < count; row++)
        {
            if ((row & 4095) == 0) token.ThrowIfCancellationRequested();
            for (int lane = 0; lane < components; lane++)
            {
                float number = value(row, lane);
                if (!float.IsFinite(number)) throw new InvalidDataException("World geometry contains a nonfinite attribute.");
                block[used++] = number;
                if (used == block.Length) { Write(MemoryMarshal.AsBytes(block)); used = 0; }
            }
        }
        if (used != 0) Write(MemoryMarshal.AsBytes(block[..used]));
        return view;
    }

    /** Values 0 to 1 stored as UInt8 lanes, rounded to nearest. */
    internal WorldBufferView Bytes(int count, int components, Func<int, int, float> value)
    {
        var view = Begin(count, components, 1, "UInt8");
        byte[] block = new byte[4096 * components];
        int used = 0;
        for (int row = 0; row < count; row++)
        {
            if ((row & 4095) == 0) token.ThrowIfCancellationRequested();
            for (int lane = 0; lane < components; lane++)
                block[used++] = (byte)Math.Round(Math.Clamp(value(row, lane), 0, 1) * 255);
            if (used == block.Length) { Write(block); used = 0; }
        }
        if (used != 0) Write(block.AsSpan(0, used));
        return view;
    }

    /** Writes triangle indices as a buffer view; throws if empty, not a multiple of 3 or out of range. */
    internal WorldBufferView Indices(uint[] values, int vertices)
    {
        if (values.Length == 0 || values.Length % 3 != 0 || values.Any(value => value >= vertices))
            throw new InvalidDataException("World geometry has invalid triangle indices.");
        var view = Begin(values.Length, 1, 4, "UInt32");
        for (int offset = 0; offset < values.Length; offset += 16384)
        {
            token.ThrowIfCancellationRequested();
            Write(MemoryMarshal.AsBytes(values.AsSpan(offset, Math.Min(16384, values.Length - offset))));
        }
        return view;
    }

    /** Writes count fixed-size records with the encode callback and returns its buffer view. */
    internal WorldBufferView Records(int count, int stride, Action<int, SpanWriter> encode)
    {
        var view = Begin(count, stride, 1, "UInt8");
        byte[] row = new byte[stride];
        var writer = new SpanWriter(row);
        for (int index = 0; index < count; index++)
        {
            if ((index & 1023) == 0) token.ThrowIfCancellationRequested();
            Array.Clear(row); encode(index, writer); Write(row);
        }
        return view;
    }

    /** Record helpers write the declared little-endian wire layout without a struct ABI dependency. */
    internal sealed class SpanWriter(byte[] bytes)
    {
        /** Writes a little-endian 32-bit value. */
        internal void U32(int offset, uint value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        /** Writes a little-endian signed 32-bit value. */
        internal void I32(int offset, int value) => System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
        /** Writes a little-endian 64-bit value. */
        internal void U64(int offset, ulong value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
        /** Writes a float; throws for non-finite values. */
        internal void F32(int offset, float value)
        {
            if (!float.IsFinite(value)) throw new InvalidDataException("World placement contains a nonfinite matrix.");
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value);
        }
    }

    private WorldBufferView Begin(int count, int components, int width, string type)
    {
        if (!BitConverter.IsLittleEndian) throw new NotSupportedException("World binary export needs a little-endian host.");
        long length = checked((long)count * components * width);
        if (count < 0 || components < 1 || length > ShardBytes) throw new InvalidDataException("A world buffer view exceeds its shard limit.");
        if (stream is not null && bytes + length > ShardBytes) Close();
        if (stream is null)
        {
            path = $"buffers/{buffers.Count:D4}.bin";
            string full = BundlePaths.Resolve(root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            stream = new(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
            hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); bytes = 0;
        }
        return new(buffers.Count, bytes, count, components, type);
    }

    private void Write(ReadOnlySpan<byte> data) { stream!.Write(data); hash!.AppendData(data); bytes += data.Length; }

    /** Closes the buffer file. */
    internal void Finish() => Close();
    private void Close()
    {
        if (stream is null) return;
        stream.Flush(); stream.Dispose(); stream = null;
        buffers.Add(new(path, bytes, Convert.ToHexString(hash!.GetHashAndReset())));
        hash.Dispose(); hash = null;
    }

    /** Releases the stream and hash. */
    public void Dispose() { stream?.Dispose(); hash?.Dispose(); }
}
