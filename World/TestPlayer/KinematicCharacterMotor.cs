using Godot;

public partial class KinematicCharacterMotor : Node
{
    private enum SnapOutcome { None, Primary, SupportContinuity }
    private enum StepOutcome { None, Landing, SupportedProgress }

    private readonly struct StaticSupportCandidate
    {
        public readonly Vector3 Normal;
        public readonly Vector3 Point;
        public readonly Rid Rid;
        public readonly ulong ObjectId;
        public readonly float Drop;
        public readonly float AppliedDrop;

        public StaticSupportCandidate(Vector3 normal, Vector3 point, Rid rid,
            ulong objectId, float drop, float appliedDrop)
        {
            Normal = normal;
            Point = point;
            Rid = rid;
            ObjectId = objectId;
            Drop = drop;
            AppliedDrop = appliedDrop;
        }
    }

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
    private readonly PhysicsTestMotionResult3D _validationResult = new();
    private readonly PhysicsRayQueryParameters3D _supportRay = new();
    private readonly Godot.Collections.Array<Rid> _supportExclusions = new();
    private readonly Vector3[] _planes = new Vector3[32];
    private int _planeCount;

    public override void _Ready()
    {
        Body ??= GetParent<CharacterBody3D>();
        _query.RecoveryAsCollision = true;
        _query.CollideSeparationRay = false;
        _query.ExcludeBodies = _noExclusions;
        _supportExclusions.Add(Body.GetRid());
        _supportRay.Exclude = _supportExclusions;
        _supportRay.CollideWithBodies = true;
        _supportRay.CollideWithAreas = false;
        _supportRay.HitBackFaces = false;
    }

