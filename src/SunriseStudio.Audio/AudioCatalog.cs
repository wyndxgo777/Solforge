/** File: Reads audio-related package entries (events and sound banks) into documents with their media. */
using System.Buffers.Binary;
using System.Text;
using SunriseStudio.Packages;

namespace SunriseStudio.Audio;

/** One media file: id, package tag, name and size, or embedded bytes. */
public sealed record AudioMedia(uint Id, uint? Tag, string Name, int Size, byte[]? Embedded = null);
/** One raw object of a sound bank: its type, id and bytes. */
public sealed record AudioBankObject(byte Type, uint Id, byte[] Data);
/** A parsed sound bank with its objects and embedded media. */
public sealed record AudioBank(uint Tag, uint Id, uint Version, byte[] Bytes,
    IReadOnlyList<AudioBankObject> Objects, IReadOnlyList<AudioMedia> EmbeddedMedia);
/** What one package tag resolved to: event or bank, its media and read diagnostics. */
public sealed record AudioDocument(uint Tag, string Name, string Kind, uint? EventId, uint? RelatedEventId,
    AudioBank? Bank, IReadOnlyList<AudioMedia> Media, IReadOnlyList<string> Diagnostics);

/** Package event links and Wwise chunk boundaries are checked before exporting any media. */
public sealed class AudioCatalog(PackageCatalog catalog, Func<uint, string?>? names = null)
{
    public const uint EventClass = 0x80809802;
    /** True when the package entry is an audio event or bank. */
    public static bool Supports(PackageEntry entry) => entry.Reference == EventClass
        || entry.Type == 26 && entry.SubType is 4 or 5 or 6;

