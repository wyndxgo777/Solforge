using SunriseStudio.Formats;

namespace SunriseStudio.Editor;

/** Remembers icon problems by key; Validate runs every frame and a PNG decode is slow. */
internal static class IconChecks
{
    private const int MaximumEntries = 32;
    /** A file is stat'ed at most this often, in milliseconds. */
    private const int StampInterval = 500;
    private static readonly Dictionary<string, string?> Problems = [];
    private static readonly Dictionary<string, (long Next, string Stamp, bool Exists)> Stamps = [];

    /** A key that changes when the file is replaced or rewritten. */
    public static string Stamp(string path) => Read(path).Stamp;

    /** File.Exists, cached with the stamp. */
    public static bool Exists(string path) => Read(path).Exists;

    private static (long Next, string Stamp, bool Exists) Read(string path)
    {
        long now = Environment.TickCount64;
        if (Stamps.TryGetValue(path, out var cached) && now < cached.Next) return cached;
        if (Stamps.Count >= MaximumEntries) Stamps.Clear();
        (string stamp, bool exists) = (path, false);
        try
        {
            var info = new FileInfo(path);
            if (info.Exists) (stamp, exists) = ($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}", true);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException) { }
        return Stamps[path] = (now + StampInterval, stamp, exists);
    }

    /** Forgets every remembered problem, so a Retry after a failed read checks again. */
    public static void Forget() => Problems.Clear();

    /** Runs the check only when the key is new; the key must name every input of the check. */
    public static string? Cached(string key, Func<string?> check)
    {
        if (Problems.TryGetValue(key, out string? problem)) return problem;
        if (Problems.Count >= MaximumEntries) Problems.Clear();
        return Problems[key] = check();
    }

    /** A problem when the PNG cannot be read or is not width x height. */
    public static string? Size(string path, int width, int height) => Cached($"size|{width}|{height}|{Stamp(path)}", () =>
    {
        try
        {
            var image = PngInterchange.Read(path);
            return image.Width == width && image.Height == height ? null : $"Use a {width} x {height} PNG.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or NotSupportedException or ArgumentException or OverflowException) { return error.Message; }
    });

    /** A symbol needs a shape in its alpha channel on a transparent background. */
    public static string? Symbol(string path) => Cached("symbol|" + Stamp(path), () =>
    {
        try
        {
            var image = PngInterchange.Read(path);
            int pixels = image.Width * image.Height, alpha = 0;
            for (int i = 0; i < pixels; i++) if (image.Pixels[i * 4 + 3] > 8) alpha++;
            return alpha == 0 ? "The symbol is fully transparent."
                : alpha == pixels ? "Use a symbol on a transparent background." : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or NotSupportedException or ArgumentException or OverflowException) { return error.Message; }
    });
}
