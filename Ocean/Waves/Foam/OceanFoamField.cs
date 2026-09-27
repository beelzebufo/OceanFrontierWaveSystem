using System;
using Godot;
using Godot.Collections;

namespace OceanFrontier.Water.Waves.Foam;

/// <summary>
/// Persistent Foam-1A scalar field. Exactly two R16F Texture2DArray resources
/// are allocated and then ping-ponged for the lifetime of the simulation.
/// </summary>
internal sealed class OceanFoamField : IDisposable
{
	private const RenderingDevice.TextureUsageBits Usage =
		RenderingDevice.TextureUsageBits.SamplingBit |
		RenderingDevice.TextureUsageBits.StorageBit;

	private readonly RenderingDevice _rd;
	private readonly Rid[] _textures = new Rid[2];
	private int _latestIndex;

	public int Resolution { get; }
	public int LodCount { get; }

	public Rid Latest => _textures[_latestIndex];
	public Rid WriteTarget => _textures[1 - _latestIndex];

	internal Rid Texture0 => _textures[0];
	internal Rid Texture1 => _textures[1];

	public OceanFoamField(
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
			RenderingDevice.DataFormat.R16Sfloat;

		if (!_rd.TextureIsFormatSupportedForUsage(format, Usage))
		{
			throw new NotSupportedException(
				$"OceanFoamField format {format} is not supported " +
				"with sampling and storage usage.");
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

		// Texture contents are otherwise undefined. Supply one zeroed R16F
		// block per array layer once at allocation time.
		byte[] zeroLayer = new byte[
			checked(Resolution * Resolution * sizeof(ushort))];

		var initialData = new Array<byte[]>();
		for (int lod = 0; lod < LodCount; lod++)
		{
			initialData.Add(zeroLayer);
		}

		_textures[0] = _rd.TextureCreate(
			textureFormat,
			new RDTextureView(),
			initialData);

		_textures[1] = _rd.TextureCreate(
			textureFormat,
			new RDTextureView(),
			initialData);

		if (!_textures[0].IsValid || !_textures[1].IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create the two persistent OceanFoamField textures.");
		}

		GD.Print(
			$"[Ocean] Foam-1A field allocated once: two " +
			$"{Resolution}x{Resolution}x{LodCount} R16F Texture2DArray resources.");
	}

	internal void CommitWrite()
	{
		_latestIndex = 1 - _latestIndex;
	}

	public void Dispose()
	{
		for (int index = 0; index < _textures.Length; index++)
		{
			if (_textures[index].IsValid)
			{
				_rd.FreeRid(_textures[index]);
				_textures[index] = default;
			}
		}
	}
}
