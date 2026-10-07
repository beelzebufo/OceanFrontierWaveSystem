using Godot;
using OceanFrontier.Water.Rendering.Reflections;

namespace OceanFrontier.Water.Debug;

/// <summary>
/// Mouse-accessible controls for the disposable planar/probe validation rig.
/// </summary>
internal sealed partial class OceanDiagnosticReflectionPanel
{
	private static readonly int[] PlanarWidths =
	{
		256,
		512,
		768,
		1024,
	};


	private OceanPlanarReflectionDiagnostic _diagnostic;
	private Label _temporalState;
	private TemporalMarker _planarMarker;
	private TemporalMarker _mainMarker;
	private TextureRect _rgbPreview;

	internal void Tick()
	{
		if (_diagnostic == null) return;
		_temporalState.Text = _diagnostic.TemporalState;
		Vector2 textureSize = _diagnostic.PlanarTexture.GetSize();
		float scale = Mathf.Min(_rgbPreview.Size.X / textureSize.X,
			_rgbPreview.Size.Y / textureSize.Y);
		Vector2 imageSize = textureSize * scale;
		_planarMarker.Point = (_rgbPreview.Size - imageSize) * 0.5f +
			_diagnostic.ExpectedPlanarUv * imageSize;
		_planarMarker.Visible = _diagnostic.PlanarMarkerValid;
		_planarMarker.QueueRedraw();
		_mainMarker.Point = _diagnostic.ExpectedMainScreen;
		_mainMarker.Visible = _diagnostic.MainMarkerValid;
		_mainMarker.QueueRedraw();
	}


	internal void Initialize(
		VBoxContainer parent,
		OceanPlanarReflectionDiagnostic diagnostic)
	{
		_diagnostic =
			diagnostic;


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
		_diagnostic.TemporalStateSynchronized += Tick;


		OceanDiagnosticUi.Info(
			parent,
			"Probe, planar capture, boat segmentation, and surface debug controls.");

		_temporalState = new Label();
		parent.AddChild(_temporalState);
		OceanDiagnosticUi.Info(parent,
			"Cyan crosses: expected target origin. Main cross is the undistorted mirror point; use Distortion OFF.");
		CanvasLayer markerLayer = new CanvasLayer { Layer = 100 };
		_diagnostic.AddChild(markerLayer);
		_mainMarker = new TemporalMarker();
		_mainMarker.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		markerLayer.AddChild(_mainMarker);


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
				"OLD SCREEN_UV RGB");


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


		_rgbPreview = CreatePreview(_diagnostic.PlanarTexture);
		previews.AddChild(_rgbPreview);
		_planarMarker = new TemporalMarker();
		_planarMarker.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_rgbPreview.AddChild(_planarMarker);


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
	}

	private sealed partial class TemporalMarker : Control
	{
		internal Vector2 Point;

		internal TemporalMarker()
		{
			MouseFilter = MouseFilterEnum.Ignore;
		}

		public override void _Draw()
		{
			if (Point.X < 0 || Point.Y < 0 ||
				Point.X >= Size.X || Point.Y >= Size.Y) return;
			Color color = Colors.Cyan;
			DrawLine(Point + new Vector2(-9, 0), Point + new Vector2(9, 0), color, 2);
			DrawLine(Point + new Vector2(0, -9), Point + new Vector2(0, 9), color, 2);
			DrawArc(Point, 4, 0, Mathf.Tau, 20, color, 2);
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
