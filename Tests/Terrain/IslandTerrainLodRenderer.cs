using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// First-stage whole-island LOD renderer for LowPolyTerrainBuilder.
///
/// Contract:
/// - LowPolyTerrainManager remains the authoritative authoring/heightfield source.
/// - The addon is not modified.
/// - The source heightfield is copied once at startup.
/// - The whole island uses one LOD at a time, so there are no mixed-LOD chunk seams.
/// - LOD0 reuses the addon's exact generated chunk meshes.
/// - LOD1..3 are rebuilt from the same heightfield with progressively coarser sampling.
/// - No runtime mesh rebuild occurs during camera movement; switching only swaps mesh references.
/// - This renderer is visual only. It does not provide terrain collision.
/// </summary>
public partial class IslandTerrainLodRenderer : Node3D
{
    private static readonly int[] LodSteps = { 1, 2, 4, 8 };

    [ExportGroup("Source")]
    [Export] public NodePath SourceTerrainPath { get; set; } = new NodePath();
    [Export] public bool HideSourceTerrainAtRuntime { get; set; } = true;

    [ExportGroup("LOD")]
    [Export] public Vector3 LodDistancesM { get; set; } = new Vector3(50.0f, 120.0f, 250.0f);

    [Export(PropertyHint.Range, "0,100,0.5")]
    public float LodHysteresisM { get; set; } = 10.0f;

    [Export(PropertyHint.Range, "0.02,1.0,0.01")]
    public float LodCheckIntervalS { get; set; } = 0.10f;

    /// <summary>
    /// -1 = automatic. 0..3 = force a specific whole-island LOD.
    /// Useful for profiling.
    /// </summary>
    [Export(PropertyHint.Range, "-1,3,1")]
    public int ForceLod { get; set; } = -1;

    [ExportGroup("Distance Bounds")]

    /// <summary>
    /// Only height samples at or above this local Y contribute to the XZ bounds used
    /// for camera distance. Use ~0 for land-only bounds, or a small negative value
    /// to include the shallow shelf.
    /// </summary>
    [Export]
    public float LodBoundsMinHeightM { get; set; } = -2.0f;

    [ExportGroup("Diagnostics")]
    [Export] public bool PrintDiagnostics { get; set; } = true;

    private Node3D _source;
    private bool _sourceWasVisible;

    private Vector2I _worldChunks;
    private int _chunkSize;
    private float _cellSize;

    private float[] _heights = Array.Empty<float>();
    private byte[] _activity = Array.Empty<byte>();
    private byte[] _paint = Array.Empty<byte>();

    private int _vertexWidth;
    private int _vertexDepth;

    private float _jitterStrength;
    private float _jitterSlopeThreshold;
    private int _sourceShadingMode;

    private Material _baseMaterial;
    private Material _paintOverlay;

    // [lod][chunkIndex]
    private ArrayMesh[][] _lodMeshes = Array.Empty<ArrayMesh[]>();

    // One runtime MeshInstance3D per active chunk.
    private MeshInstance3D[] _chunkInstances = Array.Empty<MeshInstance3D>();

    private bool[] _chunkActive = Array.Empty<bool>();

    private int _currentLod = -1;
    private double _lodTimer;

    // Local XZ bounds around the island/shallow shelf used for LOD distance.
    private bool _hasLodBounds;
    private float _boundsMinX;
    private float _boundsMaxX;
    private float _boundsMinZ;
    private float _boundsMaxZ;

    public override async void _Ready()
    {
        // Let sibling LowPolyTerrainManager finish _Ready() and build its SERVERS meshes first.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        InitializeFromSource();
    }

