using System.Diagnostics;

namespace SunriseStudio.Packages;

/**
 * A package drive that briefly drops out, such as a disk behind a junction, fails reads for a few seconds.
 * Package reads retry those device errors with backoff before they report one.
 */
public static class PackageIo
{
    private const int NotReady = unchecked((int)0x80070015), GeneralFailure = unchecked((int)0x8007001F),
        NoDevice = unchecked((int)0x80070037), SemaphoreTimeout = unchecked((int)0x80070079),
        NotConnected = unchecked((int)0x8007048F);

    /** Total time a read keeps retrying a device error. */
    internal static TimeSpan RetryBudget { get; set; } = TimeSpan.FromSeconds(5);

    /** True for a drive that is not ready, gone or timing out, here or in an inner exception. */
    public static bool IsDeviceError(Exception? error)
    {
        for (; error is not null; error = error.InnerException)
            if (error is IOException { HResult: NotReady or GeneralFailure or NoDevice or SemaphoreTimeout or NotConnected })
                return true;
        return false;
    }

    /**
     * Waits before the next attempt after a device error.
     * @param started Timestamp of the first failure; zero sets it.
     * @return False once the retry budget is spent.
     */
    internal static bool Backoff(ref long started, int attempt)
    {
        if (started == 0) started = Stopwatch.GetTimestamp();
        var delay = TimeSpan.FromMilliseconds(Math.Min(50 << Math.Min(attempt, 5), 1000));
        if (Stopwatch.GetElapsedTime(started) + delay > RetryBudget) return false;
        Thread.Sleep(delay);
        return true;
    }

    /**
     * Waits out a folder whose drive is not ready; a folder that is simply missing returns at once.
     * @return True when the folder exists.
     */
    internal static bool WaitForFolder(string folder)
    {
        string drive = "";
        long started = 0;
        for (int attempt = 0; !Directory.Exists(folder); attempt++)
        {
            if (attempt == 0) drive = DriveOf(folder);
            if (!DriveNotReady(drive)) return false;
            if (!Backoff(ref started, attempt))
                throw new PackageDeviceException(folder, new IOException("The device is not ready.", NotReady));
        }
        return true;
    }

    private static bool DriveNotReady(string drive)
    {
        try { return drive.Length == 2 && drive[1] == ':' && !new DriveInfo(drive).IsReady; }
        catch (ArgumentException) { return false; }
    }

    /** The drive that holds a path, such as "E:", following a junction or link in the path itself or any folder above it. */
    public static string DriveOf(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            string? target;
            try { target = new DirectoryInfo(current).LinkTarget; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            if (target is null) continue;
            try { full = Path.GetFullPath(target, Path.GetDirectoryName(current) ?? current); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { }
            break;
        }
        return (Path.GetPathRoot(full) ?? "").TrimEnd('\\', '/');
    }
}

/** A device error that outlasted the retries; the message names the drive. HResult is the original error's. */
public sealed class PackageDeviceException : IOException
{
    public string Path { get; }
    /** The drive behind Path, such as "E:"; empty when it cannot be told. */
    public string Drive { get; }

    public PackageDeviceException(string path, IOException error) : this(path, PackageIo.DriveOf(path), error) { }

    private PackageDeviceException(string path, string drive, IOException error)
        : base((drive.Length == 0 ? "The drive with the game packages is not ready."
            : $"Drive {drive} is not ready (the game packages live there).") + " Reconnect it and press Retry.", error)
    {
        Path = path; Drive = drive; HResult = error.HResult;
    }
}
