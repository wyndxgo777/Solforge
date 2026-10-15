using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SunriseStudio.Editor.Ai;

/**
 * Solforge Phase 47B: a Windows pseudo console (ConPTY, Windows 10 1809+) running one command line. Output
 * is decoded on a reader thread and handed to the screen; input bytes go straight to the console.
 */
internal sealed class ConPtyTerminal : IDisposable
{
    private readonly IntPtr console;
    private readonly SafeFileHandle input, output;
    private readonly FileStream writer;
    private readonly IntPtr process, thread;
    private readonly Thread reader;
    private bool disposed;

    public VtScreen Screen { get; }
    public string CommandLine { get; }
    public bool Exited { get; private set; }
    public event Action? Changed;

    /** @param environment Variables set or replaced for the child; the rest are inherited. */
    public ConPtyTerminal(string commandLine, string workingDirectory, int columns, int rows,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        CommandLine = commandLine;
        Screen = new VtScreen(columns, rows);
        Screen.Reply = Send;
        if (!CreatePipe(out var consoleInput, out input, IntPtr.Zero, 0) || !CreatePipe(out output, out var consoleOutput, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the terminal pipes.");
        int result = CreatePseudoConsole(new Coord((short)columns, (short)rows), consoleInput, consoleOutput, 0, out console);
        consoleInput.Dispose();
        consoleOutput.Dispose();
        if (result != 0) throw new Win32Exception(result, "Could not create the pseudo console (needs Windows 10 1809 or later).");

        var startup = new StartupInfoEx { StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>() } };
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        startup.lpAttributeList = Marshal.AllocHGlobal(size);
        IntPtr block = environment is null ? IntPtr.Zero : EnvironmentBlock(environment);
        try
        {
            if (!InitializeProcThreadAttributeList(startup.lpAttributeList, 1, 0, ref size)
                || !UpdateProcThreadAttribute(startup.lpAttributeList, 0, (IntPtr)PseudoConsoleAttribute, console, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach the pseudo console.");
            var command = new StringBuilder(commandLine);
            if (!CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    block, workingDirectory, ref startup, out var information))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start {commandLine}.");
            process = information.hProcess;
            thread = information.hThread;
        }
        finally
        {
            DeleteProcThreadAttributeList(startup.lpAttributeList);
            Marshal.FreeHGlobal(startup.lpAttributeList);
            if (block != IntPtr.Zero) Marshal.FreeHGlobal(block);
        }
        writer = new FileStream(input, FileAccess.Write, 1, false);
        reader = new Thread(Read) { IsBackground = true, Name = "Solforge AI terminal" };
        reader.Start();
    }

    /** This process's variables with the overrides, sorted as Windows expects, as a UTF-16 block. */
    private static IntPtr EnvironmentBlock(IReadOnlyDictionary<string, string> overrides)
    {
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && key.Length > 0) values[key] = entry.Value as string ?? "";
        foreach (var (key, value) in overrides) values[key] = value;
        var text = new StringBuilder();
        foreach (var (key, value) in values) text.Append(key).Append('=').Append(value).Append('\0');
        return Marshal.StringToHGlobalUni(text.Append('\0').ToString());
    }

    public void Send(string text)
    {
        if (disposed || Exited || text.Length == 0) return;
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            writer.Write(bytes);
            writer.Flush();
        }
        catch (IOException) { Exited = true; }
    }

    public void Resize(int columns, int rows)
    {
        if (disposed || columns < 2 || rows < 2 || columns == Screen.Columns && rows == Screen.Rows) return;
        Screen.Resize(columns, rows);
        _ = ResizePseudoConsole(console, new Coord((short)columns, (short)rows));
    }

    private void Read()
    {
        var buffer = new byte[16384];
        // Bytes carried over from the last read can decode into more chars than this read's bytes.
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length) + 4];
        var decoder = Encoding.UTF8.GetDecoder();
        using var stream = new FileStream(output, FileAccess.Read, 1, false);
        try
        {
            while (true)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                int count = decoder.GetChars(buffer, 0, read, chars, 0);
                // A parser fault must not kill the reader thread and with it the app.
                try { Screen.Feed(chars.AsSpan(0, count)); }
                catch (Exception error) when (error is IndexOutOfRangeException or ArgumentException) { }
                Changed?.Invoke();
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        Exited = true;
        Changed?.Invoke();
    }

    public bool HasExited => Exited || process != IntPtr.Zero && WaitForSingleObject(process, 0) == 0;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // Closing the console ends the client and breaks the reader's pipe.
        ClosePseudoConsole(console);
        if (process != IntPtr.Zero && WaitForSingleObject(process, 500) != 0) TerminateProcess(process, 1);
        writer.Dispose();
        CloseHandle(thread);
        CloseHandle(process);
    }

#pragma warning disable CS0649, CS0169 // Interop structs the OS fills in.
    private const int PseudoConsoleAttribute = 0x00020016;
    private const uint ExtendedStartupInfoPresent = 0x00080000, CreateUnicodeEnvironment = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Coord(short X, short Y);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

#pragma warning restore CS0649, CS0169
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);
    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr console, Coord size);
    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size,
        IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup,
        out ProcessInformation information);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
