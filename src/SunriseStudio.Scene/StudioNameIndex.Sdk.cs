using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SunriseStudio.Scene;

/** SDK part of StudioNameIndex: names taken from installed SDK text. */
public sealed partial class StudioNameIndex
{
    /** Installed SDK text remains candidate evidence until the live package reader supplies a name. */
    private void ReadSdkNames(CancellationToken token, Action<string>? progress)
    {
        string sdk = Path.Combine(catalog.GameRoot, "bin", "x64", "Sunrise", "sdk");
        string activities = Path.Combine(sdk, "lua", "activities");
        string shards = Path.Combine(sdk, "scenarios");
        string[] modules = Directory.Exists(activities)
            ? Directory.GetFiles(activities, "*.lua").Order(StringComparer.Ordinal).ToArray() : [];
        string[] files = Directory.Exists(shards)
            ? Directory.GetFiles(shards, "*.pack").Order(StringComparer.Ordinal).ToArray() : [];
        int total = modules.Length + files.Length;
        for (int i = 0; i < modules.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            TryRead(Path.GetFileName(modules[i]), () => ReadActivityModule(modules[i]));
            if ((i & 63) == 0) Stage("Sunrise SDK names", progress, i + 1, total);
        }
        for (int i = 0; i < files.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            TryRead(Path.GetFileName(files[i]), () => ReadSdkShardNames(files[i], token));
            if ((i & 15) == 0)
            {
                Stage("Sunrise SDK names", progress, modules.Length + i + 1, total);
            }
        }
        Stage("Sunrise SDK names", progress, total, total);
    }

