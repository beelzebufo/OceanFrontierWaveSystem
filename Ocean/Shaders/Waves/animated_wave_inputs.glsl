#[compute]
#version 450

// Crest 4 source reference (MIT), pinned commit
// db0658ff0b2e93e4a9e28cc2867509658b0ecc00:
// - LodDataMgrAnimWaves.FilterWavelength
// - ShapeWaves.WaveBatch blend/IgnoreTransitionWeight behaviour
// - AnimWavesGerstnerBatchGeometry feather-at-UV-extents formula
// - ScaleByFactor.shader multiplicative scalar/invert behaviour
// - LodDataMgr.SubmitDrawsFiltered transition weight forwarding
// - AnimWavesSpectrum.shader two-angle directional FFT sampling
// - ShapeWaves.cs::WaveBatch one-erase-per-source Blend behaviour
// - ShapeFFT.cs local geometry direction contract

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;


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


layout(std430, set = 0, binding = 0)
readonly buffer AnimatedWaveLodBuffer
{
	AnimatedWaveLodParams lods[];
}
u_lod_data;


// std430 stride = 80 bytes. Must match AnimatedWaveInputPass.
struct AnimatedWaveInputDescriptor
{
	// xy = world centre, zw = local X axis in world XZ.
	vec4 center_xz_axis_x;

	// xy = local Z axis in world XZ, zw = full rectangle size XZ.
	vec4 axis_z_size_xz;

	// x = feather width in UV, y = weight,
	// z = source amplitude, w = wavelength metres.
	vec4 feather_weight_amplitude_wavelength;

	// x = displacement X or DirectionalFft relative angle radians,
	// yz = constant source displacement YZ, w = ScaleByFactor scalar.
	vec4 displacement_xyz_scale;

	// x = placement, y = blend mode, z = operation, w = flags.
	uvec4 placement_blend_operation_flags;
};


layout(std430, set = 0, binding = 1)
readonly buffer AnimatedWaveInputBuffer
{
	AnimatedWaveInputDescriptor inputs[];
}
u_inputs;


layout(rgba16f, set = 0, binding = 2)
uniform image2DArray u_target;


// Existing global raw FFT source. DirectionalFft only resamples this texture;
// it does not own or evolve another FFT.
layout(set = 0, binding = 3)
uniform sampler2DArray u_fft_displacement;


layout(push_constant, std430)
uniform PushConstants
{
	uint resolution;
	uint lod_count;
	uint input_count;
	uint phase;
	float lod_scale_alpha;
	uint fft_cascade_count;
	float wave_resolution_multiplier;
	float padding_2;
}
pc;


const uint PLACEMENT_WAVELENGTH_PRE_COMBINE = 0u;
const uint PLACEMENT_ALL_LODS_PRE_COMBINE = 1u;
const uint PLACEMENT_ALL_LODS_POST_COMBINE = 2u;
const uint BLEND_MODE_BLEND = 1u;
const uint OPERATION_SCALE_BY_FACTOR = 1u;
const uint OPERATION_DIRECTIONAL_FFT = 2u;
const uint FLAG_INVERT = 1u;
const float PI = 3.14159265358979323846;
const float TWO_PI = 2.0 * PI;
const float DIRECTION_STEP = PI / 20.0;


// Direct port of Crest 4 LodDataMgrAnimWaves.FilterWavelength semantics.
// Eligibility is deliberately separate from transition weight because Blend's
// erase pass ignores transition alpha while its additive source does not.
bool wavelength_lod_weight(
	float wavelength,
	uint lod_index,
	out float transition_weight)
{
	transition_weight = 1.0;

	float lod_max = u_lod_data.lods[lod_index].max_wavelength;
	float lod_min = 0.5 * lod_max;

	if (wavelength < lod_min)
	{
		return false;
	}

	float global_max =
		u_lod_data.lods[pc.lod_count - 1u].max_wavelength;

	if (wavelength >= 0.5 * global_max)
	{
		if (pc.lod_count == 1u)
		{
			return true;
		}

		if (lod_index == pc.lod_count - 2u)
		{
			transition_weight = 1.0 - clamp(pc.lod_scale_alpha, 0.0, 1.0);
			return true;
		}

		if (lod_index == pc.lod_count - 1u)
		{
			transition_weight = clamp(pc.lod_scale_alpha, 0.0, 1.0);
			return true;
		}

		return false;
	}

	return wavelength < lod_max;
}


