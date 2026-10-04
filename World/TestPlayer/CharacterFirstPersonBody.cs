using System;
using System.Collections.Generic;
using Godot;

// Builds the local camera's headless rendering once; both meshes share one skeleton.
public partial class CharacterFirstPersonBody : Node
{
	private const int FirstPersonLayer = 17;
	private const int ExternalLayer = 18;
	private const uint FirstPersonMask = 1u << (FirstPersonLayer - 1);
	private const uint ExternalMask = 1u << (ExternalLayer - 1);

	[Export] public Skeleton3D Skeleton { get; set; }
	[Export] public MeshInstance3D FullBody { get; set; }
	[Export] public MeshInstance3D Eyes { get; set; }
	[Export] public MeshInstance3D Eyebrows { get; set; }
	[Export] public Camera3D FirstPersonCamera { get; set; }

	public MeshInstance3D FirstPersonBody { get; private set; }
	public bool SetupSucceeded { get; private set; }
	public string SetupError { get; private set; } = "not started";
	public int HeadBoneIndex { get; private set; } = -1;
	public int[] HeadBindIndices { get; private set; } = Array.Empty<int>();
	public int SourceSurfaceCount { get; private set; }
	public int SourceVertexCount { get; private set; }
	public int HiddenVertexCount { get; private set; }
	public int SourceTriangleCount { get; private set; }
	public int HeadlessTriangleCount { get; private set; }
	public uint FirstPersonLayerMask => FirstPersonMask;
	public uint ExternalLayerMask => ExternalMask;
	public uint ResultCameraCullMask { get; private set; }
	public double SetupMilliseconds { get; private set; }

	public override void _Ready()
	{
		ulong start = Time.GetTicksUsec();
		try
		{
			Setup();
			SetupSucceeded = true;
			SetupError = "";
			SetupMilliseconds = (Time.GetTicksUsec() - start) / 1000.0;
			GD.Print($"FP body ready: Head bone={HeadBoneIndex}, binds=[{string.Join(",", HeadBindIndices)}], " +
				$"surfaces={SourceSurfaceCount}, vertices={SourceVertexCount}, hidden={HiddenVertexCount}, " +
				$"triangles={SourceTriangleCount}->{HeadlessTriangleCount}, layers={FirstPersonLayer}/{ExternalLayer}, " +
				$"camera mask={ResultCameraCullMask}, setup={SetupMilliseconds:F2} ms");
		}
		catch (Exception error)
		{
			SetupError = error.Message;
			GD.PushError($"FP body setup failed: {SetupError}");
		}
	}

	private void Setup()
	{
		if (Skeleton == null || FullBody == null || Eyes == null || Eyebrows == null ||
			FirstPersonCamera == null || FullBody.GetParent() != Skeleton ||
			Eyes.GetParent() != Skeleton || Eyebrows.GetParent() != Skeleton ||
			FullBody.Skin == null || Eyes.Skin != FullBody.Skin ||
			Eyebrows.Skin != FullBody.Skin || FullBody.GetNodeOrNull<Skeleton3D>(FullBody.Skeleton) != Skeleton)
			throw new InvalidOperationException("Expected one Quaternius Skeleton3D and shared Skin for body, eyes and eyebrows.");

		HeadBoneIndex = Skeleton.FindBone("Head");
		if (HeadBoneIndex < 0)
			throw new InvalidOperationException("Head bone is missing.");
		Skin skin = FullBody.Skin;
		var bindIndices = new List<int>();
		for (int i = 0; i < skin.GetBindCount(); i++)
		{
			StringName bindName = skin.GetBindName(i);
			if (bindName == "Head" || (bindName.IsEmpty && skin.GetBindBone(i) == HeadBoneIndex))
				bindIndices.Add(i);
		}
		if (bindIndices.Count == 0)
			throw new InvalidOperationException("No Skin bind resolves to Head.");
		HeadBindIndices = bindIndices.ToArray();

		if (FullBody.Mesh is not ArrayMesh source)
			throw new InvalidOperationException("Full body mesh is not an ArrayMesh.");
		if (source.GetBlendShapeCount() != 0)
			throw new InvalidOperationException("Blend shapes require preservation before headless conversion.");
		SourceSurfaceCount = source.GetSurfaceCount();
		if (SourceSurfaceCount == 0)
			throw new InvalidOperationException("Full body mesh has no surfaces.");

		var headless = new ArrayMesh();
		for (int surface = 0; surface < SourceSurfaceCount; surface++)
			AddHeadlessSurface(source, headless, surface, skin.GetBindCount());
		if (HeadlessTriangleCount == 0 || HiddenVertexCount == 0)
			throw new InvalidOperationException("Head cut removed no vertices or all triangles.");

		var instance = new MeshInstance3D
		{
			Name = "FirstPersonBody",
			Transform = FullBody.Transform,
			Mesh = headless,
			Skin = skin,
			Skeleton = FullBody.Skeleton,
			Layers = FirstPersonMask,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			MaterialOverride = FullBody.MaterialOverride,
			MaterialOverlay = FullBody.MaterialOverlay
		};
		for (int surface = 0; surface < SourceSurfaceCount; surface++)
		{
			Material overrideMaterial = FullBody.GetSurfaceOverrideMaterial(surface);
			if (overrideMaterial != null)
				instance.SetSurfaceOverrideMaterial(surface, overrideMaterial);
		}
		uint oldBodyLayers = FullBody.Layers;
		uint oldEyesLayers = Eyes.Layers;
		uint oldEyebrowsLayers = Eyebrows.Layers;
		uint oldCameraMask = FirstPersonCamera.CullMask;
		try
		{
			Skeleton.AddChild(instance);
			FullBody.Layers = ExternalMask;
			Eyes.Layers = ExternalMask;
			Eyebrows.Layers = ExternalMask;
			FirstPersonCamera.CullMask = (oldCameraMask | FirstPersonMask) & ~ExternalMask;
			FirstPersonBody = instance;
			ResultCameraCullMask = FirstPersonCamera.CullMask;
		}
		catch
		{
			FullBody.Layers = oldBodyLayers;
			Eyes.Layers = oldEyesLayers;
			Eyebrows.Layers = oldEyebrowsLayers;
			FirstPersonCamera.CullMask = oldCameraMask;
			instance.QueueFree();
			throw;
		}
	}

