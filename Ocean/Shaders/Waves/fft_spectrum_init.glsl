#[compute]
#version 450

// Spectrum initialization design based on the public MIT-licensed
// Crest Ocean System FFT spectrum implementation:
//
// crest/Assets/Crest/Crest/Shaders/Resources/FFT/FFTSpectrum.compute
//
// This is a Godot/GLSL reimplementation, not a direct Unity shader port.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;


// -----------------------------------------------------------------------------
// Resources
// -----------------------------------------------------------------------------

layout(set = 0, binding = 0, std430) readonly buffer SpectrumControls
{
	float power[];
}
spectrum_controls;


// RG = H0(k)
// BA = H0(-k)
layout(
	rgba32f,
	set = 0,
	binding = 1
)
uniform writeonly image2DArray result_initial;


// -----------------------------------------------------------------------------
// Push constants
// -----------------------------------------------------------------------------

layout(push_constant, std430) uniform Params
{
	// x = resolution
	// y = cascade count
	// z = spectrum band count
	// w = reserved
	uvec4 dimensions;

	// x = wind speed [m/s]
	// y = turbulence [0..1]
	// z = gravity
	// w = loop period [s], 0 = disabled
	vec4 sea_state;

	// x = wind direction X
	// y = wind direction Z
	// z = smallest wavelength power-of-two
	// w = reserved
	vec4 spectrum;
}
params;


// -----------------------------------------------------------------------------
// Constants
// -----------------------------------------------------------------------------

const float PI =
	3.14159265358979323846;

const float TWO_PI =
	6.28318530717958647692;

const float INV_PI_TIMES_TWO =
	0.63661977236758134308;

// 1 / (pi * 2) in the Crest spreading expression.
const float POS_COS_SQUARED_SCALE =
	0.33661977236758134308;

const uint WAVE_SAMPLE_FACTOR = 8u;


// -----------------------------------------------------------------------------
// Deterministic random
// -----------------------------------------------------------------------------

uint pcg_hash(uint input_value)
{
	uint state =
		input_value * 747796405u +
		2891336453u;

	uint word =
		((state >> ((state >> 28u) + 4u)) ^ state)
		* 277803737u;

	return (word >> 22u) ^ word;
}


float random_01(inout uint state)
{
	state = pcg_hash(state);

	return float(state) *
		(1.0 / 4294967296.0);
}


float random_gaussian(inout uint state)
{
	float u1 =
		max(random_01(state), 1e-6);

	float u2 =
		random_01(state);

	return
		sqrt(-2.0 * log(u1))
		* cos(TWO_PI * u2);
}


// -----------------------------------------------------------------------------
// Spectrum functions
// -----------------------------------------------------------------------------

void deep_dispersion(
	float k,
	float gravity,
	float loop_period,
	out float omega,
	out float domega_dk)
{
	omega =
		sqrt(abs(gravity * k));

	if (loop_period > 0.0)
	{
		float natural_period =
			TWO_PI / omega;

		float loops =
			ceil(loop_period / natural_period);

		float quantized_period =
			loop_period / loops;

		omega =
			TWO_PI / quantized_period;
	}

	domega_dk =
		gravity / (2.0 * omega);
}


float pierson_moskowitz_wind_term(
	float omega,
	float gravity,
	float wind_speed)
{
	if (wind_speed <= 1e-4)
	{
		return 0.0;
	}

	float omega_peak =
		0.87 * gravity / wind_speed;

	return exp(
		-1.291 *
		pow(omega_peak / omega, 4.0));
}


float directional_spreading(
	float cos_theta,
	float turbulence)
{
	if (cos_theta > 0.0)
	{
		return mix(
			INV_PI_TIMES_TWO *
				cos_theta * cos_theta,

			POS_COS_SQUARED_SCALE,

			turbulence);
	}

	return
		POS_COS_SQUARED_SCALE *
		turbulence;
}


// Equivalent to sampling the center-aligned 1D Crest control texture
// with linear clamp filtering.
float sample_spectrum_control(
	float octave_index,
	uint band_count)
{
	if (band_count == 0u)
	{
		return 0.0;
	}

	float max_index =
		float(band_count - 1u);

	float index =
		clamp(
			octave_index,
			0.0,
			max_index);

	uint index0 =
		uint(floor(index));

	uint index1 =
		min(index0 + 1u, band_count - 1u);

	float alpha =
		fract(index);

	return mix(
		spectrum_controls.power[index0],
		spectrum_controls.power[index1],
		alpha);
}


