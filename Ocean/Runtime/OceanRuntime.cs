using System;
using System.Threading;
using Godot;
using OceanFrontier.Water.Waves;
using OceanFrontier.Water.Waves.FFT;
using OceanFrontier.Water.Waves.AnimatedWaves;
using OceanFrontier.Water.Waves.SeaFloorDepth;
using OceanFrontier.Water.Queries;

namespace OceanFrontier.Water.Runtime;

[GlobalClass]
public partial class OceanRuntime : Node
{
	[Export]
	public SeaState SeaState { get; set; } = new();

	[Export]
	public SpectrumDefinition Spectrum { get; set; } = new();

	[Export(PropertyHint.Range, "16,512,1")]
	public int FftResolution { get; set; } = 128;

	[Export(PropertyHint.Range, "1,16,1")]
	public int FftCascadeCount { get; set; } = 11;


	[Export(PropertyHint.Range, "32,512,1")]
	public int AnimatedWaveResolution { get; set; } = 384;

	[Export(PropertyHint.Range, "1,4,0.25")]
	public float AnimatedWaveResolutionMultiplier { get; set; } = 1.0f;

	[Export(PropertyHint.Range, "1,16,1")]
	public int AnimatedWaveLodCount { get; set; } = 8;

	[Export(PropertyHint.Range, "0.25,4096,0.25,or_greater")]
	public float AnimatedWaveBaseWorldSize { get; set; } = 32.0f;

	[Export]
	public bool AnimatedWaveViewHeightScaleEnabled { get; set; } = true;

	[Export(PropertyHint.Range, "0,20,0.25")]
	public float AnimatedWaveDetailBandHeight { get; set; } = 4.0f;


	private readonly FftWaveSource _fftWaveSource = new();

	private readonly AnimatedWaveComposer _animatedWaveComposer = new();

	private readonly AnimatedWaveInputRegistry _animatedWaveInputs = new();

	private readonly AnimatedWaveInputSnapshot[] _animatedWaveInputRenderScratch =
		new AnimatedWaveInputSnapshot[AnimatedWaveInputRegistry.Capacity];

	private readonly SeaFloorDepthInputRegistry _seaFloorDepthInputs = new();
	private readonly SeaFloorDepthInputSnapshot[] _seaFloorDepthRenderScratch =
		new SeaFloorDepthInputSnapshot[SeaFloorDepthInputRegistry.Capacity];

	public OceanPointQueryService PointQueries { get; } = new();


	private double _simulationTime;

	private volatile bool _gpuReady;

	private Callable _renderUpdateCallable;


	private RuntimeWaveSettings _startupWaveSettings;
	private RuntimeWaveSettings _requestedWaveSettings;
	private RuntimeWaveSettings _frameWaveSettings;

	private int _appliedH0Revision;

	private bool _simulationPaused;
	private float _simulationTimeScale = 1.0f;

	private bool _focusOverrideEnabled;
	private Vector2 _focusOverrideXZ;

	private float _runtimeResolutionMultiplier;


	//
	// Crest-like whole-LOD-stack scale.
	//
	// world scale:
	//     1 -> LOD0 = 32m
	//     2 -> LOD0 = 64m
	//     4 -> LOD0 = 128m
	//
	// alpha:
	//     continuous interpolation towards the next x2 scale.
	//

	private float _pendingLodScale = 1.0f;
	private float _pendingLodScaleAlpha;

	private bool _lodScaleOverrideEnabled;
	private float _lodScaleOverride = 1.0f;


	//
	// Main-thread snapshot consumed by the render-thread update.
	//

	private float _pendingSimulationTime;
	private Vector2 _pendingFocusXZ;

	private int _surfaceFieldResolution;
	private int _surfaceLodCount;


	//
	// Persistent main-thread scratch storage for legacy single-LOD
	// surface accessors.
	//
	// The production renderer will move to one coherent whole-stack
	// snapshot per frame.
	//
	// No per-frame allocations.
	//

