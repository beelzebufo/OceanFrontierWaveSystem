using System;
using Godot;
using OceanFrontier.Water.Rendering;
using OceanFrontier.Water.Runtime;
using OceanFrontier.Water.Waves.AnimatedWaves;

namespace OceanFrontier.Water.Debug;

internal sealed class OceanDiagnosticLightingPanel
{
	private OceanRuntime _runtime;

	private AnimatedWaveSurfaceRenderer _surface;

	private DirectionalLight3D _sun;

	private OceanDiagnosticLightDirectionGizmo _lightGizmo;

	private Func<int> _getSelectedLod;


	internal void Initialize(
		VBoxContainer parent,
		OceanRuntime runtime,
		AnimatedWaveSurfaceRenderer surface,
		DirectionalLight3D sun,
		OceanDiagnosticLightDirectionGizmo lightGizmo,
		Func<int> getSelectedLod)
	{
		_runtime =
			runtime;

		_surface =
			surface;

		_sun =
			sun;

		_lightGizmo =
			lightGizmo;

		_getSelectedLod =
			getSelectedLod;


		Build(
			parent);
	}


	private void Build(
		VBoxContainer parent)
	{
		OceanDiagnosticUi.Header(
			parent,
			"Lighting");


		var sunProgress =
			OceanDiagnosticUi.Spin(
				parent,
				"Sun position",
				0.35,
				0.0,
				1.0,
				0.01);


		sunProgress.ValueChanged +=
			value =>
				SetSunProgress(
					(float)value);


		SetSunProgress(
			(float)sunProgress.Value);


		var sunScatterStrength =
			OceanDiagnosticUi.Spin(
				parent,
				"Sun scatter strength",
				_surface?.WaterSunScatterStrength ??
					AnimatedWaveSurfaceRenderer
						.DefaultWaterSunScatterStrength,
				0.0,
				4.0,
				0.01);


		sunScatterStrength.ValueChanged +=
			value =>
				_surface?.SetWaterSunScatterStrength(
					(float)value);


		_surface?.SetWaterSunScatterStrength(
			(float)sunScatterStrength.Value);


		var shallowColorStrength =
			OceanDiagnosticUi.Spin(
				parent,
				"Shallow colour strength",
				_surface?.WaterShallowColorStrength ??
					AnimatedWaveSurfaceRenderer
						.DefaultWaterShallowColorStrength,
				0.0,
				1.0,
				0.01);


		shallowColorStrength.ValueChanged +=
			value =>
				_surface?.SetWaterShallowColorStrength(
					(float)value);


		_surface?.SetWaterShallowColorStrength(
			(float)shallowColorStrength.Value);


		OceanDiagnosticUi.Check(
			parent,
			"Surface lighting",
			true)
			.Toggled +=
				value =>
					_surface?.SetLightingEnabled(
						value);


		OceanDiagnosticUi.Spin(
			parent,
			"Roughness",
			0.65,
			0.0,
			1.0,
			0.01)
			.ValueChanged +=
				value =>
					_surface?.SetDiagnosticRoughness(
						(float)value);


		OceanDiagnosticUi.Check(
			parent,
			"Show light direction",
			false)
			.Toggled +=
				value =>
				{
					if (_lightGizmo != null)
					{
						_lightGizmo.Visible =
							value;
					}
				};


		VBoxContainer normals =
			OceanDiagnosticUi.Foldout(
				parent,
				"Normals",
				expanded: true);


		OceanDiagnosticUi.Check(
			normals,
			"Show normal vectors",
			false)
			.Toggled +=
				value =>
					_surface?.SetNormalVectorsVisible(
						value);


		var normalMethod =
			OceanDiagnosticUi.Option(
				normals,
				"Normal method",
				"Blended XYZ Forward",
				"Crest Per-LOD Forward",
				"Derivative Field");


		normalMethod.Selected =
			_surface?.NormalMethod ??
			0;


		normalMethod.ItemSelected +=
			index =>
				_surface?.SetNormalMethod(
					(int)index);


		OceanDiagnosticUi.Info(
			normals,
			"These controls affect rendering diagnostics only. The canonical AnimatedWaveField used by physics is unchanged.");
	}


	internal void Tick(
		double delta)
	{
		if (_lightGizmo == null ||
			!_lightGizmo.Visible ||
			_sun == null ||
			_runtime == null)
		{
			return;
		}


		int lod =
			_getSelectedLod?.Invoke() ??
			0;


		if (_runtime.TryGetAnimatedWaveSurface(
				lod,
				out _,
				out _,
				out _,
				out AnimatedWaveLodSlice slice))
		{
			_lightGizmo.UpdateFrom(
				_sun,
				slice);
		}
	}


	private void SetSunProgress(
		float progress)
	{
		if (_sun == null)
		{
			return;
		}


		float azimuth =
			Mathf.Lerp(
				-Mathf.Pi *
					0.5f,
				Mathf.Pi *
					0.5f,
				progress);


		float elevation =
			Mathf.DegToRad(
				8.0f +
				57.0f *
				Mathf.Sin(
					Mathf.Pi *
						progress));


		Vector3 direction =
			new(
				Mathf.Cos(
					elevation) *
					Mathf.Cos(
						azimuth),

				-Mathf.Sin(
					elevation),

				Mathf.Cos(
					elevation) *
					Mathf.Sin(
						azimuth));


		_sun.LookAt(
			_sun.GlobalPosition +
				direction,
			Vector3.Up);


		// DirectionalLight3D emits along global local -Z. LightColor is stored
		// as nonlinear sRGB, while the numerical shader uniform is linear RGB.
		Vector3 rayDirection =
			-_sun.GlobalTransform.Basis.Z.Normalized();


		Color linearColor =
			_sun.LightColor.SrgbToLinear();


		float energy =
			Mathf.Max(
				_sun.LightEnergy,
				0.0f);


		_surface?.SetPrimarySunState(
			rayDirection,
			new Vector3(
				linearColor.R,
				linearColor.G,
				linearColor.B) *
			energy);
	}
}