bool spatial_feather(
	vec2 world_xz,
	AnimatedWaveInputDescriptor descriptor,
	out float feather)
{
	vec2 delta = world_xz - descriptor.center_xz_axis_x.xy;
	vec2 local = vec2(
		dot(delta, descriptor.center_xz_axis_x.zw),
		dot(delta, descriptor.axis_z_size_xz.xy));
	vec2 uv = local / descriptor.axis_z_size_xz.zw + vec2(0.5);

	if (any(lessThan(uv, vec2(0.0))) ||
		any(greaterThan(uv, vec2(1.0))))
	{
		feather = 0.0;
		return false;
	}

	// Direct port of Crest AnimWavesGerstnerBatchGeometry feather formula.
	vec2 offset = abs(uv - vec2(0.5));
	float radius = max(offset.x, offset.y);
	float feather_width =
		clamp(descriptor.feather_weight_amplitude_wavelength.x, 0.001, 0.5);

	feather = clamp(
		1.0 - (radius - (0.5 - feather_width)) / feather_width,
		0.0,
		1.0);

	return true;
}


float fft_world_size(uint cascade_index)
{
	return 0.5 * exp2(float(cascade_index));
}


float effective_fft_wavelength(uint cascade_index)
{
	return
		(fft_world_size(cascade_index) / 8.0) /
		pc.wave_resolution_multiplier;
}


// Direct mathematical port of Crest 4 AnimWavesSpectrum.shader. Positive
// relative angles rotate +X toward +Z. Sampling uses R(-angle) world XZ and
// horizontal displacement uses R(+angle), matching SeaState's (cos, sin).
vec3 sample_directional_fft(
	vec2 world_xz,
	uint cascade_index,
	float direction_radians)
{
	vec2 requested_axis = vec2(
		cos(direction_radians),
		sin(direction_radians));

	float axis_heading =
		atan(requested_axis.y, requested_axis.x) + TWO_PI;
	float remainder = mod(axis_heading, DIRECTION_STEP);
	float angle_0 = axis_heading - remainder;
	float angle_1 = angle_0 + DIRECTION_STEP;

	vec2 axis_x_0 = vec2(cos(angle_0), sin(angle_0));
	vec2 axis_x_1 = vec2(cos(angle_1), sin(angle_1));
	vec2 axis_z_0 = vec2(-axis_x_0.y, axis_x_0.x);
	vec2 axis_z_1 = vec2(-axis_x_1.y, axis_x_1.x);

	vec2 scaled_world = world_xz / fft_world_size(cascade_index);
	vec2 uv_0 = vec2(
		dot(scaled_world, axis_x_0),
		dot(scaled_world, axis_z_0));
	vec2 uv_1 = vec2(
		dot(scaled_world, axis_x_1),
		dot(scaled_world, axis_z_1));

	vec3 displacement_0 = textureLod(
		u_fft_displacement,
		vec3(uv_0, float(cascade_index)),
		0.0).xyz;
	vec3 displacement_1 = textureLod(
		u_fft_displacement,
		vec3(uv_1, float(cascade_index)),
		0.0).xyz;

	vec3 displacement = mix(
		displacement_0,
		displacement_1,
		remainder / DIRECTION_STEP);

	displacement.xz =
		displacement.x * requested_axis +
		displacement.z * vec2(-requested_axis.y, requested_axis.x);

	return displacement;
}


