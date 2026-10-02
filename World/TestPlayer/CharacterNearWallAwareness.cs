using Godot;

public readonly struct NearWallSurface
{
    public readonly bool Valid;
    public readonly Rid ColliderRid;
    public readonly ulong ColliderObjectId;
    public readonly Vector3 Point;
    public readonly Vector3 Normal;
    public readonly float PlaneDistance;
    public readonly float PointDistance;
    public readonly float WallUpDot;
    public readonly float FacingDot;
    public readonly float ForwardPointDot;

    public NearWallSurface(CharacterProximityContact contact, Vector3 normal,
        float planeDistance, float pointDistance, float wallUpDot,
        float facingDot, float forwardPointDot)
    {
        Valid = true;
        ColliderRid = contact.ColliderRid;
        ColliderObjectId = contact.ColliderObjectId;
        Point = contact.Point;
        Normal = normal;
        PlaneDistance = planeDistance;
        PointDistance = pointDistance;
        WallUpDot = wallUpDot;
        FacingDot = facingDot;
        ForwardPointDot = forwardPointDot;
    }
}

public readonly struct NearWallSnapshot
{
    public readonly ulong PhysicsTick;
    public readonly int RawContactCount;
    public readonly int WallCandidateCount;
    public readonly NearWallSurface PrimaryWall;
    public readonly NearWallSurface ForwardWall;

    public bool HasWall => PrimaryWall.Valid;
    public bool HasForwardWall => ForwardWall.Valid;

    public NearWallSnapshot(ulong physicsTick, int rawContactCount,
        int wallCandidateCount, NearWallSurface primaryWall, NearWallSurface forwardWall)
    {
        PhysicsTick = physicsTick;
        RawContactCount = rawContactCount;
        WallCandidateCount = wallCandidateCount;
        PrimaryWall = primaryWall;
        ForwardWall = forwardWall;
    }
}

// Coarse eye-region awareness only. Future hand and held-item contact need
// their own directional probes and geometry; a wall hint is not a climb result.
public partial class CharacterNearWallAwareness : Node
{
    private const float NegativePlaneTolerance = 0.005f;
    private const float ScoreTieTolerance = 0.00001f;

    [Export] public CharacterProximitySensor Sensor { get; set; }
    [Export] public KinematicCharacterMotor Motor { get; set; }
    [Export] public PlayerViewController View { get; set; }
    [Export(PropertyHint.Range, "0,2,0.01")]
    public float WallAwarenessDistance { get; set; } = 0.40f;
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float WallMaxUpDot { get; set; } = 0.35f;
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float ForwardFacingDotMin { get; set; } = 0.50f;
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float ForwardPointDotMin { get; set; } = 0.50f;
    [Export(PropertyHint.Range, "0,0.2,0.001")]
    public float SurfaceSwitchDistanceBias { get; set; } = 0.025f;

    private NearWallSnapshot _snapshot;
    private ulong _sensorInvalidationSerial;
    private float _effectiveWallAwarenessDistance;
    private int _rejectedContactCount;
    private int _primarySwitchCount;
    private int _forwardSwitchCount;

    private bool SnapshotFresh => Sensor != null && _snapshot.PhysicsTick != 0 &&
        Sensor.PhysicsTick == _snapshot.PhysicsTick &&
        Sensor.InvalidationSerial == _sensorInvalidationSerial;

