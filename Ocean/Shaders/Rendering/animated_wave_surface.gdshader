shader_type spatial;
render_mode world_vertex_coords, cull_disabled;

#include "res://Ocean/Shaders/Rendering/animated_wave_surface_contract.gdshaderinc"


uniform bool show_surface_grid = true;
uniform bool show_surface_markers = true;
uniform bool lighting_enabled = true;

uniform float diagnostic_roughness :
    hint_range(0.0, 1.0) = 0.65;


// Renderer-only visual micro-normal layer.
//
// This texture never modifies AnimatedWaveField.
// It only modifies the lighting normal in fragment().
uniform bool visual_micro_normals_enabled = false;

uniform sampler2D visual_normal_map :
    hint_normal,
    repeat_enable,
    filter_linear_mipmap_anisotropic;


// Crest-compatible parameter convention.
//
// Crest default:
//     _NormalsScale = 40
uniform float visual_normal_scale = 40.0;


// Crest default:
//     _NormalsStrength = 0.36
uniform float visual_normal_strength = 0.36;


varying float displacement_height;

varying vec2 base_uv;


// Undisplaced world-space position after topology morph.
//
// This is deliberately captured before wave displacement.
// Crest also samples visual normals from the undisplaced
// horizontal position.
varying vec2 undisplaced_world_xz;


// Same spatial / altitude transition alpha used by this vertex.
//
// Used by visual normals to transition continuously to the
// next x2 tiled-normal scale.
varying float visual_normal_lod_alpha;



//
// Crest Water 4 reference:
//
// OceanNormalMapping.hlsl
//
//     v0 = ( 0.94,  0.34)
//     v1 = (-0.85, -0.53)
//
// Two differently directed scrolling samples prevent the
// normal pattern from looking like one translated texture.
//

vec2 sample_visual_normal_pair(
    vec2 world_xz,
    float stretch,
    float scroll_speed)
{
    if (stretch <= 0.000001)
    {
        return vec2(0.0);
    }


    const vec2 direction_0 =
        vec2(
            0.94,
            0.34);


    const vec2 direction_1 =
        vec2(
            -0.85,
            -0.53);


    vec2 uv_0 =
        (
            world_xz +
            direction_0 *
            TIME *
            scroll_speed
        ) /
        stretch;


    vec2 uv_1 =
        (
            world_xz +
            direction_1 *
            TIME *
            scroll_speed
        ) /
        stretch;


    //
    // Crest uses UnpackNormal(...).xy.
    //
    // For a Godot normal texture the stored RG components are
    // still in the 0..1 range, so convert them to -1..1.
    //

    vec2 normal_0 =
        texture(
            visual_normal_map,
            uv_0).rg *
        2.0 -
        1.0;


    vec2 normal_1 =
        texture(
            visual_normal_map,
            uv_1).rg *
        2.0 -
        1.0;


    //
    // Match Crest:
    //
    // norm = sample0.xy + sample1.xy
    //

    return
        normal_0 +
        normal_1;
}



//
// Crest computes the two scroll speeds from the physical
// Animated Waves texel width:
//
// speedLow  = pow(log(1 + 2 * texelWidth) * 1.875, 1.4)
// speedHigh = pow(log(1 + 4 * texelWidth) * 1.875, 1.4)
//
// The second speed corresponds to the next x2 visual scale.
//

float visual_normal_scroll_speed(
    float texel_width,
    float texel_multiplier)
{
    float x =
        log(
            1.0 +
            texel_multiplier *
            texel_width) *
        1.875;


    return pow(
        max(
            x,
            0.0),
        1.4);
}



//
// Renderer-only micro-normal slope.
//
// Crest:
//
//     nstretch =
//         normalScale *
//         cascadeScale /
//         100
//
// Our physical LOD world width is:
//
//     worldSize = 4 * cascadeScale
//
// therefore:
//
//     cascadeScale = worldSize / 4
//
// and:
//
//     stretch =
//         normalScale *
//         worldSize /
//         400
//
// For current baseline:
//
//     worldSize = 32m
//     normalScale = 40
//
//     stretch = 3.2m
//

