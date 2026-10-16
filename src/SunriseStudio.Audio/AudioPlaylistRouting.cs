/** File: Playlist route enumeration and the play history that drives sequence containers. */
namespace SunriseStudio.Audio;

/** One selectable playlist entry: segment or container. */
public sealed record AudioPlaylistEntry(uint Id, string Name, uint Segment, bool Container);
/** The items one play of a playlist walks, flagged when truncated. */
public sealed record AudioPlaylistPath(uint Playlist, int PlayNumber, uint StartItem, IReadOnlyList<uint> Items, bool Truncated);

/** Step cursors belong to one loaded bank and publish only after a completed preparation. */
public sealed class AudioPlaylistHistory
{
    private AudioAnalysis? source;
    private readonly Dictionary<(uint Playlist, uint Item), int> sequences = [];
    private readonly Dictionary<uint, int> plays = [];
    /** How many times the playlist has been played in this history. */
    public int PlayCount(uint playlist) => plays.GetValueOrDefault(playlist);
    /** Independent copy, so a render can fail without advancing the real history. */
    public AudioPlaylistHistory Copy()
    {
        var result = new AudioPlaylistHistory { source = source };
        foreach (var pair in sequences) result.sequences.Add(pair.Key, pair.Value);
        foreach (var pair in plays) result.plays.Add(pair.Key, pair.Value);
        return result;
    }
    /** Forgets all sequence positions and play counts. */
    public void Reset() { source = null; sequences.Clear(); plays.Clear(); }

    /** Ties the history to one loaded bank; another bank throws until Reset. */
    internal void Bind(AudioAnalysis analysis)
    {
        if (source is not null && !ReferenceEquals(source, analysis))
            throw new InvalidOperationException("Playlist history belongs to another loaded bank. Reset it first.");
        source = analysis;
    }
    /** Next child of a sequence container (round robin), or the explicit child after bounds checks. */
    internal int Next(uint playlist, AudioPlaylistItem item, int? explicitChild = null)
    {
        if (item.Children.Count == 0) throw new InvalidDataException("Sequence container has no children.");
        var key = (playlist, item.Id);
        int index = explicitChild ?? (sequences.GetValueOrDefault(key, -1) + 1) % item.Children.Count;
        if ((uint)index >= item.Children.Count) throw new InvalidDataException("Playlist jump leaves its container.");
        sequences[key] = index; return index;
    }
    /** Adopts a successful render's history and counts one more play of the playlist. */
    internal void Commit(AudioPlaylistHistory next, uint playlist)
    {
        source = next.source; sequences.Clear(); plays.Clear();
        foreach (var pair in next.sequences) sequences.Add(pair.Key, pair.Value);
        foreach (var pair in next.plays) plays.Add(pair.Key, pair.Value);
        plays[playlist] = checked(PlayCount(playlist) + 1);
    }
}

/** Enumerates and walks playlist routes. */
public static class AudioPlaylistRouting
{
    /** The segments and containers of a playlist that can start a play. */
    public static IReadOnlyList<AudioPlaylistEntry> Entries(AudioAnalysis analysis, uint playlist)
    {
        if (!analysis.Playlists.TryGetValue(playlist, out var definition)) throw new InvalidDataException("Music playlist is missing.");
        var result = new List<AudioPlaylistEntry>();
        void Visit(AudioPlaylistItem item)
        {
            result.Add(new(item.Id, item.Segment != 0 ? analysis.Segments.GetValueOrDefault(item.Segment)?.Name ?? $"Segment {item.Segment}"
                : (item.Kind == 1 ? "Sequence step" : item.Kind == 0 ? "Sequence" : "Random container") + $" {item.Id}", item.Segment, item.Segment == 0));
            foreach (var child in item.Children) Visit(child);
        }
        Visit(definition.Root); return result;
    }