	private AnimatedWaveLodSlice[] _surfaceStateScratch;


	private bool _processLogged;


	internal float SimulationTime =>
		(float)_simulationTime;

	internal int AppliedH0Revision =>
		Volatile.Read(
			ref _appliedH0Revision);

	internal bool H0Pending =>
		_requestedWaveSettings != null &&
		_requestedWaveSettings.H0Revision != AppliedH0Revision;

	internal bool SimulationPaused
	{
		get => _simulationPaused;
		set => _simulationPaused = value;
	}

	internal float SimulationTimeScale
	{
		get => _simulationTimeScale;

		set =>
			_simulationTimeScale =
				Mathf.Clamp(
					value,
					0.0f,
					4.0f);
	}

	internal bool FocusOverrideEnabled
	{
		get => _focusOverrideEnabled;
		set => _focusOverrideEnabled = value;
	}

	internal Vector2 FocusOverrideXZ
	{
		get => _focusOverrideXZ;
		set => _focusOverrideXZ = value;
	}

	internal int RuntimeFftCascadeCount =>
		_fftWaveSource?.CascadeCount ?? 0;

	internal int RuntimeAnimatedWaveLodCount =>
		_surfaceLodCount;

	internal int RuntimeAnimatedWaveResolution =>
		_surfaceFieldResolution;

	internal float RuntimeResolutionMultiplier =>
		_runtimeResolutionMultiplier;

	internal float RuntimeLodScale =>
		_pendingLodScale;

	internal float RuntimeLodScaleAlpha =>
		_pendingLodScaleAlpha;

	internal int RuntimeAnimatedWaveInputCount =>
		_animatedWaveComposer.ActiveInputCount;

	internal long RuntimeAnimatedWaveInputDispatchCount =>
		_animatedWaveComposer.InputDispatchCount;

	internal int RuntimeSeaFloorDepthInputCount =>
		_animatedWaveComposer.ActiveSeaFloorDepthInputCount;

	internal long RuntimeSeaFloorDepthDispatchCount =>
		_animatedWaveComposer.SeaFloorDepthDispatchCount;


	internal void RegisterAnimatedWaveInput(
		IAnimatedWaveInputSnapshotSource input)
	{
		_animatedWaveInputs.Register(
			input);
	}


	internal void UnregisterAnimatedWaveInput(
		IAnimatedWaveInputSnapshotSource input)
	{
		_animatedWaveInputs.Unregister(
			input);
	}

	internal void RegisterSeaFloorDepthInput(ISeaFloorDepthInputSnapshotSource input) =>
		_seaFloorDepthInputs.Register(input);

	internal void UnregisterSeaFloorDepthInput(ISeaFloorDepthInputSnapshotSource input) =>
		_seaFloorDepthInputs.Unregister(input);

	internal bool LodScaleOverrideEnabled
	{
		get => _lodScaleOverrideEnabled;
		set => _lodScaleOverrideEnabled = value;
	}

	internal float LodScaleOverride
	{
		get => _lodScaleOverride;

		set =>
			_lodScaleOverride =
				Mathf.Max(
					1.0f,
					value);
	}


	internal RuntimeWaveSettings GetWaveSettingsSnapshot() =>
		_requestedWaveSettings?.Copy();

	internal RuntimeWaveSettings GetStartupWaveSettingsSnapshot() =>
		_startupWaveSettings?.Copy();


	internal void RequestWaveSettings(
		RuntimeWaveSettings requested)
	{
		if (requested == null ||
			_requestedWaveSettings == null)
		{
			return;
		}

		RuntimeWaveSettings next =
			requested.Copy();

		next.H0Revision =
			_requestedWaveSettings.H0Revision +
			(_requestedWaveSettings.HasSameH0(next)
				? 0
				: 1);

		_requestedWaveSettings =
			next;
	}


