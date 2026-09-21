using Godot;
using OceanFrontier.Water.Waves.AnimatedWaves;

namespace OceanFrontier.Water.Debug;

/// <summary>
/// Diagnostic-only camera framing and vessel camera rig.
/// No wave data access.
/// </summary>
public partial class OceanDiagnosticCameraController : Camera3D
{
	internal enum ViewMode
	{
		Perspective,
		Top,
		SideX,
		SideZ,
	}

	private readonly float[] _zoomFactors =
	{
		1.0f,
		1.0f,
		1.0f,
		1.0f,
	};

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

	//
	// Vessel camera.
	//

	private RigidBody3D _vessel;

	private bool _vesselModeEnabled;
	private bool _vesselMastEnabled;
	private bool _vesselCameraInitialized;

	//
	// Camera position on the vessel's local XZ footprint.
	//
	// X = port/starboard.
	// Y = local Z = bow/stern.
	//

	private Vector2 _vesselLocalXZ;

	private Vector2 _vesselHalfExtents =
		new(1.75f, 6.5f);

	private float _vesselHalfHeight =
		0.85f;

	private float _vesselLookYawOffset;
	private float _vesselLookPitch;

	[Export(PropertyHint.Range, "0,10,0.05,or_greater")]
	public float VesselDeckEyeHeight { get; set; } =
		1.5f;

	[Export(PropertyHint.Range, "0,100,0.1,or_greater")]
	public float VesselMastEyeHeight { get; set; } =
		20.0f;

	[Export(PropertyHint.Range, "0.1,20,0.1,or_greater")]
	public float VesselMoveSpeed { get; set; } =
		2.0f;

	/// <summary>
	/// Exponential position smoothing time in seconds.
	/// Zero disables smoothing.
	/// </summary>
	[Export(PropertyHint.Range, "0,2,0.01")]
	public float VesselCameraDamping { get; set; } =
		0.35f;

	[Export(PropertyHint.Range, "0,0.5,0.01")]
	public float VesselEdgeMargin { get; set; } =
		0.15f;

	internal bool VesselModeEnabled =>
		_vesselModeEnabled;

	internal bool VesselMastEnabled =>
		_vesselMastEnabled;

	internal void EnterVesselMode(
		RigidBody3D vessel)
	{
		if (vessel == null)
		{
			return;
		}

		EndMouseLook();

		_vessel =
			vessel;

		//
		// Read the diagnostic hull dimensions from its collider.
		// Do not duplicate the 13 × 3.5 × 1.7 dimensions here.
		//

		CollisionShape3D collision =
			_vessel.GetNodeOrNull<CollisionShape3D>(
				"CollisionShape3D");

		if (collision?.Shape is BoxShape3D box)
		{
			Vector3 size =
				box.Size;

			_vesselHalfExtents =
				new Vector2(
					Mathf.Abs(size.X) * 0.5f,
					Mathf.Abs(size.Z) * 0.5f);

			_vesselHalfHeight =
				Mathf.Abs(size.Y) * 0.5f;
		}

		_vesselModeEnabled =
			true;

		_freeCameraEnabled =
			false;

		_vesselCameraInitialized =
			false;

		_vesselLocalXZ =
			Vector2.Zero;

		_vesselLookYawOffset =
			0.0f;

		_vesselLookPitch =
			0.0f;

		Projection =
			ProjectionType.Perspective;

		Near =
			0.05f;

		_hasFrame =
			true;

		_target =
			_vessel.GlobalPosition;

		_frameCenter =
			_target;

		//
		// First update snaps directly to the requested vessel
		// camera position. Subsequent updates are damped.
		//

		UpdateVesselCamera(
			0.0);
	}

	internal void ExitVesselMode()
	{
		if (!_vesselModeEnabled)
		{
			return;
		}

		EndMouseLook();

		_vesselModeEnabled =
			false;

		_vesselCameraInitialized =
			false;

		_vessel =
			null;

		SyncLookAngles();
	}

	internal void SetVesselMastEnabled(
		bool enabled)
	{
		_vesselMastEnabled =
			enabled;

		//
		// Do not snap when changing deck/mast mode.
		// Existing damping handles the transition.
		//
	}