    public override void _Process(double delta)
    {
        if (_source == null || _lodMeshes.Length == 0)
        {
            return;
        }

        _lodTimer += delta;
        if (_lodTimer < LodCheckIntervalS)
        {
            return;
        }

        _lodTimer = 0.0;

        int requestedLod;
        if (ForceLod >= 0)
        {
            requestedLod = Math.Clamp(ForceLod, 0, LodSteps.Length - 1);
        }
        else
        {
            Camera3D camera = GetViewport()?.GetCamera3D();
            if (camera == null)
            {
                return;
            }

            float distanceM = GetHorizontalDistanceToLodBounds(camera.GlobalPosition);
            requestedLod = SelectLodWithHysteresis(distanceM);
        }

        if (requestedLod != _currentLod)
        {
            ApplyLod(requestedLod);
        }
    }

    public override void _ExitTree()
    {
        if (_source != null && GodotObject.IsInstanceValid(_source))
        {
            _source.Visible = _sourceWasVisible;
        }
    }

    private void InitializeFromSource()
    {
        if (SourceTerrainPath == null || SourceTerrainPath.IsEmpty)
        {
            GD.PushError($"{Name}: SourceTerrainPath is empty.");
            return;
        }

        _source = GetNodeOrNull<Node3D>(SourceTerrainPath);
        if (_source == null)
        {
            GD.PushError($"{Name}: LowPolyTerrainManager not found at '{SourceTerrainPath}'.");
            return;
        }

        ulong startMs = Time.GetTicksMsec();

        if (!ReadSourceSnapshot())
        {
            return;
        }

        // The C# renderer occupies the same world transform as the authoring manager.
        GlobalTransform = _source.GlobalTransform;

        BuildDistanceBounds();
        CaptureMaterials();

        if (!BuildAllLods())
        {
            return;
        }

        CreateRuntimeChunkInstances();

        // Apply an initial LOD before hiding the source.
        int initialLod = ForceLod >= 0
            ? Math.Clamp(ForceLod, 0, LodSteps.Length - 1)
            : 0;

        ApplyLod(initialLod);

        _sourceWasVisible = _source.Visible;
        if (HideSourceTerrainAtRuntime)
        {
            _source.Visible = false;
        }

        if (PrintDiagnostics)
        {
            ulong elapsedMs = Time.GetTicksMsec() - startMs;
            int activeChunks = 0;
            foreach (bool active in _chunkActive)
            {
                if (active) activeChunks++;
            }

            GD.Print(
                $"{Name}: island LOD ready | " +
                $"{_vertexWidth}x{_vertexDepth} heights | " +
                $"{activeChunks}/{_chunkActive.Length} active chunks | " +
                $"cell {_cellSize:0.###} m | build {elapsedMs} ms"
            );
        }
    }

    private bool ReadSourceSnapshot()
    {
        try
        {
            _worldChunks = _source.Get("world_chunks").AsVector2I();
            _chunkSize = _source.Get("chunk_size").AsInt32();
            _cellSize = _source.Get("cell_size").AsSingle();

            _heights = _source.Get("global_height_data").AsFloat32Array();
            _activity = _source.Get("chunk_activity_data").AsByteArray();
            _paint = _source.Get("global_paint_data").AsByteArray();

            _jitterStrength = _source.Get("jitter_strength").AsSingle();
            _jitterSlopeThreshold = _source.Get("jitter_slope_threshold").AsSingle();
            _sourceShadingMode = _source.Get("shading_mode").AsInt32();
        }
        catch (Exception e)
        {
            GD.PushError($"{Name}: failed to read LowPolyTerrainManager data: {e.Message}");
            return false;
        }

        if (_worldChunks.X <= 0 || _worldChunks.Y <= 0)
        {
            GD.PushError($"{Name}: invalid world_chunks {_worldChunks}.");
            return false;
        }

        if (_chunkSize <= 0 || _cellSize <= 0.0f)
        {
            GD.PushError($"{Name}: invalid chunk_size/cell_size.");
            return false;
        }

        _vertexWidth = (_worldChunks.X * _chunkSize) + 1;
        _vertexDepth = (_worldChunks.Y * _chunkSize) + 1;

        long expectedHeights = (long)_vertexWidth * _vertexDepth;
        if (_heights.LongLength != expectedHeights)
        {
            GD.PushError(
                $"{Name}: heightfield size mismatch. Expected {expectedHeights}, got {_heights.LongLength}."
            );
            return false;
        }

        int chunkCount = _worldChunks.X * _worldChunks.Y;
        _chunkActive = new bool[chunkCount];

        for (int i = 0; i < chunkCount; i++)
        {
            // LowPolyTerrainManager treats missing activity entries as active.
            _chunkActive[i] = i >= _activity.Length || _activity[i] != 0;
        }

        if (_sourceShadingMode != 0 && PrintDiagnostics)
        {
            GD.PushWarning(
                $"{Name}: source ShadingMode is not FLAT. " +
                "LOD0 keeps the source mesh exactly, but generated LOD1..3 currently use flat face normals."
            );
        }

        return true;
    }

