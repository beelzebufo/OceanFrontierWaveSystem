using System;
using Godot;
using Godot.Collections;

namespace OceanFrontier.Water.Rendering.Underwater;

/// <summary>
/// Main-thread owner for the forced UW-2A compositor toggle.
/// </summary>
public partial class OceanUnderwaterController : Node
{
	private bool _forceUnderwater;

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


			if (_effect != null)
			{
				_effect.Enabled =
					value;
			}
		}
	}


	public override void _Ready()
	{
		_camera =
			GetViewport()?.GetCamera3D();


		if (_camera == null)
		{
			GD.PushError(
				"OceanUnderwaterController could not resolve the current Camera3D.");


			return;
		}


		_effect =
			new OceanUnderwaterCompositorEffect
			{
				Enabled =
					_forceUnderwater,
			};


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
	}


	public override void _ExitTree()
	{
		OceanUnderwaterCompositorEffect effect =
			_effect;


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
	}
}
