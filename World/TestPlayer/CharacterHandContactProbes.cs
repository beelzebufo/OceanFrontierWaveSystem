using Godot;

public readonly struct HandContactCandidate
{
    public readonly bool Valid;
    public readonly Rid ColliderRid;
    public readonly ulong ColliderObjectId;
    public readonly Vector3 Point;
    public readonly Vector3 Normal;
    public readonly float Distance;
    public readonly float ReachFraction;
    public readonly float WallUpDot;
    public readonly bool MatchesPrimaryWall;
    public readonly bool MatchesForwardWall;

    public HandContactCandidate(Rid colliderRid, ulong colliderObjectId,
        Vector3 point, Vector3 normal, float distance, float reach,
        float wallUpDot, bool matchesPrimaryWall, bool matchesForwardWall)
    {
        Valid = true;
        ColliderRid = colliderRid;
        ColliderObjectId = colliderObjectId;
        Point = point;
        Normal = normal;
        Distance = distance;
        ReachFraction = Mathf.Clamp(distance / reach, 0f, 1f);
        WallUpDot = wallUpDot;
        MatchesPrimaryWall = matchesPrimaryWall;
        MatchesForwardWall = matchesForwardWall;
    }
}

// Physics-only geometric candidates. Hand poses, IK and physical interaction
// are separate consumers, and the broad eye sensor is optional context.
public partial class CharacterHandContactProbes : Node3D
{
    private const int ContactCapacity = 8;
    private const float NegativeAlongTolerance = 0.005f;
    private const float ReachTolerance = 0.01f;