// -----------------------------------------------------------------------------
// Kernel
// -----------------------------------------------------------------------------

void main()
{
	uvec3 id =
		gl_GlobalInvocationID;

	uint resolution =
		params.dimensions.x;

	uint cascade_count =
		params.dimensions.y;

	uint band_count =
		params.dimensions.z;

	if (id.x >= resolution ||
		id.y >= resolution ||
		id.z >= cascade_count)
	{
		return;
	}


	ivec2 center =
		ivec2(int(resolution / 2u));

	ivec2 coord =
		ivec2(id.xy) - center;


	// DC component.
	if (coord.x == 0 &&
		coord.y == 0)
	{
		imageStore(
			result_initial,
			ivec3(id),
			vec4(0.0));

		return;
	}


	uint max_coord =
		uint(max(
			abs(coord.x),
			abs(coord.y)));


	// Every ordinary cascade owns one non-overlapping frequency band.
	//
	// The final cascade additionally accepts lower frequencies so the
	// longest wavelengths are not discarded.
	if (
		(
			id.z < cascade_count - 1u &&
			max_coord < WAVE_SAMPLE_FACTOR / 2u
		)
		||
		max_coord >= WAVE_SAMPLE_FACTOR
	)
	{
		imageStore(
			result_initial,
			ivec3(id),
			vec4(0.0));

		return;
	}


	float world_size =
		0.5 * exp2(float(id.z));


	vec2 k =
		TWO_PI *
		vec2(coord) /
		world_size;

	float k_mag =
		length(k);


	float omega;
	float domega_dk;

	deep_dispersion(
		k_mag,
		params.sea_state.z,
		params.sea_state.w,
		omega,
		domega_dk);


	float wavelength =
		TWO_PI / k_mag;

	float octave_index =
		log2(wavelength) -
		params.spectrum.z;


	float spectral_power =
		sample_spectrum_control(
			octave_index,
			band_count);


	spectral_power *=
		pierson_moskowitz_wind_term(
			omega,
			params.sea_state.z,
			params.sea_state.x);


	vec2 wind_direction =
		params.spectrum.xy;


	float cos_theta =
		dot(k, wind_direction) /
		k_mag;


	float turbulence =
		clamp(
			params.sea_state.y,
			0.0,
			1.0);


	float delta_s_positive =
		spectral_power *
		directional_spreading(
			cos_theta,
			turbulence);


	float delta_s_negative =
		spectral_power *
		directional_spreading(
			-cos_theta,
			turbulence);


	float delta_k =
		TWO_PI / world_size;


	float spectral_measure =
		(delta_k * delta_k) *
		domega_dk /
		k_mag;


	delta_s_positive *=
		spectral_measure;

	delta_s_negative *=
		spectral_measure;


	// Deterministic seed per texel/cascade.
	uint linear_index =
		id.z * resolution * resolution +
		id.y * resolution +
		id.x;

	uint rng_state =
		pcg_hash(
			linear_index ^
			0xA341316Cu);


	float amplitude_positive =
		random_gaussian(rng_state) *
		sqrt(abs(delta_s_positive) * 2.0);


	float amplitude_negative =
		random_gaussian(rng_state) *
		sqrt(abs(delta_s_negative) * 2.0);


	float phase_positive =
		random_01(rng_state) *
		TWO_PI;


	float phase_negative =
		random_01(rng_state) *
		TWO_PI;


	vec2 h0_positive =
		amplitude_positive *
		vec2(
			cos(phase_positive),
			-sin(phase_positive));


	vec2 h0_negative =
		amplitude_negative *
		vec2(
			cos(phase_negative),
			-sin(phase_negative));


	// Retained initially for behavioral calibration against Crest.
	// Revisit after numerical comparison.
	const float amplitude_calibration = 1.5;


	imageStore(
		result_initial,
		ivec3(id),
		amplitude_calibration *
		vec4(
			h0_positive,
			h0_negative));
}
