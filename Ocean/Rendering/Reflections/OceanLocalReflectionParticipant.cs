using Godot;

namespace OceanFrontier.Water.Rendering.Reflections;

/// <summary>Scene-owned metadata for one possible near-field reflector.</summary>
public partial class OceanLocalReflectionParticipant : Node
{
	[Export] public NodePath RegistryPath { get; set; }
	[Export] public NodePath SourceRootPath { get; set; }
	[Export] public NodePath CenterAnchorPath { get; set; }

	private OceanLocalReflectionRegistry _registry;
	private Node3D _sourceRoot;
	private Node3D _centerAnchor;
	private bool _enabled = true;
	private float _influenceRadius = 14.0f;
	private int _priority;

	[Export]
	public bool Enabled
	{
		get => _enabled;
		set
		{
			if (_enabled == value) return;
			_enabled = value;
			_registry?.ParticipantChanged();
		}
	}

	[Export(PropertyHint.Range, "0.1,1000,0.1,or_greater")]
	public float InfluenceRadius
	{
		get => _influenceRadius;
		set
		{
			if (!float.IsFinite(value) || value <= 0.0f)
				return;
			if (_influenceRadius == value) return;
			_influenceRadius = value;
			_registry?.ParticipantChanged();
		}
	}

	[Export]
	public int Priority
	{
		get => _priority;
		set
		{
			if (_priority == value) return;
			_priority = value;
			_registry?.ParticipantChanged();
		}
	}

	public Node3D SourceRoot =>
		GodotObject.IsInstanceValid(_sourceRoot) && _sourceRoot.IsInsideTree()
			? _sourceRoot : null;

	public Node3D CenterAnchor =>
		GodotObject.IsInstanceValid(_centerAnchor) && _centerAnchor.IsInsideTree()
			? _centerAnchor : null;

	public override void _Ready()
	{
		_registry = RegistryPath != null && !RegistryPath.IsEmpty
			? GetNodeOrNull<OceanLocalReflectionRegistry>(RegistryPath) : null;
		_sourceRoot = SourceRootPath != null && !SourceRootPath.IsEmpty
			? GetNodeOrNull<Node3D>(SourceRootPath) : null;
		_centerAnchor = CenterAnchorPath != null && !CenterAnchorPath.IsEmpty
			? GetNodeOrNull<Node3D>(CenterAnchorPath) : null;
		if (_sourceRoot != null)
			_sourceRoot.TreeExiting += OnSourceRootExiting;
		if (_centerAnchor != null && _centerAnchor != _sourceRoot)
			_centerAnchor.TreeExiting += OnCenterAnchorExiting;
		if (_registry == null)
			GD.PushWarning($"Local reflection registry was not found for {GetPath()}.");
		else
		{
			_registry.TreeExiting += OnRegistryExiting;
			_registry.Register(this);
		}
	}

	public bool TryGetCenterWorld(out Vector3 center)
	{
		Node3D source = SourceRoot;
		if (source == null)
		{
			center = default;
			return false;
		}
		center = (CenterAnchor ?? source).GetGlobalTransformInterpolated().Origin;
		return true;
	}

	private void OnSourceRootExiting()
	{
		if (_centerAnchor == _sourceRoot)
			_centerAnchor = null;
		_sourceRoot = null;
		_registry?.ParticipantChanged();
	}

	private void OnCenterAnchorExiting()
	{
		_centerAnchor = null;
		_registry?.ParticipantChanged();
	}

	private void OnRegistryExiting()
	{
		_registry = null;
	}

	public override void _ExitTree()
	{
		if (GodotObject.IsInstanceValid(_registry))
		{
			_registry.Unregister(this);
			_registry.TreeExiting -= OnRegistryExiting;
		}
		if (GodotObject.IsInstanceValid(_sourceRoot))
			_sourceRoot.TreeExiting -= OnSourceRootExiting;
		if (GodotObject.IsInstanceValid(_centerAnchor) &&
			_centerAnchor != _sourceRoot)
			_centerAnchor.TreeExiting -= OnCenterAnchorExiting;
		_registry = null;
		_sourceRoot = null;
		_centerAnchor = null;
	}
}
