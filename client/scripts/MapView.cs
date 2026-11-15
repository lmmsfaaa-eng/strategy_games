using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using IH.Shared;
using Resource = IH.Shared.Resource;
using TileData = IH.Shared.TileData;

namespace IH.Client;

/// <summary>
/// 3D map renderer and map input. Terrain is built into static chunk meshes (rebuilt only when their tiles
/// change); decorations use distance-based visibility as level of detail; units are separate nodes that are
/// interpolated between tiles, with client-side prediction of the player's own moves.
/// </summary>
public partial class MapView : Node3D
{
    public const float R = 1f;
    public const float RowH = 1.5f * R;
    public static readonly float HexW = Mathf.Sqrt(3f) * R;
    private const int ChunkSize = 12;
    private const float MoveSpeed = 9f;
    private const float BaseY = -0.6f, BaseDistance = 20f;
    private static readonly float Pitch = Mathf.DegToRad(52);

    private static readonly Vector3[] Corner = Enumerable.Range(0, 6)
        .Select(k => new Vector3(Mathf.Cos(Mathf.DegToRad(60 * k - 30)), 0, Mathf.Sin(Mathf.DegToRad(60 * k - 30))) * R).ToArray();
    // directions E, NE, NW, W, SW, SE and the corner pair forming the shared edge
    private static readonly (int dx, int dy)[] EvenRow = { (1, 0), (0, -1), (-1, -1), (-1, 0), (-1, 1), (0, 1) };
    private static readonly (int dx, int dy)[] OddRow = { (1, 0), (1, -1), (0, -1), (-1, 0), (0, 1), (1, 1) };
    // direction whose shared edge runs from corner k to corner k+1
    private static readonly int[] DirOfCorner = { 0, 5, 4, 3, 2, 1 };
    private static readonly (int a, int b)[] Edge = { (0, 1), (5, 0), (4, 5), (3, 4), (2, 3), (1, 2) };

