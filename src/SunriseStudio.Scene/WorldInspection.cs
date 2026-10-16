using System.Globalization;
using System.Numerics;

namespace SunriseStudio.Scene;

/** Inspection preserves decoded facts and references instead of reducing selection to one asset tag. */
internal static class WorldInspection
{
    /** Creates the world objects for a scene. */
    internal static IReadOnlyList<WorldObject> CreateObjects(SceneDocument scene,
        IReadOnlyDictionary<uint, uint> aliases, Func<uint, string> name,
        IReadOnlyDictionary<uint, IReadOnlyList<WorldStateKey>> authoredOwners)
    {
        var result = new List<WorldObject>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        string Identity(string semantic)
        {
            int ordinal = occurrences.GetValueOrDefault(semantic);
            occurrences[semantic] = ordinal + 1;
            return ordinal == 0 ? semantic : $"{semantic}#{ordinal}";
        }
        var bubbles = scene.Bubbles.GroupBy(x => x.NameHash).ToDictionary(x => x.Key, x => x.First().Name);
        uint Canonical(uint hash) => aliases.GetValueOrDefault(hash, hash);
        string Bubble(uint hash) => hash == 0 ? "Shared" : bubbles.GetValueOrDefault(Canonical(hash), $"0x{Canonical(hash):X8}");
        IReadOnlyList<WorldStateKey> Owners(uint mission, uint bubble, int state) => mission != 0 && state >= 0
            ? [new(mission, Canonical(bubble), state)] : [];
        for (int i = 0; i < scene.Instances.Count; i++)
        {
            var item = scene.Instances[i];
            // A decorator record keys by its payload and index, so moving it keeps its ID.
            string placement = item.InstanceSourceTag != 0 || item.PlacementSourceTag != 0
                ? $"{item.InstanceSourceTag:X8}:{item.InstanceSourceIndex}:{item.PlacementSourceTag:X8}:{item.PlacementSourceOffset}:{item.PlacementIdentity:X16}"
                : item.ObjectPlacement is { } record ? $"record:{record.SourceTag:X8}:{record.SourceIndex}"
                : $"transform:{TransformIdentity(item.Transform):X16}";
            string id = Identity($"mesh:{placement}:{item.Mesh.TagHash:X8}:{item.Mesh.PartIndex}:{item.Mesh.LodLevel}:{item.Mesh.Native?.RenderStage ?? -1}:{Canonical(item.BubbleHash):X8}:{item.ActivityTag:X8}:{item.StateOrdinal}:{item.Layer}");
            var (meshPosition, meshExtents) = Placement(item);
            result.Add(new(id, item.Name, WorldObjectKind.Mesh, item, item.ObjectClassTag != 0 ? item.ObjectClassTag : item.Mesh.TagHash,
                Canonical(item.BubbleHash), Bubble(item.BubbleHash), item.ActivityTag, item.StateOrdinal, item.StateName,
                meshPosition, meshExtents, Owners(item.ActivityTag, item.BubbleHash, item.StateOrdinal)));
        }
        for (int i = 0; i < scene.Markers.Count; i++)
        {
            var item = scene.Markers[i];
            string placement = item.SourceTag != 0
                ? $"{item.SourceTag:X8}:{item.SourceOffset}:{item.PlacementIdentity:X16}"
                : $"point:{TransformIdentity(Matrix4x4.CreateFromQuaternion(item.Rotation) * Matrix4x4.CreateTranslation(item.Position)):X16}";
            string slot = item.Slot is { } identity ? $"{identity.RegistryKey:X8}:{identity.Type}:{identity.Index}" : "none";
            string effect = item.Effect is { } description ? $"{description.SourceTag:X8}:{description.SourceOffset}:{description.NodeName:X8}" : "none";
            string id = Identity($"marker:{placement}:{item.TagHash:X8}:{item.Layer}:{Canonical(item.BubbleHash):X8}:{item.ActivityTag:X8}:{item.StateOrdinal}:{slot}:{effect}");
            var (markerPosition, markerExtents) = Placement(item);
            result.Add(new(id, item.Name, WorldObjectKind.Marker, item, item.TagHash,
                Canonical(item.BubbleHash), Bubble(item.BubbleHash), item.ActivityTag, item.StateOrdinal, item.StateName,
                markerPosition, markerExtents, Owners(item.ActivityTag, item.BubbleHash, item.StateOrdinal)));
        }
        foreach (var table in scene.LightTables)
            foreach (var row in table.Table.Rows)
            {
                var marker = table.OwnerMarker;
                var light = new WorldLight(table, row);
                var (lightPosition, lightExtents) = Placement(light);
                result.Add(new(Identity($"light:{table.Table.TagHash:X8}:{row.Index}:{table.PlacementSourceTag:X8}:{table.PlacementSourceOffset}:{Canonical(table.BubbleHash):X8}:{marker?.ActivityTag ?? 0:X8}:{marker?.StateOrdinal ?? -1}"),
                    $"{name(table.Table.TagHash)} light {row.Index}", WorldObjectKind.Light, light,
                    table.Table.TagHash, Canonical(table.BubbleHash), Bubble(table.BubbleHash), marker?.ActivityTag ?? 0,
                    marker?.StateOrdinal ?? -1, marker?.StateName ?? "", lightPosition, lightExtents,
                    Owners(marker?.ActivityTag ?? 0, table.BubbleHash, marker?.StateOrdinal ?? -1)));
            }
        foreach (var item in scene.AuthoredObjects)
        {
            var owners = authoredOwners.GetValueOrDefault(item.TagHash, []);
            var uniqueBubbles = owners.Select(x => x.BubbleHash).Distinct().ToArray();
            uint bubble = uniqueBubbles.Length == 1 ? uniqueBubbles[0] : 0;
            result.Add(new(Identity($"object:{item.TagHash:X8}:{item.RegistryKey:X8}:{scene.Scenario?.TagHash ?? 0:X8}"), name(item.TagHash), WorldObjectKind.AuthoredObject,
                item, item.TagHash, bubble, uniqueBubbles.Length > 1 ? "Multiple bubbles" : Bubble(bubble),
                scene.Scenario?.TagHash ?? 0, owners.Count == 1 ? owners[0].Ordinal : -1, "", null, null, owners));
        }
        var squadAnchors = scene.Markers.Where(x => x.Layer == SceneLayer.Squads)
            .ToLookup(x => (x.TagHash, x.Slot, Canonical(x.BubbleHash)));
        foreach (var item in scene.Squads)
        {
            var states = scene.Scenario?.Bubbles.Where(x => x.NameHash == Canonical(item.BubbleHash))
                .SelectMany(x => x.States).Where(x => x.EntryIndex == item.EntryIndex).ToArray() ?? [];
            var owners = states.Select(x => new WorldStateKey(scene.Scenario!.TagHash, x.BubbleNameHash, x.Ordinal)).ToArray();
            var anchors = squadAnchors[(item.ConfigTag, item.Slot, Canonical(item.BubbleHash))].ToArray();
            result.Add(new(Identity($"squad:{item.ConfigTag:X8}:{item.Slot.RegistryKey:X8}:{item.Slot.Type}:{item.Slot.Index}:{item.EntryIndex}:{Canonical(item.BubbleHash):X8}:{scene.Scenario?.TagHash ?? 0:X8}"),
                anchors.FirstOrDefault()?.Name ?? name(item.ConfigTag), WorldObjectKind.Squad, item, item.ConfigTag,
                Canonical(item.BubbleHash), Bubble(item.BubbleHash), scene.Scenario?.TagHash ?? 0,
                states.Length == 1 ? states[0].Ordinal : -1, states.Length == 1 ? states[0].Name : "",
                anchors.Length == 1 ? anchors[0].Position : null, null, owners));
        }
        if (scene.Scenario is { } scenario)
            foreach (var state in scenario.Bubbles.SelectMany(x => x.States))
                result.Add(new(Identity($"state:{scenario.TagHash:X8}:{state.BubbleNameHash:X8}:{state.Ordinal}"), state.Name,
                    WorldObjectKind.State, state, state.EntryTag, state.BubbleNameHash, Bubble(state.BubbleNameHash),
                    scenario.TagHash, state.Ordinal, state.Name, null, null,
                    [new(scenario.TagHash, state.BubbleNameHash, state.Ordinal)]));
        return result.AsReadOnly();
    }

