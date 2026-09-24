shader_type spatial;
render_mode world_vertex_coords, cull_disabled, depth_draw_always;

#include "res://Ocean/Shaders/Rendering/animated_wave_surface_contract.gdshaderinc"


uniform bool show_surface_grid = true;
uniform bool show_surface_markers = true;
uniform bool lighting_enabled = true;

uniform float diagnostic_roughness :
    hint_range(0.0, 1.0) = 0.65;

uniform float water_ior :
    hint_range(1.0, 2.0, 0.001) = 1.333;

uniform float water_refraction_strength :
    hint_range(0.0, 0.1, 0.0001) = 0.008;

// Extinction coefficients are inverse metres. Godot world units are treated
// as metres for this optical baseline.
uniform vec3 water_extinction =
    vec3(0.18, 0.07, 0.025);

uniform vec3 water_scatter_color :
    source_color = vec3(0.015, 0.12, 0.18);

uniform vec3 water_shallow_scatter_color :
    source_color = vec3(0.03, 0.22, 0.18);

uniform float water_shallow_color_strength :
    hint_range(0.0, 1.0, 0.01) = 1.0;

uniform float water_shallow_depth_max :
    hint_range(0.1, 50.0, 0.1) = 10.0;

uniform float water_shallow_depth_power :
    hint_range(0.01, 10.0, 0.01) = 2.5;

uniform bool has_sea_floor_depth = false;

uniform sampler2DArray sea_floor_depth_field :
    repeat_disable,
    filter_nearest;

// Direction in which the primary sun's rays travel, from the sun toward the
// world. Godot DirectionalLight3D emits along its global local -Z axis.
uniform vec3 primary_sun_ray_direction_world =
    vec3(0.0, -1.0, 0.0);

// Linear RGB radiance supplied explicitly by the renderer. Godot's built-in
// light loop remains responsible for surface reflection and direct specular.
uniform vec3 primary_sun_radiance =
    vec3(0.0);

uniform vec3 water_sun_scatter_tint :
    source_color = vec3(0.25, 0.65, 0.70);

uniform float water_sun_scatter_strength :
    hint_range(0.0, 4.0, 0.01) = 0.5;

uniform float water_sun_scatter_falloff :
    hint_range(1.0, 16.0, 0.1) = 5.0;

uniform sampler2D screen_texture :
    hint_screen_texture,
    repeat_disable,
    filter_linear;

uniform sampler2D depth_texture :
    hint_depth_texture,
    repeat_disable,
    filter_nearest;


// Renderer-only visual micro-normal layer.
//
// This texture never modifies AnimatedWaveField.
// It only modifies the lighting normal in fragment().
uniform bool visual_micro_normals_enabled = false;

uniform sampler2D visual_normal_map :
    hint_normal,
    repeat_enable,
    filter_linear_mipmap_anisotropic;
	
	
uniform vec3 water_horizon_radiance :
    source_color = vec3(0.69, 0.79, 0.90);

uniform float water_horizon_guard_fade :
    hint_range(0.001, 0.2, 0.001) = 0.03;	


// Crest-compatible parameter convention.
//
// Crest default:
//     _NormalsScale = 40
uniform float visual_normal_scale = 40.0;


// Conservative validation default; Crest's source default is 0.36.
uniform float visual_normal_strength = 0.08;

// Ocean simulation time supplied by OceanRuntime.
// Unlike Godot TIME, this freezes with SimulationPaused
// and respects SimulationTimeScale.
uniform float visual_time = 0.0;


varying float displacement_height;

// Raw det(I + horizontal displacement gradient), sampled and blended in the
// vertex stage. Non-canonical diagnostic geometry deliberately receives 1.
varying float surface_det_j;

varying vec2 base_uv;


// Undisplaced world-space position after topology morph.
//
// Crest samples visual normals using the undisplaced
// horizontal position.
varying vec2 undisplaced_world_xz;


// Same transition alpha used by displacement/topology.
//
// Visual normals use it to blend continuously into
// the next x2 texture scale.
varying float visual_normal_lod_alpha;


const float MAX_OPTICAL_PATH_METRES = 100.0;


vec3 reconstruct_view_position(
    vec2 uv,
    float depth,
    mat4 inverse_projection)
{
    // Forward+ and Mobile use Godot's reverse-Z [0, 1] NDC convention.
    vec4 view_position =
        inverse_projection *
        vec4(
            uv * 2.0 - 1.0,
            depth,
            1.0);


    return
        view_position.xyz /
        view_position.w;
}


vec2 clamp_screen_uv(
    vec2 uv,
    vec2 viewport_size)
{
    vec2 half_texel =
        0.5 /
        max(
            viewport_size,
            vec2(1.0));


    return clamp(
        uv,
        half_texel,
        vec2(1.0) -
            half_texel);
}


