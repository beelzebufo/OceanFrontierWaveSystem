using System;
using System.Collections.Generic;
using Godot;
using OceanFrontier.Water.Runtime;

namespace OceanFrontier.Water.Rendering.Reflections;

/// <summary>Captures up to four local reflectors in one ownership-partitioned viewport.</summary>
public partial class OceanLocalBoatReflectionDiagnostic : Node
{
	private const uint ProxyLayer = 1u << 14;
	private const uint MainOnlyLayer = 1u << 13;
	private const float LocalFar = 80.0f;
	private const int Capacity = OceanLocalReflectionRegistry.MaxActiveReflectors;

	[Export] public NodePath SurfaceRendererPath { get; set; }
	[Export] public NodePath MainCameraPath { get; set; }
	[Export] public NodePath ReflectionProbePath { get; set; }
	[Export] public NodePath ParticipantPath { get; set; }
	[Export] public NodePath RegistryPath { get; set; }

	private sealed class ProxyMesh
	{
		public MeshInstance3D Source;
		public MeshInstance3D Proxy;
		public ShaderMaterial Material;
		public uint OriginalLayers;
	}

	private sealed class ProxySet
	{
		public OceanLocalReflectionParticipant Participant;
		public Node3D Root;
		public readonly List<ProxyMesh> Meshes = new();
		public int Slot = -1;
		public int Columns;
		public int Rows;
	}

	private readonly ProxySet[] _sets = new ProxySet[Capacity];
	private readonly ProxySet[] _nextSets = new ProxySet[Capacity];
	private AnimatedWaveSurfaceRenderer _surfaceRenderer;
	private OceanLocalReflectionRegistry _registry;
	private OceanLocalReflectionParticipant _diagnosticBoat;
	private Camera3D _mainCamera;
	private ReflectionProbe _reflectionProbe;
	private uint _originalMainMask;
	private uint _originalProbeMask;
	private Node3D _proxyRoot;
	private Shader _proxyShader;
	private SubViewport _viewport;
	private Camera3D _camera;
	private Texture2D _texture;
	private bool _enabled = true;
	private bool _captureActive;
	private int _captureCount;
	private int _columns = 1;
	private int _rows = 1;
	private int _targetWidth = 256;
	private float _fallbackRadius = 14.0f;
	private Projection _appliedProjection;
	private bool _hasProjection;

	internal bool LocalEnabled => _enabled;
	internal float Radius => GodotObject.IsInstanceValid(_diagnosticBoat)
		? _diagnosticBoat.InfluenceRadius : _fallbackRadius;
	internal int TargetWidth => _targetWidth;
	internal Texture2D Texture => _texture;

