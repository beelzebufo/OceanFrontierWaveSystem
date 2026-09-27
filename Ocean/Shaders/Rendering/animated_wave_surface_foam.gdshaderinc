uniform sampler2DArray ocean_foam_field :
    filter_linear,
    repeat_disable;

uniform bool has_ocean_foam = false;

uniform sampler2D foam_pattern_texture :
    repeat_enable,
    filter_linear_mipmap_anisotropic;

uniform bool has_foam_pattern_texture = false;
uniform float foam_pattern_scale = 10.0;
uniform float foam_pattern_feather = 0.4;
uniform vec4 foam_tint : source_color = vec4(1.0);


// Crest cascadeData._scale is the cascade half-width: one quarter of our
// full texture world size. WhiteFoamTexture divides that value by 25, so the
// equivalent pattern world scale is FoamScale * lod_world_size / 100.
const float FOAM_CREST_CASCADE_SCALE_CONVERSION = 0.01;


float sample_ocean_foam_lod(
    vec2 world_xz,
    vec2 center_xz,
    float world_size,
    float layer)
{
    vec2 uv =
        world_to_lod_uv(
            world_xz,
            center_xz,
            world_size);


    if (any(lessThan(uv, vec2(0.0))) ||
        any(greaterThan(uv, vec2(1.0))))
    {
        return 0.0;
    }


    return textureLod(
        ocean_foam_field,
        vec3(
            uv,
            layer),
        0.0).r;
}


float sample_render_foam(
    vec2 world_xz,
    float lod_alpha)
{
    float current_foam =
        sample_ocean_foam_lod(
            world_xz,
            lod_center_xz,
            lod_world_size,
            selected_lod);


    if (!has_next_lod)
    {
        return current_foam;
    }


    float next_foam =
        sample_ocean_foam_lod(
            world_xz,
            next_lod_center_xz,
            next_lod_world_size,
            next_lod);


    return mix(
        current_foam,
        next_foam,
        clamp(
            lod_alpha,
            0.0,
            1.0));
}


float sample_visible_foam(
    vec2 world_xz,
    float lod_alpha,
    float foam_amount)
{
    foam_amount =
        clamp(
            foam_amount,
            0.0,
            1.0);


    if (!has_foam_pattern_texture)
    {
        return foam_amount;
    }


    float current_pattern_world_scale =
        max(
            foam_pattern_scale *
                lod_world_size *
                FOAM_CREST_CASCADE_SCALE_CONVERSION,
            0.0001);


    vec2 drift =
        vec2(
            visual_time *
            0.1);


    vec2 pattern_position =
        1.25 *
            world_xz +
        drift;


    float current_pattern =
        texture(
            foam_pattern_texture,
            pattern_position /
                current_pattern_world_scale).r;


    float next_pattern =
        texture(
            foam_pattern_texture,
            pattern_position /
                (current_pattern_world_scale * 2.0)).r;


    float pattern =
        mix(
            current_pattern,
            next_pattern,
            clamp(
                lod_alpha,
                0.0,
                1.0));


    float threshold =
        clamp(
            1.0 -
                foam_amount,
            0.0,
            1.0);


    float feather =
        max(
            foam_pattern_feather,
            0.001);


    return smoothstep(
        threshold,
        threshold +
            feather,
        pattern);
}