void main()
{
	uvec3 id = gl_GlobalInvocationID;

	if (id.x >= pc.resolution ||
		id.y >= pc.resolution ||
		id.z >= pc.lod_count)
	{
		return;
	}

	AnimatedWaveLodParams lod = u_lod_data.lods[id.z];
	vec2 uv = (vec2(id.xy) + vec2(0.5)) / lod.texture_resolution;
	vec2 world_xz = lod.center_xz +
		(uv - vec2(0.5)) * lod.texel_width * lod.texture_resolution;

	ivec3 coordinate = ivec3(id);
	vec4 target = imageLoad(u_target, coordinate);
	vec3 result = target.xyz;

	for (uint input_index = 0u;
		 input_index < pc.input_count;
		 ++input_index)
	{
		AnimatedWaveInputDescriptor descriptor = u_inputs.inputs[input_index];
		uint placement = descriptor.placement_blend_operation_flags.x;
		uint operation = descriptor.placement_blend_operation_flags.z;

		if (operation == OPERATION_DIRECTIONAL_FFT)
		{
			// Direction sources always write the direct field. A corrupted post
			// placement is rejected rather than modifying canonical AWF.
			if (pc.phase != 0u || placement == PLACEMENT_ALL_LODS_POST_COMBINE)
			{
				continue;
			}
		}
		else if ((pc.phase == 0u && placement == PLACEMENT_ALL_LODS_POST_COMBINE) ||
			(pc.phase == 1u && placement != PLACEMENT_ALL_LODS_POST_COMBINE))
		{
			continue;
		}

		float transition_weight = 1.0;
		bool eligible = true;

		if (operation != OPERATION_DIRECTIONAL_FFT &&
			placement == PLACEMENT_WAVELENGTH_PRE_COMBINE)
		{
			eligible = wavelength_lod_weight(
				descriptor.feather_weight_amplitude_wavelength.w,
				id.z,
				transition_weight);
		}

		if (!eligible)
		{
			continue;
		}

		float feather = 0.0;
		if (!spatial_feather(world_xz, descriptor, feather))
		{
			continue;
		}

		if (operation == OPERATION_DIRECTIONAL_FFT)
		{
			float input_weight =
				clamp(descriptor.feather_weight_amplitude_wavelength.y, 0.0, 1.0);

			if (feather <= 0.0 || input_weight <= 0.0)
			{
				continue;
			}

			bool has_eligible_cascade = false;
			vec3 directional_source = vec3(0.0);
			float direction_radians = descriptor.displacement_xyz_scale.x;

			for (uint cascade_index = 0u;
				 cascade_index < pc.fft_cascade_count;
				 ++cascade_index)
			{
				float cascade_transition_weight = 1.0;
				bool cascade_eligible = wavelength_lod_weight(
					effective_fft_wavelength(cascade_index),
					id.z,
					cascade_transition_weight);

				if (!cascade_eligible)
				{
					continue;
				}

				has_eligible_cascade = true;

				if (cascade_transition_weight <= 0.0)
				{
					continue;
				}

				directional_source +=
					cascade_transition_weight *
					sample_directional_fft(
						world_xz,
						cascade_index,
						direction_radians);
			}

			if (!has_eligible_cascade)
			{
				continue;
			}

			float override_weight = input_weight * feather;

			if (descriptor.placement_blend_operation_flags.y == BLEND_MODE_BLEND)
			{
				// Crest WaveBatch erases once per ShapeWaves source and LOD,
				// independent of per-cascade transition alpha.
				result =
					result * (1.0 - override_weight) +
					directional_source * override_weight;
			}
			else
			{
				result += directional_source * override_weight;
			}

			continue;
		}

		if (operation == OPERATION_SCALE_BY_FACTOR)
		{
			// Crest LodDataMgr.SubmitDrawsFiltered skips the draw at alpha 0.
			// For alpha > 0 it forwards weight * alpha as _Weight.
			if (transition_weight <= 0.0)
			{
				continue;
			}

			float selected_scale =
				clamp(descriptor.displacement_xyz_scale.w, 0.0, 1.0);

			if ((descriptor.placement_blend_operation_flags.w & FLAG_INVERT) != 0u)
			{
				selected_scale = 1.0 - selected_scale;
			}

			// Direct port of Crest ScaleByFactor.shader:
			// scale = lerp(1, selectedScale, FeatherWeightFromUV(...));
			// return scale * _Weight;
			float factor =
				mix(1.0, selected_scale, feather) * transition_weight;

			result.xyz *= factor;
			continue;
		}

		if (feather <= 0.0)
		{
			continue;
		}

		float weight = descriptor.feather_weight_amplitude_wavelength.y;
		vec3 source = descriptor.displacement_xyz_scale.xyz *
			descriptor.feather_weight_amplitude_wavelength.z;

		if (descriptor.placement_blend_operation_flags.y == BLEND_MODE_BLEND)
		{
			// Crest WaveBatch performs a transition-independent multiply pass,
			// followed by the normal transition-weighted additive pass.
			float override_weight = weight * feather;
			float add_weight = override_weight * transition_weight;
			result = result * (1.0 - override_weight) + source * add_weight;
		}
		else
		{
			float effective_weight = weight * feather * transition_weight;
			result += source * effective_weight;
		}
	}

	// Stage 5A-1 modifies displacement XYZ only.
	imageStore(u_target, coordinate, vec4(result, target.w));
}
