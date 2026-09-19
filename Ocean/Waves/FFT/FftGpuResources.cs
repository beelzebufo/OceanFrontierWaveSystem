using System;
using Godot;
using Godot.Collections;

namespace OceanFrontier.Water.Waves.FFT;

/// <summary>
/// Owns all persistent GPU textures required by the FFT wave source.
///
/// Important:
/// - Must be created and destroyed on the render thread.
/// - Does not execute FFT.
/// - Does not own spectrum mathematics.
/// - Does not expose a final AnimatedWaveField.
/// </summary>
internal sealed class FftGpuResources : IDisposable
{
	private const RenderingDevice.TextureUsageBits TextureUsage =
		RenderingDevice.TextureUsageBits.SamplingBit |
		RenderingDevice.TextureUsageBits.StorageBit;

	private readonly RenderingDevice _rd;

	public int Resolution { get; }
	public int CascadeCount { get; }
	public int FftPassCount { get; }

	public Rid Butterfly { get; private set; }

	// H0(k), H0(-k).
	public Rid SpectrumInitial { get; private set; }

	// Time-evolved complex spectra.
	public Rid SpectrumHeight { get; private set; }
	public Rid SpectrumDisplaceX { get; private set; }
	public Rid SpectrumDisplaceZ { get; private set; }

	// Intermediate IFFT buffers.
	public Rid FftTempHeight { get; private set; }
	public Rid FftTempX { get; private set; }
	public Rid FftTempZ { get; private set; }

	// Raw FFT displacement source.
	//
	// XYZ:
	// X = horizontal displacement X
	// Y = vertical displacement
	// Z = horizontal displacement Z
	//
	// This is NOT the canonical AnimatedWaveField.
	public Rid Displacement { get; private set; }

	public FftGpuResources(
		RenderingDevice rd,
		int resolution,
		int cascadeCount)
	{
		_rd = rd ?? throw new ArgumentNullException(nameof(rd));

		if (!IsPowerOfTwo(resolution))
		{
			throw new ArgumentException(
				"FFT resolution must be a power of two.",
				nameof(resolution));
		}

		if (resolution < 8)
		{
			throw new ArgumentOutOfRangeException(
				nameof(resolution),
				"FFT resolution must be at least 8.");
		}

		if (cascadeCount <= 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(cascadeCount),
				"Cascade count must be greater than zero.");
		}

		Resolution = resolution;
		CascadeCount = cascadeCount;
		FftPassCount =FftButterflyTable.GetPassCount(resolution);

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
		// Initial spectrum stores positive/negative complex amplitudes:
		//
		// RG = H0(k)
		// BA = H0(-k)
		SpectrumInitial = CreateTextureArray(
			RenderingDevice.DataFormat.R32G32B32A32Sfloat);

		// Complex frequency-domain fields.
		SpectrumHeight = CreateTextureArray(
			RenderingDevice.DataFormat.R32G32Sfloat);

		SpectrumDisplaceX = CreateTextureArray(
			RenderingDevice.DataFormat.R32G32Sfloat);

		SpectrumDisplaceZ = CreateTextureArray(
			RenderingDevice.DataFormat.R32G32Sfloat);

		// IFFT intermediates.
		FftTempHeight = CreateTextureArray(
			RenderingDevice.DataFormat.R32G32Sfloat);

		FftTempX = CreateTextureArray(
			RenderingDevice.DataFormat.R32G32Sfloat);

		FftTempZ = CreateTextureArray(
			RenderingDevice.DataFormat.R32G32Sfloat);

		// Spatial-domain displacement.
		Displacement = CreateTextureArray(
			RenderingDevice.DataFormat.R16G16B16A16Sfloat);

		Butterfly = CreateButterflyTexture();	

		GD.Print(
			$"[Ocean] GPU resources created; Butterfly={Resolution}x{FftPassCount}, " +
			$"FftPassCount={FftPassCount}");
	}

	private Rid CreateTextureArray(RenderingDevice.DataFormat format)
	{
		if (!_rd.TextureIsFormatSupportedForUsage(format, TextureUsage))
		{
			throw new NotSupportedException(
				$"RenderingDevice does not support {format} " +
				$"with required FFT texture usage flags.");
		}

		var textureFormat = new RDTextureFormat
		{
			Format = format,
			Width = (uint)Resolution,
			Height = (uint)Resolution,
			Depth = 1,
			ArrayLayers = (uint)CascadeCount,
			Mipmaps = 1,
			TextureType = RenderingDevice.TextureType.Type2DArray,
			UsageBits = TextureUsage,
		};

		Rid texture = _rd.TextureCreate(
			textureFormat,
			new RDTextureView());

		if (!texture.IsValid)
		{
			throw new InvalidOperationException(
				$"Failed to create FFT texture: {format}, " +
				$"{Resolution}x{Resolution}x{CascadeCount}.");
		}

		return texture;
	}

	private Rid CreateButterflyTexture()
	{
		const RenderingDevice.DataFormat format =
			RenderingDevice.DataFormat.R32G32Sfloat;

		const RenderingDevice.TextureUsageBits usage =
			RenderingDevice.TextureUsageBits.SamplingBit;

		if (!_rd.TextureIsFormatSupportedForUsage(format,usage))
		{
			throw new NotSupportedException(
				$"RenderingDevice does not support {format} " +
				"for FFT butterfly storage-image usage.");
		}

		byte[] butterflyData =
			FftButterflyTable.GenerateRg32Float(
				Resolution);

		int expectedByteCount =
			checked(
				Resolution *
				FftPassCount *
				2 *
				sizeof(float));

		if (butterflyData.Length != expectedByteCount)
		{
			throw new InvalidOperationException(
				"FFT butterfly table byte size mismatch.");
		}

		var textureFormat =
			new RDTextureFormat
			{
				Format = format,

				Width =
					(uint)Resolution,

				Height =
					(uint)FftPassCount,

				Depth = 1,
				ArrayLayers = 1,
				Mipmaps = 1,

				TextureType =RenderingDevice.TextureType.Type2D,

				UsageBits = usage,
			};

		// Godot expects one byte block for a normal 2D texture.
		var initialData =
			new Array<byte[]>();

		initialData.Add(
			butterflyData);

		Rid texture =
			_rd.TextureCreate(
			textureFormat,
			new RDTextureView(),
			initialData);

		if (!texture.IsValid)
		{
			throw new InvalidOperationException(
		   		$"Failed to create FFT butterfly texture " +
				$"{Resolution}x{FftPassCount}.");
		}

		return texture;
	}


	public void Dispose()
	{	

		Free(Butterfly);
		Butterfly = default;
		
		Free(Displacement);
		Displacement = default;

		Free(FftTempZ);
		FftTempZ = default;

		Free(FftTempX);
		FftTempX = default;

		Free(FftTempHeight);
		FftTempHeight = default;

		Free(SpectrumDisplaceZ);
		SpectrumDisplaceZ = default;

		Free(SpectrumDisplaceX);
		SpectrumDisplaceX = default;

		Free(SpectrumHeight);
		SpectrumHeight = default;

		Free(SpectrumInitial);
		SpectrumInitial = default;
	}

	private void Free(Rid rid)
	{
		if (!rid.IsValid)
		{
			return;
		}

		_rd.FreeRid(rid);
	}

	private static bool IsPowerOfTwo(int value)
	{
		return value > 0 && (value & (value - 1)) == 0;
	}
}
