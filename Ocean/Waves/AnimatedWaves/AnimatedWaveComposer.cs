using System;
using Godot;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// Owns and composes the canonical AnimatedWaveField.
///
/// Crest Animated Waves equivalent:
///
///     wave inputs
///         -> direct per-LOD wave buffer
///         -> coarse-to-fine ShapeCombine
///         -> final Animated Waves texture array
///
/// Current Stage A input:
///
///     FFT source
///
/// Pipeline:
///
///     Raw FFT displacement
///         -> AnimatedWaveFftDirectPass
///         -> AnimatedWaveDirectField
///         -> AnimatedWaveCombinePass
///         -> AnimatedWaveField
///
/// AnimatedWaveField remains the only authoritative final wave field
/// consumed by renderer, physics, queries, foam and FX.
/// </summary>
internal sealed class AnimatedWaveComposer : IDisposable
{
	private RenderingDevice _rd;


	/// <summary>
	/// Canonical cumulative final Animated Waves field.
	/// </summary>
	public AnimatedWaveField Field { get; private set; }


	/// <summary>
	/// Direct per-spatial-LOD wave contributions.
	///
	/// Crest equivalent:
	///
	///     LodDataMgrAnimWaves._waveBuffers
	///
	/// This is internal composition storage and is NOT authoritative
	/// wave data for external consumers.
	/// </summary>
	public AnimatedWaveDirectField DirectField { get; private set; }


	/// <summary>
	/// Authoritative spatial Animated Waves LOD layout used by
	/// composition and GPU LOD metadata.
	/// </summary>
	public AnimatedWaveLodLayout LodLayout { get; private set; }


	/// <summary>
	/// GPU copy of the current Animated Waves spatial LOD metadata.
	///
	/// Crest equivalent:
	///
	///     _CrestCascadeData
	/// </summary>
	public AnimatedWaveLodGpuBuffer LodGpuBuffer { get; private set; }


	//
	// Stage A:
	//
	// FFT is one Animated Waves input.
	//

	private AnimatedWaveFftDirectPass _fftDirectPass;


	//
	// Crest ShapeCombine equivalent.
	//

	private AnimatedWaveCombinePass _combinePass;


	public bool IsInitialized =>
		Field != null &&
		DirectField != null &&
		LodLayout != null &&
		LodGpuBuffer != null;


	public void Initialize(
		RenderingDevice rd,
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


		_rd =
			rd;


		try
		{
			//
			// Canonical FINAL Animated Waves field.
			//
			// External consumers continue to use only this field.
			//

			Field =
				new AnimatedWaveField(
					rd,
					resolution,
					lodCount);


			//
			// Crest-equivalent direct Animated Waves buffer.
			//
			// Contains only the contribution assigned directly
			// to each spatial LOD before ShapeCombine.
			//

			DirectField =
				new AnimatedWaveDirectField(
					rd,
					resolution,
					lodCount);


			//
			// One shared spatial LOD description for:
			//
			// direct input placement,
			// ShapeCombine,
			// final AWF sampling,
			// physics queries.
			//

			LodLayout =
				new AnimatedWaveLodLayout(
					resolution,
					lodCount,
					baseWorldSize);


			LodGpuBuffer =
				new AnimatedWaveLodGpuBuffer(
					rd,
					LodLayout);
		}
		catch
		{
			Release();

			throw;
		}
	}


	/// <summary>
	/// Updates CPU spatial LOD metadata only.
	///
	/// Production composition normally updates the layout through
	/// ComposeFft(). This method remains available for existing
	/// diagnostic/runtime plumbing.
	/// </summary>
	public void UpdateLodLayout(
		Vector2 focusXZ,
		float worldScale = 1.0f)
	{
		if (LodLayout == null)
		{
			return;
		}


		LodLayout.Update(
			focusXZ,
			worldScale);
	}


	/// <summary>
	/// Connects the raw FFT displacement source as an Animated Waves input.
	///
	/// Crest equivalent:
	///
	///     ShapeWaves / WaveBatch
	///         -> direct Animated Waves wave buffer
	///         -> ShapeCombine
	///
	/// All GPU resources are persistent.
	/// </summary>
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


		if (Field == null ||
			DirectField == null ||
			LodLayout == null ||
			LodGpuBuffer == null)
		{
			throw new InvalidOperationException(
				"Animated Wave resources are incomplete.");
		}


		//
		// Consumers/passes borrow:
		//
		//     FFT displacement
		//     LodGpuBuffer
		//     DirectField
		//     Field
		//
		// Release passes before replacing them.
		//

