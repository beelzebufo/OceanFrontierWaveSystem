shader_type spatial;

#include "res://Ocean/Shaders/Rendering/ocean_planar_reflection_clip.gdshaderinc"

uniform vec4 material_albedo : source_color = vec4(0.12, 0.18, 0.24, 1.0);
uniform float material_metallic : hint_range(0.0, 1.0) = 0.12;
uniform float material_roughness : hint_range(0.0, 1.0) = 0.48;

uniform bool reflection_clip_enabled = true;
uniform float reflection_clip_y = 0.0;
uniform float reflection_clip_bias = 0.0;

void fragment()
{
	if (ocean_should_clip_planar_reflection(
		reflection_clip_enabled,
		reflection_clip_y,
		reflection_clip_bias,
		VERTEX,
		INV_VIEW_MATRIX,
		CAMERA_VISIBLE_LAYERS))
	{
		discard;
	}

	ALBEDO = material_albedo.rgb;
	METALLIC = material_metallic;
	ROUGHNESS = material_roughness;
}
