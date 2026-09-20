using Godot;
using OceanFrontier.Water.Waves.AnimatedWaves;

namespace OceanFrontier.Water.Debug;

/// <summary>Diagnostic-only camera framing; no wave data access.</summary>
public partial class OceanDiagnosticCameraController : Camera3D
{
	internal enum ViewMode { Perspective, Top, SideX, SideZ }
	private readonly float[] _zoomFactors = { 1.0f, 1.0f, 1.0f, 1.0f };
	private const float MouseSensitivity = 0.0025f;
	private const float PitchLimit = 89.0f * Mathf.Pi / 180.0f;
	private const float FastMultiplier = 4.0f;
	private const float PrecisionMultiplier = 0.25f;
	private float _moveSpeed;
	private float _lodWorldSize;
	private float _squareRadius;
	private float _automaticPerspectiveDistance;
	private float _automaticOrthoSize;
	private Vector3 _target;
	private Vector3 _frameCenter;
	private ViewMode _mode;
	private bool _hasFrame;
	private bool _freeCameraEnabled;
	private bool _mouseLooking;
	private float _yaw;
	private float _pitch;
	private Input.MouseModeEnum _mouseModeBeforeLook;

	internal void SetFreeCameraEnabled(bool enabled)
	{
		if (_freeCameraEnabled == enabled) return;
		_freeCameraEnabled = enabled;
		if (!enabled) EndMouseLook();
		UpdateFar();
	}

	internal void Frame(ViewMode mode, AnimatedWaveLodSlice slice)
	{
		_mode = mode;
		_target = new Vector3(slice.CenterXZ.X, 0, slice.CenterXZ.Y);
		_frameCenter = _target;
		_lodWorldSize = slice.WorldSize;
		_squareRadius = _lodWorldSize * 0.5f * Mathf.Sqrt(2.0f);
		float aspect = Mathf.Max(0.01f, GetViewport().GetVisibleRect().Size.Aspect());
		_moveSpeed = Mathf.Clamp(_lodWorldSize * 0.4f, 0.5f, 80.0f);
		Near = 0.05f;
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
		SyncLookAngles();
		UpdateFar();
	}

	internal void FrameOverview(ViewMode mode, AnimatedWaveLodSlice slice)
	{
		_zoomFactors[(int)mode] = 1.0f;
		Frame(mode, slice);
	}

	public override void _UnhandledInput(InputEvent inputEvent)
	{
		if (!_hasFrame || inputEvent is not InputEventMouseButton wheel || !wheel.Pressed) return;
		if (_freeCameraEnabled && wheel.ButtonIndex == MouseButton.Right)
		{
			Control hoveredControl = GetViewport().GuiGetHoveredControl();
			if (hoveredControl != null && hoveredControl.MouseFilter != Control.MouseFilterEnum.Ignore)
				return;
			_mouseModeBeforeLook = Input.MouseMode;
			_mouseLooking = true;
			Input.MouseMode = Input.MouseModeEnum.Captured;
			GetViewport().SetInputAsHandled();
			return;
		}
		if (_freeCameraEnabled) return;
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

	public override void _Input(InputEvent inputEvent)
	{
		if (!_mouseLooking) return;
		if (inputEvent is InputEventMouseButton button &&
			button.ButtonIndex == MouseButton.Right && !button.Pressed)
		{
			EndMouseLook();
			GetViewport().SetInputAsHandled();
		}
		else if (inputEvent is InputEventMouseMotion motion)
		{
			_yaw -= motion.Relative.X * MouseSensitivity;
			_pitch = Mathf.Clamp(_pitch - motion.Relative.Y * MouseSensitivity,
				-PitchLimit, PitchLimit);
			GlobalBasis = Basis.FromEuler(new Vector3(_pitch, _yaw, 0.0f));
			GetViewport().SetInputAsHandled();
		}
	}

	private void EndMouseLook()
	{
		if (!_mouseLooking) return;
		_mouseLooking = false;
		Input.MouseMode = _mouseModeBeforeLook;
	}

	private void SyncLookAngles()
	{
		Vector3 forward = -GlobalBasis.Z;
		_pitch = Mathf.Asin(Mathf.Clamp(forward.Y, -1.0f, 1.0f));
		Vector3 horizontalForward = new(forward.X, 0.0f, forward.Z);
		if (horizontalForward.LengthSquared() > 1e-6f)
			_yaw = Mathf.Atan2(-forward.X, -forward.Z);
		else
		{
			Vector3 right = GlobalBasis.X;
			_yaw = Mathf.Atan2(-right.Z, right.X);
		}
	}

	private float ClampPerspectiveDistance(float distance)
	{
		float minimum = Mathf.Max(0.1f, _lodWorldSize * 0.02f);
		float maximum = Mathf.Max(_lodWorldSize * 12.0f, minimum * 2.0f);
		return Mathf.Clamp(distance, minimum, maximum);
	}

	private float ClampOrthoSize(float size)
	{
		float minimum = Mathf.Max(0.1f, _lodWorldSize * 0.02f);
		return Mathf.Clamp(size, minimum, _lodWorldSize * 4.0f);
	}

	private void UpdateFar()
	{
		float cameraDistance = Mathf.Max(GlobalPosition.DistanceTo(_target),
			GlobalPosition.DistanceTo(_frameCenter));
		Far = Mathf.Max(50.0f, (cameraDistance + _squareRadius + _lodWorldSize) * 1.2f);
	}

	public override void _Process(double delta)
	{
		if (!_freeCameraEnabled || !_hasFrame) return;
		if (_mouseLooking && !Input.IsMouseButtonPressed(MouseButton.Right)) EndMouseLook();
		Control focusOwner = GetViewport().GuiGetFocusOwner();
		if (focusOwner is LineEdit or TextEdit or SpinBox) return;
		Vector3 motion = Vector3.Zero;
		Vector3 forward = new(-Mathf.Sin(_yaw), 0.0f, -Mathf.Cos(_yaw));
		Vector3 right = new(Mathf.Cos(_yaw), 0.0f, -Mathf.Sin(_yaw));
		if (Input.IsKeyPressed(Key.W)) motion += forward;
		if (Input.IsKeyPressed(Key.S)) motion -= forward;
		if (Input.IsKeyPressed(Key.A)) motion -= right;
		if (Input.IsKeyPressed(Key.D)) motion += right;
		if (Input.IsKeyPressed(Key.Q)) motion -= Vector3.Up;
		if (Input.IsKeyPressed(Key.E)) motion += Vector3.Up;
		if (motion != Vector3.Zero)
		{
			float speedMultiplier = Input.IsKeyPressed(Key.Ctrl) ? PrecisionMultiplier :
				Input.IsKeyPressed(Key.Shift) ? FastMultiplier : 1.0f;
			Vector3 shift = motion.Normalized() * _moveSpeed * speedMultiplier * (float)delta;
			GlobalPosition += shift;
			_target += shift;
			UpdateFar();
		}
	}

	public override void _ExitTree() => EndMouseLook();
}