	internal void ResetWaveSettings()
	{
		if (_startupWaveSettings != null)
		{
			RequestWaveSettings(
				_startupWaveSettings);
		}
	}


	public override void _Ready()
	{
		GD.Print(
			"[Ocean] _Ready");

		//
		// This callable IS the per-frame render-thread update.
		//
		// _Process() only snapshots main-thread state and queues it.
		//

		_renderUpdateCallable =
			Callable.From(
				RenderThreadUpdateSpectrum);

		QueueGpuInitialization();
	}


	public override void _ExitTree()
	{
		_gpuReady = false;

		QueueGpuRelease();
	}


	/// <summary>
	/// Crest-like viewpoint-height driven whole LOD-stack scale.
	///
	/// Crest reference:
	///
	///     camDistance = max(abs(viewerHeight) - 4, 0)
	///     level       = max(camDistance, minScale)
	///     l2          = log2(level)
	///     scale       = 2^floor(l2)
	///     alpha       = frac(l2)
	///
	/// Our minimum Crest scale is derived from the physical width
	/// of LOD0:
	///
	///     LOD0 width = 4 * Crest scale
	///
	/// Therefore 32m LOD0 corresponds to Crest scale 8.
	/// </summary>
	private void GetAnimatedWaveLodScale(
		out float worldScale,
		out float scaleAlpha)
	{
		if (_lodScaleOverrideEnabled)
		{
			worldScale =
				Mathf.Max(
					1.0f,
					_lodScaleOverride);

			scaleAlpha =
				0.0f;

			return;
		}


		if (!AnimatedWaveViewHeightScaleEnabled)
		{
			worldScale =
				1.0f;

			scaleAlpha =
				0.0f;

			return;
		}


		Camera3D camera =
			GetViewport()?.GetCamera3D();

		if (camera == null)
		{
			worldScale =
				1.0f;

			scaleAlpha =
				0.0f;

			return;
		}


		//
		// Current ocean contract:
		// sea level = world Y 0.
		//
		// Later this can use authoritative local sea level.
		//

		float viewerHeight =
			MathF.Abs(
				camera.GlobalPosition.Y);


		//
		// Crest keeps full detail inside a band around sea level.
		//

		float cameraDistance =
			MathF.Max(
				viewerHeight -
				AnimatedWaveDetailBandHeight,
				0.0f);


		//
		// Crest scale 8 -> physical LOD0 width 32m.
		//

		float minimumCrestScale =
			AnimatedWaveBaseWorldSize *
			0.25f;


		float level =
			MathF.Max(
				cameraDistance,
				minimumCrestScale);


		//
		// Normalize Crest absolute scale so our
		// AnimatedWaveLodLayout receives:
		//
		// 1, 2, 4, 8...
		//

		float relativeLevel =
			MathF.Max(
				level /
					minimumCrestScale,
				1.0f);


		float log2Level =
			MathF.Log2(
				relativeLevel);

		float floorLevel =
			MathF.Floor(
				log2Level);


		worldScale =
			MathF.Pow(
				2.0f,
				floorLevel);


		scaleAlpha =
			Mathf.Clamp(
				log2Level -
					floorLevel,
				0.0f,
				1.0f);
	}


