using Godot;

// Diagnostic camera only; it never changes player or first-person transforms.
public partial class DebugThirdPersonView : Node3D
{
	private const uint FirstPersonMask = 1u << 16;
	private const uint ExternalMask = 1u << 17;

	[Export] public PlayerLocomotion Player { get; set; }
	[Export] public Node3D FacingYaw { get; set; }
	[Export] public Camera3D FirstPersonCamera { get; set; }
	[Export] public Camera3D ThirdPersonCamera { get; set; }
	[Export] public Vector3 Offset { get; set; } = new(0f, 2.0f, 3.8f);
	[Export] public float LookAtHeight { get; set; } = 1.15f;

	public bool ThirdPersonActive { get; private set; }

	public override void _Ready()
	{
		Player ??= GetParent<PlayerLocomotion>();
		FacingYaw ??= GetNode<Node3D>("../ViewRoot/FacingYaw");
		FirstPersonCamera ??= GetNode<Camera3D>("../ViewRoot/FacingYaw/ViewAnchor/ViewMotionPosition/PitchPivot/ViewMotionRotation/Camera3D");
		ThirdPersonCamera ??= GetNode<Camera3D>("Camera3D");
		if (Player == null || FacingYaw == null || FirstPersonCamera == null || ThirdPersonCamera == null)
		{
			GD.PushError("DebugThirdPersonView camera references are incomplete.");
			SetProcess(false);
			return;
		}
		ThirdPersonCamera.CullMask = (FirstPersonCamera.CullMask | ExternalMask) & ~FirstPersonMask;
		ThirdPersonCamera.Fov = FirstPersonCamera.Fov;
		ThirdPersonCamera.Far = FirstPersonCamera.Far;
	}

	public override void _Process(double delta)
	{
		Vector3 up = Vector3.Up;
		GlobalPosition = Player.GlobalPosition + FacingYaw.GlobalTransform.Basis * Offset;
		ThirdPersonCamera.LookAt(Player.GlobalPosition + up * LookAtHeight, up);
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event is not InputEventKey key || !key.Pressed || key.Echo ||
			(key.Keycode != Key.F3 && key.PhysicalKeycode != Key.F3))
			return;
		ThirdPersonActive = !ThirdPersonActive;
		if (ThirdPersonActive)
			ThirdPersonCamera.MakeCurrent();
		else
			FirstPersonCamera.MakeCurrent();
		GetViewport().SetInputAsHandled();
	}
}
