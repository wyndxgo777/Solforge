using System.Diagnostics;
using System.Security.Cryptography;
using SunriseStudio.Editor;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

namespace SunriseStudio.Creation.Tests;

/** Checks the sandbox perk icon workflow: separate item and perk icon links, replacement, undo/redo and reopen. */
internal static class SandboxPerkIconWorkflowChecks
{
    /** Catalyst artwork and perk symbols stay separate through repeated edits and Undo. */
    public static void Run(string install, string keys, string fixture, string itemPng, string perkPng, string output)
    {
        string copy = Path.Combine(output, "perk-icon-project");
        Directory.CreateDirectory(copy);
        File.Copy(Path.Combine(fixture, "sunrise.project.json"), Path.Combine(copy, "sunrise.project.json"));
        foreach (string folder in new[] { "assets", "deltas" })
        foreach (string path in Directory.EnumerateFiles(Path.Combine(fixture, folder), "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(copy, Path.GetRelativePath(fixture, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target);
        }
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install, keys); Wait(workspace);
        workspace.OpenProject(Path.Combine(copy, "sunrise.project.json")); Wait(workspace);
        workspace.LoadInvestment(); Wait(workspace);
        var item = workspace.Investment!.Items.Single(item => item.Name == "Heliostat Catalyst");
        var perk = workspace.Investment.ReadItem(item.Tag, workspace.Catalog!.Read(item.Tag)).Perks.Single();
        var behavior = InvestmentSandboxPerkAuthoring.Read(workspace.Catalog, perk.Index);
        string behaviorHash = Convert.ToHexString(SHA256.HashData(behavior.Gameplay));
        workspace.Select(item.Tag); Wait(workspace);
        Apply(itemPng, perkPng);
        var first = CheckIcons(PngInterchange.Read(itemPng), PngInterchange.Read(perkPng));
        string secondPath = Path.Combine(output, "replacement-perk.png");
        var secondImage = new RgbaImage(96, 96, Enumerable.Range(0, 96 * 96 * 4)
            .Select(index => (byte)(index % 4 == 3 ? 255 : 90)).ToArray());
        PngInterchange.Write(secondPath, secondImage);
        Apply(null, secondPath);
        var second = CheckIcons(PngInterchange.Read(itemPng), secondImage);
        Check(first.Item == second.Item && first.Perk != second.Perk,
            "Replacing a perk icon changed the catalyst artwork or reused its old container.");
        workspace.Undo(); Wait(workspace); workspace.LoadInvestment(); Wait(workspace);
        Check(CheckIcons(PngInterchange.Read(itemPng), PngInterchange.Read(perkPng)) == first,
            "Undo did not restore both icon links.");
        workspace.Redo(); Wait(workspace); workspace.LoadInvestment(); Wait(workspace);
        Check(CheckIcons(PngInterchange.Read(itemPng), secondImage) == second,
            "Redo did not restore the separate perk symbol.");
        string saved = workspace.Project!.ManifestPath;
        workspace.CloseProject(); Wait(workspace); workspace.OpenProject(saved); Wait(workspace);
        workspace.LoadInvestment(); Wait(workspace);
        Check(CheckIcons(PngInterchange.Read(itemPng), secondImage) == second,
            "The separate icon links did not reopen.");
        Console.WriteLine("sandbox_perk_icon assignments=2 item_and_perk=separate pixels=exact behavior=unchanged hash_rows=unique undo=pass redo=pass reopen=pass");

        void Apply(string? itemPath, string perkPath)
        {
            var draft = new ItemWorkflow(workspace, InvestmentItemKinds.Mods, workspace.Investment!.FindItem(item.Tag));
            draft.Update(workspace);
            CreationField Field(string label) => draft.Inputs.Single(field => field.Label == label);
            Field("Package ID").Text = "0AB1";
            if (itemPath is not null) { Field("Icon from item").MakeNew = true; Field("Custom icon (PNG)").Text = itemPath; }
            Field("Sandbox perk icon (PNG)").Text = perkPath;
            Check(draft.Problems(workspace).Count == 0, string.Join("; ", draft.Problems(workspace)));
            draft.Create(workspace); Wait(workspace); workspace.LoadInvestment(); Wait(workspace);
        }
        (uint Item, uint Perk) CheckIcons(RgbaImage itemImage, RgbaImage perkImage)
        {
            var current = workspace.Investment!.FindItem(item.Tag)!;
            var itemIcon = workspace.Investment.ReadIcon(current)!;
            var perkIcon = workspace.Investment.ReadPerkIcon(perk.Index)!;
            Check(itemIcon.ContainerTag != perkIcon.ContainerTag
                && itemIcon.Texture.Subresources.Single().Data.SequenceEqual(itemImage.Pixels)
                && perkIcon.Texture.Subresources.Single().Data.SequenceEqual(perkImage.Pixels),
                "The catalyst and bonus perk did not retain distinct PNG pixels.");
            Check(Convert.ToHexString(SHA256.HashData(workspace.Catalog!.Read(behavior.GameplayTag))) == behaviorHash,
                "An icon edit changed the sandbox behavior.");
            var rows = InvestmentItemClone.ReadIndexRows(workspace.Catalog.Read(new InvestmentSources().Icons));
            Check(rows.Count(row => row.Hash == current.Hash) == 1
                && rows.Count(row => row.Hash == behavior.DefinitionHash) == 1,
                "Repeated icon editing duplicated an identity row.");
            return (itemIcon.ContainerTag, perkIcon.ContainerTag);
        }
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var timer = Stopwatch.StartNew();
        while (workspace.IsBusy && timer.Elapsed < TimeSpan.FromMinutes(5)) { workspace.Poll(); Thread.Sleep(2); }
        workspace.Poll();
        Check(!workspace.IsBusy && workspace.Error.Length == 0,
            workspace.Error.Length == 0 ? "Icon workflow timed out." : workspace.Error);
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
