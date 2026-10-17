using SunriseStudio.Packages;
using SunriseStudio.Packages.TagSchemas;

namespace SunriseStudio.Formats;

/**
 * The investment offsets come from the tag schema registry (src/SunriseStudio.Packages/TagSchemas/schemas/investment.json),
 * the same rows dump_tag, get_tag and validate_tag show, so a corrected offset changes every reader at once.
 */
public sealed partial class InvestmentCatalog
{
    private static readonly TagSchema ItemSchema = TagSchemaRegistry.Default.Require(InvestmentTags.ItemClass);
    private static readonly TagSchema DisplaySchema = TagSchemaRegistry.Default.Require(InvestmentTags.ItemDisplayClass);
    private static readonly TagSchema IndexRowSchema = TagSchemaRegistry.Default.Require("investment_index_row");
    internal static readonly int MaximumOffset = ItemSchema.Offset("maximum_stack"), BucketOffset = ItemSchema.Offset("bucket"),
        TierOffset = ItemSchema.Offset("tier"), InstancedOffset = ItemSchema.Offset("instanced");
    internal static readonly int ItemNameOffset = DisplaySchema.Offset("name"), ItemTypeOffset = DisplaySchema.Offset("item_type"),
        ItemDescriptionOffset = DisplaySchema.Offset("description");
    internal static readonly int VendorNameOffset = TagSchemaRegistry.Default.Require(InvestmentTags.VendorDisplayClass).Offset("name");
    internal static readonly int IndexRowStride = IndexRowSchema.Size, IndexHashOffset = IndexRowSchema.Offset("hash"), IndexTagOffset = IndexRowSchema.Offset("tag");
}
