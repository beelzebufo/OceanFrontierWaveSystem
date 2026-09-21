shader_type spatial;
render_mode world_vertex_coords, cull_disabled, unshaded;

#include "res://Ocean/Shaders/Rendering/animated_wave_surface_contract.gdshaderinc"

uniform float diagnostic_normal_length = 0.1;

void vertex()
{
    vec2 world_xz =
        VERTEX.xz;

    float lod_alpha;

    world_xz =
        transition_vertex_layout(
            world_xz,
            geometry_grid_width,
            lod_alpha);

    vec3 current_disp =
        sample_awf_lod(
            world_xz,
            lod_center_xz,
            lod_world_size,
            selected_lod);

    vec3 next_disp =
        current_disp;

    if (has_next_lod)
    {
        next_disp =
            sample_awf_lod(
                world_xz,
                next_lod_center_xz,
                next_lod_world_size,
                next_lod);
    }
    else
    {
        lod_alpha =
            0.0;
    }

    vec3 composed_content =
        compose_wave_content(
            current_disp,
            next_disp,
            lod_alpha);

    vec3 displayed_displacement =
        apply_display_displacement(
            composed_content);

    vec3 surface_normal =
        compute_surface_normal(
            world_xz,
            current_lod_texel_width,
            current_disp,
            next_disp,
            lod_alpha);

    vec3 surface_position =
        vec3(
            world_xz.x,
            0.0,
            world_xz.y) +
        displayed_displacement;

    VERTEX =
        surface_position +
        surface_normal *
        diagnostic_normal_length *
        COLOR.r;
}

void fragment()
{
    ALBEDO =
        vec3(
            0.95,
            0.15,
            0.85);
}
