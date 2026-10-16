// Console test runner for material interchange: native slots, constants and texture files survive export and import checking.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

if (args is not ([] or ["--export", _] or ["--import-check", _]))
{
    Console.Error.WriteLine("usage: [--export <folder> | --import-check <manifest>]");
    return 2;
}
string root = Path.Combine(Path.GetTempPath(), "sunrise-material-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "install", "packages"));
try
{
    const uint materialTag = 0x80A00000, firstTexture = materialTag + 1, payload = materialTag + 2;
    const uint secondTexture = materialTag + 3;
    const uint replacementTexture = materialTag + 4, replacementPayload = materialTag + 5;
    byte[] material = new byte[0x520];
    BinaryPrimitives.WriteUInt64LittleEndian(material, (ulong)material.Length);
    material[0x100] = 0x5A;
    ArrayAt(material, 0x50, 0x410, 1, 0x80807211);
    U(material, 0x420, 3); U(material, 0x424, firstTexture);
    ArrayAt(material, 0x2D0, 0x440, 1, 0x80807211);
    U(material, 0x450, 7); U(material, 0x454, secondTexture);
    ArrayAt(material, 0x2F8, 0x480, 1, 0x80800090);
    ArrayAt(material, 0x318, 0x4C0, 1, 0x80800090);
    for (int i = 0; i < 4; i++)
    {
        BinaryPrimitives.WriteSingleLittleEndian(material.AsSpan(0x490 + i * 4), i + .25f);
        BinaryPrimitives.WriteSingleLittleEndian(material.AsSpan(0x4D0 + i * 4), i - .75f);
    }
    byte[] pixels = [255, 0, 0, 255, 0, 255, 0, 255];
    byte[] header = TextureHeader(pixels.Length);
    string package = Path.Combine(root, "install", "packages", "w64_synthetic_0100_0.pkg");
    PackageWriter.WriteNew(package, 0x100,
    [
        new(0x808071E8, 8u << 9, material, "synthetic material"),
        new(payload, (32u << 9) | (1u << 6), header, "first texture"),
        new(0, 40u << 9, pixels),
        new(payload, (32u << 9) | (1u << 6), header, "second texture"),
        new(replacementPayload, (32u << 9) | (1u << 6), header, "replacement texture"),
        new(0, 40u << 9, [0, 0, 255, 255, 255, 255, 255, 255])
    ]);
    string sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package)));
    using var catalog = PackageCatalog.Open(Path.Combine(root, "install"));
    string bundle = Path.Combine(root, "bundle");
    var result = MaterialInterchange.Export(catalog, materialTag, bundle);
    var document = JsonSerializer.Deserialize<MaterialDocument>(File.ReadAllBytes(result.ManifestPath))!;
    Require(document.Slots.Count == 2 && document.Constants.Count == 8 && result.TextureCount == 2,
        "Native slots, constants, or texture files were lost.");
    Require(document.Slots[0].Stage == "Vertex" && document.Slots[0].Slot == 3
        && document.Slots[1].Stage == "Pixel" && document.Slots[1].Slot == 7, "Native stage identity changed.");
    Require(MaterialInterchange.ReadMaterialTag(result.ManifestPath) == materialTag
        && MaterialInterchange.IsMaterialDocument(result.ManifestPath), "Material discriminator failed.");
    Require(MaterialInterchange.Import(catalog, result.ManifestPath).Count == 0, "Unchanged bundle produced edits.");
    Require(document.Textures.All(x =>
        File.Exists(Path.Combine(bundle, x.DdsPath!)) && File.Exists(Path.Combine(bundle, x.PreviewPath!))),
        "Material working files were not exported.");
    Require(Directory.GetFiles(bundle, "*.py", SearchOption.AllDirectories).Length == 0,
        "Material export wrote a Blender script; the add-on ships separately.");

    var edited = document with
    {
        Slots = [document.Slots[0] with { TextureTag = replacementTexture }, document.Slots[1]],
        Constants = document.Constants.Select((x, i) => i == 0 ? x with { Value = "2.5" } : x).ToArray()
    };
    Save(result.ManifestPath, edited);
    var changes = MaterialInterchange.Import(catalog, result.ManifestPath);
    Require(changes.Count == 1 && changes.ContainsKey(materialTag), "Scalar edits changed unrelated assets.");
    byte[] expected = (byte[])material.Clone();
    U(expected, 0x424, replacementTexture);
    BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(0x490), 2.5f);
    Require(changes[materialTag].SequenceEqual(expected), "Material import changed bytes outside selected fields.");
    catalog.ReadOverride = tag => tag == materialTag ? expected : null;
    Require(MaterialInterchange.Import(catalog, result.ManifestPath).Count == 0,
        "Reimporting applied material values was not idempotent.");
    var repeated = edited with
    {
        Slots = [edited.Slots[0] with { TextureTag = firstTexture }, edited.Slots[1]],
        Constants = edited.Constants.Select((x, i) => i == 0 ? x with { Value = "3.5" } : x).ToArray()
    };
    Save(result.ManifestPath, repeated);
    var secondChanges = MaterialInterchange.Import(catalog, result.ManifestPath);
    Require(BinaryPrimitives.ReadUInt32LittleEndian(secondChanges[materialTag].AsSpan(0x424)) == firstTexture
        && BinaryPrimitives.ReadSingleLittleEndian(secondChanges[materialTag].AsSpan(0x490)) == 3.5f,
        "Repeated scalar and slot edits failed after the first material import.");
    catalog.ReadOverride = null;
    Save(result.ManifestPath, edited);

    string firstDds = Path.Combine(bundle, document.Textures[0].DdsPath!);
    var texture = DdsInterchange.Read(firstDds);
    byte[] changedPixels = (byte[])texture.Subresources[0].Data.Clone(); changedPixels[0] = 128;
    DdsInterchange.Write(firstDds, texture with { Subresources = [texture.Subresources[0] with { Data = changedPixels }] });
    changes = MaterialInterchange.Import(catalog, result.ManifestPath);
    Require(changes.Count == 2 && changes[payload].SequenceEqual(changedPixels),
        "DDS edits did not return the native texture payload.");
    Require(!changes.ContainsKey(replacementPayload), "Slot reassignment redirected old texture pixel edits.");
    catalog.ReadOverride = tag => tag == payload ? changedPixels : null;
    var repeatedTexture = MaterialInterchange.Import(catalog, result.ManifestPath);
    Require(!repeatedTexture.ContainsKey(payload), "Applied texture edits were not idempotent.");
    DdsInterchange.Write(firstDds, texture);
    Save(result.ManifestPath, edited with { Textures = [edited.Textures[0] with { EditedPath = edited.Textures[0].DdsPath }, edited.Textures[1]] });
    var revertedTexture = MaterialInterchange.Import(catalog, result.ManifestPath);
    Require(revertedTexture[payload].SequenceEqual(pixels), "Restoring the exported DDS did not revert imported pixels.");
    catalog.ReadOverride = null;
    Save(result.ManifestPath, edited);
    DdsInterchange.Write(firstDds, texture with { Subresources = [texture.Subresources[0] with { Data = changedPixels }] });
    string secondDds = Path.Combine(bundle, document.Textures[1].DdsPath!);
    byte[] conflictingPixels = (byte[])pixels.Clone(); conflictingPixels[0] = 64;
    DdsInterchange.Write(secondDds, texture with { Subresources = [texture.Subresources[0] with { Data = conflictingPixels }] });
    Reject(() => MaterialInterchange.Import(catalog, result.ManifestPath));
    DdsInterchange.Write(secondDds, texture);
    DdsInterchange.Write(firstDds, texture);

    var image = PngInterchange.Read(Path.Combine(bundle, document.Textures[0].PreviewPath!));
    image.Pixels[1] = 150;
    PngInterchange.Write(Path.Combine(bundle, document.Textures[0].PreviewPath!), image);
    edited = document with { Textures = [document.Textures[0] with { EditedPath = document.Textures[0].PreviewPath }, document.Textures[1]] };
    Save(result.ManifestPath, edited);
    changes = MaterialInterchange.Import(catalog, result.ManifestPath);
    Require(changes.Count == 1 && changes[payload][1] == 150, "Explicit PNG edits did not reach native pixels.");
    Require(MaterialInterchange.Import(catalog, result.ManifestPath, false).Count == 0,
        "Disabling texture import still changed texture payloads.");

    Save(result.ManifestPath, document with { Constants = [document.Constants[0] with { Offset = 0x100 }, .. document.Constants.Skip(1)] });
    Reject(() => MaterialInterchange.Import(catalog, result.ManifestPath));
    Save(result.ManifestPath, document with { Slots = [document.Slots[0] with { TextureTag = materialTag }, document.Slots[1]] });
    Reject(() => MaterialInterchange.Import(catalog, result.ManifestPath));
    Save(result.ManifestPath, document with { Textures = [document.Textures[0] with { EditedPath = "../outside.png" }, document.Textures[1]] });
    Reject(() => MaterialInterchange.Import(catalog, result.ManifestPath));
    Save(result.ManifestPath, document);
    byte[] changedSchema = (byte[])material.Clone(); changedSchema[0x100] ^= 1;
    catalog.ReadOverride = tag => tag == materialTag ? changedSchema : null;
    Reject(() => MaterialInterchange.Import(catalog, result.ManifestPath));
    catalog.ReadOverride = null;
    Reject(() => MaterialInterchange.Export(catalog, materialTag, Path.Combine(root, "install", "bad-export")));

    const uint addedTag = 0x82800000;
    catalog.SetOverlayEntries([new(new(addedTag, 0x808071E8, 8, 0, material.Length,
        "project material", 0x1000, 0) { TypeInfo = 8u << 9 }, "Added material")]);
    catalog.ReadOverride = tag => tag == addedTag ? material : null;
    var addedBundle = MaterialInterchange.Export(catalog, addedTag, Path.Combine(root, "added-bundle"));
    Require(MaterialInterchange.Import(catalog, addedBundle.ManifestPath).Count == 0,
        "Added material overlay required an installed baseline.");
    Require(sourceHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))), "Material export changed source packages.");
    if (args is ["--import-check", var editedManifest])
    {
        var blenderChanges = MaterialInterchange.Import(catalog, editedManifest);
        byte[] blenderExpected = (byte[])expected.Clone(); U(blenderExpected, 0x424, secondTexture);
        Require(blenderChanges.Count == 1 && blenderChanges[materialTag].SequenceEqual(blenderExpected),
            "Blender edits did not roundtrip to the expected native material bytes.");
        Console.WriteLine("blender_material_roundtrip constants=pass assignments=pass bytes=exact");
    }
    else if (args is ["--export", var exportFolder])
    {
        var blender = MaterialInterchange.Export(catalog, materialTag, exportFolder);
        Console.WriteLine($"material_bundle manifest={blender.ManifestPath}");
    }
    Console.WriteLine("material_test fields=pass textures=pass shared_payload=pass paths=pass added_overlay=pass originals=unchanged");
    return 0;
}
finally
{
    string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
    string verified = Path.GetFullPath(root);
    if (verified.StartsWith(temporaryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(verified).StartsWith("sunrise-material-tests-", StringComparison.Ordinal))
        Directory.Delete(verified, true);
}

static void Save(string path, MaterialDocument document) => File.WriteAllText(path, JsonSerializer.Serialize(document));
static void U(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

static byte[] TextureHeader(int size)
{
    byte[] header = new byte[40];
    U(header, 0, (uint)size); U(header, 4, 28); U(header, 0x24, uint.MaxValue);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x0C), 0xCAFE);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x0E), 2);
    foreach (int offset in new[] { 0x10, 0x12, 0x14 })
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(offset), 1);
    header[0x17] = 1;
    return header;
}

static void ArrayAt(byte[] bytes, int field, int header, int count, uint type)
{
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(field), count);
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(field + 8), header - field - 8);
    U(bytes, header - 4, 0x80809FBD);
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(header), count);
    U(bytes, header + 8, type);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Reject(Action action)
{
    try { action(); }
    catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException) { return; }
    throw new InvalidOperationException("Invalid material operation was accepted.");
}
