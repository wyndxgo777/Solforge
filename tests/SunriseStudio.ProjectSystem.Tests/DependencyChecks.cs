using System.Buffers.Binary;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using static SunriseStudio.ProjectSystem.Tests.Check;

namespace SunriseStudio.ProjectSystem.Tests;

/** Phase 18: dependency crawl + missing-reference manifest, and package ids from 0x0100-0x0CFF. */
internal static class DependencyChecks
{
    internal static void Run(string root, List<string> checks)
    {
        var fixture = new Fixture(Path.Combine(root, "install"), 6, 1801);
        using var catalog = PackageCatalog.Open(fixture.Install);
        var project = StudioProject.Create(Path.Combine(root, "project"), "Dependency fixture");

        var allocator = PackageIdAllocator.For(catalog, project);
        Require(allocator.IsUsed(Fixture.PackageId), "The allocator handed out an installed package id.");
        ushort packageId = allocator.Allocate();
        Require(packageId == 0x101, $"The allocator chose {packageId:X4} instead of the lowest free id 0101.");
        byte[] addedBody = new byte[64];
        var asset = project.AddAsset("fixture:new", addedBody, new NativeAssetMetadata(packageId, 0x80800001, 0x100A, "fixture:new", 0));
        uint addedTag = DependencyCrawler.TagOf(packageId, 0);

        uint edited = Fixture.Tag(1), present = Fixture.Tag(2), missing = Fixture.Tag(999);
        byte[] bytes = catalog.ReadOriginal(edited);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), present);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), missing);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), addedTag);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 0x83F00000u); // a package nobody has: not a reference
        project.ApplyEdit(edited, bytes, catalog);

        var manifest = DependencyCrawler.Crawl(project, catalog);
        var node = manifest.Nodes.Single(item => item.Tag == edited);
        Require(node.Source == "changed" && node.References.Contains(present) && node.References.Contains(addedTag),
            "The crawler missed a child of the changed tag.");
        Require(manifest.Nodes.Any(item => item.Tag == addedTag && item.Source == "added"), "The crawler skipped the added asset.");
        var issue = manifest.Issues.SingleOrDefault() ?? throw new InvalidOperationException(
            $"Expected exactly one missing reference, got {manifest.Issues.Count}.");
        Require(issue.Kind == DependencyIssueKind.MissingReference && issue.FromTag == edited && issue.TargetTag == missing,
            "The missing reference was not reported.");
        string path = manifest.Write(project.DirectoryPath);
        var json = JsonNode.Parse(File.ReadAllBytes(path))!;
        Require(json["complete"]!.GetValue<bool>() is false && json["issues"]!.AsArray().Count == 1, "The dependency manifest file is wrong.");
        checks.Add("dependency crawler finds children and reports the missing reference in build/dependencies.json");

        File.Delete(Path.Combine(project.DirectoryPath, asset.Path.Replace('/', Path.DirectorySeparatorChar)));
        var broken = DependencyCrawler.Crawl(project, catalog);
        Require(broken.Issues.Any(item => item.Kind == DependencyIssueKind.MissingProjectFile && item.FromTag == addedTag),
            "A deleted added-asset file was not reported.");
        checks.Add("a missing added-asset file shows in the dependency manifest");

        var pool = new PackageIdAllocator([0x100, 0x101, 0x103, 0x0FF, 0xD00]);
        Require(pool.Allocate() == 0x102 && pool.Allocate() == 0x104, "The allocator did not take the lowest free ids.");
        Require(!pool.TryReserve(0x0FF) && !pool.TryReserve(0xD00) && !pool.TryReserve(0x103) && pool.TryReserve(0xCFF),
            "TryReserve accepted an id outside 0x0100-0x0CFF or one in use.");
        pool.Release(0x102);
        Require(pool.Allocate() == 0x102, "A released id was not reused.");
        int handed = 0;
        while (pool.Available > 0) { ushort id = pool.Allocate(); Require(PackageWriter.IsNewPackageId(id), $"Allocated {id:X4}."); handed++; }
        Require(handed == PackageIdAllocator.Capacity - 6, "The allocator range is not 0x0100-0x0CFF.");
        Throws<InvalidOperationException>(() => pool.Allocate(), "An exhausted allocator still handed out an id.");
        checks.Add($"package-id allocator stays inside {PackageWriter.NewPackageIdRange} and refuses used ids");
    }
}
