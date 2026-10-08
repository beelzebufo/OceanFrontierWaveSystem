using Godot;
using OceanFrontier.Water.Rendering.Reflections;

namespace OceanFrontier.Water.Debug;

/// <summary>
/// Mouse-accessible controls for the disposable planar/probe validation rig.
/// </summary>
internal sealed class OceanDiagnosticReflectionPanel
{
	private static readonly int[] PlanarWidths =
	{
		256,
		512,
		768,
		1024,
	};


	private OceanPlanarReflectionDiagnostic _diagnostic;
	private OceanLocalBoatReflectionDiagnostic _localBoat;


	internal void Initialize(
		VBoxContainer parent,
		OceanPlanarReflectionDiagnostic diagnostic)
	{
		_diagnostic =
			diagnostic;
		_localBoat = diagnostic?.GetParent()?.GetNodeOrNull<OceanLocalBoatReflectionDiagnostic>(
			"OceanLocalBoatReflectionDiagnostic");


		OceanDiagnosticUi.Header(
			parent,
			"Reflection Diagnostics");


		if (_diagnostic == null)
		{
			OceanDiagnosticUi.Info(
				parent,
				"OceanPlanarReflectionDiagnostic was not found.");

			return;
		}


		OceanDiagnosticUi.Info(
			parent,
			"Probe, planar capture, boat segmentation, and surface debug controls.");



		CheckBox probeEnabled =
			OceanDiagnosticUi.Check(
				parent,
				"ReflectionProbe enabled",
				_diagnostic.ProbeEnabled);


		probeEnabled.Toggled +=
			_diagnostic.SetProbeEnabled;


		OceanDiagnosticUi.Spin(
			parent,
			"Probe intensity",
			_diagnostic.ProbeIntensity,
			0.0,
			2.0,
			0.05)
			.ValueChanged +=
				value =>
					_diagnostic.SetProbeIntensity(
						(float)value);


		parent.AddChild(
			new HSeparator());


		CheckBox planarEnabled =
			OceanDiagnosticUi.Check(
				parent,
				"Planar reflection enabled",
				_diagnostic.PlanarEnabled);


		planarEnabled.Toggled +=
			_diagnostic.SetPlanarEnabled;


		OceanDiagnosticUi.Spin(
			parent,
			"Planar weight",
			_diagnostic.PlanarWeight,
			0.0,
			1.0,
			0.05)
			.ValueChanged +=
				value =>
					_diagnostic.SetPlanarWeight(
						(float)value);


		OptionButton updateMode =
			OceanDiagnosticUi.Option(
				parent,
				"Update mode",
				"ALWAYS",
				"EVERY 2");


		updateMode.Selected =
			_diagnostic.EveryTwoFrames
				? 1
				: 0;


		updateMode.ItemSelected +=
			index =>
				_diagnostic.SetEveryTwoFrames(
					index == 1);


		OptionButton resolution =
			OceanDiagnosticUi.Option(
				parent,
				"Planar resolution",
				"256",
				"512",
				"768",
				"1024");


		resolution.Selected =
			System.Array.IndexOf(
				PlanarWidths,
				_diagnostic.TargetWidth);


		if (resolution.Selected < 0)
		{
			resolution.Selected =
				0;
		}


		resolution.ItemSelected +=
			index =>
				_diagnostic.SetTargetWidth(
					PlanarWidths[
						(int)index]);


		OptionButton distortion =
			OceanDiagnosticUi.Option(
				parent,
				"Distortion",
				"OFF",
				"SMALL");


		distortion.Selected =
			_diagnostic.SmallDistortion
				? 1
				: 0;


		distortion.ItemSelected +=
			index =>
				_diagnostic.SetSmallDistortion(
					index == 1);


		OptionButton segmentation =
			OceanDiagnosticUi.Option(
				parent,
				"Boat segmentation",
				"WHOLE",
				"SEGMENTED");


		segmentation.Selected =
			_diagnostic.SegmentedHull
				? 1
				: 0;


		segmentation.ItemSelected +=
			index =>
				_diagnostic.SetSegmentedHull(
					index == 1);


		CheckBox materialClip =
			OceanDiagnosticUi.Check(
				parent,
				"Material world-space clip",
				_diagnostic.MaterialClipEnabled);


		materialClip.Toggled +=
			_diagnostic.SetMaterialClipEnabled;


		OptionButton debugMode =
			OceanDiagnosticUi.Option(
				parent,
				"Surface display",
				"FINAL",
				"PROJECTIVE RGB",
				"PROJECTIVE ALPHA",
				"PROJECTIVE UV",
				"FORCE WEIGHT 1",
				"DISTORTION OFF",
				"OLD SCREEN_UV RGB",
				"LOCAL RGB",
				"LOCAL ALPHA",
				"LOCAL INFLUENCE");


		debugMode.Selected =
			_diagnostic.SurfaceDiagnosticMode;


		debugMode.ItemSelected +=
			index =>
				_diagnostic.SetSurfaceDiagnosticMode(
					(int)index);


		VBoxContainer previews =
			OceanDiagnosticUi.Foldout(
				parent,
				"Planar previews");


		OceanDiagnosticUi.Info(
			previews,
			"RGB");


		previews.AddChild(CreatePreview(_diagnostic.PlanarTexture));


		OceanDiagnosticUi.Info(
			previews,
			"Alpha");


		TextureRect alphaPreview =
			CreatePreview(
				_diagnostic.PlanarTexture);


		alphaPreview.Material =
			new ShaderMaterial
			{
				Shader =
					new Shader
					{
						Code =
							"shader_type canvas_item;\n" +
							"void fragment(){ float a = texture(TEXTURE, UV).a; COLOR = vec4(vec3(a), 1.0); }",
					},
			};


		previews.AddChild(
			alphaPreview);

		if (_localBoat != null)
		{
			parent.AddChild(new HSeparator());
			OceanDiagnosticUi.Header(parent, "Local boat reflection");
			OceanDiagnosticUi.Check(parent, "Local boat reflection enabled",
				_localBoat.LocalEnabled).Toggled += _localBoat.SetLocalEnabled;
			OceanDiagnosticUi.Spin(parent, "Influence radius",
				_localBoat.Radius, 8.0, 30.0, 0.5).ValueChanged +=
				value => _localBoat.SetRadius((float)value);
			VBoxContainer localPreviews =
				OceanDiagnosticUi.Foldout(parent, "Local boat previews");
			OceanDiagnosticUi.Info(localPreviews, "RGB");
			localPreviews.AddChild(CreatePreview(_localBoat.Texture));
			OceanDiagnosticUi.Info(localPreviews, "Alpha");
			TextureRect localAlpha = CreatePreview(_localBoat.Texture);
			localAlpha.Material = alphaPreview.Material;
			localPreviews.AddChild(localAlpha);
		}
	}


	private static TextureRect CreatePreview(
		Texture2D texture)
	{
		return new TextureRect
		{
			CustomMinimumSize =
				new Vector2(
					320.0f,
					180.0f),

			SizeFlagsHorizontal =
				Control.SizeFlags.ExpandFill,

			Texture =
				texture,

			ExpandMode =
				TextureRect.ExpandModeEnum.IgnoreSize,

			StretchMode =
				TextureRect.StretchModeEnum.KeepAspectCentered,
		};
	}
}