    public NearWallSnapshot Snapshot => SnapshotFresh ? _snapshot : default;
    public ulong PhysicsTick => Snapshot.PhysicsTick;
    public int RawContactCount => Snapshot.RawContactCount;
    public int WallCandidateCount => Snapshot.WallCandidateCount;
    public int RejectedContactCount => SnapshotFresh ? _rejectedContactCount : 0;
    public bool HasWall => Snapshot.HasWall;
    public float PrimaryWallDistance => Snapshot.PrimaryWall.PlaneDistance;
    public float PrimaryWallPointDistance => Snapshot.PrimaryWall.PointDistance;
    public float PrimaryWallUpDot => Snapshot.PrimaryWall.WallUpDot;
    public Vector3 PrimaryWallPoint => Snapshot.PrimaryWall.Point;
    public Vector3 PrimaryWallNormal => Snapshot.PrimaryWall.Normal;
    public Rid PrimaryWallRid => Snapshot.PrimaryWall.ColliderRid;
    public ulong PrimaryWallObjectId => Snapshot.PrimaryWall.ColliderObjectId;
    public bool HasForwardWall => Snapshot.HasForwardWall;
    public float ForwardWallDistance => Snapshot.ForwardWall.PlaneDistance;
    public Vector3 ForwardWallPoint => Snapshot.ForwardWall.Point;
    public Vector3 ForwardWallNormal => Snapshot.ForwardWall.Normal;
    public Rid ForwardWallRid => Snapshot.ForwardWall.ColliderRid;
    public ulong ForwardWallObjectId => Snapshot.ForwardWall.ColliderObjectId;
    public float ForwardWallFacingDot => Snapshot.ForwardWall.FacingDot;
    public float ForwardWallPointDot => Snapshot.ForwardWall.ForwardPointDot;
    public float EffectiveWallAwarenessDistance => SnapshotFresh ?
        _effectiveWallAwarenessDistance : 0f;
    public int PrimarySwitchCount => SnapshotFresh ? _primarySwitchCount : 0;
    public int ForwardSwitchCount => SnapshotFresh ? _forwardSwitchCount : 0;

