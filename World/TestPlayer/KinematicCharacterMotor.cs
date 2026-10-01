using Godot;

public partial class KinematicCharacterMotor : Node
{
    [Export] public CharacterBody3D Body { get; set; }
    [Export(PropertyHint.Range, "1,12,1")] public int MaxBounces { get; set; } = 5;
    [Export(PropertyHint.Range, "1,8,1")] public int MaxRecoveryPasses { get; set; } = 3;
    [Export(PropertyHint.Range, "1,32,1")] public int MaxContactsPerQuery { get; set; } = 4;
    [Export(PropertyHint.Range, "0.001,0.1,0.001")] public float CollisionMargin { get; set; } = 0.01f;
    [Export(PropertyHint.Range, "1,89,1")] public float MaxWalkAngle { get; set; } = 45f;
    [Export(PropertyHint.Range, "0.05,0.6,0.01")] public float StepHeight { get; set; } = 0.35f;
    [Export(PropertyHint.Range, "0.01,0.5,0.01")] public float StepUpDepth { get; set; } = 0.10f;
    [Export(PropertyHint.Range, "0.05,0.8,0.01")] public float SnapDownDistance { get; set; } = 0.35f;
    [Export] public Vector3 Up { get; set; } = Vector3.Up;

    public CharacterContactState Contacts { get; } = new();
    public CharacterGroundMotionTracker GroundMotion { get; } = new();

    private readonly PhysicsTestMotionParameters3D _query = new();
    private readonly Godot.Collections.Array<Rid> _carryExclusions = new();
    private readonly Godot.Collections.Array<Rid> _noExclusions = new();
    private readonly PhysicsTestMotionResult3D _result = new();
    private readonly PhysicsTestMotionResult3D _probeResult = new();
    private readonly Vector3[] _planes = new Vector3[32];
    private int _planeCount;

    public override void _Ready()
    {
        Body ??= GetParent<CharacterBody3D>();
        _query.RecoveryAsCollision = true;
        _query.CollideSeparationRay = false;
        _query.ExcludeBodies = _noExclusions;
    }

