using System.Buffers.Binary;
using System.Security.Cryptography;
using SunriseStudio.Animation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

/** Compiled presentation assets and clips for a weapon. */
public sealed record PreparedWeaponPresentation(IReadOnlyList<ProjectAssetAddition> Assets,
    uint GameplayEntityTag, uint PresentationEntityTag, uint AnimationDefinitionTag,
    IReadOnlyDictionary<uint, string> SourceHashes)
{
    public IReadOnlyDictionary<string, int> AddedClipIndices { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, uint> AddedNameHashes { get; init; } = new Dictionary<string, uint>();
}

/** One presentation clip: name, tag and frame count. */
public sealed record WeaponPresentationClip(string Name, uint ClipTag, int FrameCount);

/** Weapon animation edits own their presentation branch and leave installed weapon graphs unchanged. */
public static class WeaponPresentationAuthoring
{
    public const uint GameplayTemplate = 0x81A6B325, PresentationTemplate = 0x80BC1002;
    public const uint DefinitionTemplate = 0x80BC11AF;
    public const uint QueryTemplate = 0x80BC11B0;
    public const int ReloadSlot = 113, FireSlot = 114;
    private const uint WeaponConfig = 0x8152C7C4, SelectorConfig = 0x8152C7C9;
    private const uint AnimationConfig = 0x8152C7CD, StateConfig = 0x8161FA61;
    private sealed record Profile(uint Tag, uint Class, string Hash, int Pairs = 0);
    private static readonly Profile[] Profiles =
    [
        new(GameplayTemplate, 0x80809C0F, "E1D2D5F8CFFB29AF8F3C5824F7824B580ED135AB09262D79D12D10B461EAF455"),
        new(WeaponConfig, 0x80809C36, "0318F1E3DEEE0CDF12D2967202A50E7CBA398C139BEB7322F4E32B016A1D4E22", 13),
        new(PresentationTemplate, 0x80809C0F, "1D3145D3D34DDE9D3BCDEB7F4A6D1118EDA6DF386A263DC9FC273640E2226439"),
        new(SelectorConfig, 0x80809C36, "4883C50B1E8E8BD1E236F9913630AACC8FCABA0CDE12F1EC552CA0654692A1A5", 1),
        new(AnimationConfig, 0x80809C36, "046FB98B9A9A45C03AE0B2CEFEF764FB9151F05D49D69A5D458AED0EA59702D5", 21),
        new(StateConfig, 0x80809C36, "BA253B06617AC0E020916BF06BCAFDA6FE21B85FDA310DF9AB27396CBB6C4AAD", 58),
        new(DefinitionTemplate, 0x808036F6, "0B4C828F69FE6C98FF87F8A66E5C5ECF61636022568C43A6092E6CA8E5660C0A")
    ];

    /** New clip tags must use the source branch's 75-control group layout. */
    public static PreparedWeaponPresentation Prepare(PackageCatalog catalog, ushort packageId,
        int firstEntry, string name, uint newReloadClipTag, uint newFireClipTag,
        IReadOnlyList<WeaponPresentationClip>? additionalClips = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        additionalClips ??= [];
        if (additionalClips.Count > 64 || additionalClips.Select(clip => clip.Name).Distinct(StringComparer.Ordinal).Count() != additionalClips.Count)
            throw new InvalidDataException("Additional weapon clips need unique names and at most 64 entries.");
        var profiles = Profiles.ToList();
        if (additionalClips.Count != 0) profiles.Add(new(QueryTemplate, 0x80803465,
            "4CFB65B2F602BD76BAD818BA3D84329655F0EB52C6A0EDE364B6108C520DFFA4"));
        if (!PackageWriter.IsNewPackageId(packageId) || catalog.ContainsInstalledPackage(packageId)
            || firstEntry < 0 || firstEntry > 8192 - profiles.Count)
            throw new InvalidDataException("Weapon presentation needs an unused package entry for each cloned asset.");
        uint Tag(int index) => checked(0x80800000u + ((uint)packageId << 13) + (uint)(firstEntry + index));
        var tags = profiles.Select((profile, index) => (profile.Tag, NewTag: Tag(index)))
            .ToDictionary(pair => pair.Tag, pair => pair.NewTag);
        if (tags.Values.Any(catalog.ContainsTag) || tags.Values.Contains(newReloadClipTag)
            || tags.Values.Contains(newFireClipTag) || newReloadClipTag == newFireClipTag)
            throw new InvalidDataException("Weapon presentation destinations overlap another asset.");
        foreach (uint clip in new[] { newReloadClipTag, newFireClipTag }.Concat(additionalClips.Select(clip => clip.ClipTag)))
        {
            if (clip < 0x80800000 || clip == uint.MaxValue || tags.Values.Contains(clip))
                throw new InvalidDataException("Weapon animation needs a native clip tag.");
            if (catalog.ContainsTag(clip)) ValidateClip(catalog, clip);
        }
        long revision = catalog.OverlayRevision;
        var bytes = new Dictionary<uint, byte[]>();
        var hashes = new Dictionary<uint, string>();
        foreach (var profile in profiles)
        {
            if (!catalog.TryGetEntry(profile.Tag, out var entry) || entry.Reference != profile.Class)
                throw new NotSupportedException("The checked weapon presentation profile is missing.");
            byte[] source = catalog.Read(profile.Tag);
            string hash = Convert.ToHexString(SHA256.HashData(source));
            if (hash != profile.Hash)
                throw new NotSupportedException("Weapon presentation needs the unchanged checked source profile.");
            bytes[profile.Tag] = (byte[])source.Clone(); hashes[profile.Tag] = hash;
        }
        ValidateClip(catalog, 0x81A6B4F0); ValidateClip(catalog, 0x81A6B4F1);
        foreach (var profile in Profiles.Where(profile => profile.Class == 0x80809C36))
            RetagConfig(bytes[profile.Tag], profile.Tag, tags[profile.Tag], profile.Pairs);
        bytes[GameplayTemplate] = WeaponConfigEdits.RetagRoot(bytes[GameplayTemplate], WeaponConfig, tags[WeaponConfig]);
        foreach (uint config in new[] { SelectorConfig, AnimationConfig, StateConfig })
            bytes[PresentationTemplate] = WeaponConfigEdits.RetagRoot(bytes[PresentationTemplate], config, tags[config]);
        byte[] weapon = bytes[WeaponConfig];
        int resource = Pointer(weapon, 24);
        if (U32(weapon, resource - 4) != 0x80804221)
            throw new InvalidDataException("The weapon presentation resource changed class.");
        int[] variants = NativeRows.Rows(weapon, resource + 1416, 0x80803E72, 232);
        int[] selected = variants.Where(row => U32(weapon, row) == 0x44A0B334).ToArray();
        if (selected.Length != 1)
            throw new InvalidDataException("The weapon presentation selector is missing or ambiguous.");
        Replace(weapon, resource + 1184 + 120, PresentationTemplate, tags[PresentationTemplate]);
        Replace(weapon, selected[0] + 120, PresentationTemplate, tags[PresentationTemplate]);
        Replace(bytes[SelectorConfig], 0x818, DefinitionTemplate, tags[DefinitionTemplate]);
        Replace(bytes[AnimationConfig], 0x150, DefinitionTemplate, tags[DefinitionTemplate]);
        Replace(bytes[StateConfig], 0x3640, DefinitionTemplate, tags[DefinitionTemplate]);
        byte[] definition = bytes[DefinitionTemplate];
        int[] clips = NativeRows.Rows(definition, 8, 0x80808F48, 4);
        if (clips.Length != 116) throw new InvalidDataException("The weapon clip list changed.");
        Replace(definition, clips[ReloadSlot], 0x81A6B4F0, newReloadClipTag);
        Replace(definition, clips[FireSlot], 0x81A6B4F1, newFireClipTag);
        var addedIndices = new Dictionary<string, int>();
        var addedHashes = new Dictionary<string, uint>();
        foreach (var clip in additionalClips)
        {
            if (string.IsNullOrWhiteSpace(clip.Name) || clip.FrameCount is < 1 or > short.MaxValue)
                throw new InvalidDataException("A named weapon clip needs a name and a valid native frame count.");
            uint hash = InvestmentItemClone.Fnv1(clip.Name);
            int index = NativeRows.Rows(bytes[DefinitionTemplate], 8, 0x80808F48, 4).Length;
            int nameIndex = NativeRows.Rows(bytes[DefinitionTemplate], 104, 0x80809002, 32).Length;
            bytes[DefinitionTemplate] = AppendClip(bytes[DefinitionTemplate], clip, hash);
            bytes[QueryTemplate] = AppendQuery(bytes[QueryTemplate], hash, nameIndex);
            addedIndices.Add(clip.Name, index); addedHashes.Add(clip.Name, hash);
        }
        if (additionalClips.Count != 0)
            Replace(bytes[SelectorConfig], 0x824, QueryTemplate, tags[QueryTemplate]);
        var assets = profiles.Select((profile, index) =>
        {
            catalog.TryGetEntry(profile.Tag, out var entry);
            string suffix = profile.Tag == GameplayTemplate ? "gameplay" : profile.Tag == PresentationTemplate
                ? "presentation" : profile.Tag == DefinitionTemplate ? "animations" : $"component-{profile.Tag:X8}";
            return new ProjectAssetAddition(name + "." + suffix, bytes[profile.Tag],
                new(packageId, entry.Reference, entry.TypeInfo,
                    profile.Tag == GameplayTemplate ? name : null, firstEntry + index), profile.Tag);
        }).ToArray();
        if (catalog.OverlayRevision != revision)
            throw new InvalidDataException("Weapon presentation sources changed during preparation.");
        return new(assets, tags[GameplayTemplate], tags[PresentationTemplate], tags[DefinitionTemplate], hashes)
        { AddedClipIndices = addedIndices, AddedNameHashes = addedHashes };
    }

    /** A clip, its parallel entry and its named-state row are appended as one native definition. */
    private static byte[] AppendClip(byte[] definition, WeaponPresentationClip clip, uint nameHash)
    {
        int[] clips = NativeRows.Rows(definition, 8, 0x80808F48, 4);
        int[] mirrors = NativeRows.Rows(definition, 24, 0x80800007, 4);
        int[] names = NativeRows.Rows(definition, 104, 0x80809002, 32);
        if (clips.Length != mirrors.Length || clips.Length >= short.MaxValue
            || names.Any(row => U32(definition, row + 16) == nameHash))
            throw new InvalidDataException("The named animation is duplicate or its clip arrays disagree.");
        foreach (int row in names)
            if (I64(definition, row) != 0 || I64(definition, row + 8) != 0)
                throw new NotSupportedException("Animation name rows with undecoded links cannot grow.");
        byte[] tag = new byte[4]; Put32(tag, 0, clip.ClipTag);
        byte[] output = InvestmentItemClone.AppendRow(definition, 8, 4, tag);
        Put32(tag, 0, uint.MaxValue);
        output = InvestmentItemClone.AppendRow(output, 24, 4, tag);
        byte[] label = new byte[32]; Put32(label, 16, nameHash);
        BinaryPrimitives.WriteSingleLittleEndian(label.AsSpan(20), (clip.FrameCount - 1) / 30f);
        BinaryPrimitives.WriteInt16LittleEndian(label.AsSpan(24), checked((short)clips.Length));
        label[26] = 1;
        return InvestmentItemClone.AppendRow(output, 104, 32, label);
    }

    /** New names resolve directly to their definition row; existing query branches keep their targets. */
    private static byte[] AppendQuery(byte[] query, uint nameHash, int definitionRow)
    {
        if (nameHash is 0 or uint.MaxValue or InvestmentItemClone.EmptyStringHash)
            throw new InvalidDataException("Animation name hashes cannot be empty.");
        int[] map = NativeRows.Rows(query, 8, 0x8080342E, 8);
        int[] selectors = NativeRows.Rows(query, 24, 0x8080342F, 16);
        for (int i = 0; i < map.Length; i++)
            if (U32(query, map[i]) == nameHash || i > 0 && U32(query, map[i - 1]) >= U32(query, map[i]))
                throw new InvalidDataException("Animation query names must be unique and sorted.");
        int payload = checked((query.Length + 19) & ~15);
        byte[] output = new byte[checked(payload + 48)]; query.CopyTo(output, 0);
        Put32(output, payload - 4, 0x80803434);
        Put64(output, payload, 1); Put64(output, payload + 8, 16);
        Put32(output, payload + 20, 0x80809FBD); Put64(output, payload + 24, 1);
        Put32(output, payload + 32, 0x80803439); Put32(output, payload + 40, checked((uint)definitionRow));
        BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(payload + 44), 1);
        Put64(output, 0, output.Length);
        byte[] selector = new byte[16]; selector[0] = 1; Put64(selector, 8, payload - 8);
        output = InvestmentItemClone.AppendRow(output, 24, 16, selector, [0], 0);
        byte[] key = new byte[8]; Put32(key, 0, nameHash); Put32(key, 4, checked((uint)selectors.Length));
        output = InvestmentItemClone.AppendRow(output, 8, 8, key);
        int[] moved = NativeRows.Rows(output, 8, 0x8080342E, 8);
        var sorted = moved.Select(row => (Name: U32(output, row), Index: U32(output, row + 4)))
            .OrderBy(row => row.Name).ToArray();
        for (int i = 0; i < moved.Length; i++)
        { Put32(output, moved[i], sorted[i].Name); Put32(output, moved[i] + 4, sorted[i].Index); }
        return output;
    }

    /** Clip controls retain the two player groups before the five weapon controls. */
    public static void ValidateClip(PackageCatalog catalog, uint tag)
    {
        var clip = new AnimationControlReader(catalog).Read(tag);
        var expected = new[] { new AnimationControlGroup(0xD59A5FE6, 8),
            new AnimationControlGroup(0x7CB60FEC, 62), new AnimationControlGroup(0xC42D6707, 5) };
        if (clip.ControlCount != 75 || !clip.ControlGroups.SequenceEqual(expected))
            throw new InvalidDataException("Weapon presentation clips need the checked 70 player and five weapon controls.");
    }

    /** Rebinding preserves each query name and updates the durations of rows that use this clip slot. */
    public static byte[] BindClip(PackageCatalog catalog, ReadOnlyMemory<byte> current,
        int slot, uint clipTag)
    {
        ValidateClip(catalog, clipTag);
        var clip = new AnimationControlReader(catalog).Read(clipTag);
        byte[] output = current.ToArray();
        int[] clips = NativeRows.Rows(output, 8, 0x80808F48, 4);
        int[] names = NativeRows.Rows(output, 104, 0x80809002, 32);
        if (slot < 0 || slot >= clips.Length)
            throw new InvalidDataException("The animation slot is outside this definition.");
        Put32(output, clips[slot], clipTag);
        foreach (int row in names)
            if (BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(row + 24)) == slot)
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(row + 20), clip.DurationSeconds);
        return output;
    }

    /** Every changed owner is a reciprocal typed pair or a checked bound-method endpoint. */
    private static void RetagConfig(byte[] bytes, uint source, uint destination, int pairCount)
    {
        var owners = new HashSet<int>();
        int pairs = 0;
        for (int at = 0; at + 16 <= bytes.Length; at += 4)
        {
            if (U32(bytes, at) != source) continue;
            long peerLong = I64(bytes, at + 8);
            uint peerClass = U32(bytes, at + 4);
            if (peerLong < 0 || peerLong > bytes.Length - 16 || peerClass < 0x80800000 || peerClass >= 0x84000000) continue;
            int peer = checked((int)peerLong);
            if (U32(bytes, peer) != source || I64(bytes, peer + 8) != at) continue;
            owners.Add(at); owners.Add(peer);
            if (at < peer) pairs++;
        }
        if (pairs != pairCount) throw new InvalidDataException("A presentation owner pair changed.");
        if (source == WeaponConfig)
            foreach (var (at, slot) in new[] { (0xB0, 8u), (0xE0, 9u) })
            {
                if (U32(bytes, at) != 0x80803E70 || U32(bytes, at + 4) != slot
                    || U32(bytes, at + 8) != source || U32(bytes, at + 12) != 0x80803E70
                    || I64(bytes, at + 16) != 0x80)
                    throw new InvalidDataException("A weapon bound-method owner changed.");
                owners.Add(at + 8);
            }
        for (int at = 0; at + 4 <= bytes.Length; at += 4)
            if (U32(bytes, at) == source && !owners.Contains(at))
                throw new NotSupportedException("An unclassified presentation owner cannot be cloned.");
        foreach (int at in owners) Put32(bytes, at, destination);
    }

    private static void Replace(byte[] bytes, int at, uint expected, uint replacement)
    {
        if (U32(bytes, at) != expected) throw new InvalidDataException("A presentation reference changed.");
        Put32(bytes, at, replacement);
    }

    private static int Pointer(byte[] bytes, int field) => checked(field + (int)I64(bytes, field));
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static long I64(byte[] bytes, int at) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));
    private static void Put32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void Put64(byte[] bytes, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
}