	internal void SetVesselCameraDamping(
		float seconds)
	{
		VesselCameraDamping =
			Mathf.Max(
				0.0f,
				seconds);
	}

	internal void SetFreeCameraEnabled(
		bool enabled)
	{
		if (_freeCameraEnabled == enabled)
		{
			return;
		}

		if (enabled &&
			_vesselModeEnabled)
		{
			ExitVesselMode();
		}

		_freeCameraEnabled =
			enabled;

		if (!enabled)
		{
			EndMouseLook();
		}

		UpdateFar();
	}

	internal void Frame(
		ViewMode mode,
		AnimatedWaveLodSlice slice)
	{
		if (_vesselModeEnabled)
		{
			ExitVesselMode();
		}

		_mode =
			mode;

		_target =
			new Vector3(
				slice.CenterXZ.X,
				0,
				slice.CenterXZ.Y);

		_frameCenter =
			_target;

		_lodWorldSize =
			slice.WorldSize;

		_squareRadius =
			_lodWorldSize *
			0.5f *
			Mathf.Sqrt(2.0f);

		float aspect =
			Mathf.Max(
				0.01f,
				GetViewport()
					.GetVisibleRect()
					.Size
					.Aspect());

		_moveSpeed =
			Mathf.Clamp(
				_lodWorldSize * 0.4f,
				0.5f,
				80.0f);

		Near =
			0.05f;

		if (mode ==
			ViewMode.Perspective)
		{
			Projection =
				ProjectionType.Perspective;

			float verticalHalfFov =
				Mathf.DegToRad(Fov) *
				0.5f;

			float horizontalHalfFov =
				Mathf.Atan(
					Mathf.Tan(verticalHalfFov) *
					aspect);

			float narrowHalfFov =
				Mathf.Min(
					verticalHalfFov,
					horizontalHalfFov);

			_automaticPerspectiveDistance =
				_squareRadius /
				Mathf.Sin(narrowHalfFov) *
				1.3f;

			float distance =
				ClampPerspectiveDistance(
					_automaticPerspectiveDistance *
					_zoomFactors[(int)mode]);

			_zoomFactors[(int)mode] =
				distance /
				_automaticPerspectiveDistance;

			GlobalPosition =
				_target +
				new Vector3(
						0,
						0.85f,
						0.8f)
					.Normalized() *
				distance;

			LookAt(
				_target);
		}
		else
		{
			Projection =
				ProjectionType.Orthogonal;

			_automaticOrthoSize =
				_lodWorldSize *
				1.5f /
				Mathf.Min(
					1.0f,
					aspect);

			Size =
				ClampOrthoSize(
					_automaticOrthoSize *
					_zoomFactors[(int)mode]);

			_zoomFactors[(int)mode] =
				Size /
				_automaticOrthoSize;

			GlobalPosition =
				mode switch
				{
					ViewMode.Top =>
						_target +
						new Vector3(
							0,
							_lodWorldSize * 1.4f,
							0),

					ViewMode.SideX =>
						_target +
						new Vector3(
							_lodWorldSize * 1.4f,
							_lodWorldSize * 0.15f,
							0),

					_ =>
						_target +
						new Vector3(
							0,
							_lodWorldSize * 0.15f,
							_lodWorldSize * 1.4f),
				};

			LookAt(
				_target,
				mode == ViewMode.Top
					? Vector3.Forward
					: Vector3.Up);
		}

		_hasFrame =
			true;

		SyncLookAngles();
		UpdateFar();
	}

	internal void FrameOverview(
		ViewMode mode,
		AnimatedWaveLodSlice slice)
	{
		_zoomFactors[(int)mode] =
			1.0f;

		Frame(
			mode,
			slice);
	}