    /** Reads an event, bank or stream with its linked bank and media; soft problems become diagnostics. */
    public AudioDocument Read(uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || !Supports(entry))
            throw new NotSupportedException("Choose an audio event, bank or stream.");
        string name = names?.Invoke(tag) ?? catalog.GetName(tag) ?? $"Audio {tag:X8}";
        if (entry.Type == 26 && entry.SubType == 6)
            return new(tag, name, "Stream", null, null, null, [new(entry.Reference, tag, name, entry.Size)], []);
        byte[] bytes = catalog.Read(tag);
        var diagnostics = new List<string>();
        var media = new List<AudioMedia>();
        uint? eventId = null, related = null;
        uint bankTag = tag;
        if (entry.Reference == EventClass)
        {
            if (bytes.Length < 40 || U64(bytes, 0) != (ulong)bytes.Length)
                throw new InvalidDataException("Audio event header is invalid.");
            eventId = U32(bytes, 8); related = U32(bytes, 12); bankTag = U32(bytes, 20);
            long count = (long)U64(bytes, 24), relative = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(32));
            long header = checked(32 + relative);
            if (!(count == 0 && relative == 0) && (count is < 0 or > 100000 || header < 4 || header > bytes.Length - 16
                || U32(bytes, (int)header - 4) != 0x80809FBD || U64(bytes, (int)header) != (ulong)count
                || U32(bytes, (int)header + 8) != 0x80800014 || count > (bytes.Length - header - 16) / 4))
                throw new InvalidDataException("Audio event media array is invalid.");
            for (int i = 0; i < count; i++)
            {
                uint mediaTag = U32(bytes, checked((int)header + 16 + i * 4));
                if (!catalog.TryGetEntry(mediaTag, out var stream) || stream.Type != 26 || stream.SubType != 6)
                    throw new InvalidDataException($"Event media {mediaTag:X8} is not an installed audio stream.");
                media.Add(new(stream.Reference, mediaTag, names?.Invoke(mediaTag) ?? $"Media {stream.Reference}", stream.Size));
            }
        }
        if (!catalog.TryGetEntry(bankTag, out var bankEntry) || bankEntry.Type != 26 || bankEntry.SubType is not (4 or 5))
            throw new InvalidDataException("The event has no supported package bank link.");
        var bank = ReadBank(bankTag, catalog.Read(bankTag));
        if (bank.Id != bankEntry.Reference) throw new InvalidDataException("Package and Wwise bank identities differ.");
        if (eventId is uint selectedId && !bank.Objects.Any(value => value.Type == 4 && value.Id == selectedId))
            diagnostics.Add($"Event {selectedId} is not present in the linked bank.");
        media.AddRange(bank.EmbeddedMedia.Where(value => !media.Any(existing => existing.Id == value.Id)));
        if (entry.Reference != EventClass)
        {
            if (bank.Version == 113)
            {
                var sources = catalog.Entries.Where(value => value.Type == 26 && value.SubType == 6)
                    .GroupBy(value => value.Reference).ToDictionary(group => group.Key, group => group.ToArray());
                var declared = DeclaredBankMedia(bankTag, bankEntry.PackageId);
                var mediaIds = AudioSources.Read(bank).Select(value => (Id: value.MediaId, value.External)).Distinct();
                foreach (var source in mediaIds)
                {
                    uint id = source.Id;
                    if (media.Any(value => value.Id == id && (value.Tag.HasValue || !source.External))) continue;
                    var candidates = sources.GetValueOrDefault(id) ?? [];
                    if (declared.TryGetValue(id, out var linked)) candidates = candidates.Where(candidate => linked.Contains(candidate.TagHash)).ToArray();
                    if (candidates.Length == 1)
                    {
                        var stream = candidates[0];
                        media.RemoveAll(value => value.Id == id);
                        media.Add(new(id, stream.TagHash, names?.Invoke(stream.TagHash) ?? $"Media {id}", stream.Size));
                    }
                    else diagnostics.Add(candidates.Length == 0 ? $"Missing media {id}." : $"Media {id} has multiple package sources; open its event.");
                }
            }
            else diagnostics.Add($"External media lookup for Wwise bank version {bank.Version} is not supported.");
        }
        return new(tag, name, entry.Reference == EventClass ? "Event" : "Bank", eventId, related, bank,
            media.DistinctBy(value => (value.Id, value.Tag)).ToArray(), diagnostics);
    }

    /** Bytes of a media file, embedded or read from the package. */
    public byte[] ReadMedia(AudioMedia media) => media.Embedded ?? catalog.Read(media.Tag
        ?? throw new InvalidDataException("Media has no package source."));

    /** Reads every init bank, which carry shared buses and settings. */
    public IReadOnlyList<AudioBank> ReadInitBanks() => catalog.Entries.Where(entry => entry.Type == 26 && entry.SubType == 4)
        .Select(entry => ReadBank(entry.TagHash, catalog.Read(entry.TagHash))).ToArray();

    /** Only a typed event's explicit bank and media links disambiguate duplicate media IDs. */
    private Dictionary<uint, HashSet<uint>> DeclaredBankMedia(uint bankTag, ushort package)
    {
        var result = new Dictionary<uint, HashSet<uint>>();
        foreach (var entry in catalog.Entries.Where(entry => entry.PackageId == package
            && entry.Type is 8 or 16 && entry.Reference == EventClass))
        {
            byte[] bytes = catalog.Read(entry.TagHash);
            if (bytes.Length < 40 || U64(bytes, 0) != (ulong)bytes.Length || U32(bytes, 20) != bankTag) continue;
            long count = checked((long)U64(bytes, 24));
            long header = checked(32 + BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(32)));
            if (count == 0) continue;
            if (count is < 0 or > 100000 || header < 4 || header > bytes.Length - 16
                || U32(bytes, (int)header - 4) != 0x80809FBD || U64(bytes, (int)header) != (ulong)count
                || U32(bytes, (int)header + 8) != 0x80800014 || count > (bytes.Length - header - 16) / 4)
                throw new InvalidDataException("Linked event media array is invalid.");
            for (int i = 0; i < count; i++)
            {
                uint tag = U32(bytes, checked((int)header + 16 + i * 4));
                if (!catalog.TryGetEntry(tag, out var media) || media.Type != 26 || media.SubType != 6)
                    throw new InvalidDataException("Linked event media has the wrong package type.");
                if (!result.TryGetValue(media.Reference, out var tags)) result[media.Reference] = tags = [];
                tags.Add(tag);
            }
        }
        return result;
    }

    /** HIRC record lengths and DIDX slices must fit their own chunks, not just the bank file. */
    public static AudioBank ReadBank(uint tag, byte[] bytes)
    {
        var objects = new List<AudioBankObject>(); var media = new List<AudioMedia>();
        uint version = 0, id = 0; byte[]? index = null, data = null; bool headerSeen = false;
        var chunks = new HashSet<string>(StringComparer.Ordinal);
        for (int p = 0; p < bytes.Length;)
        {
            if (p > bytes.Length - 8) throw new InvalidDataException("Truncated Wwise chunk header.");
            uint length = U32(bytes, p + 4);
            if (length > bytes.Length - p - 8) throw new InvalidDataException("Wwise chunk leaves its bank.");
            int start = p + 8, end = checked(start + (int)length);
            string kind = Encoding.ASCII.GetString(bytes, p, 4);
            if (kind is "BKHD" or "DIDX" or "DATA" or "HIRC" && !chunks.Add(kind))
                throw new InvalidDataException("Duplicate Wwise data chunk.");
            if (kind == "BKHD")
            {
                if (headerSeen || length < 8) throw new InvalidDataException("Wwise bank header is invalid.");
                version = U32(bytes, start); id = U32(bytes, start + 4); headerSeen = true;
            }
            else if (kind == "DIDX") index = bytes.AsSpan(start, (int)length).ToArray();
            else if (kind == "DATA") data = bytes.AsSpan(start, (int)length).ToArray();
            else if (kind == "HIRC")
            {
                if (length < 4) throw new InvalidDataException("Truncated Wwise object table.");
                uint count = U32(bytes, start); int cursor = start + 4;
                if (count > 1000000) throw new InvalidDataException("Wwise object table is too large.");
                for (int i = 0; i < count; i++)
                {
                    if (cursor > end - 9) throw new InvalidDataException("Truncated Wwise object.");
                    uint size = U32(bytes, cursor + 1);
                    if (size < 4 || size > end - cursor - 5) throw new InvalidDataException("Wwise object leaves its table.");
                    objects.Add(new(bytes[cursor], U32(bytes, cursor + 5), bytes.AsSpan(cursor + 9, checked((int)size - 4)).ToArray()));
                    cursor += checked((int)size + 5);
                }
                if (cursor != end) throw new InvalidDataException("Wwise object count differs from its table.");
            }
            p = end;
        }
        if (!headerSeen) throw new InvalidDataException("Audio entry is not a Wwise bank.");
        if (index is not null)
        {
            if (data is null || index.Length % 12 != 0) throw new InvalidDataException("Wwise embedded media index is invalid.");
            for (int p = 0; p < index.Length; p += 12)
            {
                uint mediaId = U32(index, p), offset = U32(index, p + 4), size = U32(index, p + 8);
                if (offset > data.Length || size > data.Length - offset) throw new InvalidDataException("Embedded media leaves DATA.");
                media.Add(new(mediaId, null, $"Media {mediaId}", (int)size, data.AsSpan((int)offset, (int)size).ToArray()));
            }
        }
        return new(tag, id, version, bytes, objects, media);
    }

    private static uint U32(byte[] bytes, int p) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p, 4));
    private static ulong U64(byte[] bytes, int p) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(p, 8));
}
