/**
 * File: Reads and writes the .solmod container (a zip with solmod.json, content and file hashes); installing it is
 * covered by InstallSnapshots.cs.
 */
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Content.Packaging;

/** One file in a .solmod: path, size and SHA-256. */
public sealed record SolmodFile(string Path, long Size, string Sha256);

/** solmod.json: what a .solmod holds and what it needs. */
public sealed record SolmodManifest(string Id, string Name, string Version, string Profile, IReadOnlyList<SolmodFile> Files,
    IReadOnlyList<string> Dependencies)
{
    public const int Format = 1;
    public const string FileName = "solmod.json";
    public const string DefaultProfile = "D2_ARRIVALS_86657";
}

/**
 * The .solmod container (Studio.md Phase 42): a zip of an installed mod folder (mod.json and its
 * packages) plus solmod.json, which lists every file with its size and SHA-256 so an install can
 * refuse a damaged or tampered copy. Paths are always relative and forward-slashed; extraction refuses
 * any entry that leaves the target folder.
 */
public static class SolmodContainer
{
    public const string Extension = ".solmod";
    private const string FilesPrefix = "files/";

    /** Packs a built mod folder. Id, name and version come from its mod.json when the arguments are null. */
    public static SolmodManifest Pack(string modFolder, string outputPath, string? id = null, string? name = null, string? version = null,
        string profile = SolmodManifest.DefaultProfile, IReadOnlyList<string>? dependencies = null)
    {
        string root = Path.GetFullPath(modFolder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"No mod folder at {root}.");
        if (Path.GetFullPath(outputPath).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Write the .solmod outside the mod folder it packs.");
        JsonObject? modJson = null;
        string modJsonPath = Path.Combine(root, "mod.json");
        if (File.Exists(modJsonPath)) modJson = JsonNode.Parse(File.ReadAllText(modJsonPath)) as JsonObject;
        id ??= (string?)modJson?["id"] ?? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
        name ??= (string?)modJson?["name"] ?? id;
        version ??= (string?)modJson?["version"] ?? "1.0.0";
        var files = new List<SolmodFile>();
        string temp = outputPath + ".tmp";
        using (var stream = File.Create(temp))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var entry = zip.CreateEntry(FilesPrefix + relative, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); // deterministic archives
                using var source = File.OpenRead(file);
                using var target = entry.Open();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long size = 0;
                int read;
                while ((read = source.Read(buffer)) > 0)
                {
                    target.Write(buffer, 0, read);
                    hash.AppendData(buffer, 0, read);
                    size += read;
                }
                files.Add(new(relative, size, Convert.ToHexStringLower(hash.GetHashAndReset())));
            }
            var manifest = new SolmodManifest(id, name, version, profile, files, dependencies ?? []);
            var manifestEntry = zip.CreateEntry(SolmodManifest.FileName, CompressionLevel.Optimal);
            manifestEntry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using (var writer = manifestEntry.Open()) writer.Write(System.Text.Encoding.UTF8.GetBytes(ManifestJson(manifest)));
        }
        File.Move(temp, outputPath, overwrite: true);
        return ReadManifest(outputPath);
    }

    /** The manifest as solmod.json text. */
    public static string ManifestJson(SolmodManifest manifest) => new JsonObject
    {
        ["format"] = SolmodManifest.Format, ["id"] = manifest.Id, ["name"] = manifest.Name, ["version"] = manifest.Version,
        ["profile"] = manifest.Profile,
        ["dependencies"] = new JsonArray(manifest.Dependencies.Select(item => (JsonNode)item).ToArray()),
        ["files"] = new JsonArray(manifest.Files.Select(file => (JsonNode)new JsonObject
            { ["path"] = file.Path, ["size"] = file.Size, ["sha256"] = file.Sha256 }).ToArray())
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /** Reads the manifest of a .solmod file. */
    public static SolmodManifest ReadManifest(string containerPath)
    {
        using var zip = ZipFile.OpenRead(containerPath);
        return ReadManifest(zip);
    }

    /** Reads the manifest from an open archive. */
    private static SolmodManifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(SolmodManifest.FileName) ?? throw new InvalidDataException("Not a .solmod: solmod.json is missing.");
        using var reader = new StreamReader(entry.Open());
        var root = JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? throw new InvalidDataException("solmod.json is not an object.");
        if ((int?)root["format"] is not SolmodManifest.Format) throw new InvalidDataException($"Unsupported .solmod format {root["format"]}.");
        var files = (root["files"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(file => new SolmodFile((string?)file["path"] ?? "", (long?)file["size"] ?? -1, (string?)file["sha256"] ?? "")).ToList();
        var dependencies = (root["dependencies"] as JsonArray ?? new JsonArray()).Select(item => (string?)item ?? "").Where(item => item.Length > 0).ToList();
        return new((string?)root["id"] ?? "", (string?)root["name"] ?? "", (string?)root["version"] ?? "", (string?)root["profile"] ?? "", files, dependencies);
    }

    /** Problems with a container: manifest, unsafe paths, missing, extra or changed files. Empty means it is intact. */
    public static IReadOnlyList<string> Verify(string containerPath)
    {
        var problems = new List<string>();
        using var zip = ZipFile.OpenRead(containerPath);
        SolmodManifest manifest;
        try { manifest = ReadManifest(zip); }
        catch (Exception error) when (error is InvalidDataException or JsonException) { return [error.Message]; }
        if (!ContentDocument.IsValidId(manifest.Id.ToLowerInvariant())) problems.Add($"Mod id '{manifest.Id}' is not a safe folder name.");
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            if (!SafeRelative(file.Path)) { problems.Add($"Unsafe path '{file.Path}'."); continue; }
            if (!listed.Add(file.Path)) problems.Add($"'{file.Path}' is listed twice.");
            var entry = zip.GetEntry(FilesPrefix + file.Path);
            if (entry is null) { problems.Add($"'{file.Path}' is missing."); continue; }
            using var stream = entry.Open();
            string sha = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (entry.Length != file.Size || !string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase)) problems.Add($"'{file.Path}' changed.");
        }
        foreach (var entry in zip.Entries)
            if (entry.FullName.StartsWith(FilesPrefix, StringComparison.Ordinal) && entry.FullName.Length > FilesPrefix.Length
                && !entry.FullName.EndsWith('/') && !listed.Contains(entry.FullName[FilesPrefix.Length..]))
                problems.Add($"'{entry.FullName}' is not in the manifest.");
        if (!manifest.Files.Any(file => file.Path == "mod.json")) problems.Add("The mod has no mod.json; the Sunrise mod loader would skip it.");
        return problems;
    }

    /** Extracts a verified container to modsRoot/<id>; refuses a damaged one. Returns the mod folder. */
    public static string Extract(string containerPath, string modsRoot)
    {
        var problems = Verify(containerPath).Where(problem => !problem.StartsWith("The mod has no mod.json", StringComparison.Ordinal)).ToList();
        if (problems.Count > 0) throw new InvalidDataException("The .solmod is damaged: " + string.Join(" ", problems));
        using var zip = ZipFile.OpenRead(containerPath);
        var manifest = ReadManifest(zip);
        string target = Path.GetFullPath(Path.Combine(modsRoot, manifest.Id));
        string staging = target + ".installing";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        foreach (var file in manifest.Files)
        {
            string path = Path.GetFullPath(Path.Combine(staging, file.Path));
            if (!path.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Unsafe path '{file.Path}'.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            zip.GetEntry(FilesPrefix + file.Path)!.ExtractToFile(path, overwrite: true);
        }
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.Move(staging, target);
        return target;
    }

    /** Whether a path is a safe relative path (no backslash, root or parent parts). */
    public static bool SafeRelative(string path) => path.Length > 0 && !path.Contains('\\') && !path.StartsWith('/') && !path.Contains(':')
        && path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
}
