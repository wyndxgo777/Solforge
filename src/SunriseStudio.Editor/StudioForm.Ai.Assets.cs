using System.Globalization;
using System.Text.Json.Nodes;
using Solforge.Mcp;
using SunriseStudio.Animation;
using SunriseStudio.Audio;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using McpPages = Solforge.Mcp.Pages;

namespace SunriseStudio.Editor;

/**
 * The MCP shader, animation and audio reads (SolforgeTools.Assets.cs). Each one reuses the reader the editor pages already use
 * (GameMaterialReader, ClipMetadataReader, AnimationControlReader, SkeletonReader, EntityAnimationCatalog, AudioCatalog) and
 * returns metadata only. References are taken on the UI thread and the reading runs on a worker.
 */
public sealed partial class StudioForm
{
    private const uint AiClipClass = 0x80808F49, AiEntityClass = 0x80809C0F, AiDefinitionClass = 0x808036F6, AiMaterialClass = 0x808071E8;

    /** Wwise HIRC object types by number (index 0 is unused). */
    private static readonly string[] AiHircTypes =
    [
        "", "state", "sound", "action", "event", "random_sequence_container", "switch_container", "actor_mixer", "bus",
        "layer_container", "music_segment", "music_track", "music_switch", "music_random_sequence", "attenuation", "dialogue_event",
        "feedback_bus", "feedback_node", "fx_share_set", "fx_custom", "aux_bus", "lfo", "envelope"
    ];

    /** Clip tag to the animation definitions (and their clip index) that list it, built once per install. */
    private (PackageCatalog Catalog, Dictionary<uint, List<(uint Definition, int Index)>> Users)? aiClipUsers;
    /** Guards the cached animation clip user lookup. */
    private readonly object aiClipUsersLock = new();

    /** References taken on the UI thread (catalog, names, name lookups) for the worker to read with. */
    private sealed record AiAssetContext(PackageCatalog? Catalog, StudioNameIndex? Names, Func<uint, string?> Name,
        Func<uint, string?> ClipName, Func<uint, string?> Hash);

    /** Dispatches a shader, animation or audio tool by name on a worker. */
    private Task<JsonObject> AiAssetAsync(string tool, JsonObject args, CancellationToken cancel) =>
        AiOffThread(() => new AiAssetContext(workspace.Catalog, workspace.Names, workspace.ResolveName, workspace.ResolveClipName, workspace.ResolveHash),
            context =>
            {
                if (context.Catalog is null) return AiError("No installation is open.");
                return tool switch
                {
                    "list_shaders" => AiListShaders(context, args),
                    "get_shader" => AiGetShader(context, args),
                    "list_animations" => AiListAnimations(context, args, cancel),
                    "get_animation" => AiGetAnimation(context, args, cancel),
                    "list_audio" => AiListAudio(context, args),
                    "get_audio" => AiGetAudio(context, args),
                    _ => AiError("Unknown tool.")
                };
            }, cancel);

    /** Reads a tag hash argument; null when missing or invalid. */
    private static uint? AiTagArg(JsonObject args, string name) => SolforgeTools.ParseHash(McpJson.String(args, name));

    // Shaders.

    /** Package shader subtypes: 0-4 match the DXBC stage, compute is 6. */
    private static GameShaderStage? AiShaderStage(int subType) => subType switch
    {
        >= 0 and <= 4 => (GameShaderStage)subType,
        6 => GameShaderStage.Compute,
        _ => null
    };

