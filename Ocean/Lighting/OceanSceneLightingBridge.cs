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
/// OceanAmbientLightProxy is a bounded-source approximation of Godot's
/// low-frequency environment ambient; it is not sky radiance or SH data.
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
			backgroundEnergyMultiplier: 0.0f,
			backgroundColorLinear: Vector3.Zero,
			hasSky: false);

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
	internal readonly Vector3 OceanAmbientLightProxy;


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
		float backgroundEnergyMultiplier,
		Vector3 oceanAmbientLightProxy)
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
		OceanAmbientLightProxy = oceanAmbientLightProxy;
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
		float backgroundEnergyMultiplier,
		Vector3 backgroundColorLinear,
		bool hasSky)
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


		ambientColorLinear =
			SanitizeVector(
				ambientColorLinear);

		ambientEnergy =
			SanitizeScalar(
				ambientEnergy);

		ambientSkyContribution =
			Mathf.Clamp(
				SanitizeScalar(
					ambientSkyContribution),
				0.0f,
				1.0f);

		backgroundEnergyMultiplier =
			SanitizeScalar(
				backgroundEnergyMultiplier);


		Vector3 oceanAmbientLightProxy =
			DeriveOceanAmbientLightProxy(
				ambientSource,
				ambientColorLinear,
				ambientEnergy,
				ambientSkyContribution,
				backgroundMode,
				backgroundEnergyMultiplier,
				SanitizeVector(
					backgroundColorLinear),
				hasSky);


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
				ambientColorLinear,
				ambientEnergy,
				ambientSkyContribution,
				reflectionSource,
				backgroundMode,
				backgroundEnergyMultiplier,
				oceanAmbientLightProxy);
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
		BackgroundEnergyMultiplier == other.BackgroundEnergyMultiplier &&
		OceanAmbientLightProxy == other.OceanAmbientLightProxy;


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
				BackgroundEnergyMultiplier,
				OceanAmbientLightProxy));


	private static Vector3 DeriveOceanAmbientLightProxy(
		Godot.Environment.AmbientSource ambientSource,
		Vector3 ambientColorLinear,
		float ambientEnergy,
		float ambientSkyContribution,
		Godot.Environment.BGMode backgroundMode,
		float backgroundEnergyMultiplier,
		Vector3 backgroundColorLinear,
		bool hasSky)
	{
		Vector3 explicitAmbient =
			ambientColorLinear *
			ambientEnergy;

		Vector3 neutralSkyAmbient =
			hasSky
				? Vector3.One * backgroundEnergyMultiplier
				: Vector3.Zero;


		switch (ambientSource)
		{
			case Godot.Environment.AmbientSource.Disabled:
				return Vector3.Zero;

			case Godot.Environment.AmbientSource.Color:
				return explicitAmbient;

			case Godot.Environment.AmbientSource.Sky:
				return explicitAmbient.Lerp(
					neutralSkyAmbient,
					ambientSkyContribution);

			case Godot.Environment.AmbientSource.Bg:
				switch (backgroundMode)
				{
					case Godot.Environment.BGMode.ClearColor:
					case Godot.Environment.BGMode.Color:
						return
							backgroundColorLinear *
							backgroundEnergyMultiplier;

					case Godot.Environment.BGMode.Sky:
						return explicitAmbient.Lerp(
							neutralSkyAmbient,
							ambientSkyContribution);

					default:
						return Vector3.Zero;
				}

			default:
				return Vector3.Zero;
		}
	}


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
		Vector3 backgroundColorLinear = Vector3.Zero;
		bool hasSky = false;


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


			Color backgroundLinear =
				(
					backgroundMode == Godot.Environment.BGMode.ClearColor
						? RenderingServer.GetDefaultClearColor()
						: environment.BackgroundColor
				).SrgbToLinear();

			backgroundColorLinear =
				new Vector3(
					backgroundLinear.R,
					backgroundLinear.G,
					backgroundLinear.B);

			hasSky = environment.Sky != null;
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
				backgroundEnergyMultiplier,
				backgroundColorLinear,
				hasSky));
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