vec2 sample_visual_micro_normal(
    vec2 world_xz,
    float lod_alpha)
{
    float normal_scale =
        max(
            visual_normal_scale,
            0.01);


    float current_stretch =
        normal_scale *
        lod_world_size /
        400.0;


    float current_speed =
        visual_normal_scroll_speed(
            current_lod_texel_width,
            2.0);


    vec2 current_normal =
        sample_visual_normal_pair(
            world_xz,
            current_stretch,
            current_speed);


    //
    // Crest blends into the next x2 tiled-normal scale as the
    // geometry approaches the next spatial LOD.
    //
    // This does NOT require another AWF slice. It is just another
    // sampling scale of the same repeating normal texture.
    //

    float blend =
        clamp(
            lod_alpha,
            0.0,
            1.0);


    if (blend > 0.001)
    {
        float next_stretch =
            current_stretch *
            2.0;


        float next_speed =
            visual_normal_scroll_speed(
                current_lod_texel_width,
                4.0);


        vec2 next_normal =
            sample_visual_normal_pair(
                world_xz,
                next_stretch,
                next_speed);


        current_normal =
            mix(
                current_normal,
                next_normal,
                blend);
    }


    return
        current_normal *
        max(
            visual_normal_strength,
            0.0);
}



//
// Add renderer-only micro slope to the geometric AWF normal.
//
// Godot's fragment NORMAL is view-space.
//
// Crest performs its addition in the ocean/world-aligned frame:
//
//     n_pixel.xz += microNormal.xy
//
// so:
//
// view normal
//     -> world
//     -> modify XZ
//     -> normalize
//     -> view
//

vec3 apply_visual_micro_normal(
    vec3 view_normal,
    vec2 world_xz,
    float lod_alpha)
{
    if (!visual_micro_normals_enabled ||
        visual_normal_strength <= 0.0)
    {
        return view_normal;
    }


    vec3 world_normal =
        normalize(
            mat3(
                INV_VIEW_MATRIX) *
            view_normal);


    vec2 micro_normal =
        sample_visual_micro_normal(
            world_xz,
            lod_alpha);


    world_normal.xz +=
        micro_normal;


    world_normal =
        normalize(
            world_normal);


    return normalize(
        mat3(
            VIEW_MATRIX) *
        world_normal);
}



void vertex()
{
    //
    // world_vertex_coords is enabled:
    // VERTEX is already world-space.
    //

    vec2 world_xz =
        VERTEX.xz;


    float lod_alpha;


    //
    // Project-specific common-focus topology morph.
    //
    // No NODE_POSITION_WORLD/per-tile snapping:
    // our nested rings already use explicit stitched edges.
    //

    world_xz =
        transition_vertex_layout(
            world_xz,
            geometry_grid_width,
            lod_alpha);


    //
    // Canonical AWF is sampled after topology morph but before
    // displacement is applied.
    //

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


    //
    // Geometric surface normal remains entirely AWF-derived.
    //
    // Visual normal texture is NOT applied here.
    //

    NORMAL =
        compute_surface_normal(
            world_xz,
            current_lod_texel_width,
            current_disp,
            next_disp,
            lod_alpha);


    //
    // Save renderer-only fragment inputs before displacement.
    //

    undisplaced_world_xz =
        world_xz;


    visual_normal_lod_alpha =
        lod_alpha;


    //
    // Apply canonical displacement.
    //

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
    //
    // Visual micro normal modifies only the shading normal.
    //
    // It has no effect on:
    //
    // - mesh displacement;
    // - AnimatedWaveField;
    // - GPU point queries;
    // - physics;
    // - geometric normal diagnostic.
    //

    NORMAL =
        apply_visual_micro_normal(
            NORMAL,
            undisplaced_world_xz,
            visual_normal_lod_alpha);


    float signed_height =
        displacement_height /
        (
            1.0 +
            abs(
                displacement_height)
        );


    vec3 diagnostic_colour =
        mix(
            vec3(
                0.03,
                0.2,
                0.4),

            vec3(
                0.2,
                0.85,
                0.9),

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

                vec3(
                    0.04,
                    0.07,
                    0.1),

                line *
                0.8);
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

                vec3(
                    1.0,
                    0.7,
                    0.1),

                boundary);


        for (int x = 1;
             x <= 3;
             x++)
        {
            for (int y = 1;
                 y <= 3;
                 y++)
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
                        dot(
                            d,
                            d));


                diagnostic_colour =
                    mix(
                        diagnostic_colour,

                        vec3(
                            1.0,
                            0.1,
                            0.15),

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