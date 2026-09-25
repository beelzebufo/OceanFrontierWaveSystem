#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform image2D color_image;

layout(set = 0, binding = 1) uniform sampler2D depth_texture;

layout(push_constant, std430) uniform Params
{
    vec2 raster_size;
    vec2 reserved;
    mat4 inverse_projection;
}
params;

const vec3 WATER_EXTINCTION =
    vec3(
        0.18,
        0.07,
        0.025);

const vec3 WATER_SCATTER =
    vec3(
        0.015,
        0.12,
        0.18);

const float MAX_OPTICAL_PATH_METRES =
    100.0;


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


    float optical_path =
        MAX_OPTICAL_PATH_METRES;


    if (depth > 0.000001)
    {
        vec2 screen_uv =
            (
                vec2(
                    pixel) +
                vec2(0.5)
            ) /
            params.raster_size;


        vec4 view_h =
            params.inverse_projection *
            vec4(
                screen_uv *
                    2.0 -
                    1.0,
                depth,
                1.0);


        if (abs(
                view_h.w) > 0.000001)
        {
            vec3 view_position =
                view_h.xyz /
                view_h.w;


            if (!any(
                    isnan(
                        view_position)) &&
                !any(
                    isinf(
                        view_position)))
            {
                optical_path =
                    clamp(
                        length(
                            view_position),
                        0.0,
                        MAX_OPTICAL_PATH_METRES);
            }
        }
    }


    vec3 transmittance =
        exp(
            -WATER_EXTINCTION *
            optical_path);


    vec3 result =
        scene_color.rgb *
            transmittance +
        WATER_SCATTER *
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
