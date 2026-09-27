#[compute]
#version 450

// World-space persistent reprojection follows Crest 4 UpdateFoam.compute at
// wave-harmonic/crest db0658ff0b2e93e4a9e28cc2867509658b0ecc00 (MIT).

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

// Source and target are separate storage images so two persistent textures
// can ping-pong.
layout(r16f, set = 0, binding = 0)
uniform readonly image2DArray u_previous_foam;

layout(r16f, set = 0, binding = 1)
uniform writeonly image2DArray u_current_foam;

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

layout(std430, set = 0, binding = 2)
readonly buffer CurrentLodBuffer
{
	AnimatedWaveLodParams lods[];
}
u_current_lod_data;

layout(std430, set = 0, binding = 3)
readonly buffer PreviousLodBuffer
{
	AnimatedWaveLodParams lods[];
}
u_previous_lod_data;

// Borrowed derived cache from the canonical AnimatedWaveField generation.
// RGB = geometric normal, A = raw horizontal displacement Jacobian.
layout(rgba16f, set = 0, binding = 4)
uniform readonly image2DArray u_animated_wave_derivatives;

// Borrowed canonical final displacement and coherent SeaFloorDepth field.
layout(rgba16f, set = 0, binding = 5)
uniform readonly image2DArray u_animated_wave_field;

layout(set = 0, binding = 6)
uniform sampler2DArray u_sea_floor_depth;

layout(push_constant, std430)
uniform PushConstants
{
	uint resolution;
	uint lod_count;
	int source_lod_offset;
	uint inject_spot;
	float delta_time;
	float fade_rate;
	float injection_radius;
	float injection_amount;
	vec2 injection_world_xz;
	float wave_foam_strength;
	float wave_foam_coverage;
	float shoreline_foam_max_depth;
	float shoreline_foam_strength;
	uint has_sea_floor_depth;
	uint padding;
}
pc;

vec2 uv_to_world(vec2 uv, AnimatedWaveLodParams lod)
{
	return
		(uv - vec2(0.5)) *
		(lod.texel_width * lod.texture_resolution) +
		lod.center_xz;
}

vec2 world_to_uv(vec2 world_xz, AnimatedWaveLodParams lod)
{
	return
		(world_xz - lod.center_xz) /
		(lod.texel_width * lod.texture_resolution) +
		vec2(0.5);
}

bool safely_inside(vec2 uv, AnimatedWaveLodParams lod)
{
	// Crest UpdateFoam safe footprint: keep one texel inside each edge so
	// bilinear taps never synthesize coverage beyond the previous window.
	float maximum_radius = 0.5 - lod.one_over_texture_resolution;
	vec2 radius = abs(uv - vec2(0.5));
	return max(radius.x, radius.y) <= maximum_radius;
}

float sample_bilinear_clamp(vec2 uv, int layer)
{
	ivec2 size = imageSize(u_previous_foam).xy;
	vec2 texel_position = uv * vec2(size) - vec2(0.5);
	ivec2 base = ivec2(floor(texel_position));
	vec2 blend = fract(texel_position);
	ivec2 maximum_coord = size - ivec2(1);

	ivec2 p00 = clamp(base, ivec2(0), maximum_coord);
	ivec2 p10 = clamp(base + ivec2(1, 0), ivec2(0), maximum_coord);
	ivec2 p01 = clamp(base + ivec2(0, 1), ivec2(0), maximum_coord);
	ivec2 p11 = clamp(base + ivec2(1, 1), ivec2(0), maximum_coord);

	float v00 = imageLoad(u_previous_foam, ivec3(p00, layer)).x;
	float v10 = imageLoad(u_previous_foam, ivec3(p10, layer)).x;
	float v01 = imageLoad(u_previous_foam, ivec3(p01, layer)).x;
	float v11 = imageLoad(u_previous_foam, ivec3(p11, layer)).x;

	return mix(mix(v00, v10, blend.x), mix(v01, v11, blend.x), blend.y);
}

