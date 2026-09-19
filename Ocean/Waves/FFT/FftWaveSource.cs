using System;
using Godot;

namespace OceanFrontier.Water.Waves.FFT;

internal sealed class FftWaveSource
{
	private RenderingDevice _rd;

	private FftGpuResources _resources;

	private FftSpectrumInitPass _spectrumInitPass;
	private FftSpectrumUpdatePass _spectrumUpdatePass;
	private FftTransformPass _fftTransformPass;

	public bool IsInitialized =>
		_resources != null;

	public int Resolution =>
		_resources?.Resolution ?? 0;

	public int CascadeCount =>
		_resources?.CascadeCount ?? 0;

		

	/// <summary>
	/// Raw spatial FFT displacement.
	///
	/// This texture is not populated until the future IFFT stage.
	/// It is NOT the canonical AnimatedWaveField.
	/// </summary>
	internal Rid RawDisplacement =>
		_resources?.Displacement ?? default;

	public void Initialize(
		RenderingDevice rd,
		int resolution,
		int cascadeCount,
		int spectrumBandCount)
	{
		if (rd == null)
		{
			throw new ArgumentNullException(nameof(rd));
		}

		Release();

		_rd = rd;

		try
		{
			_resources =
				new FftGpuResources(
					rd,
					resolution,
					cascadeCount);

			_spectrumInitPass =
				new FftSpectrumInitPass(
					rd,
					_resources,
					spectrumBandCount);

			_spectrumUpdatePass =
				new FftSpectrumUpdatePass(
					rd,
					_resources);
			
			_fftTransformPass =
				new FftTransformPass(
	   			 rd,
				_resources);		
		}
		catch
		{
			Release();
			throw;
		}
	}

	/// <summary>
	/// Generates persistent H0(k).
	/// Call only when spectrum-generation parameters change.
	/// </summary>
	public void InitializeSpectrum(
		in FftSpectrumInitSettings settings,
		float[] linearPowerControls)
	{
		if (_spectrumInitPass == null)
		{
			throw new InvalidOperationException(
				"FFT wave source is not initialized.");
		}

		_spectrumInitPass.Dispatch(
			settings,
			linearPowerControls);
	}

	/// <summary>
	/// Evolves H0 into H(k,t).
	/// One compute dispatch per simulation update.
	/// </summary>
	public void UpdateSpectrum(
		float simulationTime,
		float chop,
		float gravity,
		float loopPeriod)
	{
		if (_spectrumUpdatePass == null)
		{
			return;
		}

		_spectrumUpdatePass.Dispatch(
			simulationTime,
			chop,
			gravity,
			loopPeriod);
	}

	public void TransformSpectrumToDisplacement()
	{
		if (_fftTransformPass == null)
		{
			return;
		}

		_fftTransformPass.Dispatch();
	}

	public void Release()
	{	

		_fftTransformPass?.Dispose();
		_fftTransformPass = null;

		_spectrumUpdatePass?.Dispose();
		_spectrumUpdatePass = null;

		_spectrumInitPass?.Dispose();
		_spectrumInitPass = null;

		_resources?.Dispose();
		_resources = null;

		_rd = null;
	}
}