    /** list_shaders: paged list of installed shaders. */
    private static JsonObject AiListShaders(AiAssetContext context, JsonObject args)
    {
        var catalog = context.Catalog!;
        string stageText = (McpJson.String(args, "stage") ?? "").Trim();
        GameShaderStage? wanted = null;
        if (stageText.Length > 0)
        {
            if (!Enum.TryParse<GameShaderStage>(stageText, true, out var parsed) || int.TryParse(stageText, out _))
                return AiError("stage must be vertex, pixel, geometry, hull, domain or compute.");
            wanted = parsed;
        }
        string package = McpJson.String(args, "package") ?? "", filter = McpJson.String(args, "filter") ?? "";
        var rows = catalog.FindByType(33)
            .Select(entry => (Entry: entry, Stage: AiShaderStage(entry.SubType)))
            .Where(row => row.Stage is not null && (wanted is null || row.Stage == wanted) && Matches(package, row.Entry.PackageName)
                && (filter.Length == 0 || Matches(filter, Hex(row.Entry.TagHash), context.Name(row.Entry.TagHash))));
        return McpPages.Page(rows, Offset(args), Limit(args), row => new JsonObject
        {
            ["tag"] = Hex(row.Entry.TagHash), ["stage"] = row.Stage!.Value.ToString().ToLowerInvariant(), ["bytecode"] = Hex(row.Entry.Reference),
            ["package"] = row.Entry.PackageName, ["size"] = row.Entry.Size, ["name"] = context.Name(row.Entry.TagHash)
        });
    }

    /** get_shader: stages and constants of one shader or material. */
    private static JsonObject AiGetShader(AiAssetContext context, JsonObject args)
    {
        var catalog = context.Catalog!;
        uint tag = AiTagArg(args, "tag") ?? 0;
        if (!catalog.TryGetEntry(tag, out var entry)) return AiError($"Tag {Hex(tag)} is not installed.");
        var reader = new GameMaterialReader(catalog);
        var result = new JsonObject { ["tag"] = Hex(tag), ["name"] = context.Name(tag) };
        if (entry.Reference == AiMaterialClass)
        {
            var material = reader.Read(tag);
            result["kind"] = "material";
            result["stateOverrides"] = Hex(material.StateOverrides);
            result["stages"] = new JsonArray(new[] { material.VertexShader, material.PixelShader, material.ComputeShader }
                .OfType<GameShaderInfo>().Select(shader => (JsonNode)AiShaderJson(shader, args)).ToArray());
            result["additionalShaders"] = new JsonArray(material.AdditionalShaderHeaders.Select(header => (JsonNode)new JsonObject
            {
                ["field"] = $"0x{header.FieldOffset:X}", ["tag"] = Hex(header.Tag),
                ["stage"] = AiShaderStage(header.PackageSubtype)?.ToString().ToLowerInvariant()
            }).ToArray());
            result["textures"] = new JsonArray(material.Textures.Select(texture => (JsonNode)new JsonObject
            {
                ["stage"] = texture.Stage.ToString().ToLowerInvariant(), ["slot"] = texture.Slot, ["texture"] = Hex(texture.TextureTag),
                ["name"] = context.Name(texture.TextureTag), ["declared"] = texture.DeclaredByShader, ["dimension"] = texture.ShaderDimension,
                ["width"] = texture.Width, ["height"] = texture.Height, ["depth"] = texture.Depth, ["arraySize"] = texture.ArraySize,
                ["format"] = texture.Format.ToString(), ["srgb"] = texture.IsSrgb
            }).ToArray());
            result["emptyTextureSlots"] = new JsonArray(material.EmptyTextureSlots.OrderBy(slot => slot.Stage).ThenBy(slot => slot.Slot)
                .Select(slot => (JsonNode)$"{slot.Stage.ToString().ToLowerInvariant()} t{slot.Slot}").ToArray());
            result["diagnostics"] = new JsonArray(material.Diagnostics.Select(line => (JsonNode)line).ToArray());
            return result;
        }
        if (entry.Type != 33 || AiShaderStage(entry.SubType) is not { } stage)
            return AiError("That tag is neither a shader header nor a material; use list_shaders, or search_assets with kind Material.");
        result["kind"] = "shader";
        foreach (var (key, value) in AiShaderJson(reader.ReadShader(tag, stage), args).ToArray())
            result[key] = value?.DeepClone();
        result["usersIndexed"] = context.Names is not null;
        result["users"] = new JsonArray((context.Names?.GetAssetOwners(tag) ?? []).Take(100)
            .Select(owner => (JsonNode)new JsonObject { ["tag"] = Hex(owner), ["name"] = context.Name(owner) }).ToArray());
        return result;
    }