    private void CaptureMaterials()
    {
        _baseMaterial = _source.Get("custom_material").AsGodotObject() as Material;

        try
        {
            _paintOverlay = _source.Call("get_active_paint_material").AsGodotObject() as Material;
        }
        catch
        {
            _paintOverlay = null;
        }
    }

    private void BuildDistanceBounds()
    {
        _hasLodBounds = false;

        for (int gz = 0; gz < _vertexDepth; gz++)
        {
            for (int gx = 0; gx < _vertexWidth; gx++)
            {
                int cx = Math.Min(gx / _chunkSize, _worldChunks.X - 1);
                int cz = Math.Min(gz / _chunkSize, _worldChunks.Y - 1);

                if (!IsChunkActive(cx, cz))
                {
                    continue;
                }

                float h = HeightAt(gx, gz);
                if (h < LodBoundsMinHeightM)
                {
                    continue;
                }

                float x = gx * _cellSize;
                float z = -gz * _cellSize;

                if (!_hasLodBounds)
                {
                    _boundsMinX = _boundsMaxX = x;
                    _boundsMinZ = _boundsMaxZ = z;
                    _hasLodBounds = true;
                }
                else
                {
                    _boundsMinX = Mathf.Min(_boundsMinX, x);
                    _boundsMaxX = Mathf.Max(_boundsMaxX, x);
                    _boundsMinZ = Mathf.Min(_boundsMinZ, z);
                    _boundsMaxZ = Mathf.Max(_boundsMaxZ, z);
                }
            }
        }

        if (_hasLodBounds)
        {
            return;
        }

        // Fallback: whole terrain rectangle.
        _boundsMinX = 0.0f;
        _boundsMaxX = (_vertexWidth - 1) * _cellSize;
        _boundsMaxZ = 0.0f;
        _boundsMinZ = -(_vertexDepth - 1) * _cellSize;
        _hasLodBounds = true;
    }

    private bool BuildAllLods()
    {
        int chunkCount = _worldChunks.X * _worldChunks.Y;
        _lodMeshes = new ArrayMesh[LodSteps.Length][];

        for (int lod = 0; lod < LodSteps.Length; lod++)
        {
            _lodMeshes[lod] = new ArrayMesh[chunkCount];
        }

        for (int cz = 0; cz < _worldChunks.Y; cz++)
        {
            for (int cx = 0; cx < _worldChunks.X; cx++)
            {
                int chunkIndex = ChunkIndex(cx, cz);
                if (!_chunkActive[chunkIndex])
                {
                    continue;
                }

                Vector2I coord = new Vector2I(cx, cz);

                // LOD0: preserve the addon's exact Delaunay/decimation/jitter result.
                ArrayMesh sourceMesh = null;
                try
                {
                    sourceMesh = _source.Call("get_chunk_mesh", coord).AsGodotObject() as ArrayMesh;
                }
                catch (Exception e)
                {
                    GD.PushError($"{Name}: get_chunk_mesh({coord}) failed: {e.Message}");
                    return false;
                }

                if (sourceMesh == null)
                {
                    GD.PushWarning($"{Name}: active source chunk {coord} has no mesh.");
                }

                _lodMeshes[0][chunkIndex] = sourceMesh;

                for (int lod = 1; lod < LodSteps.Length; lod++)
                {
                    _lodMeshes[lod][chunkIndex] = BuildGeneratedChunkMesh(
                        cx,
                        cz,
                        LodSteps[lod]
                    );
                }
            }
        }

        return true;
    }

