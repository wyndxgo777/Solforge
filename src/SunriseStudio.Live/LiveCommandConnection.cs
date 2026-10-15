using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace SunriseStudio.Live;

/** Each request opens its own mapping and releases it after the reply, so the game drops the link. */
public sealed class LiveCommandConnection : IDisposable
{
    // The game answers only at a safe point in the mission.
    private const int ReplyTimeoutMilliseconds = 30000;
    // The game still holds the last mapping until it sees the zeroed heartbeat.
    private const int OpenTimeoutMilliseconds = 3000;
    private const int PollMilliseconds = 25;
    // Request ids do not repeat within one Studio run, even across mappings.
    private static int lastRequest;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly byte[] buffer = new byte[LiveCommandProtocol.MappingBytes];
    private readonly int processId;
    private MemoryMappedFile? mapping;
    private MemoryMappedViewAccessor? view;
    private int active;
    private bool disposed;

    public LiveCommandConnection(int processId) => this.processId = processId;

    /** Asks the game to reopen every binding whose controller comes from this mod. */
    public Task<LiveCommandReply> ReloadModAsync(string modId, CancellationToken cancellationToken) =>
        SendAsync(LiveCommandProtocol.ReloadModKind, LiveCommandProtocol.EncodeModId(modId), cancellationToken);

    /**
     * Writes kind and mod id before the request id, then keeps the heartbeat fresh until the reply.
     * @return The reply whose response id matches this request.
     */
    private async Task<LiveCommandReply> SendAsync(uint kind, byte[] modId, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            throw new InvalidOperationException("A game command is already running.");
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            using var process = GameProcess();
            long deadline = Environment.TickCount64 + ReplyTimeoutMilliseconds;
            await OpenAsync(cancellation.Token).ConfigureAwait(false);
            uint request;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellation.Token.ThrowIfCancellationRequested();
                request = unchecked((uint)Interlocked.Increment(ref lastRequest));
                if (request == 0) request = unchecked((uint)Interlocked.Increment(ref lastRequest));
                view!.Write(LiveCommandProtocol.KindOffset, kind);
                view.WriteArray(LiveCommandProtocol.ModIdOffset, modId, 0, modId.Length);
                Thread.MemoryBarrier();
                view.Write(LiveCommandProtocol.RequestOffset, request);
                Thread.MemoryBarrier();
            }
            while (Environment.TickCount64 < deadline)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("The game closed before it answered.");
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    view!.Write(LiveCommandProtocol.HeartbeatOffset, Environment.TickCount64);
                    if (view.ReadUInt32(LiveCommandProtocol.ResponseOffset) == request)
                    {
                        Thread.MemoryBarrier();
                        view.ReadArray(0, buffer, 0, buffer.Length);
                        if (LiveCommandProtocol.Read(buffer, (uint)processId, request) is { } reply) return reply;
                    }
                }
                await Task.Delay(PollMilliseconds, cancellation.Token).ConfigureAwait(false);
            }
            throw new TimeoutException("The game did not answer within 30 seconds.");
        }
        finally
        {
            lock (gate) Close();
            Volatile.Write(ref active, 0);
        }
    }

    private Process GameProcess()
    {
        try { return Process.GetProcessById(processId); }
        catch (ArgumentException error) { throw new InvalidOperationException("The game is not running.", error); }
    }

    /** CreateNew keeps one editor from replacing another editor's request; the header is set before the magic. */
    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        long deadline = Environment.TickCount64 + OpenTimeoutMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    cancellationToken.ThrowIfCancellationRequested();
                    mapping = MemoryMappedFile.CreateOrOpen(LiveCommandProtocol.MappingName(processId), LiveCommandProtocol.MappingBytes);
                    view = mapping.CreateViewAccessor(0, LiveCommandProtocol.MappingBytes, MemoryMappedFileAccess.ReadWrite);
                    view.Write(4, LiveCommandProtocol.Version);
                    view.Write(8, LiveCommandProtocol.MappingBytes);
                    view.Write(12, Environment.ProcessId);
                    view.Write(LiveCommandProtocol.HeartbeatOffset, Environment.TickCount64);
                    Thread.MemoryBarrier();
                    view.Write(0, LiveCommandProtocol.Magic);
                    return;
                }
            }
            catch (IOException error)
            {
                lock (gate) Close();
                if (Environment.TickCount64 >= deadline)
                    throw new IOException("The game command link is in use. Try again.", error);
            }
            await Task.Delay(PollMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }

    /** Callers hold the gate. A zero heartbeat tells the game to drop the link. */
    private void Close()
    {
        if (view is not null) { view.Write(LiveCommandProtocol.HeartbeatOffset, 0L); view.Dispose(); view = null; }
        mapping?.Dispose();
        mapping = null;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            Close();
            lifetime.Dispose();
        }
    }
}
