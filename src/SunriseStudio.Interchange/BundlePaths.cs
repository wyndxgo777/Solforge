using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SunriseStudio.Interchange;

/** Path and JSON helpers for bundles: safe path resolution, link rejection and hashing. */
internal static class BundlePaths
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, IncludeFields = true, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    /** Upper-case SHA-256 hex of the bytes. */
    internal static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));
    /** SHA-256 hex of a file's contents. */
    internal static string FileHash(string path) => Hash(File.ReadAllBytes(path));
    /** True when path is inside root. */
    internal static bool Within(string path, string root)
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string value = Path.GetFullPath(path);
        return value.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    /** Throws if the path or any parent is a link or junction. */
    internal static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Bundle paths cannot traverse links or junctions.");
    }
    /** Resolves a manifest-relative path under root; throws for rooted, dotted or colon paths and links. */
    internal static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || relative.Split('/', '\\').Any(part => part is "" or "." or ".." || part.Contains(':')))
            throw new InvalidDataException("Bundle path leaves its directory.");
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!Within(full, root)) throw new InvalidDataException("Bundle path leaves its directory.");
        RejectLinks(full);
        return full;
    }
    /** Reads a JSON document (up to 256 MB) after rejecting links. */
    internal static T Read<T>(string path)
    {
        RejectLinks(path);
        if (new FileInfo(path).Length > 256L * 1024 * 1024)
            throw new InvalidDataException("Bundle document exceeds its size limit.");
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json)
            ?? throw new InvalidDataException("Bundle document is empty.");
    }

    /** External glTF resources obey the same path rules as the bundle manifest. */
    internal static void ValidateGltf(string path)
    {
        RejectLinks(path);
        byte[] data = File.ReadAllBytes(path);
        ReadOnlyMemory<byte> json = data;
        if (Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase))
        {
            if (data.Length < 20 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x46546C67
                || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)) != 0x4E4F534A)
                throw new InvalidDataException("Invalid glTF binary header.");
            int length = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)));
            if (length < 0 || length > data.Length - 20) throw new InvalidDataException("Invalid glTF JSON length.");
            json = data.AsMemory(20, length);
        }
        using var document = JsonDocument.Parse(json);
        foreach (string collection in new[] { "buffers", "images" })
            if (document.RootElement.TryGetProperty(collection, out var rows))
                foreach (var row in rows.EnumerateArray())
                    if (row.TryGetProperty("uri", out var uriValue))
                    {
                        string uri = uriValue.GetString() ?? throw new InvalidDataException("glTF resource URI is empty.");
                        if (uri.StartsWith("data:", StringComparison.Ordinal)) continue;
                        string decoded = Uri.UnescapeDataString(uri);
                        if (decoded.Contains('?') || decoded.Contains('#')) throw new InvalidDataException("glTF resource URI contains a query or fragment.");
                        string resource = Resolve(Path.GetDirectoryName(path)!, decoded);
                        if (!File.Exists(resource)) throw new InvalidDataException("glTF external resource is missing.");
                    }
    }
    /** Writes a value as JSON, creating the folder. */
    internal static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));
    }
}
