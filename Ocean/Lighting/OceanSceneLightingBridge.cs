using System;
using Godot;
using OceanFrontier.Water.Runtime;

namespace OceanFrontier.Water.Lighting;

internal enum OceanEnvironmentSource
{
	None,
	Camera,
	World,
	Fallback,
}


/// <summary>
/// Immutable value-only snapshot of the Godot scene lighting observed on the
/// main thread. It deliberately contains no Node, Resource or RID references.
/// Environment fields are raw controls, not an approximation of integrated
/// sky irradiance.
/// </summary>
internal readonly struct OceanLightingState :
	IEquatable<OceanLightingState>
{
	internal static readonly OceanLightingState Default =
		Create(
			hasPrimarySun: false,
			primarySunRayDirectionWorld: Vector3.Down,
			primarySunLinearRadiance: Vector3.Zero,
			physicalLightUnitsEnabled: false,
			primarySunIntensityLux: 0.0f,
			primarySunTemperatureKelvin: 6500.0f,
			environmentSource: OceanEnvironmentSource.None,
			ambientSource: Godot.Environment.AmbientSource.Disabled,
			ambientColorLinear: Vector3.Zero,
			ambientEnergy: 0.0f,
			ambientSkyContribution: 0.0f,
			reflectionSource: Godot.Environment.ReflectionSource.Disabled,
			backgroundMode: Godot.Environment.BGMode.ClearColor,
			backgroundEnergyMultiplier: 0.0f);

	internal readonly bool HasPrimarySun;
	internal readonly Vector3 PrimarySunRayDirectionWorld;
	internal readonly Vector3 PrimarySunLinearRadiance;
	internal readonly bool PhysicalLightUnitsEnabled;
	internal readonly float PrimarySunIntensityLux;
	internal readonly float PrimarySunTemperatureKelvin;

	internal readonly OceanEnvironmentSource EnvironmentSource;
	internal readonly Godot.Environment.AmbientSource AmbientSource;
	internal readonly Vector3 AmbientColorLinear;
	internal readonly float AmbientEnergy;
	internal readonly float AmbientSkyContribution;
	internal readonly Godot.Environment.ReflectionSource ReflectionSource;
	internal readonly Godot.Environment.BGMode BackgroundMode;
	internal readonly float BackgroundEnergyMultiplier;


	private OceanLightingState(
		bool hasPrimarySun,
		Vector3 primarySunRayDirectionWorld,
		Vector3 primarySunLinearRadiance,
		bool physicalLightUnitsEnabled,
		float primarySunIntensityLux,
		float primarySunTemperatureKelvin,
		OceanEnvironmentSource environmentSource,
		Godot.Environment.AmbientSource ambientSource,
		Vector3 ambientColorLinear,
		float ambientEnergy,
		float ambientSkyContribution,
		Godot.Environment.ReflectionSource reflectionSource,
		Godot.Environment.BGMode backgroundMode,
		float backgroundEnergyMultiplier)
	{
		HasPrimarySun = hasPrimarySun;
		PrimarySunRayDirectionWorld = primarySunRayDirectionWorld;
		PrimarySunLinearRadiance = primarySunLinearRadiance;
		PhysicalLightUnitsEnabled = physicalLightUnitsEnabled;
		PrimarySunIntensityLux = primarySunIntensityLux;
		PrimarySunTemperatureKelvin = primarySunTemperatureKelvin;
		EnvironmentSource = environmentSource;
		AmbientSource = ambientSource;
		AmbientColorLinear = ambientColorLinear;
		AmbientEnergy = ambientEnergy;
		AmbientSkyContribution = ambientSkyContribution;
		ReflectionSource = reflectionSource;
		BackgroundMode = backgroundMode;
		BackgroundEnergyMultiplier = backgroundEnergyMultiplier;
	}


	internal static OceanLightingState Create(
		bool hasPrimarySun,
		Vector3 primarySunRayDirectionWorld,
		Vector3 primarySunLinearRadiance,
		bool physicalLightUnitsEnabled,
		float primarySunIntensityLux,
		float primarySunTemperatureKelvin,
		OceanEnvironmentSource environmentSource,
		Godot.Environment.AmbientSource ambientSource,
		Vector3 ambientColorLinear,
		float ambientEnergy,
		float ambientSkyContribution,
		Godot.Environment.ReflectionSource reflectionSource,
		Godot.Environment.BGMode backgroundMode,
		float backgroundEnergyMultiplier)
	{
		if (!primarySunRayDirectionWorld.IsFinite() ||
			primarySunRayDirectionWorld.LengthSquared() < 1e-8f)
		{
			primarySunRayDirectionWorld = Vector3.Down;
		}
		else
		{
			primarySunRayDirectionWorld =
				primarySunRayDirectionWorld.Normalized();
		}


		return
			new OceanLightingState(
				hasPrimarySun,
				primarySunRayDirectionWorld,
				SanitizeVector(primarySunLinearRadiance),
				physicalLightUnitsEnabled,
				SanitizeScalar(primarySunIntensityLux),
				SanitizeScalar(primarySunTemperatureKelvin),
				environmentSource,
				ambientSource,
				SanitizeVector(ambientColorLinear),
				SanitizeScalar(ambientEnergy),
				Mathf.Clamp(
					SanitizeScalar(ambientSkyContribution),
					0.0f,
					1.0f),
				reflectionSource,
				backgroundMode,
				SanitizeScalar(backgroundEnergyMultiplier));
	}


	public bool Equals(
		OceanLightingState other) =>
		HasPrimarySun == other.HasPrimarySun &&
		PrimarySunRayDirectionWorld == other.PrimarySunRayDirectionWorld &&
		PrimarySunLinearRadiance == other.PrimarySunLinearRadiance &&
		PhysicalLightUnitsEnabled == other.PhysicalLightUnitsEnabled &&
		PrimarySunIntensityLux == other.PrimarySunIntensityLux &&
		PrimarySunTemperatureKelvin == other.PrimarySunTemperatureKelvin &&
		EnvironmentSource == other.EnvironmentSource &&
		AmbientSource == other.AmbientSource &&
		AmbientColorLinear == other.AmbientColorLinear &&
		AmbientEnergy == other.AmbientEnergy &&
		AmbientSkyContribution == other.AmbientSkyContribution &&
		ReflectionSource == other.ReflectionSource &&
		BackgroundMode == other.BackgroundMode &&
		BackgroundEnergyMultiplier == other.BackgroundEnergyMultiplier;


	public override bool Equals(
		object obj) =>
		obj is OceanLightingState other &&
		Equals(other);


	public override int GetHashCode() =>
		HashCode.Combine(
			HasPrimarySun,
			PrimarySunRayDirectionWorld,
			PrimarySunLinearRadiance,
			PhysicalLightUnitsEnabled,
			PrimarySunIntensityLux,
			PrimarySunTemperatureKelvin,
			EnvironmentSource,
			HashCode.Combine(
				AmbientSource,
				AmbientColorLinear,
				AmbientEnergy,
				AmbientSkyContribution,
				ReflectionSource,
				BackgroundMode,
				BackgroundEnergyMultiplier));


	private static Vector3 SanitizeVector(
		Vector3 value) =>
		new(
			SanitizeScalar(value.X),
			SanitizeScalar(value.Y),
			SanitizeScalar(value.Z));


	private static float SanitizeScalar(
		float value) =>
		float.IsFinite(value)
			? Mathf.Max(value, 0.0f)
			: 0.0f;
}


