using System.Security.Cryptography;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor.Creation;

/** The edits (and the source hashes they were made from) that repair an exotic weapon's perks. */
public sealed record PreparedExoticPerkRepair(IReadOnlyDictionary<uint, byte[]> Changes,
    IReadOnlyDictionary<uint, string> SourceHashes);

/** ExoticWeaponAuthoring part: repairs the private sockets and displays of existing exotic perks. */
public static partial class ExoticWeaponAuthoring
{
    // TODO: call from a Repair action in the editor; nothing in Studio calls this yet.
    /** Existing authored effects keep their programs while their private sockets and displays are repaired. */
    public static PreparedExoticPerkRepair PreparePerkRepair(StudioProject project, PackageCatalog catalog,
        uint weaponTag, uint intrinsicPlugTag, uint traitPlugTag, uint catalystPlugTag)
    {
        uint[] tags = [weaponTag, intrinsicPlugTag, traitPlugTag, catalystPlugTag];
        if (tags.Distinct().Count() != tags.Length)
            throw new InvalidDataException("The weapon and its three authored plugs need distinct identities.");
        var investment = InvestmentCatalog.Load(catalog);
        var changes = new Dictionary<uint, byte[]>();
        var hashes = new Dictionary<uint, string>();
        byte[] Read(uint tag)
        {
            byte[] bytes = catalog.Read(tag);
            hashes[tag] = Convert.ToHexString(SHA256.HashData(bytes));
            return bytes;
        }
        void Set(uint tag, byte[] bytes)
        {
            byte[] current = Read(tag);
            if (!current.AsSpan().SequenceEqual(bytes)) changes[tag] = bytes;
        }
        void RequireOwn(uint tag)
        {
            if (!project.AddedAssets.Any(asset => asset.Native is { } native && native.Tag() == tag)
                || !catalog.TryGetEntry(tag, out var entry) || catalog.ContainsInstalledPackage(entry.PackageId))
                throw new InvalidDataException("Perk repair needs project-owned items and behavior programs.");
        }
        foreach (uint tag in tags) RequireOwn(tag);
        var weapon = investment.FindItem(weaponTag)
            ?? throw new InvalidDataException("The authored weapon is absent from the item index.");
        if (weapon.Category != InvestmentCategory.Weapons)
            throw new InvalidDataException("Choose a weapon for perk repair.");
        byte[] definition = Read(weaponTag);
        var original = investment.ReadItem(weaponTag, definition);
        if (Malformed(original))
            throw new InvalidDataException("The weapon has malformed investment data.");
        while (investment.ReadItem(weaponTag, definition).Perks.LastOrDefault() is { } inherited)
            definition = investment.RemovePerk(weaponTag, definition, inherited);
        uint tableTag = InvestmentSandboxPerkAuthoring.DisplayTableTag;
        byte[] displays = Read(tableTag);
        var perkIndices = new HashSet<ushort>();
        int role = 0;
        foreach (uint tag in tags.Skip(1))
        {
            var plug = investment.FindItem(tag)
                ?? throw new InvalidDataException("An authored plug is absent from the item index.");
            byte[] plugBytes = Read(tag);
            var detail = investment.ReadItem(tag, plugBytes);
            if (Malformed(detail) || detail.Perks.Count != 1
                || !perkIndices.Add(detail.Perks[0].Index))
                throw new InvalidDataException("Each authored plug needs one distinct sandbox perk.");
            var behavior = InvestmentSandboxPerkAuthoring.Read(catalog, detail.Perks[0].Index);
            RequireOwn(behavior.GameplayTag);
            Read(behavior.GameplayTag);
            var socket = investment.ReadItem(weaponTag, definition).Sockets
                .SingleOrDefault(socket => socket.InitialPlug == plug.Index)
                ?? throw new InvalidDataException("The authored plug must be the default of one weapon socket.");
            definition = investment.KeepSocketPlug(weaponTag, definition, socket, checked((ushort)plug.Index));
            displays = InvestmentSandboxPerkAuthoring.SetPresentation(displays, behavior.Index,
                behavior.DefinitionHash, role++ == 0 ? SandboxPerkPresentation.Exotic : SandboxPerkPresentation.Normal);
            RequireOwn(plug.DisplayTag);
            Set(plug.DisplayTag, InvestmentPerkDisplayEdits.Reset(Read(plug.DisplayTag), 1));
        }
        definition = KeepDefaultOrnaments(investment, weaponTag, definition);
        RequireOwn(weapon.DisplayTag);
        Set(weapon.DisplayTag, InvestmentPerkDisplayEdits.Reset(Read(weapon.DisplayTag), 0));
        Set(weaponTag, definition);
        Set(tableTag, displays);
        Read(InvestmentCatalog.ItemIndexTag);
        Read(InvestmentCatalog.DisplayIndexTag);
        Read(InvestmentSandboxPerkAuthoring.DefinitionTableTag);
        Read(InvestmentSandboxPerkAuthoring.RuntimeNameMapTag);
        return new(changes, hashes);
    }

    /** Source ornaments carry the source weapon's art, so each ornament socket keeps only its default plug. */
    internal static byte[] KeepDefaultOrnaments(InvestmentCatalog investment, uint weaponTag, byte[] definition)
    {
        foreach (var socket in investment.ReadItem(weaponTag, definition).Sockets)
        {
            bool ornaments = socket.AllowedPlugs.Any(plug => investment.Items[plug].TypeName == OrnamentTypeName);
            if (!ornaments || socket.AllowedPlugs.Count == 1) continue;
            if (socket.InitialPlug == ushort.MaxValue || !socket.AllowedPlugs.Contains(socket.InitialPlug)
                || investment.Items[socket.InitialPlug].TypeName == OrnamentTypeName)
                throw new InvalidDataException("The ornament socket has no default plug to keep.");
            definition = investment.KeepSocketPlug(weaponTag, definition, socket, socket.InitialPlug);
        }
        return definition;
    }

    // Ornament plugs carry this item type name; the default ornament has none.
    private const string OrnamentTypeName = "Weapon Ornament";

    private static bool Malformed(InvestmentItemDetail item) => item.Diagnostics.Any(message =>
        message.StartsWith("Stats:", StringComparison.Ordinal) || message.StartsWith("Socket", StringComparison.Ordinal));

    /** Publication must use the same item, display and behavior inputs as the repair plan. */
    public static void ValidatePerkRepair(PackageCatalog catalog, PreparedExoticPerkRepair repair)
    {
        foreach (var (tag, hash) in repair.SourceHashes)
            if (Convert.ToHexString(SHA256.HashData(catalog.Read(tag))) != hash)
                throw new InvalidDataException("The authored weapon changed while its perk repair was prepared.");
    }
}
