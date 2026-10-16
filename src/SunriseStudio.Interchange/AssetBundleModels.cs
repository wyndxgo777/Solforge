using System.Numerics;
using SunriseStudio.Animation;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

namespace SunriseStudio.Interchange;

/** Data model of the asset bundle manifest and its edits. Whether an import edits existing assets or adds a new one. */
public enum AssetBundleImportMode { ApplyDeltas, NewAsset }
/** Options for bundle export: open context, project, animation sampling, asset limit and name lookup. */
public sealed record AssetBundleExportOptions(AssetOpenContext? Context = null, StudioProject? Project = null,
    bool IncludeAnimations = true, int SampleRate = 30, int MaximumAssets = 8192,
    Func<uint, string?>? NameResolver = null)
{
    /** Spawn, variant, parameter and channel inputs the root entity is read with; null reads the native default. */
    public SceneLoadOptions? Scene { get; init; }
}
/** Options for bundle import: mode, package ID for new assets and the new asset's name. */
public sealed record AssetBundleImportOptions(AssetBundleImportMode Mode, ushort PackageId = 0x0C00,
    string? Name = null);
/** A message about one surface of the bundle (status and optional tag). */
public sealed record BundleNotice(string Surface, string Status, string Message, uint? TagHash = null);
/** A reference from a native asset to a target tag; Verified is true when a native reader confirmed it. */
public sealed record BundleReference(uint TargetTag, int Offset, bool Verified, string Role);
/** A native package asset in the bundle with its file, hash and references. */
public sealed record BundleNativeAsset(uint TagHash, string Name, uint Reference, uint TypeInfo,
    int Type, int SubType, string Path, string Sha256, uint? BaseTagHash, string? BaseSha256,
    string? AddedAssetId, IReadOnlyList<BundleReference> References);
/** One mesh part in the bundle: model and material links, LOD, file and placement transform. */
public sealed record BundleMesh(string Id, uint ModelTag, int PartIndex, uint ConfigTag, uint MaterialTag,
    int Lod, string Path, string Sha256, float[] Transform, bool Skinned, string? SkinSha256)
{
    public int RenderStage { get; init; } = -1;
    public string Skinning { get; init; } = "Linear";
    public bool? InitiallyVisible { get; init; }
    public string? InitialVisibilityReason { get; init; }
    /** The BundleMaterial variant that holds this part's shader inputs; null uses the plain material. */
    public string? MaterialVariant { get; init; }
}
/** Mesh vertex data: positions, normals, texture coordinates, indices and optional skin weights and tangents; Basis names the tangent source. */
public sealed record BundleGeometry(string Id, float[][] Positions, float[][] Normals, float[][] TexCoords,
    uint[] Indices, int[][]? Joints = null, float[][]? Weights = null,
    float[][]? Tangents = null, string Basis = "Diagnostic")
{
    public BundleVertexMotion? VertexMotion { get; init; }
    /** Detail UVs: TexCoords times the TEXCOORD2 stream, as the vertex shader writes them. */
    public float[][]? TexCoords1 { get; init; }
    /** COLOR0 per vertex, 0 to 1; preview only. */
    public float[][]? Colors { get; init; }
    /** Lanes z and w of the UV stream, untransformed; preview only. */
    public float[][]? TexCoordsZw { get; init; }
    /** POSITION0 x, y and z before the model's offset and scale; preview only. */
    public float[][]? RawPositions { get; init; }
}
/** Vertex motion data: shader profile and its colour, constant and rate streams. */
public sealed record BundleVertexMotion(string Profile, float[][] Colors, float[][] Constants, float[][] Rates)
{
    /** The "VertexProgram" profile's offset expression; its streams are the geometry's own. */
    public BundleMaterialProgram? Program { get; init; }
    /** Vertex stage textures the program samples, by slot. */
    public IReadOnlyDictionary<uint, BundleMotionTexture>? Textures { get; init; }
}
/** A texture used by vertex motion. */
public sealed record BundleMotionTexture(uint TextureTag, bool Srgb, bool Clamp);
/** A material in the bundle with its path and optional preview. */
public sealed record BundleMaterial(uint TagHash, string Path, string? ShaderProfile,
    BundleMaterialPreview? Preview = null)
{
    /** Set on a preview-only copy of the material with one part's shader inputs; meshes name it. */
    public string? Variant { get; init; }
    public string? Name { get; init; }
}
/** One texture input of a material preview with its role, slot, channels and decode settings. */
public sealed record BundleMaterialBinding(string Role, uint Slot, uint TextureTag, string Channels,
    float Scale = 1, float Bias = 0, bool Srgb = false, bool ReconstructNormalZ = false)
{
    public float UnpackScale { get; init; } = 2;
    public float UnpackBias { get; init; } = -1;
}
/** Approximate PBR preview of a material: bindings plus base colour, metallic, roughness, alpha and emission. */
public sealed record BundleMaterialPreview(string Mode, uint PixelShaderTag, string ShaderSha256,
    IReadOnlyList<BundleMaterialBinding> Bindings, float[] BaseColor, float Metallic, float Roughness,
    float Alpha, float[] Emission, IReadOnlyList<string> Notes)
{
    public string BlendMode { get; init; } = "Opaque";
    public float[][]? PixelConstants { get; init; }
    public uint[]? PixelConstantMasks { get; init; }
    public IReadOnlyDictionary<uint, int>? TextureFormats { get; init; }
    public IReadOnlyDictionary<uint, BundleMaterialSampler>? Samplers { get; init; }
    public BundleMaterialPalette? Palette { get; init; }
    public string? Recipe { get; init; }
    public BundleMaterialProgram? Program { get; init; }
    /** The shader writes the G-buffer; blended, it changes the albedo before lighting, with premultiplied colour. */
    public bool Deferred { get; init; }
    /** Blend each render stage in the bundle draws this material with. */
    public IReadOnlyDictionary<int, string>? StageBlendModes { get; init; }
    /** Previews for the visible stage blends other than BlendMode, keyed by blend mode. */
    public IReadOnlyDictionary<string, BundleMaterialPreview>? BlendVariants { get; init; }
    /** Textures by pixel slot that the draw binds over the material document; preview only. */
    public IReadOnlyDictionary<uint, uint>? SlotTextures { get; init; }
    /** The pixel stage reads frame textures, such as scene depth; a decal without them draws on its own mesh. */
    public bool ReadsFrameTextures { get; init; }
    /** 3D textures "volume" program nodes sample, by pixel slot. */
    public IReadOnlyDictionary<uint, BundleVolumeTexture>? VolumeTextures { get; init; }
}
/** A 3D texture as its mip 0 depth slices in rows of Columns tiles; Path is null until the export writes it. */
public sealed record BundleVolumeTexture(uint TextureTag, int Width, int Height, int Depth, int Columns, int Format)
{
    public string? Path { get; init; }
    public string? Sha256 { get; init; }
    /** The mean texel, read when the slices are not written. */
    public float[]? Mean { get; init; }
}
/** Sampler state of a material texture. */
public sealed record BundleMaterialSampler(string Filter, string AddressU, string AddressV, string AddressW,
    float MipLodBias, float MinLod, float MaxLod, uint MaxAnisotropy);
