using System;

namespace OceanFrontier.Water.Waves.FFT;

/// <summary>
/// Generates the static butterfly/twiddle lookup table used by the GPU FFT.
///
/// This is initialization data only.
/// It is not a CPU FFT and performs no per-frame wave computation.
///
/// Layout:
///     width  = FFT resolution
///     height = log2(FFT resolution)
///
/// Each texel stores one complex FFT weight as:
///     R = real
///     G = imaginary
///
/// Algorithm follows the butterfly table generation used by Crest FFTCompute.
/// </summary>
internal static class FftButterflyTable
{
	public static int GetPassCount(int resolution)
	{
		if (!IsPowerOfTwo(resolution))
		{
			throw new ArgumentException(
				"FFT resolution must be a power of two.",
				nameof(resolution));
		}

		int passes = 0;
		int value = resolution;

		while (value > 1)
		{
			value >>= 1;
			passes++;
		}

		return passes;
	}

	/// <summary>
	/// Builds RG32F texture data.
	///
	/// Texel:
	///     R = twiddle.real
	///     G = twiddle.imag
	///
	/// Returned bytes are row-major:
	///     x = FFT coordinate
	///     y = FFT pass
	/// </summary>
	public static byte[] GenerateRg32Float(
		int resolution)
	{
		int passCount =
			GetPassCount(resolution);

		int texelCount =
			checked(resolution * passCount);

		// Two floats per texel: real + imaginary.
		var values =
			new float[checked(texelCount * 2)];

		int offset = 1;
		int numIterations =
			resolution >> 1;

		for (
			int passIndex = 0;
			passIndex < passCount;
			passIndex++)
		{
			int rowOffset =
				passIndex * resolution;

			int start = 0;
			int end = 2 * offset;

			for (
				int iteration = 0;
				iteration < numIterations;
				iteration++)
			{
				float bigK = 0.0f;

				for (
					int k = start;
					k < end;
					k += 2)
				{
					float phase =
						2.0f *
						MathF.PI *
						bigK *
						numIterations /
						resolution;

					float cos =
						MathF.Cos(phase);

					float sin =
						MathF.Sin(phase);

					// Crest:
					//
					// first:
					//     ( cos, -sin )
					//
					// paired:
					//     (-cos,  sin )

					WriteComplex(
						values,
						rowOffset + k / 2,
						cos,
						-sin);

					WriteComplex(
						values,
						rowOffset + k / 2 + offset,
						-cos,
						sin);

					bigK += 1.0f;
				}

				start += 4 * offset;
				end = start + 2 * offset;
			}

			numIterations >>= 1;
			offset <<= 1;
		}

		var bytes =
			new byte[
				checked(
					values.Length *
					sizeof(float))];

		Buffer.BlockCopy(
			values,
			0,
			bytes,
			0,
			bytes.Length);

		return bytes;
	}

	private static void WriteComplex(
		float[] destination,
		int texelIndex,
		float real,
		float imaginary)
	{
		int valueIndex =
			texelIndex * 2;

		destination[valueIndex] =
			real;

		destination[valueIndex + 1] =
			imaginary;
	}

	private static bool IsPowerOfTwo(
		int value)
	{
		return
			value > 0 &&
			(value & (value - 1)) == 0;
	}
}
