/**
 * File: Windows pseudo-console (ConPTY) host for the AI terminal: starts a child process attached to a pseudo console
 * and exchanges text with it. Shown by TerminalView.cs, parsed by VtScreen.cs.
 */
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SunriseStudio.Editor.Ai;

/**
 * Solforge Phase 47B: a Windows pseudo console (ConPTY, Windows 10 1809+) running one command line. Output
 * is decoded on a reader thread and handed to the screen; input bytes queue for a writer thread, so a large paste
 * the child reads slowly never blocks the UI.
 */
internal sealed class ConPtyTerminal : IDisposable
{
    private readonly IntPtr console;
    private readonly SafeFileHandle input, output;
    private readonly FileStream writer;
    private readonly IntPtr process, thread;
    private readonly Thread reader, sender;
    /** Input waiting for the writer thread, in order. */
    private readonly BlockingCollection<byte[]> pending = new();
    private bool disposed;

    public VtScreen Screen { get; }
    public string CommandLine { get; }
    public bool Exited { get; private set; }
    public int ProcessId { get; }
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
            ProcessId = information.dwProcessId;
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
        sender = new Thread(Write) { IsBackground = true, Name = "Solforge AI terminal input" };
        sender.Start();
    }

    /** This process's variables with the overrides, sorted as Windows expects, as a UTF-16 block. */
    private static IntPtr EnvironmentBlock(IReadOnlyDictionary<string, string> overrides)
    {
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && key.Length > 0) values[key] = entry.Value as string ?? "";
        // An empty override removes the variable (e.g. keeps an API key out of a subscription run).
        foreach (var (key, value) in overrides)
            if (value.Length == 0) values.Remove(key);
            else values[key] = value;
        var text = new StringBuilder();
        foreach (var (key, value) in values) text.Append(key).Append('=').Append(value).Append('\0');
        return Marshal.StringToHGlobalUni(text.Append('\0').ToString());
    }

    /** Queues text for the child process input; returns at once, the writer thread blocks while the pipe is full. */
    public void Send(string text)
    {
        if (disposed || Exited || text.Length == 0) return;
        try { pending.Add(Encoding.UTF8.GetBytes(text)); }
        catch (InvalidOperationException) { }
    }

    /** Writer thread: sends queued input until the terminal is disposed or the pipe breaks. */
    private void Write()
    {
        try
        {
            foreach (byte[] bytes in pending.GetConsumingEnumerable())
            {
                // Chunks keep a huge paste from holding one write call while the console drains it.
                for (int at = 0; at < bytes.Length; at += 4096)
                {
                    writer.Write(bytes, at, Math.Min(4096, bytes.Length - at));
                    writer.Flush();
                }
            }
        }
        catch (IOException) { Exited = true; }
        catch (ObjectDisposedException) { }
    }

    /** Resizes the pseudo console. */
    public void Resize(int columns, int rows)
    {
        if (disposed || columns < 2 || rows < 2 || columns == Screen.Columns && rows == Screen.Rows) return;
        Screen.Resize(columns, rows);
        _ = ResizePseudoConsole(console, new Coord((short)columns, (short)rows));
    }

    /** Reads child output on a background thread and feeds it to the screen. */
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

    /**
     * Ends the client and every process it started (node, shells, stdio MCP servers), then closes the console. The tree
     * is killed before the console closes, because Windows does not re-parent orphans and a dead root hides its children;
     * the open process handle keeps the id from being reused meanwhile.
     */
    public void KillTree()
    {
        if (disposed) return;
        try
        {
            using var root = Process.GetProcessById(ProcessId);
            root.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception
            or AggregateException or NotSupportedException) { }
        Dispose();
    }

    /** Closes the console and terminates the child process. */
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Exited = true;
        pending.CompleteAdding();
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

    /** Windows COORD (column, row). */
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Coord(short X, short Y);

    /** Native STARTUPINFO. */
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    /** Native STARTUPINFOEX. */
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    /** Native PROCESS_INFORMATION. */
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
