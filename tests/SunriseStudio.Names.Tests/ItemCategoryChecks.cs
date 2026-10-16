using System.Buffers.Binary;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

/** Checks item kind and category names and that installed items map to a listed category. */
internal static class ItemCategoryChecks
{
    /** The item-kind rule maps a facts table to its expected categories and types. */
    internal static void Synthetic()
    {
        static InvestmentItemFacts Item(byte bucket, string line = "", bool objectives = false, bool plug = false) =>
            new(0, 1, 2, bucket, 5, line, objectives, plug, false);
        (InvestmentItemFacts Facts, string Category, string Type)[] cases =
        [
            (Item(0, "Auto Rifle"), "Weapons", "Auto Rifle"), (Item(2), "Weapons", "Power"),
            (Item(3, "Helmet"), "Armor", "Helmet"), (Item(7, "Hunter Cloak"), "Armor", "Class armor"),
            (Item(8, "Ghost Shell"), "Ghosts", "Ghost Shell"), (Item(8, "Quest Step"), "Quests", "Quest Step"),
            (Item(9, "Vehicle"), "Vehicles", "Vehicle"), (Item(10, "Ship"), "Ships", "Ship"),
            (Item(14, "Shader"), "Shaders", "Shader"), (Item(16, "Subclass"), "Subclasses", "Subclass"),
            (Item(17), "Clan banners", ""), (Item(27, "Emblem"), "Emblems", "Emblem"),
            (Item(12), "Emotes", ""), (Item(41, "Emote"), "Emotes", "Emote"), (Item(47), "Finishers", ""),
            (Item(49), "Artifacts", ""), (Item(22, "Material"), "Currencies", "Material"),
            (Item(50, "Currency"), "Currencies", "Currency"), (Item(31, "Rare Engram"), "Engrams", "Rare Engram"),
            (Item(60, "Engram"), "Engrams", "Engram"), (Item(40, "Bounty"), "Bounties", "Bounty"),
            (Item(40, "Quest Step"), "Quests", "Quest Step"), (Item(50, "Gear", objectives: true), "Quests", "Gear"),
            (Item(33), "Quests", ""), (Item(50, "Quest"), "Quests", "Quest"),
            (Item(50, "Weapon Mod", plug: true), "Mods and plugs", "Weapon Mod"),
            (Item(50, "Treasure Map"), "Consumables", "Treasure Map"), (Item(50, "Seasonal Bonus"), "Consumables", "Seasonal Bonus"),
            (Item(50, "Material", plug: true), "Consumables", "Material"), (Item(40, "Container"), "Consumables", "Container"),
            (Item(50, "Trinket"), "Other items", "Trinket"), (Item(255), "Other items", "Bucket 255")
        ];
        foreach (var (facts, category, type) in cases)
        {
            var actual = InvestmentItemKinds.Classify(facts);
            Check(actual == (category, type),
                $"Item kind for bucket {facts.Bucket} '{facts.TypeLine}' is {actual.Category} / {actual.Type}, expected {category} / {type}.");
            Check(InvestmentItemKinds.Categories.Contains(actual.Category), $"Category {actual.Category} is not listed.");
        }
        Check(InvestmentItemKinds.TierName(5) == "Exotic" && InvestmentItemKinds.TierName(255) == "Unset"
            && InvestmentItemKinds.TierName(6) == "Tier 6", "Tier names changed.");
        Console.WriteLine($"pass item_kinds={cases.Length}");
    }

    /** Every 0x80807BEA in the installed item index has facts; prints the count of each category. */
    internal static void Owned(PackageCatalog catalog, StudioNameIndex names)
    {
        byte[] index = catalog.Read(0x81327CCB);
        long count = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(8));
        int rows = checked((int)(16 + BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(16)))) + 16;
        Dictionary<string, int> categories = [], bountyLines = [];
        int definitions = 0, missing = 0;
        for (int i = 0; i < count; i++)
        {
            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(rows + i * 24 + 16));
            if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != 0x80807BEA) continue;
            definitions++;
            if (!names.TryGetItem(tag, out var facts)) { missing++; continue; }
            var (category, type) = InvestmentItemKinds.Classify(facts);
            categories[category] = categories.GetValueOrDefault(category) + 1;
            if (category == InvestmentItemKinds.Bounties) bountyLines[type] = bountyLines.GetValueOrDefault(type) + 1;
        }
        Console.WriteLine($"item_facts index_rows={count} definitions={definitions} missing={missing} "
            + string.Join(' ', InvestmentItemKinds.Categories.Select(name => $"{name.Replace(' ', '_')}={categories.GetValueOrDefault(name)}")));
        Console.WriteLine("bounty_lines " + string.Join("; ", bountyLines.OrderByDescending(pair => pair.Value)
            .Select(pair => $"{(pair.Key.Length == 0 ? "(none)" : pair.Key)}={pair.Value}")));
        Check(definitions > 0 && missing == 0, $"{missing} item definitions have no facts.");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
