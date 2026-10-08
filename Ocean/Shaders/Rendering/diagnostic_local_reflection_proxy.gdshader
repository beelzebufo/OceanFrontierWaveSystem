shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_always;

uniform vec4 material_color : source_color = vec4(1.0);
uniform float mean_sea_level_y = 0.0;

void fragment()
{
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
