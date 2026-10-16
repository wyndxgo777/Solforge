namespace SunriseStudio.Packages;

/** Tooling labels for tag class references; they are not Bungie's names. */
public static class ClassNames
{
    private static readonly Dictionary<uint, string> Names = new()
    {
        [0x80807190] = "s_static_mesh_instance_group",
        [0x80807193] = "s_static_special_mesh",
        [0x80807194] = "s_static_mesh_data",
        [0x8080719A] = "s_static_mesh_part",
        [0x8080719B] = "s_static_mesh_group",
        [0x808071A3] = "s_static_instance_transform",
        [0x808071A7] = "s_static_mesh",
        [0x808071E8] = "s_technique",
        [0x80807211] = "s_material_texture_assignment",
        [0x808073F3] = "s_sampler_reference",
        [0x8080966D] = "s_static_mesh_instances",
        [0x808071F3] = "s_scope",
        [0x80809802] = "s_wwise_event",
        [0x80809A88] = "s_localized_strings",
        [0x80809A8A] = "s_localized_strings_data",
        [0x8080713A] = "s_light_collection",
        [0x80807140] = "s_expensive_light",
        [0x80806F95] = "s_sky_objects",
        [0x80806E68] = "s_map_decals",
        [0x80807378] = "s_entity_model_mesh",
        [0x808073A5] = "s_entity_model",
        [0x80809C04] = "s_pattern_component",
        [0x80809C0F] = "s_entity",
        [0x8080714F] = "s_terrain",
        [0x808091E0] = "s_bubble_definition",
        [0x80808A54] = "s_map_container",
        // Labels for the role each class has in this viewer's decoders.
        [0x80800065] = "s_inline_string",
        [0x80806CB1] = "s_renderer_root",
        [0x80806EF4] = "s_map_resource_link",
        [0x80806F9B] = "s_sky_model_wrapper",
        [0x80807268] = "s_occlusion_map",
        [0x8080727A] = "s_skeleton_asset",
        [0x808072A9] = "s_cloth_declaration",
        [0x808072AD] = "s_cloth_pool",
        [0x80807DAE] = "s_bubble_parent",
        [0x808036F6] = "s_animation_definition",
        [0x8080858D] = "s_tfx_parameters",
        [0x80808F49] = "s_animation_clip",
        [0x808091DE] = "s_map_root",
        [0x80809162] = "s_spawn_points",
        [0x8080925B] = "s_scenario_state_entry",
        [0x8080925E] = "s_scenario_tag_set",
        [0x80809462] = "s_authored_object",
        [0x80809468] = "s_authored_object_set",
        [0x80809962] = "s_map_location_names",
        [0x80809994] = "s_scenario",
        [0x808099D6] = "s_map_placements",
        [0x80809B14] = "s_entity_link",
        [0x80809C36] = "s_entity_config",
        [0x808040B5] = "s_sandbox_perk_program",
        [0x80804A53] = "s_icon_container",
        [0x80804A69] = "s_icon_bitmap",
        [0x80804A55] = "s_icon_library",
        [0x80809BB6] = "s_network_class_record",
        [0x80809BBB] = "s_replication_schema",
        [0x80808E68] = "s_wwise_event_timing",
        [0x80808E69] = "s_wwise_event_shared",
        [0x80803465] = "s_animation_query_table",
        [0x80809EBB] = "s_texture_descriptor",
        [0x808072D2] = "s_plated_texture_set",
        [0x8080744A] = "s_art_wrapper",
        [0x80809C54] = "s_interface_map",
        [0x80806E28] = "s_particle_system",
        [0x80806E2C] = "s_particle_emitter",
        [0x80806CC6] = "s_effect_node",
        [0x80806F68] = "s_lens_flare",
        // Config component classes, read at config +0x10 and +0x18.
        [0x808072BD] = "s_model_component",
        [0x80807286] = "s_cloth_model_component",
        [0x80807273] = "s_cloth_model_primary",
        [0x808099C8] = "s_trigger_component",
        [0x808099C9] = "s_trigger_component_peer",
        [0x80809A3B] = "s_squad_component",
        [0x8080948F] = "s_squad_component_peer",
        [0x808094CF] = "s_squad_rule_component",
        [0x808094D0] = "s_squad_rule_component_peer",
        // Placement payload classes, read 4 bytes before the payload at placement row +0x78.
        [0x808071B3] = "s_static_instances_payload",
        [0x8080714B] = "s_terrain_payload",
        [0x80806F91] = "s_sky_payload",
        [0x80809160] = "s_spawn_points_payload",
        [0x80806F38] = "s_ambient_occlusion_payload",
        [0x80806F5A] = "s_lights_payload",
        [0x80806FAD] = "s_volume_payload",
        [0x80806CBF] = "s_lens_flare_payload",
    };

    public static IReadOnlyDictionary<uint, string> All => Names;

    /** Name of a class hash: the known name, else a label derived from the component role; null when neither exists. */
    public static string? Get(uint reference) => Names.GetValueOrDefault(reference)
        ?? (ComponentRoles.Label(reference) is { } role ? $"s_{role.Replace(' ', '_')}_component" : null);

    /** Name of a class hash, falling back to class_XXXXXXXX. */
    public static string Describe(uint reference) => Get(reference) ?? $"class_{reference:X8}";
}
