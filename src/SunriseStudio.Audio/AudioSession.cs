using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SunriseStudio.Packages;

namespace SunriseStudio.Audio;

/** Extracted copyrighted media stays in a disposable cache, outside installations and delta projects. */
public sealed class AudioSession : IDisposable
{
    // Decoded sources and outputs stay under the 512 MiB working limit.
    private const long WorkingLimit = 512L * 1024 * 1024;
    private readonly string cacheRoot;
    private readonly List<AudioBank> loadedBanks = [];
    private readonly Dictionary<uint, string> mediaPaths = [];
    // Unclipped mix sources; the cache is dropped before it passes the working limit.
    private readonly Dictionary<uint, AudioSamples> mixSources = [];
    private readonly Dictionary<string, (IReadOnlyList<AudioEventComposition> Items, string Log)> variants = [];
    private AudioPlaylistHistory playlistHistory = new();
    private WwiseHierarchy? hierarchy;
    // A live session holds its lock file open; the OS drops it when the process ends.
    private const string LockName = ".lock";
    private readonly FileStream? lockStream;
    public string DirectoryPath { get; }
    public AudioDocument Document { get; }
    public IReadOnlyList<AudioEventComposition> Compositions { get; private set; } = [];
    public string CompositionLog { get; private set; } = "";
    public AudioAnalysis? Analysis { get; private set; }
    public AudioCompositionOptions? CompositionOptions { get; private set; }
    public AudioTimelinePlan? Timeline { get; private set; }
    public AudioTimelineRender? TimelineRender { get; private set; }
    /** Plays recorded for a playlist in this session's history. */
    public int PlaylistPlayCount(uint playlist) => Volatile.Read(ref playlistHistory).PlayCount(playlist);
    /** Clears the playlist history. */
    public void ResetPlaylistHistory() => Volatile.Write(ref playlistHistory, new());

