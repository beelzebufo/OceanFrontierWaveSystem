using Godot;

// View angles and interpolated position. Procedural offsets have a separate writer.
public partial class PlayerViewController : Node
{
    [Export] public KinematicCharacterMotor Motor { get; set; }
    [Export] public Node3D ViewRoot { get; set; }
    [Export] public Node3D FacingYaw { get; set; }
    [Export] public Node3D PitchPivot { get; set; }
    [Export] public Label DebugLabel { get; set; }
    [Export(PropertyHint.Range, "0.0001,0.01,0.0001")]
    public float MouseSensitivity { get; set; } = 0.002f;
    [Export(PropertyHint.Range, "0.01,2,0.01")]
    public float PitchSensitivityRatio { get; set; } = 0.625f;
    [Export(PropertyHint.Range, "-89.9,0,0.1")]
    public float MinPitch { get; set; } = -89.9f;
    [Export(PropertyHint.Range, "0,89.9,0.1")]
    public float MaxPitch { get; set; } = 70f;

    public float Yaw => WrapYaw(_inputYaw + _currentPlatformYaw);
    public float InputYaw => _inputYaw;
    public float PlatformYaw => _currentPlatformYaw;
    public float PlatformYawDelta { get; private set; }
    public float RenderPlatformYaw => Mathf.LerpAngle(_previousPlatformYaw,
        _currentPlatformYaw, InterpolationFraction());
    public float Pitch { get; private set; } = Mathf.DegToRad(25f);
    // Applied radians from the most recent process cycle; clamped pitch is reflected.
    public Vector2 LookDelta { get; private set; }

    private Vector2 _pendingLookDelta;
    private float _inputYaw;
    private float _previousPlatformYaw;
    private float _currentPlatformYaw;
    private Node3D _physicsRoot;
    private Vector3 _previousPhysicsOrigin;
    private Vector3 _currentPhysicsOrigin;

    public override void _Ready()
    {
        _physicsRoot = GetParent<Node3D>();
        Motor ??= GetNode<KinematicCharacterMotor>("../KinematicCharacterMotor");
        ViewRoot ??= GetNode<Node3D>("../ViewRoot");
        FacingYaw ??= GetNode<Node3D>("../ViewRoot/FacingYaw");
        PitchPivot ??= GetNode<Node3D>("../ViewRoot/FacingYaw/ViewAnchor/ViewMotionPosition/PitchPivot");
        DebugLabel ??= GetNode<Label>("../DebugHud/ViewDebugLabel");
        ResetViewInterpolation();
        Input.MouseMode = Input.MouseModeEnum.Captured;
        ApplyAngles();
    }

    public void ResetViewInterpolation()
    {
        _previousPhysicsOrigin = _physicsRoot.GlobalPosition;
        _currentPhysicsOrigin = _previousPhysicsOrigin;
        ViewRoot.GlobalPosition = _currentPhysicsOrigin;
    }

    public override void _PhysicsProcess(double delta)
    {
        // This node runs after PlayerLocomotion, which commits the physics root at priority 0.
        _previousPhysicsOrigin = _currentPhysicsOrigin;
        _currentPhysicsOrigin = _physicsRoot.GlobalPosition;
        _previousPlatformYaw = _currentPlatformYaw;
        PlatformYawDelta = ExtractYawTwist(Motor.GroundMotion.RotationDelta,
            Motor.Up.Normalized());
        _currentPlatformYaw = WrapYaw(_currentPlatformYaw + PlatformYawDelta);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventMouseMotion motion ||
            Input.MouseMode != Input.MouseModeEnum.Captured)
            return;

        ApplyLookInput(motion.Relative);
        GetViewport().SetInputAsHandled();
    }

    public void ApplyLookInput(Vector2 relative)
    {
        float oldPitch = Pitch;
        _inputYaw = WrapYaw(_inputYaw - relative.X * MouseSensitivity);
        Pitch = Mathf.Clamp(Pitch - relative.Y * MouseSensitivity * PitchSensitivityRatio,
            Mathf.DegToRad(MinPitch), Mathf.DegToRad(MaxPitch));
        _pendingLookDelta += new Vector2(-relative.X * MouseSensitivity, Pitch - oldPitch);
        ApplyAngles();
    }

    public override void _Process(double delta)
    {
        float alpha = InterpolationFraction();
        ViewRoot.GlobalPosition = _previousPhysicsOrigin.Lerp(_currentPhysicsOrigin, alpha);
        ApplyAngles();
        LookDelta = _pendingLookDelta;
        _pendingLookDelta = Vector2.Zero;
        if (DebugLabel != null)
        {
            Vector3 up = Motor.Up.Normalized();
            float rootUpDot = _physicsRoot.GlobalBasis.Y.Dot(up);
            Vector3 physicsPosition = _physicsRoot.GlobalPosition;
            Vector3 viewPosition = ViewRoot.GlobalPosition;
            DebugLabel.Text = $"AuthYaw: {Mathf.RadToDeg(Yaw):F1}°  Pitch: {Mathf.RadToDeg(Pitch):F1}°  PlatΔ: {Mathf.RadToDeg(PlatformYawDelta):F2}°\n" +
                $"InputYaw: {Mathf.RadToDeg(_inputYaw):F1}°  Plat: {Mathf.RadToDeg(_currentPlatformYaw):F1}° " +
                $"Render: {Mathf.RadToDeg(RenderPlatformYaw):F1}°\n" +
                $"LookDelta: ({Mathf.RadToDeg(LookDelta.X):F2}°, {Mathf.RadToDeg(LookDelta.Y):F2}°)  " +
                $"Root up·Up: {rootUpDot:F3}\n" +
                $"PhysicsPos: {FormatPosition(physicsPosition)}\n" +
                $"ViewPos: {FormatPosition(viewPosition)}\n" +
                $"Interp α: {alpha:F2}  Lag: {physicsPosition.DistanceTo(viewPosition):F3} m";
        }
    }

    private static string FormatPosition(Vector3 position) =>
        $"({position.X:F2}, {position.Y:F2}, {position.Z:F2})";

    private static float WrapYaw(float radians) => Mathf.Wrap(radians, -Mathf.Pi, Mathf.Pi);

    private static float InterpolationFraction() =>
        Mathf.Clamp((float)Engine.GetPhysicsInterpolationFraction(), 0f, 1f);

    private static float ExtractYawTwist(Quaternion delta, Vector3 up)
    {
        Quaternion normalized = delta.Normalized();
        float projection = new Vector3(normalized.X, normalized.Y, normalized.Z).Dot(up);
        float lengthSquared = projection * projection + normalized.W * normalized.W;
        if (lengthSquared < 0.00000001f)
            return 0f;
        float inverseLength = 1f / Mathf.Sqrt(lengthSquared);
        return WrapYaw(2f * Mathf.Atan2(projection * inverseLength,
            normalized.W * inverseLength));
    }

    public Vector3 GetPlanarForward(Vector3 up)
    {
        Vector3 forward = -(new Basis(up.Normalized(), Yaw)).Z;
        return forward.Slide(up.Normalized()).Normalized();
    }

    public Vector3 GetPlanarRight(Vector3 up)
    {
        Vector3 right = (new Basis(up.Normalized(), Yaw)).X;
        return right.Slide(up.Normalized()).Normalized();
    }

    private void ApplyAngles()
    {
        FacingYaw.GlobalBasis = new Basis(Motor.Up.Normalized(),
            WrapYaw(_inputYaw + RenderPlatformYaw));
        PitchPivot.Rotation = new Vector3(Pitch, 0f, 0f);
    }
}