    private ArrayMesh BuildGeneratedChunkMesh(int chunkX, int chunkZ, int step)
    {
        List<int> sampleCoords = BuildSampleCoordinates(step);
        int sampleCount = sampleCoords.Count;

        var points2D = new List<Vector2>(sampleCount * sampleCount);
        var points3D = new List<Vector3>(sampleCount * sampleCount);
        var pointsColor = new List<Color>(sampleCount * sampleCount);

        int baseGx = chunkX * _chunkSize;
        int baseGz = chunkZ * _chunkSize;

        for (int zi = 0; zi < sampleCount; zi++)
        {
            int localZ = sampleCoords[zi];

            for (int xi = 0; xi < sampleCount; xi++)
            {
                int localX = sampleCoords[xi];
                int gx = baseGx + localX;
                int gz = baseGz + localZ;

                bool edge =
                    localX == 0 || localX == _chunkSize ||
                    localZ == 0 || localZ == _chunkSize;

                bool corner =
                    (localX == 0 || localX == _chunkSize) &&
                    (localZ == 0 || localZ == _chunkSize);

                float currentH = HeightAt(gx, gz);

                bool flatCenter = false;
                if (!edge && xi > 0 && xi + 1 < sampleCount && zi > 0 && zi + 1 < sampleCount)
                {
                    int leftX = baseGx + sampleCoords[xi - 1];
                    int rightX = baseGx + sampleCoords[xi + 1];
                    int upZ = baseGz + sampleCoords[zi - 1];
                    int downZ = baseGz + sampleCoords[zi + 1];

                    flatCenter =
                        SameHeight(currentH, HeightAt(leftX, gz)) &&
                        SameHeight(currentH, HeightAt(rightX, gz)) &&
                        SameHeight(currentH, HeightAt(gx, upZ)) &&
                        SameHeight(currentH, HeightAt(gx, downZ));

                    if (flatCenter && HasPaintData())
                    {
                        flatCenter =
                            SamePaint(gx, gz, leftX, gz) &&
                            SamePaint(gx, gz, rightX, gz) &&
                            SamePaint(gx, gz, gx, upZ) &&
                            SamePaint(gx, gz, gx, downZ);
                    }
                }

                if (flatCenter)
                {
                    continue;
                }

                bool flatEdge = false;
                if (edge && !corner)
                {
                    if ((localZ == 0 || localZ == _chunkSize) && xi > 0 && xi + 1 < sampleCount)
                    {
                        int leftX = baseGx + sampleCoords[xi - 1];
                        int rightX = baseGx + sampleCoords[xi + 1];

                        flatEdge =
                            SameHeight(currentH, HeightAt(leftX, gz)) &&
                            SameHeight(currentH, HeightAt(rightX, gz));

                        if (flatEdge && HasPaintData())
                        {
                            flatEdge =
                                SamePaint(gx, gz, leftX, gz) &&
                                SamePaint(gx, gz, rightX, gz);
                        }

                        if (flatEdge && xi % 4 != 0)
                        {
                            continue;
                        }
                    }
                    else if ((localX == 0 || localX == _chunkSize) && zi > 0 && zi + 1 < sampleCount)
                    {
                        int upZ = baseGz + sampleCoords[zi - 1];
                        int downZ = baseGz + sampleCoords[zi + 1];

                        flatEdge =
                            SameHeight(currentH, HeightAt(gx, upZ)) &&
                            SameHeight(currentH, HeightAt(gx, downZ));

                        if (flatEdge && HasPaintData())
                        {
                            flatEdge =
                                SamePaint(gx, gz, gx, upZ) &&
                                SamePaint(gx, gz, gx, downZ);
                        }

                        if (flatEdge && zi % 4 != 0)
                        {
                            continue;
                        }
                    }
                }

                Vector3 jitter = ComputeSourceCompatibleJitter(
                    chunkX,
                    chunkZ,
                    localX,
                    localZ,
                    gx,
                    gz,
                    currentH
                );

                float px = localX * _cellSize + jitter.X;
                float pz = -localZ * _cellSize + jitter.Z;

                points2D.Add(new Vector2(px, pz));
                points3D.Add(new Vector3(px, currentH, pz));
                pointsColor.Add(PaintColorAt(gx, gz));
            }
        }

        if (points2D.Count < 3)
        {
            return null;
        }

        int[] triangles = Geometry2D.TriangulateDelaunay(points2D.ToArray());
        if (triangles == null || triangles.Length < 3)
        {
            return null;
        }

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        float chunkMeters = _chunkSize * _cellSize;

        for (int i = 0; i + 2 < triangles.Length; i += 3)
        {
            int i0 = triangles[i];
            int i1 = triangles[i + 1];
            int i2 = triangles[i + 2];

            Vector2 a2 = points2D[i0];
            Vector2 b2 = points2D[i1];
            Vector2 c2 = points2D[i2];

            Vector2 e1 = b2 - a2;
            Vector2 e2 = c2 - a2;
            float cross2D = e1.X * e2.Y - e1.Y * e2.X;

            // Match the addon's winding rule:
            // positive XZ 2D cross => negative 3D cross.Y => Godot front-facing top surface.
            if (cross2D < 0.0f)
            {
                (i1, i2) = (i2, i1);
            }

            Vector3 p0 = points3D[i0];
            Vector3 p1 = points3D[i1];
            Vector3 p2 = points3D[i2];

            Vector3 geometric = (p1 - p0).Cross(p2 - p0);
            if (geometric.LengthSquared() < 0.0000001f)
            {
                continue;
            }

            // Winding is intentionally opposite the lighting normal.
            Vector3 normal = -geometric.Normalized();
            if (normal.Y < 0.0f)
            {
                normal = -normal;
            }

            AddFlatVertex(st, p0, normal, pointsColor[i0], chunkMeters);
            AddFlatVertex(st, p1, normal, pointsColor[i1], chunkMeters);
            AddFlatVertex(st, p2, normal, pointsColor[i2], chunkMeters);
        }

        st.GenerateTangents();
        st.Index();
        return st.Commit();
    }

