shader_type spatial;

render_mode
	world_vertex_coords,
	blend_mix,
	depth_draw_opaque,
	cull_back,
	diffuse_lambert,
	specular_schlick_ggx;


/*
===============================================================================
	LOW POLY ROCK DETAIL

	- world-space triplanar mapping
	- no mesh UV required
	- 3 diffuse samples
	- optional 3 normal samples
	- optional 3 cavity/height samples
	- preserves base colour instead of multiplying rock into darkness
	- cheap top weathering
	- MultiMesh per-instance colour variation

	INSTANCE_CUSTOM:
		B = random colour variation
===============================================================================
*/


// ============================================================================
// BASE
// ============================================================================

group_uniforms Base;

uniform vec4 base_color : source_color =
	vec4(0.48, 0.47, 0.43, 1.0);

uniform float brightness :
	hint_range(0.2, 3.0) = 1.05;

uniform float roughness :
	hint_range(0.0, 1.0) = 0.78;

uniform float specular :
	hint_range(0.0, 1.0) = 0.10;


// Variation between MultiMesh instances.
uniform float instance_color_variation :
	hint_range(0.0, 1.0) = 0.12;

uniform vec4 variation_color : source_color =
	vec4(0.72, 0.76, 0.66, 1.0);

group_uniforms;


// ============================================================================
// ROCK DIFFUSE
// ============================================================================

group_uniforms Rock_Texture;

uniform bool use_rock_texture = true;

uniform sampler2D rock_texture :
	source_color,
	filter_linear_mipmap_anisotropic,
	repeat_enable;


// World-space frequency.
// 1.0 means roughly one texture repeat per metre.
uniform float texture_scale :
	hint_range(0.01, 8.0, 0.01) = 1.0;


// How strongly photographic texture modifies base colour.
uniform float texture_amount :
	hint_range(0.0, 1.0) = 0.70;


// Controls brightness variation from texture.
// Kept below 1 to prevent dark diffuse from swallowing lighting.
uniform float texture_contrast :
	hint_range(0.0, 2.0) = 0.60;


// How much original texture hue affects the rock.
uniform float texture_color_strength :
	hint_range(0.0, 1.0) = 0.45;


// Higher = harder transition between projection axes.
uniform float projection_sharpness :
	hint_range(1.0, 16.0) = 4.0;

group_uniforms;


// ============================================================================
// NORMAL
// ============================================================================

group_uniforms Rock_Normal;

uniform bool use_rock_normal = true;

uniform sampler2D rock_normal_texture :
	hint_normal,
	filter_linear_mipmap_anisotropic,
	repeat_enable;

uniform float normal_strength :
	hint_range(0.0, 1.5) = 0.30;

group_uniforms;


// ============================================================================
// HEIGHT / CAVITY
// ============================================================================

group_uniforms Rock_Cavity;

uniform bool use_rock_cavity = false;

uniform sampler2D rock_height_texture :
	filter_linear_mipmap_anisotropic,
	repeat_enable;


// Dark values in displacement become recesses.
uniform float cavity_low :
	hint_range(0.0, 1.0) = 0.20;

uniform float cavity_high :
	hint_range(0.0, 1.0) = 0.55;

uniform float cavity_strength :
	hint_range(0.0, 0.8) = 0.18;

uniform float cavity_roughness :
	hint_range(0.0, 0.4) = 0.06;


// Tiny highlight on raised areas.
uniform float height_highlight :
	hint_range(0.0, 0.4) = 0.035;

group_uniforms;


// ============================================================================
// WEATHERING
// ============================================================================

group_uniforms Weathering;


// Cheap material variation on upward-facing faces.
uniform vec4 top_tint : source_color =
	vec4(0.78, 0.80, 0.72, 1.0);

uniform float top_tint_amount :
	hint_range(0.0, 1.0) = 0.16;

uniform float top_tint_power :
	hint_range(0.5, 16.0) = 4.0;

group_uniforms;


// ============================================================================
// DETAIL FADE
// ============================================================================

group_uniforms Detail_Fade;


// Normal + cavity disappear with distance.
// Diffuse remains.
uniform float detail_fade_start :
	hint_range(0.0, 1000.0) = 35.0;

uniform float detail_fade_end :
	hint_range(1.0, 2000.0) = 100.0;

group_uniforms;


// ============================================================================

varying vec3 world_position;
varying float rock_random;


// ============================================================================
// HELPERS
// ============================================================================

vec3 decode_normal(vec3 encoded)
{
	return normalize(
		encoded * 2.0 - 1.0
	);
}


float sign_nonzero(float value)
{
	return value >= 0.0 ? 1.0 : -1.0;
}


// X-facing projection.
// UV = Z/Y.
vec3 normal_from_x(
	vec3 tangent_normal,
	float axis_sign
)
{
	vec3 tangent =
		vec3(
			0.0,
			0.0,
			-axis_sign
		);

	vec3 bitangent =
		vec3(
			0.0,
			1.0,
			0.0
		);

	vec3 plane_normal =
		vec3(
			axis_sign,
			0.0,
			0.0
		);

	return normalize(
		tangent * tangent_normal.x +
		bitangent * tangent_normal.y +
		plane_normal * tangent_normal.z
	);
}


