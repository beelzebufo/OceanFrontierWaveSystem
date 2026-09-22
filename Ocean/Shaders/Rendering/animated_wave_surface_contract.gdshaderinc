// Portions of this file are directly ported from Crest Water 4.
// Source: wave-harmonic/crest commit db0658ff0b2e93e4a9e28cc2867509658b0ecc00
// File: OceanVertHelpers.hlsl
// Crest is licensed under the MIT License. See THIRD_PARTY_NOTICES_CREST4.txt.

uniform sampler2DArray animated_wave_field : filter_linear, repeat_disable;
uniform vec2 lod_center_xz;
uniform float lod_world_size;
uniform float current_lod_texel_width;
uniform vec2 lod_focus_xz;
uniform float selected_lod;
uniform vec2 next_lod_center_xz;
uniform float next_lod_world_size;
uniform float next_lod_texel_width;
uniform float next_lod;
uniform bool has_next_lod = false;
uniform int normal_method = 0;
uniform int wave_content_mode = 0;
uniform int display_mode = 0;
uniform float horizontal_display_scale = 1.0;
uniform float vertical_display_scale = 1.0;
uniform float lod_scale_alpha = 0.0;
uniform float geometry_grid_width = 0.0;
uniform bool planet_curvature_enabled = false;
uniform float planet_radius = 6371000.0;

// Crest OceanRenderer computes these from the base mesh density:
//
//     baseMeshDensity = lodDataResolution * 0.25 / geometryDownSampleFactor
//     blackPoint      = 0.4 / (baseMeshDensity / 8)
//     whiteRange      = 1 - blackPoint - blackPoint
//
// Geometry.cs uploads the exact values when nested resources are built.
uniform float lod_alpha_black_point_fade = 0.06666667;
uniform float lod_alpha_black_point_white_point_fade = 0.86666667;

const int NORMAL_METHOD_BLENDED_XYZ_FORWARD = 0;
const int NORMAL_METHOD_CREST_PER_LOD_FORWARD = 1;

vec2 world_to_lod_uv(vec2 world_xz, vec2 center_xz, float world_size)
{
    return (world_xz - center_xz) / world_size + vec2(0.5);
}

vec3 sample_awf_lod(vec2 world_xz, vec2 center_xz, float world_size, float layer)
{
    vec2 uv = world_to_lod_uv(world_xz, center_xz, world_size);
    return textureLod(animated_wave_field, vec3(uv, layer), 0.0).xyz;
}


// Direct port of Crest Water 4 ComputeLodAlpha().
float compute_surface_lod_alpha(vec2 world_xz)
{
    // Taxicab distance from ocean center drives LOD transitions.
    vec2 offset_from_center =
        abs(
            world_xz -
            lod_focus_xz);

    float taxicab_norm =
        max(
            offset_from_center.x,
            offset_from_center.y);

    // Crest cascade scale = texture world size / 4.
    float cascade_scale =
        lod_world_size *
        0.25;

    float lod_alpha =
        taxicab_norm /
        cascade_scale -
        1.0;

    // Exact Crest remap. These constants are derived from actual mesh
    // density in AnimatedWaveSurfaceRenderer.Geometry.cs.
    lod_alpha =
        max(
            (
                lod_alpha -
                lod_alpha_black_point_fade
            ) /
            lod_alpha_black_point_white_point_fade,
            0.0);

    // Crest only applies ViewerAltitudeLevelAlpha to LOD0 geometry/shape.
    float mesh_scale_alpha =
        selected_lod < 0.5
            ? lod_scale_alpha
            : 0.0;

    lod_alpha =
        min(
            lod_alpha +
            mesh_scale_alpha,
            1.0);

    return lod_alpha;
}


// Direct Godot port of Crest Water 4 SnapAndTransitionVertLayout().
//
// Crest passes the tile object-matrix translation. In Godot the caller
// supplies MODEL_MATRIX * vec4(0,0,0,1), avoiding stage built-ins inside
// this helper function.
vec2 transition_vertex_layout(
    vec2 world_xz,
    vec2 object_world_xz,
    float grid_width,
    out float lod_alpha)
{
    if (grid_width <= 0.0)
    {
        lod_alpha =
            compute_surface_lod_alpha(
                world_xz);

        return world_xz;
    }

    float grid_size_2 =
        2.0 *
        grid_width;

    float grid_size_4 =
        4.0 *
        grid_width;

    // Crest: snap size is twice the original grid size to preserve the
    // eight-triangle layout while the ocean root follows the viewpoint.
    world_xz -=
        fract(
            object_world_xz /
            grid_size_2) *
        grid_size_2;

    lod_alpha =
        compute_surface_lod_alpha(
            world_xz);

    // Crest: smoothly transition the vertex layout toward the next LOD.
    vec2 m =
        fract(
            world_xz /
            grid_size_4);

    vec2 offset =
        m -
        vec2(0.5);

    const float MIN_RADIUS = 0.375;

    if (abs(offset.x) < MIN_RADIUS)
    {
        world_xz.x +=
            offset.x *
            lod_alpha *
            grid_size_4;
    }

    if (abs(offset.y) < MIN_RADIUS)
    {
        world_xz.y +=
            offset.y *
            lod_alpha *
            grid_size_4;
    }

    return world_xz;
}

