shader_type spatial;

render_mode
	cull_disabled,
	depth_draw_opaque,
	world_vertex_coords;


/*
===============================================================================
	LOW POLY GRASS WIND — TWO-SIDED LIGHTING

	Main change versus previous shader:
	- custom light() with abs(dot(N, L))
	- both sides of the blade receive lighting
	- optional backface brightness control

	INSTANCE_CUSTOM:
		R = random phase 0..1
		G = wind multiplier 0..1
		B = color variation 0..1
		A = reserved

	UV convention:
		default assumes V=1 at ROOT and V=0 at TOP.
		If your mesh is opposite, enable invert_uv_y.
===============================================================================
*/


// ============================================================================
// APPEARANCE
// ============================================================================

group_uniforms Appearance;

uniform sampler2D albedo_texture :
	source_color,
	filter_linear_mipmap_anisotropic;

uniform vec4 base_color : source_color =
	vec4(0.62, 0.82, 0.30, 1.0);

uniform vec4 dry_color : source_color =
	vec4(0.76, 0.74, 0.38, 1.0);

uniform float color_variation :
	hint_range(0.0, 1.0) = 0.10;

uniform float brightness :
	hint_range(0.2, 3.0) = 1.15;

uniform float roughness :
	hint_range(0.0, 1.0) = 0.78;

uniform float specular :
	hint_range(0.0, 1.0) = 0.08;

uniform float alpha_scissor :
	hint_range(0.0, 1.0) = 0.45;

group_uniforms;


// ============================================================================
// WIND
// ============================================================================

group_uniforms Wind;


// World-space XZ direction.
uniform vec2 wind_direction =
	vec2(1.0, 0.35);


// Main displacement in metres.
uniform float wind_strength :
	hint_range(0.0, 2.0) = 0.18;


// Animation speed.
uniform float wind_speed :
	hint_range(0.0, 10.0) = 1.4;


// Size of wind structures in world space.
// Smaller = broader waves.
uniform float wind_spatial_frequency :
	hint_range(0.001, 2.0) = 0.08;


// Secondary irregular motion.
uniform float gust_strength :
	hint_range(0.0, 1.0) = 0.30;


// Controls how tightly the root stays fixed.
// 1 = linear
// 2-3 = more rigid root
uniform float bend_exponent :
	hint_range(0.5, 6.0) = 2.2;


// Use this if your mesh has:
// V=0 at root
// V=1 at top
uniform bool invert_uv_y = false;

group_uniforms;


// ============================================================================
// SMALL MOTION
// ============================================================================

group_uniforms Flutter;

uniform float flutter_strength :
	hint_range(0.0, 0.5) = 0.035;

uniform float flutter_speed :
	hint_range(0.0, 20.0) = 4.5;

group_uniforms;


// ============================================================================
// TWO-SIDED LIGHTING
// ============================================================================

group_uniforms Two_Sided_Lighting;


// Makes the "inside"/back side not feel dead.
uniform float backface_brightness :
	hint_range(0.5, 3.0) = 1.15;


// Soft constant lift for very dark cases.
uniform float ambient_lift :
	hint_range(0.0, 1.0) = 0.08;


// Makes blade tips catch a bit more light.
uniform float top_light_boost :
	hint_range(0.0, 1.0) = 0.12;

group_uniforms;


// ============================================================================

varying float grass_random;
varying float tip_mask;
varying float face_sign;


// ============================================================================
// VERTEX
// ============================================================================

