using Godot;
using OceanFrontier.Water.Runtime;

namespace OceanFrontier.Water.Waves.AnimatedWaves;

/// <summary>
/// Scene-usable rectangular constant-displacement Animated Waves input.
/// Transform translation and yaw define its world-space placement.
/// </summary>
[GlobalClass]
public partial class AnimatedWaveRectInput :
	Node3D,
	IAnimatedWaveInputSnapshotSource
{
	[Export]
	public bool Enabled { get; set; } = true;

	[Export]
	public int Priority { get; set; }

	[Export]
	public AnimatedWaveInputPlacement Placement { get; set; } =
		AnimatedWaveInputPlacement.AllLodsPostCombine;

	[Export]
	public AnimatedWaveInputBlendMode BlendMode { get; set; } =
		AnimatedWaveInputBlendMode.Additive;

	[Export(PropertyHint.Range, "0,1,0.01")]
	public float Weight { get; set; } = 1.0f;

	[Export(PropertyHint.Range, "0,10,0.01,or_greater")]
	public float SourceAmplitude { get; set; } = 1.0f;

	[Export(PropertyHint.Range, "0.001,8192,0.001,or_greater")]
	public float WavelengthMeters { get; set; } = 1.0f;

	[Export]
	public Vector2 SizeXZ { get; set; } = new(20.0f, 20.0f);

	[Export(PropertyHint.Range, "0.001,0.5,0.001")]
	public float FeatherWidth { get; set; } = 0.1f;

	[Export]
	public Vector3 Displacement { get; set; } = new(0.0f, 0.5f, 0.0f);


	private OceanRuntime _runtime;


	string IAnimatedWaveInputSnapshotSource.DiagnosticName =>
		Name;


	public override void _EnterTree()
	{
		_runtime =
			FindRuntime();


		_runtime?.RegisterAnimatedWaveInput(
			this);
	}


	public override void _ExitTree()
	{
		_runtime?.UnregisterAnimatedWaveInput(
			this);


		_runtime =
			null;
	}


	private OceanRuntime FindRuntime()
	{
		Node current =
			GetParent();


		while (current != null)
		{
			if (current is OceanRuntime runtime)
			{
				return runtime;
			}


			current =
				current.GetParent();
		}


		return null;
	}


	bool IAnimatedWaveInputSnapshotSource.TryCapture(
		long registrationOrder,
		out AnimatedWaveInputSnapshot snapshot)
	{
		snapshot =
			default;


		if (!Enabled ||
			!IsInsideTree())
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


		Vector2 size =
			new(
				sizeX,
				sizeZ);


		Transform3D transform =
			GlobalTransform;


		Vector2 axisX =
			new(
				transform.Basis.X.X,
				transform.Basis.X.Z);


		if (!float.IsFinite(axisX.X) ||
			!float.IsFinite(axisX.Y) ||
			axisX.LengthSquared() <
			0.000001f)
		{
			axisX =
				Vector2.Right;
		}
		else
		{
			axisX =
				axisX.Normalized();
		}


		Vector2 axisZ =
			new(
				-axisX.Y,
				axisX.X);


		Vector3 position =
			transform.Origin;


		if (!position.IsFinite())
		{
			return false;
		}


		AnimatedWaveInputPlacement placement =
			Placement is
				AnimatedWaveInputPlacement.WavelengthFilteredPreCombine or
				AnimatedWaveInputPlacement.AllLodsPreCombine or
				AnimatedWaveInputPlacement.AllLodsPostCombine
					? Placement
					: AnimatedWaveInputPlacement.AllLodsPostCombine;


		AnimatedWaveInputBlendMode blendMode =
			BlendMode is
				AnimatedWaveInputBlendMode.Additive or
				AnimatedWaveInputBlendMode.Blend
					? BlendMode
					: AnimatedWaveInputBlendMode.Additive;


		float weight =
			float.IsFinite(Weight)
				? Mathf.Clamp(Weight, 0.0f, 1.0f)
				: 0.0f;


		float sourceAmplitude =
			float.IsFinite(SourceAmplitude)
				? Mathf.Max(SourceAmplitude, 0.0f)
				: 0.0f;


		float wavelength =
			float.IsFinite(WavelengthMeters)
				? Mathf.Max(WavelengthMeters, 0.001f)
				: 0.001f;


		float featherWidth =
			float.IsFinite(FeatherWidth)
				? Mathf.Clamp(FeatherWidth, 0.001f, 0.5f)
				: 0.1f;


		snapshot =
			new AnimatedWaveInputSnapshot(
				Priority,
				registrationOrder,
				placement,
				blendMode,
				weight,
				sourceAmplitude,
				wavelength,
				new Vector2(position.X, position.Z),
				axisX,
				axisZ,
				size,
				featherWidth,
				Displacement.IsFinite()
					? Displacement
					: Vector3.Zero);


		return true;
	}
}
