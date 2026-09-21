using System;
using Godot;
using OceanFrontier.Water.Queries;
using OceanFrontier.Water.Runtime;

namespace OceanFrontier.Water.Physics;

/// <summary>
/// Distributed vertical spring buoyancy driven by asynchronous AnimatedWaveField queries.
/// Attach as a child of a RigidBody3D.
/// </summary>
[GlobalClass]
public partial class OceanBuoyancy : Node
{
	public enum BuoyancyMode
	{
		HeaveOnly,
		HeavePitch,
		HeaveRoll,
		HeavePitchRoll,
	}

	private const int QueryCapacity = 12;
	private const float QueryScaleDivisor = 4.0f;

	[Export]
	public BuoyancyMode Mode { get; set; } = BuoyancyMode.HeavePitchRoll;

	[Export(PropertyHint.Range, "0.01,100,0.01,or_greater")]
	public float MinSpatialLength { get; set; } = 3.5f;

	[Export(PropertyHint.Range, "0.01,10,0.01,or_greater")]
	public float TargetSubmersion { get; set; } = 0.6f;

	[Export(PropertyHint.Range, "0,2,0.01")]
	public float DampingRatio { get; set; } = 0.9f;

	[Export(PropertyHint.Range, "1,10,0.1,or_greater")]
	public float MaximumBuoyancyFactor { get; set; } = 3.0f;

	[Export(PropertyHint.Range, "-10,10,0.01")]
	public float ProbePlaneY { get; set; } = -0.65f;

	[Export(PropertyHint.Range, "0,10,0.01,or_greater")]
	public float ForwardDrag { get; set; } = 0.25f;

	[Export(PropertyHint.Range, "0,10,0.01,or_greater")]
	public float LateralDrag { get; set; } = 0.8f;

	[Export]
	public float SeaLevel { get; set; }

	[Export]
	public NodePath OceanRuntimePath { get; set; }

	[Export]
	public bool ShowProbeVisualization { get; set; }

	[Export]
	public bool ShowForceLines { get; set; } = true;

	[Export(PropertyHint.Range, "0.1,10,0.1,or_greater")]
	public float ForceLineScale { get; set; } = 2.0f;

	[Export]
	public Vector3[] ProbeLocalPositions { get; set; } =
	{
		new(-1.30f, 0.0f, 5.20f),
		new(0.00f, 0.0f, 5.20f),
		new(1.30f, 0.0f, 5.20f),
		new(-1.30f, 0.0f, 1.73f),
		new(0.00f, 0.0f, 1.73f),
		new(1.30f, 0.0f, 1.73f),
		new(-1.30f, 0.0f, -1.73f),
		new(0.00f, 0.0f, -1.73f),
		new(1.30f, 0.0f, -1.73f),
		new(-1.30f, 0.0f, -5.20f),
		new(0.00f, 0.0f, -5.20f),
		new(1.30f, 0.0f, -5.20f),
	};

	[Export]
	public float[] ProbeWeights { get; set; } =
	{
		1.0f, 1.0f, 1.0f,
		1.0f, 1.0f, 1.0f,
		1.0f, 1.0f, 1.0f,
		1.0f, 1.0f, 1.0f,
	};

	private readonly Vector2[] _queryPositions = new Vector2[QueryCapacity];
	private readonly Vector4[] _readbackResults = new Vector4[QueryCapacity];
	private readonly Vector4[] _latestResults = new Vector4[QueryCapacity];
	private readonly Vector3[] _probeWorldPositions = new Vector3[QueryCapacity];
	private readonly float[] _probeForces = new float[QueryCapacity];
	private readonly bool[] _probeSubmerged = new bool[QueryCapacity];
	private readonly MeshInstance3D[] _probeMarkers = new MeshInstance3D[QueryCapacity];
	private readonly MeshInstance3D[] _waterMarkers = new MeshInstance3D[QueryCapacity];
	private readonly MeshInstance3D[] _forceLines = new MeshInstance3D[QueryCapacity];

