using System.Globalization;
using System.Text.Json.Nodes;
using Solforge.Mcp.Data;

namespace Solforge.Mcp;

/**
 * Middleware decoders over MCP: Havok collision/physics/cloth packfiles, Wwise audio and CRI USM cutscenes. All are
 * read-only on game data; the export tools write files only under the project's exports folder (like export_data).
 */
public static partial class SolforgeTools
{
    public static readonly string[] MediaToolNames =
    [
        "havok_packfile", "export_collision_obj", "audio_info", "export_audio_wav", "video_info", "export_video"
    ];

    private const string NoMedia = "This Solforge host has no Havok/audio/video decoders.";

    private static IEnumerable<McpTool> MediaTools(ISolforgeHost host)
    {
        McpTool Media(string name, string description, JsonObject schema,
            Func<ISolforgeMediaHost, uint, JsonObject, CancellationToken, Task<JsonObject?>> run) =>
            new(name, description, schema, false, async (args, cancel) =>
            {
                if (host is not ISolforgeMediaHost media) return McpToolResult.Error(NoMedia);
                if (host is ISolforgeDataHost { DataUnavailable: { } reason }) return McpToolResult.Error(reason);
                if (ParseHash(McpJson.String(args, "hash")) is not { } tag) return McpToolResult.Error("Give a tag hash.");
                try
                {
                    var result = await run(media, tag, args, cancel).ConfigureAwait(false);
                    return result is null ? McpToolResult.Error($"Unknown tag 0x{tag:X8}.") : McpToolResult.Json(result);
                }
                catch (InvalidDataException error) { return McpToolResult.Error(error.Message); }
                catch (NotSupportedException error) { return McpToolResult.Error(error.Message); }
            });

        string Folder(string kind) => Path.Combine(ExportFolder(host.ProjectRoot), kind);
        static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        yield return Media("havok_packfile", "Decode a Havok 2012 packfile tag (type 26/7: map collision compounds, trigger shape " +
            "arrays, entity physics bodies, cloth): root class and kind, object classes with counts, imports, every collision " +
            "shape decoded (class, vertices, triangles, local bounds, undecoded parts), cloth collidables (name, shape, rest " +
            "position) and notes. Ragdoll/hinge constraints and cloth simulation data are counted, not decoded.",
            McpJson.Schema(("hash", "string", "Packfile tag hash", true), ("max_shapes", "integer", "Shapes listed (default 100, max 5000)", false),
                ("original", "boolean", "Shipped bytes, ignoring project edits", false)),
            (media, tag, args, _) => Task.FromResult(media.DescribeHavok(tag, McpJson.Bool(args, "original", false),
                Math.Clamp(McpJson.Int(args, "max_shapes", 100), 0, 5000))));

        yield return Media("export_collision_obj", "Write the collision a Havok packfile tag places by itself (compound instances " +
            "with their transforms, array entries, physics bodies in body space, cloth collidables at rest) as a Wavefront OBJ " +
            "under the project's exports\\collision folder. Children a map compound imports from other packfiles are left out " +
            "(open the owner in the viewport for those). Returns the path and triangle count.",
            McpJson.Schema(("hash", "string", "Packfile tag hash", true), ("original", "boolean", "Shipped bytes", false)),
            (media, tag, args, _) =>
            {
                string folder = Folder("collision");
                Directory.CreateDirectory(folder);
                return Task.FromResult(media.ExportHavokObj(tag, McpJson.Bool(args, "original", false),
                    Path.Combine(folder, $"{tag:X8}-{Stamp()}.obj")));
            });

        yield return Media("audio_info", "Wwise audio of a tag: an event (class 0x80809802), sound bank (26/5) or WEM stream (26/6). " +
            "Returns kind, bank, and each media id with its stream tag, size, sample rate, channels, seconds and codec " +
            "(build 86657 ships Wwise Vorbis; every stream decodes).",
            McpJson.Schema(("hash", "string", "Event, bank or stream tag hash", true)),
            (media, tag, _, cancel) => media.DescribeAudio(tag, cancel));

        yield return Media("export_audio_wav", "Decode Wwise media of a tag to 16-bit PCM WAV under the project's exports\\audio " +
            "folder (all channels kept). 'media' picks one media id from audio_info; without it every media of the tag is " +
            "written. Returns the files and anything skipped (empty placeholders).",
            McpJson.Schema(("hash", "string", "Event, bank or stream tag hash", true), ("media", "string", "Media id (decimal or 0x hex)", false)),
            (media, tag, args, cancel) =>
            {
                string text = McpJson.String(args, "media") ?? "";
                uint? id = text.Length == 0 ? null
                    : text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ParseHash(text)
                    : uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint number) ? number : null;
                if (text.Length > 0 && id is null) throw new InvalidDataException("Give the media id in decimal or 0x hex.");
                return media.ExportAudioWav(tag, id, Path.Combine(Folder("audio"), $"{tag:X8}"), cancel);
            });

        yield return Media("video_info", "A CRI USM cutscene tag (type 27, 'CRID'): source file name, and each stream: video " +
            "(codec, size, frames, frame rate), audio tracks by channel (ADX, sample rate, channels; track 0 is the mix, the " +
            "others are per-language dialogue) and subtitles, with data sizes and whether it looks encrypted.",
            McpJson.Schema(("hash", "string", "USM tag hash", true)),
            (media, tag, _, cancel) => media.DescribeVideo(tag, cancel));

        yield return Media("export_video", "Demux a USM cutscene tag under the project's exports\\video folder: the MPEG video " +
            "elementary stream (.m2v, plays in VLC/ffmpeg), each ADX audio track (.adx plus a decoded .wav) and subtitles (.sbt). " +
            "'channel' keeps one audio track; 'video' false skips the (large) video stream.",
            McpJson.Schema(("hash", "string", "USM tag hash", true), ("channel", "integer", "Only this audio channel", false),
                ("video", "boolean", "Include the video stream (default true)", false)),
            (media, tag, args, cancel) => media.ExportVideo(tag, Path.Combine(Folder("video"), $"{tag:X8}"),
                args["channel"] is null ? null : McpJson.Int(args, "channel", 0), McpJson.Bool(args, "video", true), cancel));
    }
}
