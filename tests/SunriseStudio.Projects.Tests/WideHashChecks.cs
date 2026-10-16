using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

/** Checks project wide-hash support: native layout, versions, persistence, collisions and atomic overlay writes. */
internal static class WideHashChecks
{
    private const ulong Installed = 0x1122334455667788, Authored = 0xFFEEDDCCBBAA9988;
    private const uint NativeClass = 0x80809994;

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string parent)
    {
        string root = Path.Combine(parent, "wide-hashes");
        Directory.CreateDirectory(root);
        ulong[] source = [Authored, 1];
        var aliases = new WideHashAliases(source); source[0] = 5;
        Check(aliases.SequenceEqual(new ulong[] { 1, Authored }), "copied, sorted aliases");
        var equal = new WideHashAliases([Authored, 1]);
        Check(aliases.Equals(equal) && aliases.GetHashCode() == equal.GetHashCode(), "value equality");
        var native = new NativeAssetMetadata(0xC00, NativeClass, 0x1000) { WideHashes = aliases };
        Check(native == native with { WideHashes = equal }, "metadata value equality");
        Reject<InvalidDataException>(() => new WideHashAliases([1, 1]));
        Reject<JsonException>(() => JsonSerializer.Deserialize<WideHashAliases>("[\"123\"]"));

        string install = Path.Combine(root, "install"), packages = Path.Combine(install, "packages");
        Directory.CreateDirectory(packages);
        string original = Path.Combine(packages, "w64_wide_0100_0.pkg");
        PackageWriter.WriteNew(original, 0x100,
            [new(NativeClass, 0x1000, [1, 2, 3], "installed") { WideHashes = new([Installed]) }]);
        string originalHash = Hash(original);
        using var catalog = PackageCatalog.Open(install);
        Check(catalog.Resolve64(Installed) == 0x80A00000 && catalog.HasInstalledWideHashClaim(Installed), "installed lookup");
        Writer(root);
        Occupancy(root);
        Project(root, catalog, native);
        TargetAdmission(root, catalog);
        Overlay(catalog);
        Aggregate(root, catalog);
        Check(Hash(original) == originalHash, "installed bytes unchanged");
        Console.WriteLine("PASS wide_hashes native_layout versions persistence collisions overlay_atomicity limits");
    }

    private static void Writer(string root)
    {
        var entry = new AuthoredPackageEntry(NativeClass, 0x1000, [1, 2, 3], "fixture:wide:names");
        string plain = Path.Combine(root, "w64_plain_0c00_0.pkg");
        PackageWriter.WriteNew(plain, 0xC00, [entry]);
        byte[] plainMisc = Misc(plain);
        Check(Convert.ToHexString(SHA256.HashData(plainMisc)) ==
            "92C3C867E79098900BCECDC61B265D473523CB248933A9326BD03E4ED433F5AD", "named-only golden");
        string sourceDirectory = Path.Combine(root, "writer-source"); Directory.CreateDirectory(sourceDirectory);
        string mixed = Path.Combine(sourceDirectory, "w64_mixed_0c00_0.pkg");
        PackageWriter.WriteNew(mixed, 0xC00,
            [entry with { WideHashes = new([Authored, 1]) }, new(NativeClass + 1, 0x1000, [4]) { WideHashes = new([3]) }]);
        var rows = PackageFormatValidation.WideHashes(mixed);
        Check(rows.SequenceEqual(new (ulong, uint, uint)[]
            { (1, 0x82000000, NativeClass), (3, 0x82000001, NativeClass + 1), (Authored, 0x82000000, NativeClass) }),
            "independent wide rows");
        byte[] mixedMisc = Misc(mixed);
        Check(plainMisc.AsSpan(0x60).SequenceEqual(mixedMisc.AsSpan(0x60, plainMisc.Length - 0x60)),
            "named rows and string pointers unchanged");
        string patch = Path.Combine(root, "w64_mixed_0c00_1.pkg");
        PackageWriter.WritePatch(mixed, patch, new Dictionary<int, byte[]> { [0] = [9, 8, 7, 6] });
        PackageFormatValidation.CheckInheritance(mixed, patch, new HashSet<int> { 0 });
        Check(PackageFormatValidation.WideHashes(patch).SequenceEqual(rows), "patch preserves aliases");
        string unnamed = Path.Combine(root, "w64_unnamed_0c01_0.pkg");
        PackageWriter.WriteNew(unnamed, 0xC01, [entry with { Name = null, WideHashes = new([7, 8]) }]);
        Check(PackageFormatValidation.WideHashes(unnamed).Length == 2, "unnamed aliases");
        Reject<InvalidDataException>(() => PackageWriter.WriteNew(Path.Combine(root, "duplicate.pkg"), 0xC02,
            [entry with { WideHashes = new([1]) }, entry with { WideHashes = new([1]) }]));
    }

    /** Occupancy includes structurally valid claims that the current resolver cannot select. */
    private static void Occupancy(string root)
    {
        string install = Path.Combine(root, "occupied"), packages = Path.Combine(install, "packages");
        Directory.CreateDirectory(packages);
        var entry = new AuthoredPackageEntry(NativeClass, 0x1000, [1]) { WideHashes = new([Installed]) };
        PackageWriter.WriteNew(Path.Combine(packages, "w64_a_0100_0.pkg"), 0x100, [entry]);
        PackageWriter.WriteNew(Path.Combine(packages, "w64_b_0101_0.pkg"), 0x101, [entry]);
        string incompatible = Path.Combine(packages, "w64_c_0102_0.pkg");
        PackageWriter.WriteNew(incompatible, 0x102, [entry with { WideHashes = new([Installed + 1]) }]);
        EditMisc(incompatible, misc =>
        {
            int row = checked(0x48 + (int)BinaryPrimitives.ReadUInt64LittleEndian(misc.AsSpan(0x38)));
            BinaryPrimitives.WriteUInt32LittleEndian(misc.AsSpan(row + 12), NativeClass + 1);
        });
        string old = Path.Combine(packages, "w64_d_0103_0.pkg"), latest = Path.Combine(packages, "w64_d_0103_1.pkg");
        PackageWriter.WriteNew(old, 0x103, [entry with { WideHashes = new([Installed + 2]) }]);
        string staged = Path.Combine(root, "w64_d_0103_1.pkg");
        PackageWriter.WritePatch(old, staged, new Dictionary<int, byte[]> { [0] = [2] });
        File.Copy(staged, latest);
        EditMisc(latest, misc => misc.AsSpan(0x30, 16).Clear());
        using var catalog = PackageCatalog.Open(install);
        foreach (ulong hash in new[] { Installed, Installed + 1, Installed + 2 })
        {
            Check(catalog.Resolve64(hash) is null && catalog.HasInstalledWideHashClaim(hash), "unresolved reserved claim");
            Reject<InvalidDataException>(() => catalog.SetOverlayEntries([Row(0, hash)]));
        }
    }

    private static void Project(string root, PackageCatalog catalog, NativeAssetMetadata native)
    {
        var project = StudioProject.Create(Path.Combine(root, "project"), "Wide hashes");
        var asset = project.AddDerivedAsset("bubble.bin", 0x80A00000, catalog.ReadOriginal(0x80A00000),
            native with { WideHashes = null }, catalog);
        string payload = Path.Combine(project.DirectoryPath, asset.Path), savedHash = Hash(payload);
        DateTime stamp = File.GetLastWriteTimeUtc(payload);
        Check(Version(project) == 1, "legacy creation");
        Check(project.SetWideHashAliases(asset.Id, native.WideHashes!, catalog), "alias edit");
        Check(Version(project) == 3 && File.ReadAllText(project.ManifestPath).Contains("FFEEDDCCBBAA9988"), "v3 hex persistence");
        Check(Hash(payload) == savedHash && File.GetLastWriteTimeUtc(payload) == stamp, "metadata edit leaves delta untouched");
        var prior = project.CaptureAddedAsset(asset.Id, catalog);
        project = StudioProject.Open(project.DirectoryPath);
        Check(project.AddedAssets.Single().Native!.WideHashes!.Equals(native.WideHashes), "reopened aliases");
        string manifest = File.ReadAllText(project.ManifestPath);
        Reject<InvalidDataException>(() => project.SetWideHashAliases(asset.Id, [Installed], catalog));
        Check(File.ReadAllText(project.ManifestPath) == manifest, "collision leaves manifest");
        Reject<InvalidDataException>(() => project.AddAssets([new("other", [4], native)], catalog));
        Check(project.AddedAssets.Count == 1 && File.ReadAllText(project.ManifestPath) == manifest, "creation duplicate rollback");
        var build = project.Build(catalog);
        string built = build.PackageFiles.Single();
        Check(PackageFormatValidation.WideHashes(built).Length == 2, "build alias rows");
        string verify = Path.Combine(root, "built"), packages = Path.Combine(verify, "packages");
        Directory.CreateDirectory(packages); File.Copy(built, Path.Combine(packages, Path.GetFileName(built)));
        using (var reopened = PackageCatalog.Open(verify))
            Check(reopened.Resolve64(Authored) == 0x82000000 && reopened.Read(0x82000000).SequenceEqual(new byte[] { 1, 2, 3 }),
                "built native lookup and bytes");
        project.SetWideHashAliases(asset.Id, [], catalog);
        Check(Version(project) == 1 && !File.ReadAllText(project.ManifestPath).Contains("wideHashes"), "empty set downgrades without unknown field");
        project.RestoreTransaction(new(new Dictionary<uint, byte[]>()),
            new Dictionary<string, AddedAssetSnapshot?> { [asset.Id] = prior }, catalog);
        Check(Version(project) == 3 && Hash(payload) == savedHash && File.GetLastWriteTimeUtc(payload) == stamp, "restore aliases without payload rewrite");
        var json = JsonNode.Parse(File.ReadAllText(project.ManifestPath))!;
        json["version"] = 1; File.WriteAllText(project.ManifestPath, json.ToJsonString());
        Reject<InvalidDataException>(() => StudioProject.Open(project.DirectoryPath));
        json["version"] = 2; json["objectEdits"] = new JsonArray(); File.WriteAllText(project.ManifestPath, json.ToJsonString());
        Reject<InvalidDataException>(() => StudioProject.Open(project.DirectoryPath));
        json["addedAssets"]![0]!["native"]!["wideHashes"] = new JsonArray(); json["version"] = 1; json.AsObject().Remove("objectEdits");
        File.WriteAllText(project.ManifestPath, json.ToJsonString());
        project = StudioProject.Open(project.DirectoryPath); project.Save();
        Check(!File.ReadAllText(project.ManifestPath).Contains("wideHashes"), "legacy empty set normalized");
    }

    private static void Overlay(PackageCatalog catalog)
    {
        var a = Row(0, Authored); var b = Row(1, Authored);
        catalog.SetOverlayEntries([a]);
        using var stop = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        int reads = 0;
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Check(catalog.TryResolve64(Authored, out var value) && value.TagHash == 0x82000000U + (uint)value.EntryIndex
                    && value.Reference == NativeClass + (uint)value.EntryIndex, "atomic alias and metadata");
                Interlocked.Increment(ref reads);
                started.Set();
            }
        });
        try
        {
            Check(started.Wait(TimeSpan.FromSeconds(10)), "reader start");
            for (int i = 0; i < 2000; i++) catalog.SetOverlayEntries([i % 2 == 0 ? b : a]);
        }
        finally { stop.Cancel(); reader.GetAwaiter().GetResult(); }
        Check(reads > 0, "concurrent reader ran");
        uint? previous = catalog.Resolve64(Authored);
        Reject<InvalidDataException>(() => catalog.SetOverlayEntries([a, b]));
        Check(catalog.Resolve64(Authored) == previous, "failed overlay keeps old state");
        catalog.SetOverlayEntries([]);
        Check(catalog.Resolve64(Authored) is null, "overlay detach removes aliases");
    }

    private static void TargetAdmission(string root, PackageCatalog catalog)
    {
        var project = StudioProject.Create(Path.Combine(root, "target-admission"), "Target admission");
        var asset = project.AddAsset("collision", [1], new(0x100, NativeClass, 0x1000));
        byte[] saved = File.ReadAllBytes(project.ManifestPath);
        Reject<InvalidDataException>(() => project.SetWideHashAliases(asset.Id, [Authored], catalog));
        Check(File.ReadAllBytes(project.ManifestPath).SequenceEqual(saved), "installed target refusal before save");
        var json = JsonNode.Parse(saved)!; json["addedAssets"]![0]!["native"]!["packageId"] = 0xD00;
        File.WriteAllText(project.ManifestPath, json.ToJsonString());
        project = StudioProject.Open(project.DirectoryPath);
        saved = File.ReadAllBytes(project.ManifestPath);
        Reject<InvalidDataException>(() => project.SetWideHashAliases(asset.Id, [Authored], catalog));
        Check(File.ReadAllBytes(project.ManifestPath).SequenceEqual(saved), "legacy target refusal before save");
    }

    private static void Aggregate(string root, PackageCatalog catalog)
    {
        var large = new WideHashAliases(Enumerable.Range(0, 500_001).Select(value => (ulong)value));
        var entry = new AuthoredPackageEntry(NativeClass, 0x1000, [1]) { WideHashes = large };
        Reject<InvalidDataException>(() => PackageWriter.WriteNew(Path.Combine(root, "overlimit.pkg"), 0xC00, [entry, entry]));
        Reject<InvalidDataException>(() => catalog.SetOverlayEntries([Row(0, 1) with { WideHashes = large }, Row(1, 2) with { WideHashes = large }]));
        var project = StudioProject.Create(Path.Combine(root, "overlimit-project"), "Bounded");
        var native = new NativeAssetMetadata(0xC00, NativeClass, 0x1000) { WideHashes = large };
        Reject<InvalidDataException>(() => project.AddAssets([new("a", [1], native), new("b", [2], native)], catalog));
        Check(project.AddedAssets.Count == 0 && !File.Exists(Path.Combine(root, "overlimit.pkg")), "aggregate refusal has no publication");
    }

    private static PackageOverlayEntry Row(int index, ulong hash) => new(new(0x82000000U + (uint)index,
        NativeClass + (uint)index, 8, 0, 1, "w64_authored_0c00_0", 0xC00, index) { TypeInfo = 0x1000 }) { WideHashes = new([hash]) };
    private static int Version(StudioProject project) => JsonNode.Parse(File.ReadAllText(project.ManifestPath))!["version"]!.GetValue<int>();
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static byte[] Misc(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        return file.AsSpan((int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0xF0)),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0xF4))).ToArray();
    }
    private static void EditMisc(string path, Action<byte[]> edit)
    {
        byte[] file = File.ReadAllBytes(path), misc = Misc(path);
        edit(misc); misc.CopyTo(file, (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0xF0)));
        SHA1.HashData(misc).CopyTo(file, 0xF8); File.WriteAllBytes(path, file);
    }
    private static void Check(bool value, string name) { if (!value) throw new InvalidDataException("Wide hash: " + name); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidDataException("Expected " + typeof(T).Name);
    }
}
