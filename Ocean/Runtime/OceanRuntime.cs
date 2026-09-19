using System;
using System.Threading;
using Godot;
using OceanFrontier.Water.Waves;
using OceanFrontier.Water.Waves.FFT;
using OceanFrontier.Water.Waves.AnimatedWaves;

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
	public int FftCascadeCount { get; set; } = 8;


	[Export(PropertyHint.Range, "32,512,1")]
	public int AnimatedWaveResolution { get; set; } = 256;

	[Export(PropertyHint.Range, "1,4,0.25")]
	public float AnimatedWaveResolutionMultiplier { get; set; } = 2.0f;

	[Export(PropertyHint.Range, "1,16,1")]
	public int AnimatedWaveLodCount { get; set; } = 8;

	[Export(PropertyHint.Range, "0.25,4096,0.25,or_greater")]
	public float AnimatedWaveBaseWorldSize { get; set; } = 4.0f;


	private readonly FftWaveSource _fftWaveSource = new();

	private readonly AnimatedWaveComposer _animatedWaveComposer = new();


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


	// Snapshot from main thread for the next render-thread update.
	private float _pendingSimulationTime;
	private Vector2 _pendingFocusXZ;
	private int _surfaceFieldResolution;
	private int _surfaceLodCount;
	private float _surfaceBaseWorldSize;


	private bool _processLogged;

	internal float SimulationTime => (float)_simulationTime;
	internal int AppliedH0Revision => Volatile.Read(ref _appliedH0Revision);
	internal bool H0Pending => _requestedWaveSettings != null &&
		_requestedWaveSettings.H0Revision != AppliedH0Revision;
	internal bool SimulationPaused { get => _simulationPaused; set => _simulationPaused = value; }
	internal float SimulationTimeScale { get => _simulationTimeScale; set => _simulationTimeScale = Mathf.Clamp(value, 0.0f, 4.0f); }
	internal bool FocusOverrideEnabled { get => _focusOverrideEnabled; set => _focusOverrideEnabled = value; }
	internal Vector2 FocusOverrideXZ { get => _focusOverrideXZ; set => _focusOverrideXZ = value; }
	internal int RuntimeFftCascadeCount => _fftWaveSource?.CascadeCount ?? 0;
	internal int RuntimeAnimatedWaveLodCount => _surfaceLodCount;
	internal int RuntimeAnimatedWaveResolution => _surfaceFieldResolution;
	internal float RuntimeResolutionMultiplier => _runtimeResolutionMultiplier;
	internal RuntimeWaveSettings GetWaveSettingsSnapshot() => _requestedWaveSettings?.Copy();
	internal RuntimeWaveSettings GetStartupWaveSettingsSnapshot() => _startupWaveSettings?.Copy();

	internal void RequestWaveSettings(RuntimeWaveSettings requested)
	{
		if (requested == null || _requestedWaveSettings == null) return;
		RuntimeWaveSettings next = requested.Copy();
		next.H0Revision = _requestedWaveSettings.H0Revision +
			(_requestedWaveSettings.HasSameH0(next) ? 0 : 1);
		_requestedWaveSettings = next;
	}

	internal void ResetWaveSettings()
	{
		if (_startupWaveSettings != null)
			RequestWaveSettings(_startupWaveSettings);
	}


	public override void _Ready()
	{
		GD.Print("[Ocean] _Ready");

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
		// Snapshot all Godot Resource/configuration data
		// on the main thread.
		//

		int resolution =
			FftResolution;

		int cascadeCount =
			FftCascadeCount;


		RuntimeWaveSettings initialSettings =
			RuntimeWaveSettings.FromResources(SeaState, Spectrum);
		_startupWaveSettings = initialSettings.Copy();
		_requestedWaveSettings = initialSettings;
		_frameWaveSettings = initialSettings;
		int bandCount = initialSettings.PowerLog10.Length;


		int animatedWaveResolution =
			AnimatedWaveResolution;

		int animatedWaveLodCount =
			AnimatedWaveLodCount;

		float animatedWaveResolutionMultiplier =
			AnimatedWaveResolutionMultiplier;

		float animatedWaveBaseWorldSize =
			AnimatedWaveBaseWorldSize;

		GD.Print(
			$"[Ocean] Animated Wave sampling: resolution={animatedWaveResolution}, " +
			$"multiplier={animatedWaveResolutionMultiplier}");


		// Initial spatial focus is also captured
		// on the main thread.
		Vector2 initialFocusXZ =
			GetAnimatedWaveFocusXZ();

		_pendingFocusXZ = initialFocusXZ;
		_surfaceFieldResolution = animatedWaveResolution;
		_surfaceLodCount = animatedWaveLodCount;
		_runtimeResolutionMultiplier = animatedWaveResolutionMultiplier;
		_surfaceBaseWorldSize = animatedWaveBaseWorldSize;


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


					var lod0 =
						composer.LodLayout[0];

					var lodLast =
						composer.LodLayout[
							animatedWaveLodCount - 1];


					GD.Print(
						$"[Ocean] AnimatedWave LOD layout: " +
						$"LOD0 size={lod0.WorldSize:0.###}m, " +
						$"texel={lod0.TexelWidth:0.######}m, " +
						$"wavelength={lod0.MinWavelength:0.######}-" +
						$"{lod0.MaxWavelength:0.######}m; " +
						$"LOD{lodLast.Index} size={lodLast.WorldSize:0.###}m.");


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
						initialSettings.ToInitSettings(resolution, cascadeCount),
						initialSettings.BuildLinearPowerControls());
					Volatile.Write(ref _appliedH0Revision, initialSettings.H0Revision);


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

					composer.ComposeFft(
						initialFocusXZ);


					//
					// Runtime can now consume the GPU pipeline.
					//

					_gpuReady = true;


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


		RenderingServer.CallOnRenderThread(
			Callable.From(() =>
			{
				//
				// Composer references FFT resources,
				// so composer must be released first.
				//

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
			_simulationTime += delta * _simulationTimeScale;


		if (!_gpuReady)
		{
			return;
		}


		if (!_processLogged)
		{
			_processLogged = true;

			GD.Print(
				"[Ocean] Per-frame processing started");
		}


		//
		// Snapshot main-thread state.
		//
		// Do NOT touch AnimatedWaveLodLayout here.
		// It is updated on the render thread by ComposeFft().
		//

		_pendingSimulationTime =
			(float)_simulationTime;

		_pendingFocusXZ =
			GetAnimatedWaveFocusXZ();
		Volatile.Write(ref _frameWaveSettings, _requestedWaveSettings);


		//
		// Reuse one Callable.
		// No per-frame lambda/closure allocation.
		//

		RenderingServer.CallOnRenderThread(
			_renderUpdateCallable);
	}


	private void RenderThreadUpdateSpectrum()
	{
		if (!_gpuReady ||
			!_fftWaveSource.IsInitialized)
		{
			return;
		}


		//
		// 1. Evolve spectral data.
		//

		RuntimeWaveSettings settings = Volatile.Read(ref _frameWaveSettings);
		if (settings.H0Revision != Volatile.Read(ref _appliedH0Revision))
		{
			_fftWaveSource.InitializeSpectrum(
				settings.ToInitSettings(_fftWaveSource.Resolution, _fftWaveSource.CascadeCount),
				settings.BuildLinearPowerControls());
			Volatile.Write(ref _appliedH0Revision, settings.H0Revision);
			GD.Print($"[Ocean] SpectrumInit regenerated: H0 rev {settings.H0Revision}");
		}

		_fftWaveSource.UpdateSpectrum(
			_pendingSimulationTime,
			settings.Chop,
			settings.Gravity,
			settings.LoopPeriod);


		//
		// 2. Raw FFT spectral bands -> spatial displacement.
		//

		_fftWaveSource.TransformSpectrumToDisplacement();


		//
		// 3. Raw FFT source -> canonical AnimatedWaveField.
		//
		// ComposeFft:
		// - updates camera-relative LOD layout;
		// - uploads LOD metadata;
		// - samples appropriate FFT bands;
		// - writes AnimatedWaveField.
		//

		_animatedWaveComposer.ComposeFft(
			_pendingFocusXZ);
	}


	/// <summary>
	/// Main-thread camera/focus lookup.
	///
	/// AnimatedWaveField spatial LODs follow the active camera in XZ.
	/// </summary>
	private Vector2 GetAnimatedWaveFocusXZ()
	{
		if (_focusOverrideEnabled) return _focusOverrideXZ;
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

	internal bool TryGetAnimatedWaveSurface(
		int spatialLodIndex,
		out Rid texture,
		out int resolution,
		out int lodCount,
		out AnimatedWaveLodSlice selectedSlice)
	{
		texture = default;
		resolution = 0;
		lodCount = 0;
		selectedSlice = default;

		if (!_gpuReady ||
			spatialLodIndex < 0 ||
			spatialLodIndex >= _surfaceLodCount)
		{
			return false;
		}

		AnimatedWaveField field = _animatedWaveComposer.Field;
		if (field == null || !field.Displacement.IsValid)
		{
			return false;
		}

		texture = field.Displacement;
		resolution = _surfaceFieldResolution;
		lodCount = _surfaceLodCount;
		selectedSlice = AnimatedWaveLodLayout.CalculateSlice(
			resolution,
			_surfaceBaseWorldSize,
			spatialLodIndex,
			_pendingFocusXZ);

		return true;
	}

	internal bool TryGetAnimatedWaveSurfaceWithNext(
		int spatialLodIndex,
		out Rid texture,
		out int resolution,
		out int lodCount,
		out AnimatedWaveLodSlice selectedSlice,
		out AnimatedWaveLodSlice nextSlice)
	{
		nextSlice = default;
		if (!TryGetAnimatedWaveSurface(
			spatialLodIndex, out texture, out resolution, out lodCount, out selectedSlice))
			return false;

		if (spatialLodIndex + 1 < lodCount)
			nextSlice = AnimatedWaveLodLayout.CalculateSlice(
				resolution, _surfaceBaseWorldSize, spatialLodIndex + 1, _pendingFocusXZ);
		return true;
	}


	//
	// DEBUG
	//
	// Stage 1D-C still exposes the raw FFT texture.
	// We will add a canonical AnimatedWaveField debug accessor
	// separately during visual validation of Stage 2D.
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
	texture = default;
	lodCount = 0;

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
}
 
