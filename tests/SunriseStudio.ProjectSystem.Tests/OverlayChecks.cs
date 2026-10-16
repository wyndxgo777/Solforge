using SunriseStudio.Packages;
using SunriseStudio.Projects;
using static SunriseStudio.ProjectSystem.Tests.Check;

namespace SunriseStudio.ProjectSystem.Tests;

/** Phase 16: overlay reads, base-vs-authored diff and revert to base, without writing the installation. */
internal static class OverlayChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run(string root, List<string> checks)
    {
        var fixture = new Fixture(Path.Combine(root, "install"), 4, 1601);
        var before = fixture.Hashes();
        using var catalog = PackageCatalog.Open(fixture.Install);
        var project = StudioProject.Create(Path.Combine(root, "project"), "Overlay fixture");
        uint tag = Fixture.Tag(1);
        byte[] original = catalog.ReadOriginal(tag);
        byte[] edited = [.. original, 0xAB, 0xCD];
        BitConverter.TryWriteBytes(edited.AsSpan(8), 1.5f);
        project.ApplyEdit(tag, edited, catalog);
        Require(project.ReadAsset(tag, catalog).SequenceEqual(edited), "The overlay did not return the authored bytes.");
        Require(catalog.ReadOriginal(tag).SequenceEqual(original), "The base read changed under the overlay.");
        Require(project.ReadAsset(Fixture.Tag(2), catalog).SequenceEqual(catalog.ReadOriginal(Fixture.Tag(2))),
            "An unedited tag did not fall through to base.");

        var diff = ProjectTagDiff.Compare(project, tag, catalog);
        bool wordChanged = original.AsSpan(8, 4).SequenceEqual(edited.AsSpan(8, 4)) is false;
        Require(diff.Words.Any(word => word.Offset == original.Length && word.Base is null && word.Authored == 0xCDABu),
            "The diff did not show the appended tail word.");
        Require(!wordChanged || diff.Words.Any(word => word.Offset == 8 && word.AuthoredFloat == 1.5f),
            "The diff did not show the authored float.");
        Require(diff.Words.All(word => word.Offset == 8 || word.Offset == original.Length), "The diff listed an unchanged word.");
        Require(diff.ChangedBytes >= 2 && !diff.Identical, "The diff byte count is wrong.");
        checks.Add($"base-vs-authored diff: {diff.ChangedWords} words, {diff.ChangedBytes} bytes");

        project.Revert(tag, catalog);
        Require(!project.ContainsEdit(tag), "Revert left the change in the manifest.");
        Require(ProjectTagDiff.Compare(project, tag, catalog).Identical, "Revert did not restore base bytes.");
        Require(StudioProject.Open(project.DirectoryPath).Changes.Count == 0, "The reverted manifest did not save.");
        Require(fixture.Hashes().SequenceEqual(before), "The overlay or revert wrote the installation.");
        checks.Add("overlay reads authored bytes, revert restores base, installation untouched");
    }
}
