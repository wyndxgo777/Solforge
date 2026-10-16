using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Solforge.Mcp.LocalServer;

/**
 * Where the local WSL sol-server lives. Local only: every command runs through wsl.exe on this PC and every
 * admin call goes to 127.0.0.1 inside WSL, so nothing here can reach the VPS (its /opt/sol-server layout is refused).
 */
public sealed record LocalServerOptions
{
    public string Distro { get; init; } = "";
    public string RunDirectory { get; init; } = "/root/sol-local/run";
    public string Executable { get; init; } = "sol-server";
    public string BackupDirectory { get; init; } = "/root/sol-local/solforge-backups";
    /** BAP / activity host (UDP and TCP), the public web API, the loopback admin API. */
    public int BapPort { get; init; } = 30974;
    public int WebPort { get; init; } = 8098;
    public int AdminPort { get; init; } = 8099;
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan SelftestTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public string SunriseDirectory => RunDirectory + "/Sunrise";
    public string LogPath => RunDirectory + "/stdout.log";

    private static readonly Regex SafePath = new("^/[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeName = new("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant);

    /** SOLFORGE_LOCAL_SERVER_DIR, SOLFORGE_LOCAL_SERVER_BACKUPS and SOLFORGE_WSL_DISTRO override the defaults. */
    public static LocalServerOptions FromEnvironment()
    {
        var options = new LocalServerOptions();
        if (Environment.GetEnvironmentVariable("SOLFORGE_LOCAL_SERVER_DIR") is { Length: > 0 } run) options = options with { RunDirectory = run.TrimEnd('/') };
        if (Environment.GetEnvironmentVariable("SOLFORGE_LOCAL_SERVER_BACKUPS") is { Length: > 0 } backups) options = options with { BackupDirectory = backups.TrimEnd('/') };
        if (Environment.GetEnvironmentVariable("SOLFORGE_WSL_DISTRO") is { Length: > 0 } distro) options = options with { Distro = distro };
        return options;
    }

    /** Null when the options are safe to put in a script; otherwise why not. */
    public string? Problem()
    {
        foreach (var (name, path) in new[] { ("run directory", RunDirectory), ("backup directory", BackupDirectory) })
        {
            if (!SafePath.IsMatch(path) || path.Contains("..", StringComparison.Ordinal) || path == "/")
                return $"The {name} '{path}' must be an absolute WSL path of letters, digits, '.', '_', '-' and '/'.";
            if (path.StartsWith("/opt/sol-server", StringComparison.Ordinal))
                return $"The {name} '{path}' is the VPS layout; the local server manager only runs the local WSL server.";
        }
        if (BackupDirectory.StartsWith(RunDirectory + "/", StringComparison.Ordinal))
            return "Keep backups outside the run directory so a deploy never backs up its own backups.";
        if (!SafeName.IsMatch(Executable)) return $"Executable '{Executable}' must be a plain file name.";
        if (Distro.Length > 0 && !SafeName.IsMatch(Distro)) return $"WSL distro '{Distro}' must be a plain name.";
        return null;
    }
}

public sealed record ShellResult(int ExitCode, string Output, string Error, bool TimedOut)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
}

/** Runs a bash script inside WSL. Tests swap in a fake. */
public interface IWslShell
{
    Task<ShellResult> RunAsync(string script, TimeSpan timeout, CancellationToken cancel);
}

/** wsl.exe -u root -e bash -l -s with the script on stdin, so nothing goes through Windows argument quoting. */
public sealed class WslShell(string distro = "") : IWslShell
{
    public async Task<ShellResult> RunAsync(string script, TimeSpan timeout, CancellationToken cancel)
    {
        var start = new ProcessStartInfo("wsl.exe")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (distro.Length > 0) { start.ArgumentList.Add("-d"); start.ArgumentList.Add(distro); }
        foreach (string arg in new[] { "-u", "root", "-e", "bash", "-l", "-s" }) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception error) { return new ShellResult(-1, "", "wsl.exe could not start: " + error.Message, false); }
        var output = process.StandardOutput.ReadToEndAsync(cancel);
        var errors = process.StandardError.ReadToEndAsync(cancel);
        await process.StandardInput.WriteAsync(script.Replace("\r\n", "\n").AsMemory(), cancel).ConfigureAwait(false);
        process.StandardInput.Close();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        bool timedOut = false;
        try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            timedOut = !cancel.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (!timedOut) throw;
        }
        string text = "", error2 = "";
        try { text = await output.ConfigureAwait(false); error2 = await errors.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        return new ShellResult(timedOut ? -1 : process.ExitCode, text, error2, timedOut);
    }
}

public static class Shell
{
    /** One bash single-quoted word. */
    public static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /** C:\Users\x -> /mnt/c/Users/x; null for UNC, \\wsl$ or relative paths. */
    public static string? WslPath(string windowsPath)
    {
        string full;
        try { full = Path.GetFullPath(windowsPath); } catch (Exception) { return null; }
        if (full.Length < 3 || full[1] != ':' || !char.IsAsciiLetter(full[0]) || (full[2] != '\\' && full[2] != '/')) return null;
        return "/mnt/" + char.ToLowerInvariant(full[0]) + "/" + full[3..].Replace('\\', '/').TrimEnd('/');
    }
}