void main()
{
	ivec3 coord = ivec3(gl_GlobalInvocationID);
	if (coord.x >= int(pc.resolution) ||
		coord.y >= int(pc.resolution) ||
		coord.z >= int(pc.lod_count))
	{
		return;
	}

	vec2 current_uv =
		(vec2(coord.xy) + vec2(0.5)) /
		float(pc.resolution);
	AnimatedWaveLodParams current_lod =
		u_current_lod_data.lods[coord.z];
	vec2 world_xz = uv_to_world(current_uv, current_lod);

	int source_lod = clamp(
		coord.z + pc.source_lod_offset,
		0,
		int(pc.lod_count) - 1);
	AnimatedWaveLodParams previous_lod =
		u_previous_lod_data.lods[source_lod];
	vec2 previous_uv = world_to_uv(world_xz, previous_lod);

	float foam = 0.0;
	if (safely_inside(previous_uv, previous_lod))
	{
		foam = sample_bilinear_clamp(previous_uv, source_lod);
	}
	else if (source_lod + 1 < int(pc.lod_count))
	{
		// Match Crest: if the corresponding previous slice has no safe
		// coverage, try exactly the next/coarser slice before returning zero.
		int coarser_lod = source_lod + 1;
		AnimatedWaveLodParams previous_coarser =
			u_previous_lod_data.lods[coarser_lod];
		vec2 coarser_uv = world_to_uv(world_xz, previous_coarser);

		if (safely_inside(coarser_uv, previous_coarser))
		{
			foam = sample_bilinear_clamp(coarser_uv, coarser_lod);
		}
	}

	foam *= max(0.0, 1.0 - pc.fade_rate * pc.delta_time);

	// Crest whitecap production, using the already-derived same-LOD raw
	// horizontal Jacobian determinant. The current FFT path has variance W=0,
	// so Crest's foamBase contribution is intentionally absent.
	if (pc.delta_time > 0.0 && pc.wave_foam_strength > 0.0)
	{
		float determinant = imageLoad(
			u_animated_wave_derivatives,
			coord).a;
		float generation = clamp(
			pc.wave_foam_coverage - determinant,
			0.0,
			1.0);

		foam +=
			5.0 *
			pc.delta_time *
			pc.wave_foam_strength *
			generation;
	}

	// Crest shoreline source. The canonical final displacement includes all
	// composed wave inputs. Depth is sampled at the horizontally displaced
	// surface position, using the same current spatial LOD generation.
	if (pc.delta_time > 0.0 &&
		pc.has_sea_floor_depth != 0u &&
		pc.shoreline_foam_strength > 0.0)
	{
		vec3 displacement = imageLoad(
			u_animated_wave_field,
			coord).xyz;

		vec2 displaced_world_xz =
			world_xz +
			displacement.xz;

		vec2 depth_uv = world_to_uv(
			displaced_world_xz,
			current_lod);

		float terrain_y = textureLod(
			u_sea_floor_depth,
			vec3(
				depth_uv,
				float(coord.z)),
			0.0).r;

		float signed_water_depth =
			displacement.y -
			terrain_y;

		float shoreline_factor = clamp(
			1.0 -
				signed_water_depth /
				pc.shoreline_foam_max_depth,
			0.0,
			1.0);

		foam +=
			pc.shoreline_foam_strength *
			pc.delta_time *
			shoreline_factor;
	}

	if (pc.inject_spot != 0u && pc.injection_radius > 0.0)
	{
		float spot = 1.0 - smoothstep(
			0.75 * pc.injection_radius,
			pc.injection_radius,
			length(world_xz - pc.injection_world_xz));
		foam = max(foam, spot * pc.injection_amount);
	}

	imageStore(u_current_foam, coord, vec4(clamp(foam, 0.0, 1.0)));
}