	private void QueueGpuInitialization()
	{
		GD.Print(
			"[Ocean] QueueGpuInitialization");


		if (SeaState == null)
		{
			GD.PushError(
				"OceanRuntime: SeaState is null.");

			return;
		}


		if (Spectrum == null ||
			!Spectrum.IsStructurallyValid())
		{
			GD.PushError(
				"OceanRuntime: SpectrumDefinition is invalid.");

			return;
		}


		//
		// Snapshot Godot Resource/configuration data
		// on the main thread.
		//

		int resolution =
			FftResolution;

		int cascadeCount =
			FftCascadeCount;


		RuntimeWaveSettings initialSettings =
			RuntimeWaveSettings.FromResources(
				SeaState,
				Spectrum);

		_startupWaveSettings =
			initialSettings.Copy();

		_requestedWaveSettings =
			initialSettings;

		_frameWaveSettings =
			initialSettings;

		int bandCount =
			initialSettings.PowerLog10.Length;


		int animatedWaveResolution =
			AnimatedWaveResolution;

		int animatedWaveLodCount =
			AnimatedWaveLodCount;

		float animatedWaveResolutionMultiplier =
			AnimatedWaveResolutionMultiplier;

		float animatedWaveBaseWorldSize =
			AnimatedWaveBaseWorldSize;


		GD.Print(
			$"[Ocean] Animated Wave sampling: " +
			$"resolution={animatedWaveResolution}, " +
			$"multiplier={animatedWaveResolutionMultiplier}");


		//
		// Initial spatial state.
		//

		Vector2 initialFocusXZ =
			GetAnimatedWaveFocusXZ();


		GetAnimatedWaveLodScale(
			out float initialLodScale,
			out float initialLodScaleAlpha);


		_pendingFocusXZ =
			initialFocusXZ;

		_pendingLodScale =
			initialLodScale;

		_pendingLodScaleAlpha =
			initialLodScaleAlpha;


		_animatedWaveInputs.CaptureMainThread();
		_seaFloorDepthInputs.CaptureMainThread();


		_surfaceFieldResolution =
			animatedWaveResolution;

		_surfaceLodCount =
			animatedWaveLodCount;

		_runtimeResolutionMultiplier =
			animatedWaveResolutionMultiplier;


		_surfaceStateScratch =
			new AnimatedWaveLodSlice[animatedWaveLodCount];


		var fft =
			_fftWaveSource;

		var composer =
			_animatedWaveComposer;


		RenderingServer.CallOnRenderThread(
			Callable.From(() =>
			{
				try
				{
					GD.Print(
						"[Ocean] RenderThread initialization entered");


					RenderingDevice rd =
						RenderingServer.GetRenderingDevice();

					if (rd == null)
					{
						GD.PushError(
							"OceanRuntime requires a global " +
							"RenderingDevice.");

						return;
					}


					GD.Print(
						"[Ocean] RenderingDevice acquired");


					//
					// FFT GPU source.
					//

					fft.Initialize(
						rd,
						resolution,
						cascadeCount,
						bandCount);


					GD.Print(
						"[Ocean] FftWaveSource initialized");


					//
					// Canonical Animated Waves resources.
					//

					composer.Initialize(
						rd,
						animatedWaveResolution,
						animatedWaveLodCount,
						animatedWaveBaseWorldSize);


					GD.Print(
						$"[Ocean] AnimatedWave LOD GPU buffer created: " +
						$"{composer.LodGpuBuffer.ByteSize} bytes.");


					GD.Print(
						$"[Ocean] AnimatedWaveField created: " +
						$"{animatedWaveResolution}x{animatedWaveResolution}, " +
						$"{animatedWaveLodCount} LOD slices.");


					GD.Print(
						$"[Ocean] AnimatedWaveDerivativeField matches canonical field: " +
						$"{composer.DerivativeField.Resolution}x" +
						$"{composer.DerivativeField.Resolution}, " +
						$"{composer.DerivativeField.LodCount} LOD slices, RGBA16F.");


					//
					// FFT becomes an input of AnimatedWaveComposer.
					//

					composer.InitializeFftInput(
						fft.RawDisplacement,
						fft.CascadeCount,
						animatedWaveResolutionMultiplier);


					//
					// Spectrum initialization.
					//

					fft.InitializeSpectrum(
						initialSettings.ToInitSettings(
							resolution,
							cascadeCount),
						initialSettings.BuildLinearPowerControls());


					Volatile.Write(
						ref _appliedH0Revision,
						initialSettings.H0Revision);


					GD.Print(
						"[Ocean] SpectrumInit dispatched");


					//
					// Build t=0 raw FFT displacement.
					//

					fft.UpdateSpectrum(
						0.0f,
						initialSettings.Chop,
						initialSettings.Gravity,
						initialSettings.LoopPeriod);


					GD.Print(
						"[Ocean] SpectrumUpdate t=0 dispatched");


					fft.TransformSpectrumToDisplacement();


					GD.Print(
						"[Ocean] IFFT dispatched");


					//
					// First canonical AnimatedWaveField composition.
					//
					// Crest Animated Waves path:
					//
					// raw FFT
					// -> direct per-LOD wave contributions
					// -> coarse-to-fine combine
					// -> canonical AnimatedWaveField
					//
					// IMPORTANT:
					// initialLodScale and initialLodScaleAlpha belong to
					// the same initial spatial LOD state.
					//

					int initialInputCount =
						_animatedWaveInputs.CopyLatest(
							_animatedWaveInputRenderScratch);

					int initialDepthInputCount =
						_seaFloorDepthInputs.CopyLatest(
							_seaFloorDepthRenderScratch);


					composer.ComposeFft(
						initialFocusXZ,
						initialLodScale,
						initialLodScaleAlpha,
						_animatedWaveInputRenderScratch.AsSpan(
							0,
							initialInputCount),
						_seaFloorDepthRenderScratch.AsSpan(
							0,
							initialDepthInputCount),
						initialSettings.ShallowWaterAttenuation,
						initialSettings.ShallowWaterMaximumDepth);


					var lod0 =
						composer.LodLayout[0];

					var lodLast =
						composer.LodLayout[
							animatedWaveLodCount - 1];


					GD.Print(
						$"[Ocean] AnimatedWave LOD layout: " +
						$"scale=x{initialLodScale:0.###}, " +
						$"alpha={initialLodScaleAlpha:0.###}; " +
						$"LOD0 size={lod0.WorldSize:0.###}m, " +
						$"texel={lod0.TexelWidth:0.######}m, " +
						$"wavelength={lod0.MinWavelength:0.######}-" +
						$"{lod0.MaxWavelength:0.######}m; " +
						$"LOD{lodLast.Index} size={lodLast.WorldSize:0.###}m.");


					PointQueries.Initialize(
						rd,
						composer.Field.Displacement,
						composer.LodGpuBuffer.Buffer,
						composer.Field.LodCount);


					//
					// Runtime can now consume the GPU pipeline.
					//

					_gpuReady =
						true;


					GD.Print(
						"[Ocean] GPU ready");


					GD.Print(
						$"Ocean FFT H0 initialized: " +
						$"{resolution}x{resolution}, " +
						$"{cascadeCount} cascades, " +
						$"{bandCount} spectrum bands.");
				}
				catch (Exception exception)
				{
					GD.PushError(
						$"Ocean FFT initialization failed:\n" +
						exception);
				}
			}));
	}