    [Export] public CharacterBody3D Body { get; set; }
    [Export] public KinematicCharacterMotor Motor { get; set; }
    [Export] public PlayerViewController View { get; set; }
    [Export] public CharacterProximitySensor Sensor { get; set; }
    [Export] public CharacterNearWallAwareness Awareness { get; set; }
    [Export] public ShapeCast3D LeftHandProbe { get; set; }
    [Export] public ShapeCast3D RightHandProbe { get; set; }
    [Export] public bool HandContactProbesEnabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
                ResetContacts();
        }
    }
    [Export(PropertyHint.Range, "0.01,0.15,0.005")]
    public float HandProbeRadius { get; set; } = 0.055f;
    [Export(PropertyHint.Range, "0.1,1.5,0.01")]
    public float HandReachDistance { get; set; } = 0.65f;
    [Export(PropertyHint.Range, "0,0.6,0.01")]
    public float ShoulderHalfWidth { get; set; } = 0.20f;
    [Export(PropertyHint.Range, "0,0.8,0.01")]
    public float ShoulderBelowEye { get; set; } = 0.25f;
    [Export(PropertyHint.Range, "-0.3,0.3,0.01")]
    public float ShoulderForwardOffset { get; set; }
    // Zero inherits the character's collision mask.
    [Export] public uint CollisionMaskOverride { get; set; }

    private SphereShape3D _leftSphere;
    private SphereShape3D _rightSphere;
    private bool _enabled = true;
    private HandContactCandidate _leftContact;
    private HandContactCandidate _rightContact;
    private Vector3 _leftOrigin;
    private Vector3 _rightOrigin;
    private Vector3 _probeForward;
    private ulong _physicsTick;
    private ulong _sensorInvalidationSerial;
    private int _queriesThisTick;

    private bool SnapshotFresh => Sensor != null && _physicsTick != 0 &&
        Sensor.PhysicsTick == _physicsTick &&
        Sensor.InvalidationSerial == _sensorInvalidationSerial;

    public ulong PhysicsTick => SnapshotFresh ? _physicsTick : 0;
    public ulong SourceSensorTick => PhysicsTick;
    public Vector3 LeftOrigin => SnapshotFresh ? _leftOrigin : Vector3.Zero;
    public Vector3 RightOrigin => SnapshotFresh ? _rightOrigin : Vector3.Zero;
    public Vector3 ProbeForward => SnapshotFresh ? _probeForward : Vector3.Zero;
    public HandContactCandidate LeftContact => SnapshotFresh ? _leftContact : default;
    public HandContactCandidate RightContact => SnapshotFresh ? _rightContact : default;
    public bool HasLeftContact => LeftContact.Valid;
    public bool HasRightContact => RightContact.Valid;
    public Vector3 LeftContactPoint => LeftContact.Point;
    public Vector3 LeftContactNormal => LeftContact.Normal;
    public float LeftContactDistance => LeftContact.Distance;
    public float LeftReachFraction => LeftContact.ReachFraction;
    public Rid LeftColliderRid => LeftContact.ColliderRid;
    public ulong LeftColliderObjectId => LeftContact.ColliderObjectId;
    public bool LeftMatchesForwardWall => LeftContact.MatchesForwardWall;
    public Vector3 RightContactPoint => RightContact.Point;
    public Vector3 RightContactNormal => RightContact.Normal;
    public float RightContactDistance => RightContact.Distance;
    public float RightReachFraction => RightContact.ReachFraction;
    public Rid RightColliderRid => RightContact.ColliderRid;
    public ulong RightColliderObjectId => RightContact.ColliderObjectId;
    public bool RightMatchesForwardWall => RightContact.MatchesForwardWall;
    public int QueriesThisTick => SnapshotFresh ? _queriesThisTick : 0;
    public ulong TotalQueryCount { get; private set; }

    public override void _Ready()
    {
        Body ??= GetParent<CharacterBody3D>();
        Motor ??= GetNode<KinematicCharacterMotor>("../KinematicCharacterMotor");
        View ??= GetNode<PlayerViewController>("../PlayerViewController");
        Sensor ??= GetNode<CharacterProximitySensor>("../CharacterProximitySensor");
        Awareness ??= GetNodeOrNull<CharacterNearWallAwareness>("../CharacterNearWallAwareness");
        LeftHandProbe ??= GetNode<ShapeCast3D>("LeftHandProbe");
        RightHandProbe ??= GetNode<ShapeCast3D>("RightHandProbe");
        _leftSphere = ConfigureProbe(LeftHandProbe);
        _rightSphere = ConfigureProbe(RightHandProbe);
        ResetContacts();
    }

    private SphereShape3D ConfigureProbe(ShapeCast3D probe)
    {
        SphereShape3D sphere = probe.Shape as SphereShape3D ?? new SphereShape3D();
        probe.Shape = sphere;
        sphere.Radius = Mathf.Max(HandProbeRadius, 0.001f);
        probe.TargetPosition = Vector3.Forward * Mathf.Max(HandReachDistance, 0.001f);
        probe.Enabled = false;
        probe.ExcludeParent = false;
        probe.AddExceptionRid(Body.GetRid());
        probe.CollideWithBodies = true;
        probe.CollideWithAreas = false;
        probe.MaxResults = ContactCapacity;
        probe.CollisionMask = CollisionMaskOverride == 0 ? Body.CollisionMask : CollisionMaskOverride;
        return sphere;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!HandContactProbesEnabled || Body == null || Motor == null || View == null ||
            Sensor == null || Sensor.PhysicsTick == 0 ||
            Sensor.PhysicsTick != Engine.GetPhysicsFrames() ||
            !Sensor.PhysicsNeutralEyePosition.IsFinite())
        {
            ResetContacts();
            return;
        }
        Vector3 up = Motor.Up;
        if (!up.IsFinite() || up.LengthSquared() < 0.000001f)
        {
            ResetContacts();
            return;
        }
        up = up.Normalized();
        Vector3 forward = View.GetPlanarForward(up);
        Vector3 right = View.GetPlanarRight(up);
        if (!forward.IsFinite() || !right.IsFinite() ||
            forward.LengthSquared() < 0.000001f || right.LengthSquared() < 0.000001f)
        {
            ResetContacts();
            return;
        }

        float radius = Mathf.Max(HandProbeRadius, 0.001f);
        float reach = Mathf.Max(HandReachDistance, 0.001f);
        float halfWidth = Mathf.Max(ShoulderHalfWidth, 0f);
        float belowEye = Mathf.Max(ShoulderBelowEye, 0f);
        float forwardOffset = ShoulderForwardOffset;
        if (!float.IsFinite(radius) || !float.IsFinite(reach) ||
            !float.IsFinite(halfWidth) || !float.IsFinite(belowEye) ||
            !float.IsFinite(forwardOffset))
        {
            ResetContacts();
            return;
        }
        Vector3 shoulder = Sensor.PhysicsNeutralEyePosition - up * belowEye +
            forward * forwardOffset;
        _leftOrigin = shoulder - right * halfWidth;
        _rightOrigin = shoulder + right * halfWidth;
        _probeForward = forward;
        Basis basis = new(right, up, -forward);
        uint mask = CollisionMaskOverride == 0 ? Body.CollisionMask : CollisionMaskOverride;
        float wallUpLimit = Awareness != null && float.IsFinite(Awareness.WallMaxUpDot)
            ? Mathf.Clamp(Awareness.WallMaxUpDot, 0f, 1f) : 0.35f;
        NearWallSnapshot context = Awareness?.Snapshot ?? default;
        _sensorInvalidationSerial = Sensor.InvalidationSerial;
        _physicsTick = Sensor.PhysicsTick;
        _queriesThisTick = 0;
        _leftContact = UpdateProbe(LeftHandProbe, _leftSphere, _leftOrigin, basis,
            forward, up, radius, reach, mask, wallUpLimit, context);
        _rightContact = UpdateProbe(RightHandProbe, _rightSphere, _rightOrigin, basis,
            forward, up, radius, reach, mask, wallUpLimit, context);
    }

    private HandContactCandidate UpdateProbe(ShapeCast3D probe, SphereShape3D sphere,
        Vector3 origin, Basis basis, Vector3 forward, Vector3 up,
        float radius, float reach, uint mask, float wallUpLimit,
        NearWallSnapshot context)
    {
        probe.GlobalTransform = new Transform3D(basis, origin);
        if (!Mathf.IsEqualApprox(sphere.Radius, radius))
            sphere.Radius = radius;
        probe.TargetPosition = Vector3.Forward * reach;
        probe.CollisionMask = mask;
        probe.ForceShapecastUpdate();
        _queriesThisTick++;
        TotalQueryCount++;

        HandContactCandidate best = default;
        int count = Mathf.Min(probe.GetCollisionCount(), ContactCapacity);
        for (int i = 0; i < count; i++)
        {
            Vector3 point = probe.GetCollisionPoint(i);
            Vector3 normal = probe.GetCollisionNormal(i);
            if (!point.IsFinite() || !normal.IsFinite() ||
                normal.LengthSquared() < 0.000001f)
                continue;
            normal = normal.Normalized();
            float wallUpDot = Mathf.Abs(normal.Dot(up));
            if (wallUpDot > wallUpLimit)
                continue;
            float along = (point - origin).Dot(forward);
            if (!float.IsFinite(along) || along < -NegativeAlongTolerance ||
                along > reach + radius + ReachTolerance)
                continue;
            float distance = Mathf.Max(along, 0f);
            if (best.Valid && distance >= best.Distance)
                continue;
            Rid rid = probe.GetColliderRid(i);
            GodotObject collider = probe.GetCollider(i);
            best = new HandContactCandidate(rid, collider?.GetInstanceId() ?? 0,
                point, normal, distance, reach, wallUpDot,
                context.HasWall && rid == context.PrimaryWall.ColliderRid,
                context.HasForwardWall && rid == context.ForwardWall.ColliderRid);
        }
        return best;
    }

    public void ResetContacts()
    {
        _leftContact = _rightContact = default;
        _leftOrigin = _rightOrigin = _probeForward = Vector3.Zero;
        _physicsTick = 0;
        _sensorInvalidationSerial = Sensor?.InvalidationSerial ?? 0;
        _queriesThisTick = 0;
        TotalQueryCount = 0;
    }
}