	public override void _Ready()
	{
		_surfaceRenderer = GetNodeOrNull<AnimatedWaveSurfaceRenderer>(SurfaceRendererPath);
		_mainCamera = GetNodeOrNull<Camera3D>(MainCameraPath);
		_reflectionProbe = GetNodeOrNull<ReflectionProbe>(ReflectionProbePath);
		_diagnosticBoat = ParticipantPath != null && !ParticipantPath.IsEmpty
			? GetNodeOrNull<OceanLocalReflectionParticipant>(ParticipantPath) : null;
		_registry = RegistryPath != null && !RegistryPath.IsEmpty
			? GetNodeOrNull<OceanLocalReflectionRegistry>(RegistryPath) : null;
		if (_surfaceRenderer == null || _mainCamera == null ||
			_reflectionProbe == null || _registry == null)
		{
			GD.PushError("Local reflection capture node paths are incomplete.");
			SetProcess(false);
			return;
		}
		_originalMainMask = _mainCamera.CullMask;
		_originalProbeMask = _reflectionProbe.CullMask;
		_mainCamera.CullMask = (_originalMainMask | MainOnlyLayer) & ~ProxyLayer;
		_reflectionProbe.CullMask &= ~(ProxyLayer | MainOnlyLayer);

		_proxyRoot = new Node3D { Name = "LocalReflectionProxies" };
		AddChild(_proxyRoot);
		_proxyShader = GD.Load<Shader>(
			"res://Ocean/Shaders/Rendering/diagnostic_local_reflection_proxy.gdshader");
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
			RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
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
		_surfaceRenderer.SetLocalReflectionTexture(_texture);
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

	private ProxySet CreateSet(OceanLocalReflectionParticipant participant,
		Projection projection)
	{
		Node3D sourceRoot = participant.SourceRoot;
		if (sourceRoot == null)
			return null;
		var set = new ProxySet
		{
			Participant = participant,
			Root = new Node3D { Name = participant.Name + "ReflectionProxies" },
		};
		_proxyRoot.AddChild(set.Root);
		CollectMeshes(sourceRoot, set, projection);
		return set;
	}

	private void CollectMeshes(Node node, ProxySet set, Projection projection)
	{
		if (node is MeshInstance3D source && source.Mesh != null)
		{
			var material = new ShaderMaterial { Shader = _proxyShader };
			material.SetShaderParameter("material_color", GetSourceColor(source));
			material.SetShaderParameter("mean_sea_level_y", OceanRuntime.MeanSeaLevelY);
			material.SetShaderParameter("source_projection", projection);
			var proxy = new MeshInstance3D
			{
				Name = source.Name + "LocalReflectionProxy",
				Mesh = source.Mesh,
				MaterialOverride = material,
				Layers = ProxyLayer,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off,
			};
			set.Root.AddChild(proxy);
			set.Meshes.Add(new ProxyMesh
			{
				Source = source,
				Proxy = proxy,
				Material = material,
				OriginalLayers = source.Layers,
			});
			source.Layers = MainOnlyLayer;
		}
		foreach (Node child in node.GetChildren())
			CollectMeshes(child, set, projection);
	}

	private static void ReleaseSet(ProxySet set)
	{
		if (set == null)
			return;
		foreach (ProxyMesh mesh in set.Meshes)
			if (GodotObject.IsInstanceValid(mesh.Source))
				mesh.Source.Layers = mesh.OriginalLayers;
		if (GodotObject.IsInstanceValid(set.Root))
		{
			set.Root.Visible = false;
			set.Root.QueueFree();
		}
	}

	private void ReconcileSets()
	{
		int desiredCount = _captureActive ? Math.Min(_registry.ActiveCount, Capacity) : 0;
		bool changed = desiredCount != _captureCount;
		for (int i = 0; i < desiredCount && !changed; i++)
			changed = _sets[i]?.Participant != _registry.GetActiveParticipant(i);
		if (!changed)
			return;
		for (int i = 0; i < Capacity; i++)
			_nextSets[i] = null;
		for (int i = 0; i < desiredCount; i++)
		{
			OceanLocalReflectionParticipant participant =
				_registry.GetActiveParticipant(i);
			for (int old = 0; old < Capacity; old++)
				if (_sets[old]?.Participant == participant)
				{
					_nextSets[i] = _sets[old];
					break;
				}
		}
		for (int old = 0; old < Capacity; old++)
		{
			ProxySet set = _sets[old];
			if (set == null)
				continue;
			bool retained = false;
			for (int i = 0; i < desiredCount; i++)
				if (_nextSets[i] == set)
				{
					retained = true;
					break;
				}
			if (!retained)
				ReleaseSet(set);
		}
		_captureCount = desiredCount;
		_columns = desiredCount > 1 ? 2 : 1;
		_rows = desiredCount > 2 ? 2 : 1;
		Projection projection = _mainCamera.GetCameraProjection();
		for (int i = 0; i < desiredCount; i++)
		{
			_nextSets[i] ??= CreateSet(_registry.GetActiveParticipant(i), projection);
			ApplyTile(_nextSets[i], i);
		}
		for (int i = 0; i < Capacity; i++)
			_sets[i] = _nextSets[i];
		_surfaceRenderer.SetLocalReflectionCount(0);
		UpdateViewportSize();
	}

	private void ApplyTile(ProxySet set, int slot)
	{
		if (set == null ||
			(set.Slot == slot && set.Columns == _columns && set.Rows == _rows))
			return;
		set.Slot = slot;
		set.Columns = _columns;
		set.Rows = _rows;
		Vector2 tile = new(slot % _columns, slot / _columns);
		Vector2 grid = new(_columns, _rows);
		foreach (ProxyMesh mesh in set.Meshes)
		{
			mesh.Material.SetShaderParameter("tile_index", tile);
			mesh.Material.SetShaderParameter("atlas_grid", grid);
		}
	}

	private void SynchronizeForDraw()
	{
		if (!IsInsideTree() || !_captureActive ||
			!GodotObject.IsInstanceValid(_registry) ||
			!GodotObject.IsInstanceValid(_mainCamera))
			return;
		Transform3D effectiveCamera = _mainCamera.GetCameraTransform();
		_registry.RefreshSelection(effectiveCamera);
		ReconcileSets();
		_camera.Projection = _mainCamera.Projection;
		_camera.Fov = _mainCamera.Fov;
		_camera.Size = _mainCamera.Size;
		_camera.Near = _mainCamera.Near;
		_camera.Far = Mathf.Min(_mainCamera.Far, LocalFar);
		_camera.FrustumOffset = _mainCamera.FrustumOffset;
		_camera.KeepAspect = _mainCamera.KeepAspect;
		_camera.GlobalTransform = effectiveCamera;
		_camera.ForceUpdateTransform();
		Projection projection = _mainCamera.GetCameraProjection();
		if (!_hasProjection || !_appliedProjection.Equals(projection))
		{
			_appliedProjection = projection;
			_hasProjection = true;
			for (int i = 0; i < _captureCount; i++)
				if (_sets[i] != null)
					foreach (ProxyMesh mesh in _sets[i].Meshes)
						mesh.Material.SetShaderParameter("source_projection", projection);
		}
		for (int i = 0; i < _captureCount; i++)
		{
			ProxySet set = _sets[i];
			if (set == null)
				continue;
			foreach (ProxyMesh mesh in set.Meshes)
			{
				if (!GodotObject.IsInstanceValid(mesh.Source) ||
					!mesh.Source.IsInsideTree())
				{
					mesh.Proxy.Visible = false;
					continue;
				}
				mesh.Proxy.Visible = mesh.Source.IsVisibleInTree();
				if (mesh.Proxy.Mesh != mesh.Source.Mesh)
					mesh.Proxy.Mesh = mesh.Source.Mesh;
				mesh.Proxy.GlobalTransform = Mirror(
					mesh.Source.GetGlobalTransformInterpolated());
				mesh.Proxy.ForceUpdateTransform();
			}
			if (set.Participant.TryGetCenterWorld(out Vector3 center))
				_surfaceRenderer.SetLocalReflectionRegion(i,
					new Vector4(center.X, center.Z,
						set.Participant.InfluenceRadius, 0.0f));
		}
		_surfaceRenderer.SetLocalReflectionGrid(new Vector2(_columns, _rows));
		_surfaceRenderer.SetLocalReflectionCount(_captureCount);
		_surfaceRenderer.SetLocalReflectionWeight(_captureCount > 0 ? 1.0f : 0.0f);
		_viewport.RenderTargetUpdateMode = _captureCount > 0
			? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
	}

	public override void _Process(double delta)
	{
		if (_viewport == null)
			return;
		UpdateViewportSize();
		_captureActive = _enabled && GodotObject.IsInstanceValid(_registry) &&
			!_surfaceRenderer.PlanetCurvatureEnabled &&
			_mainCamera.GlobalPosition.Y >= OceanRuntime.MeanSeaLevelY;
		if (_captureActive)
			return;
		ReconcileSets();
		_surfaceRenderer.SetLocalReflectionCount(0);
		_surfaceRenderer.SetLocalReflectionWeight(0.0f);
		_viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
	}

	private void UpdateViewportSize()
	{
		if (_viewport == null)
			return;
		Vector2 size = GetViewport().GetVisibleRect().Size;
		if (size.X <= 0 || size.Y <= 0)
			return;
		int tileHeight = Math.Max(2,
			Mathf.RoundToInt(_targetWidth * size.Y / size.X));
		Vector2I targetSize = new(_targetWidth * _columns, tileHeight * _rows);
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
		if (GodotObject.IsInstanceValid(_diagnosticBoat))
			_diagnosticBoat.Enabled = enabled;
		if (!enabled && _viewport != null)
			_Process(0.0);
	}

	internal void SetRadius(float radius)
	{
		float bounded = Mathf.Clamp(radius, 8.0f, 30.0f);
		if (GodotObject.IsInstanceValid(_diagnosticBoat))
			_diagnosticBoat.InfluenceRadius = bounded;
		else
			_fallbackRadius = bounded;
	}

	public override void _ExitTree()
	{
		RenderingServer.FramePreDraw -= SynchronizeForDraw;
		for (int i = 0; i < Capacity; i++)
		{
			ReleaseSet(_sets[i]);
			_sets[i] = null;
		}
		if (GodotObject.IsInstanceValid(_surfaceRenderer))
		{
			_surfaceRenderer.SetLocalReflectionCount(0);
			_surfaceRenderer.SetLocalReflectionWeight(0.0f);
		}
		if (GodotObject.IsInstanceValid(_mainCamera))
			_mainCamera.CullMask = _originalMainMask;
		if (GodotObject.IsInstanceValid(_reflectionProbe))
			_reflectionProbe.CullMask = _originalProbeMask;
	}
}
