using Godot;
using OceanFrontier.Water.Runtime;

namespace OceanFrontier.Water.Waves.Foam;

[GlobalClass]
public partial class OceanFoamRectInput : Node3D, IOceanFoamInputSnapshotSource
{
	[Export]
	public bool Enabled { get; set; } = true;

	[Export]
	public int Priority { get; set; }

	[Export]
	public OceanFoamInputMode Mode { get; set; } =
		OceanFoamInputMode.AdditiveRate;

	[Export(PropertyHint.Range, "0,1,0.01")]
	public float Weight { get; set; } = 1.0f;

	[Export]
	public Vector2 SizeXZ { get; set; } =
		new(4.0f, 4.0f);

	[Export(PropertyHint.Range, "0,100,0.01,or_greater")]
	public float FeatherWidth { get; set; } = 0.5f;

	[Export(PropertyHint.Range, "0,20,0.01,or_greater")]
	public float AdditiveRate { get; set; } = 1.0f;

	[Export(PropertyHint.Range, "0,1,0.01")]
	public float OverrideValue { get; set; } = 1.0f;

	private OceanRuntime _runtime;

	string IOceanFoamInputSnapshotSource.DiagnosticName =>
		Name;

	public override void _EnterTree()
	{
		_runtime =
			FindRuntime();
		_runtime?.RegisterFoamInput(
			this);
	}

	public override void _ExitTree()
	{
		_runtime?.UnregisterFoamInput(
			this);
		_runtime =
			null;
	}

	private OceanRuntime FindRuntime()
	{
		for (Node current = GetParent();
			 current != null;
			 current = current.GetParent())
		{
			if (current is OceanRuntime runtime)
			{
				return runtime;
			}
		}


		return null;
	}

	bool IOceanFoamInputSnapshotSource.TryCapture(
		long registrationOrder,
		out OceanFoamInputSnapshot snapshot)
	{
		snapshot =
			default;


		if (!Enabled ||
			!IsInsideTree())
		{
			return false;
		}


		Transform3D transform =
			GlobalTransform;
		Vector3 position =
			transform.Origin;


		if (!position.IsFinite())
		{
			return false;
		}


		Vector2 axisX =
			new(
				transform.Basis.X.X,
				transform.Basis.X.Z);


		axisX =
			axisX.IsFinite() &&
			axisX.LengthSquared() >= 0.000001f
				? axisX.Normalized()
				: Vector2.Right;


		Vector2 axisZ =
			new(
				-axisX.Y,
				axisX.X);


		Vector2 size =
			new(
				float.IsFinite(SizeXZ.X)
					? Mathf.Max(Mathf.Abs(SizeXZ.X), 0.001f)
					: 0.001f,
				float.IsFinite(SizeXZ.Y)
					? Mathf.Max(Mathf.Abs(SizeXZ.Y), 0.001f)
					: 0.001f);


		float feather =
			float.IsFinite(FeatherWidth)
				? Mathf.Max(FeatherWidth, 0.0f)
				: 0.0f;

		float weight =
			Mathf.Clamp(
				float.IsFinite(Weight)
					? Weight
					: 1.0f,
				0.0f,
				1.0f);

		float additiveRate =
			float.IsFinite(AdditiveRate)
				? Mathf.Max(AdditiveRate, 0.0f)
				: 0.0f;

		float overrideValue =
			Mathf.Clamp(
				float.IsFinite(OverrideValue)
					? OverrideValue
					: 1.0f,
				0.0f,
				1.0f);


		snapshot =
			new OceanFoamInputSnapshot(
				Priority,
				registrationOrder,
				Mode == OceanFoamInputMode.Override
					? OceanFoamInputMode.Override
					: OceanFoamInputMode.AdditiveRate,
				new Vector2(
					position.X,
					position.Z),
				axisX,
				axisZ,
				size,
				feather,
				weight,
				additiveRate,
				overrideValue);


		return true;
	}
}