	private RigidBody3D _body;
	private OceanRuntime _runtime;
	private OceanPointQueryService.OwnerHandle _queryOwner;
	private StandardMaterial3D _dryMaterial;
	private StandardMaterial3D _submergedMaterial;
	private StandardMaterial3D _invalidMaterial;
	private StandardMaterial3D _waterMaterial;
	private StandardMaterial3D _forceMaterial;
	private int _probeCount;
	private float _totalWeight;
	private float _gravity;
	private long _latestGeneration;
	private bool _hasLatestResult;

	public long LatestCompletedGeneration => _latestGeneration;
	public bool HasCompletedResult => _hasLatestResult;

	public override void _Ready()
	{
		_body = GetParent() as RigidBody3D;
		_runtime = ResolveRuntime();
		if (_body == null || _runtime == null || !ValidateConfiguration())
		{
			GD.PushError(
				"OceanBuoyancy requires a RigidBody3D parent, an OceanRuntime, " +
				"and 1-12 valid probes.");
			SetPhysicsProcess(false);
			return;
		}

		_queryOwner = _runtime.PointQueries.RegisterOwner(QueryCapacity);
		_gravity = ResolvePhysicsGravity();
		if (ShowProbeVisualization)
		{
			CreateVisualization();
		}
	}

	public override void _ExitTree()
	{
		if (_queryOwner != null)
		{
			_runtime?.PointQueries.UnregisterOwner(_queryOwner);
			_queryOwner = null;
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_queryOwner == null)
		{
			return;
		}

		ConsumeLatestResult();
		BuildCurrentProbePositions();
		ApplyBuoyancy();
		ApplyHorizontalDrag();
		UpdateVisualization();

		_runtime.PointQueries.SubmitBatch(
			_queryOwner,
			_queryPositions.AsSpan(0, _probeCount),
			MinSpatialLength / QueryScaleDivisor);
	}

	private OceanRuntime ResolveRuntime()
	{
		if (OceanRuntimePath != null && !OceanRuntimePath.IsEmpty)
		{
			return GetNodeOrNull<OceanRuntime>(OceanRuntimePath);
		}

		Node node = GetParent();
		while (node != null)
		{
			if (node is OceanRuntime runtime)
			{
				return runtime;
			}

			node = node.GetParent();
		}

		return null;
	}

	private bool ValidateConfiguration()
	{
		_probeCount = ProbeLocalPositions?.Length ?? 0;
		if (_probeCount is < 1 or > QueryCapacity ||
			ProbeWeights == null ||
			ProbeWeights.Length != _probeCount ||
			!float.IsFinite(MinSpatialLength) || MinSpatialLength <= 0.0f ||
			!float.IsFinite(TargetSubmersion) || TargetSubmersion <= 0.0f ||
			!float.IsFinite(DampingRatio) || DampingRatio < 0.0f ||
			!float.IsFinite(MaximumBuoyancyFactor) || MaximumBuoyancyFactor < 1.0f ||
			!float.IsFinite(ProbePlaneY) ||
			!float.IsFinite(ForwardDrag) || ForwardDrag < 0.0f ||
			!float.IsFinite(LateralDrag) || LateralDrag < 0.0f ||
			!float.IsFinite(SeaLevel))
		{
			return false;
		}

		_totalWeight = 0.0f;
		for (int i = 0; i < _probeCount; i++)
		{
			Vector3 probe = ProbeLocalPositions[i];
			float weight = ProbeWeights[i];
			if (!float.IsFinite(probe.X) ||
				!float.IsFinite(probe.Y) ||
				!float.IsFinite(probe.Z) ||
				!float.IsFinite(weight) ||
				weight <= 0.0f)
			{
				return false;
			}

			_totalWeight += weight;
		}

		return _totalWeight > 0.0f;
	}

