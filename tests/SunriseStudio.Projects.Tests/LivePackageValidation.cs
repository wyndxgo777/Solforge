using System.Security.Cryptography;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

/** Validates that owned package patches round-trip, inherited assets are unchanged and the original package is never modified. */
internal static class LivePackageValidation
{
    /** Real package output stays in a temporary directory and is removed after verification. */
    internal static void Run(string gameRoot)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "sunrise-studio-owned-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var original = PackageCatalog.Open(gameRoot);
            var entry = original.Entries.First(x => x.PackageId == 0x01CE && x.Size > 0);
            string source = Path.Combine(original.PackageDirectory, entry.PackageName + ".pkg");
            var dependencies = PackageWriter.GetRequiredPredecessors(source).Append(source)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (dependencies.Sum(x => new FileInfo(x).Length) > 32 * 1024 * 1024)
                throw new InvalidOperationException("Owned package fixture exceeds the copy limit.");
            var before = dependencies.ToDictionary(x => x, Digest);
            byte[] bytes = original.ReadOriginal(entry.TagHash);
            byte[] edited = new byte[bytes.Length + 0x210000];
            bytes.CopyTo(edited, 0);
            edited[^1] ^= 1;
            var project = StudioProject.Create(Path.Combine(temporary, "project"), "Owned fixture");
            project.ApplyEdit(entry.TagHash, edited, original);
            var build = project.Build(original);
            PackageFormatValidation.Check(build.PackageFiles[0]);
            PackageFormatValidation.CheckInheritance(source, build.PackageFiles[0], new HashSet<int> { entry.EntryIndex });
            string verificationRoot = Path.Combine(temporary, "verification");
            string packageRoot = Path.Combine(verificationRoot, "packages");
            Directory.CreateDirectory(packageRoot);
            foreach (string file in dependencies.Concat(build.PackageFiles))
                File.Copy(file, Path.Combine(packageRoot, Path.GetFileName(file)));
            string codec = Path.Combine("bin", "x64", "oo2core_3_win64.dll");
            Directory.CreateDirectory(Path.Combine(verificationRoot, "bin", "x64"));
            File.Copy(Path.Combine(original.GameRoot, codec), Path.Combine(verificationRoot, codec));
            using var catalog = PackageCatalog.Open(verificationRoot, original.KeySource);
            if (!catalog.ReadOriginal(entry.TagHash).SequenceEqual(edited))
                throw new InvalidDataException("Owned asset patch roundtrip failed.");
            int verified = 0;
            foreach (var untouched in original.Entries.Where(x => x.PackageId == entry.PackageId && x.TagHash != entry.TagHash))
            {
                if (!catalog.ReadOriginal(untouched.TagHash).SequenceEqual(original.ReadOriginal(untouched.TagHash)))
                    throw new InvalidDataException("Owned inherited asset changed.");
                verified++;
            }
            foreach (var pair in before)
                if (Digest(pair.Key) != pair.Value) throw new IOException("Original package was modified.");
            Console.WriteLine($"PASS owned-package family={entry.PackageName} changed=1 inherited={verified} source-files={dependencies.Length}");
        }
        finally
        {
            if (Directory.Exists(temporary) && Path.GetFullPath(temporary)
                .StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(temporary, true);
        }
    }

    private static string Digest(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }
}
