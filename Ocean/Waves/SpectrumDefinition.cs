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
		-5.71f,
		-5.03f,
		-4.54f,
		-3.88f,
		-3.28f,
		-2.32f,
		-1.78f,
		-1.21f,
		-0.54f,
		 0.28f,
		 0.54f,
		 1.03f,
		 1.44f,
		-8.00f,
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