    /** A sequence jump skips earlier siblings once, then resumes the normal parent traversal. */
    internal static (IReadOnlyList<uint> Segments, AudioPlaylistPath Path, AudioPlaylistHistory Next) Select(
        AudioAnalysis analysis, AudioPlaylist playlist, int loops, int variation, AudioPlaylistHistory history,
        uint startItem, HashSet<string> notices)
    {
        var next = history.Copy(); next.Bind(analysis);
        var sequence = new List<uint>(); var items = new List<uint>(); bool truncated = false;
        var seen = new HashSet<uint>(); var path = new List<AudioPlaylistItem>(); var selectedPath = new List<AudioPlaylistItem>();
        void Find(AudioPlaylistItem item, int depth)
        {
            if (depth > 64 || !seen.Add(item.Id)) throw new InvalidDataException("Playlist item identities are duplicated or too deeply nested.");
            path.Add(item);
            if (item.Id == startItem) selectedPath.AddRange(path);
            foreach (var child in item.Children) Find(child, depth + 1);
            path.RemoveAt(path.Count - 1);
        }
        Find(playlist.Root, 0);
        if (startItem != 0 && selectedPath.Count == 0) throw new InvalidDataException("Jump target is outside the selected playlist.");
        var recent = new Dictionary<uint, Queue<uint>>();
        void Walk(AudioPlaylistItem item, int depth, int jumpDepth = -1)
        {
            if (depth > 64) throw new InvalidDataException("Playlist nesting is too deep.");
            if (item.LoopMinimum != 0 || item.LoopMaximum != 0)
                throw new NotSupportedException("Randomized playlist loop counts need their native selection policy.");
            int repetitions = item.Loops == 0 ? loops : item.Loops;
            if (repetitions is < 1 or > 64) throw new NotSupportedException("This playlist loop count exceeds the audition limit.");
            int childIndex = -1;
            if (jumpDepth >= 0 && jumpDepth + 1 < selectedPath.Count)
            {
                if (item.Kind is not (0 or 1)) throw new NotSupportedException("Jumping through a random ancestor needs its playback history.");
                childIndex = item.Children.ToList().IndexOf(selectedPath[jumpDepth + 1]);
                if (childIndex < 0) throw new InvalidDataException("Playlist jump path is disconnected.");
            }
            for (int repeat = 0; repeat < repetitions; repeat++)
            {
                if (sequence.Count >= 256) throw new InvalidDataException("Playlist exceeds 256 audition segments.");
                if (item.Segment != 0) { sequence.Add(item.Segment); items.Add(item.Id); continue; }
                if (item.Children.Count == 0) throw new InvalidDataException("Playlist container has no children.");
                if (item.Kind == 0)
                {
                    int first = repeat == 0 && childIndex >= 0 ? childIndex : 0;
                    for (int index = first; index < item.Children.Count; index++)
                    {
                        Walk(item.Children[index], depth + 1, repeat == 0 && index == childIndex ? jumpDepth + 1 : -1);
                        if (truncated) return;
                    }
                }
                else if (item.Kind == 1)
                {
                    int index = next.Next(playlist.Id, item, repeat == 0 && childIndex >= 0 ? childIndex : null);
                    Walk(item.Children[index], depth + 1, repeat == 0 && index == childIndex ? jumpDepth + 1 : -1);
                    if (truncated) return;
                }
                else if (item.Kind == 2)
                {
                    // A loop ends once every child has played; here each child plays once.
                    notices.Add("Random: repeatable path, not the game seed");
                    if (!recent.TryGetValue(item.Id, out var previous)) recent[item.Id] = previous = new();
                    var eligible = item.Children.Where(child => !item.UseWeights || child.Weight > 0).ToArray();
                    if (eligible.Length == 0) throw new InvalidDataException("Random playlist has no available child.");
                    // The engine caps avoid-repeat at one less than the child count.
                    int avoid = Math.Min(item.Shuffle ? eligible.Length - 1 : item.AvoidRepeat, eligible.Length - 1);
                    var played = new HashSet<uint>();
                    while (played.Count < eligible.Length)
                    {
                        var choices = eligible.Where(child => !played.Contains(child.Id) && !previous.Contains(child.Id)).ToArray();
                        if (choices.Length == 0) throw new InvalidDataException("Random playlist has no available child.");
                        var chosen = choices[variation % choices.Length]; played.Add(chosen.Id); previous.Enqueue(chosen.Id);
                        while (previous.Count > avoid) previous.Dequeue();
                        Walk(chosen, depth + 1);
                        if (truncated) return;
                    }
                }
                else throw new NotSupportedException("Random step playlists need persistent random-cycle history.");
            }
            if (item.Loops == 0) { truncated = true; notices.Add("Preview ends inside an infinite playlist item."); }
        }
        Walk(playlist.Root, 0, startItem == 0 ? -1 : 0);
        if (sequence.Count == 0) throw new InvalidDataException("Playlist contains no music segments.");
        return (sequence, new(playlist.Id, history.PlayCount(playlist.Id) + 1, startItem, items.AsReadOnly(), truncated), next);
    }
}
