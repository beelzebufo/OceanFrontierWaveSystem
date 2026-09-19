using Godot;
using OceanFrontier.Water.Waves.AnimatedWaves;

namespace OceanFrontier.Water.Debug;

/// <summary>Diagnostic-only camera framing; no wave data access.</summary>
public partial class OceanDiagnosticCameraController : Camera3D
{
	internal enum ViewMode { Perspective, Top, SideX, SideZ }
	private readonly float[] _zoomFactors = { 1.0f, 1.0f, 1.0f, 1.0f };
	private float _moveSpeed = 5.0f;
	private float _lodWorldSize;
	private float _squareRadius;
	private float _automaticPerspectiveDistance;
	private float _automaticOrthoSize;
	private Vector3 _target;
	private ViewMode _mode;
	private bool _hasFrame;

	internal void Frame(ViewMode mode, AnimatedWaveLodSlice slice)
	{
		_mode = mode;
		_target = new Vector3(slice.CenterXZ.X, 0, slice.CenterXZ.Y);
		_lodWorldSize = slice.WorldSize;
		_squareRadius = _lodWorldSize * 0.5f * Mathf.Sqrt(2.0f);
		float aspect = Mathf.Max(0.01f, GetViewport().GetVisibleRect().Size.Aspect());
		_moveSpeed = _lodWorldSize * 0.8f;
		Near = 0.1f;
		if (mode == ViewMode.Perspective)
		{
			Projection = ProjectionType.Perspective;
			float verticalHalfFov = Mathf.DegToRad(Fov) * 0.5f;
			float horizontalHalfFov = Mathf.Atan(Mathf.Tan(verticalHalfFov) * aspect);
			float narrowHalfFov = Mathf.Min(verticalHalfFov, horizontalHalfFov);
			_automaticPerspectiveDistance = _squareRadius / Mathf.Sin(narrowHalfFov) * 1.3f;
			float distance = ClampPerspectiveDistance(
				_automaticPerspectiveDistance * _zoomFactors[(int)mode]);
			_zoomFactors[(int)mode] = distance / _automaticPerspectiveDistance;
			GlobalPosition = _target + new Vector3(0, 0.85f, 0.8f).Normalized() * distance;
			LookAt(_target);
		}
		else
		{
			Projection = ProjectionType.Orthogonal;
			_automaticOrthoSize = _lodWorldSize * 1.5f / Mathf.Min(1.0f, aspect);
			Size = ClampOrthoSize(_automaticOrthoSize * _zoomFactors[(int)mode]);
			_zoomFactors[(int)mode] = Size / _automaticOrthoSize;
			GlobalPosition = mode switch
			{
				ViewMode.Top => _target + new Vector3(0, _lodWorldSize * 1.4f, 0),
				ViewMode.SideX => _target + new Vector3(_lodWorldSize * 1.4f, _lodWorldSize * 0.15f, 0),
				_ => _target + new Vector3(0, _lodWorldSize * 0.15f, _lodWorldSize * 1.4f),
			};
			LookAt(_target, mode == ViewMode.Top ? Vector3.Forward : Vector3.Up);
		}
		_hasFrame = true;
		UpdateFar();
	}

	public override void _UnhandledInput(InputEvent inputEvent)
	{
		if (!_hasFrame || inputEvent is not InputEventMouseButton wheel || !wheel.Pressed)
			return;
		int steps = wheel.ButtonIndex switch
		{
			MouseButton.WheelUp => 1,
			MouseButton.WheelDown => -1,
			_ => 0,
		};
		if (steps == 0) return;
		Control hovered = GetViewport().GuiGetHoveredControl();
		if (hovered != null && hovered.MouseFilter != Control.MouseFilterEnum.Ignore)
			return;

		float factor = Mathf.Pow(0.9f, steps);
		if (_mode == ViewMode.Perspective)
		{
			Vector3 offset = GlobalPosition - _target;
			float distance = ClampPerspectiveDistance(offset.Length() * factor);
			GlobalPosition = _target + offset.Normalized() * distance;
			_zoomFactors[(int)_mode] = distance / _automaticPerspectiveDistance;
			UpdateFar();
		}
		else
		{
			Size = ClampOrthoSize(Size * factor);
			_zoomFactors[(int)_mode] = Size / _automaticOrthoSize;
		}
		GetViewport().SetInputAsHandled();
	}

	private float ClampPerspectiveDistance(float distance)
	{
		float minimum = Mathf.Max(0.25f, _lodWorldSize * 0.05f);
		float maximum = Mathf.Max(_lodWorldSize * 8.0f, minimum * 2.0f);
		return Mathf.Clamp(distance, minimum, maximum);
	}

	private float ClampOrthoSize(float size)
	{
		float minimum = Mathf.Max(0.1f, _lodWorldSize * 0.02f);
		return Mathf.Clamp(size, minimum, _lodWorldSize * 4.0f);
	}

	private void UpdateFar()
	{
		float cameraDistance = GlobalPosition.DistanceTo(_target);
		Far = Mathf.Max(500.0f, (cameraDistance + _squareRadius + _lodWorldSize) * 1.2f);
	}

	public override void _Process(double delta)
	{
		if (GetViewport().GuiGetFocusOwner() != null) return;
		Vector3 motion = Vector3.Zero;
		if (Input.IsKeyPressed(Key.W)) motion -= GlobalTransform.Basis.Z;
		if (Input.IsKeyPressed(Key.S)) motion += GlobalTransform.Basis.Z;
		if (Input.IsKeyPressed(Key.A)) motion -= GlobalTransform.Basis.X;
		if (Input.IsKeyPressed(Key.D)) motion += GlobalTransform.Basis.X;
		if (Input.IsKeyPressed(Key.Q)) motion -= Vector3.Up;
		if (Input.IsKeyPressed(Key.E)) motion += Vector3.Up;
		if (motion != Vector3.Zero)
		{
			Vector3 shift = motion.Normalized() * _moveSpeed * (float)delta;
			GlobalPosition += shift;
			_target += shift;
			UpdateFar();
		}
	}
}
