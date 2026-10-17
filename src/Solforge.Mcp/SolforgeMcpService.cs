using System.Diagnostics;

namespace Solforge.Mcp;

/** The running server for one Solforge process: dispatcher, HTTP listener, discovery file and client configs. */
public sealed partial class SolforgeMcpService : IAsyncDisposable
{
    private readonly McpHttpServer server;

    private SolforgeMcpService(McpHttpServer server, McpDispatcher dispatcher)
    {
        this.server = server;
        Dispatcher = dispatcher;
    }

    public McpDispatcher Dispatcher { get; }
    /** Connected MCP sessions, the map claim and running work (the editor's presence list reads it). */
    public McpSessionRegistry? Sessions => server.Sessions;
    public string Url => server.Url;
    public int Port => server.Port;
    public int Requests => server.RequestCount;
    public string? DiscoveryPath { get; private set; }
    public IReadOnlyList<McpClientConfigs.Written> Configs { get; private set; } = [];

    /** Starts the HTTP server, registers the tools and writes the discovery file. */
    public static SolforgeMcpService Start(ISolforgeHost host, string version)
    {
        // Local server tools (LocalServer/LocalServerTools.cs) sit beside the editor tools; they never reach the VPS.
        var localServer = LocalServer.LocalServerTools.Create(LocalServer.LocalServerManager.CreateDefault(), () => host.ProjectRoot, host.ApproveAsync);
        // get_client_build, testkit_status and testkit_restart live in ClientProvenance/ and Testkit/.
        // Several agents can share this Solforge (MultiAgentTools.cs): sessions, the open_map guard and map-scoped reads.
        var sessions = new McpSessionRegistry();
        var dispatcher = new McpDispatcher(MultiAgentTools.Wrap([.. SolforgeTools.Create(host), .. localServer, .. ClientProvenance.ClientBuildTools.Create(),
            .. Testkit.TestkitTools.Create(Testkit.TestkitMonitor.CreateDefault(), host.ApproveAsync)], host as ISolforgePresence, sessions), version,
            SolforgeTools.Instructions + LocalServer.LocalServerTools.Instructions + ClientProvenance.ClientBuildTools.Instructions + Testkit.TestkitTools.Instructions + MultiAgentTools.Instructions);
        dispatcher.OnToolCall = tool => host.Automatic?.NoteToolCall(tool);
        var server = new McpHttpServer(dispatcher) { Viewport = host.Viewport, Sessions = sessions };
        ConnectOwnerFeatures(server, host);
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
    public IReadOnlyList<McpClientConfigs.Written> WriteClientConfigs(string projectRoot, string shimPath,
        IReadOnlyList<SolMcpServers.Server>? servers = null) =>
        Configs = McpClientConfigs.WriteAll(projectRoot, shimPath, servers: servers);

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

    /** Owner builds only (RELEASING.md): wires their extra server features to the host; public builds have none. */
    static partial void ConnectOwnerFeatures(McpHttpServer server, ISolforgeHost host);
    /** Owner builds only: stops what ConnectOwnerFeatures or their tools started. */
    static partial void StopOwnerFeatures(ViewportFeed feed);

    /** Removes the discovery file and stops the server. */
    public async ValueTask DisposeAsync()
    {
        McpDiscovery.Remove(Environment.ProcessId);
        if (server.Viewport is { } feed) { StopOwnerFeatures(feed); feed.LocalUrl = null; }
        await server.DisposeAsync().ConfigureAwait(false);
    }

    /** Debug-output trace line. */
    internal static void Trace(string text) => Debug.WriteLine("[solforge-mcp] " + text);
}
