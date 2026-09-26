#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform image2D color_image;
layout(set = 0, binding = 1) uniform sampler2D depth_texture;
layout(set = 0, binding = 2) uniform sampler2DArray animated_wave_field;

struct AnimatedWaveLodParams
{
    vec2 center_xz;
    float scale;
    float texture_resolution;
    float one_over_texture_resolution;
    float texel_width;
    float weight;
    float max_wavelength;
};

layout(std430, set = 0, binding = 3) readonly buffer LodBuffer
{
    AnimatedWaveLodParams lods[];
}
animated_wave_lod_data;

layout(set = 0, binding = 4) uniform sampler2D caustics_texture;
layout(set = 0, binding = 5) uniform sampler2D caustics_distortion_texture;

layout(std140, set = 0, binding = 6) uniform UnderwaterFrameData
{
    mat4 camera_to_world;
    // x=time, y=caustics enabled, z=distortion enabled, w=max caustics mip.
    vec4 caustics_frame;
    // x=scale, y=texture average, z=strength, w=focal depth.
    vec4 caustics_config_0;
    // x=depth of field, y=distortion scale, z=distortion strength,
    // w=canonical AWF LOD count.
    vec4 caustics_config_1;
    // xyz=value-only low-frequency environment ambient, w=reserved.
    vec4 ambient_lighting;
}
frame;

layout(push_constant, std430) uniform Params
{
    vec2 raster_size;
    vec2 primary_sun_radiance_rg;
    mat4 inverse_projection;
    vec4 extinction_and_max_path;
    vec4 scatter_and_sun_radiance_b;
    vec4 sun_ray_direction_world_and_incident_path;
}
params;

const float APPROXIMATE_DIRECT_SUN_FRACTION = 0.25;
const float DIRECTIONAL_SCATTER_STRENGTH = 1.00;
const float DIRECTIONAL_SCATTER_FALLOFF = 4.0;
const float DEEP_SCATTER_AMBIENT_FLOOR = 0.45;
const float SUN_SHAFT_STRENGTH = 0.90;
const float SUN_FIELD_BASE = 0.62;
const vec2 SUN_FIELD_K0 = vec2(0.018, 0.006);
const vec2 SUN_FIELD_K1 = vec2(-0.013, 0.031);
const vec2 SUN_FIELD_K2 = vec2(0.043, -0.020);
const float SUN_FIELD_AMPLITUDE_0 = 0.27;
const float SUN_FIELD_AMPLITUDE_1 = 0.19;
const float SUN_FIELD_AMPLITUDE_2 = 0.14;
const float SUN_FIELD_SPEED_0 = 0.037;
const float SUN_FIELD_SPEED_1 = -0.023;
const float SUN_FIELD_SPEED_2 = 0.014;
const float SUN_FIELD_PHASE_0 = 0.40;
const float SUN_FIELD_PHASE_1 = 2.10;
const float SUN_FIELD_PHASE_2 = -1.30;
const float SUN_PROJECTION_FADE_START = 0.04;
const float SUN_PROJECTION_FADE_END = 0.16;


float stable_beer_integral_channel(
    float extinction,
    float transmittance,
    float optical_path)
{
    float optical_depth =
        extinction *
        optical_path;


    if (optical_depth < 0.001)
    {
        return
            optical_path *
            (
                1.0 -
                0.5 * optical_depth +
                optical_depth * optical_depth / 6.0
            );
    }


    return
        (1.0 - transmittance) /
        extinction;
}


vec3 stable_beer_integral(
    vec3 extinction,
    vec3 transmittance,
    float optical_path)
{
    return
        vec3(
            stable_beer_integral_channel(
                extinction.r,
                transmittance.r,
                optical_path),
            stable_beer_integral_channel(
                extinction.g,
                transmittance.g,
                optical_path),
            stable_beer_integral_channel(
                extinction.b,
                transmittance.b,
                optical_path));
}


