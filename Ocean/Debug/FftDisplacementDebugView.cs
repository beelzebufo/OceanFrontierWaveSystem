using System;
using Godot;
using OceanFrontier.Water.Runtime;

namespace OceanFrontier.Water.Debug;

/// <summary>
/// Temporary GPU displacement visualization.
///
/// Can display:
/// - raw FFT spectral displacement slices;
/// - canonical AnimatedWaveField spatial LOD slices.
/// - derived normal/Jacobian spatial LOD slices.
///
/// Debug only.
/// Must never become the production ocean rendering path.
/// </summary>
public partial class FftDisplacementDebugView : CanvasLayer
{
	private const string ShaderPath =
		"res://Ocean/Shaders/Debug/fft_displacement_debug.gdshader";


	public enum DebugSource
	{
		RawFft = 0,
		AnimatedWaveField = 1,
		AnimatedWaveDerivativeField = 2,
	}


	public enum DebugChannel
	{
		DisplacementX = 0,
		Height = 1,
		DisplacementZ = 2,
	}


	public enum DebugMode
	{
		Grayscale = 0,
		Sign = 1,
		Magnitude = 2,
		UvTest = 3,
		Normal = 4,
		Jacobian = 5,
	}


	[Export]
	public DebugSource Source { get; set; } =
		DebugSource.AnimatedWaveField;


	[Export]
	public DebugMode Mode { get; set; } =
		DebugMode.Sign;


	// Kept as CascadeIndex to avoid unnecessary scene/property churn.
	//
	// RawFft:
	//     FFT cascade index.
	//
	// AnimatedWaveField:
	//     spatial Animated Wave LOD index.
	[Export(PropertyHint.Range, "0,15,1")]
	public int CascadeIndex { get; set; } = 0;


	[Export(
		PropertyHint.Range,
		"0.000001,10.0,0.000001,or_greater")]
	public float Gain { get; set; } =
		0.001f;


	[Export]
	public DebugChannel Channel { get; set; } =
		DebugChannel.Height;

	public string PhysicalMetadata { get; set; } = string.Empty;


	[Export]
	public Vector2 PanelPosition { get; set; } =
		new Vector2(
			16.0f,
			16.0f);


	[Export]
	public Vector2 PanelSize { get; set; } =
		new Vector2(
			512.0f,
			512.0f);


	private OceanRuntime _runtime;

	private ColorRect _display;
	private Label _label;

	private ShaderMaterial _material;

	private Texture2DArrayRD _textureArray;

	private Rid _boundRid;


	private int _lastCascade = -1;

	private float _lastGain =
		float.NaN;

	private int _lastChannel = -1;

	private int _lastMode = -1;

	private int _lastSource = -1;


	public override void _Ready()
	{
		_runtime =
			GetParent() as OceanRuntime;


		if (_runtime == null)
		{
			GD.PushError(
				"FftDisplacementDebugView must be a direct " +
				"child of OceanRuntime.");

			SetProcess(false);

			return;
		}


		Shader shader =
			GD.Load<Shader>(
				ShaderPath);


		if (shader == null)
		{
			GD.PushError(
				$"Could not load displacement debug shader: " +
				$"{ShaderPath}");

			SetProcess(false);

			return;
		}


		_material =
			new ShaderMaterial
			{
				Shader = shader,
			};


		_display =
			new ColorRect
			{
				Position =
					PanelPosition,

				Size =
					PanelSize,

				Color =
					Colors.White,

				MouseFilter =
					Control.MouseFilterEnum.Ignore,

				Material =
					_material,

				Visible =
					false,
			};


		AddChild(
			_display);


		_label =
			new Label
			{
				Position =
					new Vector2(
						PanelPosition.X,
						PanelPosition.Y +
						PanelSize.Y +
						6.0f),

				MouseFilter =
					Control.MouseFilterEnum.Ignore,

				Text =
					"Ocean displacement: waiting for GPU...",
			};


		AddChild(
			_label);
	}


