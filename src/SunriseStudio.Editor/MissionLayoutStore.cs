using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SunriseStudio.Editor;

/** Node positions per script file. They stay in local settings, never in a script or a project. */
internal sealed class MissionLayoutStore(string directory)
{
    private sealed record Layout(string Script, Dictionary<string, float[]> Positions);

    /** Reads saved node positions for the script; an unreadable file yields none. */
    public Dictionary<string, Vector2> Load(string scriptPath)
    {
        string file = FileFor(scriptPath);
        if (!File.Exists(file)) return [];
        try
        {
            var layout = JsonSerializer.Deserialize<Layout>(File.ReadAllBytes(file));
            if (layout?.Positions is not { } positions) return [];
            return positions.Where(pair => pair.Value is { Length: 2 } && pair.Value.All(float.IsFinite))
                .ToDictionary(pair => pair.Key, pair => new Vector2(pair.Value[0], pair.Value[1]), StringComparer.Ordinal);
        }
        // A damaged layout only costs the saved positions; the graph lays itself out again.
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { return []; }
    }

    /** Writes node positions for the script. */
    public void Save(string scriptPath, IEnumerable<KeyValuePair<string, Vector2>> positions)
    {
        Directory.CreateDirectory(directory);
        var layout = new Layout(Path.GetFullPath(scriptPath), positions.ToDictionary(pair => pair.Key, pair => new[] { pair.Value.X, pair.Value.Y }));
        string file = FileFor(scriptPath), temporary = file + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(layout));
        File.Move(temporary, file, true);
    }

    private string FileFor(string scriptPath) => Path.Combine(directory, Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(scriptPath).ToUpperInvariant())))[..32] + ".json");
}
