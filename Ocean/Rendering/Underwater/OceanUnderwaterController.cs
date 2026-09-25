using System;
using Godot;
using Godot.Collections;
using OceanFrontier.Water.Queries;
using OceanFrontier.Water.Runtime;
using OceanFrontier.Water.Optics;
using OceanFrontier.Water.Rendering;
using OceanFrontier.Water.Waves.AnimatedWaves;

namespace OceanFrontier.Water.Rendering.Underwater;

/// <summary>
/// Main-thread owner for automatic underwater classification and the forced
/// UW-2A compositor override.
/// </summary>
public partial class OceanUnderwaterController : Node
{
	private readonly Vector2[] _queryPositions =
		new Vector2[1];

	private readonly Vector4[] _queryResults =
		new Vector4[1];

	private bool _forceUnderwater;
	private bool _automaticUnderwaterEnabled = true;
	private bool _hasValidSurface;
	private bool _automaticUnderwater;

	private long _lastCompletedGeneration;
	private int _appliedOpticsRevision = -1;
	private int _appliedPrimarySunRevision = -1;
	private int _appliedCausticsRevision = -1;

	private OceanRuntime _runtime;
	private OceanPointQueryService.OwnerHandle _queryOwner;
	private Camera3D _camera;
	private Compositor _previousCompositor;
	private Compositor _underwaterCompositor;
	private OceanUnderwaterCompositorEffect _effect;


	[Export]
	public bool ForceUnderwater
	{
		get =>
			_forceUnderwater;

		set
		{
			_forceUnderwater =
				value;


			ApplyEnabledState();
		}
	}


	[Export]
	public bool AutomaticUnderwater
	{
		get =>
			_automaticUnderwaterEnabled;

		set
		{
			_automaticUnderwaterEnabled =
				value;


			ApplyEnabledState();
		}
	}


	public override void _Ready()
	{
		_runtime =
			GetParent() as OceanRuntime;


		if (_runtime == null)
		{
			GD.PushError(
				"OceanUnderwaterController must be a direct child of OceanRuntime.");
		}
		else
		{
			_queryOwner =
				_runtime.PointQueries.RegisterOwner(
					1);
		}


		_camera =
			GetViewport()?.GetCamera3D();


		if (_camera == null)
		{
			GD.PushError(
				"OceanUnderwaterController could not resolve the current Camera3D.");


			return;
		}


		OceanOpticsState initialOptics =
			OceanOpticsSettings.DefaultState;


		if (_runtime != null)
		{
			_runtime.GetOpticsState(
				out initialOptics,
				out _appliedOpticsRevision);
		}


		OceanPrimarySunState initialSun =
			OceanPrimarySunState.Default;

		OceanCausticsState initialCaustics =
			OceanCausticsSettings.DefaultState;


		if (_runtime != null)
		{
			_runtime.GetPrimarySunState(
				out initialSun,
				out _appliedPrimarySunRevision);


			_runtime.GetCausticsState(
				out initialCaustics,
				out _appliedCausticsRevision);
		}


		_effect =
			new OceanUnderwaterCompositorEffect(
				initialOptics.Extinction,
				initialOptics.DeepScatterColor,
				initialSun.RayDirectionWorld,
				initialSun.LinearRadiance,
				initialCaustics.ToGpuState())
			{
				Enabled =
					false,
			};


		_runtime?.RegisterAnimatedWaveFieldGpuConsumer(
			_effect);


		_previousCompositor =
			_camera.Compositor;


		var effects =
			new Array<CompositorEffect>();


		if (_previousCompositor != null)
		{
			foreach (CompositorEffect existingEffect in
				_previousCompositor.CompositorEffects)
			{
				effects.Add(
					existingEffect);
			}
		}


		effects.Add(
			_effect);


		_underwaterCompositor =
			new Compositor
			{
				CompositorEffects =
					effects,
			};


		_camera.Compositor =
			_underwaterCompositor;


		ApplyEnabledState();
	}


