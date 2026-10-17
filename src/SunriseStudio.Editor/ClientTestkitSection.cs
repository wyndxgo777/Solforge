/**
 * File: the "Client" tab of the Local Server panel: which source tree built the installed client DLL, and the
 * sol-testkit MCP health light with check, restart and start-HTTP buttons. Backed by Solforge.Mcp ClientProvenance and
 * Testkit (the same code as the get_client_build, testkit_status and testkit_restart MCP tools).
 */
using System.Numerics;
using ImGuiNET;
using Solforge.Mcp.ClientProvenance;
using Solforge.Mcp.Testkit;

namespace SunriseStudio.Editor;

/** Draws the client build provenance and testkit health; all work runs off the UI thread and Draw only reads results. */
public sealed class ClientTestkitSection
{
    private static readonly Vector4 Good = new(.45f, .85f, .45f, 1), Bad = new(.95f, .4f, .35f, 1), Warn = new(.95f, .8f, .3f, 1);

    private readonly TestkitMonitor testkit = TestkitMonitor.CreateDefault();
    private ClientBuildReport? build;
    private TestkitStatus? health;
    private string message = "";
    private volatile bool buildBusy, healthBusy, restartBusy;
    private readonly System.Diagnostics.Stopwatch buildClock = new(), healthClock = new();

    /** The testkit light colour and tip for the tab header; polls every 15 s while the page is drawn. */
    public (Vector4 Color, string Tip) Dot()
    {
        PollHealth();
        var current = health;
        if (current is null) return (new(.6f, .6f, .6f, 1), "Testkit: checking");
        return (current.Light switch { "green" => Good, "amber" => Warn, _ => Bad }, "Testkit: " + current.Summary);
    }

    /** Draws both sections. */
    public void Draw()
    {
        if (!buildClock.IsRunning) { buildClock.Restart(); RefreshBuild(); }
        PollHealth();
        DrawBuild();
        ImGui.Separator();
        DrawTestkit();
    }

    private void PollHealth()
    {
        if (!healthClock.IsRunning || (healthClock.Elapsed > TimeSpan.FromSeconds(15) && !healthBusy)) { healthClock.Restart(); RefreshHealth(); }
    }

    private void RefreshBuild()
    {
        if (buildBusy) return;
        buildBusy = true;
        _ = Task.Run(() =>
        {
            try { build = ClientBuildInfo.Inspect(ClientBuildInfo.DefaultDll(), ClientBuildInfo.DefaultTrees()); }
            finally { buildBusy = false; }
        });
    }

    private void RefreshHealth()
    {
        if (healthBusy) return;
        healthBusy = true;
        _ = Task.Run(async () =>
        {
            try { health = await testkit.StatusAsync().ConfigureAwait(false); }
            finally { healthBusy = false; }
        });
    }

    private void Restart(string mode)
    {
        if (restartBusy) return;
        restartBusy = true;
        _ = Task.Run(async () =>
        {
            try { message = (await testkit.RestartAsync(mode).ConfigureAwait(false)).Message; }
            finally { restartBusy = false; RefreshHealth(); }
        });
    }

    private void DrawBuild()
    {
        ImGui.TextUnformatted("Installed client");
        ImGui.SameLine();
        if (UiAuto.SmallButton("Recheck##build")) RefreshBuild();
        var report = build;
        if (report is null) { ImGui.TextDisabled(buildBusy ? "reading the DLL and hashing build outputs..." : "no result yet"); return; }
        ImGui.TextDisabled(report.Dll);
        if (!report.Exists) { ImGui.TextColored(Bad, report.Summary); return; }
        ImGui.TextUnformatted($"Build {report.Build?.ToString() ?? "?"}  (version {report.Version ?? "none"}), {report.Modified?.LocalDateTime:yyyy-MM-dd HH:mm}, {report.Size / 1048576} MB");
        var color = report.Confidence switch { "exact" => Good, "likely" => Good, "weak" => Warn, _ => Bad };
        ImGui.TextColored(color, report.Best is null ? "Source tree: unknown" : $"Source tree: {report.Best.Tree.Name} ({report.Confidence})");
        if (report.Best is { } best)
        {
            ImGui.TextDisabled(best.Tree.Root);
            var git = best.Git;
            if (git.Error is not null) ImGui.TextColored(Warn, "git: " + git.Error);
            else
            {
                ImGui.TextUnformatted($"Branch {git.Branch}  HEAD {git.Head}");
                ImGui.TextColored(git.DirtyFiles == 0 ? Good : Warn, $"{git.DirtyFiles} dirty file(s), {git.ChangedAfterBuild} changed after this build");
            }
            ImGui.TextWrapped(best.Reason);
        }
        if (ImGui.TreeNode("All trees compared##trees"))
        {
            foreach (var row in report.Trees)
                ImGui.TextUnformatted($"{row.Tree.Name}: score {row.Score}, build_number {row.BuildNumber?.ToString() ?? "-"}, {row.Git.Branch ?? "no git"} {row.Git.Head}");
            ImGui.TreePop();
        }
    }

    private void DrawTestkit()
    {
        ImGui.TextUnformatted("sol-testkit MCP");
        var current = health;
        if (current is null) ImGui.TextDisabled(healthBusy ? "checking..." : "no result yet");
        else
        {
            ImGui.TextColored(current.Light switch { "green" => Good, "amber" => Warn, _ => Bad }, "(o) " + current.Light);
            ImGui.SameLine();
            ImGui.TextWrapped(current.Summary);
            ImGui.TextDisabled($"instances {current.Instances}, handshake {current.HandshakeDetail ?? "not run"}, pad port {(current.PadUp ? "up" : "off")}, live port {(current.LiveUp ? "up" : "off")}");
            foreach (var row in current.Processes) ImGui.TextDisabled($"pid {row.Pid} {row.Name}");
        }
        if (UiAuto.Button("Check##testkit")) RefreshHealth();
        ImGui.SameLine();
        ImGui.BeginDisabled(restartBusy);
        if (UiAuto.Button("Restart (end it, client reconnects)")) Restart("kill");
        ImGui.SameLine();
        if (UiAuto.Button("Start HTTP")) Restart("http");
        ImGui.EndDisabled();
        if (message.Length > 0) ImGui.TextWrapped(message);
        ImGui.TextDisabled("Launched by the MCP client from .mcp.json (py -3 sol_testkit_mcp.py, stdio). A restart cuts this session's testkit tools until /mcp reconnects.");
    }
}