		_combinePass?.Dispose();

		_combinePass =
			null;


		_fftDirectPass?.Dispose();

		_fftDirectPass =
			null;


		try
		{
			//
			// Crest WaveBatch + FilterWavelength + input shader
			// equivalent.
			//

			_fftDirectPass =
				new AnimatedWaveFftDirectPass(
					_rd,
					fftDisplacement,
					LodGpuBuffer.Buffer,
					DirectField.Displacement,
					Field.Resolution,
					Field.LodCount,
					fftCascadeCount,
					waveResolutionMultiplier);


			//
			// Crest ShapeCombine.compute equivalent.
			//

			_combinePass =
				new AnimatedWaveCombinePass(
					_rd,
					DirectField.Displacement,
					LodGpuBuffer.Buffer,
					Field.Displacement,
					Field.Resolution,
					Field.LodCount);
		}
		catch
		{
			_combinePass?.Dispose();

			_combinePass =
				null;


			_fftDirectPass?.Dispose();

			_fftDirectPass =
				null;


			throw;
		}
	}


	/// <summary>
	/// Transitional overload retained so the existing OceanRuntime
	/// continues to compile until its single Stage A call-site change.
	///
	/// At the current minimum whole-ocean scale the Crest LOD scale
	/// transition alpha is zero.
	///
	/// The next Stage A file change will pass the actual
	/// lodScaleAlpha explicitly.
	/// </summary>
	public void ComposeFft(
		Vector2 focusXZ,
		float worldScale = 1.0f)
	{
		ComposeFft(
			focusXZ,
			worldScale,
			0.0f);
	}


	/// <summary>
	/// Composes the canonical AnimatedWaveField using the Crest
	/// Animated Waves data flow.
	///
	/// Exact order:
	///
	///     1. Update spatial LOD layout.
	///     2. Upload CascadeParams-equivalent metadata once.
	///     3. Raw FFT -> direct per-LOD contributions.
	///     4. GPU dependency barrier.
	///     5. Coarse-to-fine ShapeCombine.
	///
	/// After this method returns from CPU command recording,
	/// AnimatedWaveField is the canonical final field for this update.
	/// </summary>
	public void ComposeFft(
		Vector2 focusXZ,
		float worldScale,
		float lodScaleAlpha)
	{
		if (_fftDirectPass == null ||
			_combinePass == null ||
			LodLayout == null ||
			LodGpuBuffer == null ||
			DirectField == null ||
			Field == null)
		{
			return;
		}


		if (!float.IsFinite(
				lodScaleAlpha))
		{
			throw new ArgumentOutOfRangeException(
				nameof(lodScaleAlpha));
		}


		lodScaleAlpha =
			Mathf.Clamp(
				lodScaleAlpha,
				0.0f,
				1.0f);


		//
		// 1. Update the ONE spatial Animated Waves layout.
		//
		// Centres remain snapped independently to each LOD's texel
		// width, matching Crest LodTransform.
		//

		LodLayout.Update(
			focusXZ,
			worldScale);


		//
		// 2. Upload spatial LOD metadata exactly once.
		//
		// Both the direct-input pass and ShapeCombine consume this
		// same buffer.
		//

		LodGpuBuffer.Upload(
			LodLayout);


		//
		// 3. FFT Animated-Wave input.
		//
		// Writes:
		//
		//     DirectField[L]
		//
		// Each spatial LOD receives only its directly assigned
		// wavelength content.
		//

		_fftDirectPass.Dispatch(
			lodScaleAlpha);


		


		//
		// 4. Crest ShapeCombine:
		//
		//     Final[last] = Direct[last]
		//
		//     Final[L] =
		//         Direct[L]
		//         +
		//         Resample(Final[L + 1])
		//
		// AnimatedWaveCombinePass itself inserts compute barriers
		// between dependent neighbouring LOD dispatches.
		//

		_combinePass.Dispatch();
	}


	public void Release()
	{
		//
		// Release GPU passes first because they borrow all resources
		// below.
		//

		_combinePass?.Dispose();

		_combinePass =
			null;


		_fftDirectPass?.Dispose();

		_fftDirectPass =
			null;


		//
		// Then release persistent composition storage.
		//

		DirectField?.Dispose();

		DirectField =
			null;


		LodGpuBuffer?.Dispose();

		LodGpuBuffer =
			null;


		Field?.Dispose();

		Field =
			null;


		LodLayout =
			null;


		_rd =
			null;
	}


	public void Dispose()
	{
		Release();
	}
}
