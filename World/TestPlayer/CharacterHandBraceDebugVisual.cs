using Godot;

// Temporary, presentation-only world gizmos. All nodes, meshes and materials
// are created once when the Inspector toggle is first enabled.
internal sealed class CharacterHandBraceDebugVisual
{
    private const float AxisLength = 0.12f;
    private const float AxisThickness = 0.004f;
    private const float MarkerRadius = 0.023f;

    private readonly HandGizmo _left;
    private readonly HandGizmo _right;

    private sealed class HandGizmo
    {
        public Node3D Root;
        public Node3D Presented;
        public MeshInstance3D Raw;
        public MeshInstance3D Connector;
    }

    public CharacterHandBraceDebugVisual(Node parent)
    {
        StandardMaterial3D xMaterial = Material(new Color(1f, 0.22f, 0.22f));
        StandardMaterial3D yMaterial = Material(new Color(0.28f, 1f, 0.28f));
        StandardMaterial3D zMaterial = Material(new Color(0.28f, 0.48f, 1f));
        SphereMesh presentedMesh = new() { Radius = MarkerRadius, Height = MarkerRadius * 2f };
        SphereMesh rawMesh = new() { Radius = 0.012f, Height = 0.024f };
        BoxMesh xMesh = new() { Size = new Vector3(AxisLength, AxisThickness, AxisThickness) };
        BoxMesh yMesh = new() { Size = new Vector3(AxisThickness, AxisLength, AxisThickness) };
        BoxMesh zMesh = new() { Size = new Vector3(AxisThickness, AxisThickness, AxisLength) };
        BoxMesh connectorMesh = new() { Size = new Vector3(0.004f, 1f, 0.004f) };
        _left = CreateHand(parent, "LeftBraceDebugWorld", presentedMesh, rawMesh,
            xMesh, yMesh, zMesh, connectorMesh, xMaterial, yMaterial, zMaterial,
            new Color(0.12f, 0.95f, 0.95f));
        _right = CreateHand(parent, "RightBraceDebugWorld", presentedMesh, rawMesh,
            xMesh, yMesh, zMesh, connectorMesh, xMaterial, yMaterial, zMaterial,
            new Color(1f, 0.65f, 0.12f));
        Hide();
    }

    public void Update(HandBraceTarget left, HandBraceTarget right)
    {
        UpdateHand(_left, left);
        UpdateHand(_right, right);
    }

    public void Hide()
    {
        _left.Root.Visible = false;
        _right.Root.Visible = false;
    }

    private static StandardMaterial3D Material(Color color) => new()
    {
        AlbedoColor = color,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
    };

    private static MeshInstance3D AddMesh(Node3D parent, string name,
        Mesh mesh, StandardMaterial3D material)
    {
        MeshInstance3D instance = new()
        {
            Name = name,
            Mesh = mesh,
            MaterialOverride = material
        };
        parent.AddChild(instance);
        return instance;
    }

    private static HandGizmo CreateHand(Node parent, string name,
        SphereMesh presentedMesh, SphereMesh rawMesh,
        BoxMesh xMesh, BoxMesh yMesh, BoxMesh zMesh, BoxMesh connectorMesh,
        StandardMaterial3D xMaterial, StandardMaterial3D yMaterial,
        StandardMaterial3D zMaterial, Color handColor)
    {
        Node3D root = new() { Name = name, TopLevel = true };
        parent.AddChild(root);
        Node3D presented = new() { Name = "SmoothedTargetAndBasis" };
        root.AddChild(presented);
        AddMesh(presented, "SmoothedTarget", presentedMesh, Material(handColor));
        AddMesh(presented, "TangentX", xMesh, xMaterial).Position = Vector3.Right * AxisLength * 0.5f;
        AddMesh(presented, "SurfaceUpY", yMesh, yMaterial).Position = Vector3.Up * AxisLength * 0.5f;
        AddMesh(presented, "OutwardNormalZ", zMesh, zMaterial).Position = Vector3.Back * AxisLength * 0.5f;
        MeshInstance3D raw = AddMesh(root, "RawTarget", rawMesh,
            Material(handColor.Darkened(0.45f)));
        MeshInstance3D connector = AddMesh(root, "RawToSmoothed", connectorMesh,
            Material(handColor.Lightened(0.35f)));
        return new HandGizmo { Root = root, Presented = presented, Raw = raw,
            Connector = connector };
    }

    private static void UpdateHand(HandGizmo visual, HandBraceTarget target)
    {
        bool physical = target.HasPhysicalContact;
        bool presented = target.IsPresented;
        visual.Root.Visible = physical || presented;
        if (!visual.Root.Visible)
            return;

        // The root is top-level: its world transform never inherits Player,
        // ViewRoot or procedural camera motion.
        visual.Root.GlobalTransform = new Transform3D(
            target.SmoothedTargetBasis, target.SmoothedTargetPosition);
        visual.Presented.Visible = presented;
        if (presented)
            visual.Presented.Scale = Vector3.One * Mathf.Sqrt(Mathf.Clamp(target.Weight, 0f, 1f));

        visual.Raw.Visible = physical;
        visual.Connector.Visible = false;
        if (!physical)
            return;
        visual.Raw.GlobalPosition = target.RawTargetPosition;
        visual.Raw.Scale = Vector3.One * (0.5f + 0.5f * target.Weight);
        if (!presented)
            return;
        Vector3 displacement = target.SmoothedTargetPosition - target.RawTargetPosition;
        float length = displacement.Length();
        if (length <= 0.0001f || !float.IsFinite(length))
            return;
        Vector3 direction = displacement / length;
        Vector3 reference = Mathf.Abs(direction.Y) < 0.9f ? Vector3.Up : Vector3.Right;
        Vector3 x = reference.Cross(direction).Normalized();
        Vector3 z = x.Cross(direction).Normalized();
        visual.Connector.GlobalTransform = new Transform3D(new Basis(x, direction, z),
            (target.RawTargetPosition + target.SmoothedTargetPosition) * 0.5f);
        float thickness = Mathf.Sqrt(Mathf.Clamp(target.Weight, 0f, 1f));
        visual.Connector.Scale = new Vector3(thickness, length, thickness);
        visual.Connector.Visible = true;
    }
}
