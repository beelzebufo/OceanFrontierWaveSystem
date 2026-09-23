using System;
using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// Persistent GPU cache derived from the canonical AnimatedWaveField.
/// RGB = geometric surface normal; A = raw horizontal Jacobian determinant.
/// This field is never an independent source of ocean displacement.
/// </summary>
internal sealed class AnimatedWaveDerivativeField : IDisposable
{
	private const RenderingDevice.TextureUsageBits Usage =
		RenderingDevice.TextureUsageBits.SamplingBit |
		RenderingDevice.TextureUsageBits.StorageBit;

	private readonly RenderingDevice _rd;

	public int Resolution { get; }
	public int LodCount { get; }
	public Rid NormalJacobian { get; private set; }

	public AnimatedWaveDerivativeField(
		RenderingDevice rd,
		int resolution,
		int lodCount)
	{
		_rd = rd ?? throw new ArgumentNullException(nameof(rd));

		if (resolution <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(resolution));
		}

		if (lodCount <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(lodCount));
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

		if (!_rd.TextureIsFormatSupportedForUsage(format, Usage))
		{
			throw new NotSupportedException(
				$"AnimatedWaveDerivativeField format {format} " +
				"is not supported with required usage.");
		}

		var textureFormat = new RDTextureFormat
		{
			Format = format,
			Width = (uint)Resolution,
			Height = (uint)Resolution,
			Depth = 1,
			ArrayLayers = (uint)LodCount,
			Mipmaps = 1,
			TextureType = RenderingDevice.TextureType.Type2DArray,
			UsageBits = Usage,
		};

		NormalJacobian = _rd.TextureCreate(
			textureFormat,
			new RDTextureView());

		if (!NormalJacobian.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create AnimatedWaveDerivativeField.");
		}

		GD.Print(
			$"[Ocean] AnimatedWaveDerivativeField allocated once: " +
			$"{Resolution}x{Resolution}x{LodCount}, RGBA16F, RID {NormalJacobian.Id}.");
	}

	public void Dispose()
	{
		if (NormalJacobian.IsValid)
		{
			_rd.FreeRid(NormalJacobian);
			NormalJacobian = default;
		}
	}
}