    /** Stage, profile, constant buffers, texture and sampler slots, declarations and an optional page of disassembly. */
    private static JsonObject AiShaderJson(GameShaderInfo shader, JsonObject args)
    {
        var buffers = new JsonArray();
        var samplers = new JsonArray();
        foreach (string line in shader.Declarations)
        {
            // dcl_constantbuffer CB12[34], immediateIndexed
            if (line.StartsWith("dcl_constantbuffer CB", StringComparison.Ordinal))
            {
                string rest = line["dcl_constantbuffer CB".Length..];
                int open = rest.IndexOf('['), close = rest.IndexOf(']');
                if (open > 0 && close > open && int.TryParse(rest[..open], NumberStyles.None, CultureInfo.InvariantCulture, out int slot)
                    && int.TryParse(rest[(open + 1)..close], NumberStyles.None, CultureInfo.InvariantCulture, out int registers))
                    buffers.Add(new JsonObject
                    {
                        ["slot"] = slot, ["float4s"] = registers, ["bytes"] = registers * 16,
                        ["access"] = rest[(close + 1)..].TrimStart(',', ' ').Trim()
                    });
            }
            // dcl_sampler s0, mode_default
            else if (line.StartsWith("dcl_sampler s", StringComparison.Ordinal))
            {
                string rest = line["dcl_sampler s".Length..];
                int comma = rest.IndexOf(',');
                if (int.TryParse(comma < 0 ? rest : rest[..comma], NumberStyles.None, CultureInfo.InvariantCulture, out int slot))
                    samplers.Add(new JsonObject { ["slot"] = slot, ["mode"] = comma < 0 ? null : rest[(comma + 1)..].Trim() });
            }
        }
        var result = new JsonObject
        {
            ["tag"] = Hex(shader.HeaderTag), ["bytecode"] = Hex(shader.BytecodeTag), ["stage"] = shader.Stage.ToString().ToLowerInvariant(),
            ["profile"] = shader.Profile, ["constantBuffers"] = buffers,
            ["textureSlots"] = new JsonArray(shader.TextureSlots.Select(slot => (JsonNode)new JsonObject { ["slot"] = slot.Slot, ["dimension"] = slot.Dimension }).ToArray()),
            ["samplers"] = samplers,
            ["declarations"] = new JsonArray(shader.Declarations.Select(line => (JsonNode)line).ToArray())
        };
        if (McpJson.Bool(args, "disassembly", false))
        {
            string[] lines = shader.Disassembly.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
            int offset = Math.Min(Offset(args), lines.Length), limit = Math.Clamp(McpJson.Int(args, "limit", 200), 1, 2000);
            int count = Math.Min(limit, lines.Length - offset);
            result["disassembly"] = new JsonObject
            {
                ["total"] = lines.Length, ["offset"] = offset, ["count"] = count,
                ["next"] = offset + count < lines.Length ? (int?)(offset + count) : null,
                ["text"] = string.Join('\n', lines, offset, count)
            };
        }
        return result;
    }

    // Animations.

    /** Name of an animation clip, from the lookup or the catalog. */
    private static string? AiClipName(AiAssetContext context, uint clip) => context.ClipName(clip) ?? context.Catalog!.GetName(clip);

