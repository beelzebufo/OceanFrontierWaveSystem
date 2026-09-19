using System;
using Godot;

namespace OceanFrontier.Water.Waves;

[GlobalClass]
public partial class SpectrumDefinition : Resource
{
	public const int DefaultBandCount = 14;

	[Export]
	public float SmallestWavelengthPowerOfTwo { get; set; } = -4.0f;

	[Export(PropertyHint.Range, "0.0,10.0,0.01,or_greater")]
	public float Multiplier { get; set; } = 1.0f;

	[Export]
	public float[] PowerLog10 { get; set; } =
	{
		-7.10794f,
		-6.42794f,
		-5.93794f,
		-5.27794f,
		-4.67794f,
		-3.71794f,
		-3.17794f,
		-2.60794f,
		-1.93794f,
		-1.11794f,
		-0.85794f,
		-0.36794f,
		 0.04206f,
		-9.39794f,
	};

	[Export]
	public Godot.Collections.Array<bool> Disabled { get; set; } =
	[
		false, false, false, false,
		false, false, false, false,
		false, false, false, false,
		false, false,
	];		

	public int BandCount => PowerLog10?.Length ?? 0;

	public bool IsStructurallyValid()
	{
		return PowerLog10 != null
			&& Disabled != null
			&& PowerLog10.Length > 0
			&& PowerLog10.Length == Disabled.Count;
	}

	/// <summary>
	/// Converts authoring-space log10 power into the compact linear
	/// power array consumed by the GPU spectrum initializer.
	///
	/// This is not FFT work. Only a small configuration array is built.
	/// </summary>
	public float[] BuildLinearPowerControls()
	{
		if (!IsStructurallyValid())
		{
			throw new InvalidOperationException(
				"SpectrumDefinition arrays are invalid.");
		}

		var result = new float[PowerLog10.Length];

		float multiplierSq = Multiplier * Multiplier;

		for (int i = 0; i < result.Length; i++)
		{
			result[i] = Disabled[i]
				? 0.0f
				: MathF.Pow(10.0f, PowerLog10[i]) * multiplierSq;
		}

		return result;
	}
}
