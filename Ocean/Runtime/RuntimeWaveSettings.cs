using System;
using Godot;
using OceanFrontier.Water.Waves;
using OceanFrontier.Water.Waves.FFT;

namespace OceanFrontier.Water.Runtime;

/// <summary>Plain main-thread settings snapshot; never a Godot Resource.</summary>
internal sealed class RuntimeWaveSettings
{
	internal float WindSpeed;
	internal float WindDirectionDegrees;
	internal float WindTurbulence;
	internal float Gravity;
	internal float LoopPeriod;
	internal float Chop;
	internal float Multiplier;
	internal float SmallestWavelengthPowerOfTwo;
	internal float[] PowerLog10;
	internal bool[] Disabled;
	internal int H0Revision;

	internal static RuntimeWaveSettings FromResources(SeaState sea, SpectrumDefinition spectrum) => new()
	{
		WindSpeed = sea.WindSpeedMetersPerSecond,
		WindDirectionDegrees = sea.WindDirectionDegrees,
		WindTurbulence = sea.WindTurbulence,
		Gravity = sea.Gravity,
		LoopPeriod = sea.LoopPeriodSeconds,
		Chop = sea.Chop,
		Multiplier = spectrum.Multiplier,
		SmallestWavelengthPowerOfTwo = spectrum.SmallestWavelengthPowerOfTwo,
		PowerLog10 = (float[])spectrum.PowerLog10.Clone(),
		Disabled = CopyDisabled(spectrum.Disabled),
		H0Revision = 1,
	};

	internal RuntimeWaveSettings Copy()
	{
		var copy = (RuntimeWaveSettings)MemberwiseClone();
		copy.PowerLog10 = (float[])PowerLog10.Clone();
		copy.Disabled = (bool[])Disabled.Clone();
		return copy;
	}

	internal bool HasSameH0(RuntimeWaveSettings other)
	{
		if (WindSpeed != other.WindSpeed ||
			WindDirectionDegrees != other.WindDirectionDegrees ||
			WindTurbulence != other.WindTurbulence ||
			Gravity != other.Gravity ||
			LoopPeriod != other.LoopPeriod ||
			Multiplier != other.Multiplier ||
			SmallestWavelengthPowerOfTwo != other.SmallestWavelengthPowerOfTwo ||
			PowerLog10.Length != other.PowerLog10.Length)
			return false;

		for (int i = 0; i < PowerLog10.Length; i++)
			if (PowerLog10[i] != other.PowerLog10[i] || Disabled[i] != other.Disabled[i])
				return false;
		return true;
	}

	internal float[] BuildLinearPowerControls()
	{
		var result = new float[PowerLog10.Length];
		float multiplierSq = Multiplier * Multiplier;
		for (int i = 0; i < result.Length; i++)
			result[i] = Disabled[i] ? 0.0f : MathF.Pow(10.0f, PowerLog10[i]) * multiplierSq;
		return result;
	}

	internal FftSpectrumInitSettings ToInitSettings(int resolution, int cascades)
	{
		float radians = Mathf.DegToRad(WindDirectionDegrees);
		return new FftSpectrumInitSettings(
			resolution, cascades, PowerLog10.Length,
			WindSpeed, WindTurbulence, Gravity, LoopPeriod,
			Mathf.Cos(radians), Mathf.Sin(radians), SmallestWavelengthPowerOfTwo);
	}

	private static bool[] CopyDisabled(Godot.Collections.Array<bool> source)
	{
		var result = new bool[source.Count];
		for (int i = 0; i < result.Length; i++) result[i] = source[i];
		return result;
	}
}
