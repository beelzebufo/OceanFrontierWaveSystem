using Godot;

public partial class PlayerLocomotion : CharacterBody3D
{
    [Export] public PlayerIntent Intent { get; set; }
    [Export] public KinematicCharacterMotor Motor { get; set; }
    [Export] public Label DebugLabel { get; set; }
    [Export] public CharacterDiagnosticConsole DiagnosticConsole { get; set; }
    [Export(PropertyHint.Range, "0.1,30,0.1")] public float WalkSpeed { get; set; } = 8f;
    [Export(PropertyHint.Range, "0.1,100,0.1")] public float Gravity { get; set; } = 24f;
    [Export(PropertyHint.Range, "0.1,20,0.1")] public float JumpSpeed { get; set; } = 6f;
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float CoyoteTime { get; set; } = 0.10f;
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float JumpBufferTime { get; set; } = 0.10f;
    [Export(PropertyHint.Range, "0,50,0.1")] public float AirAcceleration { get; set; } = 15f;

    private Vector3 _velocity;
    private Vector3 _inheritedGroundVelocity;
    private Vector3 _lastGroundVelocity;
    private Rid _lastGroundRid;
    private ulong _lastGroundObjectId;
    private float _coyoteRemaining;
    private float _jumpBufferRemaining;
    private bool _coyoteAvailable;

    public Vector3 LocomotionVelocity => _velocity;
    public Vector3 InheritedGroundVelocity => _inheritedGroundVelocity;
    public Vector3 LastGroundVelocity => _lastGroundVelocity;
    public float CoyoteRemaining => _coyoteRemaining;
    public float JumpBufferRemaining => _jumpBufferRemaining;
    public bool JumpAcceptedThisTick { get; private set; }
    public bool CoyoteJumpThisTick { get; private set; }
    public bool JumpBufferedThisTick { get; private set; }
    public bool AirborneEnteredThisTick { get; private set; }
    public bool LandedThisTick { get; private set; }
    public bool GroundVelocityTransferredThisTick { get; private set; }
    public Vector3 TransferredGroundVelocity { get; private set; }
    public float LandingPreImpactVerticalSpeed { get; private set; }
    public Vector3 JumpLaunchVelocity { get; private set; }
    public Rid LastGroundRid => _lastGroundRid;
    public ulong LastGroundObjectId => _lastGroundObjectId;

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
        Vector3 previousGroundVelocity = previous.GroundColliderVelocity;
        Rid previousGroundRid = previous.GroundColliderRid;
        ulong previousGroundId = previous.GroundColliderObjectId;
        ResetFrameEvents();

        if (wasStable)
        {
            _lastGroundVelocity = previousGroundVelocity;
            _lastGroundRid = previousGroundRid;
            _lastGroundObjectId = previousGroundId;
            _coyoteRemaining = CoyoteTime;
            _coyoteAvailable = true;
        }
        else
            _coyoteRemaining = Mathf.Max(0f, _coyoteRemaining - dt);

        bool pressed = Intent.ConsumeJumpPressed();
        if (pressed)
            _jumpBufferRemaining = Mathf.Max(JumpBufferTime, dt);
        else
            _jumpBufferRemaining = Mathf.Max(0f, _jumpBufferRemaining - dt);

        Vector3 inputDirection = Intent.ReadWorldDirection();
        Vector3 groundMove = inputDirection * WalkSpeed;
        if (wasStable && !groundMove.IsZeroApprox())
            groundMove = groundMove.Slide(previous.GroundNormal).Normalized() * WalkSpeed;

        bool canJump = wasStable || (_coyoteAvailable && _coyoteRemaining > 0f);
        bool jump = _jumpBufferRemaining > 0f && canJump;
        JumpBufferedThisTick = pressed && !jump;
        if (jump)
        {
            JumpAcceptedThisTick = true;
            CoyoteJumpThisTick = !wasStable;
            _jumpBufferRemaining = 0f;
            _coyoteRemaining = 0f;
            _coyoteAvailable = false;
            if (wasStable)
            {
                // Carry is skipped this tick. The point velocity is inherited
                // exactly once, including the vertical component of an elevator.
                _inheritedGroundVelocity = previousGroundVelocity;
                _velocity = groundMove + previousGroundVelocity;
                _velocity += up * (JumpSpeed - groundMove.Dot(up));
                RecordVelocityTransfer(previousGroundVelocity);
            }
            else
            {
                // Walk-off already transferred the platform velocity. A
                // coyote jump replaces only the vertical launch component.
                float desiredUpSpeed = _lastGroundVelocity.Dot(up) + JumpSpeed;
                _velocity += up * (desiredUpSpeed - _velocity.Dot(up));
            }
            JumpLaunchVelocity = _velocity;
        }
        else if (wasStable)
            _velocity = groundMove;
        else if (!inputDirection.IsZeroApprox())
        {
            Vector3 inheritedHorizontal = _inheritedGroundVelocity -
                up * _inheritedGroundVelocity.Dot(up);
            Vector3 currentHorizontal = _velocity - up * _velocity.Dot(up);
            Vector3 controlled = currentHorizontal - inheritedHorizontal;
            controlled = controlled.MoveToward(inputDirection * WalkSpeed, AirAcceleration * dt);
            _velocity = controlled + inheritedHorizontal + up * _velocity.Dot(up);
        }

