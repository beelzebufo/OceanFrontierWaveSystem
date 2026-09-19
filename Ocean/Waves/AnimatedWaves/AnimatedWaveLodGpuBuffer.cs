using System;
using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// Persistent GPU metadata for AnimatedWaveField LOD slices.
///
/// Layout intentionally follows Crest CascadeParams closely,
/// but this is NOT an FFT cascade buffer.
///
/// std430 layout:
///
/// float2 centerXZ;
/// float  scale;
/// float  textureResolution;
///
/// float  oneOverTextureResolution;
/// float  texelWidth;
/// float  weight;
/// float  maxWavelength;
///
/// Total: 8 floats = 32 bytes per LOD.
/// </summary>
internal sealed class AnimatedWaveLodGpuBuffer : IDisposable
{
	public const int FloatsPerLod = 8;
	public const int StrideBytes =
		FloatsPerLod * sizeof(float);

	private readonly RenderingDevice _rd;

	private readonly float[] _uploadFloats;
	private readonly byte[] _uploadBytes;

	public Rid Buffer { get; private set; }

	public int LodCount { get; }

	public int ByteSize =>
		_uploadBytes.Length;


	public AnimatedWaveLodGpuBuffer(
		RenderingDevice rd,
		AnimatedWaveLodLayout layout)
	{
		_rd = rd ??
			throw new ArgumentNullException(
				nameof(rd));

		if (layout == null)
		{
			throw new ArgumentNullException(
				nameof(layout));
		}

		LodCount =
			layout.LodCount;

		_uploadFloats =
			new float[
				LodCount *
				FloatsPerLod];

		_uploadBytes =
			new byte[
				LodCount *
				StrideBytes];

		FillUploadData(
			layout);

		Buffer =
			_rd.StorageBufferCreate(
				(uint)_uploadBytes.Length,
				_uploadBytes);

		if (!Buffer.IsValid)
		{
			throw new InvalidOperationException(
				"Failed to create Animated Wave LOD GPU buffer.");
		}
	}


	/// <summary>
	/// Uploads current CPU LOD metadata.
	///
	/// Must be called on the render thread and outside
	/// an active draw/compute list.
	///
	/// Stage 2C creates this method but does not call it
	/// every frame yet.
	/// </summary>
	public void Upload(
		AnimatedWaveLodLayout layout)
	{
		if (layout == null)
		{
			throw new ArgumentNullException(
				nameof(layout));
		}

		if (layout.LodCount != LodCount)
		{
			throw new InvalidOperationException(
				"Animated Wave LOD count changed after GPU buffer creation.");
		}

		FillUploadData(
			layout);

		Error error =
			_rd.BufferUpdate(
				Buffer,
				0,
				(uint)_uploadBytes.Length,
				_uploadBytes);

		if (error != Error.Ok)
		{
			throw new InvalidOperationException(
				$"Animated Wave LOD GPU buffer update failed: {error}.");
		}
	}


	private void FillUploadData(
		AnimatedWaveLodLayout layout)
	{
		for (int lod = 0;
			 lod < LodCount;
			 lod++)
		{
			AnimatedWaveLodSlice slice =
				layout[lod];

			int offset =
				lod *
				FloatsPerLod;

			// Crest relation:
			//
			// worldSize = 4 * scale
			//
			// Therefore:
			//
			// scale = worldSize / 4

			float scale =
				slice.WorldSize *
				0.25f;

			_uploadFloats[offset + 0] =
				slice.CenterXZ.X;

			_uploadFloats[offset + 1] =
				slice.CenterXZ.Y;

			_uploadFloats[offset + 2] =
				scale;

			_uploadFloats[offset + 3] =
				layout.Resolution;

			_uploadFloats[offset + 4] =
				1.0f /
				layout.Resolution;

			_uploadFloats[offset + 5] =
				slice.TexelWidth;

			// No last-LOD transition yet.
			_uploadFloats[offset + 6] =
				1.0f;

			_uploadFloats[offset + 7] =
				slice.MaxWavelength;
		}

		System.Buffer.BlockCopy(
			_uploadFloats,
			0,
			_uploadBytes,
			0,
			_uploadBytes.Length);
	}


	public void Dispose()
	{
		if (Buffer.IsValid)
		{
			_rd.FreeRid(
				Buffer);

			Buffer =
				default;
		}
	}
}
