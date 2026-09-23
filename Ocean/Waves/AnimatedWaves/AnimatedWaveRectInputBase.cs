using Godot;
using OceanFrontier.Water.Runtime;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// Shared world-space rectangle, registration and main-thread snapshot logic
/// for batched Animated Waves inputs.
/// </summary>
public abstract partial class AnimatedWaveRectInputBase :
	Node3D,
	IAnimatedWaveInputSnapshotSource
{
	[Export]
	public bool Enabled { get; set; } = true;

	[Export]
	public int Priority { get; set; }

	[Export]
	public Vector2 SizeXZ { get; set; } = new(20.0f, 20.0f);

	[Export(PropertyHint.Range, "0.001,0.5,0.001")]
	public float FeatherWidth { get; set; } = 0.1f;


	private OceanRuntime _runtime;


	string IAnimatedWaveInputSnapshotSource.DiagnosticName =>
		Name;


	public override void _EnterTree()
	{
		_runtime = FindRuntime();
		_runtime?.RegisterAnimatedWaveInput(this);
	}


	public override void _ExitTree()
	{
		_runtime?.UnregisterAnimatedWaveInput(this);
		_runtime = null;
	}


	private OceanRuntime FindRuntime()
	{
		Node current = GetParent();


		while (current != null)
		{
			if (current is OceanRuntime runtime)
			{
				return runtime;
			}


			current = current.GetParent();
		}


		return null;
	}


	bool IAnimatedWaveInputSnapshotSource.TryCapture(
		long registrationOrder,
		out AnimatedWaveInputSnapshot snapshot)
	{
		snapshot = default;


		if (!Enabled || !IsInsideTree())
		{
			return false;
		}


		float sizeX =
			float.IsFinite(SizeXZ.X)
				? Mathf.Max(Mathf.Abs(SizeXZ.X), 0.001f)
				: 0.001f;


		float sizeZ =
			float.IsFinite(SizeXZ.Y)
				? Mathf.Max(Mathf.Abs(SizeXZ.Y), 0.001f)
				: 0.001f;


		Transform3D transform = GlobalTransform;
		Vector2 axisX = new(transform.Basis.X.X, transform.Basis.X.Z);


		if (!float.IsFinite(axisX.X) ||
			!float.IsFinite(axisX.Y) ||
			axisX.LengthSquared() < 0.000001f)
		{
			axisX = Vector2.Right;
		}
		else
		{
			axisX = axisX.Normalized();
		}


		Vector3 position = transform.Origin;


		if (!position.IsFinite())
		{
			return false;
		}


		float featherWidth =
			float.IsFinite(FeatherWidth)
				? Mathf.Clamp(FeatherWidth, 0.001f, 0.5f)
				: 0.1f;


		Vector2 axisZ = new(-axisX.Y, axisX.X);


		snapshot = CreateSnapshot(
			registrationOrder,
			new Vector2(position.X, position.Z),
			axisX,
			axisZ,
			new Vector2(sizeX, sizeZ),
			featherWidth);


		return true;
	}


	internal abstract AnimatedWaveInputSnapshot CreateSnapshot(
		long registrationOrder,
		Vector2 centerXZ,
		Vector2 axisX,
		Vector2 axisZ,
		Vector2 sizeXZ,
		float featherWidth);
}
