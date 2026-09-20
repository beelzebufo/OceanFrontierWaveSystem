using System;
using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// Owns and will compose the canonical AnimatedWaveField.
///
/// Stage 2A:
/// - owns final field;
/// - no inputs yet;
/// - no composition dispatch yet.
///
/// Later:
/// FFT sources / local inputs
///     -> AnimatedWaveComposer
///     -> AnimatedWaveField
/// </summary>
internal sealed class AnimatedWaveComposer : IDisposable
{
	private RenderingDevice _rd;

	public AnimatedWaveField Field { get; private set; }

	public AnimatedWaveLodLayout LodLayout { get; private set; }

	public AnimatedWaveLodGpuBuffer LodGpuBuffer { get; private set; }

	private AnimatedWaveFftComposePass _fftComposePass;

	public bool IsInitialized =>
		Field != null;

	public void Initialize(	RenderingDevice rd, 
							int resolution,
							int lodCount,
							float baseWorldSize)
	{
		if (rd == null)
		{
			throw new ArgumentNullException(
				nameof(rd));
		}

		Release();

		_rd = rd;

		try
		{
			Field = new AnimatedWaveField(
						rd,
						resolution,
						lodCount);

			LodLayout = new AnimatedWaveLodLayout(
						resolution,
						lodCount,
						baseWorldSize);

			LodGpuBuffer = new AnimatedWaveLodGpuBuffer(
							rd,
							LodLayout);
		}
		catch
		{
			Release();
			throw;
		}
	}

	public void UpdateLodLayout( Vector2 focusXZ, float worldScale = 1.0f)
	{
		if (LodLayout == null)
		{
			return;
		}

		LodLayout.Update( focusXZ, worldScale);
	}

	public void InitializeFftInput(
		Rid fftDisplacement,
		int fftCascadeCount,
		float waveResolutionMultiplier)
	{
		if (_rd == null)
		{
			throw new InvalidOperationException(
				"AnimatedWaveComposer is not initialized.");
		}

		if (Field == null || LodLayout == null || LodGpuBuffer == null)
		{
			throw new InvalidOperationException(
				"Animated Wave resources are incomplete.");
		}

		_fftComposePass?.Dispose();

		_fftComposePass = new AnimatedWaveFftComposePass(
					_rd,
					fftDisplacement,
					LodGpuBuffer.Buffer,
					Field.Displacement,
					Field.Resolution,
					Field.LodCount,
					fftCascadeCount,
					waveResolutionMultiplier);
	}

	public void ComposeFft( Vector2 focusXZ, float worldScale = 1.0f)
	{
		if (_fftComposePass == null || LodLayout == null || LodGpuBuffer == null)
		{
			return;
		}

		LodLayout.Update(focusXZ, worldScale);

		LodGpuBuffer.Upload(LodLayout);

		_fftComposePass.Dispatch();
	}



	public void Release()
	{	

		_fftComposePass?.Dispose();
		_fftComposePass = null;
		
		LodGpuBuffer?.Dispose();
		LodGpuBuffer = null;
		
		Field?.Dispose();
		Field = null;

		LodLayout = null;

		_rd = null;
	}

	public void Dispose()
	{
		Release();
	}


}