    /** Copies the selection's media into a new locked cache folder and loads its banks. */
    public AudioSession(PackageCatalog catalog, uint tag, Func<uint, string?>? names = null, string? cacheDirectory = null)
    {
        var reader = new AudioCatalog(catalog, names);
        Document = reader.Read(tag);
        cacheRoot = Path.GetFullPath(cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sunrise Studio", "Cache", "Audio"));
        AudioPaths.Check(cacheRoot, catalog.GameRoot);
        Directory.CreateDirectory(cacheRoot);
        RemoveStaleSessions(cacheRoot);
        DirectoryPath = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(DirectoryPath);
        try
        {
            lockStream = new FileStream(Path.Combine(DirectoryPath, LockName), FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            long total = 0;
            foreach (var media in Document.Media)
            {
                if (media.Size < 0 || media.Size > WorkingLimit - total)
                    throw new InvalidDataException("Audio selection exceeds the 512 MiB working limit.");
                byte[] bytes = reader.ReadMedia(media); total = checked(total + bytes.Length);
                if (total > WorkingLimit) throw new InvalidDataException("Audio selection exceeds the 512 MiB working limit.");
                string path = Path.Combine(DirectoryPath, media.Id + ".wem");
                if (mediaPaths.TryGetValue(media.Id, out var previous)
                    && !SHA256.HashData(File.ReadAllBytes(previous)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
                    throw new InvalidDataException("Audio media identity has conflicting payloads.");
                File.WriteAllBytes(path, bytes); mediaPaths[media.Id] = path;
            }
            if (Document.Bank is { } bank)
            {
                foreach (var init in reader.ReadInitBanks()) AddBank(init);
                AddBank(bank);
            }
        }
        catch { Dispose(); throw; }
        void AddBank(AudioBank bank)
        {
            if (!loadedBanks.Any(value => value.Id == bank.Id)) loadedBanks.Add(bank);
        }
    }

    /** Path of a media file in the session cache; throws for media outside the selection. */
    public string MediaPath(uint mediaId) => mediaPaths.TryGetValue(mediaId, out var path) ? path
        : throw new InvalidDataException($"Media {mediaId} is not part of this audio selection.");

    /** Reads the stream facts without decoding audio; unsupported media throws AudioUnsupportedException. */
    public Task<AudioStreamInfo> InspectMedia(uint mediaId, CancellationToken token = default)
    {
        string path = MediaPath(mediaId);
        return Task.Run(() => WemDecoder.Inspect(File.ReadAllBytes(path)), token);
    }

    /** A composition's length comes from its layout; samples are 48 kHz stereo frames. */
    public async Task<AudioStreamInfo> InspectComposition(int index, CancellationToken token = default)
    {
        var plan = Composition(index).Plan;
        var lengths = new Dictionary<uint, double>();
        foreach (uint id in plan.Media())
        {
            token.ThrowIfCancellationRequested();
            lengths[id] = (await InspectMedia(id, token)).DurationSeconds;
        }
        var timeline = AudioEventRender.Layout(plan, id => lengths[id]);
        return new(48000, 2, (long)Math.Round(timeline.DurationSeconds * 48000, MidpointRounding.AwayFromZero), "Composition");
    }

    /** Composes every event of the selection once. */
    public async Task GenerateCompositions(CancellationToken token = default)
    {
        if (Compositions.Count > 0) return;
        var result = await Compose(null, token);
        Compositions = result.Items; CompositionLog = result.Log;
    }

    /** Analyses the loaded banks' music routing once. */
    public async Task Analyze(CancellationToken token = default)
    {
        if (Analysis is not null) return;
        if (loadedBanks.Count == 0) throw new InvalidOperationException("Choose a bank or event for routing analysis.");
        token.ThrowIfCancellationRequested();
        (hierarchy, Analysis) = await Task.Run(() =>
        {
            var read = WwiseHierarchy.Read(loadedBanks);
            return (read, AudioAnalysis.Read(read));
        }, token);
    }

    /** Each selection has a distinct cache key so regenerated choices cannot reuse the wrong WAV. */
    public async Task GenerateCompositions(AudioCompositionOptions choices, CancellationToken token = default)
    {
        choices.Validate();
        if (!variants.TryGetValue(choices.Key, out var result)) { result = await Compose(choices, token); variants[choices.Key] = result; }
        Compositions = result.Items; CompositionLog = result.Log; CompositionOptions = choices;
    }

    /** Composes the selected event, or every event of the bank, and collects their notices. */
    private async Task<(IReadOnlyList<AudioEventComposition> Items, string Log)> Compose(AudioCompositionOptions? choices,
        CancellationToken token)
    {
        if (loadedBanks.Count == 0 || Document.Bank is not { } bank)
            throw new InvalidOperationException("Choose a bank or event to generate compositions.");
        await Analyze(token);
        // A bank lists the compositions of each of its own events, in bank order.
        uint[] events = Document.EventId is uint selected ? [selected]
            : [.. bank.Objects.Where(value => value.Type == 4).Select(value => value.Id).Distinct()];
        var log = new List<string>(hierarchy!.Diagnostics);
        var items = await Task.Run(() =>
        {
            var result = new List<AudioEventComposition>();
            foreach (uint eventId in events)
            {
                token.ThrowIfCancellationRequested();
                if (result.Count >= AudioEventComposer.CompositionLimit)
                {
                    log.Add($"Stopped after {AudioEventComposer.CompositionLimit} compositions.");
                    break;
                }
                try
                {
                    var composed = AudioEventComposer.Compose(hierarchy!, eventId, choices, Analysis);
                    if (composed.Count == 0) log.Add($"Event {eventId} plays nothing.");
                    result.AddRange(composed);
                }
                catch (InvalidDataException error) when (events.Length > 1) { log.Add($"Event {eventId} skipped: {error.Message}"); }
            }
            return result;
        }, token);
        if (events.Length == 0) log.Add("The bank has no events.");
        foreach (var item in items.Where(value => value.Plan.Notices.Count > 0))
            log.AddRange(item.Plan.Notices.Select(notice => $"{item.Name}: {notice}"));
        return (items, string.Join("\n", log.Distinct()));
    }

    /** Decodes once to a cached WAV; more than two channels mix down to stereo. */
    public async Task<string> DecodeMedia(uint mediaId, CancellationToken token = default)
    {
        string target = Path.Combine(DirectoryPath, mediaId + ".wav");
        if (File.Exists(target)) return target;
        string source = MediaPath(mediaId);
        await Task.Run(() =>
        {
            var wave = WemDecoder.Decode(File.ReadAllBytes(source), 2, token);
            WriteAtomically(target, path => PcmWave.Write(path, wave.SampleRate, wave.Channels, wave.Samples));
        }, token);
        return target;
    }

    /** Decodes the composition at index to a cached WAV and returns its path. */
    public async Task<string> DecodeComposition(int index, CancellationToken token = default)
    {
        var composition = Composition(index);
        string signature = Key(composition.Routing + "|" + (CompositionOptions?.Key ?? "default"));
        string target = Path.Combine(DirectoryPath, "composition-" + signature + ".wav");
        if (File.Exists(target)) return target;
        var sources = await DecodeSources(composition.Plan.Media(), token);
        await Task.Run(() => WriteAtomically(target, path => AudioEventRender.Render(composition.Plan, sources, path, token)), token);
        return target;
    }

    /** The first 20 hex digits of the text's SHA-256; cache file names use it. */
    private static string Key(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..20];

    private AudioEventComposition Composition(int index) =>
        (uint)index < Compositions.Count ? Compositions[index] : throw new ArgumentOutOfRangeException(nameof(index));

    /** Mix sources keep samples past full scale, as the game's float decoder does; limits count PCM16 bytes. */
    private async Task<Dictionary<uint, AudioSamples>> DecodeSources(IEnumerable<uint> media, CancellationToken token)
    {
        var decoded = new Dictionary<uint, AudioSamples>(); long bytes = 0;
        foreach (uint id in media.Distinct())
        {
            token.ThrowIfCancellationRequested();
            AudioSamples? samples;
            lock (mixSources) mixSources.TryGetValue(id, out samples);
            if (samples is null)
            {
                string source = MediaPath(id);
                samples = await Task.Run(() => WemDecoder.DecodeSamples(File.ReadAllBytes(source), 2, token), token);
                lock (mixSources)
                {
                    if (mixSources.Values.Sum(value => value.Pcm16Bytes) + samples.Pcm16Bytes > WorkingLimit) mixSources.Clear();
                    mixSources[id] = samples;
                }
            }
            bytes = checked(bytes + samples.Pcm16Bytes);
            if (bytes > WorkingLimit) throw new InvalidDataException("Decoded sources exceed the 512 MiB working limit.");
            decoded.Add(id, samples);
        }
        return decoded;
    }

    /** Writes beside the target, then moves it into place; a failed write leaves no partial file. */
    private static void WriteAtomically(string target, Action<string> write)
    {
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".wav";
        try { write(temporary); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /** Renders one playlist route; the play history advances only after the render succeeds. */
    public async Task<AudioTimelineRender> RenderTimeline(uint playlist, int loops, int variation,
        CancellationToken token = default, uint startItem = 0)
    {
        await Analyze(token);
        var staged = Volatile.Read(ref playlistHistory).Copy();
        var plan = AudioTimeline.Build(Analysis!, playlist, loops, variation, staged, startItem);
        var rendered = await RenderPlan(plan, $"timeline-{playlist}-{loops}-{variation}-{plan.PlaylistPath!.PlayNumber}-{startItem}", token);
        Volatile.Write(ref playlistHistory, staged);
        return rendered;
    }

    /** Renders one transition of a container between two segments. */
    public async Task<AudioTimelineRender> RenderTransition(uint container, uint source, uint destination,
        double requestSeconds, CancellationToken token = default, int destinationVariation = 0)
    {
        await Analyze(token);
        var plan = AudioTimeline.BuildTransition(Analysis!, container, source, destination, requestSeconds, destinationVariation);
        return await RenderPlan(plan, "transition-" + Key(JsonSerializer.Serialize(plan)), token);
    }

    /** Destination history advances only after every selected segment has rendered successfully. */
    public async Task<AudioTimelineRender> RenderPlaylistTransition(uint container, uint source, uint destination,
        double requestSeconds, int loops, int variation, CancellationToken token = default, int destinationVariation = 0)
    {
        await Analyze(token);
        var staged = Volatile.Read(ref playlistHistory).Copy();
        var plan = AudioTimeline.BuildPlaylistTransition(Analysis!, container, source, destination,
            requestSeconds, loops, variation, staged, destinationVariation);
        var rendered = await RenderPlan(plan, "playlist-transition-" + Key(JsonSerializer.Serialize(plan)), token);
        Volatile.Write(ref playlistHistory, staged); return rendered;
    }

    /** Renders the plan to a temporary WAV, then moves it into the session folder. */
    private async Task<AudioTimelineRender> RenderPlan(AudioTimelinePlan plan, string fileName, CancellationToken token)
    {
        var decoded = await DecodeSources(plan.Clips.Select(clip => clip.Media), token);
        string output = Path.Combine(DirectoryPath, fileName + ".wav");
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".wav";
        try
        {
            var rendered = AudioTimeline.Render(plan, decoded, temporary, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, output, true);
            Timeline = plan; TimelineRender = rendered with { Path = output };
            return TimelineRender;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /** Copies a media file out of the cache; the target must be outside the game and any project. */
    public void ExportMedia(uint mediaId, string target, string gameRoot)
    { AudioPaths.Check(target, gameRoot); File.Copy(MediaPath(mediaId), target, true); }

    /** Copies a WAV out; the target must be outside the game and any project. */
    public static void ExportWave(string wave, string target, string gameRoot)
    { AudioPaths.Check(target, gameRoot); File.Copy(wave, target, true); }

    /** Writes the media, the banks, one WAV per playable composition and routing.txt with every routing. */
    public async Task ExportCompositionSet(string target, string gameRoot, CancellationToken token = default)
    {
        AudioPaths.Check(target, gameRoot); Directory.CreateDirectory(target);
        foreach (string source in mediaPaths.Values)
        {
            string path = Path.Combine(target, Path.GetFileName(source)); AudioPaths.Check(path, gameRoot);
            File.Copy(source, path, true);
        }
        foreach (var bank in loadedBanks)
        {
            string path = Path.Combine(target, bank.Id + ".bnk"); AudioPaths.Check(path, gameRoot);
            File.WriteAllBytes(path, bank.Bytes);
        }
        string output = Path.Combine(target, "compositions"); AudioPaths.Check(output, gameRoot); Directory.CreateDirectory(output);
        var routing = new StringBuilder();
        for (int index = 0; index < Compositions.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var composition = Compositions[index];
            string name = $"{index + 1:D4} {string.Concat(composition.Name.Select(value => Path.GetInvalidFileNameChars().Contains(value) ? '_' : value))}";
            routing.Append("== ").Append(name).Append('\n').Append(composition.Routing);
            try
            {
                string path = Path.Combine(output, name + ".wav"); AudioPaths.Check(path, gameRoot);
                File.Copy(await DecodeComposition(index, token), path, true);
            }
            catch (Exception error) when (error is NotSupportedException or InvalidDataException)
            { routing.Append("Not exported: ").Append(error.Message).Append('\n'); }
            routing.Append('\n');
        }
        string list = Path.Combine(target, "routing.txt"); AudioPaths.Check(list, gameRoot);
        File.WriteAllText(list, routing.ToString());
    }

    /** Releases the lock and deletes the session folder. */
    public void Dispose()
    {
        lockStream?.Dispose();
        AudioPaths.RemoveWorkingDirectory(DirectoryPath, cacheRoot);
    }

    /** Removes session folders left by a crash; a held lock or a recent unlocked folder is kept. */
    private static void RemoveStaleSessions(string cacheRoot)
    {
        foreach (string folder in Directory.EnumerateDirectories(cacheRoot))
        {
            if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out _)) continue;
            try
            {
                string lockPath = Path.Combine(folder, LockName);
                if (File.Exists(lockPath))
                    new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose();
                // A new session makes its folder before its lock, so give unlocked folders a day.
                else if (Directory.GetLastWriteTimeUtc(folder) > DateTime.UtcNow.AddDays(-1)) continue;
                AudioPaths.RemoveWorkingDirectory(folder, cacheRoot);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException) { }
        }
    }
}

/** Path guards for audio export and cache folders. */
internal static class AudioPaths
{
    /** Cleanup accepts only checked descendants of the operation's own working root. */
    public static void RemoveWorkingDirectory(string path, string root)
    {
        string full = Path.GetFullPath(path), parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        if (full.Equals(parent, StringComparison.OrdinalIgnoreCase) || !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Audio cleanup leaves its working root.");
        if (!Directory.Exists(full)) return;
        RejectLinks(full); var pending = new Stack<string>(); pending.Push(full);
        while (pending.TryPop(out string? folder))
            foreach (string child in Directory.EnumerateFileSystemEntries(folder))
            {
                RejectLinks(child);
                if (Directory.Exists(child)) pending.Push(child);
            }
        Directory.Delete(full, true);
    }

    /** Throws when the target is inside the game install or a delta project, or crosses a link. */
    public static void Check(string path, string gameRoot)
    {
        string full = Path.GetFullPath(path), game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        if (full.Equals(game, StringComparison.OrdinalIgnoreCase) || full.StartsWith(game + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Export audio outside the installation.");
        RejectLinks(full);
        for (string? current = File.Exists(full) ? Path.GetDirectoryName(full) : full; current is not null; current = Path.GetDirectoryName(current))
            if (File.Exists(Path.Combine(current, "sunrise.project.json")))
                throw new InvalidOperationException("Extracted audio must stay outside delta projects.");
    }

    /** Throws when any part of the path is a symbolic link or junction. */
    public static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Audio paths cannot use links or junctions.");
    }
}
