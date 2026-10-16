/**
 * File: Compact binary diff of an edited asset against its original (copy, xor and add operations) with
 * create and apply.
 */
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SunriseStudio.Projects;

/** What a delta operation does: copy base bytes, xor a mask over base bytes, or add literal bytes. */
public enum DeltaKind { Copy, Xor, Add }
/** One delta step: kind, offset and length in the base or result, plus literal data for adds. */
public sealed record DeltaOperation(DeltaKind Kind, int Offset, int Length, byte[]? Data = null);

/** Copy operations refer to the verified base; edits store XOR masks instead of base bytes. */
public sealed record BinaryDelta(int Version, string BaseSha256, int BaseLength,
    string ResultSha256, int ResultLength, IReadOnlyList<DeltaOperation> Operations)
{
    // Anchors bound the index to one row per sixteen base bytes.
    private const int AnchorStride = 16;
    private const int MaximumLiteral = 65536;

    /** A shifted base range remains a reference after insertions or deletions. */
    public static BinaryDelta Create(ReadOnlySpan<byte> original, ReadOnlySpan<byte> edited)
    {
        var anchors = new Dictionary<ulong, int>();
        for (int i = 0; i <= original.Length - sizeof(ulong); i += AnchorStride)
            anchors.TryAdd(BinaryPrimitives.ReadUInt64LittleEndian(original[i..]), i);
        var operations = new List<DeltaOperation>();
        int cursor = 0, baseCursor = 0, pending = -1, pendingBase = 0;
        while (cursor < edited.Length)
        {
            int match = -1;
            if (baseCursor < original.Length && original[baseCursor] == edited[cursor])
                match = baseCursor;
            else if (cursor <= edited.Length - sizeof(ulong)
                && anchors.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(edited[cursor..]), out int anchor))
                match = anchor;
            int length = 0;
            if (match >= 0)
                while (match + length < original.Length && cursor + length < edited.Length
                    && original[match + length] == edited[cursor + length]) length++;
            if (length >= sizeof(ulong) || length > 0 && match == baseCursor && pending < 0)
            {
                if (pending >= 0)
                {
                    AppendMask(operations, original, edited[pending..cursor], pendingBase);
                    pending = -1;
                }
                if (operations.LastOrDefault() is { Kind: DeltaKind.Copy } previous
                    && previous.Offset + previous.Length == match)
                    operations[^1] = previous with { Length = previous.Length + length };
                else operations.Add(new(DeltaKind.Copy, match, length));
                cursor += length;
                baseCursor = match + length;
            }
            else
            {
                if (pending < 0) { pending = cursor; pendingBase = baseCursor; }
                cursor++;
                baseCursor++;
                if (cursor - pending == MaximumLiteral)
                {
                    AppendMask(operations, original, edited[pending..cursor], pendingBase);
                    pending = -1;
                }
            }
        }
        if (pending >= 0) AppendMask(operations, original, edited[pending..], pendingBase);
        return new(1, Hash(original), original.Length, Hash(edited), edited.Length, operations);
    }

    /** A base mismatch stops reconstruction before any output is returned. */
    public byte[] Apply(ReadOnlySpan<byte> original)
    {
        if (Version != 1 || BaseLength != original.Length || BaseSha256 != Hash(original))
            throw new InvalidDataException("Original asset changed. Restore the matching package version.");
        if (ResultLength < 0 || Operations is null || Operations.Count > ResultLength + 1L)
            throw new InvalidDataException("Invalid delta length.");
        var result = new byte[ResultLength];
        int cursor = 0;
        foreach (var operation in Operations)
        {
            if (operation.Length <= 0 || operation.Length > result.Length - cursor || operation.Offset < 0)
                throw new InvalidDataException("Invalid delta operation.");
            var destination = result.AsSpan(cursor, operation.Length);
            switch (operation.Kind)
            {
                case DeltaKind.Copy when operation.Data is null
                    && operation.Offset <= original.Length - operation.Length:
                    original.Slice(operation.Offset, operation.Length).CopyTo(destination);
                    break;
                case DeltaKind.Xor when original.Length > 0 && operation.Data?.Length == operation.Length:
                    for (int i = 0; i < destination.Length; i++)
                        destination[i] = (byte)(operation.Data[i]
                            ^ original[(int)(((long)operation.Offset + i) % original.Length)]);
                    break;
                case DeltaKind.Add when original.IsEmpty && operation.Data?.Length == operation.Length:
                    operation.Data.CopyTo(destination);
                    break;
                default: throw new InvalidDataException("Invalid delta operation.");
            }
            cursor += operation.Length;
        }
        if (cursor != result.Length || ResultSha256 != Hash(result))
            throw new InvalidDataException("Delta output verification failed.");
        return result;
    }

    private static void AppendMask(List<DeltaOperation> operations, ReadOnlySpan<byte> original,
        ReadOnlySpan<byte> edited, int baseOffset)
    {
        byte[] mask = edited.ToArray();
        if (original.Length == 0) { operations.Add(new(DeltaKind.Add, 0, mask.Length, mask)); return; }
        int offset = baseOffset % original.Length;
        for (int i = 0; i < mask.Length; i++) mask[i] ^= original[(int)(((long)offset + i) % original.Length)];
        operations.Add(new(DeltaKind.Xor, offset, mask.Length, mask));
    }

    /** Upper-case hex SHA-256 of the bytes. */
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
