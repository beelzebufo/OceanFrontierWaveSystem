using Godot;

// Render-rate position constraint over contacts cached by the physics sensor.
public partial class CameraObstructionGuard : Node
{
    [Export] public CharacterProximitySensor Sensor { get; set; }
    [Export] public Node3D ViewAnchor { get; set; }
    [Export] public Camera3D Camera { get; set; }
    [Export] public bool CameraGuardEnabled { get; set; } = true;
    [Export(PropertyHint.Range, "0.01,0.5,0.005")]
    public float CameraClearanceRadius { get; set; } = 0.10f;
    [Export(PropertyHint.Range, "0,0.05,0.001")]
    public float CameraSafetyMargin { get; set; } = 0.01f;

    public Vector3 DesiredCameraOffset { get; private set; }
    public Vector3 CoverageLimitedCameraOffset { get; private set; }
    public Vector3 SafeCameraOffset { get; private set; }
    public float CameraSafeFraction { get; private set; } = 1f;
    public float CoverageSafeFraction { get; private set; } = 1f;
    public float ContactSafeFraction { get; private set; } = 1f;
    public float MaxGuaranteedOffset { get; private set; }
    public bool CameraBlocked { get; private set; }
    public bool BaseObstructed { get; private set; }
    public bool CoverageExceeded { get; private set; }
    public float EffectiveCameraClearance { get; private set; }
    public float MinimumNearPlaneBoundingRadius { get; private set; }
    public int ProximityContactCount { get; private set; }
    public Rid BlockingColliderRid { get; private set; }
    public Vector3 BlockingNormal { get; private set; }
    public Vector3 BlockingPoint { get; private set; }

    public override void _Ready()
    {
        Sensor ??= GetNode<CharacterProximitySensor>("../CharacterProximitySensor");
        ViewAnchor ??= GetNode<Node3D>("../ViewRoot/FacingYaw/ViewAnchor");
        Camera ??= GetNode<Camera3D>(
            "../ViewRoot/FacingYaw/ViewAnchor/ViewMotionPosition/PitchPivot/ViewMotionRotation/Camera3D");
    }

