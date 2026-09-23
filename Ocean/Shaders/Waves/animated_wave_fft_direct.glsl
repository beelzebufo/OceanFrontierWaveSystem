#[compute]
#version 450

layout(
	local_size_x = 8,
	local_size_y = 8,
	local_size_z = 1
) in;


// -----------------------------------------------------------------------------
// Raw FFT displacement.
//
// Crest equivalent:
//
//     ShapeWaves / FFTCompute wave buffers.
//
// XYZ:
//     horizontal X
//     vertical Y
//     horizontal Z
//
// Each array layer is one periodic FFT wavelength band.
// -----------------------------------------------------------------------------

layout(
	set = 0,
	binding = 0
)
uniform sampler2DArray u_fft_displacement;


// -----------------------------------------------------------------------------
// Spatial Animated Waves LOD metadata.
//
// Must match:
//
//     AnimatedWaveLodGpuBuffer
//
// and follows Crest CascadeParams semantics.
//
// std430 stride = 32 bytes.
// -----------------------------------------------------------------------------

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


layout(
	std430,
	set = 0,
	binding = 1
)
readonly buffer AnimatedWaveLodBuffer
{
	AnimatedWaveLodParams lods[];
}
u_lod_data;


// -----------------------------------------------------------------------------
// Direct Animated Waves field.
//
// Crest equivalent:
//
//     LodDataMgrAnimWaves._waveBuffers
//
// IMPORTANT:
//
// This is NOT the canonical cumulative AnimatedWaveField.
//
// Each layer contains only wave content assigned directly to that spatial LOD.
// Coarser contributions are added later by the ShapeCombine equivalent.
//
// XYZ = direct displacement contribution.
// A   = reserved, zero in this stage.
// -----------------------------------------------------------------------------

layout(
	rgba16f,
	set = 0,
	binding = 2
)
uniform writeonly image2DArray u_direct_wave_field;

layout(r16f, set = 0, binding = 3)
uniform readonly image2DArray u_sea_floor_depth;


// -----------------------------------------------------------------------------
// Push constants.
//
// C# pass must match this block exactly.
//
//  0 : uint  resolution
//  4 : uint  lod_count
//  8 : uint  fft_cascade_count
// 12 : float wave_resolution_multiplier
// 16 : float lod_scale_alpha
// 20 : float padding
// 24 : float padding
// 28 : float padding
//
// Total = 32 bytes.
// -----------------------------------------------------------------------------

layout(
	push_constant,
	std430
)
uniform PushConstants
{
	uint resolution;

	uint lod_count;

	uint fft_cascade_count;

	float wave_resolution_multiplier;

	float lod_scale_alpha;

	uint has_sea_floor_depth;

	float shallow_water_attenuation;

	float shallow_water_maximum_depth;
}
pc;


// -----------------------------------------------------------------------------
// Crest FFT cascade relation.
//
// Existing OceanFrontier FFT source follows ShapeFFT's physical cascade sizes:
//
//     worldSize(cascade) = 0.5 * 2^cascade
//
// Minimum wavelength represented by that FFT cascade:
//
//     minWavelength = worldSize / 8
//
// This describes the RAW FFT source, not the spatial Animated Waves LOD.
// -----------------------------------------------------------------------------

float fft_world_size(
	uint cascade_index)
{
	return
		0.5 *
		exp2(
			float(
				cascade_index));
}


float fft_min_wavelength(
	uint cascade_index)
{
	return
		fft_world_size(
			cascade_index) /
		8.0;
}


// -----------------------------------------------------------------------------
// Crest WaveBatch wavelength.
//
// ShapeWaves.WaveBatch stores:
//
//     Wavelength = MinWavelength / WaveResolutionMultiplier
//
// LodDataMgrAnimWaves.FilterWavelength then uses this effective wavelength
// for assigning the wave input to a spatial Animated Waves LOD.
//
// Do the same here.
// -----------------------------------------------------------------------------

float effective_input_wavelength(
	uint cascade_index)
{
	return
		fft_min_wavelength(
			cascade_index) /
		pc.wave_resolution_multiplier;
}


float shallow_attenuation_weight(float terrain_y, uint cascade_index)
{
	// Direct port of Crest 4 AnimWavesSpectrum.shader shallow attenuation
	// at db0658ff0b2e93e4a9e28cc2867509658b0ecc00 (MIT).
	float average_wavelength = fft_min_wavelength(cascade_index) * 1.5;
	float depth = 0.0 - terrain_y;
	float depth_weight = clamp(2.0 * depth / average_wavelength, 0.0, 1.0);
	if (pc.shallow_water_maximum_depth < 1000.0)
	{
		depth_weight = mix(
			depth_weight,
			1.0,
			clamp(depth / pc.shallow_water_maximum_depth, 0.0, 1.0));
	}
	float amount = clamp(pc.shallow_water_attenuation, 0.0, 1.0);
	return amount * depth_weight + (1.0 - amount);
}


