using Godot;

public readonly struct CharacterProximityContact
{
    public readonly Vector3 Point;
    public readonly Vector3 Normal;
    public readonly Rid ColliderRid;
    public readonly ulong ColliderObjectId;

    public CharacterProximityContact(Vector3 point, Vector3 normal, Rid rid, ulong objectId)
    {
        Point = point;
        Normal = normal;
        ColliderRid = rid;
        ColliderObjectId = objectId;
    }
}

// Physics-tick contact cache. Consumers decide how to respond to these surfaces.
public partial class CharacterProximitySensor : ShapeCast3D
{
    private const int ContactCapacity = 8;

    [Export] public CharacterBody3D Body { get; set; }
    [Export] public Node3D ViewAnchor { get; set; }
    [Export(PropertyHint.Range, "0.05,2,0.01")]
    public float ProximityRadius { get; set; } = 0.45f;
    // Zero inherits the character's collision mask.
    [Export] public uint CollisionMaskOverride { get; set; }

    public ulong PhysicsTick { get; private set; }
    public ulong InvalidationSerial { get; private set; }
    public int ContactCount { get; private set; }
    public Vector3 PhysicsNeutralEyePosition { get; private set; }
    public Vector3 SensorWorldPosition { get; private set; }
    public float SensorCenterError => SensorWorldPosition.DistanceTo(PhysicsNeutralEyePosition);
    public float SensedRadius { get; private set; }

    private readonly CharacterProximityContact[] _contacts = new CharacterProximityContact[ContactCapacity];
    private SphereShape3D _sphere;
    private Vector3 _neutralEyeLocalOffset;

    public override void _Ready()
    {
        Body ??= GetParent<CharacterBody3D>();
        ViewAnchor ??= GetNode<Node3D>("../ViewRoot/FacingYaw/ViewAnchor");
        Node3D facingYaw = ViewAnchor.GetParent<Node3D>();
        // ViewRoot is top-level and render-interpolated. Cache only scene-local
        // neutral geometry, then apply it to the authoritative physics root.
        _neutralEyeLocalOffset = (facingYaw.Transform * ViewAnchor.Transform).Origin;
        if (!GetNode<Node3D>("../ViewRoot/FacingYaw/ViewAnchor/ViewMotionPosition").Position.IsZeroApprox() ||
            !GetNode<Node3D>("../ViewRoot/FacingYaw/ViewAnchor/ViewMotionPosition/PitchPivot").Position.IsZeroApprox() ||
            !GetNode<Node3D>("../ViewRoot/FacingYaw/ViewAnchor/ViewMotionPosition/PitchPivot/ViewMotionRotation").Position.IsZeroApprox() ||
            !GetNode<Camera3D>("../ViewRoot/FacingYaw/ViewAnchor/ViewMotionPosition/PitchPivot/ViewMotionRotation/Camera3D").Position.IsZeroApprox())
            GD.PushWarning("Neutral camera descendants have positional offsets; update the sensor's cached neutral-eye chain.");
        _sphere = Shape as SphereShape3D ?? new SphereShape3D();
        Shape = _sphere;
        SensedRadius = Mathf.Max(ProximityRadius, 0.001f);
        _sphere.Radius = SensedRadius;
        TargetPosition = Vector3.Zero;
        Enabled = false;
        ExcludeParent = true;
        CollideWithBodies = true;
        CollideWithAreas = false;
        MaxResults = ContactCapacity;
        CollisionMask = CollisionMaskOverride == 0 ? Body.CollisionMask : CollisionMaskOverride;
        Invalidate();
    }

    public override void _PhysicsProcess(double delta)
    {
        PhysicsNeutralEyePosition = Body.GlobalTransform * _neutralEyeLocalOffset;
        GlobalPosition = PhysicsNeutralEyePosition;
        SensorWorldPosition = GlobalPosition;
        float radius = Mathf.Max(ProximityRadius, 0.001f);
        if (!Mathf.IsEqualApprox(_sphere.Radius, radius))
            _sphere.Radius = radius;
        SensedRadius = radius;
        CollisionMask = CollisionMaskOverride == 0 ? Body.CollisionMask : CollisionMaskOverride;
        ContactCount = 0;
        ForceShapecastUpdate();
        int found = Mathf.Min(GetCollisionCount(), ContactCapacity);
        for (int i = 0; i < found; i++)
        {
            Vector3 point = GetCollisionPoint(i);
            Vector3 normal = GetCollisionNormal(i);
            if (!point.IsFinite() || !normal.IsFinite() || normal.LengthSquared() < 0.000001f)
                continue;
            GodotObject collider = GetCollider(i);
            _contacts[ContactCount++] = new CharacterProximityContact(point,
                normal.Normalized(), GetColliderRid(i), collider?.GetInstanceId() ?? 0);
        }
        PhysicsTick = Engine.GetPhysicsFrames();
    }

    public CharacterProximityContact GetContact(int index)
    {
        if ((uint)index >= (uint)ContactCount)
            throw new System.ArgumentOutOfRangeException(nameof(index));
        return _contacts[index];
    }

    public void Invalidate()
    {
        ContactCount = 0;
        PhysicsTick = 0;
        InvalidationSerial++;
    }
}