// Y-facing projection.
// UV = X/Z.
vec3 normal_from_y(
	vec3 tangent_normal,
	float axis_sign
)
{
	vec3 tangent =
		vec3(
			1.0,
			0.0,
			0.0
		);

	vec3 bitangent =
		vec3(
			0.0,
			0.0,
			-axis_sign
		);

	vec3 plane_normal =
		vec3(
			0.0,
			axis_sign,
			0.0
		);

	return normalize(
		tangent * tangent_normal.x +
		bitangent * tangent_normal.y +
		plane_normal * tangent_normal.z
	);
}


// Z-facing projection.
// UV = X/Y.
vec3 normal_from_z(
	vec3 tangent_normal,
	float axis_sign
)
{
	vec3 tangent =
		vec3(
			axis_sign,
			0.0,
			0.0
		);

	vec3 bitangent =
		vec3(
			0.0,
			1.0,
			0.0
		);

	vec3 plane_normal =
		vec3(
			0.0,
			0.0,
			axis_sign
		);

	return normalize(
		tangent * tangent_normal.x +
		bitangent * tangent_normal.y +
		plane_normal * tangent_normal.z
	);
}


// ============================================================================
// VERTEX
// ============================================================================

void vertex()
{
	// world_vertex_coords means VERTEX is already world-space here.
	world_position = VERTEX;

	// IslandScatterLayer already fills this.
	rock_random =
		INSTANCE_CUSTOM.b;
}


// ============================================================================
// FRAGMENT
// ============================================================================

