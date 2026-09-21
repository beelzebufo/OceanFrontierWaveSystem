shader_type spatial;
render_mode world_vertex_coords, cull_disabled;

#include "res://Ocean/Shaders/Rendering/animated_wave_surface_contract.gdshaderinc"

uniform bool show_surface_grid = true;
uniform bool show_surface_markers = true;
uniform bool lighting_enabled = true;
uniform float diagnostic_roughness : hint_range(0.0, 1.0) = 0.65;

varying float displacement_height;
varying vec2 base_uv;

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

    NORMAL =
        compute_surface_normal(
            world_xz,
            current_lod_texel_width,
            current_disp,
            next_disp,
            lod_alpha);

    VERTEX.x =
        world_xz.x;

    VERTEX.z =
        world_xz.y;

    VERTEX +=
        displayed_displacement;

    displacement_height =
        composed_content.y;

    base_uv =
        world_to_lod_uv(
            world_xz,
            lod_center_xz,
            lod_world_size);
}

void fragment()
{
    float signed_height =
        displacement_height /
        (1.0 + abs(displacement_height));

    vec3 diagnostic_colour =
        mix(
            vec3(0.03, 0.2, 0.4),
            vec3(0.2, 0.85, 0.9),
            clamp(
                0.5 +
                0.5 *
                signed_height,
                0.0,
                1.0));

    if (show_surface_grid)
    {
        vec2 cell =
            abs(
                fract(
                    base_uv *
                    8.0 +
                    0.5) -
                0.5);

        float line =
            1.0 -
            step(
                0.012,
                min(
                    cell.x,
                    cell.y));

        diagnostic_colour =
            mix(
                diagnostic_colour,
                vec3(0.04, 0.07, 0.1),
                line * 0.8);
    }

    if (show_surface_markers)
    {
        vec2 edge =
            min(
                base_uv,
                vec2(1.0) -
                base_uv);

        float boundary =
            1.0 -
            step(
                0.006,
                min(
                    edge.x,
                    edge.y));

        diagnostic_colour =
            mix(
                diagnostic_colour,
                vec3(1.0, 0.7, 0.1),
                boundary);

        for (int x = 1; x <= 3; x++)
        {
            for (int y = 1; y <= 3; y++)
            {
                vec2 d =
                    base_uv -
                    vec2(
                        float(x),
                        float(y)) *
                    0.25;

                float marker =
                    1.0 -
                    step(
                        0.00015,
                        dot(d, d));

                diagnostic_colour =
                    mix(
                        diagnostic_colour,
                        vec3(1.0, 0.1, 0.15),
                        marker);
            }
        }
    }

    ROUGHNESS =
        diagnostic_roughness;

    if (lighting_enabled)
    {
        ALBEDO =
            diagnostic_colour;

        EMISSION =
            vec3(0.0);

        SPECULAR =
            0.5;
    }
    else
    {
        ALBEDO =
            vec3(0.0);

        EMISSION =
            diagnostic_colour;

        SPECULAR =
            0.0;
    }
}