	public override void _Process(
		double delta)
	{
		if (_runtime == null)
		{
			return;
		}


		if (!TryGetSelectedTexture(
				out Rid textureRid,
				out int sliceCount))
		{
			if (_display != null)
			{
				_display.Visible =
					false;
			}


			if (_label != null)
			{
				_label.Text =
					$"{GetSourceDisplayName()} | " +
					"waiting for GPU...";
			}

			return;
		}


		if (!_boundRid.IsValid ||
			_boundRid.Id != textureRid.Id)
		{
			BindTexture(
				textureRid);
		}


		int slice =
			Math.Clamp(
				CascadeIndex,
				0,
				sliceCount - 1);


		UpdateMaterialParameters(
			slice);


		_display.Visible =
			true;


		string sliceName =
			Source ==
			DebugSource.RawFft
				? "Cascade"
				: "LOD";


		_label.Text =
			$"{GetSourceDisplayName()} | " +
			$"{sliceName} {slice}/{sliceCount - 1} | " +
			$"{Channel} | " +
			$"{Mode} | " +
			$"Gain {Gain:0.######}" +
			(PhysicalMetadata.Length > 0
				? $"\n{PhysicalMetadata}"
				: string.Empty);
	}


	private bool TryGetSelectedTexture(
		out Rid textureRid,
		out int sliceCount)
	{
		textureRid =
			default;

		sliceCount =
			0;


		switch (Source)
		{
			case DebugSource.RawFft:
				return
					_runtime.TryGetRawFftDebugTexture(
						out textureRid,
						out sliceCount);


			case DebugSource.AnimatedWaveField:
				return
					_runtime.TryGetAnimatedWaveDebugTexture(
						out textureRid,
						out sliceCount);


			case DebugSource.AnimatedWaveDerivativeField:
				return
					_runtime.TryGetAnimatedWaveDerivativeDebugTexture(
						out textureRid,
						out sliceCount);


			default:
				return false;
		}
	}


	private string GetSourceDisplayName()
	{
		return
			Source switch
			{
				DebugSource.RawFft =>
					"Raw FFT Displacement",

				DebugSource.AnimatedWaveField =>
					"AnimatedWaveField",

				DebugSource.AnimatedWaveDerivativeField =>
					"AnimatedWaveDerivativeField",

				_ =>
					"Unknown displacement source",
			};
	}


	private void BindTexture(
		Rid textureRid)
	{
		if (_textureArray == null)
		{
			_textureArray =
				new Texture2DArrayRD();
		}


		_textureArray.TextureRdRid =
			textureRid;


		_material.SetShaderParameter(
			"displacement_texture",
			_textureArray);


		_boundRid =
			textureRid;


		GD.Print(
			$"[Ocean Debug] Bound " +
			$"{GetSourceDisplayName()} RID " +
			$"{textureRid.Id}");
	}


	private void UpdateMaterialParameters(
		int slice)
	{
		if (_lastCascade != slice)
		{
			_material.SetShaderParameter(
				"cascade_index",
				(float)slice);

			_lastCascade =
				slice;
		}


		if (!Mathf.IsEqualApprox(
				_lastGain,
				Gain))
		{
			_material.SetShaderParameter(
				"gain",
				Gain);

			_lastGain =
				Gain;
		}


		int channel =
			(int)Channel;

		if (_lastChannel != channel)
		{
			_material.SetShaderParameter(
				"channel",
				channel);

			_lastChannel =
				channel;
		}


		int mode =
			(int)Mode;

		if (_lastMode != mode)
		{
			_material.SetShaderParameter(
				"debug_mode",
				mode);

			_lastMode =
				mode;
		}


		int source =
			(int)Source;

		if (_lastSource != source)
		{
			_lastSource =
				source;
		}
	}


	public override void _ExitTree()
	{
		//
		// Texture2DArrayRD is only a wrapper.
		// The RenderingDevice resource is owned by the FFT source
		// or AnimatedWaveField.
		//

		if (_textureArray != null)
		{
			_textureArray.TextureRdRid =
				default;
		}


		_boundRid =
			default;
	}
}