	private void QueueGpuRelease()
	{
		var fft =
			_fftWaveSource;

		var composer =
			_animatedWaveComposer;

		var queries =
			PointQueries;


		RenderingServer.CallOnRenderThread(
			Callable.From(() =>
			{
				//
				// Release consumers before resources
				// borrowed from composer/FFT.
				//

				queries.Release();

				composer.Release();

				fft.Release();


				GD.Print(
					"[Ocean] GPU wave resources released.");
			}));
	}


	public override void _Process(
		double delta)
	{
		if (!_simulationPaused)
		{
			_simulationTime +=
				delta *
				_simulationTimeScale;
		}


		if (!_gpuReady)
		{
			return;
		}


		if (!_processLogged)
		{
			_processLogged =
				true;

			GD.Print(
				"[Ocean] Per-frame processing started");
		}


		//
		// MAIN THREAD:
		//
		// Snapshot everything needed by the next
		// render-thread update.
		//
		// Do NOT mutate AnimatedWaveLodLayout here.
		//

		_pendingSimulationTime =
			(float)_simulationTime;


		_pendingFocusXZ =
			GetAnimatedWaveFocusXZ();


		GetAnimatedWaveLodScale(
			out _pendingLodScale,
			out _pendingLodScaleAlpha);


		Volatile.Write(
			ref _frameWaveSettings,
			_requestedWaveSettings);


		_animatedWaveInputs.CaptureMainThread();
		_seaFloorDepthInputs.CaptureMainThread();


		//
		// Queue the persistent callable.
		//
		// RenderThreadUpdateSpectrum() below is the
		// actual render-thread update.
		//

		RenderingServer.CallOnRenderThread(
			_renderUpdateCallable);
	}


