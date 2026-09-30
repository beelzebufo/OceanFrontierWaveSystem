using Godot;

// Stores one relative attachment. The motor owns all collision queries and movement.
public sealed class CharacterGroundMotionTracker
{
    private Node3D _platform;
    private Rid _rid;
    private ulong _objectId;
    private string _name;
    private Transform3D _platformTransform;
    private Vector3 _playerLocalOrigin;

    public Rid PlatformRid => _rid;
    public string Transition { get; private set; }
    public float CarryRequested { get; private set; }
    public float CarryTravelled { get; private set; }
    public bool CarryBlocked { get; private set; }

    public void BeginTick()
    {
        Transition = null;
        CarryRequested = CarryTravelled = 0f;
        CarryBlocked = false;
        if (_platform != null && !GodotObject.IsInstanceValid(_platform))
            Clear("detach " + _name + " (invalid)");
    }

    public Vector3 ComputeCarryMotion(Vector3 playerWorldOrigin)
    {
        if (_platform == null)
            return Vector3.Zero;
        _platformTransform = _platform.GlobalTransform;
        return _platformTransform * _playerLocalOrigin - playerWorldOrigin;
    }

    public void RecordCarry(Vector3 requested, Vector3 traveled)
    {
        CarryRequested = requested.Length();
        CarryTravelled = Mathf.Max(0f, traveled.Dot(requested.Normalized()));
        CarryBlocked = requested.LengthSquared() > 0.00000001f &&
            CarryTravelled < CarryRequested - 0.005f;
    }

    public void UpdateAttachment(CharacterContactState contact, Vector3 playerWorldOrigin)
    {
        Node3D candidate = ResolveMovingGround(contact);
        if (candidate == null)
        {
            if (_platform != null)
                Clear("detach " + _name);
            return;
        }

        ulong candidateId = contact.GroundColliderObjectId;
        Rid candidateRid = contact.GroundColliderRid;
        if (_platform == null || _objectId != candidateId || _rid != candidateRid)
        {
            string nextName = candidate.Name + "/" + candidateId;
            Transition = _platform == null ? "attach " + nextName :
                "switch " + _name + " -> " + nextName;
            _name = nextName;
            _platform = candidate;
            _objectId = candidateId;
            _rid = candidateRid;
        }

        _platformTransform = candidate.GlobalTransform;
        _playerLocalOrigin = _platformTransform.AffineInverse() * playerWorldOrigin;
    }

    private static Node3D ResolveMovingGround(CharacterContactState contact)
    {
        if (!contact.IsStable || contact.GroundColliderObjectId == 0)
            return null;
        // Static terrain and scene geometry have no transform delta to follow.
        GodotObject collider = GodotObject.InstanceFromId(contact.GroundColliderObjectId);
        // AnimatableBody3D derives from StaticBody3D in Godot, so test the
        // movable types explicitly instead of excluding that base class.
        return collider is AnimatableBody3D or RigidBody3D or CharacterBody3D
            ? (Node3D)collider : null;
    }

    private void Clear(string transition)
    {
        Transition = transition;
        _platform = null;
        _rid = default;
        _objectId = 0;
        _name = null;
        _platformTransform = Transform3D.Identity;
        _playerLocalOrigin = Vector3.Zero;
    }
}