	public override void _Process(
		double delta)
	{
		if (_runtime == null ||
			_queryOwner == null ||
			_camera == null)
		{
			return;
		}


		ApplyOpticsState();
		ApplyPrimarySunState();
		ApplyCausticsState();


		_effect?.SetPendingVisualTime(
			_runtime.SimulationTime);


		if (_runtime.PointQueries.TryCopyLatest(
				_queryOwner,
				_queryResults,
				out int count,
				out long generation,
				out _,
				out _) &&
			generation >
				_lastCompletedGeneration)
		{
			_lastCompletedGeneration =
				generation;


			Vector4 result =
				_queryResults[0];


			if (count == 1 &&
				result.W > 0.5f &&
				float.IsFinite(result.X) &&
				float.IsFinite(result.Y) &&
				float.IsFinite(result.Z))
			{
				_effect?.SetPendingCameraWaterDepth(
					Mathf.Max(
						result.Y -
							_camera.GlobalPosition.Y,
						0.0f));


				_automaticUnderwater =
					_camera.GlobalPosition.Y <
					result.Y;

				_hasValidSurface =
					true;


				ApplyEnabledState();
			}
		}


		if (!_runtime.PointQueries.CanSubmitBatch(
				_queryOwner))
		{
			return;
		}


		Vector3 cameraPosition =
			_camera.GlobalPosition;


		_queryPositions[0] =
			new Vector2(
				cameraPosition.X,
				cameraPosition.Z);


		_runtime.PointQueries.SubmitBatch(
			_queryOwner,
			_queryPositions,
			0.0f);
	}


	public override void _ExitTree()
	{
		if (_queryOwner != null &&
			_runtime != null &&
			GodotObject.IsInstanceValid(
				_runtime))
		{
			_runtime.PointQueries.UnregisterOwner(
				_queryOwner);
		}


		_queryOwner =
			null;

		OceanUnderwaterCompositorEffect effect =
			_effect;


		if (effect != null &&
			_runtime != null &&
			GodotObject.IsInstanceValid(
				_runtime))
		{
			_runtime.UnregisterAnimatedWaveFieldGpuConsumer(
				effect);
		}


		if (effect != null)
		{
			effect.Enabled =
				false;
		}


		if (_camera != null &&
			ReferenceEquals(
				_camera.Compositor,
				_underwaterCompositor))
		{
			_camera.Compositor =
				_previousCompositor;
		}


		if (effect != null)
		{
			RenderingServer.CallOnRenderThread(
				Callable.From(
					effect.ReleaseGpuResources));
		}


		_effect =
			null;

		_underwaterCompositor =
			null;

		_previousCompositor =
			null;

		_camera =
			null;

		_runtime =
			null;
	}


	private void ApplyEnabledState()
	{
		if (_effect == null)
		{
			return;
		}


		_effect.Enabled =
			_forceUnderwater ||
			(
				_automaticUnderwaterEnabled &&
				_hasValidSurface &&
				_automaticUnderwater
			);
	}


	private void ApplyOpticsState()
	{
		_runtime.GetOpticsState(
			out OceanOpticsState state,
			out int revision);


		if (_appliedOpticsRevision ==
			revision)
		{
			return;
		}


		OceanUnderwaterCompositorEffect effect =
			_effect;


		if (effect == null)
		{
			return;
		}


		Vector3 extinction =
			state.Extinction;

		Vector3 deepScatterColor =
			state.DeepScatterColor;


		RenderingServer.CallOnRenderThread(
			Callable.From(() =>
				effect.SetOpticsState(
					extinction,
					deepScatterColor)));


		_appliedOpticsRevision =
			revision;
	}


	private void ApplyPrimarySunState()
	{
		_runtime.GetPrimarySunState(
			out OceanPrimarySunState state,
			out int revision);


		if (_appliedPrimarySunRevision ==
			revision)
		{
			return;
		}


		OceanUnderwaterCompositorEffect effect =
			_effect;


		if (effect == null)
		{
			return;
		}


		Vector3 rayDirectionWorld =
			state.RayDirectionWorld;

		Vector3 linearRadiance =
			state.LinearRadiance;


		RenderingServer.CallOnRenderThread(
			Callable.From(() =>
				effect.SetPrimarySunState(
					rayDirectionWorld,
					linearRadiance)));


		_appliedPrimarySunRevision =
			revision;
	}


	private void ApplyCausticsState()
	{
		_runtime.GetCausticsState(
			out OceanCausticsState state,
			out int revision);


		if (_appliedCausticsRevision ==
			revision)
		{
			return;
		}


		OceanUnderwaterCompositorEffect effect =
			_effect;


		if (effect == null)
		{
			return;
		}


		OceanCausticsGpuState gpuState =
			state.ToGpuState();


		RenderingServer.CallOnRenderThread(
			Callable.From(() =>
				effect.SetCausticsState(
					gpuState)));


		_appliedCausticsRevision =
			revision;
	}
}