	/// <summary>
	/// PER-FRAME RENDER-THREAD UPDATE.
	///
	/// This is the "render update".
	///
	/// Order:
	///
	/// spectrum evolution
	/// -> IFFT
	/// -> canonical AWF composition
	/// -> physics query dispatch
	///
	/// All LOD scale data is snapshotted at the beginning so the
	/// composer and point query use the same state for this update.
	/// </summary>
	private void RenderThreadUpdateSpectrum()
	{
		if (!_gpuReady ||
			!_fftWaveSource.IsInitialized)
		{
			return;
		}


		//
		// Snapshot main-thread values once for this render update.
		//

		float simulationTime =
			_pendingSimulationTime;

		Vector2 focusXZ =
			_pendingFocusXZ;

		float lodScale =
			_pendingLodScale;

		float lodScaleAlpha =
			_pendingLodScaleAlpha;


		RuntimeWaveSettings settings =
			Volatile.Read(
				ref _frameWaveSettings);


		if (settings == null)
		{
			return;
		}


		//
		// 1. Evolve spectral data.
		//

		if (settings.H0Revision !=
			Volatile.Read(
				ref _appliedH0Revision))
		{
			_fftWaveSource.InitializeSpectrum(
				settings.ToInitSettings(
					_fftWaveSource.Resolution,
					_fftWaveSource.CascadeCount),
				settings.BuildLinearPowerControls());


			//
			// H0 regeneration is a discontinuous change of the
			// canonical wave field.
			//
			// Do not finite-difference a displacement result from
			// the previous H0 revision against the new spectrum.
			//

			PointQueries.InvalidateVelocities();


			Volatile.Write(
				ref _appliedH0Revision,
				settings.H0Revision);


			GD.Print(
				$"[Ocean] SpectrumInit regenerated: " +
				$"H0 rev {settings.H0Revision}");
		}


		_fftWaveSource.UpdateSpectrum(
			simulationTime,
			settings.Chop,
			settings.Gravity,
			settings.LoopPeriod);


		//
		// 2. Raw FFT spectral bands
		//    -> spatial displacement.
		//

		_fftWaveSource.TransformSpectrumToDisplacement();


		//
		// 3. Raw FFT source
		//    -> canonical AnimatedWaveField.
		//
		// Crest Animated Waves composition:
		//
		// - applies the current whole-stack LOD scale;
		// - updates camera-relative LOD layout;
		// - uploads LOD metadata once;
		// - assigns FFT bands to direct spatial LOD inputs;
		// - combines coarse -> fine;
		// - writes the cumulative canonical AnimatedWaveField.
		//
		// lodScaleAlpha drives Crest's final-two-LOD wavelength
		// transition during whole-stack scale changes.
		//

		int animatedWaveInputCount =
			_animatedWaveInputs.CopyLatest(
				_animatedWaveInputRenderScratch);

		int seaFloorDepthInputCount =
			_seaFloorDepthInputs.CopyLatest(
				_seaFloorDepthRenderScratch);


		_animatedWaveComposer.ComposeFft(
			focusXZ,
			lodScale,
			lodScaleAlpha,
			_animatedWaveInputRenderScratch.AsSpan(
				0,
				animatedWaveInputCount),
			_seaFloorDepthRenderScratch.AsSpan(
				0,
				seaFloorDepthInputCount),
			settings.ShallowWaterAttenuation,
			settings.ShallowWaterMaximumDepth);


		//
		// 4. Queries consume the SAME canonical AWF
		//    immediately after composition.
		//
		// lodScaleAlpha is required so physics sampling
		// matches the renderer's LOD0 -> LOD1 altitude blend.
		//
		// simulationTime is the timestamp of the exact AWF state
		// being queried. Query velocity finite differences therefore
		// follow ocean simulation time, including pause/time scale.
		//

		PointQueries.DispatchAfterCompose(
			_animatedWaveComposer.LodLayout.FocusXZ,
			lodScaleAlpha,
			simulationTime);
	}


