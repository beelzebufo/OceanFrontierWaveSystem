using Godot;

public enum CharacterContactKind
{
    WalkableFloor,
    UnwalkableSlope,
    Wall,
    Ceiling
}

// The motor owns this state; CharacterBody3D floor state is deliberately unused.
public sealed class CharacterContactState
{
    public bool IsGrounded { get; private set; }
    public bool IsStable { get; private set; }
    public bool IsWalkable => IsStable;
    public bool HasWall { get; private set; }
    public bool HasCeiling { get; private set; }
    public bool HasUnwalkableSlope { get; private set; }
    public Vector3 GroundNormal { get; private set; } = Vector3.Up;
    public Vector3 GroundPoint { get; private set; }
    public float SlopeAngle { get; private set; }
    public Rid GroundColliderRid { get; private set; }
    public ulong GroundColliderObjectId { get; private set; }
    public Vector3 GroundColliderVelocity { get; private set; }
    public int BounceCount { get; internal set; }
    public int RecoveryCount { get; internal set; }
    public int ContactCount { get; private set; }
    public bool SteppedUp { get; internal set; }
    public float StepRise { get; internal set; }
    public float StepRiseRequested { get; internal set; }
    public float StepRiseTravelled { get; internal set; }
    public float StepClearanceRequested { get; internal set; }
    public float StepClearanceTravelled { get; internal set; }
    public float StepForwardRequested { get; internal set; }
    public float StepForwardTravelled { get; internal set; }
    public float StepLandingDelta { get; internal set; }
    public string StepStatus { get; internal set; } = "not tried";
    public bool SnappedDown { get; internal set; }
    public float SnapDistance { get; internal set; }
    public string SnapStatus { get; internal set; } = "not tried";
    public string SnapSource { get; internal set; } = "none";
    public string SnapPrimaryStatus { get; internal set; } = "not tried";
    public Vector3 SnapPrimaryNormal { get; internal set; }
    public float SnapPrimarySlope { get; internal set; }
    public float SnapPrimaryTravel { get; internal set; }
    public bool SnapSecondaryAttempted { get; internal set; }
    public bool SnapSecondaryCandidateFound { get; internal set; }
    public Vector3 SnapSecondaryNormal { get; internal set; }
    public float SnapSecondarySlope { get; internal set; }
    public float SnapSecondaryDrop { get; internal set; }
    public Vector3 SnapSecondaryRecovery { get; internal set; }
    public string SnapSecondaryValidation { get; internal set; } = "not tried";

    private float _bestGroundDot;

    internal void Reset()
    {
        HasWall = HasCeiling = HasUnwalkableSlope = false;
        ClearGround();
        BounceCount = RecoveryCount = ContactCount = 0;
        SteppedUp = SnappedDown = false;
        StepRise = SnapDistance = 0f;
        StepRiseRequested = StepRiseTravelled = 0f;
        StepClearanceRequested = StepClearanceTravelled = 0f;
        StepForwardRequested = StepForwardTravelled = StepLandingDelta = 0f;
        StepStatus = SnapStatus = "not tried";
        SnapSource = "none";
        SnapPrimaryStatus = SnapSecondaryValidation = "not tried";
        SnapPrimaryNormal = SnapSecondaryNormal = Vector3.Zero;
        SnapSecondaryRecovery = Vector3.Zero;
        SnapPrimarySlope = SnapPrimaryTravel = SnapSecondarySlope = SnapSecondaryDrop = 0f;
        SnapSecondaryAttempted = SnapSecondaryCandidateFound = false;
    }

    // Movement contacts remain useful for wall/ceiling diagnostics. Ground data
    // is rebuilt from the final simulated pose so an old floor cannot linger.
    internal void ClearGround()
    {
        IsGrounded = IsStable = false;
        GroundNormal = Vector3.Up;
        GroundPoint = Vector3.Zero;
        SlopeAngle = 0f;
        GroundColliderRid = default;
        GroundColliderObjectId = 0;
        GroundColliderVelocity = Vector3.Zero;
        _bestGroundDot = -1f;
    }

    internal CharacterContactKind Add(Vector3 normal, Vector3 point, Rid rid, ulong objectId,
        Vector3 colliderVelocity, Vector3 up, float minWalkDot)
    {
        ContactCount++;
        float upDot = normal.Dot(up);
        if (upDot >= minWalkDot)
        {
            IsStable = true;
            SetGround(upDot, normal, point, rid, objectId, colliderVelocity, up);
            return CharacterContactKind.WalkableFloor;
        }
        if (upDot > 0.05f)
        {
            HasUnwalkableSlope = true;
            SetGround(upDot, normal, point, rid, objectId, colliderVelocity, up);
            return CharacterContactKind.UnwalkableSlope;
        }
        if (upDot < -0.05f)
        {
            HasCeiling = true;
            return CharacterContactKind.Ceiling;
        }
        HasWall = true;
        return CharacterContactKind.Wall;
    }

    private void SetGround(float upDot, Vector3 normal, Vector3 point, Rid rid,
        ulong objectId, Vector3 colliderVelocity, Vector3 up)
    {
        IsGrounded = true;
        if (upDot <= _bestGroundDot)
            return;
        _bestGroundDot = upDot;
        GroundNormal = normal;
        GroundPoint = point;
        SlopeAngle = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(upDot, -1f, 1f)));
        GroundColliderRid = rid;
        GroundColliderObjectId = objectId;
        GroundColliderVelocity = colliderVelocity;
    }
}