    private void AddFlatVertex(
        SurfaceTool st,
        Vector3 position,
        Vector3 normal,
        Color color,
        float chunkMeters
    )
    {
        st.SetNormal(normal);
        st.SetColor(color);
        st.SetUV(new Vector2(
            position.X / chunkMeters,
            -position.Z / chunkMeters
        ));
        st.AddVertex(position);
    }

    private List<int> BuildSampleCoordinates(int step)
    {
        var coords = new List<int>();

        for (int value = 0; value < _chunkSize; value += step)
        {
            coords.Add(value);
        }

        if (coords.Count == 0 || coords[^1] != _chunkSize)
        {
            coords.Add(_chunkSize);
        }

        return coords;
    }

    private Vector3 ComputeSourceCompatibleJitter(
        int chunkX,
        int chunkZ,
        int localX,
        int localZ,
        int gx,
        int gz,
        float currentH
    )
    {
        bool edge =
            localX == 0 || localX == _chunkSize ||
            localZ == 0 || localZ == _chunkSize;

        if (edge || Mathf.IsZeroApprox(_jitterStrength))
        {
            return Vector3.Zero;
        }

        int rightLocal = Math.Min(localX + 1, _chunkSize);
        int leftLocal = Math.Max(localX - 1, 0);
        int downLocal = Math.Min(localZ + 1, _chunkSize);
        int upLocal = Math.Max(localZ - 1, 0);

        int baseGx = chunkX * _chunkSize;
        int baseGz = chunkZ * _chunkSize;

        float hRight = HeightAt(baseGx + rightLocal, gz);
        float hLeft = HeightAt(baseGx + leftLocal, gz);
        float hDown = HeightAt(gx, baseGz + downLocal);
        float hUp = HeightAt(gx, baseGz + upLocal);

        float diffX = Mathf.Max(
            Mathf.Abs(currentH - hRight),
            Mathf.Abs(currentH - hLeft)
        );

        float diffZ = Mathf.Max(
            Mathf.Abs(currentH - hDown),
            Mathf.Abs(currentH - hUp)
        );

        float maxDiff = Mathf.Max(diffX, diffZ);
        float trueSlope = maxDiff / _cellSize;

        float threshold = Mathf.IsZeroApprox(_jitterSlopeThreshold)
            ? 0.5f
            : _jitterSlopeThreshold;

        float t = Mathf.Clamp(trueSlope / threshold, 0.0f, 1.0f);
        float slopeFactor = t * t * (3.0f - 2.0f * t);

        float distToEdgeX = Mathf.Min(localX, _chunkSize - localX);
        float distToEdgeZ = Mathf.Min(localZ, _chunkSize - localZ);
        float edgeDamp = Mathf.Clamp(
            Mathf.Min(distToEdgeX, distToEdgeZ) / 2.0f,
            0.0f,
            1.0f
        );

        // Exact hash constants used by LowPolyTerrainMeshBuilder.get_jitter_offset().
        float hashX =
            Mathf.Sin(gx * 12.9898f + gz * 78.233f) * 43758.5453f;
        float hashZ =
            Mathf.Sin(gx * 37.719f + gz * 11.135f) * 43758.5453f;

        float randomX = (hashX - Mathf.Floor(hashX)) * 2.0f - 1.0f;
        float randomZ = (hashZ - Mathf.Floor(hashZ)) * 2.0f - 1.0f;

        Vector3 raw = new Vector3(
            randomX * _cellSize * _jitterStrength,
            0.0f,
            -randomZ * _cellSize * _jitterStrength
        );

        return raw * slopeFactor * edgeDamp;
    }

