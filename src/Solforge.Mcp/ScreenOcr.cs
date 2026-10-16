/** File: Screen text reading for read_screen_text via Windows OCR through the ScreenOcr.ps1 helper process. */
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * Reads the text on a frame or screenshot with Windows.Media.Ocr, the OCR engine built into Windows 10/11 (no download,
 * no GPU). Windows PowerShell 5.1 projects WinRT by itself, so the embedded ScreenOcr.ps1 runs as one long-lived helper
 * process that takes a JSON request per line and answers a JSON line; this assembly stays plain net9.0 with no Windows
 * SDK reference. Replies give lines and words with boxes in the original image's pixels, so a region and the upscale
 * the helper applies to small text never show in the coordinates.
 */
public sealed class ScreenOcr : IDisposable
{
    /** The shared instance used by the tools. */
    public static ScreenOcr Shared { get; } = new();

    /** Serialises requests so only one is in flight. */
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private string lastError = "";

    /** How long one recognition may take before the helper is restarted (its first start loads WinRT, about a second). */
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /** Windows PowerShell 5.1 (the one with WinRT projection); null off Windows or when it was removed. */
    public static string? PowerShellPath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(path) ? path : null;
    }

    public static bool Available => PowerShellPath() is not null;

    /** The helper script, embedded in this assembly. */
    public static string ScriptText()
    {
        using var stream = typeof(ScreenOcr).Assembly.GetManifestResourceStream("Solforge.Mcp.ScreenOcr.ps1")
            ?? throw new InvalidOperationException("ScreenOcr.ps1 is not embedded in Solforge.Mcp.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /** OCR of an in-memory PNG or JPEG (a viewport frame or screenshot). */
    public async Task<JsonObject> ReadAsync(byte[] image, int[]? region, double scale, string? lang, CancellationToken cancel)
    {
        string folder = Path.Combine(Path.GetTempPath(), "solforge-ocr");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, Guid.NewGuid().ToString("N") + (McpToolResult.MimeType(image) == "image/jpeg" ? ".jpg" : ".png"));
        await File.WriteAllBytesAsync(file, image, cancel).ConfigureAwait(false);
        try { return await ReadFileAsync(file, region, scale, lang, cancel).ConfigureAwait(false); }
        finally
        {
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /** OCR of an image file. region is [x, y, w, h] in image pixels; scale 0 lets the helper pick (2x up to 1280 px, else 1x). */
    public async Task<JsonObject> ReadFileAsync(string path, int[]? region, double scale, string? lang, CancellationToken cancel)
    {
        if (PowerShellPath() is null) return Failure("Screen OCR needs Windows (Windows PowerShell 5.1 and Windows.Media.Ocr).");
        string request = Request(path, region, scale, lang);
        await gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var helper = Start();
                    await helper.StandardInput.WriteLineAsync(request).ConfigureAwait(false);
                    await helper.StandardInput.FlushAsync(cancel).ConfigureAwait(false);
                    string? line = await helper.StandardOutput.ReadLineAsync(cancel).AsTask().WaitAsync(Timeout, cancel).ConfigureAwait(false);
                    if (line is null) throw new IOException("The OCR helper exited. " + lastError);
                    return Parse(line);
                }
                catch (OperationCanceledException)
                {
                    Stop(); // its reply would answer the next request
                    throw;
                }
                catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException
                    or System.ComponentModel.Win32Exception)
                {
                    Stop();
                    if (attempt >= 1) return Failure("Screen OCR failed: " + error.Message);
                }
            }
        }
        finally { gate.Release(); }
    }

    /** One request line for the helper. */
    public static string Request(string path, int[]? region, double scale, string? lang)
    {
        var request = new JsonObject { ["path"] = Path.GetFullPath(path) };
        if (region is { Length: 4 }) request["region"] = new JsonArray(region.Select(value => (JsonNode)value).ToArray());
        if (scale > 0) request["scale"] = Math.Clamp(scale, 0.25, 8);
        if (!string.IsNullOrWhiteSpace(lang)) request["lang"] = lang.Trim();
        return request.ToJsonString();
    }

    /** A helper reply line as JSON; anything else becomes ok:false. */
    public static JsonObject Parse(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is JsonObject reply && reply["ok"] is JsonValue) return reply;
        }
        catch (JsonException) { }
        return Failure("Unexpected OCR helper output: " + (line.Length > 200 ? line[..200] : line));
    }

    /** Builds a failed reply with an error message. */
    public static JsonObject Failure(string message) => new() { ["ok"] = false, ["error"] = message };

    /** Whether a reply reports success. */
    public static bool Ok(JsonObject reply) => reply["ok"] is JsonValue value && value.TryGetValue(out bool ok) && ok;

    /**
     * The tool view of a reply: text, then lines (text + box), with words only when asked. contains keeps the lines
     * holding that text (case-insensitive), for "where is the Launch button".
     */
    public static JsonObject Summarize(JsonObject reply, bool words, string? contains)
    {
        if (!Ok(reply)) return new JsonObject { ["ok"] = false, ["error"] = reply["error"]?.DeepClone() };
        var lines = (reply["lines"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(line => string.IsNullOrEmpty(contains) || (line["text"]?.ToString() ?? "").Contains(contains, StringComparison.OrdinalIgnoreCase))
            .Select(line =>
            {
                var row = new JsonObject { ["text"] = line["text"]?.DeepClone(), ["box"] = line["box"]?.DeepClone() };
                if (words) row["words"] = line["words"]?.DeepClone();
                return (JsonNode)row;
            }).ToArray();
        var summary = new JsonObject
        {
            ["text"] = string.IsNullOrEmpty(contains) ? reply["text"]?.DeepClone() : string.Join("\n", lines.Select(line => line!["text"]?.ToString())),
            ["lineCount"] = lines.Length,
            ["lines"] = new JsonArray(lines),
            ["image"] = new JsonArray(reply["width"]?.DeepClone(), reply["height"]?.DeepClone()),
            ["region"] = reply["region"]?.DeepClone(),
            ["scale"] = reply["scale"]?.DeepClone(),
            ["lang"] = reply["lang"]?.DeepClone(),
            ["boxes"] = "[x, y, width, height] in image pixels"
        };
        if (!string.IsNullOrEmpty(contains)) summary["contains"] = contains;
        return summary;
    }

    /** region as [x, y, w, h] (array or "x,y,w,h"); null when absent. error is set for a malformed one. */
    public static int[]? ParseRegion(JsonNode? node, out string? error)
    {
        error = null;
        if (node is null) return null;
        double[] values = node is JsonArray array
            ? array.Select(item => item is JsonValue value && value.GetValueKind() == JsonValueKind.Number
                && double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                    out double number) ? number : double.NaN).ToArray()
            : node.ToString().Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                    out double number) ? number : double.NaN).ToArray();
        if (values.Length != 4 || values.Any(double.IsNaN) || values[0] < 0 || values[1] < 0 || values[2] < 1 || values[3] < 1)
        {
            error = "region is [x, y, width, height] in image pixels, with x, y >= 0 and width, height >= 1.";
            return null;
        }
        return values.Select(value => (int)Math.Round(value)).ToArray();
    }

    /** Starts the OCR process if it is not running and returns it. */
    private Process Start()
    {
        if (process is { HasExited: false }) return process;
        Stop();
        string script = ScriptText();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)))[..12];
        string folder = Path.Combine(Path.GetTempPath(), "solforge-ocr");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"ScreenOcr-{hash}.ps1");
        // A BOM keeps Windows PowerShell 5.1 reading the script as UTF-8.
        if (!File.Exists(file)) File.WriteAllText(file, script, new UTF8Encoding(true));
        var start = new ProcessStartInfo(PowerShellPath()!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false), StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file, "-Serve" })
            start.ArgumentList.Add(argument);
        var started = Process.Start(start) ?? throw new InvalidOperationException("Could not start Windows PowerShell for OCR.");
        started.ErrorDataReceived += (_, line) => { if (!string.IsNullOrWhiteSpace(line.Data)) lastError = line.Data; };
        started.BeginErrorReadLine();
        process = started;
        return started;
    }

    /** Kills the OCR process. */
    private void Stop()
    {
        var old = process;
        process = null;
        if (old is null) return;
        try
        {
            if (!old.HasExited) old.Kill(true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        old.Dispose();
    }

    /** Stops the process and releases the request gate. */
    public void Dispose()
    {
        Stop();
        gate.Dispose();
    }
}
