#[compute]
#version 450

layout(
	local_size_x = 64,
	local_size_y = 1,
	local_size_z = 1
) in;


// Final canonical AnimatedWaveField.
//
// The sampler matches the renderer:
// linear filtering + clamp/repeat-disable.
layout(
	set = 0,
	binding = 0
)
uniform sampler2DArray u_animated_wave_field;


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


// Same 32-byte std430 elements uploaded by
// AnimatedWaveLodGpuBuffer.
layout(
	std430,
	set = 0,
	binding = 1
)
readonly buffer LodBuffer
{
	AnimatedWaveLodParams lods[];
}
u_lod_data;


// x/y = target world X/Z
// z   = minimum requested texel width
// w   = reserved
layout(
	std430,
	set = 0,
	binding = 2
)
readonly buffer QueryBuffer
{
	vec4 queries[];
}
u_queries;


// xyz = final displacement
// w   = 1 valid / 0 invalid
layout(
	std430,
	set = 0,
	binding = 3
)
writeonly buffer ResultBuffer
{
	vec4 results[];
}
u_results;


// 32 bytes.
//
// C# layout:
//
//  0 : uint  count
//  4 : uint  lod_count
//  8 : float focus_x
// 12 : float focus_z
// 16 : float lod_scale_alpha
// 20 : float padding
// 24 : float padding
// 28 : float padding
layout(
	push_constant,
	std430
)
uniform PushConstants
{
	uint count;

	uint lod_count;

	vec2 focus_xz;

	float lod_scale_alpha;

	float padding_0;

	float padding_1;

	float padding_2;
}
pc;


// Select the finest spatial LOD which:
//
// 1. physically covers the point;
// 2. satisfies the requested minimum texel width.
//
// If the requested minimum is coarser than every covering
// LOD, fall back to the coarsest covering LOD.
//
// Invalid only means the point lies outside the whole
// AnimatedWaveField.
int choose_lod(
	vec2 world_xz,
	float min_texel_width)
{
	int coarsest_covering_lod =
		-1;


	vec2 offset =
		abs(
			world_xz -
			pc.focus_xz);


	for (uint lod = 0u;
		 lod < pc.lod_count;
		 ++lod)
	{
		AnimatedWaveLodParams slice =
			u_lod_data.lods[lod];


		float world_size =
			4.0 *
			slice.scale;


		if (max(
				offset.x,
				offset.y) <=
			0.5 *
			world_size)
		{
			coarsest_covering_lod =
				int(lod);


			if (slice.texel_width >=
				min_texel_width)
			{
				return int(lod);
			}
		}
	}


	return
		coarsest_covering_lod;
}


// Exact AnimatedWaveField slice sampling.
vec3 sample_lod(
	vec2 world_xz,
	int lod)
{
	AnimatedWaveLodParams slice =
		u_lod_data.lods[lod];


	float world_size =
		4.0 *
		slice.scale;


	vec2 uv =
		(world_xz -
		 slice.center_xz) /
		world_size +
		vec2(0.5);


	return textureLod(
		u_animated_wave_field,
		vec3(
			uv,
			float(lod)),
		0.0
	).xyz;
}


// Samples the same cumulative spatial surface contract
// as the renderer.
//
// Spatial LOD blending:
//     current slice -> next slice
//
// View-height blending:
//     only LOD0 receives the Crest-like
//     lod_scale_alpha addition.
vec3 sample_surface(
	vec2 world_xz,
	float min_texel_width,
	out bool valid)
{
	int lod =
		choose_lod(
			world_xz,
			min_texel_width);


	valid =
		lod >= 0;


	if (!valid)
	{
		return
			vec3(0.0);
	}


	vec3 current_disp =
		sample_lod(
			world_xz,
			lod);


	if (uint(lod + 1) >=
		pc.lod_count)
	{
		return
			current_disp;
	}


	vec3 next_disp =
		sample_lod(
			world_xz,
			lod + 1);


	//
	// Existing spatial transition.
	//
	// scale = WorldSize / 4.
	//

	float scale =
		u_lod_data
			.lods[lod]
			.scale;


	vec2 offset =
		abs(
			world_xz -
			pc.focus_xz);


	float raw_alpha =
		max(
			offset.x,
			offset.y) /
		scale -
		1.0;


	float alpha =
		clamp(
			(raw_alpha - 0.15) /
			(0.85 - 0.15),
			0.0,
			1.0);


	//
	// Crest-like viewpoint altitude transition.
	//
	// As the viewpoint approaches the next x2 whole-ocean
	// scale, fade LOD0 into LOD1 before the discrete scale
	// switch occurs.
	//
	// Only LOD0 receives this term.
	//

	if (lod == 0)
	{
		alpha =
			min(
				alpha +
				pc.lod_scale_alpha,
				1.0);
	}


	return mix(
		current_disp,
		next_disp,
		alpha);
}


// Horizontal displacement inversion.
//
// Solve:
//
//     undisplaced + D.xz(undisplaced) = target
//
// using Crest-like four fixed-point iterations.
void main()
{
	uint index =
		gl_GlobalInvocationID.x;


	if (index >=
		pc.count)
	{
		return;
	}


	vec4 query =
		u_queries.queries[index];


	vec2 target_xz =
		query.xy;


	vec2 undisplaced =
		target_xz;


	bool valid =
		false;


	for (int iteration = 0;
		 iteration < 4;
		 ++iteration)
	{
		vec3 displacement =
			sample_surface(
				undisplaced,
				query.z,
				valid);


		if (!valid)
		{
			u_results.results[index] =
				vec4(0.0);

			return;
		}


		vec2 error =
			undisplaced +
			displacement.xz -
			target_xz;


		undisplaced -=
			error;
	}


	vec3 displacement =
		sample_surface(
			undisplaced,
			query.z,
			valid);


	u_results.results[index] =
		valid
			? vec4(
				displacement,
				1.0)
			: vec4(0.0);
}