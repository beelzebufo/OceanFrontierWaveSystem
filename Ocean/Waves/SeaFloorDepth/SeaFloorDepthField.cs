using System;
using Godot;

namespace OceanFrontier.Water.Waves.SeaFloorDepth;

internal sealed class SeaFloorDepthField : IDisposable
{
	public const float DeepSentinel = -65504.0f;
	private const RenderingDevice.TextureUsageBits Usage =
		RenderingDevice.TextureUsageBits.SamplingBit |
		RenderingDevice.TextureUsageBits.StorageBit;

	private readonly RenderingDevice _rd;
	public int Resolution { get; }
	public int LodCount { get; }
	public Rid Height { get; private set; }

	public SeaFloorDepthField(RenderingDevice rd, int resolution, int lodCount)
	{
		_rd = rd ?? throw new ArgumentNullException(nameof(rd));
		if (resolution <= 0) throw new ArgumentOutOfRangeException(nameof(resolution));
		if (lodCount <= 0) throw new ArgumentOutOfRangeException(nameof(lodCount));
		Resolution = resolution;
		LodCount = lodCount;
		Create();
	}

	private void Create()
	{
		const RenderingDevice.DataFormat format = RenderingDevice.DataFormat.R16Sfloat;
		if (!_rd.TextureIsFormatSupportedForUsage(format, Usage))
			throw new NotSupportedException($"SeaFloorDepthField format {format} is not supported with required usage.");

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

		Height = _rd.TextureCreate(textureFormat, new RDTextureView());
		if (!Height.IsValid) throw new InvalidOperationException("Failed to create SeaFloorDepthField.");
	}

	public void Dispose()
	{
		if (!Height.IsValid) return;
		_rd.FreeRid(Height);
		Height = default;
	}
}
