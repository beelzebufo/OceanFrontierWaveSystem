using Godot;

// World-space surface frame: +Z is the outward contact normal, +Y is
// character Up projected onto the wall, and +X = +Y cross +Z.
// A future HandRigAdapter applies each rig's palm/wrist calibration, places
// IK and pole targets, then drives TwoBoneIK3D influence from Weight.
public readonly struct HandBraceTarget
{
    public readonly bool HasPhysicalContact;
    public readonly bool IsPresented;
    public readonly bool SurfaceChanged;
    public readonly Rid ColliderRid;
    public readonly ulong ColliderObjectId;
    public readonly Vector3 RawContactPoint;
    public readonly Vector3 RawSurfaceNormal;
    public readonly Vector3 RawTargetPosition;
    public readonly Basis RawTargetBasis;
    public readonly Vector3 SmoothedTargetPosition;
    public readonly Basis SmoothedTargetBasis;
    public readonly float ContactDistance;
    public readonly float ContactFactor;
    public readonly float Weight;

    public HandBraceTarget(bool hasPhysicalContact, bool isPresented, bool surfaceChanged,
        Rid colliderRid, ulong colliderObjectId, Vector3 rawContactPoint,
        Vector3 rawSurfaceNormal, Vector3 rawTargetPosition, Basis rawTargetBasis,
        Vector3 smoothedTargetPosition, Basis smoothedTargetBasis,
        float contactDistance, float contactFactor, float weight)
    {
        HasPhysicalContact = hasPhysicalContact;
        IsPresented = isPresented;
        SurfaceChanged = surfaceChanged;
        ColliderRid = colliderRid;
        ColliderObjectId = colliderObjectId;
        RawContactPoint = rawContactPoint;
        RawSurfaceNormal = rawSurfaceNormal;
        RawTargetPosition = rawTargetPosition;
        RawTargetBasis = rawTargetBasis;
        SmoothedTargetPosition = smoothedTargetPosition;
        SmoothedTargetBasis = smoothedTargetBasis;
        ContactDistance = contactDistance;
        ContactFactor = contactFactor;
        Weight = weight;
    }
}

// Presentation-only consumer of immutable, physics-tick hand contacts.
public partial class CharacterHandBraceTargets : Node
{
    private const float PoseEpsilon = 0.000001f;
    private const float WeightEpsilon = 0.001f;

    [Export] public CharacterHandContactProbes Probes { get; set; }
    [Export] public CharacterProximitySensor Sensor { get; set; }
    [Export] public PlayerViewController View { get; set; }
    [Export] public KinematicCharacterMotor Motor { get; set; }
    [Export] public bool DebugVisualizeBraceTargets
    {
        get => _debugVisualizeBraceTargets;
        set
        {
            _debugVisualizeBraceTargets = value;
            if (!value)
                _debugVisual?.Hide();
        }
    }
    [Export(PropertyHint.Range, "0.01,1.5,0.01")]
    public float BraceStartDistance { get; set; } = 0.55f;
    [Export(PropertyHint.Range, "0,1.5,0.01")]
    public float BraceFullDistance { get; set; } = 0.28f;
    [Export(PropertyHint.Range, "0,0.1,0.001")]
    public float PalmSurfaceClearance { get; set; } = 0.015f;
    [Export(PropertyHint.Range, "0.1,50,0.1")]
    public float BraceEnterSpeed { get; set; } = 12f;
    [Export(PropertyHint.Range, "0.1,50,0.1")]
    public float BraceExitSpeed { get; set; } = 16f;
    [Export(PropertyHint.Range, "0.1,100,0.1")]
    public float TargetPositionFollowSpeed { get; set; } = 25f;
    [Export(PropertyHint.Range, "0.1,100,0.1")]
    public float OrientationFollowSpeed { get; set; } = 20f;

    private struct HandState
    {
        public float Weight;
        public Vector3 Position;
        public Quaternion Rotation;
        public Rid LastRid;
        public bool HadContact;
        public bool HasPose;
    }

    private bool _debugVisualizeBraceTargets;
    private CharacterHandBraceDebugVisual _debugVisual;
    private HandState _left;
    private HandState _right;
    private ulong _lastSensorSerial;
    private HandBraceTarget _leftBrace;
    private HandBraceTarget _rightBrace;
    private bool SourceCurrent => PhysicsSourceTick != 0 && Probes != null &&
        Sensor != null && Probes.PhysicsTick == PhysicsSourceTick &&
        Sensor.InvalidationSerial == _lastSensorSerial;
    public HandBraceTarget LeftBrace => SourceCurrent ? _leftBrace : WithoutContact(_leftBrace);
    public HandBraceTarget RightBrace => SourceCurrent ? _rightBrace : WithoutContact(_rightBrace);
    public ulong PhysicsSourceTick { get; private set; }
    public ulong RenderFrameCounter { get; private set; }

