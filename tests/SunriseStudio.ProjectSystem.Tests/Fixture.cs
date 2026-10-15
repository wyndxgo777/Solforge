using System.Security.Cryptography;
using SunriseStudio.Packages;

namespace SunriseStudio.ProjectSystem.Tests;

/** A synthetic installation: one new-range package of random tag bodies, written by PackageWriter. */
internal sealed class Fixture
{
    public const ushort PackageId = 0x100;
    public string Install { get; }
    public string PackageFile { get; }
    public int Count { get; }

    public Fixture(string install, int count, int seed)
    {
        Install = install;
        Count = count;
        string packages = Path.Combine(install, "packages");
        Directory.CreateDirectory(packages);
        var random = new Random(seed);
        var entries = new List<AuthoredPackageEntry>();
        for (int i = 0; i < count; i++)
        {
            byte[] body = new byte[64 + random.Next(0, 512) * 4];
            random.NextBytes(body);
            entries.Add(new(0x80800000u + (uint)i + 1, 0x100A, body, $"fixture:{i}"));
        }
        PackageFile = Path.Combine(packages, $"w64_fixture_{PackageId:x4}_0.pkg");
        PackageWriter.WriteNew(PackageFile, PackageId, entries);
    }

    /** Tag hash of entry index in the fixture package. */
    public static uint Tag(int index) => 0x80800000u | ((uint)PackageId << 13) | (uint)index;

    /** SHA-256 of every package file under the installation, for "base files untouched" checks. */
    public SortedDictionary<string, string> Hashes()
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Install, "*.pkg", SearchOption.AllDirectories))
        {
            using var stream = File.OpenRead(file);
            result[Path.GetRelativePath(Install, file).Replace('\', '/')] = Convert.ToHexString(SHA256.HashData(stream));
        }
        return result;
    }
}