    private void CreateRuntimeChunkInstances()
    {
        int chunkCount = _worldChunks.X * _worldChunks.Y;
        _chunkInstances = new MeshInstance3D[chunkCount];

        float chunkMeters = _chunkSize * _cellSize;

        for (int cz = 0; cz < _worldChunks.Y; cz++)
        {
            for (int cx = 0; cx < _worldChunks.X; cx++)
            {
                int index = ChunkIndex(cx, cz);
                if (!_chunkActive[index])
                {
                    continue;
                }

                var instance = new MeshInstance3D
                {
                    Name = $"IslandChunk_{cx}_{cz}",
                    Position = new Vector3(
                        cx * chunkMeters,
                        0.0f,
                        -cz * chunkMeters
                    )
                };

                if (_baseMaterial != null)
                {
                    instance.MaterialOverride = _baseMaterial;
                }

                if (_paintOverlay != null)
                {
                    instance.MaterialOverlay = _paintOverlay;
                }

                AddChild(instance);
                _chunkInstances[index] = instance;
            }
        }
    }

    private void ApplyLod(int lod)
    {
        lod = Math.Clamp(lod, 0, LodSteps.Length - 1);

        for (int i = 0; i < _chunkInstances.Length; i++)
        {
            MeshInstance3D instance = _chunkInstances[i];
            if (instance == null)
            {
                continue;
            }

            instance.Mesh = _lodMeshes[lod][i];
            instance.Visible = instance.Mesh != null;
        }

        _currentLod = lod;

        if (PrintDiagnostics)
        {
            GD.Print(
                $"{Name}: LOD{lod} active " +
                $"(source sampling step {LodSteps[lod]}, effective grid {_cellSize * LodSteps[lod]:0.###} m)"
            );
        }
    }