    // Scalar/vector accessors keep diagnostics available to GDScript tools.
    public bool LeftHasPhysicalContact => LeftBrace.HasPhysicalContact;
    public bool RightHasPhysicalContact => RightBrace.HasPhysicalContact;
    public bool LeftPresentationActive => LeftBrace.IsPresented;
    public bool RightPresentationActive => RightBrace.IsPresented;
    public bool LeftSurfaceChanged => LeftBrace.SurfaceChanged;
    public bool RightSurfaceChanged => RightBrace.SurfaceChanged;
    public float LeftWeight => LeftBrace.Weight;
    public float RightWeight => RightBrace.Weight;
    public float LeftRawContactFactor => LeftBrace.ContactFactor;
    public float RightRawContactFactor => RightBrace.ContactFactor;
    public float LeftContactDistance => LeftBrace.ContactDistance;
    public float RightContactDistance => RightBrace.ContactDistance;
    public Vector3 LeftRawContactPoint => LeftBrace.RawContactPoint;
    public Vector3 RightRawContactPoint => RightBrace.RawContactPoint;
    public Vector3 LeftRawContactNormal => LeftBrace.RawSurfaceNormal;
    public Vector3 RightRawContactNormal => RightBrace.RawSurfaceNormal;
    public Vector3 LeftRawTargetPosition => LeftBrace.RawTargetPosition;
    public Vector3 RightRawTargetPosition => RightBrace.RawTargetPosition;
    public Basis LeftRawTargetBasis => LeftBrace.RawTargetBasis;
    public Basis RightRawTargetBasis => RightBrace.RawTargetBasis;
    public Vector3 LeftSmoothedTargetPosition => LeftBrace.SmoothedTargetPosition;
    public Vector3 RightSmoothedTargetPosition => RightBrace.SmoothedTargetPosition;
    public Basis LeftSmoothedTargetBasis => LeftBrace.SmoothedTargetBasis;
    public Basis RightSmoothedTargetBasis => RightBrace.SmoothedTargetBasis;
    public Rid LeftColliderRid => LeftBrace.ColliderRid;
    public Rid RightColliderRid => RightBrace.ColliderRid;

    public override void _Ready()
    {
        Probes ??= GetNode<CharacterHandContactProbes>("../CharacterHandContactProbes");
        Sensor ??= GetNode<CharacterProximitySensor>("../CharacterProximitySensor");
        View ??= GetNode<PlayerViewController>("../PlayerViewController");
        Motor ??= GetNode<KinematicCharacterMotor>("../KinematicCharacterMotor");
        ResetPresentation();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        if (!float.IsFinite(dt) || dt < 0f)
            return;
        // An invalidated sensor makes the probe snapshot stale immediately.
        // A caller performing a teleport can use ResetPresentation() to hard
        // clear old world-space targets before the next render frame.
        ulong sourceTick = Probes?.PhysicsTick ?? 0;
        bool fresh = sourceTick != 0 && Sensor != null &&
            Sensor.InvalidationSerial == _lastSensorSerial;
        if (Sensor != null && Sensor.InvalidationSerial != _lastSensorSerial)
        {
            ResetPresentation();
            _lastSensorSerial = Sensor.InvalidationSerial;
            fresh = sourceTick != 0;
        }
        PhysicsSourceTick = fresh ? sourceTick : 0;
        RenderFrameCounter++;
        float reach = Probes == null ? 0f : Probes.HandReachDistance;
        float start = float.IsFinite(BraceStartDistance) && float.IsFinite(reach)
            ? Mathf.Clamp(BraceStartDistance, 0.001f, Mathf.Max(reach, 0.001f)) : 0.001f;
        float full = float.IsFinite(BraceFullDistance)
            ? Mathf.Clamp(BraceFullDistance, 0f, start - 0.001f) : 0f;
        _leftBrace = UpdateHand(ref _left, fresh ? Probes.LeftContact : default,
            dt, start, full);
        _rightBrace = UpdateHand(ref _right, fresh ? Probes.RightContact : default,
            dt, start, full);
        if (DebugVisualizeBraceTargets)
        {
            _debugVisual ??= new CharacterHandBraceDebugVisual(GetParent());
            _debugVisual.Update(LeftBrace, RightBrace);
        }
    }

    private static HandBraceTarget WithoutContact(HandBraceTarget prior) =>
        new(false, prior.IsPresented, false, default, 0,
            Vector3.Zero, Vector3.Zero, Vector3.Zero, Basis.Identity,
            prior.SmoothedTargetPosition, prior.SmoothedTargetBasis,
            0f, 0f, prior.Weight);