/// <summary>
/// Main-thread, read-only bridge from Godot scene lighting to ocean value
/// snapshots. Render-thread consumers never access scene Nodes or Resources.
/// </summary>
public partial class OceanSceneLightingBridge : Node
{
	private const string PhysicalLightUnitsSetting =
		"rendering/lights_and_shadows/use_physical_light_units";

	[Export]
	public NodePath PrimarySunPath { get; set; }

	private OceanRuntime _runtime;
	private DirectionalLight3D _primarySun;
	private bool _physicalLightUnitsEnabled;


	public override void _Ready()
	{
		_runtime = GetParent() as OceanRuntime;


		if (_runtime == null)
		{
			GD.PushError(
				"OceanSceneLightingBridge must be a direct child of OceanRuntime.");

			SetProcess(false);
			return;
		}


		_physicalLightUnitsEnabled =
			(bool)ProjectSettings.GetSetting(
				PhysicalLightUnitsSetting,
				false);


		_primarySun =
			GetNodeOrNull<DirectionalLight3D>(
				PrimarySunPath);


		if (_primarySun == null)
		{
			GD.PushWarning(
				"OceanSceneLightingBridge has no valid primary DirectionalLight3D.");
		}


		// Lighting is scene state, not simulation state. Capture before the
		// default-priority renderer consumers and continue while the tree is
		// paused.
		ProcessPriority = -100;
		ProcessMode = ProcessModeEnum.Always;


		CaptureAndPublish();
	}