    private int SelectLodWithHysteresis(float distanceM)
    {
        float[] thresholds =
        {
            LodDistancesM.X,
            LodDistancesM.Y,
            LodDistancesM.Z
        };

        if (_currentLod < 0)
        {
            if (distanceM > thresholds[2]) return 3;
            if (distanceM > thresholds[1]) return 2;
            if (distanceM > thresholds[0]) return 1;
            return 0;
        }

        int lod = _currentLod;

        // Moving away: require crossing threshold + hysteresis.
        while (lod < 3 && distanceM > thresholds[lod] + LodHysteresisM)
        {
            lod++;
        }

        // Moving closer: require crossing threshold - hysteresis.
        while (lod > 0 && distanceM < thresholds[lod - 1] - LodHysteresisM)
        {
            lod--;
        }

        return lod;
    }

    private float GetHorizontalDistanceToLodBounds(Vector3 cameraWorldPosition)
    {
        Vector3 local = ToLocal(cameraWorldPosition);

        float dx = 0.0f;
        if (local.X < _boundsMinX)
        {
            dx = _boundsMinX - local.X;
        }
        else if (local.X > _boundsMaxX)
        {
            dx = local.X - _boundsMaxX;
        }

        float dz = 0.0f;
        if (local.Z < _boundsMinZ)
        {
            dz = _boundsMinZ - local.Z;
        }
        else if (local.Z > _boundsMaxZ)
        {
            dz = local.Z - _boundsMaxZ;
        }

        // Current island integration keeps terrain scale at 1.
        // This is intentionally XZ-only: altitude should not force a lower island LOD.
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private bool IsChunkActive(int cx, int cz)
    {
        if (cx < 0 || cx >= _worldChunks.X || cz < 0 || cz >= _worldChunks.Y)
        {
            return false;
        }

        int index = ChunkIndex(cx, cz);
        return index >= 0 && index < _chunkActive.Length && _chunkActive[index];
    }

    private int ChunkIndex(int cx, int cz)
    {
        return cz * _worldChunks.X + cx;
    }

    private float HeightAt(int gx, int gz)
    {
        gx = Math.Clamp(gx, 0, _vertexWidth - 1);
        gz = Math.Clamp(gz, 0, _vertexDepth - 1);
        return _heights[gz * _vertexWidth + gx];
    }

    private bool HasPaintData()
    {
        long expected = (long)_vertexWidth * _vertexDepth * 4L;
        return _paint.LongLength >= expected;
    }

    private Color PaintColorAt(int gx, int gz)
    {
        if (!HasPaintData())
        {
            return new Color(0, 0, 0, 0);
        }

        int baseIndex = (gz * _vertexWidth + gx) * 4;
        const float scale = 1.0f / 31.0f; // LowPolyTerrainManager.PAINT_STEPS - 1

        return new Color(
            _paint[baseIndex] * scale,
            _paint[baseIndex + 1] * scale,
            _paint[baseIndex + 2] * scale,
            _paint[baseIndex + 3] * scale
        );
    }

    private bool SamePaint(int ax, int az, int bx, int bz)
    {
        if (!HasPaintData())
        {
            return true;
        }

        int a = (az * _vertexWidth + ax) * 4;
        int b = (bz * _vertexWidth + bx) * 4;

        return
            _paint[a] == _paint[b] &&
            _paint[a + 1] == _paint[b + 1] &&
            _paint[a + 2] == _paint[b + 2] &&
            _paint[a + 3] == _paint[b + 3];
    }

    private static bool SameHeight(float a, float b)
    {
        return Mathf.IsEqualApprox(a, b);
    }
}
