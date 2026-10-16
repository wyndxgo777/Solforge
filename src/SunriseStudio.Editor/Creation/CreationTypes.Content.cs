namespace SunriseStudio.Editor.Creation;

/**
 * CreationTypes part: registers texture, material, screen and other content types with the reasons some stay
 * blocked.
 */
public static partial class CreationTypes
{
    private const string NoScreen = "New screens are not supported yet.";

    static partial void AddContentTypes()
    {
        Add(TextureKey, CreationMode.New, new("Texture", "A 2D RGBA8 texture from a PNG.", "",
            workspace => new TextureWorkflow(workspace)));
        Add(TextureKey, CreationMode.Clone, new("Texture", "", "A copy would share its pixels."));
        Add(MaterialKey, CreationMode.New, new("Material", "", "Shaders cannot be authored."));
        Add(MaterialKey, CreationMode.Clone, new("Material", "A copy with its own textures and constants.", "",
            workspace => new MaterialWorkflow(workspace)));
        Add(StaticPropKey, CreationMode.New, new("Static prop", "A model from a mesh file. Not placed in any map.", "",
            workspace => new StaticPropWorkflow(workspace)));
        Add(StaticPropKey, CreationMode.Clone, new("Static prop", "", "A copy would share its buffers and material."));
        Add(StudioCategories.CuiName, CreationMode.New, new("Screen", "", NoScreen));
        Add(StudioCategories.CuiName, CreationMode.Clone, new("Screen", "", NoScreen));
        Add(StudioCategories.AudioName, CreationMode.New, new("Audio", "", AudioBlocker));
        Add(StudioCategories.AudioName, CreationMode.Clone,
            new("Audio", "", "Nothing plays a copy until a config names it."));
    }
}