void sample_surface_components(
    vec2 world_xz,
    out vec3 current_disp,
    out vec3 next_disp,
    out float lod_alpha)
{
    current_disp =
        sample_awf_lod(
            world_xz,
            lod_center_xz,
            lod_world_size,
            selected_lod);

    if (has_next_lod)
    {
        next_disp =
            sample_awf_lod(
                world_xz,
                next_lod_center_xz,
                next_lod_world_size,
                next_lod);

        lod_alpha =
            compute_surface_lod_alpha(
                world_xz);
    }
    else
    {
        next_disp =
            current_disp;

        lod_alpha =
            0.0;
    }
}

vec3 compose_wave_content(
    vec3 current_disp,
    vec3 next_disp,
    float lod_alpha)
{
    if (wave_content_mode == 1)
    {
        return has_next_lod
            ? current_disp - next_disp
            : current_disp;
    }

    if (wave_content_mode == 0 &&
        has_next_lod)
    {
        return mix(
            current_disp,
            next_disp,
            lod_alpha);
    }

    return current_disp;
}

vec3 apply_display_displacement(
    vec3 displacement)
{
    float horizontal =
        display_mode == 1
            ? 0.0
            : horizontal_display_scale;

    float vertical =
        display_mode == 2
            ? 0.0
            : vertical_display_scale;

    return displacement *
        vec3(
            horizontal,
            vertical,
            horizontal);
}

vec3 sample_render_displacement(
    vec2 world_xz,
    out vec3 current_disp,
    out vec3 next_disp,
    out float lod_alpha,
    out vec3 composed_content)
{
    sample_surface_components(
        world_xz,
        current_disp,
        next_disp,
        lod_alpha);

    composed_content =
        compose_wave_content(
            current_disp,
            next_disp,
            lod_alpha);

    return apply_display_displacement(
        composed_content);
}

float compute_planet_sag(
    vec2 world_xz)
{
    if (!planet_curvature_enabled)
    {
        return 0.0;
    }

    float radius =
        max(
            planet_radius,
            10000.0);

    vec2 delta =
        world_xz -
        lod_focus_xz;

    float distance_squared =
        dot(
            delta,
            delta);

    float radius_squared =
        radius *
        radius;

    // The tangent-plane projection of a sphere is defined for d < R.
    // Clamp only the unreachable far tail of an exaggerated diagnostic
    // radius so sqrt() cannot receive a negative value.
    float safe_distance_squared =
        min(
            distance_squared,
            radius_squared *
            0.999999);

    return
        safe_distance_squared /
        (
            radius +
            sqrt(
                max(
                    radius_squared -
                    safe_distance_squared,
                    0.0))
        );
}

vec3 apply_planet_curvature(
    vec3 render_position,
    vec2 flat_world_xz)
{
    render_position.y -=
        compute_planet_sag(
            flat_world_xz);

    return render_position;
}

vec3 evaluate_render_surface_position(
    vec2 world_xz)
{
    vec3 current_disp;
    vec3 next_disp;
    float lod_alpha;
    vec3 composed_content;

    vec3 displacement =
        sample_render_displacement(
            world_xz,
            current_disp,
            next_disp,
            lod_alpha,
            composed_content);

    return apply_planet_curvature(
        vec3(
            world_xz.x,
            0.0,
            world_xz.y) +
        displacement,
        world_xz);
}

vec3 safe_surface_normal(
    vec3 cross_product)
{
    float len2 =
        dot(
            cross_product,
            cross_product);

    if (len2 < 1e-12)
    {
        return vec3(
            0.0,
            1.0,
            0.0);
    }

    return cross_product *
        inversesqrt(
            len2);
}

