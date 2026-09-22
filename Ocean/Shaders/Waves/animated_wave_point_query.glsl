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
// z   = Crest minGridSize
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


// Samples the canonical AnimatedWaveField using the Crest 4
// collision-query LOD contract.
//
// Crest reference:
//
//     QueryDisplacements.compute
//     OceanHelpersNew.hlsl::PosToSliceIndices()
//
// The last AWF slice is transition-only for displacement queries:
// it may be slice1, but never slice0.
vec3 sample_surface(
	vec2 world_xz,
	float min_grid_size,
	out bool valid)
{
	valid =
		pc.lod_count > 0u;


	if (!valid)
	{
		return
			vec3(0.0);
	}


	//
	// Project-specific validity rule.
	//
	// Crest itself can sample clamped data outside the outermost
	// cascade. We deliberately keep the existing OceanFrontier
	// contract instead: outside the complete canonical AWF is invalid.
	//

	uint last_lod =
		pc.lod_count -
		1u;


	AnimatedWaveLodParams coarsest =
		u_lod_data.lods[
			last_lod];


	float coarsest_world_size =
		4.0 *
		coarsest.scale;


	vec2 coarsest_offset =
		abs(
			world_xz -
			coarsest.center_xz);


	if (max(
			coarsest_offset.x,
			coarsest_offset.y) >
		0.5 *
		coarsest_world_size)
	{
		valid =
			false;


		return
			vec3(0.0);
	}


	//
	// Degenerate one-LOD configuration has no transition slice.
	//

	if (pc.lod_count == 1u)
	{
		return
			sample_lod(
				world_xz,
				0);
	}


	//
	// Crest QueryDisplacements.compute:
	//
	//     minSlice =
	//         floor(
	//             log2(
	//                 max(
	//                     minGridSize /
	//                     gridSizeSlice0,
	//                     1.0)));
	//
	// The caller already supplies:
	//
	//     minGridSize = MinSpatialLength / 4
	//

	float grid_size_slice_0 =
		u_lod_data
			.lods[0]
			.texel_width;


	float max_slice =
		float(
			pc.lod_count -
			2u);


	float min_slice =
		clamp(
			floor(
				log2(
					max(
						min_grid_size /
							grid_size_slice_0,
						1.0))),
			0.0,
			max_slice);


	//
	// Crest OceanHelpersNew.hlsl::PosToSliceIndices().
	//

	float base_scale =
		u_lod_data
			.lods[0]
			.scale;


	vec2 offset =
		abs(
			world_xz -
			pc.focus_xz);


	float taxicab =
		max(
			offset.x,
			offset.y);


	float slice_number =
		log2(
			max(
				taxicab /
					base_scale,
				1.0));


	slice_number =
		clamp(
			slice_number,
			min_slice,
			max_slice);


	int lod0 =
		int(
			floor(
				slice_number));


	int lod1 =
		lod0 +
		1;


	float lod_alpha =
		fract(
			slice_number);


	//
	// Exact Crest PosToSliceIndices transition remap.
	//

	const float BLACK_POINT =
		0.15;


	const float WHITE_POINT =
		0.85;


	lod_alpha =
		clamp(
			(lod_alpha -
			 BLACK_POINT) /
			(WHITE_POINT -
			 BLACK_POINT),
			0.0,
			1.0);


	//
	// Crest _MeshScaleLerp / ViewerAltitudeLevelAlpha.
	//
	// Only LOD0 receives the whole-stack altitude transition.
	//

	if (lod0 == 0)
	{
		lod_alpha =
			min(
				lod_alpha +
					pc.lod_scale_alpha,
				1.0);
	}


	//
	// Crest QueryDisplacements.compute weighting.
	//
	// weight is currently 1 for our AWF slices, but preserve
	// the contract for future last-LOD transition weighting.
	//

	float weight0 =
		u_lod_data
			.lods[lod0]
			.weight;


	float weight1 =
		u_lod_data
			.lods[lod1]
			.weight;


	float wt0 =
		(1.0 -
		 lod_alpha) *
		weight0;


	float wt1 =
		(1.0 -
		 wt0) *
		weight1;


	return
		wt0 *
			sample_lod(
				world_xz,
				lod0) +
		wt1 *
			sample_lod(
				world_xz,
				lod1);
}


// Horizontal displacement inversion.
//
// Solve:
//
//     undisplaced + D.xz(undisplaced) = target
//
// using Crest-like four fixed-point iterations.
//
// query.z is Crest minGridSize:
//
//     minWavelength = MinSpatialLength / 2
//     minGridSize   = minWavelength / 2
//                   = MinSpatialLength / 4
//
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