	private void ConsumeLatestResult()
	{
		if (!_runtime.PointQueries.TryCopyLatest(
				_queryOwner,
				_readbackResults,
				out int count,
				out long generation,
				out _,
				out _) ||
			generation <= _latestGeneration ||
			count != _probeCount)
		{
			return;
		}

		_readbackResults.AsSpan(0, count).CopyTo(_latestResults);
		_latestGeneration = generation;
		_hasLatestResult = true;
	}

	private void BuildCurrentProbePositions()
	{
		Transform3D transform = _body.GlobalTransform;
		for (int i = 0; i < _probeCount; i++)
		{
			Vector3 localProbe = ProbeLocalPositions[i] + Vector3.Up * ProbePlaneY;
			Vector3 worldProbe = transform * localProbe;
			_probeWorldPositions[i] = worldProbe;
			_queryPositions[i] = new Vector2(worldProbe.X, worldProbe.Z);
		}
	}

	private void ApplyBuoyancy()
	{
		Array.Clear(_probeForces, 0, _probeCount);
		Array.Clear(_probeSubmerged, 0, _probeCount);
		if (!_hasLatestResult)
		{
			return;
		}

		float centralForce = 0.0f;
		Vector3 worldCenterOfMass = GetWorldCenterOfMass();

		for (int i = 0; i < _probeCount; i++)
		{
			Vector4 result = _latestResults[i];
			if (result.W <= 0.5f || !float.IsFinite(result.Y))
			{
				continue;
			}

			Vector3 probeWorld = _probeWorldPositions[i];
			float waterHeight = SeaLevel + result.Y;
			float submersion = waterHeight - probeWorld.Y;
			if (submersion <= 0.0f)
			{
				continue;
			}

			float probeMass = _body.Mass * ProbeWeights[i] / _totalWeight;
			float springCoefficient = probeMass * _gravity / TargetSubmersion;
			float dampingCoefficient =
				2.0f * DampingRatio * Mathf.Sqrt(springCoefficient * probeMass);
			Vector3 radius = probeWorld - worldCenterOfMass;
			Vector3 probeVelocity = _body.LinearVelocity + _body.AngularVelocity.Cross(radius);
			float springForce = springCoefficient * submersion;
			float dampingForce = -dampingCoefficient * probeVelocity.Y;
			float maximumForce = probeMass * _gravity * MaximumBuoyancyFactor;
			float forceMagnitude = Mathf.Clamp(
				springForce + dampingForce,
				0.0f,
				maximumForce);

			_probeForces[i] = forceMagnitude;
			_probeSubmerged[i] = true;
			if (Mode == BuoyancyMode.HeaveOnly)
			{
				centralForce += forceMagnitude;
				continue;
			}

			Vector3 offset = probeWorld - _body.GlobalPosition;
			if (Mode == BuoyancyMode.HeavePitch)
			{
				offset.X = 0.0f;
			}
			else if (Mode == BuoyancyMode.HeaveRoll)
			{
				offset.Z = 0.0f;
			}

			_body.ApplyForce(Vector3.Up * forceMagnitude, offset);
		}

		if (Mode == BuoyancyMode.HeaveOnly && centralForce > 0.0f)
		{
			_body.ApplyCentralForce(Vector3.Up * centralForce);
		}
	}

	private void ApplyHorizontalDrag()
	{
		Vector3 velocity = _body.LinearVelocity;
		velocity.Y = 0.0f;
		Vector3 right = _body.GlobalBasis.X;
		Vector3 forward = _body.GlobalBasis.Z;
		right.Y = 0.0f;
		forward.Y = 0.0f;
		if (right.LengthSquared() < 0.000001f || forward.LengthSquared() < 0.000001f)
		{
			return;
		}

		right = right.Normalized();
		forward = forward.Normalized();
		float lateralSpeed = velocity.Dot(right);
		float forwardSpeed = velocity.Dot(forward);
		Vector3 dragForce = -_body.Mass *
			(LateralDrag * lateralSpeed * right +
			 ForwardDrag * forwardSpeed * forward);

		if (dragForce.IsFinite())
		{
			_body.ApplyCentralForce(dragForce);
		}
	}