	public override void _Process(
		double delta) =>
		CaptureAndPublish();


	private void CaptureAndPublish()
	{
		if (_runtime == null)
		{
			return;
		}


		CapturePrimarySun(
			out bool hasPrimarySun,
			out Vector3 rayDirectionWorld,
			out Vector3 linearRadiance,
			out float intensityLux,
			out float temperatureKelvin);


		Godot.Environment environment =
			ResolveEffectiveEnvironment(
				out OceanEnvironmentSource environmentSource);


		Godot.Environment.AmbientSource ambientSource =
			Godot.Environment.AmbientSource.Disabled;

		Vector3 ambientColorLinear = Vector3.Zero;
		float ambientEnergy = 0.0f;
		float ambientSkyContribution = 0.0f;

		Godot.Environment.ReflectionSource reflectionSource =
			Godot.Environment.ReflectionSource.Disabled;

		Godot.Environment.BGMode backgroundMode =
			Godot.Environment.BGMode.ClearColor;

		float backgroundEnergyMultiplier = 0.0f;


		if (environment != null)
		{
			ambientSource = environment.AmbientLightSource;

			Color ambientLinear =
				environment.AmbientLightColor.SrgbToLinear();

			ambientColorLinear =
				new Vector3(
					ambientLinear.R,
					ambientLinear.G,
					ambientLinear.B);

			ambientEnergy = environment.AmbientLightEnergy;
			ambientSkyContribution = environment.AmbientLightSkyContribution;
			reflectionSource = environment.ReflectedLightSource;
			backgroundMode = environment.BackgroundMode;
			backgroundEnergyMultiplier = environment.BackgroundEnergyMultiplier;
		}


		_runtime.PublishLightingState(
			OceanLightingState.Create(
				hasPrimarySun,
				rayDirectionWorld,
				linearRadiance,
				_physicalLightUnitsEnabled,
				intensityLux,
				temperatureKelvin,
				environmentSource,
				ambientSource,
				ambientColorLinear,
				ambientEnergy,
				ambientSkyContribution,
				reflectionSource,
				backgroundMode,
				backgroundEnergyMultiplier));
	}


	private void CapturePrimarySun(
		out bool hasPrimarySun,
		out Vector3 rayDirectionWorld,
		out Vector3 linearRadiance,
		out float intensityLux,
		out float temperatureKelvin)
	{
		DirectionalLight3D sun = _primarySun;


		hasPrimarySun =
			GodotObject.IsInstanceValid(sun);


		if (!hasPrimarySun)
		{
			rayDirectionWorld = Vector3.Down;
			linearRadiance = Vector3.Zero;
			intensityLux = 0.0f;
			temperatureKelvin = 6500.0f;
			return;
		}


		// DirectionalLight3D emits along global local -Z.
		rayDirectionWorld =
			-sun.GlobalTransform.Basis.Z.Normalized();


		Color linearColor =
			sun.LightColor.SrgbToLinear();


		// Godot applies correlated temperature colour only when physical
		// light units are enabled. The project currently has them disabled,
		// preserving the previous normalized LightEnergy contract exactly.
		if (_physicalLightUnitsEnabled)
		{
			Color correlatedLinear =
				sun.GetCorrelatedColor().SrgbToLinear();

			linearColor *= correlatedLinear;
		}


		float energy =
			sun.IsVisibleInTree()
				? Mathf.Max(sun.LightEnergy, 0.0f)
				: 0.0f;


		linearRadiance =
			new Vector3(
				linearColor.R,
				linearColor.G,
				linearColor.B) *
			energy;

		intensityLux = sun.LightIntensityLux;
		temperatureKelvin = sun.LightTemperature;
	}


	private Godot.Environment ResolveEffectiveEnvironment(
		out OceanEnvironmentSource source)
	{
		Camera3D camera = GetViewport()?.GetCamera3D();


		if (camera?.Environment != null)
		{
			source = OceanEnvironmentSource.Camera;
			return camera.Environment;
		}


		World3D world =
			camera?.GetWorld3D() ??
			GetViewport()?.World3D;


		if (world?.Environment != null)
		{
			source = OceanEnvironmentSource.World;
			return world.Environment;
		}


		if (world?.FallbackEnvironment != null)
		{
			source = OceanEnvironmentSource.Fallback;
			return world.FallbackEnvironment;
		}


		source = OceanEnvironmentSource.None;
		return null;
	}
}