	private void AddHeadlessSurface(ArrayMesh source, ArrayMesh headless, int surface, int bindCount)
	{
		if (source.SurfaceGetPrimitiveType(surface) != Mesh.PrimitiveType.Triangles)
			throw new InvalidOperationException($"Surface {surface} is not triangles.");
		Godot.Collections.Array arrays = source.SurfaceGetArrays(surface);
		Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
		int[] bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
		float[] weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
		int[] indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
		int vertexCount = vertices.Length;
		if (vertexCount == 0 || bones.Length != weights.Length ||
			bones.Length % vertexCount != 0 || indices.Length == 0 || indices.Length % 3 != 0)
			throw new InvalidOperationException($"Surface {surface} has an unsupported vertex or index layout.");
		int influences = bones.Length / vertexCount;
		if (influences != 4 && influences != 8)
			throw new InvalidOperationException($"Surface {surface} has {influences} influences per vertex.");

		var hidden = new bool[vertexCount];
		for (int vertex = 0; vertex < vertexCount; vertex++)
		{
			for (int influence = 0; influence < influences; influence++)
			{
				int entry = vertex * influences + influence;
				if (Mathf.IsZeroApprox(weights[entry]))
					continue;
				if (bones[entry] < 0 || bones[entry] >= bindCount)
					throw new InvalidOperationException($"Surface {surface} references an invalid Skin bind.");
				if (Array.IndexOf(HeadBindIndices, bones[entry]) < 0)
					continue;
				hidden[vertex] = true;
				HiddenVertexCount++;
				break;
			}
		}

		var kept = new int[indices.Length];
		int keptCount = 0;
		for (int index = 0; index < indices.Length; index += 3)
		{
			int a = indices[index], b = indices[index + 1], c = indices[index + 2];
			if ((uint)a >= vertexCount || (uint)b >= vertexCount || (uint)c >= vertexCount)
				throw new InvalidOperationException($"Surface {surface} has an out-of-range triangle index.");
			if (hidden[a] || hidden[b] || hidden[c])
				continue;
			kept[keptCount++] = a;
			kept[keptCount++] = b;
			kept[keptCount++] = c;
		}
		SourceVertexCount += vertexCount;
		SourceTriangleCount += indices.Length / 3;
		HeadlessTriangleCount += keptCount / 3;
		if (keptCount == 0)
			throw new InvalidOperationException($"Surface {surface} lost every triangle.");
		Array.Resize(ref kept, keptCount);
		arrays[(int)Mesh.ArrayType.Index] = kept;

		Mesh.ArrayFormat sourceFormat = source.SurfaceGetFormat(surface);
		Mesh.ArrayFormat flags = sourceFormat &
			(Mesh.ArrayFormat.FlagUse8BoneWeights | Mesh.ArrayFormat.FlagUseDynamicUpdate |
			 Mesh.ArrayFormat.FlagCompressAttributes);
		int outputSurface = headless.GetSurfaceCount();
		headless.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null,
			null, flags);
		headless.SurfaceSetName(outputSurface, source.SurfaceGetName(surface));
		headless.SurfaceSetMaterial(outputSurface, source.SurfaceGetMaterial(surface));
	}
}