void fragment()
{
	// Mesh normal arrives in view space in fragment().
	vec3 base_normal_view =
		normalize(NORMAL);

	vec3 base_normal_world =
		normalize(
			mat3(INV_VIEW_MATRIX) *
			base_normal_view
		);


	// ------------------------------------------------------------------------
	// INSTANCE COLOUR
	// ------------------------------------------------------------------------

	vec3 color =
		base_color.rgb;


	vec3 random_tint =
		mix(
			vec3(1.0),
			variation_color.rgb,
			rock_random
		);


	color *=
		mix(
			vec3(1.0),
			random_tint,
			instance_color_variation
		);


	// ------------------------------------------------------------------------
	// TRIPLANAR WEIGHTS
	// ------------------------------------------------------------------------

	vec3 weights =
		pow(
			max(
				abs(base_normal_world),
				vec3(0.0001)
			),
			vec3(projection_sharpness)
		);

	weights /=
		max(
			weights.x +
			weights.y +
			weights.z,
			0.0001
		);


	float sign_x =
		sign_nonzero(base_normal_world.x);

	float sign_y =
		sign_nonzero(base_normal_world.y);

	float sign_z =
		sign_nonzero(base_normal_world.z);


	// ------------------------------------------------------------------------
	// WORLD UVs
	// ------------------------------------------------------------------------

	vec2 uv_x =
		vec2(
			-world_position.z * sign_x,
			world_position.y
		) *
		texture_scale;


	vec2 uv_y =
		vec2(
			world_position.x,
			-world_position.z * sign_y
		) *
		texture_scale;


	vec2 uv_z =
		vec2(
			world_position.x * sign_z,
			world_position.y
		) *
		texture_scale;


	// Derivatives for stable mip selection.
	vec3 world_dx =
		dFdx(world_position);

	vec3 world_dy =
		dFdy(world_position);


	vec2 dx_x =
		vec2(
			-world_dx.z * sign_x,
			world_dx.y
		) *
		texture_scale;

	vec2 dy_x =
		vec2(
			-world_dy.z * sign_x,
			world_dy.y
		) *
		texture_scale;


	vec2 dx_y =
		vec2(
			world_dx.x,
			-world_dx.z * sign_y
		) *
		texture_scale;

	vec2 dy_y =
		vec2(
			world_dy.x,
			-world_dy.z * sign_y
		) *
		texture_scale;


	vec2 dx_z =
		vec2(
			world_dx.x * sign_z,
			world_dx.y
		) *
		texture_scale;

	vec2 dy_z =
		vec2(
			world_dy.x * sign_z,
			world_dy.y
		) *
		texture_scale;


	// ------------------------------------------------------------------------
	// DIFFUSE
	// ------------------------------------------------------------------------

	if (use_rock_texture)
	{
		vec3 rock_x =
			textureGrad(
				rock_texture,
				uv_x,
				dx_x,
				dy_x
			).rgb;


		vec3 rock_y =
			textureGrad(
				rock_texture,
				uv_y,
				dx_y,
				dy_y
			).rgb;


		vec3 rock_z =
			textureGrad(
				rock_texture,
				uv_z,
				dx_z,
				dy_z
			).rgb;


		vec3 rock =
			rock_x * weights.x +
			rock_y * weights.y +
			rock_z * weights.z;


		// ------------------------------------------------------------
		// Prevent dark photographic diffuse from killing base colour.
		// ------------------------------------------------------------

		float rock_luma =
			dot(
				rock,
				vec3(
					0.2126,
					0.7152,
					0.0722
				)
			);


		vec3 rock_chroma =
			rock /
			max(
				rock_luma,
				0.08
			);


		rock_chroma =
			mix(
				vec3(1.0),
				clamp(
					rock_chroma,
					vec3(0.65),
					vec3(1.45)
				),
				texture_color_strength
			);


		float rock_brightness =
			mix(
				1.0,
				mix(
					0.78,
					1.22,
					rock_luma
				),
				texture_contrast
			);


		vec3 rock_detail =
			rock_chroma *
			rock_brightness;


		color *=
			mix(
				vec3(1.0),
				rock_detail,
				texture_amount
			);
	}


	// ------------------------------------------------------------------------
	// CHEAP TOP WEATHERING
	// ------------------------------------------------------------------------

	float top_mask =
		pow(
			max(
				base_normal_world.y,
				0.0
			),
			top_tint_power
		);


	color *=
		mix(
			vec3(1.0),
			top_tint.rgb,
			top_mask *
				top_tint_amount
		);


	// ------------------------------------------------------------------------
	// DISTANCE DETAIL
	// ------------------------------------------------------------------------

	float safe_fade_end =
		max(
			detail_fade_end,
			detail_fade_start + 0.01
		);


	float distance_to_camera =
		length(
			CAMERA_POSITION_WORLD -
			world_position
		);


	float detail_weight =
		1.0 -
		smoothstep(
			detail_fade_start,
			safe_fade_end,
			distance_to_camera
		);


	// ------------------------------------------------------------------------
	// CAVITY
	// ------------------------------------------------------------------------

	float final_roughness =
		roughness;


	if (
		use_rock_cavity &&
		detail_weight > 0.001
	)
	{
		float height_x =
			textureGrad(
				rock_height_texture,
				uv_x,
				dx_x,
				dy_x
			).r;


		float height_y =
			textureGrad(
				rock_height_texture,
				uv_y,
				dx_y,
				dy_y
			).r;


		float height_z =
			textureGrad(
				rock_height_texture,
				uv_z,
				dx_z,
				dy_z
			).r;


		float rock_height =
			height_x * weights.x +
			height_y * weights.y +
			height_z * weights.z;


		float cavity =
			1.0 -
			smoothstep(
				cavity_low,
				max(
					cavity_high,
					cavity_low + 0.001
				),
				rock_height
			);


		cavity *=
			cavity_strength *
			detail_weight;


		color *=
			1.0 - cavity;


		float high_mask =
			smoothstep(
				cavity_high,
				1.0,
				rock_height
			);


		color *=
			1.0 +
			high_mask *
			height_highlight *
			detail_weight;


		final_roughness +=
			cavity *
			cavity_roughness;
	}


	// ------------------------------------------------------------------------
	// NORMAL
	// ------------------------------------------------------------------------

	vec3 final_normal_world =
		base_normal_world;


	if (
		use_rock_normal &&
		detail_weight > 0.001
	)
	{
		vec3 tangent_normal_x =
			decode_normal(
				textureGrad(
					rock_normal_texture,
					uv_x,
					dx_x,
					dy_x
				).rgb
			);


		vec3 tangent_normal_y =
			decode_normal(
				textureGrad(
					rock_normal_texture,
					uv_y,
					dx_y,
					dy_y
				).rgb
			);


		vec3 tangent_normal_z =
			decode_normal(
				textureGrad(
					rock_normal_texture,
					uv_z,
					dx_z,
					dy_z
				).rgb
			);


		vec3 normal_x =
			normal_from_x(
				tangent_normal_x,
				sign_x
			);

		vec3 normal_y =
			normal_from_y(
				tangent_normal_y,
				sign_y
			);

		vec3 normal_z =
			normal_from_z(
				tangent_normal_z,
				sign_z
			);


		// Use linear abs(normal) weights here deliberately.
		// With a neutral normal map this reconstructs the original
		// geometric normal instead of snapping toward a projection axis.
		vec3 normal_weights =
			abs(base_normal_world);

		normal_weights /=
			max(
				normal_weights.x +
				normal_weights.y +
				normal_weights.z,
				0.0001
			);


		vec3 mapped_normal_world =
			normalize(
				normal_x * normal_weights.x +
				normal_y * normal_weights.y +
				normal_z * normal_weights.z
			);


		final_normal_world =
			normalize(
				mix(
					base_normal_world,
					mapped_normal_world,
					clamp(
						normal_strength *
						detail_weight,
						0.0,
						1.0
					)
				)
			);
	}


	// ------------------------------------------------------------------------
	// OUTPUT
	// ------------------------------------------------------------------------

	ALBEDO =
		clamp(
			color * brightness,
			vec3(0.0),
			vec3(1.0)
		);


	NORMAL =
		normalize(
			mat3(VIEW_MATRIX) *
			final_normal_world
		);


	ROUGHNESS =
		clamp(
			final_roughness,
			0.0,
			1.0
		);


	SPECULAR =
		specular;
}