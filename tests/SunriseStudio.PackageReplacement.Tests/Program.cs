// Console test runner for package replacement: startup filename and replacement edits.
using System.Security.Cryptography;
using System.Text.Json;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sunrise-replacement-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
try
{
    string install = Path.Combine(root, "install");
    Directory.CreateDirectory(Path.Combine(install, "packages"));
    byte[] original = Enumerable.Range(0, 900).Select(i => (byte)i).ToArray();
    byte[] other = new byte[2_400_000];
    new Random(87122).NextBytes(other);
    string source = Path.Combine(install, "packages", "w64_fixture_unp1_0.pkg");
    PackageWriter.WriteNew(source, 0x109,
        [new(0x80800001, 0x100A, original, "startup:one"), new(0x80800002, 0x100A, other, "startup:two")]);
    string sourceHash = HashFile(source);
    using (var catalog = PackageCatalog.Open(install))
    {
        uint tag = catalog.Entries[0].TagHash;
        var project = StudioProject.Create(Path.Combine(root, "project"), "Startup replacement");
        byte[] edited = (byte[])original.Clone(); edited[3] ^= 7;
        project.ApplyEdit(tag, edited, catalog);
        var result = project.Build(catalog);
        Check(Path.GetFileName(result.PackageFiles.Single()) == Path.GetFileName(source), "Startup filename changed.");
        using var rebuilt = PackageCatalog.Open(result.OutputDirectory);
        Check(rebuilt.Read(tag).SequenceEqual(edited), "Replacement edit differs.");
        Check(rebuilt.Read(tag + 1).SequenceEqual(other), "Untouched spanning asset differs.");
        Check(rebuilt.GetName(tag) == "startup:one", "Named metadata was lost.");
        using var receipt = JsonDocument.Parse(File.ReadAllText(result.ReceiptPath));
        Check(receipt.RootElement.GetProperty("requiredPredecessors").GetArrayLength() == 0, "Replacement needs old blocks.");
        Check(receipt.RootElement.GetProperty("replacesInstalledFiles").GetArrayLength() == 1, "Receipt omitted replacement mode.");
        Check(HashFile(source) == sourceHash, "Original package changed.");
    }
    Console.WriteLine("replacement synthetic=pass identity=pass metadata=pass blocks=pass receipt=pass");
    if (args.Length == 2 && args[0] == "--game-root")
    {
        using var owned = PackageCatalog.Open(args[1]);
        const string family = "w64_ui_startup_unp1_0";
        var entries = owned.Entries.Where(entry => entry.PackageName == family).OrderBy(entry => entry.EntryIndex).ToArray();
        Check(entries.Length != 0, "Owned startup fixture is missing.");
        var target = entries.First(entry => entry.Size != 0);
        byte[] edited = owned.ReadOriginal(target.TagHash); edited[^1] ^= 1;
        string ownedSource = Path.Combine(owned.PackageDirectory, family + ".pkg");
        string ownedHash = HashFile(ownedSource);
        string output = Path.Combine(root, "owned-output", "packages", family + ".pkg");
        var artifact = PackageWriter.WriteReplacement(ownedSource, output,
            new Dictionary<int, byte[]> { [target.EntryIndex] = edited }, owned.ReadOriginal, owned.GameRoot);
        using var rebuilt = PackageCatalog.Open(Path.GetDirectoryName(Path.GetDirectoryName(output)!)!);
        foreach (var entry in entries)
            Check(rebuilt.Read(entry.TagHash).SequenceEqual(entry.TagHash == target.TagHash ? edited : owned.ReadOriginal(entry.TagHash)),
                $"Owned entry {entry.EntryIndex} differs.");
        Check(artifact.PatchId == 0 && HashFile(ownedSource) == ownedHash, "Owned package identity or original changed.");
        Console.WriteLine($"replacement owned=pass entries={entries.Length} patch=0 source=unchanged");
    }
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }
finally
{
    if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(root).StartsWith("sunrise-replacement-", StringComparison.Ordinal))
        throw new IOException("Cleanup left its temporary test root.");
    Directory.Delete(root, true);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}