    public void Simulate(Vector3 requestedMotion, Vector3 walkMotion,
        bool allowGrounding = true, bool allowPlatformCarry = true)
    {
        bool wasStable = Contacts.IsStable;
        bool wasSupportContinuity = Contacts.SupportContinuityActive;
        bool hadCeiling = Contacts.HasCeiling;
        Vector3 previousSupportPoint = Contacts.GroundPoint;
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
                StepOutcome step = TryStep(simulated, forward, walkMotion, previousSupportPoint,
                    up, walkDot, out Transform3D landing, out StaticSupportCandidate stepSupport);
                if (step != StepOutcome.None)
                {
                    simulated = landing;
                    Contacts.ClearGround();
                    if (step == StepOutcome.Landing)
                        RecordContacts(_probeResult, up, walkDot);
                    else
                        Contacts.SetSupportContinuity(stepSupport.Normal, stepSupport.Point,
                            stepSupport.Rid, stepSupport.ObjectId, stepSupport.Drop,
                            stepSupport.AppliedDrop, up);
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

            if (wasStable && allowGrounding && !Contacts.IsStable)
            {
                SnapOutcome outcome = TrySnapDown(simulated, walkMotion, previousSupportPoint,
                    wasSupportContinuity, up, walkDot,
                    out Transform3D snapped, out StaticSupportCandidate support);
                if (outcome != SnapOutcome.None)
                {
                    simulated = snapped;
                    Contacts.ClearGround();
                    if (outcome == SnapOutcome.Primary)
                        RecordContacts(_probeResult, up, walkDot);
                    else
                        Contacts.SetSupportContinuity(support.Normal, support.Point,
                            support.Rid, support.ObjectId, support.Drop, support.AppliedDrop, up);
                }
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

    private StepOutcome TryStep(Transform3D from, Vector3 forward, Vector3 walkMotion,
        Vector3 previousSupportPoint, Vector3 up, float walkDot,
        out Transform3D landing, out StaticSupportCandidate support)
    {
        landing = from;
        support = default;
        Contacts.StepForwardRequested = forward.Length();
        if (forward.LengthSquared() < 0.00000001f)
        {
            Contacts.StepStatus = "too little forward motion";
            return StepOutcome.None;
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
            return StepOutcome.None;
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
            return StepOutcome.None;
        }

        Transform3D advanced = raised;
        advanced.Origin += Test(raised, forward, _probeResult, false)
            ? _probeResult.GetTravel() : forward;
        float forwardTravelled = (advanced.Origin - raised.Origin).Dot(forward.Normalized());
        Contacts.StepForwardTravelled = forwardTravelled;
        if (forwardTravelled < forward.Length() - 0.005f)
        {
            Contacts.StepStatus = "forward blocked";
            return StepOutcome.None;
        }

        Vector3 descent = -up * (StepHeight + SnapDownDistance);
        if (!Test(advanced, descent, _probeResult, false) ||
            !HasWalkableContact(_probeResult, up, walkDot))
        {
            if (TrySupportedStepProgress(from, raised, advanced, direction,
                walkMotion, previousSupportPoint, descent, up, walkDot,
                out landing, out support))
                return StepOutcome.SupportedProgress;
            Contacts.StepStatus = "no walkable landing";
            return StepOutcome.None;
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
            return StepOutcome.None;
        }
        if (height < -SnapDownDistance - 0.005f || height > StepHeight + 0.005f)
        {
            Contacts.StepStatus = "landing outside step range";
            return StepOutcome.None;
        }
        Contacts.SteppedUp = true;
        Contacts.StepRise = height;
        Contacts.StepStatus = "accepted";
        Contacts.SnappedDown = true;
        Contacts.SnapDistance = (advanced.Origin - landing.Origin).Dot(up);
        Contacts.SnapStatus = "step landing";
        return StepOutcome.Landing;
    }

    private bool TrySupportedStepProgress(Transform3D from, Transform3D raised,
        Transform3D advanced, Vector3 direction, Vector3 walkMotion,
        Vector3 previousSupportPoint, Vector3 descent, Vector3 up, float walkDot,
        out Transform3D landing, out StaticSupportCandidate support)
    {
        landing = from;
        support = default;
        // The fixed-depth lookahead only supplies a target tread height. The
        // committed horizontal travel remains this tick's actual remainder.
        Transform3D lookahead = raised;
        lookahead.Origin += direction * StepUpDepth;
        if (!Test(lookahead, descent, _validationResult, false) ||
            !HasWalkableContact(_validationResult, up, walkDot))
            return false;

        Vector3 futureOrigin = lookahead.Origin + _validationResult.GetTravel();
        float rise = (futureOrigin - from.Origin).Dot(up);
        if (rise <= CollisionMargin * 2f || rise > StepHeight + 0.005f)
            return false;

        Transform3D candidate = advanced;
        candidate.Origin += up * (futureOrigin - candidate.Origin).Dot(up);
        Vector3 down = candidate.Origin - advanced.Origin;
        if (down.Dot(up) >= 0f)
            return false;
        if (Test(advanced, down, _validationResult, false) &&
            _validationResult.GetTravel().DistanceTo(down) > CollisionMargin * 0.5f)
            return false;

        // This is a progress pose, not an upper-tread landing. A fresh ray at
        // the final pose must confirm the same static tread as last tick.
        if (TryEdgeSupport(candidate, walkMotion, Vector3.Zero, previousSupportPoint,
                true, up, walkDot, out landing, out support) != SnapOutcome.SupportContinuity)
            return false;

        Contacts.SteppedUp = true;
        Contacts.StepRise = (landing.Origin - from.Origin).Dot(up);
        Contacts.StepLandingDelta = Contacts.StepRise;
        Contacts.StepStatus = "supported progress";
        return true;
    }

    private SnapOutcome TrySnapDown(Transform3D from, Vector3 walkMotion,
        Vector3 previousSupportPoint, bool wasSupportContinuity, Vector3 up,
        float walkDot, out Transform3D snapped, out StaticSupportCandidate support)
    {
        snapped = from;
        support = default;
        if (!Test(from, -up * SnapDownDistance, _probeResult, false))
        {
            Contacts.SnapPrimaryStatus = "no hit";
            Contacts.SnapStatus = "no ground in range";
            return SnapOutcome.None;
        }
        Vector3 primaryTravel = _probeResult.GetTravel();
        Contacts.SnapPrimaryTravel = -primaryTravel.Dot(up);
        float bestPrimaryDot = -1f;
        for (int i = 0; i < _probeResult.GetCollisionCount(); i++)
        {
            Vector3 normal = _probeResult.GetCollisionNormal(i).Normalized();
            float dot = normal.Dot(up);
            if (dot > bestPrimaryDot)
            {
                bestPrimaryDot = dot;
                Contacts.SnapPrimaryNormal = normal;
                Contacts.SnapPrimarySlope = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(dot, -1f, 1f)));
            }
        }
        if (!HasWalkableContact(_probeResult, up, walkDot))
        {
            Contacts.SnapPrimaryStatus = "unwalkable";
            Contacts.SnapStatus = "unwalkable ground";
            Vector3 planarWalk = walkMotion.Slide(up);
            Vector3 sideNormal = Contacts.SnapPrimaryNormal.Slide(up);
            if (!wasSupportContinuity &&
                (planarWalk.LengthSquared() < 0.000001f ||
                 sideNormal.LengthSquared() < 0.000001f ||
                 sideNormal.Normalized().Dot(planarWalk.Normalized()) <= 0.1f))
            {
                Contacts.SnapSecondaryValidation = "not a trailing edge";
                return SnapOutcome.None;
            }
            return TryEdgeSupport(from, walkMotion, primaryTravel,
                previousSupportPoint, false, up, walkDot,
                out snapped, out support);
        }
        Contacts.SnapPrimaryStatus = "walkable";
        float distance = -_probeResult.GetTravel().Dot(up);
        if (distance < 0f || distance > SnapDownDistance + 0.005f)
        {
            Contacts.SnapStatus = "outside useful snap range";
            return SnapOutcome.None;
        }
        if (distance < CollisionMargin * 1.5f)
        {
            // The fresh full-capsule sweep already found walkable support.
            // Keep the pose to avoid applying skin-sized recovery every tick.
            Contacts.SnapStatus = "primary contact support";
            Contacts.SnapSource = "primary";
            return SnapOutcome.Primary;
        }
        snapped.Origin += primaryTravel;
        Contacts.SnappedDown = true;
        Contacts.SnapDistance = distance;
        Contacts.SnapStatus = "accepted";
        Contacts.SnapSource = "primary";
        return SnapOutcome.Primary;
    }

    private SnapOutcome TryEdgeSupport(Transform3D from, Vector3 walkMotion,
        Vector3 primaryTravel, Vector3 previousSupportPoint, bool requireSameLevel,
        Vector3 up, float walkDot,
        out Transform3D snapped, out StaticSupportCandidate support)
    {
        snapped = from;
        support = default;
        Contacts.SnapSecondaryAttempted = true;
        Vector3 planarWalk = walkMotion - up * walkMotion.Dot(up);
        Vector3 forward = planarWalk.LengthSquared() > 0.000001f
            ? planarWalk.Normalized() * 0.12f : Vector3.Zero;
        PhysicsDirectSpaceState3D space = Body.GetWorld3D().DirectSpaceState;
        _supportRay.CollisionMask = Body.CollisionMask;
        const float startHeight = 0.05f;
        float maxDrop = SnapDownDistance + 0.005f;
        float bestDrop = float.PositiveInfinity;
        Vector3 bestNormal = Vector3.Zero;
        Vector3 bestPoint = Vector3.Zero;
        Rid bestRid = default;
        ulong bestObjectId = 0;
        bool sawMovingCandidate = false;
        bool sawTooDeepCandidate = false;

        // The second sample is a short look ahead, not a player displacement.
        for (int sample = 0; sample < 2; sample++)
        {
            if (sample == 1 && forward.IsZeroApprox())
                break;
            Vector3 offset = sample == 0 ? Vector3.Zero : forward;
            _supportRay.From = from.Origin + offset + up * startHeight;
            _supportRay.To = from.Origin + offset - up * (maxDrop + CollisionMargin);
            Godot.Collections.Dictionary hit = space.IntersectRay(_supportRay);
            if (hit.Count == 0)
                continue;
            Vector3 normal = ((Vector3)hit["normal"]).Normalized();
            if (normal.Dot(up) < walkDot)
                continue;
            Vector3 point = (Vector3)hit["position"];
            float drop = (from.Origin - point).Dot(up) - CollisionMargin;
            if (drop <= CollisionMargin * 1.5f || drop > maxDrop || drop >= bestDrop)
                continue;
            if (requireSameLevel &&
                Mathf.Abs((point - previousSupportPoint).Dot(up)) > CollisionMargin)
                continue;
            // The capsule can slide partway down a rounded upper edge while
            // still classed stable. Keep the full level change bounded by the
            // last confirmed support, not just the current capsule height.
            if ((previousSupportPoint - point).Dot(up) > maxDrop)
            {
                sawTooDeepCandidate = true;
                continue;
            }
            Rid rid = (Rid)hit["rid"];
            ulong objectId = (ulong)(long)hit["collider_id"];
            // Server-owned terrain bodies can have no ObjectId. Physics mode
            // still proves they are static. AnimatableBody3D derives from
            // StaticBody3D, so explicitly exclude it and every moving type.
            GodotObject collider = objectId == 0 ? null : GodotObject.InstanceFromId(objectId);
            if (PhysicsServer3D.BodyGetMode(rid) != PhysicsServer3D.BodyMode.Static ||
                collider is AnimatableBody3D or RigidBody3D or CharacterBody3D)
            {
                sawMovingCandidate = true;
                continue;
            }
            bestDrop = drop;
            bestNormal = normal;
            bestPoint = point;
            bestRid = rid;
            bestObjectId = objectId;
        }

        if (bestNormal.IsZeroApprox())
        {
            Contacts.SnapSecondaryValidation = sawTooDeepCandidate
                ? "support level beyond snap range"
                : sawMovingCandidate ? "moving support excluded" : "no walkable candidate";
            return SnapOutcome.None;
        }
        Contacts.SnapSecondaryCandidateFound = true;
        Contacts.SnapSecondaryNormal = bestNormal;
        Contacts.SnapSecondarySlope = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(bestNormal.Dot(up), -1f, 1f)));
        Contacts.SnapSecondaryDrop = bestDrop;