    public readonly ClientWorld W;
    private readonly GameScreen _screen;
    private readonly Camera3D _camera = new() { Fov = 42, Near = 0.5f, Far = 600 };
    private readonly Godot.Environment _environment;
    private readonly bool _forwardPlus;
    private readonly Chunk[] _chunks;
    private Vector2 _viewMin, _viewMax;
    private readonly int _chunkCols;
    private readonly HashSet<int> _dirtyChunks = new();
    private readonly Dictionary<int, UnitNode> _units = new();
    private readonly Dictionary<int, CityNode> _cities = new();
    private readonly Dictionary<int, int> _moveSeq = new();
    private readonly List<(int tile, double until, Color color)> _flashes = new();
    private readonly ImmediateMesh _lines = new();
    private readonly ImmediateMesh _fogTransitionMesh = new();
    private readonly List<(int Tile, double End)> _fogTransitions = new();
    private readonly byte[] _lastVisibility;
    private bool _fogWasDrawn;
    private readonly StandardMaterial3D _terrainMat = new() { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 1f };
    private readonly Mesh _ringMesh = new TorusMesh { InnerRadius = 0.5f, OuterRadius = 0.62f, Rings = 24, RingSegments = 6 };
    private readonly StandardMaterial3D _ringMat = new() { AlbedoColor = new Color(1f, 0.85f, 0.25f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };

    /// <summary>Zoom factor: 1 = default distance, smaller = further away.</summary>
    public float Zoom = 1f;
    /// <summary>Ground point the camera looks at, in map units (x, z).</summary>
    public Vector2 Cam;
    public float Yaw;
    public int Lod = 2;
    public int SelectedUnit = -1, HoverTile = -1;
    public List<int>? HoverPath;
    public int HoverTurns { get; private set; }
    public int[] Highlight = Array.Empty<int>();
    private HashSet<int>? _cityFocus;

    public void FocusCity(CityDto? city)
    {
        var tiles = city == null ? null : W.Grid.Range(city.Tile, city.Radius)
            .Where(t => W.Vis[t] > 0 && W.Tiles[t].Owner == city.Owner)
            .Concat(city.Detail?.Worked ?? Array.Empty<int>()).ToHashSet();
        if (tiles == null ? _cityFocus == null : _cityFocus != null && tiles.SetEquals(_cityFocus)) return;
        _cityFocus = tiles;
        for (int i = 0; i < _chunks.Length; i++) _dirtyChunks.Add(i);
        _dirty = true;
    }
    public int UnitProjectionRevision { get; private set; }
    private bool _dirty = true, _layoutDirty, _dragging, _pressed;
    private Vector2 _pressPos;

    private sealed class Chunk
    {
        public Mesh? Terrain, Decor;
        public readonly List<(MeshInstance3D Terrain, MeshInstance3D Decor)> Images = new();
        public int X0, Y0;
    }

    private sealed class UnitNode
    {
        public Node3D Root = null!;
        public MeshInstance3D Body = null!, Ring = null!;
        public float BodyTop;
        public float MeshRadius, MeshHeight;
        public Vector2 MeshCenter;
        public int Type = -1, Owner = -1;
        public Vector3 Pos;
        public readonly Queue<Vector3> Path = new();
        public readonly List<Node3D> Copies = new();
        public int Tile, Predicted = -1;
        public double Deadline;
    }

    private sealed class CityNode
    {
        public Node3D Root = null!;
        public MeshInstance3D Body = null!;
        public string Look = "";
        public readonly List<Node3D> Copies = new();
    }

    public MapView(GameScreen screen, ClientWorld world)
    {
        _screen = screen; W = world;
        _lastVisibility = new byte[W.Grid.Count];
        AddChild(new MeshInstance3D
        {
            Mesh = _fogTransitionMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                VertexColorUseAsAlbedo = true,
            },
        });
        _chunkCols = (W.Grid.W + ChunkSize - 1) / ChunkSize;
        int rows = (W.Grid.H + ChunkSize - 1) / ChunkSize;
        _chunks = new Chunk[_chunkCols * rows];
        for (int cy = 0; cy < rows; cy++)
            for (int cx = 0; cx < _chunkCols; cx++)
            {
                _chunks[cy * _chunkCols + cx] = new Chunk { X0 = cx * ChunkSize, Y0 = cy * ChunkSize };
            }

        AddChild(_camera);
        _forwardPlus = RenderingServer.GetCurrentRenderingMethod() == "forward_plus";
        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-47, -36, 0), LightColor = Ui.Col(0xFFF0DA),
            LightEnergy = 0.93f, ShadowEnabled = _forwardPlus, LightAngularDistance = 1.4f,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            DirectionalShadowBlendSplits = true, DirectionalShadowMaxDistance = 92f,
        });
        _environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = Ui.Col(0x94AAB3),
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Ui.Col(0xE0ECEA),
            AmbientLightEnergy = 0.63f, ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled,
            SsaoEnabled = _forwardPlus, SsaoRadius = 0.42f, SsaoIntensity = 1.05f, SsaoLightAffect = 0.25f,
            FogEnabled = true, FogMode = Godot.Environment.FogModeEnum.Depth, FogLightColor = Ui.Col(0xA2B5BD),
            FogDepthBegin = 25f, FogDepthEnd = 230f, FogDensity = 0.62f, FogDepthCurve = 1.1f,
        };
        AddChild(new WorldEnvironment { Environment = _environment });
        AddChild(new MeshInstance3D
        {
            Mesh = _lines,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, NoDepthTest = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            },
        });
        Cam = Ground(W.Grid.Count / 2);
    }

    // ----------------------------------------------------------- geometry
    private Vector2 Ground(int idx)
    {
        int x = idx % W.Grid.W, y = idx / W.Grid.W;
        return new Vector2(HexW * (x + 0.5f * (y & 1)), RowH * y);
    }

    public static float Height(in TileData t)
    {
        if (t.Terrain == Terrain.Ocean) return -0.16f;
        if (t.Terrain == Terrain.Coast) return -0.08f;
        return t.Relief switch { Relief.Hills => 0.34f, Relief.Mountain => 0.42f, _ => 0.12f };
    }

    private Vector3 CanonicalCenter(int idx)
    {
        var g = Ground(idx);
        return new Vector3(g.X, Height(W.Tiles[idx]), g.Y);
    }

    /// <summary>The map image nearest the camera, or nearest a route point when drawing a contiguous path.</summary>
    public Vector3 Center(int idx)
    {
        var focus = (_viewMin + _viewMax) * 0.5f;
        return NearestCenter(idx, new Vector3(focus.X, 0, focus.Y));
    }

    public Vector3 NearestCenter(int idx, Vector3 reference) => NearestImage(CanonicalCenter(idx), reference);

    private Vector3 NearestImage(Vector3 point, Vector3 reference)
    {
        var period = MapPixelSize;
        int row = Mathf.RoundToInt((reference.Z - point.Z) / period.Y);
        Vector3 best = point;
        float bestDistance = float.MaxValue;
        for (int y = row - 1; y <= row + 1; y++)
        {
            int x = Mathf.RoundToInt((reference.X - point.X - y * SeamShear) / period.X);
            Vector2 offset = PeriodOffset(x, y);
            var image = point + new Vector3(offset.X, 0, offset.Y);
            float distance = reference.DistanceSquaredTo(image);
            if (distance < bestDistance) { bestDistance = distance; best = image; }
        }
        return best;
    }

    private int Neighbor(int idx, int dir)
    {
        int x = idx % W.Grid.W, y = idx / W.Grid.W;
        var (dx, dy) = (y & 1) == 0 ? EvenRow[dir] : OddRow[dir];
        return W.Grid.WrapIndex(x + dx, y + dy);
    }

    private int TileAtGround(Vector2 p)
    {
        int row = Mathf.RoundToInt(p.Y / RowH);
        int col = Mathf.RoundToInt(p.X / HexW - 0.5f * (row & 1));
        int best = W.Grid.WrapIndex(col, row);
        var reference = new Vector3(p.X, 0, p.Y);
        float DistanceToImage(int tile)
        {
            var image = NearestCenter(tile, reference);
            return new Vector2(image.X, image.Z).DistanceSquaredTo(p);
        }
        float bestD = DistanceToImage(best);
        foreach (int n in W.Grid.Nb[best])
        {
            float d = DistanceToImage(n);
            if (d < bestD) { bestD = d; best = n; }
        }
        return bestD <= R * R ? best : -1;
    }

    /// <summary>Tile under a screen position: a ray from the camera is intersected with the terrain height.</summary>
    public int TileAt(Vector2 screen)
    {
        var origin = _camera.ProjectRayOrigin(screen);
        var dir = _camera.ProjectRayNormal(screen);
        if (dir.Y > -0.01f) return -1;
        float h = 0.12f;
        int tile = -1;
        for (int pass = 0; pass < 3; pass++)
        {
            var hit = origin + dir * ((h - origin.Y) / dir.Y);
            tile = TileAtGround(new Vector2(hit.X, hit.Z));
            if (tile < 0) return -1;
            float th = Height(W.Tiles[tile]);
            if (Mathf.Abs(th - h) < 0.01f) break;
            h = th;
        }
        return tile;
    }

    public Vector2 MapPixelSize => new(HexW * W.Grid.W, RowH * W.Grid.H);
    private float SeamShear => (W.Grid.H & 1) * HexW * 0.5f;
    public Vector2 PeriodOffset(int x, int y) => new(x * MapPixelSize.X + y * SeamShear, y * MapPixelSize.Y);
    private float Distance => BaseDistance / Zoom;
    /// <summary>Approximate size of the ground area in view, for the minimap frame.</summary>
    public Vector2 ViewSize => new(Distance * 1.25f, Distance * 0.95f);

    public Vector2 ScreenPos(int tile) => _camera.UnprojectPosition(Center(tile) + new Vector3(0, 0.3f, 0));

    /// <summary>Screen position of a point lifted above a tile; false if it is behind the camera.</summary>
    public bool Project(int tile, float lift, out Vector2 screen)
    {
        var p = Center(tile) + new Vector3(0, lift, 0);
        screen = _camera.UnprojectPosition(p);
        return !_camera.IsPositionBehind(p);
    }

    internal bool ProjectHovered(float lift, out Vector2 screen)
    {
        var p = HoveredCenter() + Vector3.Up * lift;
        screen = _camera.UnprojectPosition(p);
        return !_camera.IsPositionBehind(p);
    }

    private Vector3 HoveredCenter()
    {
        var mouse = GetViewport().GetMousePosition();
        var direction = _camera.ProjectRayNormal(mouse);
        if (direction.Y > -0.01f) return Center(HoverTile);
        var origin = _camera.ProjectRayOrigin(mouse);
        var hit = origin + direction * ((Height(W.Tiles[HoverTile]) - origin.Y) / direction.Y);
        return NearestCenter(HoverTile, hit);
    }

    internal IEnumerable<Vector2> ProjectTileImages(int tile, float lift)
    {
        var origin = CanonicalCenter(tile) + Vector3.Up * lift;
        var ground = new Vector2(origin.X, origin.Z);
        foreach (var repeat in VisiblePeriods(ground - Vector2.One, ground + Vector2.One))
        {
            var offset = PeriodOffset(repeat.X, repeat.Y);
            var world = origin + new Vector3(offset.X, 0, offset.Y);
            if (!_camera.IsPositionBehind(world)) yield return _camera.UnprojectPosition(world);
        }
    }

    public bool ProjectUnit(int unitId, out Vector2 screen, out bool moving)
    {
        screen = Vector2.Zero;
        moving = false;
        if (!_units.TryGetValue(unitId, out var n) || !n.Root.Visible) return false;
        var point = n.Root.Position + Vector3.Up * (n.BodyTop + 0.02f);
        if (_camera.IsPositionBehind(point)) return false;
        screen = _camera.UnprojectPosition(point);
        moving = n.Path.Count != 0 || n.Predicted >= 0;
        return true;
    }

    internal IEnumerable<(Vector2I Copy, Vector2 Screen, bool Moving)> ProjectUnitImages(UnitDto unit)
    {
        if (!_units.TryGetValue(unit.Id, out var n)) yield break;
        var ground = Ground(unit.Tile);
        bool moving = n.Path.Count > 0 || n.Predicted >= 0;
        foreach (var instance in n.Copies.Prepend(n.Root))
        {
            if (!instance.Visible) continue;
            var p = instance.Position + Vector3.Up * (n.BodyTop + 0.02f);
            if (_camera.IsPositionBehind(p)) continue;
            int y = Mathf.RoundToInt((p.Z - ground.Y) / MapPixelSize.Y);
            int x = Mathf.RoundToInt((p.X - ground.X - y * SeamShear) / MapPixelSize.X);
            yield return (new Vector2I(x, y), _camera.UnprojectPosition(p), moving);
        }
    }

    public void CenterOn(int tile) { Cam = Ground(tile); ApplyCamera(); }

    /// <summary>Centres on a tile but leaves it shifted on screen: a positive X puts the tile left of the middle.</summary>
    public void CenterOn(int tile, Vector2 screenShift)
    {
        float s = Distance * 0.0016f;
        Cam = Ground(tile) + RightDir * screenShift.X * s - ForwardDir * screenShift.Y * s * 1.5f;
        ApplyCamera();
    }

    public bool OnScreen(int tile)
    {
        var world = Center(tile);
        if (_camera.IsPositionBehind(world)) return false;
        var p = _camera.UnprojectPosition(world);
        var size = GetViewport().GetVisibleRect().Size;
        return p.X > 80 && p.Y > 80 && p.X < size.X - 80 && p.Y < size.Y - 180;
    }

    public void ApplyCamera()
    {
        Zoom = Mathf.Clamp(Zoom, 0.14f, 2.4f);
        var period = MapPixelSize;
        int yPeriods = Mathf.FloorToInt(Cam.Y / period.Y);
        Cam = new Vector2(Mathf.PosMod(Cam.X - yPeriods * SeamShear, period.X), Mathf.PosMod(Cam.Y, period.Y));
        var focus = new Vector3(Cam.X, 0, Cam.Y);
        float d = Distance;
        var offset = new Vector3(Mathf.Sin(Yaw) * Mathf.Cos(Pitch), Mathf.Sin(Pitch), Mathf.Cos(Yaw) * Mathf.Cos(Pitch)) * d;
        _camera.Position = focus + offset;
        _camera.LookAt(focus, Vector3.Up);
        UpdateViewBounds();
        foreach (var chunk in _chunks) LayoutChunk(chunk);
        if (_forwardPlus)
        {
            // Below this zoom the contact details are subpixel; relying on baked shading avoids stippled AO.
            _environment.SsaoEnabled = Zoom > 0.28f;
            _environment.SsaoIntensity = 1.05f * Mathf.SmoothStep(0.28f, 0.7f, Zoom);
        }
        int lod = Zoom >= 0.7f ? 2 : Zoom >= 0.38f ? 1 : 0;
        Lod = lod;
        _layoutDirty = true;
        _screen.Minimap?.QueueRedraw();
    }

    public override void _Ready()
    {
        var viewport = GetViewport();
        viewport.SizeChanged += ApplyCamera;
        TreeExiting += () => viewport.SizeChanged -= ApplyCamera;
    }

    private void UpdateViewBounds()
    {
        Vector2 size = GetViewport().GetVisibleRect().Size;
        _viewMin = Cam;
        _viewMax = Cam;
        for (int corner = 0; corner < 4; corner++)
        {
            var screen = new Vector2((corner & 1) == 0 ? 0 : size.X, (corner & 2) == 0 ? 0 : size.Y);
            var origin = _camera.ProjectRayOrigin(screen);
            var direction = _camera.ProjectRayNormal(screen);
            // Project to the terrain base and the highest ground feature. Rotating the camera
            // changes which repeat intersects this footprint.
            var basePoint = origin + direction * ((BaseY - origin.Y) / direction.Y);
            var highPoint = origin + direction * ((2.2f - origin.Y) / direction.Y);
            _viewMin = _viewMin.Min(new Vector2(basePoint.X, basePoint.Z)).Min(new Vector2(highPoint.X, highPoint.Z));
            _viewMax = _viewMax.Max(new Vector2(basePoint.X, basePoint.Z)).Max(new Vector2(highPoint.X, highPoint.Z));
        }
    }

    private void LayoutChunk(Chunk chunk)
    {
        int used = 0;
        if (chunk.Terrain != null || chunk.Decor != null)
        {
            var lower = new Vector2(chunk.X0 * HexW, chunk.Y0 * RowH) - Vector2.One * 2;
            var upper = new Vector2(Math.Min(chunk.X0 + ChunkSize, W.Grid.W) * HexW,
                Math.Min(chunk.Y0 + ChunkSize, W.Grid.H) * RowH) + Vector2.One * 2;
            foreach (var repeat in VisiblePeriods(lower, upper))
            {
                if (used == chunk.Images.Count)
                {
                    // Each visible image reuses the canonical meshes instead of rebuilding their vertices.
                    var terrain = new MeshInstance3D { MaterialOverride = _terrainMat };
                    var decor = new MeshInstance3D { MaterialOverride = _terrainMat, VisibilityRangeEnd = 51f, VisibilityRangeEndMargin = 7f };
                    AddChild(terrain);
                    AddChild(decor);
                    chunk.Images.Add((terrain, decor));
                }
                var image = chunk.Images[used++];
                var offset = PeriodOffset(repeat.X, repeat.Y);
                image.Terrain.Position = image.Decor.Position = new Vector3(offset.X, 0, offset.Y);
                image.Terrain.Mesh = chunk.Terrain;
                image.Decor.Mesh = chunk.Decor;
                image.Terrain.Show();
                image.Decor.Show();
            }
        }
        for (int i = used; i < chunk.Images.Count; i++)
        {
            chunk.Images[i].Terrain.Hide();
            chunk.Images[i].Decor.Hide();
        }
    }

    private IEnumerable<Vector2I> VisiblePeriods(Vector2 lower, Vector2 upper)
    {
        var period = MapPixelSize;
        int firstRow = Mathf.CeilToInt((_viewMin.Y - upper.Y) / period.Y);
        int lastRow = Mathf.FloorToInt((_viewMax.Y - lower.Y) / period.Y);
        for (int y = firstRow; y <= lastRow; y++)
        {
            int firstCol = Mathf.CeilToInt((_viewMin.X - upper.X - y * SeamShear) / period.X);
            int lastCol = Mathf.FloorToInt((_viewMax.X - lower.X - y * SeamShear) / period.X);
            for (int x = firstCol; x <= lastCol; x++)
                yield return new Vector2I(x, y);
        }
    }

    private void LayoutEntity(Node3D root, MeshInstance3D body, MeshInstance3D? ring, List<Node3D> copies)
    {
        int used = 0;
        var ground = new Vector2(root.Position.X, root.Position.Z);
        foreach (var repeat in VisiblePeriods(ground - Vector2.One, ground + Vector2.One))
        {
            if (repeat == Vector2I.Zero) continue;
            if (used == copies.Count)
            {
                var clone = new Node3D();
                clone.AddChild(new MeshInstance3D { MaterialOverride = _terrainMat });
                if (ring != null) clone.AddChild((MeshInstance3D)ring.Duplicate());
                AddChild(clone);
                copies.Add(clone);
            }
            var copy = copies[used++];
            var offset = PeriodOffset(repeat.X, repeat.Y);
            copy.Position = root.Position + new Vector3(offset.X, 0, offset.Y);
            var copyBody = copy.GetChild<MeshInstance3D>(0);
            copyBody.Mesh = body.Mesh;
            copyBody.Transform = body.Transform;
            if (ring != null) copy.GetChild<MeshInstance3D>(1).Visible = ring.Visible;
            copy.Show();
        }
        for (int i = used; i < copies.Count; i++) copies[i].Hide();
    }

    // -------------------------------------------------------------- state
    public void OnDelta(DeltaMsg d)
    {
        if (d.Full) for (int i = 0; i < _chunks.Length; i++) _dirtyChunks.Add(i);
        foreach (var t in d.Tiles)
        {
            if (!W.Grid.Valid(t.Idx)) continue;
            if (!d.Full && _lastVisibility[t.Idx] == 0 && W.Vis[t.Idx] > 0 && _fogTransitions.Count < 180)
                _fogTransitions.Add((t.Idx, Now + 0.42));
            _lastVisibility[t.Idx] = W.Vis[t.Idx];
            MarkDirty(t.Idx);
            foreach (int n in W.Grid.Nb[t.Idx]) MarkDirty(n);
        }
        foreach (var u in W.Units.Values)
        {
            if (!_units.TryGetValue(u.Id, out var v)) { _units[u.Id] = CreateUnit(u); continue; }
            if (u.Tile == v.Tile) continue;
            v.Tile = u.Tile;
            if (v.Predicted != u.Tile) { v.Path.Clear(); v.Path.Enqueue(NearestCenter(u.Tile, v.Pos)); }
            v.Predicted = -1;
        }
        foreach (int id in _units.Keys.Where(id => !W.Units.ContainsKey(id)).ToList())
        {
            _units[id].Root.QueueFree();
            foreach (var copy in _units[id].Copies) copy.QueueFree();
            _units.Remove(id);
        }
        if (SelectedUnit >= 0 && !W.Units.ContainsKey(SelectedUnit)) SelectedUnit = -1;
        foreach (var e in d.Events)
            if (e.Type == EventKind.Combat && W.Grid.Valid(e.B))
                _flashes.Add((e.B, Now + 0.8, e.S == "1" ? new Color(1, 0.45f, 0.15f) : new Color(0.8f, 0.85f, 1f)));
            else if (e.Type == EventKind.Ranged && W.Grid.Valid(e.B))
                _flashes.Add((e.B, Now + 0.9, new Color(1f, 0.75f, 0.2f))); // shots, spells and tower fire
        UpdateHoverRoute();
        _dirty = true;
    }

    private void MarkDirty(int idx) => _dirtyChunks.Add(idx / W.Grid.W / ChunkSize * _chunkCols + idx % W.Grid.W / ChunkSize);

    private static double Now => Time.GetTicksMsec() / 1000.0;

    /// <summary>Client-side prediction: start moving right away, the server confirms or corrects later.</summary>
    public void OrderMove(UnitDto u, int target)
    {
        if (u.Tile == target)
        {
            if (u.AutoExplore || u.Goto >= 0) _screen.Net.SendCommand(CmdType.MoveUnit, u.Id, target);
            return;
        }
        var path = W.PredictPath(u, target);
        if (path == null) { _screen.Toast("Туда нет известного пути", true); return; }
        int seq = _screen.Net.SendCommand(CmdType.MoveUnit, u.Id, target);
        _moveSeq[seq] = u.Id;
        if (!_units.TryGetValue(u.Id, out var v)) return;
        int moves = u.Moves, at = u.Tile, last = u.Tile;
        var mover = Rules.MoverFor(u.Def, W.Knows);
        v.Path.Clear();
        var position = v.Pos;
        foreach (int step in path)
        {
            if (moves <= 0) break;
            bool hostile = W.Units.Values.Any(o => o.Tile == step && o.Owner != W.MyId)
                           || (W.CityAt(step) is { } c && c.Owner != W.MyId);
            if (hostile) break; // the outcome of a fight is for the server to decide
            moves -= Rules.MoveCost(W.Tiles[at], W.Tiles[step], mover);
            position = NearestCenter(step, position);
            v.Path.Enqueue(position);
            at = last = step;
        }
        if (last != u.Tile)
        {
            v.Predicted = last;
            v.Deadline = Now + 1.5 + Math.Max(0, _screen.Net.PingMs) / 250.0;
        }
        _dirty = true;
    }

    public void OnCommandResult(int seq, bool ok)
    {
        if (!_moveSeq.Remove(seq, out int unit) || ok) return;
        if (_units.TryGetValue(unit, out var v)) Reconcile(v);
    }

    private void Reconcile(UnitNode v)
    {
        v.Predicted = -1;
        v.Path.Clear();
        v.Path.Enqueue(NearestCenter(v.Tile, v.Pos));
        _dirty = true;
    }

    public override void _Process(double delta)
    {
        var pan = Vector2.Zero;
        float turn = 0;
        if (!_screen.TypingInChat)
        {
            if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) pan.X -= 1;
            if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) pan.X += 1;
            if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) pan.Y += 1;
            if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) pan.Y -= 1;
            if (Input.IsKeyPressed(Key.Q)) turn += 1;
            if (Input.IsKeyPressed(Key.E)) turn -= 1;
        }
        if (pan != Vector2.Zero || turn != 0)
        {
            Yaw += turn * (float)delta * 1.6f;
            Cam += (RightDir * pan.X + ForwardDir * pan.Y) * (float)delta * Distance * 0.9f;
            ApplyCamera();
        }

        if (_dirtyChunks.Count > 0)
        {
            foreach (int c in _dirtyChunks) RebuildChunk(_chunks[c]);
            _dirtyChunks.Clear();
            _dirty = true;
        }

        double now = Now;
        _fogTransitions.RemoveAll(item => item.End <= now);
        if (_fogWasDrawn || _fogTransitions.Count != 0) DrawFogTransitions(now);
        bool moving = false;
        foreach (var v in _units.Values)
        {
            if (v.Predicted >= 0 && now > v.Deadline) Reconcile(v);
            if (v.Path.Count == 0) continue;
            moving = true;
            float step = MoveSpeed * (float)delta * (v.Path.Count > 3 ? 2f : 1f);
            while (step > 0 && v.Path.Count > 0)
            {
                var target = v.Path.Peek();
                float dist = v.Pos.DistanceTo(target);
                if (dist > HexW * 6) { v.Pos = target; v.Path.Dequeue(); continue; } // far jump: no slow slide across the map
                if (dist <= step) { v.Pos = target; v.Path.Dequeue(); step -= dist; }
                else { v.Pos = v.Pos.MoveToward(target, step); step = 0; }
            }
        }
        _flashes.RemoveAll(f => f.until < now);
        if (moving || _layoutDirty || _flashes.Count > 0 || _fogTransitions.Count > 0 || _dirty)
        {
            bool full = _dirty;
            _dirty = false;
            if (full || moving || _layoutDirty) SyncUnits();
            if (full || _layoutDirty) SyncCities();
            _layoutDirty = false;
            DrawLines();
            if (moving || _flashes.Count > 0 || _fogTransitions.Count > 0) OS.LowProcessorUsageMode = false;
        }
        else if (!OS.LowProcessorUsageMode) OS.LowProcessorUsageMode = true;
    }

    private void DrawFogTransitions(double now)
    {
        _fogTransitionMesh.ClearSurfaces();
        _fogWasDrawn = false;
        foreach (var (tile, end) in _fogTransitions)
        {
            var ground = Ground(tile);
            float fade = Mathf.SmoothStep(0f, 1f, (float)((end - now) / 0.42));
            foreach (var repeat in VisiblePeriods(ground - Vector2.One, ground + Vector2.One))
            {
                if (!_fogWasDrawn)
                {
                    _fogTransitionMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
                    _fogWasDrawn = true;
                }
                var shift = PeriodOffset(repeat.X, repeat.Y);
                var center = new Vector3(ground.X + shift.X, Height(W.Tiles[tile]) + 1.27f, ground.Y + shift.Y);
                Color mist = Ui.Col(0xA9BCC1, fade * 0.96f);
                for (int i = 0; i < Corner.Length; i++)
                {
                    _fogTransitionMesh.SurfaceSetColor(mist);
                    _fogTransitionMesh.SurfaceAddVertex(center);
                    _fogTransitionMesh.SurfaceSetColor(new Color(mist, mist.A * 0.66f));
                    _fogTransitionMesh.SurfaceAddVertex(center + Corner[i]);
                    _fogTransitionMesh.SurfaceSetColor(new Color(mist, mist.A * 0.66f));
                    _fogTransitionMesh.SurfaceAddVertex(center + Corner[(i + 1) % Corner.Length]);
                }
            }
        }
        if (_fogWasDrawn) _fogTransitionMesh.SurfaceEnd();
    }

    private Vector2 ForwardDir => new(-Mathf.Sin(Yaw), -Mathf.Cos(Yaw));
    private Vector2 RightDir => new(Mathf.Cos(Yaw), -Mathf.Sin(Yaw));

    // -------------------------------------------------------------- input
    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb)
        {
            if (mb.Pressed && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                Zoom *= mb.ButtonIndex == MouseButton.WheelUp ? 1.15f : 1 / 1.15f;
                ApplyCamera();
            }
            else if (mb.ButtonIndex is MouseButton.Left or MouseButton.Middle)
            {
                if (mb.Pressed) { _pressed = true; _dragging = mb.ButtonIndex == MouseButton.Middle; _pressPos = mb.Position; }
                else
                {
                    if (_pressed && !_dragging && mb.ButtonIndex == MouseButton.Left && !_screen.BannerClicked(mb.Position)) _screen.TileClicked(TileAt(mb.Position));
                    _pressed = false; _dragging = false;
                }
            }
            else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
                _screen.TileOrdered(TileAt(mb.Position));
        }
        else if (e is InputEventMouseMotion mm)
        {
            if (_pressed && !_dragging && mm.Position.DistanceTo(_pressPos) > 6) _dragging = true;
            if (_dragging)
            {
                if (Input.IsKeyPressed(Key.Shift) || Input.IsMouseButtonPressed(MouseButton.Middle) && Input.IsKeyPressed(Key.Ctrl))
                    Yaw -= mm.Relative.X * 0.006f;
                else
                {
                    float s = Distance * 0.0016f;
                    Cam += ForwardDir * mm.Relative.Y * s * 1.5f - RightDir * mm.Relative.X * s;
                }
                ApplyCamera();
                return;
            }
            int tile = TileAt(mm.Position);
            if (tile == HoverTile) return;
            HoverTile = tile;
            UpdateHoverRoute();
            _screen.HoverChanged(tile);
            _dirty = true;
        }
    }

    private void UpdateHoverRoute()
    {
        HoverPath = null;
        HoverTurns = 0;
        if (HoverTile < 0 || !W.Units.TryGetValue(SelectedUnit, out var unit) || unit.Owner != W.MyId ||
            unit.Def.Moves <= 0 || unit.Tile == HoverTile) return;
        if (unit.Def.X.Ranged > 0 && W.Grid.Distance(unit.Tile, HoverTile) <= unit.Def.X.Ranged &&
            W.UnitsAt(HoverTile).Any(other => other.Owner != W.MyId &&
                W.Players.TryGetValue(other.Owner, out var player) && (player.AtWar || player.Neutral)))
        {
            HoverTurns = unit.Moves > 0 ? 1 : 2;
            return;
        }
        HoverPath = W.PredictPath(unit, HoverTile);
        if (HoverPath != null) HoverTurns = W.PredictArrivalTurn(unit, HoverPath);
    }

    public void Select(int unitId) { SelectedUnit = unitId; UpdateHoverRoute(); _dirty = true; }
    public void Invalidate() => _dirty = true;

    // ------------------------------------------------------------- colors
    public static Color TerrainColor(in TileData t)
    {
        if (t.Relief == Relief.Mountain) return Ui.Col(0x777878);
        Color c = t.Terrain switch
        {
            Terrain.Ocean => Ui.Col(0x4A7289), Terrain.Coast => Ui.Col(0x619DA3), Terrain.Grassland => Ui.Col(0x88A96D),
            Terrain.Plains => Ui.Col(0xB2A573), Terrain.Desert => Ui.Col(0xD2B080), Terrain.Tundra => Ui.Col(0x8F9F94),
            _ => Ui.Col(0xD7E3E6),
        };
        if (t.Relief == Relief.Hills) c = c.Darkened(0.1f);
        if (t.Forest) c = c.Lerp(Ui.Col(0x587956), 0.32f);
        return c;
    }

    public Color OwnerColor(int owner) => W.Players.TryGetValue(owner, out var p) ? Ui.Col(p.Color) : Colors.Gray;

    // ------------------------------------------------------- mesh helpers
    /// <summary>Marker colour by resource class: bonus green, strategic red, luxury violet.</summary>
    public static Color ResourceColor(Resource r) => Rules.Res(r).Class switch
    {
        ResClass.Bonus => Ui.Col(0x96AE68), ResClass.Strategic => Ui.Col(0xBD7863), _ => Ui.Col(0xAE88B7),
    };

    private void RebuildChunk(Chunk ch)
    {
        var terrain = new MeshBuilder();
        var decor = new MeshBuilder();
        for (int y = ch.Y0; y < Math.Min(ch.Y0 + ChunkSize, W.Grid.H); y++)
            for (int x = ch.X0; x < Math.Min(ch.X0 + ChunkSize, W.Grid.W); x++)
            {
                int idx = y * W.Grid.W + x;
                byte vis = W.Vis[idx];
                var t = W.Tiles[idx];
                if (vis == 0)
                {
                    var ground = Ground(idx);
                    var cloud = new Vector3(ground.X, BaseY + 0.012f, ground.Y);
                    var mist = Ui.Col(0xA6B6BC).Lerp(Ui.Col(0xC7D0D0), (Mathf.Sin(x * .41f + y * .57f) + 1) * .24f);
                    for (int k = 0; k < Corner.Length; k++)
                        terrain.Tri(cloud, cloud + Corner[k], cloud + Corner[(k + 1) % Corner.Length], Vector3.Up, mist);
                    continue;
                }
                var c = CanonicalCenter(idx);
                Color Shade(Color col)
                {
                    if (vis == 1) col = col.Lerp(Ui.Col(0x475659), 0.42f).Darkened(0.22f);
                    return _cityFocus != null && !_cityFocus.Contains(idx) ? col.Darkened(0.48f) : col;
                }
                var color = TerrainColor(t);
                float variation = Mathf.Sin(x * 0.82f + y * 0.6f) * Mathf.Cos(y * 0.7f - x * 0.21f) * 0.065f;
                color = variation > 0 ? color.Lightened(variation) : color.Darkened(-variation);
                if (t.Owner >= 0) color = color.Lerp(OwnerColor(t.Owner), 0.10f);
                color = Shade(color);
                var side = color.Darkened(0.28f);

                for (int k = 0; k < 6; k++)
                {
                    var a = c + Corner[k]; var b = c + Corner[(k + 1) % 6];
                    float facet = (k + idx % 3) % 3 * 0.014f;
                    terrain.Tri(c, a, b, Vector3.Up, color.Darkened(facet));
                    // a wall is only needed where the neighbour is lower or not drawn at all
                    int n = Neighbor(idx, DirOfCorner[k]);
                    float bottom = n >= 0 && W.Vis[n] > 0 ? Height(W.Tiles[n]) : BaseY;
                    if (bottom < c.Y - 0.001f)
                        terrain.Quad(a, b, new Vector3(b.X, bottom, b.Z), new Vector3(a.X, bottom, a.Z), (a + b) / 2 - c, side);
                }

                if (t.Relief == Relief.Mountain)
                {
                    terrain.Pyramid(c, 0.86f, 1.15f, 6, Shade(Ui.Col(0x797A79)), Mathf.Pi / 6);
                    terrain.Pyramid(c + new Vector3(0, 0.72f, 0), 0.33f, 0.46f, 6, Shade(Ui.Col(0xE2EAEB)), Mathf.Pi / 6);
                }
                else if (t.Relief == Relief.Hills && t.CityId < 0)
                {
                    terrain.Column(c + new Vector3(-0.56f, 0, 0.23f), 0.29f, 0.18f, color.Lightened(0.035f), 6, 0.15f);
                    terrain.Column(c + new Vector3(0.57f, 0, -0.19f), 0.23f, 0.14f, color.Darkened(0.02f), 5, 0.09f);
                }

                // borders: a raised strip along every edge where the owner changes
                if (t.Owner >= 0)
                {
                    var bc = Shade(OwnerColor(t.Owner).Lerp(Ui.Col(0xE2D3B7), 0.2f));
                    for (int d = 0; d < 6; d++)
                    {
                        int n = Neighbor(idx, d);
                        if (n >= 0 && W.Vis[n] > 0 && W.Tiles[n].Owner == t.Owner) continue;
                        var a = c + Corner[Edge[d].a] * 0.97f + new Vector3(0, 0.035f, 0);
                        var b = c + Corner[Edge[d].b] * 0.97f + new Vector3(0, 0.035f, 0);
                        var a2 = c + Corner[Edge[d].a] * 0.915f + new Vector3(0, 0.035f, 0);
                        var b2 = c + Corner[Edge[d].b] * 0.915f + new Vector3(0, 0.035f, 0);
                        terrain.Quad(a, b, b2, a2, Vector3.Up, bc);
                    }
                }

                if (t.Road && t.CityId < 0)
                {
                    var road = Shade(Ui.Col(0xA18B68));
                    var up = new Vector3(0, 0.03f, 0);
                    for (int d = 0; d < 6; d++)
                    {
                        int n = Neighbor(idx, d);
                        if (n < 0 || W.Vis[n] == 0 || !W.Tiles[n].Road) continue;
                        var to = (Corner[Edge[d].a] + Corner[Edge[d].b]) / 2;
                        var w = to.Cross(Vector3.Up).Normalized() * 0.09f;
                        terrain.Quad(c + up - w, c + up + w, c + to + up + w, c + to + up - w, Vector3.Up, road);
                    }
                    terrain.Pyramid(c + up, 0.14f, 0.01f, 6, road);
                }

                // ---- decorations (hidden at a distance)
                if (t.Forest && t.CityId < 0)
                {
                    var leaf = Shade(Ui.Col(0x638860));
                    float spin = idx % 11 * 0.59f;
                    for (int tree = 0; tree < 4; tree++)
                    {
                        float angle = spin + Mathf.Tau * tree / 4;
                        var off = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * (tree % 2 == 0 ? 0.65f : 0.74f);
                        if (t.Resource != Resource.None && (off - new Vector3(-0.34f, 0, -0.32f)).Length() < 0.41f) continue;
                        var at = c + off;
                        float height = 0.28f + (idx + 3 * tree) % 5 * 0.05f;
                        var crown = leaf.Lerp(Shade(Ui.Col(0x9BA568)), tree * 0.17f);
                        decor.Column(at, 0.05f, height, Shade(Ui.Col(0x806A56)), 5, 0.025f);
                        if (t.Terrain is Terrain.Tundra or Terrain.Snow)
                        {
                            decor.Pyramid(at + Vector3.Up * (height * 0.42f), 0.19f, height * 1.35f, 5, crown.Darkened(0.1f), spin);
                            decor.Pyramid(at + Vector3.Up * (height * 0.86f), 0.135f, height * 1.1f, 5, crown, spin);
                        }
                        else
                        {
                            decor.Gem(at + Vector3.Up * height, 0.18f, crown, 1.1f, 6);
                            decor.Gem(at + Vector3.Up * (height + 0.12f) + off * 0.08f, 0.13f, crown.Lightened(0.06f), 1f, 5);
                        }
                    }
                }
                if (t.Improvement != Improvement.None)
                {
                    decor.Tint = Shade;
                    Town.Improvement(decor, t.Improvement, c, t.Road);
                    decor.Tint = null;
                }
                if (t.Resource != Resource.None)
                {
                    decor.Tint = Shade;
                    Models.Resource(decor, t.Resource, c + new Vector3(-0.34f, 0.005f, -0.32f), ResourceColor(t.Resource));
                    decor.Tint = null;
                }
            }
        ch.Terrain = terrain.Commit();
        ch.Decor = decor.Commit();
        LayoutChunk(ch);
    }

    // ------------------------------------------------------ units, cities
    private UnitNode CreateUnit(UnitDto u)
    {
        var n = new UnitNode { Tile = u.Tile, Pos = Center(u.Tile), Root = new Node3D() };
        n.Body = new MeshInstance3D { MaterialOverride = _terrainMat };
        n.Ring = new MeshInstance3D
        {
            Mesh = _ringMesh, MaterialOverride = _ringMat, Position = Vector3.Up * 0.025f,
            Scale = Vector3.One * 0.33f, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        n.Root.AddChild(n.Body);
        n.Root.AddChild(n.Ring);
        n.Root.Position = n.Pos;
        AddChild(n.Root);
        return n;
    }

    private void SyncUnits()
    {
        UnitProjectionRevision++;
        var stacks = W.Units.Values.Where(u => _units.ContainsKey(u.Id)).GroupBy(u =>
        {
            var n = _units[u.Id];
            return n.Predicted >= 0 ? n.Predicted : u.Tile;
        });
        foreach (var stack in stacks)
        {
            var units = stack.OrderBy(u => u.Id).ToArray();
            bool city = W.Tiles[stack.Key].CityId >= 0;
            for (int i = 0; i < units.Length; i++)
            {
                var u = units[i];
                var n = _units[u.Id];
                if (n.Type != u.Type || n.Owner != u.Owner)
                {
                    n.Type = u.Type;
                    n.Owner = u.Owner;
                    n.Body.Mesh = Models.Unit(u.Def, u.Owner, OwnerColor(u.Owner));
                    var bounds = n.Body.Mesh.GetAabb();
                    n.MeshCenter = new Vector2(bounds.GetCenter().X, bounds.GetCenter().Z);
                    n.MeshRadius = new Vector2(bounds.Size.X, bounds.Size.Z).Length() / 2;
                    n.MeshHeight = bounds.End.Y;
                }

                // A footprint radius keeps even elongated meshes inside their own slot at every camera yaw.
                float scale = Mathf.Min(0.4f, Mathf.Min(0.132f / n.MeshRadius, (city ? 0.215f : 0.25f) / n.MeshHeight));
                float lift = u.Def.X.Flying ? 0.085f : 0f;
                n.Body.Scale = Vector3.One * scale;
                n.Body.Position = new Vector3(-n.MeshCenter.X * scale, lift, -n.MeshCenter.Y * scale);
                n.BodyTop = n.MeshHeight * scale + lift;
                n.Ring.Visible = u.Id == SelectedUnit;
                Vector2 slot = FormationSlot(i, units.Length);
                Vector2 horizontal = RightDir * slot.X + ForwardDir * slot.Y;
                if (n.Path.Count == 0) n.Pos = Center(stack.Key);
                var focus = (_viewMin + _viewMax) * 0.5f;
                n.Root.Position = NearestImage(n.Pos, new Vector3(focus.X, 0, focus.Y)) + new Vector3(horizontal.X, 0, horizontal.Y);
                LayoutEntity(n.Root, n.Body, n.Ring, n.Copies);
            }
        }
    }

    private static Vector2 FormationSlot(int index, int count) => count switch
    {
        1 => Vector2.Zero,
        2 => new Vector2(index == 0 ? -0.22f : 0.22f, 0f),
        _ => index switch
        {
            0 => new Vector2(0f, 0.29f),
            1 => new Vector2(-0.245f, -0.145f),
            _ => new Vector2(0.245f, -0.145f),
        },
    };

    private void SyncCities()
    {
        foreach (var c in W.Cities.Values)
        {
            if (W.Vis[c.Tile] == 0) continue;
            if (!_cities.TryGetValue(c.Id, out var n))
            {
                n = new CityNode { Root = new Node3D { Position = Center(c.Tile) }, Body = new MeshInstance3D { MaterialOverride = _terrainMat } };
                n.Root.AddChild(n.Body);
                AddChild(n.Root);
                _cities[c.Id] = n;
            }
            n.Root.Position = Center(c.Tile);
            int era = c.VisualEra;
            string look = $"{c.Owner}|{c.Pop}|{era}|{c.Capital}|{string.Join(',', c.Buildings)}";
            if (n.Look != look)
            {
                n.Look = look;
                n.Body.Mesh = Town.City(c.Id, c.Pop, era, c.Capital, c.Buildings, OwnerColor(c.Owner));
            }
            LayoutEntity(n.Root, n.Body, null, n.Copies);
        }
    }

    // --------------------------------------------- selection, route, FX
    private void HexOutline(int tile, float scale, Color color, float lift)
        => HexOutline(Center(tile), scale, color, lift);

    private void HexOutline(Vector3 center, float scale, Color color, float lift)
    {
        var c = center + new Vector3(0, lift, 0);
        for (int k = 0; k < 6; k++)
        {
            _lines.SurfaceSetColor(color); _lines.SurfaceAddVertex(c + Corner[k] * scale);
            _lines.SurfaceSetColor(color); _lines.SurfaceAddVertex(c + Corner[(k + 1) % 6] * scale);
        }
    }

    private void DrawLines()
    {
        _lines.ClearSurfaces();
        bool any = false;
        void Begin() { if (!any) { _lines.SurfaceBegin(Mesh.PrimitiveType.Lines); any = true; } }

        foreach (int t in Highlight)
            if (W.Grid.Valid(t)) { Begin(); HexOutline(t, 0.72f, new Color(1, 1, 1, 0.9f), 0.08f); HexOutline(t, 0.68f, new Color(1, 1, 1, 0.9f), 0.08f); }
        if (HoverTile >= 0 && W.Vis[HoverTile] > 0) { Begin(); HexOutline(HoveredCenter(), 0.98f, new Color(1, 1, 1, 0.55f), 0.06f); }

        if (!_screen.CityFocused && SelectedUnit >= 0 && W.Units.TryGetValue(SelectedUnit, out var sel))
        {
            if (!_screen.IsAiming && HoverPath is { Count: > 0 })
            {
                Begin();
                var destination = HoveredCenter();
                var prev = destination + Vector3.Up * 0.15f;
                var col = new Color(1, 1, 1, 0.95f);
                for (int i = HoverPath.Count - 2; i >= -1; i--)
                {
                    int step = i < 0 ? sel.Tile : HoverPath[i];
                    var next = NearestCenter(step, prev) + Vector3.Up * 0.15f;
                    // three parallel lines read as one thick route
                    foreach (var off in new[] { Vector3.Zero, new Vector3(0.03f, 0, 0.03f), new Vector3(-0.03f, 0, -0.03f) })
                    {
                        _lines.SurfaceSetColor(col); _lines.SurfaceAddVertex(prev + off);
                        _lines.SurfaceSetColor(col); _lines.SurfaceAddVertex(next + off);
                    }
                    prev = next;
                }
                HexOutline(destination, 0.45f, col, 0.15f);
            }
            if (sel.Goto >= 0 && W.Grid.Valid(sel.Goto)) { Begin(); HexOutline(sel.Goto, 0.5f, Ui.Gold, 0.12f); HexOutline(sel.Goto, 0.4f, Ui.Gold, 0.12f); }
        }

        double now = Now;
        foreach (var (tile, until, color) in _flashes)
        {
            float k = (float)Math.Clamp((until - now) / 0.8, 0, 1);
            Begin();
            for (int ring = 0; ring < 4; ring++)
                HexOutline(tile, 1.25f - 0.8f * k + ring * 0.05f, new Color(color, k), 0.3f + (1 - k) * 0.6f);
        }
        if (any) _lines.SurfaceEnd();
    }
}
