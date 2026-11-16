using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using IH.Shared;
using TileData = IH.Shared.TileData;

namespace IH.Client;

public partial class MapView
{
    private readonly ImmediateMesh _weatherLines = new(), _weatherShapes = new();
    private readonly Dictionary<int, double> _strikes = new();
    private readonly HashSet<int> _weatherTiles = new();
    private readonly List<(int Tile, Vector3 Center)> _weatherImages = new();
    private ulong[]? _tileMapPacks, _tileWeatherPacks;
    private Vector2 _weatherViewMin, _weatherViewMax;
    private bool _weatherLayoutDirty = true, _hasWeatherLines, _hasWeatherShapes;
    private double _weatherFrameTime;

    private void AddWeatherEffects()
    {
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            VertexColorUseAsAlbedo = true,
            VertexColorIsSrgb = true,
        };
        foreach (var mesh in new[] { _weatherLines, _weatherShapes })
            AddChild(new MeshInstance3D
            {
                Mesh = mesh,
                MaterialOverride = material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
    }

    private void ResetWeather()
    {
        if (_tileMapPacks != null) Array.Clear(_tileMapPacks);
        if (_tileWeatherPacks != null) Array.Clear(_tileWeatherPacks);
        _weatherTiles.Clear();
        _strikes.Clear();
        _weatherLayoutDirty = true;
    }

    /// <returns>Whether the tile needs to rebuild the permanent map geometry.</returns>
    private bool TrackWeatherTile(TileDto delta)
    {
        _tileMapPacks ??= new ulong[W.Grid.Count];
        _tileWeatherPacks ??= new ulong[W.Grid.Count];
        int tile = delta.Idx;
        ulong map = delta.Pack & ~TileData.WeatherMask;
        ulong weather = W.Vis[tile] == 2 ? delta.Pack & TileData.WeatherMask : 0;
        if (_tileWeatherPacks[tile] != weather)
        {
            _tileWeatherPacks[tile] = weather;
            _weatherLayoutDirty = true;
            if (weather != 0)
            {
                _weatherTiles.Add(tile);
                if (W.Tiles[tile].Lightning) _strikes.TryAdd(tile, Now + 0.6);
            }
            else _weatherTiles.Remove(tile);
            if (!W.Tiles[tile].Lightning || W.Vis[tile] != 2) _strikes.Remove(tile);
            if (tile == HoverTile) _screen.HoverChanged(tile);
        }
        bool changed = _tileMapPacks[tile] != map;
        _tileMapPacks[tile] = map;
        return changed;
    }

    private void LayoutWeather()
    {
        _weatherLayoutDirty = false;
        _weatherViewMin = _viewMin;
        _weatherViewMax = _viewMax;
        _weatherImages.Clear();
        var candidates = new List<(int Tile, Vector3 Center, bool Fire, float Distance)>();
        var screen = GetViewport().GetVisibleRect().Grow(85);
        var focus = (_viewMin + _viewMax) * .5f;
        foreach (int tile in _weatherTiles)
        {
            var center = CanonicalCenter(tile);
            var ground = new Vector2(center.X, center.Z);
            foreach (var repeat in VisiblePeriods(ground - Vector2.One, ground + Vector2.One))
            {
                var offset = PeriodOffset(repeat.X, repeat.Y);
                var at = center + new Vector3(offset.X, 0, offset.Y);
                var sample = at + Vector3.Up * 0.8f;
                if (_camera.IsPositionBehind(sample) || !screen.HasPoint(_camera.UnprojectPosition(sample))) continue;
                candidates.Add((tile, at, W.Tiles[tile].FireTurns != 0, focus.DistanceSquaredTo(ground + offset)));
            }
        }
        foreach (var item in candidates.OrderByDescending(x => x.Fire).ThenBy(x => x.Distance).Take(180))
            _weatherImages.Add((item.Tile, item.Center));
    }

    private bool AnimateWeather(double delta, double now)
    {
        if (_weatherLayoutDirty || _weatherViewMin != _viewMin || _weatherViewMax != _viewMax)
            LayoutWeather();
        if (_weatherImages.Count == 0)
        {
            if (_hasWeatherLines) _weatherLines.ClearSurfaces();
            if (_hasWeatherShapes) _weatherShapes.ClearSurfaces();
            _hasWeatherLines = _hasWeatherShapes = false;
            _weatherFrameTime = 0;
            return false;
        }

        _weatherFrameTime -= delta;
        if (_weatherFrameTime > 0) return true;
        _weatherFrameTime = 1.0 / 22;
        _weatherLines.ClearSurfaces();
        _weatherShapes.ClearSurfaces();
        _hasWeatherLines = _hasWeatherShapes = false;

        float time = (float)(now % 240);
        var right = new Vector3(RightDir.X, 0, RightDir.Y);
        foreach (var (tile, at) in _weatherImages)
        {
            var data = W.Tiles[tile];
            if (data.Weather != WeatherKind.Clear)
                DrawRain(tile, at, data.Weather == WeatherKind.Thunderstorm, time);
            if (data.FireTurns != 0)
                DrawFire(tile, at, right, time);
            if (_strikes.TryGetValue(tile, out var end) && now < end)
                DrawThunderbolt(tile, at, right, (float)((end - now) / 0.6));
        }
        if (_hasWeatherLines) _weatherLines.SurfaceEnd();
        if (_hasWeatherShapes) _weatherShapes.SurfaceEnd();
        return true;
    }

    private void DrawRain(int tile, Vector3 land, bool thunder, float time)
    {
        int drops = thunder ? 5 : 3;
        float drift = thunder ? 0.31f : 0.18f;
        float speed = thunder ? 1.73f : 1.28f;
        var color = Ui.Col(thunder ? 0xA6CADD : 0xC2D8DD, thunder ? .69f : .56f);
        for (int drop = 0; drop < drops; drop++)
        {
            int seed = tile * 37 + drop * 107;
            float height = Mathf.PosMod(WeatherNoise(seed) - time * speed, 1f) * 3.5f;
            if (height < .12f) continue;
            var at = land + new Vector3(
                (WeatherNoise(seed + 1) - .5f) * 1.8f + drift * height,
                height, (WeatherNoise(seed + 2) - .5f) * 1.45f);
            WeatherSegment(at, at - new Vector3(drift * .45f, thunder ? .48f : .34f, 0), color);
        }
    }

    private void DrawFire(int tile, Vector3 land, Vector3 right, float time)
    {
        float phase = WeatherNoise(tile * 263);
        for (int i = 0; i < 3; i++)
        {
            float flicker = .72f + .28f * Mathf.Sin(time * 10 + phase * 22 + 3.5f * i);
            var flame = land + right * ((i - 1) * .25f) + Vector3.Up * .10f;
            float width = (.11f + i * .017f) * flicker;
            float height = (.44f + i * .06f) * flicker;
            WeatherTriangle(flame - right * width, flame + right * width,
                flame + right * .08f + Vector3.Up * height, Ui.Col(0xEA722F, .92f));
            WeatherTriangle(flame - right * width * .5f, flame + right * width * .53f,
                flame + Vector3.Up * height * .69f, Ui.Col(0xFFD57B, .96f));
        }
        for (int i = 0; i < 4; i++)
        {
            float rise = Mathf.PosMod(time * .69f + phase + i * .25f, 1f);
            var puff = land + Vector3.Up * (.38f + 1.95f * rise) + right * (.65f * rise);
            float radius = .15f + rise * .22f;
            var color = Ui.Col(0x59626A, .40f * (1 - rise));
            for (int edge = 0; edge < 6; edge++)
            {
                Vector3 EdgeAt(int side)
                {
                    float angle = side * Mathf.Tau / 6;
                    return puff + (right * Mathf.Cos(angle) + Vector3.Up * Mathf.Sin(angle)) * radius;
                }
                WeatherTriangle(puff, EdgeAt(edge), EdgeAt(edge + 1), color);
            }
        }
    }

    private void DrawThunderbolt(int tile, Vector3 land, Vector3 right, float fade)
    {
        var at = land + Vector3.Up * .2f;
        var color = Ui.Col(0xE5F1FF, Mathf.Clamp(fade * 1.55f, 0, 1));
        var edge = right * .036f;
        for (int segment = 1; segment <= 6; segment++)
        {
            float shift = (WeatherNoise(tile * 241 + segment * 79) - .5f) * 1.18f;
            var next = land + Vector3.Up * (.2f + segment * .58f) + right * shift;
            WeatherTriangle(at - edge, at + edge, next + edge, color);
            WeatherTriangle(at - edge, next + edge, next - edge, color);
            at = next;
        }
    }

    private void WeatherSegment(Vector3 start, Vector3 end, Color color)
    {
        if (!_hasWeatherLines)
        {
            _weatherLines.SurfaceBegin(Mesh.PrimitiveType.Lines);
            _hasWeatherLines = true;
        }
        _weatherLines.SurfaceSetColor(color);
        _weatherLines.SurfaceAddVertex(start);
        _weatherLines.SurfaceAddVertex(end);
    }

    private void WeatherTriangle(Vector3 a, Vector3 b, Vector3 c, Color color)
    {
        if (!_hasWeatherShapes)
        {
            _weatherShapes.SurfaceBegin(Mesh.PrimitiveType.Triangles);
            _hasWeatherShapes = true;
        }
        _weatherShapes.SurfaceSetColor(color);
        _weatherShapes.SurfaceAddVertex(a);
        _weatherShapes.SurfaceAddVertex(b);
        _weatherShapes.SurfaceAddVertex(c);
    }

    private static float WeatherNoise(int seed)
    {
        uint value = unchecked((uint)seed * 747796405U + 2891336453U);
        value = unchecked(((value >> (int)((value >> 28) + 4)) ^ value) * 277803737U);
        return ((value >> 22) ^ value) % 4093 / 4093f;
    }
}
