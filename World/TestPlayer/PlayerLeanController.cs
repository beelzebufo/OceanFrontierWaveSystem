using Godot;

// Hold-to-lean presentation state. The motion compositor asks this component
// for its contribution at Channel.Lean, after earlier channel positions exist.
public partial class PlayerLeanController : Node
{
    [Export] public PlayerIntent Intent { get; set; }
    [Export] public PlayerLocomotion Locomotion { get; set; }
    [Export] public KinematicCharacterMotor Motor { get; set; }
    [Export] public PlayerViewMotionController Motion { get; set; }
    [Export] public CameraObstructionGuard CameraGuard { get; set; }
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float LeanSideOffset { get; set; } = 0.25f;
    [Export(PropertyHint.Range, "0,0.3,0.005")]
    public float LeanDownOffset { get; set; } = 0.045f;
    [Export(PropertyHint.Range, "0,30,0.5")]
    public float LeanRollDegrees { get; set; } = 9f;
    [Export(PropertyHint.Range, "0.1,50,0.1")]
    public float LeanEnterSpeed { get; set; } = 12f;
    [Export(PropertyHint.Range, "0.1,50,0.1")]
    public float LeanExitSpeed { get; set; } = 14f;
    [Export(PropertyHint.Range, "0.1,50,0.1")]
    public float ObstructionRecoverySpeed { get; set; } = 18f;
    [Export] public bool RequireStableGround { get; set; } = true;
    [Export(PropertyHint.Range, "0,20,0.1")]
    public float MaxLeanPlanarSpeed { get; set; } = 4f;

    public float LeanInput { get; private set; }
    public float RequestedLean { get; private set; }
    public float RequestedMagnitude => Mathf.Abs(RequestedLean);
    public Vector3 RequestedPosition { get; private set; }
    public float RequestedRollDegrees { get; private set; }
    public float CoverageFraction { get; private set; } = 1f;
    public float ContactFraction { get; private set; } = 1f;
    public float GeometrySafeFraction { get; private set; } = 1f;
    public float RecoveredSafetyCap { get; private set; } = 1f;
    public float AppliedLean { get; private set; }
    public Vector3 AppliedPosition { get; private set; }
    public float AppliedRollDegrees { get; private set; }
    public bool IsLeanBlocked { get; private set; }
    public bool LeanAllowedByState { get; private set; }

    private int _capSide;

    public override void _Ready()
    {
        Intent ??= GetNode<PlayerIntent>("../PlayerIntent");
        Locomotion ??= GetParent<PlayerLocomotion>();
        Motor ??= GetNode<KinematicCharacterMotor>("../KinematicCharacterMotor");
        Motion ??= GetNode<PlayerViewMotionController>("../PlayerViewMotionController");
        CameraGuard ??= GetNode<CameraObstructionGuard>("../CameraObstructionGuard");
        ResetLean();
    }

    public override void _Process(double delta)
    {
        float dt = float.IsFinite((float)delta) ? Mathf.Max((float)delta, 0f) : 0f;
        LeanInput = Intent?.ReadLeanAxis() ?? 0f;
        Vector3 up = Motor?.Up ?? Vector3.Up;
        Vector3 velocity = Locomotion?.LocomotionVelocity ?? Vector3.Zero;
        bool validUp = up.IsFinite() && up.LengthSquared() > 0.000001f;
        if (validUp)
            up = up.Normalized();
        float planarSpeed = validUp && velocity.IsFinite()
            ? velocity.Slide(up).Length() : float.PositiveInfinity;
        LeanAllowedByState = validUp && Motor != null &&
            (!RequireStableGround || Motor.Contacts.IsStable) &&
            planarSpeed <= Mathf.Max(MaxLeanPlanarSpeed, 0f);
        float target = LeanAllowedByState ? LeanInput : 0f;
        float speed = Mathf.Abs(target) > Mathf.Abs(RequestedLean)
            ? LeanEnterSpeed : LeanExitSpeed;
        RequestedLean = Mathf.Lerp(RequestedLean, target, Alpha(speed, dt));
        if (target == 0f && Mathf.Abs(RequestedLean) < 0.00001f)
            RequestedLean = 0f;
    }

    // Called once by PlayerViewMotionController exactly at Channel.Lean.
    public void ContributeAtLeanChannel(Vector3 earlierChannelPosition, float delta)
    {
        float magnitude = RequestedMagnitude;
        float sign = Mathf.Sign(RequestedLean);
        RequestedPosition = new Vector3(sign * Mathf.Max(LeanSideOffset, 0f),
            -Mathf.Max(LeanDownOffset, 0f), 0f) * magnitude;
        RequestedRollDegrees = -sign * Mathf.Max(LeanRollDegrees, 0f) * magnitude;
        if (magnitude <= 0.00001f)
        {
            CoverageFraction = ContactFraction = GeometrySafeFraction = 1f;
            RecoveredSafetyCap = 1f;
            _capSide = 0;
            AppliedLean = AppliedRollDegrees = 0f;
            AppliedPosition = Vector3.Zero;
            IsLeanBlocked = false;
            return;
        }

        AdditionalCameraOffsetEvaluation evaluation = CameraGuard != null
            ? CameraGuard.EvaluateAdditionalOffset(earlierChannelPosition, RequestedPosition)
            : new AdditionalCameraOffsetEvaluation(1f, 1f);
        CoverageFraction = evaluation.CoverageFraction;
        ContactFraction = evaluation.ContactFraction;
        GeometrySafeFraction = evaluation.FinalFraction;
        int side = sign < 0f ? -1 : 1;
        if (_capSide != side || GeometrySafeFraction < RecoveredSafetyCap)
            RecoveredSafetyCap = GeometrySafeFraction;
        else
            RecoveredSafetyCap = Mathf.Lerp(RecoveredSafetyCap, GeometrySafeFraction,
                Alpha(ObstructionRecoverySpeed, delta));
        _capSide = side;
        float safeFraction = Mathf.Min(GeometrySafeFraction, RecoveredSafetyCap);
        AppliedLean = RequestedLean * safeFraction;
        AppliedPosition = RequestedPosition * safeFraction;
        AppliedRollDegrees = RequestedRollDegrees * safeFraction;
        IsLeanBlocked = safeFraction < 0.999f;
        Motion.AddPosition(PlayerViewMotionController.Channel.Lean, AppliedPosition);
        Motion.AddRotation(PlayerViewMotionController.Channel.Lean,
            new Vector3(0f, 0f, Mathf.DegToRad(AppliedRollDegrees)));
    }

    private static float Alpha(float speed, float delta) =>
        1f - Mathf.Exp(-Mathf.Max(float.IsFinite(speed) ? speed : 0f, 0f) *
            Mathf.Max(float.IsFinite(delta) ? delta : 0f, 0f));

    public void ResetLean()
    {
        LeanInput = RequestedLean = RequestedRollDegrees = AppliedLean = AppliedRollDegrees = 0f;
        RequestedPosition = AppliedPosition = Vector3.Zero;
        CoverageFraction = ContactFraction = GeometrySafeFraction = RecoveredSafetyCap = 1f;
        IsLeanBlocked = LeanAllowedByState = false;
        _capSide = 0;
    }
}
