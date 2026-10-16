using System.Runtime.InteropServices;

namespace SunriseStudio.Packages;

/** Decode and Recycle may run on any thread; the owner must stop decoding before Dispose. */
internal sealed unsafe class OodleDecoder : IDisposable
{
    // Package blocks expand to at most 256 KiB in 16 KiB steps.
    private const int BlockSize = 0x40000;
    private const int DecodeStep = 0x4000;
    // A fuzz-safe decode may write 256 bytes past the raw size (OodleLZ_GetDecodeBufferSize).
    private const int OutputSize = BlockSize + 0x100;
    // One spare buffer per concurrent decoder is enough; more would only hold memory.
    private const int PooledBuffers = 16;
    // Each call owns its output buffer; borrowing and returning buffers takes the pool lock.
    private readonly Stack<byte[]> available = [];
    private nint library;
    private readonly DecompressFunction decompress;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long DecompressFunction(byte* input, long inputSize, byte* output,
        long outputSize, int fuzzSafe, int checkCrc, long verbosity, nint decodeBuffer,
        long decodeBufferSize, nint callback, nint callbackContext, nint decoderMemory,
        long decoderMemorySize, int threadPhase);

    /** Only the owned game's codec is loaded; no DLL is copied into the project. */
    internal OodleDecoder(string gameRoot)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Package decompression needs Windows x64.");
        var path = Path.Combine(gameRoot, "bin", "x64", "oo2core_3_win64.dll");
        if (!File.Exists(path))
            throw new FileNotFoundException("The installed Oodle 3 library was not found.", path);
        library = NativeLibrary.Load(path);
        try
        {
            decompress = Marshal.GetDelegateForFunctionPointer<DecompressFunction>(
                NativeLibrary.GetExport(library, "OodleLZ_Decompress"));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /** Tries 256 KiB down in 16 KiB steps; the largest exact decode wins, as a smaller one can be a truncated prefix. */
    internal ReadOnlyMemory<byte> Decode(byte[] input)
    {
        ObjectDisposedException.ThrowIf(library == 0, this);
        byte[]? pooled;
        lock (available) available.TryPop(out pooled);
        byte[] output = pooled ?? new byte[OutputSize];
        output.AsSpan().Clear();
        try
        {
            fixed (byte* source = input)
            fixed (byte* destination = output)
            {
                for (int size = BlockSize; size >= DecodeStep; size -= DecodeStep)
                {
                    long result = decompress(source, input.Length, destination, size,
                        1, 0, 0, 0, 0, 0, 0, 0, 0, 3);
                    if (result == size)
                        return output.AsMemory(0, size);
                }
            }
            throw new InvalidDataException("Oodle rejected the package block.");
        }
        catch { Return(output); throw; }
    }

    /** The catalog returns a decoded buffer only after its last cached block reference is removed. */
    internal void Recycle(ReadOnlyMemory<byte> decoded)
    {
        if (library != 0 && MemoryMarshal.TryGetArray(decoded, out var segment) && segment.Offset == 0
            && segment.Array is { Length: OutputSize } buffer) Return(buffer);
    }

    private void Return(byte[] buffer)
    {
        lock (available) if (available.Count < PooledBuffers) available.Push(buffer);
    }

    /** Frees the native Oodle library. */
    public void Dispose()
    {
        if (library != 0)
            NativeLibrary.Free(library);
        library = 0;
        lock (available) available.Clear();
    }
}