    /** list_animations: paged list of animation clips. */
    private static JsonObject AiListAnimations(AiAssetContext context, JsonObject args, CancellationToken cancel)
    {
        var catalog = context.Catalog!;
        string filter = McpJson.String(args, "filter") ?? "";
        if (McpJson.String(args, "entity") is { Length: > 0 } entityText)
        {
            if (SolforgeTools.ParseHash(entityText) is not { } entity || !catalog.TryGetEntry(entity, out var entityEntry)
                || entityEntry.Reference != AiEntityClass)
                return AiError("entity must be an installed entity tag (0x...).");
            var clips = new EntityAnimationCatalog(catalog).Read(entity, resolveNames: true, cancellationToken: cancel)
                .Select(clip => (Clip: clip, Name: clip.Name ?? AiClipName(context, clip.ClipTag)))
                .Where(row => AllWords(filter, $"{row.Name} {Hex(row.Clip.ClipTag)}"));
            var page = McpPages.Page(clips, Offset(args), Limit(args), row => new JsonObject
            {
                ["tag"] = Hex(row.Clip.ClipTag), ["name"] = row.Name, ["definition"] = Hex(row.Clip.DefinitionTag),
                ["index"] = row.Clip.Index, ["config"] = Hex(row.Clip.ConfigTag)
            });
            page["entity"] = Hex(entity);
            return page;
        }
        var rows = catalog.FindByClass(AiClipClass).Select(entry => (Entry: entry, Name: AiClipName(context, entry.TagHash)))
            .Where(row => AllWords(filter, $"{row.Name} {Hex(row.Entry.TagHash)} {row.Entry.PackageName}"));
        return McpPages.Page(rows, Offset(args), Limit(args), row => new JsonObject
        {
            ["tag"] = Hex(row.Entry.TagHash), ["name"] = row.Name, ["package"] = row.Entry.PackageName, ["size"] = row.Entry.Size
        });
    }

    /** get_animation: metadata of one clip or animation entity. */
    private JsonObject AiGetAnimation(AiAssetContext context, JsonObject args, CancellationToken cancel)
    {
        var catalog = context.Catalog!;
        uint tag = AiTagArg(args, "tag") ?? 0;
        if (!catalog.TryGetEntry(tag, out var entry)) return AiError($"Tag {Hex(tag)} is not installed.");
        if (entry.Reference == AiEntityClass)
        {
            var clips = new EntityAnimationCatalog(catalog).Read(tag, cancellationToken: cancel);
            return new JsonObject
            {
                ["tag"] = Hex(tag), ["kind"] = "entity", ["name"] = context.Name(tag), ["skeleton"] = AiSkeleton(context, tag, args),
                ["clips"] = clips.Count, ["definitions"] = new JsonArray(clips.Select(clip => clip.DefinitionTag).Distinct()
                    .Select(definition => (JsonNode)Hex(definition)).ToArray()),
                ["next"] = clips.Count > 0 ? "list_animations with entity=" + Hex(tag) : null
            };
        }
        if (entry.Reference != AiClipClass)
            return AiError("That tag is neither an animation clip nor an entity; use list_animations.");
        var info = new ClipMetadataReader(catalog).Read(tag);
        var result = new JsonObject
        {
            ["tag"] = Hex(tag), ["kind"] = "clip", ["name"] = AiClipName(context, tag), ["package"] = entry.PackageName,
            ["frames"] = info.FrameCount, ["sampleRate"] = info.SampleRate, ["seconds"] = Math.Round(info.DurationSeconds, 4),
            ["payloadSections"] = AiSections(info.PayloadSections)
        };
        try
        {
            var clip = new AnimationControlReader(catalog).Read(tag);
            var tracks = McpPages.Page(clip.Tracks, Offset(args), Limit(args), track => new JsonObject
            {
                ["control"] = track.ControlIndex, ["scaleKeys"] = track.ScaleKeys.Count,
                ["rotationKeys"] = track.RotationKeys.Count, ["translationKeys"] = track.TranslationKeys.Count
            });
            tracks["nodes"] = clip.NodeCount;
            tracks["controls"] = clip.ControlCount;
            tracks["poseMode"] = clip.PoseMode;
            tracks["poseSpace"] = clip.PoseSpace is not null;
            tracks["retargetKeys"] = clip.RetargetKeys.Count;
            tracks["groups"] = new JsonArray(clip.ControlGroups.Select(group => (JsonNode)new JsonObject
            {
                ["hash"] = Hex(group.NameHash), ["name"] = context.Hash(group.NameHash), ["controls"] = group.ControlCount
            }).ToArray());
            tracks["auxiliarySections"] = AiSections(clip.AuxiliarySections);
            result["tracks"] = tracks;
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            result["tracksError"] = error.Message;
        }
        var users = AiClipUsers(catalog, cancel).GetValueOrDefault(tag) ?? [];
        result["users"] = new JsonArray(users.Take(100).Select(user => (JsonNode)new JsonObject
        {
            ["definition"] = Hex(user.Definition), ["index"] = user.Index, ["name"] = context.Name(user.Definition)
        }).ToArray());
        if (McpJson.String(args, "entity") is { Length: > 0 } entityText)
        {
            if (SolforgeTools.ParseHash(entityText) is not { } entity || !catalog.TryGetEntry(entity, out var entityEntry)
                || entityEntry.Reference != AiEntityClass)
                return AiError("entity must be an installed entity tag (0x...).");
            result["entity"] = Hex(entity);
            result["linked"] = new EntityAnimationCatalog(catalog).Read(entity, cancellationToken: cancel).Any(clip => clip.ClipTag == tag);
            result["skeleton"] = AiSkeleton(context, entity, args);
        }
        else result["skeletonHint"] = "Pass entity (an entity that plays this clip) for its skeleton.";
        return result;
    }