    public override void _Ready()
    {
        Sensor ??= GetNode<CharacterProximitySensor>("../CharacterProximitySensor");
        Motor ??= GetNode<KinematicCharacterMotor>("../KinematicCharacterMotor");
        View ??= GetNode<PlayerViewController>("../PlayerViewController");
        ResetAwareness();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (Sensor == null || Sensor.InvalidationSerial != _sensorInvalidationSerial ||
            Sensor.PhysicsTick == 0 || Sensor.PhysicsTick != Engine.GetPhysicsFrames())
            ResetAwareness();
        if (Sensor == null || Sensor.PhysicsTick == 0 ||
            Sensor.PhysicsTick != Engine.GetPhysicsFrames())
            return;

        Vector3 up = Motor.Up;
        if (!up.IsFinite() || up.LengthSquared() < 0.000001f)
        {
            ResetAwareness();
            return;
        }
        up = up.Normalized();
        Vector3 eye = Sensor.PhysicsNeutralEyePosition;
        Vector3 forward = View.GetPlanarForward(up);
        bool forwardValid = forward.IsFinite() && forward.LengthSquared() > 0.000001f;
        if (!float.IsFinite(Sensor.SensedRadius))
        {
            ResetAwareness();
            return;
        }
        float range = float.IsFinite(WallAwarenessDistance) ? WallAwarenessDistance : 0f;
        float upLimit = float.IsFinite(WallMaxUpDot) ?
            Mathf.Clamp(WallMaxUpDot, 0f, 1f) : 0.35f;
        float facingMin = float.IsFinite(ForwardFacingDotMin) ?
            Mathf.Clamp(ForwardFacingDotMin, 0f, 1f) : 0.50f;
        float pointMin = float.IsFinite(ForwardPointDotMin) ?
            Mathf.Clamp(ForwardPointDotMin, 0f, 1f) : 0.50f;
        _effectiveWallAwarenessDistance = Mathf.Min(Mathf.Max(range, 0f),
            Mathf.Max(Sensor.SensedRadius, 0f));
        _rejectedContactCount = 0;

        NearWallSurface bestPrimary = default;
        NearWallSurface retainedPrimary = default;
        NearWallSurface bestForward = default;
        NearWallSurface retainedForward = default;
        Rid previousPrimaryRid = _snapshot.PrimaryWall.ColliderRid;
        Rid previousForwardRid = _snapshot.ForwardWall.ColliderRid;
        int candidates = 0;

        for (int i = 0; i < Sensor.ContactCount; i++)
        {
            CharacterProximityContact contact = Sensor.GetContact(i);
            Vector3 normal = contact.Normal;
            if (!eye.IsFinite() || !contact.Point.IsFinite() || !normal.IsFinite() ||
                normal.LengthSquared() < 0.000001f)
            {
                _rejectedContactCount++;
                continue;
            }
            normal = normal.Normalized();
            float wallUpDot = Mathf.Abs(normal.Dot(up));
            if (wallUpDot > upLimit)
                continue;
            float planeDistance = (eye - contact.Point).Dot(normal);
            float pointDistance = eye.DistanceTo(contact.Point);
            if (!float.IsFinite(planeDistance) || !float.IsFinite(pointDistance) ||
                planeDistance < -NegativePlaneTolerance)
            {
                _rejectedContactCount++;
                continue;
            }
            planeDistance = Mathf.Max(planeDistance, 0f);
            if (planeDistance > _effectiveWallAwarenessDistance)
                continue;

            Vector3 horizontalNormal = normal.Slide(up);
            Vector3 toPoint = (contact.Point - eye).Slide(up);
            float facingDot = forwardValid && horizontalNormal.LengthSquared() > 0.000001f
                ? -horizontalNormal.Normalized().Dot(forward) : 0f;
            float forwardPointDot = forwardValid && toPoint.LengthSquared() > 0.000001f
                ? toPoint.Normalized().Dot(forward) : 0f;
            if (!float.IsFinite(facingDot) || !float.IsFinite(forwardPointDot))
            {
                _rejectedContactCount++;
                continue;
            }
            NearWallSurface surface = new(contact, normal, planeDistance, pointDistance,
                wallUpDot, facingDot, forwardPointDot);
            candidates++;
            if (Better(surface, bestPrimary, false))
                bestPrimary = surface;
            if (previousPrimaryRid != default && contact.ColliderRid == previousPrimaryRid &&
                Better(surface, retainedPrimary, false))
                retainedPrimary = surface;

            if (!forwardValid || facingDot < facingMin || forwardPointDot < pointMin)
                continue;
            if (Better(surface, bestForward, true))
                bestForward = surface;
            if (previousForwardRid != default && contact.ColliderRid == previousForwardRid &&
                Better(surface, retainedForward, true))
                retainedForward = surface;
        }

        float bias = float.IsFinite(SurfaceSwitchDistanceBias) ?
            Mathf.Max(SurfaceSwitchDistanceBias, 0f) : 0f;
        NearWallSurface primary = KeepPrevious(retainedPrimary, bestPrimary, bias);
        NearWallSurface forwardWall = KeepPrevious(retainedForward, bestForward, bias);
        if (_snapshot.PrimaryWall.Valid && primary.Valid &&
            _snapshot.PrimaryWall.ColliderRid != primary.ColliderRid && _primarySwitchCount < int.MaxValue)
            _primarySwitchCount++;
        if (_snapshot.ForwardWall.Valid && forwardWall.Valid &&
            _snapshot.ForwardWall.ColliderRid != forwardWall.ColliderRid && _forwardSwitchCount < int.MaxValue)
            _forwardSwitchCount++;
        _snapshot = new NearWallSnapshot(Sensor.PhysicsTick, Sensor.ContactCount,
            candidates, primary, forwardWall);
    }

    public void ResetAwareness()
    {
        _snapshot = default;
        _sensorInvalidationSerial = Sensor?.InvalidationSerial ?? 0;
        _effectiveWallAwarenessDistance = 0f;
        _rejectedContactCount = _primarySwitchCount = _forwardSwitchCount = 0;
    }

    private static NearWallSurface KeepPrevious(NearWallSurface previous,
        NearWallSurface best, float bias) => previous.Valid && best.Valid &&
        previous.PlaneDistance <= best.PlaneDistance + bias ? previous : best;

    private static bool Better(NearWallSurface candidate, NearWallSurface current,
        bool preferFacingOnTie) => !current.Valid ||
        candidate.PlaneDistance < current.PlaneDistance - ScoreTieTolerance ||
        (preferFacingOnTie &&
         Mathf.Abs(candidate.PlaneDistance - current.PlaneDistance) <= ScoreTieTolerance &&
         candidate.FacingDot > current.FacingDot);
}