    private HandBraceTarget UpdateHand(ref HandState state, HandContactCandidate contact,
        float dt, float start, float full)
    {
        bool valid = contact.Valid && contact.Point.IsFinite() &&
            contact.Normal.IsFinite() && contact.Normal.LengthSquared() > PoseEpsilon &&
            float.IsFinite(contact.Distance) && contact.Distance >= 0f &&
            Motor != null && Motor.Up.IsFinite() && Motor.Up.LengthSquared() > PoseEpsilon;
        Vector3 rawPosition = Vector3.Zero;
        Basis rawBasis = Basis.Identity;
        float factor = 0f;
        bool changed = false;
        if (valid)
        {
            Vector3 normal = contact.Normal.Normalized();
            rawBasis = MakeSurfaceBasis(normal);
            float clearance = float.IsFinite(PalmSurfaceClearance)
                ? Mathf.Max(PalmSurfaceClearance, 0f) : 0f;
            rawPosition = contact.Point + normal * clearance;
            valid = rawPosition.IsFinite();
            if (valid)
            {
                factor = Mathf.Clamp((start - contact.Distance) / (start - full), 0f, 1f);
                changed = !state.HadContact || state.LastRid != contact.ColliderRid;
                Quaternion rawRotation = rawBasis.GetRotationQuaternion().Normalized();
                if (changed || !state.HasPose ||
                    (state.Weight <= WeightEpsilon && factor > 0f))
                {
                    state.Position = rawPosition;
                    state.Rotation = rawRotation;
                    state.HasPose = true;
                }
                else
                {
                    state.Position = state.Position.Lerp(rawPosition,
                        Alpha(TargetPositionFollowSpeed, dt));
                    state.Rotation = state.Rotation.Slerp(rawRotation,
                        Alpha(OrientationFollowSpeed, dt)).Normalized();
                }
                state.LastRid = contact.ColliderRid;
                state.HadContact = true;
            }
        }
        if (!valid)
        {
            contact = default;
            state.HadContact = false;
            state.LastRid = default;
        }
        float speed = factor > state.Weight ? BraceEnterSpeed : BraceExitSpeed;
        state.Weight = Mathf.Lerp(state.Weight, factor, Alpha(speed, dt));
        if (factor == 0f && state.Weight <= WeightEpsilon)
            state.Weight = 0f;
        bool presented = state.HasPose && state.Weight > WeightEpsilon;
        return new HandBraceTarget(valid, presented, changed,
            valid ? contact.ColliderRid : default,
            valid ? contact.ColliderObjectId : 0,
            valid ? contact.Point : Vector3.Zero,
            valid ? contact.Normal : Vector3.Zero,
            valid ? rawPosition : Vector3.Zero,
            valid ? rawBasis : Basis.Identity,
            state.HasPose ? state.Position : Vector3.Zero,
            state.HasPose ? new Basis(state.Rotation).Orthonormalized() : Basis.Identity,
            valid ? contact.Distance : 0f, factor, state.Weight);
    }

    private Basis MakeSurfaceBasis(Vector3 normal)
    {
        Vector3 up = Motor.Up.Normalized();
        Vector3 surfaceUp = up - normal * up.Dot(normal);
        if (surfaceUp.LengthSquared() <= PoseEpsilon)
        {
            Vector3 right = View?.GetPlanarRight(up) ?? Vector3.Right;
            surfaceUp = right - normal * right.Dot(normal);
        }
        if (surfaceUp.LengthSquared() <= PoseEpsilon)
        {
            Vector3 forward = View?.GetPlanarForward(up) ?? Vector3.Forward;
            surfaceUp = forward - normal * forward.Dot(normal);
        }
        if (!surfaceUp.IsFinite() || surfaceUp.LengthSquared() <= PoseEpsilon)
        {
            Vector3 axis = Mathf.Abs(normal.Y) < 0.9f ? Vector3.Up : Vector3.Right;
            surfaceUp = axis - normal * axis.Dot(normal);
        }
        surfaceUp = surfaceUp.Normalized();
        Vector3 rightTangent = surfaceUp.Cross(normal).Normalized();
        surfaceUp = normal.Cross(rightTangent).Normalized();
        return new Basis(rightTangent, surfaceUp, normal);
    }

    private static float Alpha(float speed, float dt) =>
        1f - Mathf.Exp(-Mathf.Max(float.IsFinite(speed) ? speed : 0f, 0f) * dt);

    public void ResetPresentation()
    {
        _left = _right = default;
        HandBraceTarget empty = new(false, false, false, default, 0,
            Vector3.Zero, Vector3.Zero, Vector3.Zero, Basis.Identity,
            Vector3.Zero, Basis.Identity, 0f, 0f, 0f);
        _leftBrace = _rightBrace = empty;
        _debugVisual?.Hide();
        PhysicsSourceTick = 0;
        RenderFrameCounter = 0;
        _lastSensorSerial = Sensor?.InvalidationSerial ?? 0;
    }
}
