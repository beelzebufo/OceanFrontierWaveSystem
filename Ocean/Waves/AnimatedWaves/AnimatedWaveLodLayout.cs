using System;
using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// Spatial description of one AnimatedWaveField LOD slice.
///
/// This is NOT an FFT cascade.
///
/// FFT cascade:
///     frequency / wavelength band.
///
/// AnimatedWave LOD slice:
///     camera-relative spatial window.
/// </summary>
internal readonly struct AnimatedWaveLodSlice
{
	public int Index { get; }

	public Vector2 CenterXZ { get; }

	/// <summary>
	/// Full physical width/height of this square LOD in world units.
	/// </summary>
	public float WorldSize { get; }

	public float TexelWidth { get; }

	/// <summary>
	/// Crest-like smallest wavelength appropriate for this LOD.
	/// </summary>
	public float MinWavelength { get; }

	/// <summary>
	/// Crest-like largest wavelength assigned directly to this LOD.
	/// </summary>
	public float MaxWavelength { get; }

	public AnimatedWaveLodSlice(
		int index,
		Vector2 centerXZ,
		float worldSize,
		float texelWidth,
		float minWavelength,
		float maxWavelength)
	{
		Index = index;
		CenterXZ = centerXZ;

		WorldSize = worldSize;
		TexelWidth = texelWidth;

		MinWavelength = minWavelength;
		MaxWavelength = maxWavelength;
	}
}


/// <summary>
/// Camera-relative spatial layout for AnimatedWaveField.
///
/// Stage 2B:
/// - CPU metadata only;
/// - no GPU buffers;
/// - no composition;
/// - no FFT sampling.
/// </summary>
internal sealed class AnimatedWaveLodLayout
{
	private readonly AnimatedWaveLodSlice[] _slices;

	public int Resolution { get; }

	public int LodCount =>
		_slices.Length;

	public float BaseWorldSize { get; }

	public Vector2 FocusXZ { get; private set; }

	public AnimatedWaveLodSlice this[int index] =>
		_slices[index];


	public AnimatedWaveLodLayout(
		int resolution,
		int lodCount,
		float baseWorldSize)
	{
		if (resolution <= 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(resolution));
		}

		if (lodCount <= 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(lodCount));
		}

		if (!float.IsFinite(baseWorldSize) ||
			baseWorldSize <= 0.0f)
		{
			throw new ArgumentOutOfRangeException(
				nameof(baseWorldSize));
		}

		Resolution = resolution;
		BaseWorldSize = baseWorldSize;

		_slices =
			new AnimatedWaveLodSlice[lodCount];

		Update(Vector2.Zero);
	}


	/// <summary>
	/// Updates spatial LOD metadata around a world-space focus.
	///
	/// Centers are snapped independently for every LOD to that
	/// LOD's texel width.
	/// </summary>
	public void Update(
		Vector2 focusXZ)
	{
		FocusXZ = focusXZ;

		for (int lod = 0;
			 lod < _slices.Length;
			 lod++)
		{
			_slices[lod] =
				CalculateSlice(
					Resolution,
					BaseWorldSize,
					lod,
					focusXZ);
		}
	}


	internal static AnimatedWaveLodSlice CalculateSlice(
		int resolution,
		float baseWorldSize,
		int lodIndex,
		Vector2 focusXZ)
	{
		float worldSize =
			baseWorldSize * MathF.Pow(2.0f, lodIndex);

		float texelWidth =
			worldSize / resolution;

		Vector2 centerXZ = new(
			SnapDown(focusXZ.X, texelWidth),
			SnapDown(focusXZ.Y, texelWidth));

		// Crest LodTransform: max wavelength = 4 texels.
		float maxWavelength = 4.0f * texelWidth;

		return new AnimatedWaveLodSlice(
			lodIndex,
			centerXZ,
			worldSize,
			texelWidth,
			0.5f * maxWavelength,
			maxWavelength);
	}


	/// <summary>
	/// Converts an XZ world position into UV coordinates
	/// of a selected AnimatedWave LOD slice.
	/// </summary>
	public Vector2 WorldToUv(
		Vector2 worldXZ,
		int lodIndex)
	{
		AnimatedWaveLodSlice slice =
			_slices[lodIndex];

		return
			(worldXZ - slice.CenterXZ) /
			slice.WorldSize +
			new Vector2(
				0.5f,
				0.5f);
	}


	/// <summary>
	/// Converts UV coordinates of a selected LOD slice
	/// back into XZ world position.
	/// </summary>
	public Vector2 UvToWorld(
		Vector2 uv,
		int lodIndex)
	{
		AnimatedWaveLodSlice slice =
			_slices[lodIndex];

		return
			slice.WorldSize *
			(uv -
			 new Vector2(
				 0.5f,
				 0.5f)) +
			slice.CenterXZ;
	}


	private static float SnapDown(
		float value,
		float step)
	{
		return
			MathF.Floor(
				value /
				step) *
			step;
	}
}