    /** JSON for animation payload sections. */
    private static JsonArray AiSections(IEnumerable<AnimationPayloadSection> sections) =>
        new(sections.Select(section => (JsonNode)new JsonObject
        {
            ["field"] = $"0x{section.PointerField:X}", ["offset"] = section.DataOffset, ["class"] = Hex(section.ClassHash)
        }).ToArray());

    /** JSON for the skeleton of an entity. */
    private static JsonObject AiSkeleton(AiAssetContext context, uint entity, JsonObject args)
    {
        try
        {
            var rig = new SkeletonReader(context.Catalog!) { NameResolver = context.Hash }.Read(entity);
            var page = McpPages.Page(rig.Bones, Offset(args), Limit(args), bone => new JsonObject
            {
                ["index"] = bone.Index, ["name"] = bone.Name, ["hash"] = Hex(bone.NameHash), ["parent"] = bone.ParentIndex
            });
            page["resource"] = Hex(rig.ResourceTag);
            page["models"] = new JsonArray(rig.ModelTags.Select(model => (JsonNode)Hex(model)).ToArray());
            page["clothModels"] = rig.ClothModels.Count;
            page["maximumBindInverseError"] = rig.MaximumBindInverseError;
            return page;
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or ArgumentException or KeyNotFoundException)
        {
            return new JsonObject { ["error"] = error.Message };
        }
    }

