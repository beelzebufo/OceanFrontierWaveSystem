#[compute]
#version 450

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

// Final AnimatedWaveField only. The sampler matches the render material's
// filter_linear/repeat_disable sampling contract.
layout(set = 0, binding = 0) uniform sampler2DArray u_animated_wave_field;

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

// Same 32-byte std430 elements uploaded by AnimatedWaveLodGpuBuffer.
layout(std430, set = 0, binding = 1) readonly buffer LodBuffer
{
	AnimatedWaveLodParams lods[];
} u_lod_data;

// x/y = world X/Z, z = minimum texel width, w = reserved.
layout(std430, set = 0, binding = 2) readonly buffer QueryBuffer
{
	vec4 queries[];
} u_queries;

// xyz = final displacement, w = 1 for valid / 0 for outside the field.
layout(std430, set = 0, binding = 3) writeonly buffer ResultBuffer
{
	vec4 results[];
} u_results;

layout(push_constant, std430) uniform PushConstants
{
	uint count;
	uint lod_count;
	vec2 focus_xz;
} pc;

// In nested rendering, the smallest square around focus containing the
// undeformed point chooses the physical slice. A requested minimum texel
// width may force a coarser slice.
int choose_lod(vec2 world_xz, float min_texel_width)
{
	int coarsest_covering_lod = -1;
	vec2 offset = abs(world_xz - pc.focus_xz);
	for (uint lod = 0u; lod < pc.lod_count; ++lod)
	{
		AnimatedWaveLodParams slice = u_lod_data.lods[lod];
		float world_size = 4.0 * slice.scale;
		if (max(offset.x, offset.y) <= 0.5 * world_size)
		{
			coarsest_covering_lod = int(lod);
			if (slice.texel_width >= min_texel_width)
				return int(lod);
		}
	}
	return coarsest_covering_lod;
}

vec3 sample_lod(vec2 world_xz, int lod)
{
	AnimatedWaveLodParams slice = u_lod_data.lods[lod];
	vec2 uv = (world_xz - slice.center_xz) / (4.0 * slice.scale) + vec2(0.5);
	return textureLod(u_animated_wave_field, vec3(uv, float(lod)), 0.0).xyz;
}

vec3 sample_surface(vec2 world_xz, float min_texel_width, out bool valid)
{
	int lod = choose_lod(world_xz, min_texel_width);
	valid = lod >= 0;
	if (!valid)
		return vec3(0.0);

	vec3 current_disp = sample_lod(world_xz, lod);
	if (uint(lod + 1) >= pc.lod_count)
		return current_disp;

	vec3 next_disp = sample_lod(world_xz, lod + 1);
	float scale = u_lod_data.lods[lod].scale;
	vec2 offset = abs(world_xz - pc.focus_xz);
	float raw_alpha = max(offset.x, offset.y) / scale - 1.0;
	float alpha = clamp((raw_alpha - 0.15) / (0.85 - 0.15), 0.0, 1.0);
	return mix(current_disp, next_disp, alpha);
}

void main()
{
	uint index = gl_GlobalInvocationID.x;
	if (index >= pc.count)
		return;

	vec4 query = u_queries.queries[index];
	vec2 target_xz = query.xy;
	vec2 undisplaced = target_xz;
	bool valid = false;
	for (int iteration = 0; iteration < 4; ++iteration)
	{
		vec3 displacement = sample_surface(undisplaced, query.z, valid);
		if (!valid)
		{
			u_results.results[index] = vec4(0.0);
			return;
		}
		vec2 error = undisplaced + displacement.xz - target_xz;
		undisplaced -= error;
	}

	vec3 displacement = sample_surface(undisplaced, query.z, valid);
	u_results.results[index] = valid ? vec4(displacement, 1.0) : vec4(0.0);
}
