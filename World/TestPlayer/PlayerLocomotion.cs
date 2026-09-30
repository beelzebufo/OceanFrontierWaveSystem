using Godot;

public partial class PlayerLocomotion : CharacterBody3D
{
    [Export] public PlayerIntent Intent { get; set; }
    [Export] public KinematicCharacterMotor Motor { get; set; }
    [Export] public Label DebugLabel { get; set; }
    [Export] public CharacterDiagnosticConsole DiagnosticConsole { get; set; }
    [Export(PropertyHint.Range, "0.1,30,0.1")] public float WalkSpeed { get; set; } = 8f;
    [Export(PropertyHint.Range, "0.1,100,0.1")] public float Gravity { get; set; } = 24f;

    private float _verticalSpeed;

    public override void _Ready()
    {
        Intent ??= GetNode<PlayerIntent>("PlayerIntent");
        Motor ??= GetNode<KinematicCharacterMotor>("KinematicCharacterMotor");
        DebugLabel ??= GetNode<Label>("DebugHud/DebugLabel");
        DiagnosticConsole ??= GetNode<CharacterDiagnosticConsole>("DiagnosticConsole");
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        Vector3 up = Motor.Up.Normalized();
        CharacterContactState previous = Motor.Contacts;
        bool wasStable = previous.IsStable;
        Vector3 horizontal = Intent.ReadWorldDirection() * WalkSpeed;
        if (previous.IsStable && horizontal.LengthSquared() > 0f)
            horizontal = horizontal.Slide(previous.GroundNormal).Normalized() * WalkSpeed;

        if (previous.IsStable && _verticalSpeed < 0f)
            _verticalSpeed = 0f;
        // A resting walkable slope supplies support. Zero motion still runs the
        // overlap/contact query, without turning gravity into downhill drift.
        if (previous.IsStable && horizontal.IsZeroApprox())
        {
            _verticalSpeed = 0f;
            Motor.Simulate(Vector3.Zero, Vector3.Zero);
        }
        else
        {
            _verticalSpeed -= Gravity * dt;
            Motor.Simulate((horizontal + up * _verticalSpeed) * dt,
                horizontal * dt, _verticalSpeed <= 0f);
        }

        CharacterContactState contact = Motor.Contacts;
        if (contact.IsStable && _verticalSpeed < 0f || contact.HasCeiling && _verticalSpeed > 0f)
            _verticalSpeed = 0f;

        DiagnosticConsole?.Observe(contact, wasStable, Engine.GetPhysicsFrames());

        if (DebugLabel != null)
            DebugLabel.Text = $"Grounded: {contact.IsGrounded}  Walkable: {contact.IsWalkable}\n" +
                $"Normal: {contact.GroundNormal}  Slope: {contact.SlopeAngle:F1}°\n" +
                $"Wall: {contact.HasWall}  Steep: {contact.HasUnwalkableSlope}  Ceiling: {contact.HasCeiling}\n" +
                $"Contacts: {contact.ContactCount}  Bounces: {contact.BounceCount}  Recovery: {contact.RecoveryCount}\n" +
                $"Collider RID: {contact.GroundColliderRid}  ID: {contact.GroundColliderObjectId}\n" +
                $"Step: {contact.StepStatus}  Rise: {contact.StepRise:F3} m\n" +
                $"Snap: {contact.SnapStatus}  Drop: {contact.SnapDistance:F3} m";
    }
}