	public override void _UnhandledInput(
		InputEvent inputEvent)
	{
		if (!_hasFrame ||
			inputEvent is not InputEventMouseButton button ||
			!button.Pressed)
		{
			return;
		}

		//
		// RMB look is available both in free-camera mode
		// and while standing on the vessel.
		//

		if ((_freeCameraEnabled ||
			 _vesselModeEnabled) &&
			button.ButtonIndex ==
			MouseButton.Right)
		{
			Control hoveredControl =
				GetViewport()
					.GuiGetHoveredControl();

			if (hoveredControl != null &&
				hoveredControl.MouseFilter !=
				Control.MouseFilterEnum.Ignore)
			{
				return;
			}

			_mouseModeBeforeLook =
				Input.MouseMode;

			_mouseLooking =
				true;

			Input.MouseMode =
				Input.MouseModeEnum.Captured;

			GetViewport()
				.SetInputAsHandled();

			return;
		}

		//
		// Wheel zoom is intentionally disabled while in
		// free-camera or vessel-camera mode.
		//

		if (_freeCameraEnabled ||
			_vesselModeEnabled)
		{
			return;
		}

		int steps =
			button.ButtonIndex switch
			{
				MouseButton.WheelUp => 1,
				MouseButton.WheelDown => -1,
				_ => 0,
			};

		if (steps == 0)
		{
			return;
		}

		Control hovered =
			GetViewport()
				.GuiGetHoveredControl();

		if (hovered != null &&
			hovered.MouseFilter !=
				Control.MouseFilterEnum.Ignore)
		{
			return;
		}

		float factor =
			Mathf.Pow(
				0.9f,
				steps);

		if (_mode ==
			ViewMode.Perspective)
		{
			Vector3 offset =
				GlobalPosition -
				_target;

			float distance =
				ClampPerspectiveDistance(
					offset.Length() *
					factor);

			GlobalPosition =
				_target +
				offset.Normalized() *
				distance;

			_zoomFactors[(int)_mode] =
				distance /
				_automaticPerspectiveDistance;

			UpdateFar();
		}
		else
		{
			Size =
				ClampOrthoSize(
					Size *
					factor);

			_zoomFactors[(int)_mode] =
				Size /
				_automaticOrthoSize;
		}

		GetViewport()
			.SetInputAsHandled();
	}

	public override void _Input(
		InputEvent inputEvent)
	{
		if (!_mouseLooking)
		{
			return;
		}

		if (inputEvent is InputEventMouseButton button &&
			button.ButtonIndex ==
			MouseButton.Right &&
			!button.Pressed)
		{
			EndMouseLook();

			GetViewport()
				.SetInputAsHandled();

			return;
		}

		if (inputEvent is not InputEventMouseMotion motion)
		{
			return;
		}

		if (_vesselModeEnabled)
		{
			//
			// Vessel look is relative to vessel heading.
			//
			// Do not modify the body's pose and do not inherit
			// its pitch/roll.
			//

			_vesselLookYawOffset -=
				motion.Relative.X *
				MouseSensitivity;

			_vesselLookPitch =
				Mathf.Clamp(
					_vesselLookPitch -
					motion.Relative.Y *
					MouseSensitivity,
					-PitchLimit,
					PitchLimit);

			ApplyVesselOrientation();
		}
		else
		{
			_yaw -=
				motion.Relative.X *
				MouseSensitivity;

			_pitch =
				Mathf.Clamp(
					_pitch -
					motion.Relative.Y *
					MouseSensitivity,
					-PitchLimit,
					PitchLimit);

			GlobalBasis =
				Basis.FromEuler(
					new Vector3(
						_pitch,
						_yaw,
						0.0f));
		}

		GetViewport()
			.SetInputAsHandled();
	}

	private void EndMouseLook()
	{
		if (!_mouseLooking)
		{
			return;
		}

		_mouseLooking =
			false;

		Input.MouseMode =
			_mouseModeBeforeLook;
	}

	private void SyncLookAngles()
	{
		Vector3 forward =
			-GlobalBasis.Z;

		_pitch =
			Mathf.Asin(
				Mathf.Clamp(
					forward.Y,
					-1.0f,
					1.0f));

		Vector3 horizontalForward =
			new(
				forward.X,
				0.0f,
				forward.Z);

		if (horizontalForward.LengthSquared() >
			1e-6f)
		{
			_yaw =
				Mathf.Atan2(
					-forward.X,
					-forward.Z);
		}
		else
		{
			Vector3 right =
				GlobalBasis.X;

			_yaw =
				Mathf.Atan2(
					-right.Z,
					right.X);
		}
	}

	private float ClampPerspectiveDistance(
		float distance)
	{
		float minimum =
			Mathf.Max(
				0.1f,
				_lodWorldSize *
				0.02f);

		float maximum =
			Mathf.Max(
				_lodWorldSize *
				12.0f,
				minimum *
				2.0f);

		return Mathf.Clamp(
			distance,
			minimum,
			maximum);
	}

