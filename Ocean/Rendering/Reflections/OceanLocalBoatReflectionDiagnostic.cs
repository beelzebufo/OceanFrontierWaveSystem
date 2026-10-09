using System;
using System.Collections.Generic;
using Godot;
using OceanFrontier.Water.Runtime;

namespace OceanFrontier.Water.Rendering.Reflections;

/// <summary>One diagnostic boat rendered as clipped mirrored geometry.</summary>
public partial class OceanLocalBoatReflectionDiagnostic : Node
{
	private const uint ProxyLayer = 1u << 14;
	private const uint MainOnlyBoatLayer = 1u << 13;
	private const float LocalFar = 80.0f;

	[Export] public NodePath SurfaceRendererPath { get; set; }
	[Export] public NodePath MainCameraPath { get; set; }
	[Export] public NodePath BoatRootPath { get; set; }
	[Export] public NodePath ReflectionProbePath { get; set; }

	private readonly List<(MeshInstance3D Source, MeshInstance3D Proxy, uint Layers)> _meshes = new();
	private AnimatedWaveSurfaceRenderer _surfaceRenderer;
	private Camera3D _mainCamera;
	private Node3D _boatRoot;
	private ReflectionProbe _reflectionProbe;
	private uint _originalMainMask;
	private uint _originalProbeMask;
	private Node3D _proxyRoot;
	private SubViewport _viewport;
	private Camera3D _camera;
	private Texture2D _texture;
	private bool _enabled = true;
	private bool _captureActive;
	private float _radius = 14.0f;
	private int _targetWidth = 256;

	internal bool LocalEnabled => _enabled;
	internal float Radius => _radius;
	internal int TargetWidth => _targetWidth;
	internal Texture2D Texture => _texture;

	public override void _Ready()
	{
		_surfaceRenderer = GetNodeOrNull<AnimatedWaveSurfaceRenderer>(SurfaceRendererPath);
		_mainCamera = GetNodeOrNull<Camera3D>(MainCameraPath);
		_boatRoot = GetNodeOrNull<Node3D>(BoatRootPath);
		_reflectionProbe = GetNodeOrNull<ReflectionProbe>(ReflectionProbePath);
		if (_surfaceRenderer == null || _mainCamera == null ||
			_boatRoot == null || _reflectionProbe == null)
		{
			GD.PushError("Local boat reflection diagnostic node paths are incomplete.");
			SetProcess(false);
			return;
		}
		_originalMainMask = _mainCamera.CullMask;
		_originalProbeMask = _reflectionProbe.CullMask;
		_mainCamera.CullMask =
			(_mainCamera.CullMask | MainOnlyBoatLayer) & ~ProxyLayer;
		_reflectionProbe.CullMask &=
			~(ProxyLayer | MainOnlyBoatLayer);

		_proxyRoot = new Node3D { Name = "LocalReflectionBoatProxies" };
		AddChild(_proxyRoot);
		Shader shader = GD.Load<Shader>(
			"res://Ocean/Shaders/Rendering/diagnostic_local_reflection_proxy.gdshader");
		foreach (Node child in _boatRoot.GetChildren())
		{
			if (child is not MeshInstance3D source || source.Mesh == null)
				continue;
			var material = new ShaderMaterial { Shader = shader };
			material.SetShaderParameter("material_color", GetSourceColor(source));
			material.SetShaderParameter("mean_sea_level_y", OceanRuntime.MeanSeaLevelY);
			var proxy = new MeshInstance3D
			{
				Name = source.Name + "LocalReflectionProxy",
				Mesh = source.Mesh,
				MaterialOverride = material,
				Layers = ProxyLayer,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off,
			};
			_proxyRoot.AddChild(proxy);
			_meshes.Add((source, proxy, source.Layers));
		}

		_viewport = new SubViewport
		{
			Name = "LocalBoatReflectionViewport",
			OwnWorld3D = false,
			World3D = GetViewport().World3D,
			TransparentBg = true,
			Disable3D = false,
			Msaa3D = Viewport.Msaa.Disabled,
			ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled,
			UseTaa = false,
			UseOcclusionCulling = false,
			PositionalShadowAtlasSize = 0,
			RenderTargetClearMode = SubViewport.ClearMode.Always,
			RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
		};
		AddChild(_viewport);
		_camera = new Camera3D
		{
			Name = "LocalBoatReflectionCamera",
			CullMask = ProxyLayer,
			Current = true,
			Environment = new Godot.Environment
			{
				BackgroundMode = Godot.Environment.BGMode.ClearColor,
			},
			PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off,
		};
		_viewport.AddChild(_camera);
		UpdateViewportSize();
		_texture = _viewport.GetTexture();
		_surfaceRenderer.SetLocalBoatReflection(_texture, _radius);
		SetLocalEnabled(_enabled);
		RenderingServer.FramePreDraw += SynchronizeForDraw;
	}