/** One operation of a compiled material shader expression and its input nodes. */
public sealed record BundleShaderNode(string Operation, int[] Inputs, float Value = 0, uint Slot = 0, int Channel = 0)
{
    public uint? SamplerSlot { get; init; }
}
/** A compiled shader expression graph with named outputs. */
public sealed record BundleMaterialProgram(IReadOnlyList<BundleShaderNode> Nodes, IReadOnlyDictionary<string, int[]> Outputs)
{
    /** Visibility skips a shader branch it cannot resolve, such as a hashed dither threshold. */
    public bool ApproximateVisibility { get; init; }
}
/** Palette material data: mask and detail slots, detail UV, colours and blend. */
public sealed record BundleMaterialPalette(string Profile, uint MaskSlot, uint DetailSlot,
    float[] DetailUv, float[][] Colors, float[] Blend);
/** A texture in the bundle: DDS file and the per-subresource images. */
public sealed record BundleTexture(uint TagHash, string DdsPath, string DdsSha256,
    IReadOnlyList<BundleImage> Images, string? Error = null);
/** One mip/slice image of a texture, as a file. */
public sealed record BundleImage(int Mip, int Slice, int DepthSlice, string Path, string Sha256);
/** A shader in the bundle: tags, stage, profile, file and disassembly. */
public sealed record BundleShader(uint HeaderTag, uint BytecodeTag, string Stage, string Profile,
    string Path, string DisassemblyPath, string Sha256);
/** An animation clip in the bundle: pose and controls files. */
public sealed record BundleAnimation(uint ClipTag, string Name, string? PosePath, string? ControlsPath,
    string? ControlsSha256, string? Error = null);