    public void Simulate(Vector3 requestedMotion, Vector3 walkMotion,
        bool allowGrounding = true, bool allowPlatformCarry = true)
    {
        bool wasStable = Contacts.IsStable;
        bool hadCeiling = Contacts.HasCeiling;
        Contacts.Reset();
        _planeCount = 0;
        Vector3 up = Up.Normalized();
        float walkDot = Mathf.Cos(Mathf.DegToRad(MaxWalkAngle));
        Transform3D simulated = Body.GlobalTransform;

        GroundMotion.BeginTick();
        Vector3 carry = allowPlatformCarry
            ? GroundMotion.ComputeCarryMotion(simulated.Origin) : Vector3.Zero;
        Vector3 carryBlockNormal = Vector3.Zero;
        float carryBlockLimit = 0f;
        if (hadCeiling && GroundMotion.PlatformRid != default)
        {
            // The platform can pause while overlapping the capsule. Rebuild a
            // guard from this tick's starting pose even with zero carry.
            carryBlockNormal = -up;
            carryBlockLimit = simulated.Origin.Dot(carryBlockNormal);
        }
        if (carry.LengthSquared() > 0.00000001f)
        {
            Vector3 beforeCarry = simulated.Origin;
            // Ignore only the supporting body during carry. Other geometry
            // still blocks the full capsule. Restore the query before recovery.
            _carryExclusions.Add(GroundMotion.PlatformRid);
            _query.ExcludeBodies = _carryExclusions;
            Vector3 traveled;
            bool carryHit;
            try
            {
                carryHit = Test(simulated, carry, _probeResult, false);
                traveled = carryHit ? _probeResult.GetTravel() : carry;
            }
            finally
            {
                _query.ExcludeBodies = _noExclusions;
                _carryExclusions.Clear();
            }
            // At skin contact Jolt can alternate between a ceiling hit and a
            // no-hit carry. The previous final ceiling contact guards that
            // single upward tick; a downward return is never constrained.
            bool ceilingNoHitUp = !carryHit && hadCeiling && carry.Dot(up) > 0.00001f;
            if (ceilingNoHitUp)
                traveled = Vector3.Zero;
            simulated.Origin += traveled;
            GroundMotion.RecordCarry(carry, traveled);
            if (carryHit || hadCeiling)
            {
                float mostOpposing = 0f;
                for (int i = 0; carryHit && i < _probeResult.GetCollisionCount(); i++)
                {
                    Vector3 normal = _probeResult.GetCollisionNormal(i).Normalized();
                    float dot = normal.Dot(carry.Normalized());
                    if (dot < mostOpposing)
                    {
                        mostOpposing = dot;
                        carryBlockNormal = normal;
                    }
                }
                if (hadCeiling)
                    carryBlockNormal = -up;
                if (GroundMotion.CarryBlocked && carryBlockNormal.IsZeroApprox())
                    carryBlockNormal = -carry.Normalized();
                // Preserve the actual permitted part of the carry. Only later
                // recovery/movement in this tick is constrained by this plane.
                if (!carryBlockNormal.IsZeroApprox())
                {
                    Vector3 safeCarryOrigin = beforeCarry + traveled;
                    carryBlockLimit = safeCarryOrigin.Dot(carryBlockNormal);
                }
            }
        }

        // Godot performs depenetration inside BodyTestMotion. With zero motion we
        // expose that recovery explicitly and keep every intermediate pose local.
        for (int pass = 0; pass < MaxRecoveryPasses; pass++)
        {
            if (!Test(simulated, Vector3.Zero))
                break;
            Vector3 recovery = _result.GetTravel();
            RecordContacts(up, walkDot);
            float deepestOverlap = 0f;
            for (int i = 0; i < _result.GetCollisionCount(); i++)
                deepestOverlap = Mathf.Max(deepestOverlap, _result.GetCollisionDepth(i));
            // Ignore the routine skin contact. Reapplying its tiny recovery each
            // frame makes a resting capsule oscillate above the floor.
            if (recovery.LengthSquared() < 0.00000001f ||
                (deepestOverlap <= CollisionMargin * 1.5f &&
                 recovery.LengthSquared() <= 4f * CollisionMargin * CollisionMargin))
                break;
            simulated.Origin += recovery;
            Contacts.RecoveryCount++;
        }

        // The moving support may now overlap the capsule. Its recovery must
        // not undo a carry collision with a wall or ceiling.
        ConstrainCarryBlock(ref simulated, carryBlockNormal, carryBlockLimit);

        Vector3 remaining = requestedMotion;
        bool stepTried = false;
        bool stepAccepted = false;
        for (int bounce = 0; bounce < MaxBounces && remaining.LengthSquared() > 0.00000001f; bounce++)
        {
            if (!Test(simulated, remaining))
            {
                simulated.Origin += remaining;
                break;
            }

            simulated.Origin += _result.GetTravel();
            Vector3 next = _result.GetRemainder();
            if (!stepTried && wasStable && allowGrounding &&
                IsStepObstacle(_result, walkMotion, up, walkDot))
            {
                stepTried = true;
                Vector3 forward = next - up * next.Dot(up);
                if (TryStep(simulated, forward, walkMotion, up, walkDot, out Transform3D landing))
                {
                    simulated = landing;
                    Contacts.ClearGround();
                    RecordContacts(_probeResult, up, walkDot);
                    stepAccepted = true;
                    break;
                }
            }
            RecordContacts(up, walkDot);
            Contacts.BounceCount++;
            next = ClipAgainstPlanes(next, up, walkDot);
            if (next.LengthSquared() >= remaining.LengthSquared() - 0.00000001f &&
                _result.GetTravel().LengthSquared() < 0.00000001f)
                break;
            remaining = next;
        }

        ConstrainCarryBlock(ref simulated, carryBlockNormal, carryBlockLimit);
        if (!stepAccepted)
        {
            // Earlier sweep/recovery contacts can refer to a floor that is no
            // longer under the capsule after crossing an edge.
            Contacts.ClearGround();
            // A short downward support sweep reveals the tread underneath a
            // capsule touching the next riser's rounded edge. A zero-motion
            // query alone may report only that edge's steep normal.
            if (Test(simulated, -up * (CollisionMargin * 3f), _probeResult) &&
                -_probeResult.GetTravel().Dot(up) <= CollisionMargin * 2f)
                RecordContacts(_probeResult, up, walkDot);

            if (wasStable && allowGrounding && !Contacts.IsStable &&
                TrySnapDown(simulated, up, walkDot, out Transform3D snapped))
            {
                simulated = snapped;
                Contacts.ClearGround();
                RecordContacts(_probeResult, up, walkDot);
            }
        }

        GroundMotion.UpdateAttachment(Contacts, simulated.Origin);
        // One scene/physics-body transform update after all virtual substeps.
        Body.GlobalTransform = simulated;
    }