vec3 normal_blended_xyz_forward(
    vec2 world_xz,
    float finite_difference_step,
    vec3 center_current_disp,
    vec3 center_next_disp,
    float center_lod_alpha)
{
    vec3 center_content =
        compose_wave_content(
            center_current_disp,
            center_next_disp,
            center_lod_alpha);

    vec3 center_render_disp =
        apply_display_displacement(
            center_content);

    vec3 p0 =
        apply_planet_curvature(
            vec3(
                world_xz.x,
                0.0,
                world_xz.y) +
            center_render_disp,
            world_xz);

    vec3 px =
        evaluate_render_surface_position(
            world_xz +
            vec2(
                finite_difference_step,
                0.0));

    vec3 pz =
        evaluate_render_surface_position(
            world_xz +
            vec2(
                0.0,
                finite_difference_step));

    return safe_surface_normal(
        cross(
            pz - p0,
            px - p0));
}

vec3 normal_crest_lod_forward(
    vec2 world_xz,
    vec2 center_xz,
    float world_size,
    float layer,
    float texel_width,
    vec3 center_disp)
{
    vec3 center_render_disp =
        apply_display_displacement(
            center_disp);

    vec2 world_xz_x =
        world_xz +
        vec2(
            texel_width,
            0.0);

    vec3 render_disp_x =
        apply_display_displacement(
            sample_awf_lod(
                world_xz_x,
                center_xz,
                world_size,
                layer));

    vec2 world_xz_z =
        world_xz +
        vec2(
            0.0,
            texel_width);

    vec3 render_disp_z =
        apply_display_displacement(
            sample_awf_lod(
                world_xz_z,
                center_xz,
                world_size,
                layer));

    vec3 tangent_x;
    vec3 tangent_z;

    if (planet_curvature_enabled)
    {
        vec3 p0 =
            apply_planet_curvature(
                vec3(
                    world_xz.x,
                    0.0,
                    world_xz.y) +
                center_render_disp,
                world_xz);

        vec3 px =
            apply_planet_curvature(
                vec3(
                    world_xz_x.x,
                    0.0,
                    world_xz_x.y) +
                render_disp_x,
                world_xz_x);

        vec3 pz =
            apply_planet_curvature(
                vec3(
                    world_xz_z.x,
                    0.0,
                    world_xz_z.y) +
                render_disp_z,
                world_xz_z);

        tangent_x =
            px - p0;

        tangent_z =
            pz - p0;
    }
    else
    {
        // Preserve the original flat Crest path exactly, including its
        // small local differences and numerical precision far from origin.
        tangent_x =
            vec3(
                texel_width,
                0.0,
                0.0) +
            render_disp_x -
            center_render_disp;

        tangent_z =
            vec3(
                0.0,
                0.0,
                texel_width) +
            render_disp_z -
            center_render_disp;
    }

    vec3 cross_product =
        cross(
            tangent_z,
            tangent_x);

    cross_product.y =
        max(
            cross_product.y,
            0.0001);

    return normalize(
        cross_product);
}

vec3 normal_crest_per_lod_forward(
    vec2 world_xz,
    vec3 center_current_disp,
    vec3 center_next_disp,
    float center_lod_alpha)
{
    if (wave_content_mode != 0)
    {
        return normal_blended_xyz_forward(
            world_xz,
            current_lod_texel_width,
            center_current_disp,
            center_next_disp,
            center_lod_alpha);
    }

    float wt_smaller_lod =
        1.0 -
        center_lod_alpha;

    float wt_bigger_lod =
        has_next_lod
            ? 1.0 - wt_smaller_lod
            : 0.0;

    vec2 accumulated_normal_xz =
        vec2(0.0);

    if (wt_smaller_lod > 0.001)
    {
        vec3 normal_current =
            normal_crest_lod_forward(
                world_xz,
                lod_center_xz,
                lod_world_size,
                selected_lod,
                current_lod_texel_width,
                center_current_disp);

        accumulated_normal_xz +=
            wt_smaller_lod *
            normal_current.xz;
    }

    if (has_next_lod &&
        wt_bigger_lod > 0.001)
    {
        vec3 normal_next =
            normal_crest_lod_forward(
                world_xz,
                next_lod_center_xz,
                next_lod_world_size,
                next_lod,
                next_lod_texel_width,
                center_next_disp);

        accumulated_normal_xz +=
            wt_bigger_lod *
            normal_next.xz;
    }

    return normalize(
        vec3(
            accumulated_normal_xz.x,
            1.0,
            accumulated_normal_xz.y));
}

vec3 compute_surface_normal(
    vec2 world_xz,
    float finite_difference_step,
    vec3 center_current_disp,
    vec3 center_next_disp,
    float center_lod_alpha)
{
    if (normal_method ==
        NORMAL_METHOD_CREST_PER_LOD_FORWARD)
    {
        return normal_crest_per_lod_forward(
            world_xz,
            center_current_disp,
            center_next_disp,
            center_lod_alpha);
    }

    return normal_blended_xyz_forward(
        world_xz,
        finite_difference_step,
        center_current_disp,
        center_next_disp,
        center_lod_alpha);
}