	private static float ResolvePhysicsGravity()
	{
		Variant setting = ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8f);
		float gravity = setting.AsSingle();
		return float.IsFinite(gravity) && gravity > 0.0f ? gravity : 9.8f;
	}

	private Vector3 GetWorldCenterOfMass()
	{
		if (_body.CenterOfMassMode == RigidBody3D.CenterOfMassModeEnum.Custom)
		{
			return _body.GlobalTransform * _body.CenterOfMass;
		}

		return _body.GlobalPosition;
	}

	private void CreateVisualization()
	{
		var probeMesh = new SphereMesh { Radius = 0.11f, Height = 0.22f };
		var waterMesh = new SphereMesh { Radius = 0.07f, Height = 0.14f };
		var lineMesh = new CylinderMesh
		{
			TopRadius = 0.025f,
			BottomRadius = 0.025f,
			Height = 1.0f,
		};

		_dryMaterial = DebugMaterial(new Color(0.75f, 0.75f, 0.75f));
		_submergedMaterial = DebugMaterial(new Color(0.05f, 0.85f, 1.0f));
		_invalidMaterial = DebugMaterial(new Color(1.0f, 0.08f, 0.12f));
		_waterMaterial = DebugMaterial(new Color(1.0f, 0.78f, 0.05f));
		_forceMaterial = DebugMaterial(new Color(0.15f, 1.0f, 0.2f));

		for (int i = 0; i < _probeCount; i++)
		{
			_probeMarkers[i] = DebugMarker(probeMesh, _dryMaterial);
			_waterMarkers[i] = DebugMarker(waterMesh, _waterMaterial);
			_forceLines[i] = DebugMarker(lineMesh, _forceMaterial);
		}
	}

	private static StandardMaterial3D DebugMaterial(Color color) => new()
	{
		ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		AlbedoColor = color,
		NoDepthTest = true,
	};

	private MeshInstance3D DebugMarker(Mesh mesh, Material material)
	{
		var marker = new MeshInstance3D
		{
			Mesh = mesh,
			MaterialOverride = material,
			TopLevel = true,
			Visible = false,
		};
		AddChild(marker);
		return marker;
	}

	private void UpdateVisualization()
	{
		if (!ShowProbeVisualization || _probeMarkers[0] == null)
		{
			return;
		}

		float bodyWeight = Mathf.Max(_body.Mass * _gravity, 0.001f);
		for (int i = 0; i < _probeCount; i++)
		{
			Vector3 probeWorld = _probeWorldPositions[i];
			Vector4 result = _latestResults[i];
			bool valid = _hasLatestResult && result.W > 0.5f && float.IsFinite(result.Y);
			MeshInstance3D probe = _probeMarkers[i];
			MeshInstance3D water = _waterMarkers[i];
			MeshInstance3D line = _forceLines[i];

			probe.GlobalPosition = probeWorld;
			probe.MaterialOverride = valid
				? (_probeSubmerged[i] ? _submergedMaterial : _dryMaterial)
				: _invalidMaterial;
			probe.Visible = true;
			water.Visible = valid;
			line.Visible = false;

			if (!valid)
			{
				continue;
			}

			water.GlobalPosition = new Vector3(
				probeWorld.X,
				SeaLevel + result.Y,
				probeWorld.Z);

			float forceLength = Mathf.Clamp(
				_probeForces[i] / bodyWeight * ForceLineScale,
				0.0f,
				ForceLineScale);
			if (!ShowForceLines || forceLength <= 0.01f)
			{
				continue;
			}

			line.GlobalPosition = probeWorld + Vector3.Up * (forceLength * 0.5f);
			line.Scale = new Vector3(1.0f, forceLength, 1.0f);
			line.Visible = true;
		}
	}
}