    private static void ConstrainCarryBlock(ref Transform3D simulated,
        Vector3 normal, float limit)
    {
        if (normal.IsZeroApprox())
            return;
        float across = simulated.Origin.Dot(normal) - limit;
        if (across < 0f)
            simulated.Origin -= normal * across;
    }

    private bool Test(Transform3D from, Vector3 motion,
        PhysicsTestMotionResult3D result, bool recoveryAsCollision = true)
    {
        _query.From = from;
        _query.Motion = motion;
        _query.Margin = CollisionMargin;
        _query.MaxCollisions = Mathf.Clamp(MaxContactsPerQuery, 1, 32);
        _query.RecoveryAsCollision = recoveryAsCollision;
        return PhysicsServer3D.BodyTestMotion(Body.GetRid(), _query, result);
    }

    private bool Test(Transform3D from, Vector3 motion) => Test(from, motion, _result);

    private void RecordContacts(Vector3 up, float walkDot)
        => RecordContacts(_result, up, walkDot);

    private void RecordContacts(PhysicsTestMotionResult3D result, Vector3 up, float walkDot)
    {
        for (int i = 0; i < result.GetCollisionCount(); i++)
        {
            Vector3 normal = result.GetCollisionNormal(i).Normalized();
            if (normal.IsZeroApprox())
                continue;
            Contacts.Add(normal, result.GetCollisionPoint(i), result.GetColliderRid(i),
                result.GetColliderId(i), result.GetColliderVelocity(i), up, walkDot);
            if (_planeCount < _planes.Length)
                _planes[_planeCount++] = normal;
        }
    }

    private static bool IsStepObstacle(PhysicsTestMotionResult3D hit, Vector3 walkMotion,
        Vector3 up, float walkDot)
    {
        Vector3 walk = walkMotion - up * walkMotion.Dot(up);
        if (walk.LengthSquared() < 0.000001f)
            return false;
        walk = walk.Normalized();
        for (int i = 0; i < hit.GetCollisionCount(); i++)
        {
            Vector3 normal = hit.GetCollisionNormal(i).Normalized();
            float upDot = normal.Dot(up);
            // A rounded capsule often reports the upper edge of a short
            // riser as an unwalkable slope. The landing test still rejects a
            // genuinely steep ramp because its surface normal stays steep.
            if (upDot > -0.2f && upDot < walkDot &&
                normal.Dot(walk) < -0.25f)
                return true;
        }
        return false;
    }