    public Vector3 ConstrainLocalOffset(Vector3 desiredLocalOffset)
    {
        DesiredCameraOffset = desiredLocalOffset.IsFinite() ? desiredLocalOffset : Vector3.Zero;
        CoverageLimitedCameraOffset = SafeCameraOffset = DesiredCameraOffset;
        CameraSafeFraction = CoverageSafeFraction = ContactSafeFraction = 1f;
        CameraBlocked = BaseObstructed = CoverageExceeded = false;
        BlockingColliderRid = default;
        BlockingNormal = BlockingPoint = Vector3.Zero;
        MinimumNearPlaneBoundingRadius = CalculateNearPlaneRadius();
        EffectiveCameraClearance = Mathf.Max(Mathf.Max(CameraClearanceRadius, 0f),
            MinimumNearPlaneBoundingRadius + Mathf.Max(CameraSafetyMargin, 0f));
        ProximityContactCount = Sensor?.ContactCount ?? 0;
        if (!CameraGuardEnabled || Sensor == null)
            return SafeCameraOffset;

        Vector3 baseWorld = ViewAnchor.GlobalPosition;
        float centerLag = Sensor.PhysicsNeutralEyePosition.DistanceTo(baseWorld);
        // Include render interpolation lag: the camera envelope must fit inside
        // the sphere actually sampled around the current physics eye.
        MaxGuaranteedOffset = Mathf.Max(0f, Sensor.SensedRadius -
            EffectiveCameraClearance - Mathf.Max(CameraSafetyMargin, 0f) - centerLag);
        float desiredLength = DesiredCameraOffset.Length();
        if (Sensor.PhysicsTick == 0 || centerLag + EffectiveCameraClearance >= Sensor.SensedRadius)
        {
            CoverageExceeded = true;
            CoverageLimitedCameraOffset = SafeCameraOffset = Vector3.Zero;
            CoverageSafeFraction = CameraSafeFraction = 0f;
            return SafeCameraOffset;
        }
        if (desiredLength > MaxGuaranteedOffset)
        {
            CoverageExceeded = true;
            CoverageSafeFraction = MaxGuaranteedOffset / desiredLength;
            CoverageLimitedCameraOffset = DesiredCameraOffset * CoverageSafeFraction;
        }
        // The contact-plane pass cannot re-expand an offset beyond sensor coverage.
        Vector3 desiredWorld = baseWorld + ViewAnchor.GlobalBasis * CoverageLimitedCameraOffset;
        float requiredDistance = EffectiveCameraClearance;
        for (int i = 0; i < Sensor.ContactCount; i++)
        {
            CharacterProximityContact contact = Sensor.GetContact(i);
            Vector3 normal = contact.Normal;
            if (!normal.IsFinite() || normal.LengthSquared() < 0.000001f ||
                !contact.Point.IsFinite())
                continue;
            float baseDistance = (baseWorld - contact.Point).Dot(normal);
            float desiredDistance = (desiredWorld - contact.Point).Dot(normal);
            if (!float.IsFinite(baseDistance) || !float.IsFinite(desiredDistance))
                continue;
            if (baseDistance < requiredDistance)
            {
                BaseObstructed = CameraBlocked = true;
                ContactSafeFraction = 0f;
                BlockingColliderRid = contact.ColliderRid;
                BlockingNormal = normal;
                BlockingPoint = contact.Point;
                break;
            }
            if (desiredDistance >= requiredDistance)
                continue;
            float fraction = Mathf.Clamp((baseDistance - requiredDistance) /
                (baseDistance - desiredDistance), 0f, 1f);
            if (fraction < ContactSafeFraction)
            {
                ContactSafeFraction = fraction;
                CameraBlocked = true;
                BlockingColliderRid = contact.ColliderRid;
                BlockingNormal = normal;
                BlockingPoint = contact.Point;
            }
        }
        CameraSafeFraction = CoverageSafeFraction * ContactSafeFraction;
        SafeCameraOffset = CoverageLimitedCameraOffset * ContactSafeFraction;
        return SafeCameraOffset;
    }

    public void ResetGuard()
    {
        Sensor?.Invalidate();
        DesiredCameraOffset = CoverageLimitedCameraOffset = SafeCameraOffset = Vector3.Zero;
        CameraSafeFraction = CoverageSafeFraction = ContactSafeFraction = 1f;
        MaxGuaranteedOffset = 0f;
        CameraBlocked = BaseObstructed = CoverageExceeded = false;
        EffectiveCameraClearance = MinimumNearPlaneBoundingRadius = 0f;
        ProximityContactCount = 0;
        BlockingColliderRid = default;
        BlockingNormal = BlockingPoint = Vector3.Zero;
    }

    private float CalculateNearPlaneRadius()
    {
        if (Camera == null || Camera.Projection != Camera3D.ProjectionType.Perspective)
            return 0f;
        Vector2 size = Camera.GetViewport().GetVisibleRect().Size;
        float aspect = size.Y > 0f ? size.X / size.Y : 1f;
        float near = Mathf.Max(Camera.Near, 0f);
        float halfFov = Mathf.DegToRad(Mathf.Clamp(Camera.Fov, 1f, 179f)) * 0.5f;
        float halfWidth;
        float halfHeight;
        if (Camera.KeepAspect == Camera3D.KeepAspectEnum.Height)
        {
            halfHeight = near * Mathf.Tan(halfFov);
            halfWidth = halfHeight * aspect;
        }
        else
        {
            halfWidth = near * Mathf.Tan(halfFov);
            halfHeight = halfWidth / Mathf.Max(aspect, 0.001f);
        }
        return Mathf.Sqrt(near * near + halfWidth * halfWidth + halfHeight * halfHeight);
    }
}