        // The ray classifies support only. Jolt's primary full-capsule sweep
        // supplies the allowed downward travel. Its lateral recovery is never
        // applied to the player, even when the final floor pose is blocked.
        float safeDown = Mathf.Clamp(-primaryTravel.Dot(up), 0f, bestDrop);
        if (safeDown > 0.0001f)
        {
            Transform3D increment = from;
            increment.Origin -= up * safeDown;
            if (Test(increment, Vector3.Zero, _validationResult))
            {
                float deepest = 0f;
                for (int i = 0; i < _validationResult.GetCollisionCount(); i++)
                    deepest = Mathf.Max(deepest, _validationResult.GetCollisionDepth(i));
                Contacts.SnapSecondaryRecovery = _validationResult.GetTravel();
                if (_validationResult.GetTravel().Length() > CollisionMargin * 2f ||
                    deepest > CollisionMargin * 1.5f)
                    safeDown = 0f;
            }
            if (safeDown > 0f)
                snapped = increment;
        }

        support = new StaticSupportCandidate(bestNormal, bestPoint, bestRid,
            bestObjectId, bestDrop, safeDown);
        Contacts.SnapStatus = "support continuity";
        Contacts.SnapSource = "support continuity";
        Contacts.SnapDistance = safeDown;
        Contacts.SnapSecondaryValidation = safeDown > 0f
            ? "support continuity + safe vertical travel" : "support continuity (hold height)";
        return SnapOutcome.SupportContinuity;
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