    /** The position and extents a row shows for a placed source; null for other kinds. */
    internal static (Vector3? Position, Vector3? Extents) Placement(object source) => source switch
    {
        MeshInstance mesh => (mesh.Transform.Translation, null),
        SceneMarker marker => (marker.Position, marker.Extents),
        WorldLight light => (light.Row.Translation, null),
        _ => (null, null)
    };

    /** Details retain source offsets, slot identities, dependencies and decoded renderer metadata. */
    internal static WorldObjectInspection Describe(WorldDocument world, WorldObject item)
    {
        var values = new List<WorldProperty>();
        var references = new List<WorldReference>();
        var related = new List<object>();
        var slots = new List<AuthoredSlotInfo>();
        void Add(string group, string name, object? value) => values.Add(new(group, name, Format(value)));
        void Ref(string name, uint tag, int offset = -1)
        { if (tag is not (0 or uint.MaxValue)) references.Add(new(name, tag, offset)); }
        void Containers(IReadOnlyList<MapContainerInfo> containers)
        {
            foreach (var container in containers)
            {
                Ref("Map container", container.TagHash);
                Add("Map layers", $"Container {container.TagHash:X8}", string.Join(", ", container.MapGlobalIndices
                    .Select(index => world.Scene.MapLayers.FirstOrDefault(x => x.Index == index)?.Name ?? index.ToString())));
            }
        }
        Add("Object", "Name", item.Name); Add("Object", "Kind", item.Kind); Add("Ownership", "Bubble", item.BubbleName);
        Add("Ownership", "Bubble hash", $"0x{item.BubbleHash:X8}");
        Ref(world.HasMapContext ? "Map root" : "Scene root", world.Scene.TagHash);
        if (item.MissionTag != 0) Ref("Mission", item.MissionTag);
        foreach (var owner in item.Owners)
            Add("Ownership", "State", $"{owner.MissionTag:X8} / {owner.BubbleHash:X8} / {owner.Ordinal}");
        switch (item.Source)
        {
            case MeshInstance mesh:
                Add("Geometry", "Part", mesh.Mesh.PartIndex); Add("Geometry", "LOD", mesh.Mesh.LodLevel);
                Add("Geometry", "Vertices", mesh.Mesh.Vertices.Length); Add("Geometry", "Triangles", mesh.Mesh.Indices.Length / 3);
                Add("Geometry", "Category", mesh.Category); Add("Geometry", "Layer", mesh.Layer);
                Add("Geometry", "Tags", string.Join(", ", mesh.Tags)); Add("Transform", "World", mesh.Transform);
                Add("Source", "Instance index", mesh.InstanceSourceIndex); Add("Source", "Instance bytes", mesh.NativeInstanceData.Length);
                Add("Source", "Placement identity", $"0x{mesh.PlacementIdentity:X16}");
                Ref("Mesh", mesh.Mesh.TagHash); Ref("Material", mesh.Mesh.MaterialTag); Ref("Object class", mesh.ObjectClassTag);
                Ref("Model config", mesh.ModelSourceConfigTag); Ref("Instance list", mesh.InstanceSourceTag);
                Ref("Placement", mesh.PlacementSourceTag, mesh.PlacementSourceOffset);
                Add("Source", "Model resource class", $"0x{mesh.ModelSourceResourceClass:X8}");
                Add("Geometry", "Cloth simulation needed", mesh.RequiresClothSimulation);
                if (mesh.ModelSourceConfigTag != 0)
                {
                    Add("Geometry", "Logical part", mesh.Mesh.LogicalPartIndex);
                    Add("Geometry", "Initial visibility", mesh.InitiallyVisible switch { true => "Visible", false => "Inactive", _ => "Unresolved" });
                    if (mesh.InitialVisibilityReason is { } visibility) Add("Geometry", "Visibility reason", visibility);
                }
                Containers(mesh.MapContainers);
                foreach (var owner in mesh.OcclusionOwners) Add("Occlusion", "Owner", owner);
                if (mesh.AttachmentSource is not null) Add("Attachment", "Source", mesh.AttachmentSource);
                if (mesh.Mesh.Native is { } native)
                {
                    Add("Renderer", "Kind", native.Kind); Add("Renderer", "Stage", $"{native.RenderStage} {RenderNames.GetStageName(native.RenderStage)}");
                    Add("Renderer", "Material states", $"0x{native.MaterialStateOverrides:X8}");
                    Add("Renderer", "Input layout verified", native.InputLayoutVerified);
                    Add("Renderer", "Input signature fits", native.InputLayoutFitsSignature);
                    Add("Renderer", "Layout evidence", native.InputLayoutEvidence); Add("Renderer", "Layout status", native.LayoutStatus);
                    Add("Renderer", "Declaration", native.NativeDeclarationId);
                    Add("Renderer", "Primitive", native.GamePrimitiveType); Add("Renderer", "First index", native.FirstIndex);
                    Add("Renderer", "Index count", native.IndexCount); Add("Renderer", "Base vertex", native.BaseVertex);
                    Add("Renderer", "Index width", native.Is32Bit ? 32 : 16); Add("Renderer", "Index bytes", native.IndexData.Length);
                    Add("Renderer", "Position scale", native.PositionScale); Add("Renderer", "Position offset", native.PositionOffset);
                    Add("Renderer", "UV transform", native.UvTransform); Add("Renderer", "Terrain offset", native.TerrainOffset);
                    Add("Renderer", "Source header bytes", native.SourceHeader.Length);
                    Add("Renderer", "Resource word 38", $"0x{native.ResourceWord38:X8}");
                    Add("Renderer", "Buffer group word 0C", $"0x{native.BufferGroupWord0C:X8}");
                    Ref("Vertex shader header", native.VertexShaderTag); Ref("Vertex shader data", native.VertexShaderDataTag);
                    Ref("Native declaration", native.NativeDeclarationTag); Ref("Index header", native.IndexHeaderTag);
                    Ref("Index data", native.IndexDataTag); Ref("Terrain dye texture", native.TerrainDyeTexture);
                    foreach (var stream in native.VertexStreams)
                    {
                        Add("Renderer", "Vertex stream", $"Stride {stream.Stride}; type {stream.Type}; {stream.Data.Length} bytes");
                        Ref("Vertex header", stream.HeaderTag); Ref("Vertex data", stream.DataTag);
                    }
                    if (native.SkinWeightStream is { } skin)
                    { Ref("Skin header", skin.HeaderTag); Ref("Skin data", skin.DataTag); Add("Renderer", "Skin stride", skin.Stride); }
                    foreach (var input in native.InputElements) Add("Renderer", "Input element", input);
                    foreach (var input in native.ShaderInputs) Add("Renderer", "Shader input", input);
                }
                related.AddRange(world.Scene.Markers.Where(x => x.SourceTag == mesh.PlacementSourceTag
                    && x.SourceOffset == mesh.PlacementSourceOffset && mesh.PlacementSourceTag != 0));
                break;
            case SceneMarker marker:
                Add("Marker", "Layer", marker.Layer); Add("Marker", "Category", marker.Category);
                Add("Marker", "Semantic", marker.Semantic); Add("Marker", "Tags", string.Join(", ", marker.Tags));
                Add("Transform", "Position", marker.Position); Add("Transform", "Rotation", marker.Rotation);
                Add("Transform", "Extents", marker.Extents); Add("Marker", "Color", marker.Color);
                Add("Source", "Placement identity", $"0x{marker.PlacementIdentity:X16}");
                Add("Source", "Ambiguous association", marker.AssociationIsAmbiguous);
                Add("Names", "Evidence", marker.NameEvidence); Add("Names", "Name hash", $"0x{marker.NameHash:X8}");
                Add("Names", "Source slot", marker.NameSourceSlot); Add("Slot", "Identity", marker.Slot);
                Ref("Asset", marker.TagHash); Ref("Placement", marker.SourceTag, marker.SourceOffset);
                Ref("Name source", marker.NameSourceTag, marker.NameSourceOffset);
                Ref("Name reference", marker.NameReferenceTag, marker.NameReferenceOffset);
                Containers(marker.MapContainers);
                if (marker.Effect is { } effect)
                {
                    Add("Effect", "Kind", effect.Kind); Add("Effect", "Node hash", $"0x{effect.NodeName:X8}");
                    Add("Effect", "Start minimum", effect.StartMin); Add("Effect", "Start maximum", effect.StartMax);
                    Add("Effect", "Duration minimum", effect.DurationMin); Add("Effect", "Duration maximum", effect.DurationMax);
                    Add("Effect", "Variant resolved", effect.VariantResolved); Add("Effect", "Transform", effect.Transform);
                    Add("Effect", "Bone", effect.Bone); Add("Effect", "Local transform", effect.Local);
                    if (effect.Activation is { } activation)
                    {
                        Add("Effect", "Trigger", activation.Trigger()); Add("Effect", "Plays by default", activation.PlaysByDefault());
                        Add("Effect", "Sequence", $"0x{activation.SequenceTag:X8} work node {activation.WorkIndex}; "
                            + $"autostart {activation.AutoStart}");
                        Add("Effect", "Timing", $"delay {activation.Delay:0.###} s; serial start "
                            + (activation.SerialStart is { } serial ? $"{serial:0.###} s" : "never") + "; "
                            + (activation.Timed ? $"duration {activation.Duration:0.###} s" : "no timer")
                            + (activation.Continuous ? "; continuous" : "; plays once"));
                        foreach (var gate in activation.Gates)
                            Add("Effect", "Gate", $"channel 0x{gate.Channel:X8} in [{gate.Min:G4}, {gate.Max:G4})");
                        foreach (var pick in activation.RandomPicks)
                            Add("Effect", "Random pick", $"flow {pick.Flow} branch {pick.Branch} of {pick.Branches}");
                    }
                    else if (effect.Kind == EffectKind.ParticleSystem) Add("Effect", "Trigger", "sequence not read");
                    Ref("Effect entity", effect.EntityTag); Ref("Effect source", effect.SourceTag, effect.SourceOffset);
                    for (int index = 0; index < effect.Systems.Count; index++)
                    {
                        var system = effect.Systems[index];
                        Ref("Effect system", system.Tag); Ref("Emitter", system.EmitterTag);
                        Ref("Raster material", system.RasterMaterial); Ref("Effect mesh", system.MeshTag);
                        Add("Effect", $"System {system.Tag:X8} GPU simulation", system.GpuSimulated);
                        var rows = index < effect.Locations.Count ? effect.Locations[index] : [];
                        for (int slot = 0; slot < rows.Count; slot++)
                        {
                            var location = rows[slot];
                            string sources = string.Join(", ", location.Sources.Select(source =>
                                $"kind {source.Kind} point 0x{source.Point:X8} flags 0x{source.Flags:X2}"));
                            Add("Effect", $"System {system.Tag:X8} slot {slot}", $"Location {location.Index} 0x{location.Name:X8}; "
                                + $"count {location.Count}; {sources}; {location.Frames.Count} frames");
                            for (int frame = 0; frame < location.Frames.Count; frame++)
                            {
                                var value = location.Frames[frame];
                                Add("Effect", $"System {system.Tag:X8} slot {slot} frame {frame}", $"{value.Mode}"
                                    + (value.UpPass ? " with up pass" : "") + $"; bone {value.Bone}; "
                                    + $"at {Format((value.Local * value.BoneTransform).Translation)}");
                            }
                        }
                        if (system.Emitter is { } emitter)
                            Add("Effect", $"System {system.Tag:X8} transform slots", $"spawn {emitter.SpawnSlot}; "
                                + $"simulation {emitter.EmitterSlot}; {emitter.SlotCount} in all; "
                                + $"slot list [{string.Join(", ", emitter.SlotList)}]"
                                + (effect.SpawnMissing(index) ? "; spawn point missing, emits nothing" : ""));
                        foreach (uint material in system.ComputeMaterials) Ref("Compute material", material);
                        foreach (var channel in system.Emitter?.Channels ?? [])
                            Add("Emitter", channel.Name, $"Index {channel.Index}; kind {channel.Kind}; slot {channel.Slot}; {Format(channel.Constant)}");
                    }
                    foreach (var flare in effect.FlareElements)
                    { Ref("Flare material", flare.Material); Ref("Flare parameters", flare.Parameters); }
                }
                if (marker.Slot is { } markerSlot)
                {
                    var objects = world.Scene.AuthoredObjects.Where(x => x.RegistryKey == markerSlot.RegistryKey).ToArray();
                    related.AddRange(objects); slots.AddRange(objects.SelectMany(x => x.Slots));
                }
                related.AddRange(world.Scene.Instances.Where(x => x.PlacementSourceTag == marker.SourceTag
                    && x.PlacementSourceOffset == marker.SourceOffset && marker.SourceTag != 0));
                break;
            case WorldLight light:
                Add("Light", "Index", light.Row.Index); Add("Light", "Renderer", light.Owner.Table.RendererName);
                Add("Light", "Flags", $"0x{light.Row.Flags:X4}"); Add("Light", "Stages", string.Join(", ", light.Row.AuthoredStages));
                Add("Transform", "Position", light.Row.Translation); Add("Transform", "Rotation", light.Row.Rotation);
                Add("Transform", "Scale", light.Row.Scale); Add("Transform", "World", light.Row.Transform);
                Add("Transform", "Volume", light.Row.VolumeTransform); Add("Transform", "Volume world", light.Row.VolumeWorldTransform);
                Add("Light", "Source vector 60", light.Row.SourceVector60); Add("Light", "Source vector 70", light.Row.SourceVector70);
                Add("Light", "Probe rows", light.Owner.Table.ProbeRowCount); Add("Light", "Volumetric rows", light.Owner.Table.VolumetricRowCount);
                Add("Light", "Source bytes", light.Row.SourceData.Length);
                Ref("Light table", light.Owner.Table.TagHash, light.Row.SourceOffset);
                Ref("Light transform", light.Owner.Table.TagHash, light.Row.TransformOffset);
                Ref("Material", light.Row.MaterialTag); Ref("Volumetric material", light.Row.VolumetricMaterialTag);
                Ref("Bounds", light.Owner.Table.BoundsTag); Ref("Placement", light.Owner.PlacementSourceTag, light.Owner.PlacementSourceOffset);
                Ref("Probe parameters", light.Row.ProbeParameters?.TagHash ?? 0);
                Ref("Skip parameters", light.Row.SkipParameters?.TagHash ?? 0);
                foreach (var parameter in new[] { light.Row.ProbeParameters, light.Row.SkipParameters }.OfType<AuthoredLightParameters>())
                {
                    Add("Light parameters", $"{parameter.TagHash:X8} source bytes", parameter.SourceData.Length);
                    Add("Light parameters", $"{parameter.TagHash:X8} bytecode bytes", parameter.Bytecode.Length);
                    for (int i = 0; i < parameter.Constants.Count; i++) Add("Light constants", i.ToString(), parameter.Constants[i]);
                    for (int i = 0; i < parameter.Defaults.Count; i++) Add("Light defaults", i.ToString(), parameter.Defaults[i]);
                }
                if (light.Owner.OwnerMarker is { } ownerMarker) related.Add(ownerMarker);
                Containers(light.Owner.MapContainers);
                break;
            case AuthoredObjectInfo authored:
                Ref("Authored object", authored.TagHash); Add("Object", "Registry key", $"0x{authored.RegistryKey:X8}");
                slots.AddRange(authored.Slots);
                related.AddRange(world.Scene.Markers.Where(x => x.Slot?.RegistryKey == authored.RegistryKey));
                break;
            case AuthoredSquadInfo squad:
                Ref("Squad config", squad.ConfigTag); Add("Squad", "Slot", squad.Slot);
                Add("Squad", "Entry index", squad.EntryIndex); Add("Squad", "Members", squad.MemberCount);
                Add("Squad", "Anchors", squad.AnchorCount); Add("Squad", "Unresolved points", squad.UnresolvedPointCount);
                Add("Squad", "Ambiguous", squad.Ambiguous); Add("Squad", "Resolved rule", squad.HasResolvedRule);
                related.AddRange(world.Scene.Markers.Where(x => x.TagHash == squad.ConfigTag && x.Slot == squad.Slot
                    && world.CanonicalBubble(x.BubbleHash) == item.BubbleHash));
                break;
            case ScenarioStateInfo state:
                Add("State", "Ordinal", state.Ordinal); Add("State", "Name hash", $"0x{state.NameHash:X8}");
                Add("State", "Enabled by package", state.PackageEnabled); Add("State", "Public", state.IsPublic);
                Add("State", "Entry index", state.EntryIndex); Add("State", "Map bubble index", state.MapBubbleIndex);
                Add("State", "Loaded", world.Scene.ScenarioStates.Contains(state));
                Ref("Entry", state.EntryTag); Ref("Registry", state.RegistryTag);
                related.AddRange(world.Objects.Where(x => !ReferenceEquals(x.Source, state)
                    && x.Owners.Contains(new(item.MissionTag, item.BubbleHash, state.Ordinal))).Select(x => x.Source));
                break;
        }
        return new(item, values.AsReadOnly(), references.Distinct().ToArray(), slots.Distinct().ToArray(),
            related.Distinct(ReferenceEqualityComparer.Instance).ToArray());
    }

    private static string Format(object? value) => value switch
    {
        null => "", bool boolean => boolean ? "Yes" : "No",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    /** Uneditable placements without source rows retain a deterministic transform identity. */
    private static ulong TransformIdentity(Matrix4x4 transform)
    {
        // 64-bit FNV-1a over 32-bit float words, not bytes; keeps fallback IDs stable across loads.
        ulong hash = 14695981039346656037;
        ReadOnlySpan<float> values = [transform.M11, transform.M12, transform.M13, transform.M14,
            transform.M21, transform.M22, transform.M23, transform.M24,
            transform.M31, transform.M32, transform.M33, transform.M34,
            transform.M41, transform.M42, transform.M43, transform.M44];
        foreach (float value in values) hash = unchecked((hash ^ BitConverter.SingleToUInt32Bits(value)) * 1099511628211);
        return hash;
    }
}