void vertex()
{
	// MultiMesh supplies these.
	float phase_random =
		INSTANCE_CUSTOM.r;

	float instance_wind =
		mix(
			0.65,
			1.25,
			INSTANCE_CUSTOM.g
		);

	grass_random =
		INSTANCE_CUSTOM.b;


	// ------------------------------------------------------------
	// Root -> top mask
	// ------------------------------------------------------------

	float height_weight =
		invert_uv_y
			? UV.y
			: 1.0 - UV.y;

	height_weight =
		pow(
			clamp(
				height_weight,
				0.0,
				1.0
			),
			bend_exponent
		);

	tip_mask = height_weight;


	// ------------------------------------------------------------
	// World wind direction
	// ------------------------------------------------------------

	vec2 wind_dir =
		wind_direction;

	float wind_len =
		length(wind_dir);

	if (wind_len < 0.0001)
	{
		wind_dir =
			vec2(1.0, 0.0);
	}
	else
	{
		wind_dir /=
			wind_len;
	}


	vec2 side_dir =
		vec2(
			-wind_dir.y,
			wind_dir.x
		);


	// ------------------------------------------------------------
	// Large-scale coherent wave
	// ------------------------------------------------------------

	float spatial_phase =
		dot(
			VERTEX.xz,
			vec2(
				0.73,
				1.17
			)
		) *
		wind_spatial_frequency;


	float phase =
		TIME *
			wind_speed +
		spatial_phase +
		phase_random *
			6.28318530718;


	float main_wave =
		sin(phase);


	float gust =
		sin(
			phase * 1.73 +
			1.91
		);


	float wind_signal =
		main_wave +
		gust *
			gust_strength;


	// ------------------------------------------------------------
	// Small flutter
	// ------------------------------------------------------------

	float flutter =
		sin(
			TIME *
				flutter_speed +
			spatial_phase *
				2.7 +
			phase_random *
				11.3
		);


	// ------------------------------------------------------------
	// Apply world-space displacement
	// ------------------------------------------------------------

	float bend =
		wind_strength *
		instance_wind *
		height_weight;


	VERTEX.x +=
		wind_dir.x *
		wind_signal *
		bend;

	VERTEX.z +=
		wind_dir.y *
		wind_signal *
		bend;


	VERTEX.x +=
		side_dir.x *
		flutter *
		flutter_strength *
		height_weight;

	VERTEX.z +=
		side_dir.y *
		flutter *
		flutter_strength *
		height_weight;


	// Small vertical compression.
	VERTEX.y -=
		abs(wind_signal) *
		bend *
		0.08;
}


// ============================================================================
// FRAGMENT
// ============================================================================

void fragment()
{
	vec4 tex =
		texture(
			albedo_texture,
			UV
		);


	vec3 instance_color =
		mix(
			base_color.rgb,
			dry_color.rgb,
			grass_random *
				color_variation
		);


	vec3 color =
		tex.rgb *
		instance_color *
		brightness;


	// Slightly lighter toward the top.
	color *=
		1.0 +
		tip_mask *
		top_light_boost;


	ALBEDO =
		clamp(
			color,
			vec3(0.0),
			vec3(1.0)
		);


	ROUGHNESS =
		roughness;

	SPECULAR =
		specular;


	ALPHA =
		tex.a *
		base_color.a;

	ALPHA_SCISSOR_THRESHOLD =
		alpha_scissor;


	// Save whether current visible face is front/back.
	face_sign =
		FRONT_FACING ? 1.0 : -1.0;


	// For shading continuity we still orient normal
	// toward the visible side.
	vec3 face_normal = NORMAL;

	if (!FRONT_FACING)
	{
		face_normal = -face_normal;
	}

	NORMAL =
		normalize(face_normal);
}


// ============================================================================
// LIGHT
// ============================================================================

void light()
{
	vec3 N = normalize(NORMAL);
	vec3 L = normalize(LIGHT);
	vec3 V = normalize(VIEW);
	vec3 H = normalize(L + V);


	// Core trick:
	// use ABS so both sides react to light.
	float NdotL =
		abs(
			dot(N, L)
		);


	// Slightly softer foliage-like wrap.
	NdotL =
		smoothstep(
			0.0,
			1.0,
			NdotL
		);


	// Small lift so dark back areas don't die completely.
	float diffuse_term =
		max(
			NdotL,
			ambient_lift
		);


	// If currently visible face is backface,
	// optionally boost it a bit.
	float side_boost =
		(face_sign < 0.0)
			? backface_brightness
			: 1.0;


	// Simple two-sided specular.
	float NdotH =
		abs(
			dot(N, H)
		);

	float shininess =
		mix(
			64.0,
			8.0,
			roughness
		);

	float spec_term =
		pow(
			max(NdotH, 0.0),
			shininess
		) *
		specular;


	DIFFUSE_LIGHT +=
		ALBEDO *
		LIGHT_COLOR.rgb *
		diffuse_term *
		ATTENUATION *
		side_boost;


	SPECULAR_LIGHT +=
		LIGHT_COLOR.rgb *
		spec_term *
		ATTENUATION *
		side_boost;
}