vec3 integrate_attenuated_harmonic(
    vec2 wave_vector,
    float speed,
    float phase_offset,
    vec2 surface_a,
    vec2 surface_b,
    vec3 extinction,
    vec3 transmittance,
    float optical_path,
    float visual_time)
{
    float alpha =
        dot(
            wave_vector,
            surface_a) +
        speed * visual_time +
        phase_offset;


    float beta =
        dot(
            wave_vector,
            surface_b);


    float phase_span =
        beta *
        optical_path;


    vec3 beer_integral =
        stable_beer_integral(
            extinction,
            transmittance,
            optical_path);


    // For beta -> 0 the harmonic is constant along the segment. This exact
    // limit avoids cancellation in the general endpoint expression.
    if (abs(phase_span) < 0.001)
    {
        return
            sin(alpha) *
            beer_integral;
    }


    float end_phase =
        alpha +
        phase_span;


    float beta_squared =
        beta * beta;


    vec3 denominator =
        extinction * extinction +
        vec3(beta_squared);


    // Exact integral of exp(-extinction*t) * sin(alpha + beta*t), evaluated
    // at both segment endpoints independently for each extinction channel.
    return
        (
            extinction * sin(alpha) +
            vec3(beta * cos(alpha)) -
            transmittance *
                (
                    extinction * sin(end_phase) +
                    vec3(beta * cos(end_phase))
                )
        ) /
        denominator;
}


bool select_finest_covering_lod(
    vec2 world_xz,
    out int selected_lod,
    out vec2 selected_uv)
{
    int lod_count =
        max(
            int(frame.caustics_config_1.w + 0.5),
            0);


    for (int lod = 0; lod < lod_count; lod++)
    {
        AnimatedWaveLodParams slice =
            animated_wave_lod_data.lods[lod];


        float world_size =
            4.0 *
            slice.scale;


        vec2 uv =
            (world_xz - slice.center_xz) /
                world_size +
            vec2(0.5);


        float border =
            0.5 *
            slice.one_over_texture_resolution;


        if (all(greaterThanEqual(uv, vec2(border))) &&
            all(lessThanEqual(uv, vec2(1.0 - border))))
        {
            selected_lod = lod;
            selected_uv = uv;


            return true;
        }
    }


    selected_lod = 0;
    selected_uv = vec2(0.5);


    return false;
}


vec3 sample_animated_wave_lod(
    vec2 world_xz,
    int lod)
{
    AnimatedWaveLodParams slice =
        animated_wave_lod_data.lods[lod];


    float world_size =
        4.0 *
        slice.scale;


    vec2 uv =
        (world_xz - slice.center_xz) /
            world_size +
        vec2(0.5);


    return textureLod(
        animated_wave_field,
        vec3(uv, float(lod)),
        0.0).xyz;
}


