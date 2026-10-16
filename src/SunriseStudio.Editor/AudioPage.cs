using ImGuiNET;
using SunriseStudio.Audio;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Decoding and rendering run off the UI thread; playback starts only after an explicit Play action. */
public sealed partial class AudioPage : IStudioPage, IDisposable
{
    /**
     * @param Pcm The decoded Wave, read on the worker so the UI thread only opens the device.
     * @param Unsupported Why the selected item cannot play; set by InspectSelected.
     */
    private sealed record Job(AudioSession? Session = null, string? Wave = null,
        AudioStreamInfo? Info = null, bool Play = false, bool RefreshCompositions = false, PcmWave? Pcm = null,
        string? Unsupported = null);
    private readonly WavePlayback player = new();
    private StudioDocument? source;
    private AudioSession? session;
    private Task<Job>? operation;
    private CancellationTokenSource? cancellation;
    private AudioStreamInfo? info;
    private string error = "", status = "", filter = "", loadedWave = "", unsupported = "";
    private int selectedMedia = -1, selectedComposition = -1;
    private float volume = .8f;
    private bool disposed;
    private StudioDocument? requestedSource;
    public string Id => "sunrise.audio";
    public string Title => "Audio";
    public bool IsBusy => operation is not null;
    public AudioDocument? AudioDocument => session?.Document;
    /** Accepts any entry the audio catalog can decode. */
    public bool CanOpen(StudioDocument document) => AudioCatalog.Supports(document.Entry);

    /** Hosts call this even when the tab is hidden so a changed selection stops its old sound. */
    public void Update(StudioWorkspace workspace)
    {
        if (disposed) return;
        var next = workspace.Document is { } document && CanOpen(document) ? document : null;
        if (!ReferenceEquals(source, next)) { Reset(); source = next; }
        if (operation is not { IsCompleted: true } completed) return;
        operation = null; cancellation?.Dispose(); cancellation = null; status = "";
        try
        {
            var result = completed.GetAwaiter().GetResult();
            if (result.Session is not null)
            {
                session = result.Session; selectedMedia = session.Document.Media.Count > 0 ? 0 : -1;
                selectedComposition = -1;
                InspectSelected();
            }
            if (result.Info is not null) info = result.Info;
            if (result.Unsupported is not null) unsupported = result.Unsupported;
            if (result.Wave is not null)
            {
                loadedWave = result.Wave; info = result.Info; unsupported = "";
                player.Load(result.Pcm ?? throw new InvalidOperationException("Decoded audio is missing."));
                player.SetVolume(volume);
                if (result.Play) player.Play();
            }
            if (result.RefreshCompositions)
            { selectedMedia = -1; selectedComposition = session?.Compositions.Count > 0 ? 0 : -1; InspectSelected(); }
        }
        catch (OperationCanceledException) { status = "Canceled"; }
        catch (Exception exception) { error = exception.GetBaseException().Message; }
    }

    /** Reads the selected audio once, then lists its compositions and tracks above the player. */
    public void Draw(StudioPageContext context)
    {
        Update(context.Workspace);
        if (source is null || context.Workspace.Catalog is not { } catalog) return;
        if (error.Length > 0) ImGui.TextWrapped(error);
        if (session is null)
        {
            if (operation is null && !ReferenceEquals(requestedSource, source))
            { requestedSource = source; Load(catalog, context.Workspace.DisplayName); }
            if (operation is null && UiAuto.Button("Load audio")) Load(catalog, context.Workspace.DisplayName);
            DrawBusy(); return;
        }
        // A worker may replace Compositions mid-frame; read it once.
        var compositions = session.Compositions;
        ImGui.TextUnformatted(session.Document.Name);
        if (session.Document.Bank is { } objects) StudioUi.Tip($"{objects.Objects.Count:N0} objects");
        ImGui.SameLine(); ImGui.TextDisabled(session.Document.Kind);
        if (session.Document.Bank is not null)
        {
            ImGui.BeginDisabled(IsBusy || session.Compositions.Count > 0);
            if (UiAuto.Button("Build composition previews"))
            {
                var active = session;
                Begin("Resolving bank routing", async token => { await active.GenerateCompositions(token); return new(); });
            }
            ImGui.EndDisabled();
            StudioUi.Tip(session.Document.EventId is null ? "Lists the compositions of each event in the bank. Preview only."
                : "Preview only; the game may sound different.");
        }
        var notes = session.Document.Diagnostics;
        if (notes.Count > 0 && ImGui.TreeNode($"Notes ({notes.Count})###audio-notes"))
        {
            StudioUi.Facts("audio-notes", notes);
            ImGui.TreePop();
        }
        DrawCompositionOptions();
        DrawBusy();
        float height = Math.Max(100, ImGui.GetContentRegionAvail().Y - 160);
        ImGui.BeginChild("audio-items", new(0, height), ImGuiChildFlags.Borders);
        ImGui.SetNextItemWidth(-1); UiAuto.InputTextWithHint("##audio-find", "Find track or composition", ref filter, 256);
        if (compositions.Count > 0)
        {
            ImGui.SeparatorText("Compositions");
            for (int i = 0; i < compositions.Count; i++)
            {
                var item = compositions[i]; if (!Matches(item.Name)) continue;
                ImGui.PushID(i);
                ImGui.BeginDisabled(IsBusy);
                if (UiAuto.Selectable(item.Name, selectedComposition == i)) Select(-1, i);
                ImGui.EndDisabled(); ImGui.PopID();
            }
        }
        ImGui.SeparatorText("Tracks and effects");
        for (int i = 0; i < session.Document.Media.Count; i++)
        {
            var item = session.Document.Media[i]; string label = MediaLabel(item, i); if (!Matches(label) && !Matches(item.Name)) continue;
            ImGui.PushID(i + 10000); ImGui.BeginDisabled(IsBusy);
            if (UiAuto.Selectable(label, selectedMedia == i)) Select(i, -1);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{item.Name}\nMedia {item.Id} - {item.Size:N0} bytes");
            ImGui.PopID();
        }
        ImGui.EndChild();
        DrawPlayback(context.Workspace);
        if ((uint)selectedComposition < (uint)compositions.Count && ImGui.CollapsingHeader("Routing"))
        {
            ImGui.BeginChild("audio-routing", new(0, 160), ImGuiChildFlags.Borders);
            ImGui.TextUnformatted(compositions[selectedComposition].Routing);
            ImGui.EndChild();
        }
        if (session.CompositionLog.Length > 0 && ImGui.CollapsingHeader("Composer notes"))
        {
            ImGui.BeginChild("audio-composer-notes", new(0, 160), ImGuiChildFlags.Borders);
            ImGui.TextUnformatted(session.CompositionLog);
            ImGui.EndChild();
        }
    }

