shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_always;

uniform vec4 material_color : source_color = vec4(1.0);
uniform float mean_sea_level_y = 0.0;
uniform mat4 source_projection;
uniform vec2 tile_index = vec2(0.0);
uniform vec2 atlas_grid = vec2(1.0);

void vertex()
{
    // Each reflector owns one disjoint tile of the shared capture. Use the
    // main camera's projection so changing the atlas shape cannot stretch it.
    vec4 clip = source_projection * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    // POSITION bypasses Godot's built-in projection conversion on Vulkan.
    // Match the original single-object capture's vertical orientation.
    clip.y = -clip.y;
    vec2 tile_center = vec2(
        -1.0 + (2.0 * tile_index.x + 1.0) / atlas_grid.x,
        -1.0 + (2.0 * tile_index.y + 1.0) / atlas_grid.y);
    clip.xy = clip.xy / atlas_grid + clip.w * tile_center;
    POSITION = clip;
}

void fragment()
{
    vec2 tile_uv = SCREEN_UV * atlas_grid - tile_index;
    if (any(lessThan(tile_uv, vec2(0.0))) ||
        any(greaterThan(tile_uv, vec2(1.0))))
        discard;

    // The proxy has already been reflected in world space. Its world Y above
    // the plane corresponds to submerged source geometry.
    vec3 proxy_world_position =
        (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
    if (proxy_world_position.y > mean_sea_level_y)
    {
        discard;
    }

    ALBEDO = material_color.rgb;
    ALPHA = material_color.a;
}