/** The bundle manifest: everything exported for a root asset. */
public sealed record AssetBundleManifest(string Format, int Version, uint RootTag, uint SelectedTag,
    string Name, AssetOpenContext? Context, string? ScenePath, IReadOnlyList<BundleMesh> Meshes,
    IReadOnlyList<BundleMaterial> Materials, IReadOnlyList<BundleTexture> Textures,
    IReadOnlyList<BundleShader> Shaders, IReadOnlyList<BundleAnimation> Animations,
    IReadOnlyList<BundleNativeAsset> NativeAssets, IReadOnlyList<BundleNotice> Notices,
    string? ParticleMetadataPath = null)
{
    /** The collision section, a BundleCollision document, and its hash; null when the asset has none. */
    public string? CollisionPath { get; init; }
    public string? CollisionSha256 { get; init; }
}
/** Result of an export: where the manifest is and its contents. */
public sealed record AssetBundleExportResult(string ManifestPath, AssetBundleManifest Manifest);
/** One assembled scene on one rig. AttachedBones maps a skin to the bone its mesh follows rigidly. */
public sealed record AssetBundleAssembly(string Name, uint RootTag, SceneDocument Scene, SkeletonRig Rig,
    IReadOnlyList<ModelSkinBinding> Skins, IReadOnlyDictionary<ModelSkinBinding, int> AttachedBones,
    IReadOnlyList<uint> ClipTags, AssetOpenContext? Context)
{
    /** Its rest layer, such as a guardian's neutral face, is baked into the skinned geometry; actions are relative to it. */
    public AnimationRigBinding? RestBinding { get; init; }
    /** Action names by clip tag; a clip it lacks takes its package name or its slot number. */
    public IReadOnlyDictionary<uint, string>? ClipNames { get; init; }
    /** Draw-time shader inputs by part; a part with none previews its material as authored. */
    public IReadOnlyDictionary<MeshInstance, BundleShaderInputs>? ShaderInputs { get; init; }
}

/**
 * The shader inputs one part is drawn with: its gear-dye bit, dye-scope constant buffers by slot,
 * and pixel textures by slot. A texture tag found in ComposedTextures is written from that data.
 * Parts with the same Key have the same inputs.
 */
public sealed record BundleShaderInputs(string Key, string Label, int DyeBit,
    IReadOnlyDictionary<uint, Vector4[]> ConstantBuffers, IReadOnlyDictionary<uint, uint> Textures,
    IReadOnlyDictionary<uint, TextureData> ComposedTextures)
{
    /** Blend byte a decal dye forces over the pass and the material; null forces nothing. */
    public byte? ForcedBlend { get; init; }
    /** Object channel table the part's material reads; null reads (1,1,1,1). */
    public IReadOnlyList<Vector4>? ObjectChannels { get; init; }
}

/** An edited mesh: replacement positions and indices plus optional skin and attribute data. */
public sealed record BundleMeshEdit(string Id, float[][] Positions, uint[] Indices,
    int[][]? Joints = null, float[][]? Weights = null, float[][]? Normals = null, float[][]? TexCoords = null);
/** An edited texture image file for one mip/slice. */
public sealed record BundleTextureEdit(uint TagHash, string Path, int Mip = 0, int Slice = 0, int DepthSlice = 0);
/** An edited animation file for a clip. */
public sealed record BundleAnimationEdit(uint ClipTag, string Path);
/** An edited material file. */
public sealed record BundleMaterialEdit(uint TagHash, string Path);
/** One emitter constant: channel, name, offset in the tag and value. */
public sealed record BundleEmitterConstant(int Channel, string Name, int Offset, float[] Value);
/** An emitter and its constants. */
public sealed record BundleEmitter(uint TagHash, IReadOnlyList<BundleEmitterConstant> Constants);
/** Particle data: emitters and effect descriptions. */
public sealed record BundleParticles(IReadOnlyList<BundleEmitter> Emitters, IReadOnlyList<EffectDescription> Effects);
/** An edited emitter constant. */
public sealed record BundleEmitterEdit(uint TagHash, int Channel, int Offset, float[] Value);
/** The edits file: everything changed relative to a source manifest (identified by its hash). */
public sealed record AssetBundleEdits(string Format, int Version, string SourceManifestSha256,
    IReadOnlyList<BundleMeshEdit> Meshes, IReadOnlyList<BundleTextureEdit> Textures,
    IReadOnlyList<BundleAnimationEdit> Animations, IReadOnlyList<BundleMaterialEdit> Materials,
    IReadOnlyList<BundleEmitterEdit>? Emitters = null)
{
    public BundleCollisionEdits? Collision { get; init; }
}

/** Preparation is read-only; ApplyImport rechecks the project snapshot before publishing changes. */
public sealed record PreparedAssetBundleImport(AssetBundleImportMode Mode, uint RootTag,
    string ProjectFingerprint, IReadOnlyDictionary<uint, byte[]> Edits,
    IReadOnlyDictionary<string, byte[]> AddedEdits, IReadOnlyList<ProjectAssetAddition> Additions,
    IReadOnlyList<BundleNotice> Notices)
{
    public IReadOnlyDictionary<uint, string> SourceFingerprints { get; init; } = new Dictionary<uint, string>();
    /** Collision instance moves; they are saved as object edits in the same manifest write. */
    public IReadOnlyList<ObjectEdit> ObjectEdits { get; init; } = [];
    /** New collision shape packfiles; their fixed entry indices match the owner rows in Edits. */
    public IReadOnlyList<ProjectAssetAddition> CollisionShapes { get; init; } = [];
}
/** Result of an import: root tag, number of changed assets, added assets and notices. */
public sealed record AssetBundleImportResult(uint RootTag, string? RootAddedAssetId, int ChangedAssets,
    IReadOnlyList<AddedProjectAsset> AddedAssets, IReadOnlyList<BundleNotice> Notices);
