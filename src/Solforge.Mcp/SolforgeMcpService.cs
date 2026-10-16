using System.Diagnostics;

namespace Solforge.Mcp;

/** The running server for one Solforge process: dispatcher, HTTP listener, discovery file and client configs. */
public sealed class SolforgeMcpService : IAsyncDisposable
{
    private readonly McpHttpServer server;

    private SolforgeMcpService(McpHttpServer server, McpDispatcher dispatcher)
    {
        this.server = server;
        Dispatcher = dispatcher;
    }

    public McpDispatcher Dispatcher { get; }
    public string Url => server.Url;
    public int Port => server.Port;
    public int Requests => server.RequestCount;
    public string? DiscoveryPath { get; private set; }
    public IReadOnlyList<McpClientConfigs.Written> Configs { get; private set; } = [];

    public static SolforgeMcpService Start(ISolforgeHost host, string version)
    {
        // Local server tools (LocalServer/LocalServerTools.cs) sit beside the editor tools; they never reach the VPS.
        var localServer = LocalServer.LocalServerTools.Create(LocalServer.LocalServerManager.CreateDefault(), () => host.ProjectRoot, host.ApproveAsync);
        var dispatcher = new McpDispatcher([.. SolforgeTools.Create(host), .. localServer], version,
            SolforgeTools.Instructions + LocalServer.LocalServerTools.Instructions);
        var server = new McpHttpServer(dispatcher) { Viewport = host.Viewport };
        server.Start();
        host.Viewport.LocalUrl = server.ViewportUrl;
        McpDiscovery.CleanStale();
        var service = new SolforgeMcpService(server, dispatcher);
        service.Publish(host.ProjectRoot);
        return service;
    }

    /** Points the discovery file at the open project; called again when another project opens. */
    public void Publish(string? project) =>
        DiscoveryPath = McpDiscovery.Write(new McpDiscoveryEntry(Environment.ProcessId, server.Url, server.Token, project, DateTimeOffset.UtcNow));

    /** Writes .mcp.json and the Cline, Antigravity desktop and Antigravity CLI settings for the project; null shim means it was not found. */
    public IReadOnlyList<McpClientConfigs.Written> WriteClientConfigs(string projectRoot, string shimPath) =>
        Configs = McpClientConfigs.WriteAll(projectRoot, shimPath);

    /** solforge-mcp.exe beside the running exe, else the developer build of the shim project. */
    public static string? FindShim()
    {
        string here = AppContext.BaseDirectory;
        string beside = Path.Combine(here, "solforge-mcp.exe");
        if (File.Exists(beside)) return beside;
        for (var folder = new DirectoryInfo(here); folder is not null; folder = folder.Parent)
        {
            string project = Path.Combine(folder.FullName, "src", "Solforge.Mcp.Shim", "bin");
            if (!Directory.Exists(project)) continue;
            return Directory.EnumerateFiles(project, "solforge-mcp.exe", SearchOption.AllDirectories)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path)).FirstOrDefault();
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        McpDiscovery.Remove(Environment.ProcessId);
        if (server.Viewport is { } feed) { feed.Relay?.Stop(); feed.LocalUrl = null; }
        await server.DisposeAsync().ConfigureAwait(false);
    }

    internal static void Trace(string text) => Debug.WriteLine("[solforge-mcp] " + text);
}