	private static Color GetSourceColor(MeshInstance3D source)
	{
		Material sourceMaterial = source.MaterialOverride ?? source.GetActiveMaterial(0);
		if (sourceMaterial is BaseMaterial3D standard)
			return standard.AlbedoColor;
		if (sourceMaterial is ShaderMaterial shader)
			return shader.GetShaderParameter("material_albedo").AsColor();
		return Colors.White;
	}

	private static Transform3D Mirror(Transform3D source)
	{
		static Vector3 Reflect(Vector3 v) => new(v.X, -v.Y, v.Z);
		Vector3 origin = Reflect(source.Origin);
		origin.Y += 2.0f * OceanRuntime.MeanSeaLevelY;
		return new Transform3D(
			new Basis(Reflect(source.Basis.X), Reflect(source.Basis.Y), Reflect(source.Basis.Z)),
			origin);
	}

	private void SynchronizeForDraw()
	{
		if (!IsInsideTree() || !_captureActive ||
			_camera == null || _mainCamera == null)
			return;
		_camera.Projection = _mainCamera.Projection;
		_camera.Fov = _mainCamera.Fov;
		_camera.Size = _mainCamera.Size;
		_camera.Near = _mainCamera.Near;
		_camera.Far = Mathf.Min(_mainCamera.Far, LocalFar);
		_camera.FrustumOffset = _mainCamera.FrustumOffset;
		_camera.KeepAspect = _mainCamera.KeepAspect;
		_camera.GlobalTransform = _mainCamera.GetCameraTransform();
		_camera.ForceUpdateTransform();
		foreach (var (source, proxy, _) in _meshes)
		{
			proxy.GlobalTransform = Mirror(source.GetGlobalTransformInterpolated());
			proxy.ForceUpdateTransform();
		}
		_surfaceRenderer.SetLocalBoatReflectionCenter(
			_boatRoot.GetGlobalTransformInterpolated().Origin);
	}

	public override void _Process(double delta)
	{
		if (_viewport == null)
			return;
		UpdateViewportSize();
		bool active = _enabled && !_surfaceRenderer.PlanetCurvatureEnabled &&
			_mainCamera.GlobalPosition.Y >= OceanRuntime.MeanSeaLevelY;
		_captureActive = active;
		SubViewport.UpdateMode updateMode = active
			? SubViewport.UpdateMode.Always
			: SubViewport.UpdateMode.Disabled;
		if (_viewport.RenderTargetUpdateMode != updateMode)
			_viewport.RenderTargetUpdateMode = updateMode;
		_surfaceRenderer.SetLocalBoatReflectionWeight(active ? 1.0f : 0.0f);
	}

	private void UpdateViewportSize()
	{
		if (_viewport == null)
			return;
		Vector2 size = GetViewport().GetVisibleRect().Size;
		if (size.X <= 0 || size.Y <= 0)
			return;
		Vector2I targetSize = new(_targetWidth,
			Math.Max(2, Mathf.RoundToInt(_targetWidth * size.Y / size.X)));
		if (_viewport.Size != targetSize)
			_viewport.Size = targetSize;
	}

	public void SetTargetWidth(int width)
	{
		_targetWidth = width switch
		{
			128 => 128,
			512 => 512,
			_ => 256,
		};
		UpdateViewportSize();
	}

	public void SetLocalEnabled(bool enabled)
	{
		_enabled = enabled;
		foreach (var (source, _, layers) in _meshes)
			source.Layers = enabled
				? MainOnlyBoatLayer
				: layers;
		_surfaceRenderer?.SetLocalBoatReflectionWeight(enabled ? 1.0f : 0.0f);
	}

	internal void SetRadius(float radius)
	{
		_radius = Mathf.Clamp(radius, 8.0f, 30.0f);
		_surfaceRenderer?.SetLocalBoatReflectionRadius(_radius);
	}

	public override void _ExitTree()
	{
		RenderingServer.FramePreDraw -= SynchronizeForDraw;
		foreach (var (source, _, layers) in _meshes)
			if (GodotObject.IsInstanceValid(source)) source.Layers = layers;
		_surfaceRenderer?.SetLocalBoatReflectionWeight(0.0f);
		if (GodotObject.IsInstanceValid(_mainCamera))
			_mainCamera.CullMask = _originalMainMask;
		if (GodotObject.IsInstanceValid(_reflectionProbe))
			_reflectionProbe.CullMask = _originalProbeMask;
	}
}