	private float ClampOrthoSize(
		float size)
	{
		float minimum =
			Mathf.Max(
				0.1f,
				_lodWorldSize *
				0.02f);

		return Mathf.Clamp(
			size,
			minimum,
			_lodWorldSize *
			4.0f);
	}

	private void UpdateFar()
	{
		//
		// Preserve the existing LOD-framing based far plane.
		// In vessel mode the target moves with the hull.
		//

		float cameraDistance =
			Mathf.Max(
				GlobalPosition.DistanceTo(
					_target),
				GlobalPosition.DistanceTo(
					_frameCenter));

		Far =
			Mathf.Max(
				50.0f,
				(cameraDistance +
				 _squareRadius +
				 _lodWorldSize) *
				1.2f);
	}

	public override void _Process(
		double delta)
	{
		//
		// Vessel mode is independent from the old diagnostic
		// free-camera movement.
		//

		if (_vesselModeEnabled)
		{
			UpdateVesselCamera(
				delta);

			return;
		}

		if (!_freeCameraEnabled ||
			!_hasFrame)
		{
			return;
		}

		if (_mouseLooking &&
			!Input.IsMouseButtonPressed(
				MouseButton.Right))
		{
			EndMouseLook();
		}

		Control focusOwner =
			GetViewport()
				.GuiGetFocusOwner();

		if (focusOwner is LineEdit or
			TextEdit or
			SpinBox)
		{
			return;
		}

		Vector3 motion =
			Vector3.Zero;

		Vector3 forward =
			new(
				-Mathf.Sin(_yaw),
				0.0f,
				-Mathf.Cos(_yaw));

		Vector3 right =
			new(
				Mathf.Cos(_yaw),
				0.0f,
				-Mathf.Sin(_yaw));

		if (Input.IsKeyPressed(Key.W))
		{
			motion +=
				forward;
		}

		if (Input.IsKeyPressed(Key.S))
		{
			motion -=
				forward;
		}

		if (Input.IsKeyPressed(Key.A))
		{
			motion -=
				right;
		}

		if (Input.IsKeyPressed(Key.D))
		{
			motion +=
				right;
		}

		if (Input.IsKeyPressed(Key.Q))
		{
			motion -=
				Vector3.Up;
		}

		if (Input.IsKeyPressed(Key.E))
		{
			motion +=
				Vector3.Up;
		}

		if (motion ==
			Vector3.Zero)
		{
			return;
		}

		float speedMultiplier =
			Input.IsKeyPressed(Key.Ctrl)
				? PrecisionMultiplier
				: Input.IsKeyPressed(Key.Shift)
					? FastMultiplier
					: 1.0f;

		Vector3 shift =
			motion.Normalized() *
			_moveSpeed *
			speedMultiplier *
			(float)delta;

		GlobalPosition +=
			shift;

		_target +=
			shift;

		UpdateFar();
	}

	private void UpdateVesselCamera(
		double delta)
	{
		if (_vessel == null ||
			!GodotObject.IsInstanceValid(
				_vessel))
		{
			ExitVesselMode();
			return;
		}

		if (_mouseLooking &&
			!Input.IsMouseButtonPressed(
				MouseButton.Right))
		{
			EndMouseLook();
		}

		Control focusOwner =
			GetViewport()
				.GuiGetFocusOwner();

		bool keyboardBlocked =
			focusOwner is LineEdit or
			TextEdit or
			SpinBox;

		if (!keyboardBlocked)
		{
			UpdateVesselDeckMovement(
				(float)delta);
		}

		//
		// Position on the actual moving/rolling/pitching hull.
		//
		// We use the body's local deck position to follow its
		// physical pose, then add eye height in WORLD up.
		//
		// This avoids a 20 m mast offset producing huge sideways
		// camera excursions when the hull rolls.
		//

		Vector3 localDeckPoint =
			new(
				_vesselLocalXZ.X,
				_vesselHalfHeight,
				_vesselLocalXZ.Y);

		Vector3 deckWorld =
			_vessel.GlobalTransform *
			localDeckPoint;

		float eyeHeight =
			_vesselMastEnabled
				? VesselMastEyeHeight
				: VesselDeckEyeHeight;

		Vector3 targetPosition =
			deckWorld +
			Vector3.Up *
			eyeHeight;

		if (!_vesselCameraInitialized ||
			VesselCameraDamping <=
			0.0f)
		{
			GlobalPosition =
				targetPosition;

			_vesselCameraInitialized =
				true;
		}
		else
		{
			float dt =
				Mathf.Max(
					0.0f,
					(float)delta);

			//
			// Frame-rate independent exponential smoothing.
			//

			float alpha =
				1.0f -
				Mathf.Exp(
					-dt /
					Mathf.Max(
						VesselCameraDamping,
						0.0001f));

			GlobalPosition =
				GlobalPosition.Lerp(
					targetPosition,
					alpha);
		}

		ApplyVesselOrientation();

		_target =
			_vessel.GlobalPosition;

		_frameCenter =
			_target;

		UpdateFar();
	}