float water_f0_from_ior(
    float ior)
{
    float ratio =
        (1.0 - ior) /
        (1.0 + ior);


    return
        ratio *
        ratio;
}


float water_fresnel_schlick(
    float f0,
    float ndotv)
{
    return
        f0 +
        (1.0 - f0) *
        pow(
            1.0 - ndotv,
            5.0);
}



//
// Crest Water 4:
//
// OceanNormalMapping.hlsl
//
//     v0 = ( 0.94,  0.34)
//     v1 = (-0.85, -0.53)
//
// Two differently directed samples avoid the appearance
// of one normal texture simply translating over the ocean.
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
            visual_time *
            scroll_speed
        ) /
        stretch;


    vec2 uv_1 =
        (
            world_xz +
            direction_1 *
            visual_time *
            scroll_speed
        ) /
        stretch;


    //
    // Equivalent to Crest UnpackNormal(...).xy
    // for an ordinary RGB normal texture.
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


    return
        normal_0 +
        normal_1;
}



//
// Crest:
//
// speedLow =
//     pow(log(1 + 2 * texelWidth) * 1.875, 1.4)
//
// speedHigh =
//     pow(log(1 + 4 * texelWidth) * 1.875, 1.4)
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
// Crest:
//
//     nstretch =
//         normalScale *
//         cascadeScale /
//         100
//
// Our:
//
//     lodWorldSize =
//         4 * cascadeScale
//
// therefore:
//
//     stretch =
//         normalScale *
//         lodWorldSize /
//         400
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
    // Blend into the next x2 normal-map scale.
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
// Fragment NORMAL is view-space.
//
// The micro-normal slope is defined in our world-aligned
// ocean XZ frame, so transform only that perturbation
// into view-space and add it to the existing geometric
// AWF normal.
//
// This avoids converting the complete normal
// view -> world -> view.
//

vec3 apply_visual_micro_normal(
    vec3 view_normal,
    vec2 world_xz,
    float lod_alpha,
    mat4 view_matrix)
{
    if (!visual_micro_normals_enabled ||
        visual_normal_strength <= 0.0)
    {
        return view_normal;
    }


    vec2 micro_normal =
        sample_visual_micro_normal(
            world_xz,
            lod_alpha);


    vec3 micro_view =
        (
            view_matrix *
            vec4(
                micro_normal.x,
                0.0,
                micro_normal.y,
                0.0)
        ).xyz;


    return normalize(
        view_normal +
        micro_view);
}



float sample_surface_water_depth_lod(
    vec2 world_xz,
    vec2 center_xz,
    float world_size,
    float lod_index,
    float depth_max)
{
    float terrain_y =
        textureLod(
            sea_floor_depth_field,
            vec3(
                world_to_lod_uv(
                    world_xz,
                    center_xz,
                    world_size),
                lod_index),
            0.0).r;


    if (isnan(terrain_y) ||
        isinf(terrain_y) ||
        terrain_y <= -65000.0)
    {
        return depth_max;
    }


    // SeaFloorDepth stores absolute terrain world Y. This renderer's sea
    // level contract is exactly Y=0, independent of displaced wave height.
    return clamp(
        -terrain_y,
        0.0,
        depth_max);
}