// -----------------------------------------------------------------------------
// Crest FilterWavelength equivalent.
//
// Normal case:
//
//     lodMin <= wavelength < lodMax
//
// where:
//
//     lodMin = maxWavelength / 2
//     lodMax = maxWavelength
//
// Since adjacent spatial LODs have x2 texel size:
//
//     next.minWavelength == current.maxWavelength
//
// so one ordinary FFT band belongs directly to one spatial LOD.
//
// Crest has a special rule for waves which reach the end of the spatial LOD
// chain. These are blended across the last two LODs using viewer LOD scale
// alpha so whole-ocean x2 scale changes do not pop.
// -----------------------------------------------------------------------------

float direct_lod_weight(
	float input_wavelength,
	uint lod_index)
{
	if (pc.lod_count == 0u)
	{
		return 0.0;
	}


	AnimatedWaveLodParams lod =
		u_lod_data.lods[
			lod_index];


	float lod_max_wavelength =
		lod.max_wavelength;


	float lod_min_wavelength =
		0.5 *
		lod_max_wavelength;


	AnimatedWaveLodParams last_lod =
		u_lod_data.lods[
			pc.lod_count - 1u];


	float global_max_wavelength =
		last_lod.max_wavelength;


	//
	// Crest:
	//
	// if wavelength < lodMin:
	//     this input is too small for this LOD.
	//

	if (input_wavelength <
		lod_min_wavelength)
	{
		return 0.0;
	}


	//
	// Crest end-of-chain transition.
	//
	// Large wavelengths that no longer have another spatial LOD available
	// transition between the final two LOD slices.
	//

	if (input_wavelength >=
		0.5 *
		global_max_wavelength)
	{
		if (pc.lod_count == 1u)
		{
			return 1.0;
		}


		uint second_last_lod =
			pc.lod_count - 2u;


		uint last_lod_index =
			pc.lod_count - 1u;


		if (lod_index ==
			second_last_lod)
		{
			return
				1.0 -
				clamp(
					pc.lod_scale_alpha,
					0.0,
					1.0);
		}


		if (lod_index ==
			last_lod_index)
		{
			return
				clamp(
					pc.lod_scale_alpha,
					0.0,
					1.0);
		}


		return 0.0;
	}


	//
	// Ordinary Crest wavelength assignment:
	//
	//     lodMin <= wavelength < lodMax
	//

	if (input_wavelength <
		lod_max_wavelength)
	{
		return 1.0;
	}


	return 0.0;
}


// -----------------------------------------------------------------------------
// Main.
//
// One invocation writes one texel of one DirectWaveField spatial LOD.
//
// Unlike the old AnimatedWaveFftCompose pass, this does NOT accumulate every
// coarser FFT band into every fine spatial LOD.
//
// It only writes the wave inputs assigned DIRECTLY to this LOD.
//
// Cumulative hierarchy is produced later by ShapeCombine.
// -----------------------------------------------------------------------------

void main()
{
	uvec3 id =
		gl_GlobalInvocationID;


	if (id.x >=
			pc.resolution ||
		id.y >=
			pc.resolution ||
		id.z >=
			pc.lod_count)
	{
		return;
	}


	uint lod_index =
		id.z;


	AnimatedWaveLodParams lod =
		u_lod_data.lods[
			lod_index];


	//
	// Pixel centre in this spatial Animated Waves LOD.
	//

	vec2 uv =
		(
			vec2(
				id.xy) +
			vec2(0.5)
		) /
		lod.texture_resolution;


	//
	// Crest UVToWorld equivalent:
	//
	// texelWidth * textureResolution == spatial LOD world size.
	//

	vec2 world_xz =
		lod.center_xz +
		(
			uv -
			vec2(0.5)
		) *
		(
			lod.texel_width *
			lod.texture_resolution
		);


	vec3 displacement =
		vec3(0.0);

	float terrain_y = 0.0;
	if (pc.has_sea_floor_depth != 0u)
	{
		terrain_y = imageLoad(u_sea_floor_depth, ivec3(id)).r;
	}


	//
	// Each raw FFT cascade behaves as one Crest WaveBatch input.
	//
	// FilterWavelength decides whether this input belongs directly to this
	// spatial LOD.
	//

	for (uint cascade = 0u;
		 cascade <
			pc.fft_cascade_count;
		 ++cascade)
	{
		float input_wavelength =
			effective_input_wavelength(
				cascade);


		float weight =
			direct_lod_weight(
				input_wavelength,
				lod_index);


		if (weight <= 0.0)
		{
			continue;
		}


		float source_world_size =
			fft_world_size(
				cascade);


		//
		// Crest AnimWavesSpectrum.shader global-wave path:
		//
		//     worldPos / waveBufferSize
		//
		// Raw FFT data is periodic, therefore the sampler uses repeat.
		//

		vec2 fft_uv =
			world_xz /
			source_world_size;


		vec3 source_displacement =
			textureLod(
				u_fft_displacement,
				vec3(
					fft_uv,
					float(
						cascade)),
				0.0
			).xyz;

		if (pc.has_sea_floor_depth != 0u)
		{
			source_displacement *= shallow_attenuation_weight(terrain_y, cascade);
		}


		displacement +=
			weight *
			source_displacement;
	}


	imageStore(
		u_direct_wave_field,
		ivec3(
			id),
		vec4(
			displacement,
			0.0));
}
