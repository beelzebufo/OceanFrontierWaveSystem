using Godot;

// Base view angles only. Procedural camera motion belongs under ViewMotion.
public partial class PlayerViewController : Node
{
    [Export] public KinematicCharacterMotor Motor { get; set; }
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

    public float Yaw { get; private set; }
    public float Pitch { get; private set; } = Mathf.DegToRad(25f);
    // Applied radians from the most recent process cycle; clamped pitch is reflected.
    public Vector2 LookDelta { get; private set; }

    private Vector2 _pendingLookDelta;

    public override void _Ready()
    {
        Motor ??= GetNode<KinematicCharacterMotor>("../KinematicCharacterMotor");
        FacingYaw ??= GetNode<Node3D>("../FacingYaw");
        PitchPivot ??= GetNode<Node3D>("../FacingYaw/ViewAnchor/PitchPivot");
        DebugLabel ??= GetNode<Label>("../DebugHud/ViewDebugLabel");
        Input.MouseMode = Input.MouseModeEnum.Captured;
        ApplyAngles();
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
        Yaw = Mathf.Wrap(Yaw - relative.X * MouseSensitivity, -Mathf.Pi, Mathf.Pi);
        Pitch = Mathf.Clamp(Pitch - relative.Y * MouseSensitivity * PitchSensitivityRatio,
            Mathf.DegToRad(MinPitch), Mathf.DegToRad(MaxPitch));
        _pendingLookDelta += new Vector2(-relative.X * MouseSensitivity, Pitch - oldPitch);
        ApplyAngles();
    }

    public override void _Process(double delta)
    {
        LookDelta = _pendingLookDelta;
        _pendingLookDelta = Vector2.Zero;
        if (DebugLabel != null)
        {
            Vector3 up = Motor.Up.Normalized();
            float rootUpDot = GetParent<Node3D>().GlobalBasis.Y.Dot(up);
            DebugLabel.Text = $"Yaw: {Mathf.RadToDeg(Yaw):F1}°  Pitch: {Mathf.RadToDeg(Pitch):F1}°  " +
                $"LookDelta: ({Mathf.RadToDeg(LookDelta.X):F2}°, {Mathf.RadToDeg(LookDelta.Y):F2}°)  " +
                $"Root up·Up: {rootUpDot:F3}";
        }
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
        FacingYaw.GlobalBasis = new Basis(Motor.Up.Normalized(), Yaw);
        PitchPivot.Rotation = new Vector3(Pitch, 0f, 0f);
    }
}
