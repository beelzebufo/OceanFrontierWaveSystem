#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform image2D color_image;

layout(set = 0, binding = 1) uniform sampler2D depth_texture;

layout(push_constant, std430) uniform Params
{
    vec2 raster_size;
    vec2 primary_sun_radiance_rg;
    mat4 inverse_projection;
    vec4 extinction_and_max_path;
    vec4 scatter_and_sun_radiance_b;
    vec4 sun_ray_direction_view_and_incident_path;
}
params;

const float APPROXIMATE_DIRECT_SUN_FRACTION =
    0.25;

const float DIRECTIONAL_SCATTER_STRENGTH =
    0.35;

const float DIRECTIONAL_SCATTER_FALLOFF =
    5.0;


void main()
{
    ivec2 pixel =
        ivec2(
            gl_GlobalInvocationID.xy);


    ivec2 size =
        ivec2(
            params.raster_size);


    if (pixel.x >= size.x ||
        pixel.y >= size.y)
    {
        return;
    }


    vec4 scene_color =
        imageLoad(
            color_image,
            pixel);


    // Forward+/Mobile reverse-Z clear depth is zero. Far/invalid pixels use
    // the same maximum optical path as valid reconstructed geometry.
    float depth =
        texelFetch(
            depth_texture,
            pixel,
            0).r;


    bool has_scene_geometry =
        depth > 0.000001;


    vec2 screen_uv =
        (
            vec2(
                pixel) +
            vec2(0.5)
        ) /
        params.raster_size;


    // A near-far epsilon gives clear-depth pixels a stable view direction
    // without treating that reconstructed point as opaque geometry.
    vec4 view_h =
        params.inverse_projection *
        vec4(
            screen_uv *
                2.0 -
                1.0,
            max(
                depth,
                0.000001),
            1.0);


    vec3 view_position =
        vec3(
            0.0,
            0.0,
            -1.0);


    bool has_finite_view_position =
        abs(
            view_h.w) > 0.000001;


    if (has_finite_view_position)
    {
        view_position =
            view_h.xyz /
            view_h.w;


        has_finite_view_position =
            !any(
                isnan(
                    view_position)) &&
            !any(
                isinf(
                    view_position));
    }


    float optical_path =
        params.extinction_and_max_path.w;


    if (has_scene_geometry &&
        has_finite_view_position)
    {
        optical_path =
            clamp(
                length(
                    view_position),
                0.0,
                params.extinction_and_max_path.w);
    }


    vec3 transmittance =
        exp(
            -params.extinction_and_max_path.xyz *
            optical_path);


    vec3 primary_sun_radiance =
        vec3(
            params.primary_sun_radiance_rg,
            params.scatter_and_sun_radiance_b.w);


    float primary_sun_energy =
        max(
            max(
                primary_sun_radiance.r,
                primary_sun_radiance.g),
            primary_sun_radiance.b);


    vec3 sun_transmittance =
        exp(
            -params.extinction_and_max_path.xyz *
            params.sun_ray_direction_view_and_incident_path.w);


    vec3 attenuated_scene_color =
        scene_color.rgb;


    // The depth query is the canonical surface height at the camera, not
    // camera-to-scene depth. Until UW-4A2 samples AWF at every receiver, it
    // is a bounded whole-frame approximation for incident sunlight only.
    if (has_scene_geometry &&
        primary_sun_energy > 0.000001)
    {
        attenuated_scene_color *=
            mix(
                vec3(1.0),
                sun_transmittance,
                APPROXIMATE_DIRECT_SUN_FRACTION);
    }


    vec3 view_ray =
        normalize(
            view_position);


    vec3 direction_toward_sun =
        normalize(
            -params.sun_ray_direction_view_and_incident_path.xyz);


    float forward_scatter =
        pow(
            max(
                dot(
                    view_ray,
                    direction_toward_sun),
                0.0),
            DIRECTIONAL_SCATTER_FALLOFF);


    vec3 directional_scatter =
        params.scatter_and_sun_radiance_b.xyz *
        primary_sun_radiance *
        sun_transmittance *
        DIRECTIONAL_SCATTER_STRENGTH *
        forward_scatter;


    vec3 effective_scatter =
        params.scatter_and_sun_radiance_b.xyz +
        directional_scatter;


    vec3 result =
        attenuated_scene_color *
            transmittance +
        effective_scatter *
            (
                vec3(1.0) -
                transmittance
            );


    imageStore(
        color_image,
        pixel,
        vec4(
            result,
            scene_color.a));
}