    private bool TryStep(Transform3D from, Vector3 forward, Vector3 walkMotion, Vector3 up,
        float walkDot, out Transform3D landing)
    {
        landing = from;
        Contacts.StepForwardRequested = forward.Length();
        if (forward.LengthSquared() < 0.00000001f)
        {
            Contacts.StepStatus = "too little forward motion";
            return false;
        }

        // Candidate queries never alter the actual body or the main bounce
        // result. A rejected candidate returns to the original wall slide.
        Vector3 rise = up * StepHeight;
        Contacts.StepRiseRequested = StepHeight;
        Transform3D raised = from;
        raised.Origin += Test(from, rise, _probeResult, false)
            ? _probeResult.GetTravel() : rise;
        float riseTravelled = (raised.Origin - from.Origin).Dot(up);
        Contacts.StepRiseTravelled = riseTravelled;
        if (riseTravelled < StepHeight - 0.005f)
        {
            Contacts.StepStatus = "overhead blocked";
            return false;
        }

        // Probe a fixed minimum depth from the raised pose. This only tests
        // clearance; the actual move below still uses this tick's remainder.
        Vector3 walk = walkMotion - up * walkMotion.Dot(up);
        Vector3 direction = walk.LengthSquared() > 0.000001f
            ? walk.Normalized() : forward.Normalized();
        Vector3 clearance = direction * StepUpDepth;
        Contacts.StepClearanceRequested = StepUpDepth;
        Vector3 clearTravel = Test(raised, clearance, _probeResult, false)
            ? _probeResult.GetTravel() : clearance;
        float clearanceTravelled = clearTravel.Dot(direction);
        Contacts.StepClearanceTravelled = clearanceTravelled;
        if (clearanceTravelled + 0.0001f < StepUpDepth)
        {
            Contacts.StepStatus = "minimum forward clearance blocked";
            return false;
        }

        Transform3D advanced = raised;
        advanced.Origin += Test(raised, forward, _probeResult, false)
            ? _probeResult.GetTravel() : forward;
        float forwardTravelled = (advanced.Origin - raised.Origin).Dot(forward.Normalized());
        Contacts.StepForwardTravelled = forwardTravelled;
        if (forwardTravelled < forward.Length() - 0.005f)
        {
            Contacts.StepStatus = "forward blocked";
            return false;
        }

        Vector3 descent = -up * (StepHeight + SnapDownDistance);
        if (!Test(advanced, descent, _probeResult, false) ||
            !HasWalkableContact(_probeResult, up, walkDot))
        {
            Contacts.StepStatus = "no walkable landing";
            return false;
        }

        landing = advanced;
        landing.Origin += _probeResult.GetTravel();
        float height = (landing.Origin - from.Origin).Dot(up);
        Contacts.StepLandingDelta = height;
        // A tiny frame remainder can find the old floor beside a riser.
        // That is a valid floor contact, but it is not a step landing.
        if (height < CollisionMargin * 2f && height >= -CollisionMargin)
        {
            Contacts.StepStatus = "no raised landing";
            return false;
        }
        if (height < -SnapDownDistance - 0.005f || height > StepHeight + 0.005f)
        {
            Contacts.StepStatus = "landing outside step range";
            return false;
        }
        Contacts.SteppedUp = true;
        Contacts.StepRise = height;
        Contacts.StepStatus = "accepted";
        Contacts.SnappedDown = true;
        Contacts.SnapDistance = (advanced.Origin - landing.Origin).Dot(up);
        Contacts.SnapStatus = "step landing";
        return true;
    }

    private bool TrySnapDown(Transform3D from, Vector3 up, float walkDot,
        out Transform3D snapped)
    {
        snapped = from;
        if (!Test(from, -up * SnapDownDistance, _probeResult, false))
        {
            Contacts.SnapStatus = "no ground in range";
            return false;
        }
        if (!HasWalkableContact(_probeResult, up, walkDot))
        {
            Contacts.SnapStatus = "unwalkable ground";
            return false;
        }
        float distance = -_probeResult.GetTravel().Dot(up);
        if (distance < CollisionMargin * 1.5f ||
            distance > SnapDownDistance + 0.005f)
        {
            Contacts.SnapStatus = "outside useful snap range";
            return false;
        }
        snapped.Origin += _probeResult.GetTravel();
        Contacts.SnappedDown = true;
        Contacts.SnapDistance = distance;
        Contacts.SnapStatus = "accepted";
        return true;
    }

    private static bool HasWalkableContact(PhysicsTestMotionResult3D result,
        Vector3 up, float walkDot)
    {
        for (int i = 0; i < result.GetCollisionCount(); i++)
            if (result.GetCollisionNormal(i).Normalized().Dot(up) >= walkDot)
                return true;
        return false;
    }

    private Vector3 ClipAgainstPlanes(Vector3 motion, Vector3 up, float walkDot)
    {
        Vector3 clipped = motion;
        // Iteration across all contact planes handles the wall/ground crease and
        // two-wall corners without alternating penetration at each bounce.
        for (int iteration = 0; iteration < _planeCount; iteration++)
        {
            bool changed = false;
            for (int i = 0; i < _planeCount; i++)
            {
                Vector3 normal = _planes[i];
                if (clipped.Dot(normal) >= 0f)
                    continue;
                Vector3 projected = clipped.Slide(normal);
                // A steep slope may cause downward sliding, but cannot lift the
                // player upward via the horizontal walking input.
                if (normal.Dot(up) > 0.05f && normal.Dot(up) < walkDot &&
                    projected.Dot(up) > Mathf.Max(0f, clipped.Dot(up)))
                    projected -= up * (projected.Dot(up) - Mathf.Max(0f, clipped.Dot(up)));
                clipped = projected;
                changed = true;
            }
            if (!changed)
                break;
        }
        return clipped.LengthSquared() <= motion.LengthSquared() + 0.000001f ? clipped : Vector3.Zero;
    }
}