void vertex()
{
    //
    // world_vertex_coords is enabled:
    // VERTEX is already in world space.
    //

    vec2 world_xz =
        VERTEX.xz;


    // Crest SnapAndTransitionVertLayout receives the tile object-matrix
    // translation. Keep the stage-specific MODEL_MATRIX access here and
    // pass the value into the helper explicitly.
    vec2 object_world_xz =
        (
            MODEL_MATRIX *
            vec4(
                0.0,
                0.0,
                0.0,
                1.0)
        ).xz;


    float lod_alpha;


    world_xz =
        transition_vertex_layout(
            world_xz,
            object_world_xz,
            geometry_grid_width,
            lod_alpha);


    //
    // Canonical AWF sampling happens at the morphed,
    // undisplaced XZ position.
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
    // Geometric surface normal remains AWF-derived.
    //

    vec4 surface_normal_and_det_j =
        compute_surface_normal_and_det_j(
            world_xz,
            current_lod_texel_width,
            current_disp,
            next_disp,
            lod_alpha);


    NORMAL =
        surface_normal_and_det_j.xyz;


    surface_det_j =
        surface_normal_and_det_j.a;


    //
    // Renderer-only inputs for fragment micro normals.
    //

    undisplaced_world_xz =
        world_xz;


    visual_normal_lod_alpha =
        lod_alpha;


    //
    // Apply canonical displacement.
    //

    VERTEX =
        apply_planet_curvature(
            vec3(
                world_xz.x,
                0.0,
                world_xz.y) +
            displayed_displacement,
            world_xz);


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
    // Renderer-only detail.
    //
    // Does not affect:
    //
    // - AnimatedWaveField
    // - displacement
    // - GPU queries
    // - physics
    // - geometric normal diagnostics
    //

    NORMAL =
        apply_visual_micro_normal(
            NORMAL,
            undisplaced_world_xz,
            visual_normal_lod_alpha,
            VIEW_MATRIX);


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
        bool inside_normal_footprint =
            all(
                greaterThanEqual(
                    base_uv,
                    vec2(0.0))) &&
            all(
                lessThanEqual(
                    base_uv,
                    vec2(1.0)));


        vec2 edge =
            min(
                base_uv,
                vec2(1.0) -
                base_uv);


        float boundary =
            inside_normal_footprint
                ? 1.0 -
                    step(
                        0.006,
                        min(
                            edge.x,
                            edge.y))
                : 0.0;


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
                    inside_normal_footprint
                        ? 1.0 -
                            step(
                                0.00015,
                                dot(
                                    d,
                                    d))
                        : 0.0;


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


    METALLIC =
        0.0;


    // RADIANCE.rgb/alpha overrides only environment radiance. Alpha 0 keeps
    // Godot's native IBL unchanged. This lets the water guard reflections
    // that point below the world horizon without replacing the PBR BRDF.
    RADIANCE =
        vec4(0.0);


    if (lighting_enabled)
    {
        // Keep the diagnostic surface colour as a restrained overlay. The
        // refracted/scattered scene below is now the water body's base colour.
        ALBEDO =
            diagnostic_colour *
            (
                show_surface_grid ||
                show_surface_markers
                    ? 0.12
                    : 0.015
            );


        vec3 final_normal =
            normalize(
                NORMAL);


        // Crest prevents ocean reflections from sampling below the world
        // horizon. Godot's native IBL does not perform this water-specific
        // clamp, so low-roughness wave slopes can sample the dark ground
        // hemisphere and appear as black streaks.
        //
        // Mirror Godot 4.7's environment lookup direction first:
        //     ref = reflect(-view, normal)
        //     ref = mix(ref, normal, roughness^2)
        // Then detect only the below-world-horizon portion and replace that
        // environment radiance with the configured horizon radiance. Godot
        // still applies its native IBL BRDF/Fresnel afterwards.
        if (!planet_curvature_enabled)
        {
            vec3 reflection_view =
                reflect(
                    -normalize(
                        VIEW),
                    final_normal);


            reflection_view =
                mix(
                    reflection_view,
                    final_normal,
                    diagnostic_roughness *
                        diagnostic_roughness);


            vec3 reflection_world =
                normalize(
                    (
                        INV_VIEW_MATRIX *
                        vec4(
                            reflection_view,
                            0.0)
                    ).xyz);


            float below_horizon =
                smoothstep(
                    0.0,
                    max(
                        water_horizon_guard_fade,
                        0.0001),
                    -reflection_world.y);


            RADIANCE =
                vec4(
                    water_horizon_radiance,
                    below_horizon);
        }


        // Match Godot 4.7 BaseMaterial3D's screen-space refraction sign.
        vec2 candidate_uv =
            clamp_screen_uv(
                SCREEN_UV -
                    final_normal.xy *
                    water_refraction_strength,
                VIEWPORT_SIZE);


        float candidate_depth =
            textureLod(
                depth_texture,
                candidate_uv,
                0.0).r;


        vec3 candidate_view_position =
            reconstruct_view_position(
                candidate_uv,
                candidate_depth,
                INV_PROJECTION_MATRIX);


        // Positive separation means the refracted sample is behind the
        // current water fragment. Fade to the unrefracted UV across one metre
        // when the offset crosses foreground geometry.
        float refraction_validity =
            smoothstep(
                0.0,
                1.0,
                VERTEX.z -
                    candidate_view_position.z);


        vec2 refracted_uv =
            mix(
                SCREEN_UV,
                candidate_uv,
                refraction_validity);


        float scene_depth =
            textureLod(
                depth_texture,
                refracted_uv,
                0.0).r;


        vec3 scene_view_position =
            reconstruct_view_position(
                refracted_uv,
                scene_depth,
                INV_PROJECTION_MATRIX);


        // This deliberately uses geometric path length rather than Crest's
        // linear eye-Z difference. Visible opaque depth, not bathymetry data,
        // defines the optical consumer path.
        float optical_path =
            clamp(
                length(
                    scene_view_position -
                    VERTEX),
                0.0,
                MAX_OPTICAL_PATH_METRES);


        vec3 transmittance =
            exp(
                -max(
                    water_extinction,
                    vec3(0.0)) *
                optical_path);


        vec3 refracted_scene_colour =
            textureLod(
                screen_texture,
                refracted_uv,
                0.0).rgb *
            EXPOSURE;


        vec3 directional_scatter =
            vec3(0.0);


        // This is a deliberate Godot-side extension to Crest's directional
        // scattering approximation: attenuate only the new in-scattered
        // sunlight through a representative incident water path. The already
        // shaded refracted scene colour is never multiplied by this term.
        float sun_down_cos =
            max(
                -primary_sun_ray_direction_world.y,
                0.0);


        if (!planet_curvature_enabled &&
            water_sun_scatter_strength > 0.0 &&
            sun_down_cos > 0.0001)
        {
            vec3 view_world =
                normalize(
                    (
                        INV_VIEW_MATRIX *
                        vec4(
                            VIEW,
                            0.0)
                    ).xyz);


            vec3 to_sun_world =
                normalize(
                    -primary_sun_ray_direction_world);


            float towards_sun =
                pow(
                    max(
                        dot(
                            to_sun_world,
                            -view_world),
                        0.0),
                    water_sun_scatter_falloff);


            float grazing =
                1.0 -
                clamp(
                    view_world.y *
                        view_world.y,
                    0.0,
                    1.0);


            float crest_sss =
                clamp(
                    0.6 -
                        0.12 *
                        surface_det_j,
                    0.0,
                    1.0);


            vec3 into_water_world =
                -view_world;


            float representative_vertical_depth =
                0.5 *
                optical_path *
                max(
                    -into_water_world.y,
                    0.0);


            float sun_optical_path =
                clamp(
                    representative_vertical_depth /
                        max(
                            sun_down_cos,
                            0.0001),
                    0.0,
                    MAX_OPTICAL_PATH_METRES);


            vec3 sun_transmittance =
                exp(
                    -max(
                        water_extinction,
                        vec3(0.0)) *
                    sun_optical_path);


            directional_scatter =
                max(
                    primary_sun_radiance,
                    vec3(0.0)) *
                water_sun_scatter_tint *
                water_sun_scatter_strength *
                towards_sun *
                grazing *
                crest_sss *
                sun_transmittance;
        }


        float shallow_depth_max =
            max(
                water_shallow_depth_max,
                0.0001);


        float surface_water_depth =
            shallow_depth_max;


        if (has_sea_floor_depth &&
            !planet_curvature_enabled)
        {
            float current_water_depth =
                sample_surface_water_depth_lod(
                    undisplaced_world_xz,
                    lod_center_xz,
                    lod_world_size,
                    selected_lod,
                    shallow_depth_max);


            float next_water_depth =
                current_water_depth;


            if (has_next_lod)
            {
                next_water_depth =
                    sample_surface_water_depth_lod(
                        undisplaced_world_xz,
                        next_lod_center_xz,
                        next_lod_world_size,
                        next_lod,
                        shallow_depth_max);
            }


            surface_water_depth =
                mix(
                    current_water_depth,
                    next_water_depth,
                    clamp(
                        visual_normal_lod_alpha,
                        0.0,
                        1.0));
        }


        float normalized_water_depth =
            clamp(
                surface_water_depth /
                    shallow_depth_max,
                0.0,
                1.0);


        float shallowness =
            pow(
                1.0 -
                    normalized_water_depth,
                max(
                    water_shallow_depth_power,
                    0.0001)) *
            clamp(
                water_shallow_color_strength,
                0.0,
                1.0);


        if (planet_curvature_enabled)
        {
            shallowness =
                0.0;
        }


        vec3 base_scatter_colour =
            mix(
                water_scatter_color,
                water_shallow_scatter_color,
                shallowness);


        vec3 effective_scatter_colour =
            base_scatter_colour +
            directional_scatter;


        vec3 water_volume_colour =
            refracted_scene_colour *
                transmittance +
            effective_scatter_colour *
                (
                    vec3(1.0) -
                    transmittance
                );


        float water_f0 =
            water_f0_from_ior(
                water_ior);


        float ndotv =
            clamp(
                dot(
                    final_normal,
                    normalize(
                        VIEW)),
                0.0,
                1.0);


        float transmission_weight =
            1.0 -
            water_fresnel_schlick(
                water_f0,
                ndotv);


        // Godot PBR owns reflected energy. This emission term supplies only
        // the complementary transmitted/scattered energy.
        EMISSION =
            water_volume_colour *
            transmission_weight;


        SPECULAR =
            clamp(
                sqrt(
                    max(
                        water_f0,
                        0.0) /
                    0.16),
                0.0,
                1.0);
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


    // Screen-sampled transmission is explicitly composited above. Keep an
    // opaque result while the screen-texture dependency selects the
    // transparent pass.
    ALPHA =
        1.0;
}