	/// <summary>
	/// Main-thread camera/focus lookup.
	///
	/// AnimatedWaveField spatial LODs follow the active
	/// camera in XZ unless diagnostics override the focus.
	/// </summary>
	private Vector2 GetAnimatedWaveFocusXZ()
	{
		if (_focusOverrideEnabled)
		{
			return _focusOverrideXZ;
		}


		Camera3D camera =
			GetViewport()?.GetCamera3D();


		if (camera == null)
		{
			return Vector2.Zero;
		}


		Vector3 position =
			camera.GlobalPosition;


		return new Vector2(
			position.X,
			position.Z);
	}


	/// <summary>
	/// Copies one coherent committed spatial state and the persistent texture
	/// RIDs belonging to the canonical AnimatedWaveField generation and its
	/// AnimatedWaveDerivativeField cache.
	///
	/// Crest equivalent:
	///
	///     LodTransform.RenderData.Current
	///         -> WriteCascadeParams()
	///         -> consumers
	///
	/// This method does NOT calculate LOD slices.
	///
	/// The returned metadata is copied from the exact
	/// AnimatedWaveLodLayout used by the render thread to build the
	/// corresponding AnimatedWaveField generation.
	///
	/// The caller owns destination storage and should keep it persistent.
	/// No allocation occurs here.
	/// </summary>
	internal bool TryCopyAnimatedWaveSurfaceState(
		Span<AnimatedWaveLodSlice> destination,
		out Rid displacementTexture,
		out Rid derivativeTexture,
		out int resolution,
		out int lodCount,
		out Vector2 focusXZ,
		out float worldScale,
		out float lodScaleAlpha,
		out long generation)
	{
		displacementTexture =
			default;

		derivativeTexture =
			default;

		resolution =
			0;

		lodCount =
			0;

		focusXZ =
			default;

		worldScale =
			1.0f;

		lodScaleAlpha =
			0.0f;

		generation =
			0;


		if (!_gpuReady)
		{
			return false;
		}


		AnimatedWaveField field =
			_animatedWaveComposer.Field;

		AnimatedWaveDerivativeField derivativeField =
			_animatedWaveComposer.DerivativeField;

		AnimatedWaveRenderState renderState =
			_animatedWaveComposer.RenderState;


		if (field == null ||
			derivativeField == null ||
			renderState == null ||
			!field.Displacement.IsValid ||
			!derivativeField.NormalJacobian.IsValid)
		{
			return false;
		}


		if (!renderState.TryCopy(
				destination,
				out resolution,
				out lodCount,
				out focusXZ,
				out worldScale,
				out lodScaleAlpha,
				out generation))
		{
			return false;
		}


		//
		// Both texture RIDs are persistent. The derivative pass completes after
		// final composition and before this generation's RenderState is published.
		//
		// Spatial metadata above belongs to the committed generation
		// written into this canonical field by AnimatedWaveComposer.
		//

		displacementTexture =
			field.Displacement;

		derivativeTexture =
			derivativeField.NormalJacobian;


		return true;
	}


