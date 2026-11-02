using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using static SunriseStudio.Creation.Tests.Fixture;

namespace SunriseStudio.Creation.Tests;

/** apply_item_edit set_text (StudioWorkspace.EditItemText) and set_tag_value (SetTagValue) on a synthetic install. */
internal static class ItemTextChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string parent)
    {
        const uint first = 0x80A00000;
        const uint weapon = first + 2, plug = first + 3, weaponDisplay = first + 4, plugDisplay = first + 5, bankTable = first + 6;
        string install = Path.Combine(parent, "item-text-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        byte[] Index(uint tag)
        {
            byte[] bytes = new byte[48 + 24 * 2]; Q(bytes, 0, bytes.Length);
            Fixture.Array(bytes, 8, 32, 2, 0x80807BE8);
            for (int i = 0; i < 2; i++) { U(bytes, 48 + 24 * i, 0x12345678u + (uint)i); U(bytes, 64 + 24 * i, tag + (uint)i); }
            return bytes;
        }
        byte[] Item(uint bucket) { byte[] bytes = new byte[256]; Q(bytes, 0, bytes.Length); bytes[184] = (byte)bucket; bytes[186] = 4; return bytes; }
        byte[] Display(uint name, uint flavor)
        {
            byte[] bytes = new byte[160]; Q(bytes, 0, bytes.Length); U(bytes, 136, name); U(bytes, 156, flavor); return bytes;
        }
        byte[] weaponBytes = Display(0x10, 0x20), plugBytes = Display(0x11, 0x21);
        byte[] banks = new byte[56]; Q(banks, 0, banks.Length);
        Fixture.Array(banks, 8, 32, 1, 0x80805F98); U(banks, 48, 1); U(banks, 52, first + 7);
        var (container, data) = Bank(first + 8, [(0x10, "Example weapon"), (0x20, "Weapon flavor"), (0x11, "Example plug"), (0x21, "Plug flavor")]);
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_item_text_0100_0.pkg"), 0x100,
            [new(0x80807BE8, 8 << 9, Index(weapon)), new(0x80805CE0, 8 << 9, Index(weaponDisplay)),
             new(InvestmentCatalog.ItemClass, 8 << 9, Item(0)), new(InvestmentCatalog.ItemClass, 8 << 9, Item(8)),
             new(InvestmentCatalog.DisplayClass, 8 << 9, weaponBytes), new(InvestmentCatalog.DisplayClass, 8 << 9, plugBytes),
             new(0x80805F98, 8 << 9, banks), new(StringBankWriter.ContainerClass, 8 << 9, container),
             new(StringBankWriter.DataClass, 8 << 9, data)]);
        var sources = new InvestmentSources(Items: first, Displays: first + 1, StringBanks: bankTable);
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install); Wait(workspace);
        workspace.CreateProject(Path.Combine(parent, "item-text-project"), "Item text"); Wait(workspace);
        ushort package = 0;
        void Inject()
        {
            var investment = InvestmentCatalog.Load(workspace.Catalog!, sources: sources);
            foreach (var (name, value) in new (string, object)[] { ("investment", investment),
                ("investmentSource", workspace.Catalog!), ("investmentOverlayRevision", workspace.Catalog!.OverlayRevision) })
                typeof(StudioWorkspace).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(workspace, value);
        }
        void EditText(uint item, string field, string text) { Inject(); workspace.EditItemText(item, field, text, package, sources); Wait(workspace); }
        Inject();
        Check(workspace.ItemDisplayTag(weapon) == weaponDisplay && workspace.ItemDisplayTag(plug) == plugDisplay,
            "The fixture items did not resolve their display records.");
        package = workspace.CustomPerkPackage() ?? throw new InvalidDataException("No package for the fixture.");
        var project = workspace.Project!;
        byte[] manifestBefore = File.ReadAllBytes(project.ManifestPath);

        // Item display: name changes, the other strings stay, the bank is a project addition, and one undo restores everything.
        string Text(uint display, int offset) => $"{InvestmentItemClone.ReadDisplayString(workspace.Catalog!.Read(display), offset).Hash:X8}";
        EditText(weapon, "name", "Sun Cutter");
        Check(Text(weaponDisplay, InvestmentItemClone.DisplayName) == $"{StringBankWriter.Hash("Sun Cutter"):X8}"
            && Text(weaponDisplay, InvestmentItemClone.DisplayFlavor) == "00000020" && workspace.Catalog!.Read(plugDisplay).AsSpan().SequenceEqual(plugBytes),
            "set_text name did not change only the item's name reference.");
        Check(project.AddedAssets.Count >= 2 && project.Changes.Any(change => change.TagHash == weaponDisplay),
            "set_text did not add the string bank or edit the display.");
        workspace.Undo(); Wait(workspace);
        Check(workspace.Catalog!.Read(weaponDisplay).AsSpan().SequenceEqual(weaponBytes) && project.AddedAssets.Count == 0
            && workspace.Catalog.Read(bankTable).AsSpan().SequenceEqual(banks) && project.Changes.Count == 0,
            "One undo did not restore the item display, the bank table and the added bank.");

        // Plug display: description and type, then undo restores the original bytes.
        EditText(plug, "description", "A plug that sings");
        Check(Text(plugDisplay, InvestmentItemClone.DisplayFlavor) == $"{StringBankWriter.Hash("A plug that sings"):X8}"
            && Text(plugDisplay, InvestmentItemClone.DisplayName) == "00000011", "set_text description did not change a plug's display.");
        EditText(plug, "type", "Barrel");
        Check(Text(plugDisplay, InvestmentItemClone.DisplayType) == $"{StringBankWriter.Hash("Barrel"):X8}",
            "set_text type did not change the plug's type reference.");
        workspace.Undo(); Wait(workspace);
        Check(Text(plugDisplay, InvestmentItemClone.DisplayType) != $"{StringBankWriter.Hash("Barrel"):X8}"
            && Text(plugDisplay, InvestmentItemClone.DisplayFlavor) == $"{StringBankWriter.Hash("A plug that sings"):X8}",
            "Undo of the second set_text touched the first.");
        workspace.Undo(); Wait(workspace);
        Check(workspace.Catalog!.Read(plugDisplay).AsSpan().SequenceEqual(plugBytes) && project.AddedAssets.Count == 0
            && File.ReadAllBytes(project.ManifestPath).AsSpan().SequenceEqual(manifestBefore) == (project.Changes.Count == 0),
            "Undo did not restore the plug display bytes.");
        foreach (var bad in new[] { ("bogus", "x"), ("name", "  "), ("name", new string('x', 201)) })
        {
            bool refused = false;
            try { Inject(); workspace.EditItemText(weapon, bad.Item1, bad.Item2, package, sources); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException) { refused = true; }
            Check(refused, $"set_text accepted field '{bad.Item1}' with an invalid value.");
        }
        Console.WriteLine("creation item_text set_text=name,description,type item=pass plug=pass undo=one-step refused=bad-input");

        // set_tag_value on an installed tag: range checks, little-endian write, old/new values, undo.
        workspace.Select(weaponDisplay); Wait(workspace);
        Reject<InvalidDataException>(() => workspace.SetTagValue(weaponDisplay, 4, "u32", "1"), "set_tag_value wrote into the size header.");
        Reject<InvalidDataException>(() => workspace.SetTagValue(weaponDisplay, 158, "u32", "1"), "set_tag_value wrote past the end.");
        Reject<InvalidDataException>(() => workspace.SetTagValue(weaponDisplay, 160, "u8", "1"), "set_tag_value wrote at the end.");
        Reject<InvalidDataException>(() => workspace.SetTagValue(weaponDisplay, 40, "u8", "300"), "set_tag_value accepted 300 as u8.");
        Reject<ArgumentException>(() => workspace.SetTagValue(weaponDisplay, 40, "u64", "1"), "set_tag_value accepted u64.");
        Check(workspace.Catalog.Read(weaponDisplay).AsSpan().SequenceEqual(weaponBytes) && project.Changes.Count == 0,
            "A refused set_tag_value changed the project.");
        var (old, written) = workspace.SetTagValue(weaponDisplay, 40, "u32", "0xDEADBEEF"); Wait(workspace);
        byte[] edited = workspace.Catalog.Read(weaponDisplay);
        Check(old == "0" && written == "3735928559" && edited[40] == 0xEF && edited[43] == 0xDE && edited.Length == 160
            && edited.AsSpan(0, 40).SequenceEqual(weaponBytes.AsSpan(0, 40)) && edited.AsSpan(44).SequenceEqual(weaponBytes.AsSpan(44)),
            "set_tag_value did not write the little-endian u32 and nothing else.");
        var (floatOld, floatNew) = workspace.SetTagValue(weaponDisplay, 48, "f32", "1.5"); Wait(workspace);
        Check(floatOld == "0" && floatNew == "1.5" && BitConverter.ToSingle(workspace.Catalog.Read(weaponDisplay), 48) == 1.5f, "set_tag_value f32 failed.");
        workspace.Undo(); Wait(workspace); workspace.Undo(); Wait(workspace);
        Check(workspace.Catalog.Read(weaponDisplay).AsSpan().SequenceEqual(weaponBytes), "Undo did not restore the installed tag after set_tag_value.");

        // On an added asset the edit updates the stored bytes and the manifest checksum.
        EditText(weapon, "name", "Sun Cutter");
        var added = project.AddedAssets.First(asset => asset.Native?.Reference == StringBankWriter.ContainerClass);
        uint addedTag = 0x80800000u + ((uint)added.Native!.PackageId << 13) + (uint)added.Native.EntryIndex!.Value;
        string shaBefore = added.Sha256;
        workspace.Select(addedTag); Wait(workspace);
        var (addedOld, addedNew) = workspace.SetTagValue(addedTag, 0x4C, "hash", "0x11223344"); Wait(workspace);
        var after = project.AddedAssets.First(asset => asset.Id == added.Id);
        byte[] stored = project.ReadAddedAsset(added.Id, workspace.Catalog);
        Check(addedOld == "0xA5A5A5A5" && addedNew == "0x11223344" && after.Sha256 != shaBefore
            && string.Equals(after.Sha256, Convert.ToHexString(SHA256.HashData(stored)), StringComparison.OrdinalIgnoreCase)
            && stored[0x4C] == 0x44 && stored[0x4F] == 0x11,
            "set_tag_value on an added asset did not update its bytes and sha.");
        workspace.Undo(); Wait(workspace);
        var undone = project.AddedAssets.First(asset => asset.Id == added.Id);
        Check(undone.Sha256 == shaBefore && project.ReadAddedAsset(added.Id, workspace.Catalog)[0x4C] == 0xA5,
            "Undo did not restore the added asset's bytes and sha.");
        Console.WriteLine("creation tag_value refused=header,past-end,range,type u32=pass f32=pass added-asset-sha=pass undo=pass");
    }

    private static void Reject<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var time = Stopwatch.StartNew();
        while (workspace.IsBusy && time.Elapsed < TimeSpan.FromSeconds(30)) { workspace.Poll(); Thread.Sleep(1); }
        workspace.Poll();
        Check(!workspace.IsBusy && workspace.Error.Length == 0, workspace.Error.Length == 0 ? "Workspace timed out." : workspace.Error);
    }
}