void main()
{
    ivec2 pixel =
        ivec2(gl_GlobalInvocationID.xy);


    ivec2 size =
        ivec2(params.raster_size);


    if (pixel.x >= size.x || pixel.y >= size.y)
    {
        return;
    }


    vec4 scene_color =
        imageLoad(color_image, pixel);


    // Forward+/Mobile reverse-Z clear depth is zero. Background pixels keep
    // the established camera-path approximation and never read receiver AWF.
    float depth =
        texelFetch(depth_texture, pixel, 0).r;


    bool has_scene_geometry =
        depth > 0.000001;


    vec2 screen_uv =
        (vec2(pixel) + vec2(0.5)) /
        params.raster_size;


    vec4 view_h =
        params.inverse_projection *
        vec4(
            screen_uv * 2.0 - 1.0,
            max(depth, 0.000001),
            1.0);


    vec3 view_position =
        vec3(0.0, 0.0, -1.0);


    bool has_finite_view_position =
        abs(view_h.w) > 0.000001;


    if (has_finite_view_position)
    {
        view_position =
            view_h.xyz /
            view_h.w;


        has_finite_view_position =
            !any(isnan(view_position)) &&
            !any(isinf(view_position));
    }


    float optical_path =
        params.extinction_and_max_path.w;


    if (has_scene_geometry && has_finite_view_position)
    {
        optical_path =
            clamp(
                length(view_position),
                0.0,
                params.extinction_and_max_path.w);
    }


    vec3 extinction =
        max(
            params.extinction_and_max_path.xyz,
            vec3(0.0));


    vec3 transmittance =
        exp(-extinction * optical_path);


    vec3 primary_sun_radiance =
        vec3(
            params.primary_sun_radiance_rg,
            params.scatter_and_sun_radiance_b.w);


    float primary_sun_energy =
        max(
            max(primary_sun_radiance.r, primary_sun_radiance.g),
            primary_sun_radiance.b);


    vec3 camera_sun_transmittance =
        exp(
            -extinction *
            params.sun_ray_direction_world_and_incident_path.w);


    vec3 direction_toward_sun_world =
        normalize(
            -params.sun_ray_direction_world_and_incident_path.xyz);


    vec3 attenuated_scene_color =
        scene_color.rgb;


    bool used_receiver_sun_path =
        false;


    if (has_scene_geometry &&
        has_finite_view_position &&
        primary_sun_energy > 0.000001)
    {
        vec3 scene_world_position =
            (frame.camera_to_world *
             vec4(view_position, 1.0)).xyz;


        int selected_lod;
        vec2 selected_uv;


        bool has_animated_wave_coverage =
            select_finest_covering_lod(
                scene_world_position.xz,
                selected_lod,
                selected_uv);


        if (has_animated_wave_coverage)
        {
            // Start with the cheap direct lookup. For an animated surface
            // pixel, scene XZ is displaced XZ rather than the undisplaced
            // coordinate at which canonical AWF displacement was authored.
            vec3 receiver_surface_displacement =
                textureLod(
                    animated_wave_field,
                    vec3(selected_uv, float(selected_lod)),
                    0.0).xyz;


            float receiver_water_depth =
                receiver_surface_displacement.y -
                scene_world_position.y;


            float receiver_tolerance =
                clamp(
                    0.5 * animated_wave_lod_data.lods[selected_lod].texel_width,
                    0.05,
                    0.75);


            float ambiguity_limit =
                receiver_tolerance +
                length(receiver_surface_displacement.xz) +
                animated_wave_lod_data.lods[selected_lod].texel_width;


            if (receiver_water_depth > receiver_tolerance &&
                receiver_water_depth <= ambiguity_limit)
            {
                // Approximate q + D.xz(q) = visible XZ with two fixed-point
                // steps. Keep the selected canonical LOD fixed so refinement
                // cannot introduce a second LOD-selection discontinuity. The
                // existing clamp sampler remains defined if an intermediate
                // estimate crosses the half-texel coverage border.
                vec2 undisplaced_xz =
                    scene_world_position.xz -
                    receiver_surface_displacement.xz;


                receiver_surface_displacement =
                    sample_animated_wave_lod(
                        undisplaced_xz,
                        selected_lod);


                undisplaced_xz =
                    scene_world_position.xz -
                    receiver_surface_displacement.xz;


                receiver_surface_displacement =
                    sample_animated_wave_lod(
                        undisplaced_xz,
                        selected_lod);


                receiver_water_depth =
                    receiver_surface_displacement.y -
                    scene_world_position.y;
            }


            if (receiver_water_depth > receiver_tolerance &&
                direction_toward_sun_world.y > 0.0001)
            {
                float receiver_sun_path =
                    clamp(
                        receiver_water_depth /
                        direction_toward_sun_world.y,
                        0.0,
                        params.extinction_and_max_path.w);


                vec3 receiver_sun_transmittance =
                    exp(-extinction * receiver_sun_path);


                if (frame.caustics_frame.y > 0.5 &&
                    frame.caustics_config_0.z > 0.0)
                {
                    // Crest-style one-step projected receiver lookup. Factor
                    // four bounds grazing-angle stretching; this is not a raymarch.
                    vec2 light_projection =
                        direction_toward_sun_world.xz *
                        receiver_water_depth /
                        (4.0 * direction_toward_sun_world.y);


                    vec2 caustics_surface_xz =
                        scene_world_position.xz +
                        light_projection;


                    float caustics_scale =
                        max(frame.caustics_config_0.x, 0.1);


                    float visual_time =
                        frame.caustics_frame.x;


                    vec2 caustics_uv_1 =
                        caustics_surface_xz / caustics_scale +
                        vec2(
                            0.044 * visual_time + 17.16,
                            -0.169 * visual_time);


                    vec2 caustics_uv_2 =
                        1.37 * caustics_surface_xz / caustics_scale +
                        vec2(
                            0.248 * visual_time,
                            0.117 * visual_time);


                    if (frame.caustics_frame.z > 0.5)
                    {
                        vec2 distortion_uv =
                            caustics_surface_xz /
                                max(frame.caustics_config_1.y, 0.0001) +
                            vec2(
                                0.031 * visual_time,
                                -0.027 * visual_time);


                        vec2 distortion_offset =
                            (textureLod(
                                caustics_distortion_texture,
                                distortion_uv,
                                0.0).rg * 2.0 - 1.0) *
                            frame.caustics_config_1.z;


                        caustics_uv_1 += distortion_offset;
                        caustics_uv_2 += distortion_offset;
                    }


                    float caustics_mip_lod =
                        log2(max(length(view_position), 1.0)) +
                        abs(
                            receiver_water_depth -
                            frame.caustics_config_0.w) /
                        max(frame.caustics_config_1.x, 0.01);


                    caustics_mip_lod =
                        clamp(
                            caustics_mip_lod,
                            0.0,
                            max(frame.caustics_frame.w, 0.0));


                    vec3 luminance_weights =
                        vec3(0.2126, 0.7152, 0.0722);


                    float caustics_1 =
                        dot(
                            textureLod(
                                caustics_texture,
                                caustics_uv_1,
                                caustics_mip_lod).rgb,
                            luminance_weights);


                    float caustics_2 =
                        dot(
                            textureLod(
                                caustics_texture,
                                caustics_uv_2,
                                caustics_mip_lod).rgb,
                            luminance_weights);


                    float caustics_signal =
                        0.5 * (caustics_1 + caustics_2) -
                        frame.caustics_config_0.y;


                    float incident_sun =
                        dot(
                            receiver_sun_transmittance,
                            luminance_weights) *
                        smoothstep(
                            0.02,
                            0.25,
                            direction_toward_sun_world.y);


                    attenuated_scene_color *=
                        max(
                            0.0,
                            1.0 +
                            frame.caustics_config_0.z *
                            caustics_signal *
                            incident_sun);
                }


                // Exact order: caustics, direct sunlight attenuation, then
                // camera-to-receiver Beer-Lambert extinction below.
                attenuated_scene_color *=
                    mix(
                        vec3(1.0),
                        receiver_sun_transmittance,
                        APPROXIMATE_DIRECT_SUN_FRACTION);


                used_receiver_sun_path = true;
            }
        }


        // Preserve a bounded sunlight fallback outside canonical AWF coverage.
        if (!used_receiver_sun_path)
        {
            attenuated_scene_color *=
                mix(
                    vec3(1.0),
                    camera_sun_transmittance,
                    APPROXIMATE_DIRECT_SUN_FRACTION);
        }
    }


    vec3 view_ray_world =
        normalize(
            (frame.camera_to_world *
             vec4(view_position, 0.0)).xyz);


    float forward_scatter =
        pow(
            max(
                dot(view_ray_world, direction_toward_sun_world),
                0.0),
            DIRECTIONAL_SCATTER_FALLOFF);


    vec3 directional_scatter =
        params.scatter_and_sun_radiance_b.xyz *
        primary_sun_radiance *
        camera_sun_transmittance *
        DIRECTIONAL_SCATTER_STRENGTH *
        forward_scatter;


    // For P(t) = camera + viewRay*t, projecting back to mean ocean level
    // along sunlight gives surfaceXZ(t) = surfaceA + surfaceB*t. Integrate
    // the smooth harmonic light field over that complete affine segment so
    // no discrete depth samples can appear as separate layers.
    vec3 shaft_ray_direction =
        -direction_toward_sun_world;


    vec3 camera_world_position =
        frame.camera_to_world[3].xyz;


    float safe_sun_y =
        min(
            shaft_ray_direction.y,
            -SUN_PROJECTION_FADE_START);


    vec2 surface_a =
        camera_world_position.xz -
        shaft_ray_direction.xz *
            (camera_world_position.y / safe_sun_y);


    vec2 surface_b =
        view_ray_world.xz -
        shaft_ray_direction.xz *
            (view_ray_world.y / safe_sun_y);


    float visual_time =
        frame.caustics_frame.x;


    vec3 shaft_beer_integral =
        stable_beer_integral(
            extinction,
            transmittance,
            optical_path);


    vec3 shaft_field_integral =
        SUN_FIELD_BASE *
            shaft_beer_integral +
        SUN_FIELD_AMPLITUDE_0 *
        integrate_attenuated_harmonic(
            SUN_FIELD_K0,
            SUN_FIELD_SPEED_0,
            SUN_FIELD_PHASE_0,
            surface_a,
            surface_b,
            extinction,
            transmittance,
            optical_path,
            visual_time) +
        SUN_FIELD_AMPLITUDE_1 *
        integrate_attenuated_harmonic(
            SUN_FIELD_K1,
            SUN_FIELD_SPEED_1,
            SUN_FIELD_PHASE_1,
            surface_a,
            surface_b,
            extinction,
            transmittance,
            optical_path,
            visual_time) +
        SUN_FIELD_AMPLITUDE_2 *
        integrate_attenuated_harmonic(
            SUN_FIELD_K2,
            SUN_FIELD_SPEED_2,
            SUN_FIELD_PHASE_2,
            surface_a,
            surface_b,
            extinction,
            transmittance,
            optical_path,
            visual_time);


    // Keep the existing UW-4B final (1-T) composition by converting the
    // attenuated integral into an effective scatter colour. Constant fields
    // remain constant; spatial harmonics receive exact Beer weighting.
    vec3 shaft_field_average =
        shaft_field_integral /
        max(
            shaft_beer_integral,
            vec3(0.0001));


    float sun_projection_visibility =
        smoothstep(
            SUN_PROJECTION_FADE_START,
            SUN_PROJECTION_FADE_END,
            -shaft_ray_direction.y);


    float shaft_angular_visibility =
        pow(
            max(
                dot(
                    view_ray_world,
                    direction_toward_sun_world),
                0.0),
            1.5);


    vec3 shaft_scatter =
        params.scatter_and_sun_radiance_b.xyz *
        primary_sun_radiance *
        camera_sun_transmittance *
        SUN_SHAFT_STRENGTH *
        shaft_field_average *
        sun_projection_visibility *
        shaft_angular_visibility;


    // Reuse the incident sunlight path already derived from camera depth.
    // This keeps shallow water open while lowering the asymptotic ambient
    // scatter at depth; the angular lobe below prevents a flat blue limit.
    float incident_sun_luminance =
        dot(
            camera_sun_transmittance,
            vec3(0.2126, 0.7152, 0.0722));


    float ambient_scatter_retention =
        mix(
            DEEP_SCATTER_AMBIENT_FLOOR,
            1.0,
            sqrt(
                clamp(
                    incident_sun_luminance,
                    0.0,
                    1.0)));


    vec3 effective_scatter =
        params.scatter_and_sun_radiance_b.xyz *
            frame.ambient_lighting.xyz *
            ambient_scatter_retention +
        directional_scatter +
        shaft_scatter;


    vec3 result =
        attenuated_scene_color * transmittance +
        effective_scatter * (vec3(1.0) - transmittance);


    imageStore(
        color_image,
        pixel,
        vec4(result, scene_color.a));
}