	internal bool TryGetAnimatedWaveSurface(
		int spatialLodIndex,
		out Rid texture,
		out int resolution,
		out int lodCount,
		out AnimatedWaveLodSlice selectedSlice)
	{
		texture =
			default;

		resolution =
			0;

		lodCount =
			0;

		selectedSlice =
			default;


		if (spatialLodIndex < 0 ||
			spatialLodIndex >= _surfaceLodCount ||
			_surfaceStateScratch == null ||
			_surfaceStateScratch.Length <
				_surfaceLodCount)
		{
			return false;
		}


		if (!TryCopyAnimatedWaveSurfaceState(
				_surfaceStateScratch,
				out texture,
				out _,
				out resolution,
				out lodCount,
				out _,
				out _,
				out _,
				out _))
		{
			return false;
		}


		if (spatialLodIndex >=
			lodCount)
		{
			return false;
		}


		selectedSlice =
			_surfaceStateScratch[
				spatialLodIndex];


		return true;
	}


	internal bool TryGetAnimatedWaveSurfaceWithNext(
		int spatialLodIndex,
		out Rid texture,
		out int resolution,
		out int lodCount,
		out AnimatedWaveLodSlice selectedSlice,
		out AnimatedWaveLodSlice nextSlice,
		out Vector2 focusXZ)
	{
		texture =
			default;

		resolution =
			0;

		lodCount =
			0;

		selectedSlice =
			default;

		nextSlice =
			default;

		focusXZ =
			default;


		if (spatialLodIndex < 0 ||
			spatialLodIndex >= _surfaceLodCount ||
			_surfaceStateScratch == null ||
			_surfaceStateScratch.Length <
				_surfaceLodCount)
		{
			return false;
		}


		//
		// One committed snapshot supplies:
		//
		//     current LOD
		//     next LOD
		//     focus
		//
		// They therefore cannot belong to different generations.
		//

		if (!TryCopyAnimatedWaveSurfaceState(
				_surfaceStateScratch,
				out texture,
				out _,
				out resolution,
				out lodCount,
				out focusXZ,
				out _,
				out _,
				out _))
		{
			return false;
		}


		if (spatialLodIndex >=
			lodCount)
		{
			return false;
		}


		selectedSlice =
			_surfaceStateScratch[
				spatialLodIndex];


		if (spatialLodIndex + 1 <
			lodCount)
		{
			nextSlice =
				_surfaceStateScratch[
					spatialLodIndex + 1];
		}
		else
		{
			nextSlice =
				selectedSlice;
		}


		return true;
	}


	//
	// DEBUG
	//

	internal bool TryGetRawFftDebugTexture(
		out Rid texture,
		out int cascadeCount)
	{
		texture =
			default;

		cascadeCount =
			0;


		if (!_gpuReady ||
			!_fftWaveSource.IsInitialized)
		{
			return false;
		}


		texture =
			_fftWaveSource.RawDisplacement;

		cascadeCount =
			_fftWaveSource.CascadeCount;


		return
			texture.IsValid &&
			cascadeCount > 0;
	}


	internal bool TryGetAnimatedWaveDebugTexture(
		out Rid texture,
		out int lodCount)
	{
		texture =
			default;

		lodCount =
			0;


		if (!_gpuReady)
		{
			return false;
		}


		AnimatedWaveField field =
			_animatedWaveComposer.Field;


		if (field == null)
		{
			return false;
		}


		texture =
			field.Displacement;

		lodCount =
			field.LodCount;


		return
			texture.IsValid &&
			lodCount > 0;
	}


	internal bool TryGetAnimatedWaveDerivativeDebugTexture(
		out Rid texture,
		out int lodCount)
	{
		texture = default;
		lodCount = 0;

		if (!_gpuReady)
		{
			return false;
		}

		AnimatedWaveDerivativeField field =
			_animatedWaveComposer.DerivativeField;

		if (field == null)
		{
			return false;
		}

		texture = field.NormalJacobian;
		lodCount = field.LodCount;

		return texture.IsValid && lodCount > 0;
	}
}
