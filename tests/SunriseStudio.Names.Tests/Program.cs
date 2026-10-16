// Console test runner for names: string bank support, immediate metadata names and fast index behavior.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

string temporary = Path.Combine(Path.GetTempPath(), "sunrise-names-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(temporary, "packages"));
try
{
    if (args is ["--component-slots-owned", var slotGame]) { ComponentSlotChecks.Owned(slotGame); return 0; }
    PackageKeySourceChecks.Synthetic();
    if (args is ["--package-keys"]) return 0;
    NativeTableChecks.Run();
    if (args is ["--provider-checks"]) return 0;
    ComponentSlotChecks.Run(temporary);
    if (args is ["--component-slots"]) return 0;
    if (args is ["--component-refresh", var componentGame]) { ComponentRefreshChecks.Run(componentGame); return 0; }
    if (args is ["--component-baseline", var baselineGame]) { ComponentRefreshChecks.Baseline(baselineGame); return 0; }
    const uint first = 0x80A00000;
    byte[] scenario = new byte[88]; U(scenario, 8, 0x1234); U(scenario, 0x28, first + 6);
    byte[] model = new byte[176]; U(model, 8, first + 3);
    Array(model, 0x10, 128, 1, 0x80800014); U(model, 144, first + 2);
    byte[] material = new byte[1088]; Q(material, 0, material.Length);
    Array(material, 0x2D0, 1040, 1, 0x80807211); U(material, 1056, 0); U(material, 1060, first + 4);
    byte[] texture = new byte[40]; U(texture, 0, 4); U(texture, 4, 28); S(texture, 12, 0xCAFE);
    foreach (int p in new[] { 14, 16, 18, 20 }) S(texture, p, 1);
    texture[23] = 1; U(texture, 36, uint.MaxValue);
    var one = Strings(first + 7, "Friendly activity");
    var two = Strings(first + 9, "Other bank text");
    byte[] entity = new byte[176]; Array(entity, 0x10, 128, 1, 0x80809C04); U(entity, 144, first + 11);
    byte[] config = new byte[0x260]; Q(config, 0x18, 0x60 - 0x18); U(config, 0x5C, 0x808072BD);
    U(config, 0x60, first + 11); U(config, 0x60 + 0x1DC, first + 12);
    PackageWriter.WriteNew(Path.Combine(temporary, "packages", "w64_named_fixture_0100_0.pkg"), 0x100,
    [
        new(0x80809994, 8 << 9, scenario),
        new(0x808071A7, 8 << 9, model, "fixture_static_model"),
        new(0x808071E8, 8 << 9, material), new(0x80807194, 8 << 9, new byte[64]),
        new(first + 5, (32 << 9) | (1 << 6), texture),
        new(uint.MaxValue, (40 << 9) | (1 << 6), [1, 2, 3, 4]),
        new(0x80809A88, 8 << 9, one.Container), new(0x80809A8A, 8 << 9, one.Data),
        new(0x80809A88, 8 << 9, two.Container), new(0x80809A8A, 8 << 9, two.Data),
        new(0x80809C0F, 8 << 9, entity, "cache_owner"), new(0x80809C36, 8 << 9, config),
        new(0x808073A5, 8 << 9, new byte[32]), new(0x80809A88, 8 << 9, new byte[24])
    ]);
    WriteSdkFixture(temporary, first, first + 2);
    using var catalog = PackageCatalog.Open(temporary, cacheOptions: new() { Enabled = false });
    var empty = LocalizedStrings.LoadBanks(catalog, [first + 13]);
    Check(empty.ContainerCount == 1 && empty.FailedContainerCount == 0, "Empty string bank lost its prior read-only support.");
    var timer = Stopwatch.StartNew();
    var names = StudioNameIndex.Create(catalog);
    Check(names.Describe(first + 1) == "Fixture static model", "Metadata names are not immediate.");
    Check(!names.Coverage.Complete && names.GameNames is null, "Fast index decoded payloads.");
    Check(names.Lookup(first + 4).Confidence == StudioNameConfidence.Fallback, "Fallback was marked authored.");
    long metadataRevision = names.Revision;
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        Reject<OperationCanceledException>(() => names.Load(cancelled.Token));
        Check(!names.IsLoading && names.Progress == "Cancelled", "Cancellation did not leave a usable index.");
        Check(names.Describe(first + 1) == "Fixture static model", "Cancellation removed published names.");
    }
    names.Load(progress: _ =>
    {
        var progress = names.LoadingProgress;
        Check(progress.Completed >= 0 && progress.Total >= progress.Completed
            && progress.Elapsed >= TimeSpan.Zero && progress.PhaseElapsed >= TimeSpan.Zero,
            "Name progress reported invalid work counts or timing.");
    });
    Check(names.Coverage.Complete && names.Revision > metadataRevision, "Enrichment was not published.");
    Check(names.Describe(first) == "Friendly activity", "Scoped activity localization failed.");
    Check(names.ResolveHash(0x1234) is null, "Conflicting localization banks silently chose one text.");
    Check(names.Lookup(first + 2).IsAmbiguous, "SDK aliases were collapsed to one name.");
    Check(names.Coverage.SdkFiles == 2, "Version 14 and 15 SDK name shards must both remain readable.");
    Check(names.ResolveHash(0x109CBBE3) is null && names.HashCandidates(0x109CBBE3).Count == 2,
        "Conflicting FNV preimages silently chose one name.");
    var textureName = names.Lookup(first + 4);
    Check(textureName.Source == StudioNameSource.Owner && textureName.Confidence == StudioNameConfidence.Contextual,
        "Texture ownership was not distinguished from an authored name.");
    Check(textureName.DisplayName.Contains("Pixel texture", StringComparison.Ordinal), "Texture owner role is absent.");
    Check(names.Lookup(first + 5).Source == StudioNameSource.Owner, "Paired texture payload lost its owner.");
    Check(names.GameNames!.Components!.TryGetModelTag(first + 11, out uint cachedModel) && cachedModel == first + 12,
        "Component indexing did not retain its typed model link.");
    Check(names.Lookup(first + 12).DisplayName.Contains("Cache owner", StringComparison.Ordinal),
        "Reused model facts changed the owner label.");
    Check(names.PhaseTimings.Single(x => x.Stage == "Asset owners").EntryReads == 5,
        "Owner indexing reread a config already inspected by the component index.");
    using (var partialCancellation = new CancellationTokenSource())
    {
        var partial = StudioNameIndex.Create(catalog);
        Reject<OperationCanceledException>(() => partial.Load(partialCancellation.Token,
            stage => { if (stage == "Renderer names") partialCancellation.Cancel(); }));
        Check(partial.GameNames is not null && partial.Describe(first) == "Friendly activity",
            "Mid-load cancellation discarded published localization.");
    }
    var scene = new SceneDocument { TagHash = first };
    scene.Markers.Add(new SceneMarker
    {
        Name = "Exact authored name",
        NameEvidence = SceneNameEvidence.PackageInline,
        TagHash = first + 1,
        NameHash = 0x109CBBE3
    });
    scene.Markers.Add(new SceneMarker
    {
        Name = "Spawn point 2 -- Unknown place",
        NameEvidence = SceneNameEvidence.Derived,
        Slot = new(7, 3, 2),
        Category = "Spawn point"
    });
    scene.Instances.Add(new MeshInstance { Name = "Static mesh", Mesh = new MeshData { TagHash = first + 1, MaterialTag = first + 2 } });
    scene.Instances.Add(new MeshInstance { Name = "Static mesh", Mesh = new MeshData { TagHash = first + 3 } });
    names.NameScene(scene);
    long revision = names.Revision;
    string markerName = scene.Markers[1].Name;
    names.NameScene(scene);
    Check(scene.Markers[0].Name == "Exact authored name", "Scene naming replaced an authored name.");
    Check(names.Revision == revision, "Repeated scene naming publishes endless revisions.");
    Check(scene.Markers[1].Name == markerName, "Repeated scene naming grows a derived marker label.");
    Check(!scene.Instances[0].Name.Contains("0x", StringComparison.Ordinal), "Scene label left hashes in its primary name.");
    PackageCacheChecks.Synthetic(catalog, first + 5);
    PersistentNameCacheChecks.Synthetic(temporary);
    ItemCategoryChecks.Synthetic();
    Console.WriteLine($"pass synthetic=names scoped_strings=verified ambiguous_names=rejected contextual_owners=verified elapsed_ms={timer.ElapsedMilliseconds}");
    if (args.Length == 2 && args[0] == "--game-root") Installed(args[1]);
    if (args.Length == 2 && args[0] == "--cache-benchmark") PersistentNameCacheChecks.Installed(args[1]);
}
catch (Exception error)
{
    Console.Error.WriteLine($"names result=fail error={System.Text.Json.JsonSerializer.Serialize(error.ToString())}");
    return 1;
}
finally
{
    if (Path.GetFullPath(temporary).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(temporary, true);
}
return 0;

static void Installed(string gameRoot)
{
    using var catalog = PackageCatalog.Open(gameRoot);
    var timer = Stopwatch.StartNew();
    var names = StudioNameIndex.Create(catalog);
    Console.WriteLine($"names stage=metadata elapsed_ms={timer.ElapsedMilliseconds} named={names.Coverage.NamedTags}");
    long last = -1;
    names.Load(progress: stage =>
    {
        if (timer.ElapsedMilliseconds - last < 2000) return;
        last = timer.ElapsedMilliseconds;
        Console.WriteLine($"names stage={stage.Replace(' ', '_')} elapsed_ms={last}");
    });
    Check(names.Coverage.Complete && names.GameNames!.MapCount > 0 && names.RenderNames!.Entries.Count > 0,
        "Installed map and renderer names are missing.");
    Check(names.Describe(0x80B4A0F4).Contains("Tower", StringComparison.OrdinalIgnoreCase), "Tower activity label missing.");
    Check(names.Coverage.SdkFiles > 0 && names.Coverage.ContextualTags > 0, "SDK and owner names are missing.");
    Check(Enumerable.Range(0, 15424).Count(i => names.ItemName((uint)i) is not null) > 10000,
        "Installed item localization coverage is too small.");
    var entities = catalog.Entries.Where(x => x.Reference == 0x80809C0F).Take(32).Select(names.Lookup).ToArray();
    Check(entities.All(x => x.DisplayName.Length > 0), "An installed entity has no readable label.");
    var ambiguous = catalog.Entries.Select(names.Lookup).FirstOrDefault(x => x.IsAmbiguous);
    Check(ambiguous is not null, "Installed ambiguous naming fixture was not retained.");
    Console.WriteLine($"pass installed_tags={names.Coverage.NamedTags} hashes={names.Coverage.NamedHashes} ambiguous={names.Coverage.AmbiguousTags} contextual={names.Coverage.ContextualTags} sdk_files={names.Coverage.SdkFiles} read_errors={names.Coverage.ReadErrors} elapsed_ms={timer.ElapsedMilliseconds}");
    foreach (var phase in names.PhaseTimings)
        Console.WriteLine($"name_phase stage={phase.Stage.Replace(' ', '_')} elapsed_ms={phase.Elapsed.TotalMilliseconds:F0} entries={phase.EntryReads} blocks={phase.BlocksRead} hits={phase.CacheHits} opens={phase.FileOpens} io_ms={phase.ReadTime.TotalMilliseconds:F0} decode_ms={phase.DecodeTime.TotalMilliseconds:F0}");
    Console.WriteLine($"names tower={names.Describe(0x80B4A0F4)} ambiguity_tag={ambiguous!.TagHash:X8} ambiguity_source={ambiguous.Source}");
    foreach (string diagnostic in names.Diagnostics.Take(8)) Console.WriteLine($"names diagnostic={diagnostic}");
    ItemCategoryChecks.Owned(catalog, names);
    PackageCacheChecks.Owned(catalog);
}

static (byte[] Container, byte[] Data) Strings(uint dataTag, string text)
{
    var container = new byte[80]; U(container, 0x18, dataTag); Array(container, 8, 48, 1, 0x80800070); U(container, 64, 0x1234);
    var data = new byte[288]; Array(data, 8, 112, 1, 0x80809A90); Array(data, 0x48, 176, 1, 0x80809A8E);
    Q(data, 136, 224 - 136); U(data, 144, 0x811C9DC5); S(data, 148, (ushort)Encoding.UTF8.GetByteCount(text));
    Q(data, 192, 128 - 192); Q(data, 200, 1); Encoding.UTF8.GetBytes(text).CopyTo(data, 224);
    return (container, data);
}

static void WriteSdkFixture(string root, uint scenarioTag, uint targetTag)
{
    string path = Path.Combine(root, "bin", "x64", "Sunrise", "sdk", "scenarios");
    Directory.CreateDirectory(path);
    var header = new byte[664]; "SRGWSHRD"u8.CopyTo(header); U(header, 8, 14); U(header, 12, 664);
    U(header, 24, scenarioTag); U(header, 28, 35); U(header, 32, 507);
    var candidates = new byte[280];
    foreach (var (name, p) in new[] { ("fixture_psvmaagzpe", 0), ("fixture_eiecwoeuxj", 140) })
    {
        Encoding.UTF8.GetBytes(name).CopyTo(candidates, p); U(candidates, p + 128, scenarioTag);
        U(candidates, p + 132, 0x80809994); S(candidates, p + 136, (ushort)name.Length); candidates[p + 138] = 1;
        Check(StringBankWriter.Hash(name) == 0x109CBBE3, "Fixture hash collision changed.");
    }
    var hashes = new byte[20]; U(hashes, 0, 0x109CBBE3); U(hashes, 8, 2); U(hashes, 12, uint.MaxValue); hashes[16] = 1;
    var tags = new byte[24]; U(tags, 0, targetTag); U(tags, 4, 0x808071E8); U(tags, 12, 2); U(tags, 16, uint.MaxValue); tags[20] = 1;
    using var payload = new MemoryStream(); payload.Write(new byte[507]);
    foreach (var (index, stride, bytes) in new[] { (25, 20, hashes), (26, 24, tags), (27, 140, candidates), (28, 12, System.Array.Empty<byte>()), (29, 1, System.Array.Empty<byte>()) })
    {
        int p = 104 + index * 16; Q(header, p, header.Length + payload.Position);
        U(header, p + 8, (uint)(bytes.Length / stride)); U(header, p + 12, (uint)stride); payload.Write(bytes);
    }
    Q(header, 16, header.Length + payload.Length);
    foreach (uint version in new uint[] { 14, 15 })
    {
        U(header, 8, version);
        File.WriteAllBytes(Path.Combine(path, $"{scenarioTag:X8}-fixture-v{version}.pack"), [.. header, .. payload.ToArray()]);
    }
}

static void Array(byte[] bytes, int field, int header, int count, uint type)
{ Q(bytes, field, count); Q(bytes, field + 8, header - field - 8); U(bytes, header - 4, 0x80809FBD); Q(bytes, header, count); U(bytes, header + 8, type); }
static void U(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
static void S(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
static void Q(byte[] bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value);
static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
static void Reject<T>(Action action) where T : Exception
{ try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
