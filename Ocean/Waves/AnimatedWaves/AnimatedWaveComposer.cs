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
/// Current inputs:
///
///     FFT source
///     ordered rectangular constant-displacement inputs
///
/// Pipeline:
///
///     Raw FFT displacement
///         -> AnimatedWaveFftDirectPass
///         -> AnimatedWaveDirectField
///         -> optional pre-combine input pass
///         -> AnimatedWaveCombinePass
///         -> AnimatedWaveField
///         -> optional post-combine input pass
///
/// Spatial-state contract:
///
///     AnimatedWaveLodLayout
///         -> GPU CascadeParams-equivalent buffer
///         -> AnimatedWaveField composition
///         -> committed AnimatedWaveRenderState
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
	/// Authoritative working spatial Animated Waves LOD layout used by
	/// composition and GPU LOD metadata.
	///
	/// This object is mutated on the render thread while preparing the
	/// next AnimatedWaveField generation.
	///
	/// Main-thread consumers must not use it directly.
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


	/// <summary>
	/// CPU-side committed spatial state belonging to the canonical
	/// AnimatedWaveField.
	///
	/// Crest equivalent:
	///
	///     LodTransform.RenderData.Current
	///
	/// Consumers must read this state instead of independently
	/// recalculating LOD slices from current camera/pending values.
	/// </summary>
	public AnimatedWaveRenderState RenderState { get; private set; }


	//
	// FFT is currently one Animated Waves input.
	//

	private AnimatedWaveFftDirectPass _fftDirectPass;


	//
	// Crest ShapeCombine equivalent.
	//

	private AnimatedWaveCombinePass _combinePass;


	//
	// Ordered generic Animated Waves inputs.
	//

	private AnimatedWaveInputPass _inputPass;


	internal int ActiveInputCount =>
		_inputPass?.ActiveInputCount ?? 0;

	internal long InputDispatchCount =>
		_inputPass?.DispatchCount ?? 0;


	public bool IsInitialized =>
		Field != null &&
		DirectField != null &&
		LodLayout != null &&
		LodGpuBuffer != null &&
		RenderState != null &&
		_inputPass != null;


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

			Field =
				new AnimatedWaveField(
					rd,
					resolution,
					lodCount);


			//
			// Crest-equivalent direct Animated Waves buffer.
			//

			DirectField =
				new AnimatedWaveDirectField(
					rd,
					resolution,
					lodCount);


			//
			// Working render-thread spatial layout.
			//
			// Crest equivalent:
			//
			//     LodTransform.RenderData.Current
			//
			// before it is written into CascadeParams.
			//

			LodLayout =
				new AnimatedWaveLodLayout(
					resolution,
					lodCount,
					baseWorldSize);


			//
			// GPU CascadeParams equivalent.
			//

			LodGpuBuffer =
				new AnimatedWaveLodGpuBuffer(
					rd,
					LodLayout);


			//
			// CPU committed snapshot for renderer/debug consumers.
			//
			// This does not calculate spatial state.
			// It only copies the exact layout used by composition.
			//

			RenderState =
				new AnimatedWaveRenderState(
					lodCount);


			_inputPass =
				new AnimatedWaveInputPass(
					rd,
					LodGpuBuffer.Buffer,
					DirectField.Displacement,
					Field.Displacement,
					resolution,
					lodCount);
		}
		catch
		{
			Release();

			throw;
		}
	}


	/// <summary>
	/// Updates CPU working spatial LOD metadata only.
	///
	/// Production composition normally updates the layout through
	/// ComposeFft().
	///
	/// This does NOT publish RenderState and therefore does not change
	/// the spatial contract of the currently committed AnimatedWaveField.
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
			LodGpuBuffer == null ||
			RenderState == null)
		{
			throw new InvalidOperationException(
				"Animated Wave resources are incomplete.");
		}


		//
		// Passes borrow:
		//
		//     FFT displacement
		//     LodGpuBuffer
		//     DirectField
		//     Field
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
	/// Transitional overload retained for callers which do not supply
	/// whole-stack LOD transition alpha.
	/// </summary>
	public void ComposeFft(
		Vector2 focusXZ,
		float worldScale = 1.0f)
	{
		ComposeFft(
			focusXZ,
			worldScale,
			0.0f,
			ReadOnlySpan<AnimatedWaveInputSnapshot>.Empty);
	}


	/// <summary>
	/// Composes the canonical AnimatedWaveField using the Crest
	/// Animated Waves data flow.
	///
	/// Exact order:
	///
	///     1. Update working spatial LOD layout.
	///     2. Upload CascadeParams-equivalent metadata once.
	///     3. Raw FFT -> direct per-LOD contributions.
	///     4. Coarse-to-fine ShapeCombine.
	///     5. Publish the exact same spatial state as committed
	///        AnimatedWaveRenderState.
	///
	/// The committed CPU state is never independently recalculated.
	/// </summary>
	public void ComposeFft(
		Vector2 focusXZ,
		float worldScale,
		float lodScaleAlpha)
	{
		ComposeFft(
			focusXZ,
			worldScale,
			lodScaleAlpha,
			ReadOnlySpan<AnimatedWaveInputSnapshot>.Empty);
	}


	/// <summary>
	/// Composes FFT and ordered generic Animated Waves inputs.
	///
	/// Exact phase order follows Crest 4 LodDataMgrAnimWaves:
	/// wavelength/all-LOD pre inputs -> ShapeCombine -> all-LOD post inputs.
	/// </summary>
	public void ComposeFft(
		Vector2 focusXZ,
		float worldScale,
		float lodScaleAlpha,
		ReadOnlySpan<AnimatedWaveInputSnapshot> inputs)
	{
		if (_fftDirectPass == null ||
			_combinePass == null ||
			_inputPass == null ||
			LodLayout == null ||
			LodGpuBuffer == null ||
			DirectField == null ||
			Field == null ||
			RenderState == null)
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
		// 1. Update the ONE working spatial Animated Waves layout.
		//
		// Centres remain snapped independently to each LOD's texel
		// width, matching Crest LodTransform.
		//

		LodLayout.Update(
			focusXZ,
			worldScale);


		//
		// 2. Upload the exact same layout to GPU CascadeParams.
		//

		LodGpuBuffer.Upload(
			LodLayout);


		//
		// Upload the coherent, priority-sorted input snapshot once.
		// With zero inputs this performs no GPU buffer update.
		//

		_inputPass.Upload(
			inputs);


		//
		// 3. FFT Animated-Wave input.
		//
		// Writes DirectField[L] only.
		//

		_fftDirectPass.Dispatch(
			lodScaleAlpha);


		//
		// 3b. Inputs which belong to the direct field.
		//
		// This is exactly one dispatch when the phase has any active input.
		//

		_inputPass.DispatchPreCombine(
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

		_combinePass.Dispatch();


		//
		// 4b. LOD-independent inputs modify the canonical final field.
		//

		_inputPass.DispatchPostCombine();


		//
		// 5. Commit the spatial state belonging to this AWF generation.
		//
		// IMPORTANT:
		//
		// We copy the already-calculated LodLayout.
		//
		// There is no CalculateSlice() here and no camera-state lookup.
		// Renderer/debug consumers therefore receive exactly the same
		// centers, scales and texel widths which were uploaded to the GPU
		// and used for Direct + ShapeCombine.
		//
		// Crest equivalent:
		//
		//     LodTransform.RenderData.Current
		//         -> WriteCascadeParams()
		//         -> consumers
		//

		RenderState.Publish(
			LodLayout,
			lodScaleAlpha);
	}


	public void Release()
	{
		//
		// Release GPU passes first because they borrow resources below.
		//

		_inputPass?.Dispose();

		_inputPass =
			null;


		_combinePass?.Dispose();

		_combinePass =
			null;


		_fftDirectPass?.Dispose();

		_fftDirectPass =
			null;


		//
		// CPU committed state owns no GPU resources.
		//

		RenderState =
			null;


		//
		// Persistent composition storage.
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