	private void UpdateVesselDeckMovement(
		float delta)
	{
		float moveX =
			0.0f;

		float moveZ =
			0.0f;

		//
		// Movement remains vessel-local regardless of camera look:
		//
		// W/S = bow/stern.
		// A/D = port/starboard.
		//

		if (Input.IsKeyPressed(Key.A))
		{
			moveX -=
				1.0f;
		}

		if (Input.IsKeyPressed(Key.D))
		{
			moveX +=
				1.0f;
		}

		if (Input.IsKeyPressed(Key.W))
		{
			moveZ -=
				1.0f;
		}

		if (Input.IsKeyPressed(Key.S))
		{
			moveZ +=
				1.0f;
		}

		Vector2 motion =
			new(
				moveX,
				moveZ);

		if (motion ==
			Vector2.Zero)
		{
			return;
		}

		float speedMultiplier =
			Input.IsKeyPressed(Key.Ctrl)
				? PrecisionMultiplier
				: Input.IsKeyPressed(Key.Shift)
					? FastMultiplier
					: 1.0f;

		_vesselLocalXZ +=
			motion.Normalized() *
			VesselMoveSpeed *
			speedMultiplier *
			delta;

		float margin =
			Mathf.Max(
				0.0f,
				VesselEdgeMargin);

		float maxX =
			Mathf.Max(
				0.0f,
				_vesselHalfExtents.X -
				margin);

		float maxZ =
			Mathf.Max(
				0.0f,
				_vesselHalfExtents.Y -
				margin);

		_vesselLocalXZ.X =
			Mathf.Clamp(
				_vesselLocalXZ.X,
				-maxX,
				maxX);

		_vesselLocalXZ.Y =
			Mathf.Clamp(
				_vesselLocalXZ.Y,
				-maxZ,
				maxZ);
	}

	private void ApplyVesselOrientation()
	{
		if (_vessel == null ||
			!GodotObject.IsInstanceValid(
				_vessel))
		{
			return;
		}

		//
		// Extract only vessel heading.
		//
		// Hull pitch and roll are deliberately rejected so the
		// camera does not inherit full wave-induced orientation.
		//

		Vector3 vesselForward =
			-_vessel.GlobalBasis.Z;

		vesselForward.Y =
			0.0f;

		float vesselYaw =
			0.0f;

		if (vesselForward.LengthSquared() >
			1e-6f)
		{
			vesselForward =
				vesselForward.Normalized();

			vesselYaw =
				Mathf.Atan2(
					-vesselForward.X,
					-vesselForward.Z);
		}
		else
		{
			Vector3 vesselRight =
				_vessel.GlobalBasis.X;

			vesselRight.Y =
				0.0f;

			if (vesselRight.LengthSquared() >
				1e-6f)
			{
				vesselRight =
					vesselRight.Normalized();

				vesselYaw =
					Mathf.Atan2(
						-vesselRight.Z,
						vesselRight.X);
			}
		}

		float cameraYaw =
			vesselYaw +
			_vesselLookYawOffset;

		GlobalBasis =
			Basis.FromEuler(
				new Vector3(
					_vesselLookPitch,
					cameraYaw,
					0.0f));
	}

	public override void _ExitTree()
	{
		EndMouseLook();

		_vessel =
			null;

		_vesselModeEnabled =
			false;
	}
}