    private void Load(SunriseStudio.Packages.PackageCatalog catalog, Func<uint, string> name)
    {
        uint tag = source!.Entry.TagHash;
        Begin("Reading audio", token =>
        {
            token.ThrowIfCancellationRequested();
            var prepared = new AudioSession(catalog, tag, name);
            if (token.IsCancellationRequested) { prepared.Dispose(); token.ThrowIfCancellationRequested(); }
            return Task.FromResult(new Job(Session: prepared));
        });
    }

    /** Play, stop, volume, seek and the export menu; a decode starts only from Play or Prepare WAV. */
    private void DrawPlayback(StudioWorkspace workspace)
    {
        bool chosen = selectedMedia >= 0 || selectedComposition >= 0 || loadedWave.Length > 0;
        bool canDecode = unsupported.Length == 0;
        ImGui.BeginDisabled(IsBusy || !chosen);
        ImGui.BeginDisabled(!canDecode && loadedWave.Length == 0);
        if (ImGuiIcons.Button("audio-play", player.Playing ? StudioIcon.Pause : StudioIcon.Play,
            !canDecode && loadedWave.Length == 0 ? unsupported : player.Playing ? "Pause" : "Play")) Try(() =>
        {
            if (player.Playing) player.Pause();
            else if (loadedWave.Length > 0) player.Play();
            else Decode(true);
        });
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGuiIcons.Button("audio-stop", StudioIcon.Reset, "Stop")) Try(player.Stop);
        ImGui.SameLine();
        ImGui.BeginDisabled(!canDecode);
        if (UiAuto.Button("Prepare WAV")) Decode(false);
        ImGui.EndDisabled(); StudioUi.Tip(unsupported);
        ImGui.SameLine(); ImGui.SetNextItemWidth(110);
        if (UiAuto.SliderFloat("Volume", ref volume, 0, 1, "%.2f")) Try(() => player.SetVolume(volume));
        ImGui.EndDisabled();
        float position = (float)player.Position;
        double duration = player.Wave is not null ? player.Duration : info?.DurationSeconds ?? 0;
        ImGui.BeginDisabled(player.Wave is null);
        ImGui.SetNextItemWidth(-1);
        if (UiAuto.SliderFloat("##audio-position", ref position, 0, (float)Math.Max(.001, duration),
            Time(position) + " / " + Time(duration))) Try(() => player.Seek(position));
        ImGui.EndDisabled();
        if (info is not null) ImGui.TextDisabled($"{info.SampleRate:N0} Hz   {info.Channels} channels   {info.Encoding}");
        ImGui.BeginDisabled(IsBusy);
        if (ImGuiIcons.Button("audio-export", StudioIcon.Export, "Export")) ImGui.OpenPopup("audio-export-menu");
        ImGui.EndDisabled();
        if (!ImGui.BeginPopup("audio-export-menu")) return;
        if (UiAuto.MenuItem("WAV...", "", false, !IsBusy && loadedWave.Length > 0)) Try(() =>
        {
            using var dialog = new SaveFileDialog { Filter = "Wave audio|*.wav", FileName = "audio.wav", AddExtension = true };
            if (dialog.ShowDialog() == DialogResult.OK) AudioSession.ExportWave(loadedWave, dialog.FileName, workspace.Catalog!.GameRoot);
        });
        if (UiAuto.MenuItem("WEM...", "", false, !IsBusy && selectedMedia >= 0)) Try(() =>
        {
            var item = session!.Document.Media[selectedMedia];
            using var dialog = new SaveFileDialog { Filter = "Wwise media|*.wem", FileName = item.Id + ".wem", AddExtension = true };
            if (dialog.ShowDialog() == DialogResult.OK) session!.ExportMedia(item.Id, dialog.FileName, workspace.Catalog!.GameRoot);
        });
        if (UiAuto.MenuItem("Composition WAVs and routing...", "", false, !IsBusy && session!.Compositions.Count > 0)) Try(() =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Export one WAV per composition, routing.txt and the media",
                UseDescriptionForTitle = true };
            if (dialog.ShowDialog() != DialogResult.OK) return;
            var active = session!; string folder = dialog.SelectedPath, gameRoot = workspace.Catalog!.GameRoot;
            Begin("Exporting compositions", async token => { await active.ExportCompositionSet(folder, gameRoot, token); return new(); });
        });
        ImGui.EndPopup();
    }

    private void Decode(bool play)
    {
        var active = session!; int media = selectedMedia, composition = selectedComposition;
        Begin("Decoding audio", async token =>
        {
            string path = media >= 0 ? await active.DecodeMedia(active.Document.Media[media].Id, token)
                : await active.DecodeComposition(composition, token);
            var metadata = PcmWave.ReadInfo(path);
            return new(Wave: path, Info: metadata with { Encoding = composition >= 0 ? "Composition preview" : "PCM16" }, Play: play,
                Pcm: PcmWave.Read(path));
        });
    }

    private void Select(int media, int composition)
    {
        if (selectedMedia == media && selectedComposition == composition) return;
        player.Dispose(); loadedWave = ""; info = null; selectedMedia = media; selectedComposition = composition;
        InspectSelected();
    }

    /** Metadata is read on selection; it never starts playback or allocates decoded PCM. */
    private void InspectSelected()
    {
        unsupported = "";
        if (session is not { } active || selectedMedia < 0 && selectedComposition < 0) return;
        int media = selectedMedia, composition = selectedComposition;
        Begin("Reading audio length", async token =>
        {
            try
            {
                return new(Info: media >= 0 ? await active.InspectMedia(active.Document.Media[media].Id, token)
                    : await active.InspectComposition(composition, token));
            }
            catch (NotSupportedException exception) { return new(Unsupported: Describe(exception)); }
        });
    }

    /** A placeholder stream says so plainly; other refusals keep the decoder's text. */
    private static string Describe(NotSupportedException exception) =>
        exception is AudioUnsupportedException { Reason: AudioUnsupportedReason.EmptyPlaceholder }
            ? exception.Message + " (no audio)" : exception.Message;

    private static string MediaLabel(AudioMedia media, int index) => media.Name.StartsWith("Audio ", StringComparison.OrdinalIgnoreCase)
        || media.Name.StartsWith("Media ", StringComparison.OrdinalIgnoreCase) ? $"Track {index + 1}" : media.Name;

    private void Begin(string message, Func<CancellationToken, Task<Job>> action)
    {
        if (operation is not null) return;
        error = ""; status = message; cancellation = new();
        var token = cancellation.Token; operation = Task.Run(() => action(token), token);
    }

    private void DrawBusy()
    {
        if (status.Length > 0) ImGui.TextDisabled(status);
        else if (unsupported.Length > 0) ImGui.TextDisabled(unsupported);
        if (IsBusy) { ImGui.SameLine(); if (UiAuto.SmallButton("Cancel")) cancellation?.Cancel(); }
    }

    /** Old sessions are deleted only after their last operation has finished. */
    private void Reset()
    {
        cancellation?.Cancel(); var oldCancellation = cancellation; cancellation = null;
        var pending = operation; operation = null; var previous = session; session = null;
        player.Dispose(); loadedWave = ""; info = null; error = status = unsupported = "";
        selectedMedia = selectedComposition = -1;
        requestedSource = null;
        gameSyncSelections.Clear(); parameterValues.Clear(); selectedPlaylist = 0; timelineVariation = 0;
        if (pending is null) { Cleanup(previous); oldCancellation?.Dispose(); }
        else _ = pending.ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion) Cleanup(task.Result.Session);
            _ = task.Exception; Cleanup(previous); oldCancellation?.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void Cleanup(AudioSession? value)
    { try { value?.Dispose(); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private bool Matches(string value) => filter.Length == 0 || value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    private static string Time(double seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";
    private void Try(Action action) { try { error = ""; action(); } catch (Exception exception) { error = exception.Message; } }
    /** Stops playback and releases the session; safe to call twice. */
    public void Dispose() { if (disposed) return; Reset(); disposed = true; source = null; }
}
