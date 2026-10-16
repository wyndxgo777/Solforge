/**
 * File: Recovers clip names from an animation definition's name rows and from audio event paths, keeping the
 * evidence.
 */
using System.Buffers.Binary;
using System.Text;

namespace SunriseStudio.Animation;

/** One piece of naming evidence: where a name hash and audio path were found. */
public sealed record AnimationClipNameEvidence(uint NameHash, int DefinitionRow, int ClipListIndex,
    uint AudioEventTag, int EventOffset, string Name, string AudioPath);

/** Animation definition +0x68: 32-byte name rows of class 0x80809002, hash at +16, i16 clip slot at +24. */
public static class AnimationNameRows
{
    private const int MaximumRows = 65_536;

    /** Clip slots of every name row carrying hash; empty for a definition with no name rows. */
    public static IEnumerable<int> SlotsForHash(byte[] definition, uint hash) =>
        Read(definition).Where(row => row.Hash == hash).Select(row => row.Slot).ToArray();

    /** Every name row in order; empty for a definition too short to hold the array. */
    internal static (uint Hash, int Slot, byte Kind, int Row)[] Read(byte[] definition)
    {
        if (definition.Length < 0x78) return [];
        var data = new AnimationControlReader.Data(definition);
        if (data.I64(0x68) is < 0 or > MaximumRows)
            throw new InvalidDataException("Animation name row count is invalid.");
        return [.. data.Rows(0x68, 0x80809002, 32).Select((offset, index) =>
            (data.U32(offset + 16), (int)data.I16(offset + 24), data.U8(offset + 26), index))];
    }
}

/** Only a clip's typed audio-event paths may supply spellings for its owner's name hashes. */
internal static class AnimationClipNames
{
    /** An audio event path with the tag and offset it was read from. */
    internal sealed record EventPath(uint Tag, int Offset, string Path);
    /** A resolved clip name (null when ambiguous) with the evidence behind it. */
    internal sealed record Label(string? Name, IReadOnlyList<AnimationClipNameEvidence> Evidence);
    private const int MaximumRows = 65_536, MaximumPath = 4096, MaximumTokens = 64;

    /** Distinct spellings remain unresolved even when they share the same 32-bit hash. */
    internal static IReadOnlyDictionary<int, Label> Resolve(byte[] definition, uint[] clips,
        Func<uint, IReadOnlyList<EventPath>> paths)
    {
        var rows = AnimationNameRows.Read(definition);
        var repeatedHashes = rows.GroupBy(row => row.Hash).Where(group => group.Count() != 1)
            .Select(group => group.Key).ToHashSet();
        var evidence = new Dictionary<int, List<AnimationClipNameEvidence>>();
        foreach (var row in rows)
        {
            if (row.Slot < 0 || row.Slot >= clips.Length || row.Kind is not (1 or 2)
                || row.Hash is 0 or 0x811C9DC5 or uint.MaxValue || repeatedHashes.Contains(row.Hash)
                || clips[row.Slot] is 0 or uint.MaxValue) continue;
            foreach (var source in paths(clips[row.Slot]))
                foreach (string candidate in Candidates(source.Path))
                    if (Hash(candidate) == row.Hash)
                    {
                        if (!evidence.TryGetValue(row.Slot, out var matches)) evidence[row.Slot] = matches = [];
                        matches.Add(new(row.Hash, row.Row, row.Slot, source.Tag, source.Offset, candidate, source.Path));
                    }
        }
        return evidence.ToDictionary(pair => pair.Key, pair => new Label(
            Unique(pair.Value.Select(value => value.Name)), Array.AsReadOnly(pair.Value.Distinct().ToArray())));
    }

    /** Event pointers and string-bank bounds are checked before a path contributes a name. */
    internal static IReadOnlyList<EventPath> ReadPaths(byte[] clip, Func<uint, uint> classOf)
    {
        if (clip.Length < 0x190) return [];
        var data = new AnimationControlReader.Data(clip);
        if (data.I64(0x160) is < 0 or > MaximumRows)
            throw new InvalidDataException("Animation event count is invalid.");
        var rows = data.Rows(0x160, 0x8080902B, 8);
        if (rows.Length == 0) return [];
        var banks = StringBanks(clip);
        var result = new List<EventPath>();
        foreach (int row in rows)
        {
            if (data.I64(row) == 0) continue;
            int value = data.Pointer(row);
            if (data.U32(value - 4) != 0x80809033) continue;
            if (data.U8(value + 2) != 3) continue;
            uint eventTag = data.U32(value + 24);
            if (eventTag is 0 or uint.MaxValue || classOf(eventTag) != 0x80809802) continue;
            if (data.I64(value + 16) == 0) continue;
            int start = data.Pointer(value + 16);
            var owners = banks.Where(bank => start >= bank.Start && start < bank.End
                && (start == bank.Start || clip[start - 1] == 0)).ToArray();
            if (owners.Length != 1) continue;
            var bytes = clip.AsSpan(start, Math.Min(MaximumPath + 1, owners[0].End - start));
            int length = bytes.IndexOf((byte)0);
            if (length is <= 0 or > MaximumPath) continue;
            bytes = bytes[..length];
            if (bytes.ContainsAnyExceptInRange((byte)32, (byte)126)) continue;
            string path = Encoding.ASCII.GetString(bytes);
            if (Candidates(path).Count != 0) result.Add(new(eventTag, value, path));
        }
        return result.AsReadOnly();
    }

    /** A pointer may target any NUL-delimited entry inside one typed inline string bank. */
    private static List<(int Start, int End)> StringBanks(byte[] data)
    {
        var result = new List<(int, int)>();
        ReadOnlySpan<byte> marker = [0x65, 0, 0x80, 0x80];
        int scan = 0;
        while (scan <= data.Length - 12)
        {
            int found = data.AsSpan(scan).IndexOf(marker);
            if (found < 0) break;
            int offset = scan + found;
            scan = offset + 4;
            if (offset > data.Length - 12) break;
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset + 4, 8));
            if (length > 0 && length <= (ulong)(data.Length - offset - 12))
                result.Add((offset + 12, checked(offset + 12 + (int)length)));
        }
        return result;
    }

    /** Only contiguous tokens from the referenced audio filename are candidates. */
    internal static IReadOnlySet<string> Candidates(string path)
    {
        const string prefix = "content/audio/wwise_events/", extension = ".wwise_event";
        string normalized = path.Replace('\\', '/').ToLowerInvariant();
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal)
            || !normalized.EndsWith(extension, StringComparison.Ordinal)) return new HashSet<string>();
        string stem = normalized[(normalized.LastIndexOf('/') + 1)..^extension.Length];
        if (stem.Length is 0 or > MaximumPath || stem.Any(value => value is not (>= 'a' and <= 'z'
            or >= '0' and <= '9' or '_'))) return new HashSet<string>();
        string[] tokens = stem.Split('_');
        if (tokens.Length > MaximumTokens || tokens.Any(value => value.Length == 0)) return new HashSet<string>();
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (int start = 0; start < tokens.Length; start++)
            for (int count = 1; count <= tokens.Length - start; count++)
                result.Add(string.Join("_", tokens, start, count));
        return result;
    }

    /** The single distinct non-blank name, or null when there are none or several. */
    internal static string? Unique(IEnumerable<string?> names)
    {
        string[] values = names.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
            .Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    /** FNV-1 over UTF-16 code units, exact case; equal to the UTF-8 hash only for ASCII text. */
    internal static uint Hash(string text)
    {
        uint result = 0x811C9DC5;
        foreach (char value in text) result = unchecked(result * 0x01000193) ^ value;
        return result;
    }
}