    /** Lua modules are parsed as generated metadata; no Lua source is executed. */
    private void ReadActivityModule(string path)
    {
        string[] lines = File.ReadLines(path).Take(40).ToArray();
        string? Text(string key)
        {
            string? line = lines.FirstOrDefault(x => x.TrimStart().StartsWith(key + " = ", StringComparison.Ordinal));
            if (line is null) return null;
            int quote = line.IndexOf('"'), last = line.LastIndexOf('"');
            if (quote < 0 || last <= quote) return null;
            try { return JsonSerializer.Deserialize<string>(line[quote..(last + 1)]); }
            catch (JsonException) { return null; }
        }
        uint Hash(string key)
        {
            string? line = lines.FirstOrDefault(x => x.TrimStart().StartsWith(key + " = ", StringComparison.Ordinal));
            if (line is null) return 0;
            int start = line.IndexOf("0x", StringComparison.Ordinal);
            return start >= 0 && line.Length >= start + 10
                && uint.TryParse(line.AsSpan(start + 2, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value) ? value : 0;
        }
        string? name = Text("display_name") ?? Text("name");
        if (string.IsNullOrWhiteSpace(name)) return;
        foreach (string key in new[] { "scenario_tag", "activity_root_tag" })
        {
            uint tag = Hash(key);
            if (catalog.TryGetEntry(tag, out var entry) && (key == "activity_root_tag" || entry.Reference == 0x80809994))
                AddTag(tag, new(name, StudioNameSource.InstalledSdk, StudioNameConfidence.Candidate,
                    path, 50, tag));
        }
        if (Text("name") is { } internalName)
            AddHash(Fnv1(internalName), new(internalName, StudioNameSource.InstalledSdk,
                StudioNameConfidence.Candidate, path, 65));
        sdkFiles++;
    }

    /** Only the five name sections are read; geometry and mission payloads stay on disk. */
    private void ReadSdkShardNames(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] header = new byte[664];
        stream.ReadExactly(header);
        if (!header.AsSpan(0, 8).SequenceEqual("SRGWSHRD"u8) || U32(header, 8) is not (14 or 15)
            || U32(header, 12) != header.Length || U64(header, 16) != (ulong)stream.Length
            || U32(header, 28) != 35 || U32(header, 36) != 0)
            throw new InvalidDataException("Unsupported SDK shard header.");
        uint scenarioTag = U32(header, 24);
        if (!catalog.TryGetEntry(scenarioTag, out var scenario) || scenario.Reference != 0x80809994) return;
        byte[] Section(int index, uint expectedStride)
        {
            int field = 104 + index * 16;
            ulong offset = U64(header, field);
            uint count = U32(header, field + 8), stride = U32(header, field + 12);
            ulong size = (ulong)count * stride;
            if (stride != expectedStride || size > 128 * 1024 * 1024 || offset < (ulong)header.Length
                || offset > (ulong)stream.Length || size > (ulong)stream.Length - offset)
                throw new InvalidDataException("SDK name section leaves its shard.");
            var bytes = new byte[(int)size];
            stream.Position = (long)offset;
            stream.ReadExactly(bytes);
            return bytes;
        }
        byte[] hashes = Section(25, 20), tags = Section(26, 24), candidates = Section(27, 140);
        byte[] inline = Section(28, 12), text = Section(29, 1);
        StudioNameCandidate? Candidate(uint index)
        {
            if (index >= candidates.Length / 140) return null;
            int offset = (int)index * 140;
            int length = BinaryPrimitives.ReadUInt16LittleEndian(candidates.AsSpan(offset + 136));
            byte evidence = candidates[offset + 138];
            if (length is <= 0 or > 128 || evidence is < 1 or > 3) return null;
            string name;
            try { name = StrictText.GetString(candidates, offset, length).TrimEnd('\0'); }
            catch (DecoderFallbackException) { return null; }
            if (name.Length == 0 || name.Any(char.IsControl)) return null;
            uint source = U32(candidates, offset + 128), sourceClass = U32(candidates, offset + 132);
            if (source != 0 && (!catalog.TryGetEntry(source, out var entry) || sourceClass != 0 && entry.Reference != sourceClass)) return null;
            return new(name, StudioNameSource.InstalledSdk, StudioNameConfidence.Candidate,
                path, evidence == 1 ? 65 : evidence == 2 ? 70 : 80, source);
        }
        for (int p = 0; p < hashes.Length; p += 20)
        {
            if ((p & 0xFFF) == 0) token.ThrowIfCancellationRequested();
            if (hashes[p + 17] != 0) continue;
            uint hash = U32(hashes, p), first = U32(hashes, p + 4), count = U32(hashes, p + 8);
            if ((ulong)first + count > (ulong)candidates.Length / 140) throw new InvalidDataException("SDK name candidate range is invalid.");
            for (uint i = 0; i < count; i++)
                if (Candidate(first + i) is { } value && Fnv1(value.Text) == hash) AddHash(hash, value);
        }
        for (int p = 0; p < tags.Length; p += 24)
        {
            if ((p & 0xFFF) == 0) token.ThrowIfCancellationRequested();
            uint tag = U32(tags, p), type = U32(tags, p + 4), first = U32(tags, p + 8), count = U32(tags, p + 12);
            if (!catalog.TryGetEntry(tag, out var entry) || type != 0 && entry.Reference != type) continue;
            if ((ulong)first + count > (ulong)candidates.Length / 140) throw new InvalidDataException("SDK tag candidate range is invalid.");
            for (uint i = 0; i < count; i++)
                if (Candidate(first + i) is { } value) AddTag(tag, value);
        }
        for (int p = 0; p < inline.Length; p += 12)
        {
            if ((p & 0xFFF) == 0) token.ThrowIfCancellationRequested();
            uint hash = U32(inline, p), start = U32(inline, p + 4), length = U32(inline, p + 8);
            if (length is 0 or > 1024 || (ulong)start + length > (ulong)text.Length) continue;
            string name;
            try { name = StrictText.GetString(text, (int)start, (int)length).TrimEnd('\0'); }
            catch (DecoderFallbackException) { continue; }
            if (name.Any(char.IsControl) || Fnv1(name) != hash) continue;
            AddHash(hash, new(name, StudioNameSource.InstalledSdk, StudioNameConfidence.Candidate,
                path, 65, scenarioTag));
        }
        sdkFiles++;
    }

    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    private static ulong U64(byte[] data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
}