    /** Every animation definition's clip list, read once per install; definitions that fail to read are skipped. */
    private Dictionary<uint, List<(uint Definition, int Index)>> AiClipUsers(PackageCatalog catalog, CancellationToken cancel)
    {
        lock (aiClipUsersLock)
        {
            if (aiClipUsers is { } cached && ReferenceEquals(cached.Catalog, catalog)) return cached.Users;
            var users = new Dictionary<uint, List<(uint, int)>>();
            var reader = new EntityAnimationCatalog(catalog);
            foreach (var definition in catalog.FindByClass(AiDefinitionClass))
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    foreach (var clip in reader.ReadDefinition(definition.TagHash, cancellationToken: cancel))
                    {
                        if (!users.TryGetValue(clip.ClipTag, out var list)) users[clip.ClipTag] = list = [];
                        list.Add((definition.TagHash, clip.Index));
                    }
                }
                catch (Exception error) when (error is NotSupportedException or InvalidDataException or ArgumentException
                    or IndexOutOfRangeException or IOException or KeyNotFoundException) { }
            }
            aiClipUsers = (catalog, users);
            return users;
        }
    }

    // Audio.

    /** Short kind label of an audio package entry. */
    private static string AiAudioKind(PackageEntry entry) =>
        entry.Reference == AudioCatalog.EventClass ? "event" : entry.SubType == 6 ? "stream" : "bank";

    /** list_audio: paged list of audio tags. */
    private static JsonObject AiListAudio(AiAssetContext context, JsonObject args)
    {
        string kind = (McpJson.String(args, "kind") ?? "").Trim().ToLowerInvariant(), filter = McpJson.String(args, "filter") ?? "";
        if (kind is not ("" or "event" or "bank" or "stream")) return AiError("kind must be event, bank or stream.");
        var rows = context.Catalog!.Entries.Where(AudioCatalog.Supports)
            .Select(entry => (Entry: entry, Kind: AiAudioKind(entry)))
            .Where(row => kind.Length == 0 || row.Kind == kind)
            .Select(row => (row.Entry, row.Kind, Name: context.Name(row.Entry.TagHash)))
            .Where(row => AllWords(filter, $"{row.Name} {Hex(row.Entry.TagHash)} {row.Entry.PackageName}"));
        return McpPages.Page(rows, Offset(args), Limit(args), row => new JsonObject
        {
            ["tag"] = Hex(row.Entry.TagHash), ["kind"] = row.Kind, ["name"] = row.Name, ["package"] = row.Entry.PackageName,
            ["size"] = row.Entry.Size, ["wwiseId"] = row.Kind == "event" ? null : (uint?)row.Entry.Reference
        });
    }

    /** get_audio: metadata of one audio tag. */
    private static JsonObject AiGetAudio(AiAssetContext context, JsonObject args)
    {
        uint tag = AiTagArg(args, "tag") ?? 0;
        if (!context.Catalog!.TryGetEntry(tag, out var entry)) return AiError($"Tag {Hex(tag)} is not installed.");
        if (!AudioCatalog.Supports(entry)) return AiError("That tag is not an audio event, bank or stream; use list_audio.");
        var document = new AudioCatalog(context.Catalog!, context.Name).Read(tag);
        var result = new JsonObject
        {
            ["tag"] = Hex(tag), ["kind"] = document.Kind.ToLowerInvariant(), ["name"] = document.Name, ["package"] = entry.PackageName,
            ["eventId"] = document.EventId, ["relatedEventId"] = document.RelatedEventId
        };
        if (document.Bank is { } bank)
        {
            var types = new JsonObject();
            foreach (var group in bank.Objects.GroupBy(value => value.Type).OrderBy(group => group.Key))
                types[group.Key < AiHircTypes.Length && group.Key > 0 ? AiHircTypes[group.Key] : $"type_{group.Key}"] = group.Count();
            result["bank"] = new JsonObject
            {
                ["tag"] = Hex(bank.Tag), ["name"] = context.Name(bank.Tag), ["id"] = bank.Id, ["version"] = bank.Version,
                ["bytes"] = bank.Bytes.Length, ["objects"] = bank.Objects.Count, ["objectTypes"] = types, ["embeddedMedia"] = bank.EmbeddedMedia.Count
            };
        }
        result["media"] = new JsonArray(document.Media.Take(500).Select(media => (JsonNode)new JsonObject
        {
            ["id"] = media.Id, ["tag"] = media.Tag is { } stream ? Hex(stream) : null, ["name"] = media.Name, ["size"] = media.Size,
            ["embedded"] = media.Embedded is not null
        }).ToArray());
        result["mediaTotal"] = document.Media.Count;
        result["diagnostics"] = new JsonArray(document.Diagnostics.Select(line => (JsonNode)line).ToArray());
        return result;
    }
}