        float preImpactUpSpeed;
        if (wasStable && !jump)
        {
            // Retain the FPC-1C walking/support motion without accumulating
            // negative gravity in the persistent locomotion velocity.
            Vector3 motion = groundMove.IsZeroApprox() ? Vector3.Zero :
                (groundMove - up * Gravity * dt) * dt;
            preImpactUpSpeed = 0f;
            Motor.Simulate(motion, groundMove * dt);
        }
        else
        {
            _velocity -= up * Gravity * dt;
            preImpactUpSpeed = _velocity.Dot(up);
            Motor.Simulate(_velocity * dt, Vector3.Zero,
                allowGrounding: !jump && preImpactUpSpeed <= 0f,
                allowPlatformCarry: !jump);
        }

        CharacterContactState contact = Motor.Contacts;
        if (contact.HasCeiling && _velocity.Dot(up) > 0f)
            _velocity -= up * _velocity.Dot(up);

        if (contact.IsStable)
        {
            if (!wasStable)
            {
                LandedThisTick = true;
                LandingPreImpactVerticalSpeed = preImpactUpSpeed;
            }
            if (_velocity.Dot(up) < 0f)
                _velocity -= up * _velocity.Dot(up);
            _inheritedGroundVelocity = Vector3.Zero;
            _lastGroundVelocity = contact.GroundColliderVelocity;
            _lastGroundRid = contact.GroundColliderRid;
            _lastGroundObjectId = contact.GroundColliderObjectId;
            _coyoteRemaining = CoyoteTime;
            _coyoteAvailable = true;
        }
        else if (wasStable && !jump)
        {
            // The carry phase handled this frame's platform displacement.
            // Transfer point velocity only for subsequent airborne frames.
            // The last capsule-edge normal can be steep even while the
            // support is still classified walkable. Departure keeps the
            // horizontal player input, not that contact's vertical tangent.
            _velocity = inputDirection * WalkSpeed + previousGroundVelocity;
            _inheritedGroundVelocity = previousGroundVelocity;
            RecordVelocityTransfer(previousGroundVelocity);
        }

        AirborneEnteredThisTick = wasStable && !contact.IsStable;
        DiagnosticConsole?.Observe(contact, wasStable, Motor.GroundMotion,
            Engine.GetPhysicsFrames(), this);

        if (DebugLabel != null)
            DebugLabel.Text = $"Grounded: {contact.IsGrounded}  Walkable: {contact.IsWalkable}\n" +
                $"Normal: {contact.GroundNormal}  Slope: {contact.SlopeAngle:F1}°\n" +
                $"Wall: {contact.HasWall}  Steep: {contact.HasUnwalkableSlope}  Ceiling: {contact.HasCeiling}\n" +
                $"Contacts: {contact.ContactCount}  Bounces: {contact.BounceCount}  Recovery: {contact.RecoveryCount}\n" +
                $"Collider RID: {contact.GroundColliderRid}  ID: {contact.GroundColliderObjectId}\n" +
                $"Step: {contact.StepStatus}  Rise: {contact.StepRise:F3} m\n" +
                $"Snap: {contact.SnapStatus}  Drop: {contact.SnapDistance:F3} m\n" +
                $"Platform carry: {Motor.GroundMotion.CarryTravelled:F3}/{Motor.GroundMotion.CarryRequested:F3} m  Blocked: {Motor.GroundMotion.CarryBlocked}\n" +
                $"State: {(contact.IsStable ? "Grounded" : "Airborne")}  Velocity: {_velocity}  Up: {_velocity.Dot(up):F2}\n" +
                $"Coyote: {_coyoteRemaining:F2}s  Buffer: {_jumpBufferRemaining:F2}s  Last ground v: {_lastGroundVelocity}\n" +
                $"Inherited ground v: {_inheritedGroundVelocity}";
    }

    private void RecordVelocityTransfer(Vector3 groundVelocity)
    {
        if (groundVelocity.LengthSquared() <= 0.000001f)
            return;
        GroundVelocityTransferredThisTick = true;
        TransferredGroundVelocity = groundVelocity;
    }

    private void ResetFrameEvents()
    {
        JumpAcceptedThisTick = CoyoteJumpThisTick = JumpBufferedThisTick = false;
        AirborneEnteredThisTick = LandedThisTick = GroundVelocityTransferredThisTick = false;
        TransferredGroundVelocity = JumpLaunchVelocity = Vector3.Zero;
        LandingPreImpactVerticalSpeed = 0f;
    }
}
