using System;
using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// Canonical final wave field.
///
/// All future consumers must read wave displacement from this field,
/// not directly from FFT sources.
///
/// Stage 2A only owns persistent GPU storage.
/// Composition is implemented later.
/// </summary>
internal sealed class AnimatedWaveField : IDisposable
{
	private const RenderingDevice.TextureUsageBits Usage =
		RenderingDevice.TextureUsageBits.SamplingBit |
		RenderingDevice.TextureUsageBits.StorageBit;

	private readonly RenderingDevice _rd;

	public int Resolution { get; }
	public int LodCount { get; }

	/// <summary>
	/// XYZ = final displacement.
	/// A   = reserved for wave variance / future energy data.
	/// </summary>
	public Rid Displacement { get; private set; }

	public AnimatedWaveField(
		RenderingDevice rd,
		int resolution,
		int lodCount)
	{
		_rd = rd ??
			throw new ArgumentNullException(nameof(rd));

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

		Resolution = resolution;
		LodCount = lodCount;

		try
		{
			Create();
		}
		catch
		{
			Dispose();
			throw;
		}
	}

	private void Create()
	{
		const RenderingDevice.DataFormat format =
			RenderingDevice.DataFormat.R16G16B16A16Sfloat;

		if (!_rd.TextureIsFormatSupportedForUsage(
				format,
				Usage))
		{
			throw new NotSupportedException(
				$"AnimatedWaveField format {format} " +
				"is not supported with required usage.");
		}

		var textureFormat =
			new RDTextureFormat
			{
				Format = format,

				Width = (uint)Resolution,
				Height = (uint)Resolution,

				Depth = 1,
				ArrayLayers = (uint)LodCount,
				Mipmaps = 1,

				TextureType =
					RenderingDevice.TextureType.Type2DArray,

				UsageBits = Usage,
			};

		Displacement =
			_rd.TextureCreate(
				textureFormat,
				new RDTextureView());

		if (!Displacement.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create canonical AnimatedWaveField.");
		}
	}

	public void Dispose()
	{
		if (Displacement.IsValid)
		{
			_rd.FreeRid(Displacement);
			Displacement = default;
		}
	}
}
