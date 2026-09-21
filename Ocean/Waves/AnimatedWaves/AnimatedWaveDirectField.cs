using System;
using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// Persistent GPU storage for direct Animated Waves contributions.
///
/// Crest equivalent:
///
///     LodDataMgrAnimWaves._waveBuffers
///
/// This is NOT the canonical final wave field.
///
/// Contract:
///
///     wave inputs
///         -> AnimatedWaveDirectField
///         -> coarse-to-fine combine
///         -> AnimatedWaveField
///
/// Each array layer contains only the wave content assigned
/// directly to that spatial Animated Wave LOD.
///
/// It must not contain cumulative contributions from coarser LODs.
/// Cumulative composition is performed later by the ShapeCombine
/// equivalent.
///
/// XYZ = direct displacement contribution.
/// A   = reserved for variance / future wave energy data.
///
/// AnimatedWaveField remains the only authoritative final field
/// consumed by renderer, physics, queries, foam and FX.
/// </summary>
internal sealed class AnimatedWaveDirectField : IDisposable
{
	private const RenderingDevice.TextureUsageBits Usage =
		RenderingDevice.TextureUsageBits.SamplingBit |
		RenderingDevice.TextureUsageBits.StorageBit;


	private readonly RenderingDevice _rd;


	public int Resolution { get; }

	public int LodCount { get; }


	/// <summary>
	/// Per-spatial-LOD direct wave contributions.
	///
	/// This texture corresponds to Crest's Animated Waves
	/// wave buffer before ShapeCombine.
	/// </summary>
	public Rid Displacement { get; private set; }


	public AnimatedWaveDirectField(
		RenderingDevice rd,
		int resolution,
		int lodCount)
	{
		_rd =
			rd ??
			throw new ArgumentNullException(
				nameof(rd));


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


		Resolution =
			resolution;

		LodCount =
			lodCount;


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
		//
		// Match the canonical AnimatedWaveField format.
		//
		// Crest also keeps the direct wave buffer and final
		// Animated Waves data in compatible displacement formats
		// because ShapeCombine reads one and writes the other.
		//

		const RenderingDevice.DataFormat format =
			RenderingDevice.DataFormat
				.R16G16B16A16Sfloat;


		if (!_rd.TextureIsFormatSupportedForUsage(
				format,
				Usage))
		{
			throw new NotSupportedException(
				$"Animated Wave direct-field format {format} " +
				"is not supported with required usage.");
		}


		var textureFormat =
			new RDTextureFormat
			{
				Format =
					format,


				Width =
					(uint)Resolution,

				Height =
					(uint)Resolution,


				Depth =
					1,

				ArrayLayers =
					(uint)LodCount,

				Mipmaps =
					1,


				TextureType =
					RenderingDevice
						.TextureType
						.Type2DArray,


				UsageBits =
					Usage,
			};


		Displacement =
			_rd.TextureCreate(
				textureFormat,
				new RDTextureView());


		if (!Displacement.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create Animated Wave direct field.");
		}
	}


	public void Dispose()
	{
		if (Displacement.IsValid)
		{
			_rd.FreeRid(
				Displacement);


			Displacement =
				default;
		}
	}
}
