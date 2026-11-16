using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using IH.Shared;
using TileData = IH.Shared.TileData;

namespace IH.Client;

public partial class GameScreen
{
    private int _citizenSource = -1;
    internal CityDto? CitizenCity => _win == Win.City && W.Cities.TryGetValue(_cityId, out var city)
        && city.Owner == W.MyId && city.Detail != null ? city : null;

    private int[] CitizenHighlights(CityDto city, CityDetail detail) => _citizenSource < 0
        ? detail.Worked : detail.Assignable.Except(detail.Worked).Append(_citizenSource).ToArray();

    private bool TryCitizenClick(int tile)
    {
        if (CitizenCity is not { Detail: { } detail } city || tile < 0) return false;
        if (_citizenSource >= 0)
        {
            if (tile == _citizenSource) _citizenSource = -1;
            else if (detail.Assignable.Contains(tile) && !detail.Worked.Contains(tile))
            {
                Net.SendCommand(CmdType.AssignCitizen, city.Id, _citizenSource, tile);
                _citizenSource = -1;
            }
            else
            {
                if (!detail.Worked.Contains(tile)) return false;
                _citizenSource = tile;
            }
        }
        else if (detail.Worked.Contains(tile) || tile == city.Tile && detail.Worked.Length < city.Pop - detail.Unhappy)
            _citizenSource = tile;
        else return false;
        Sound.Play(Sfx.Id.Click);
        RebuildWindow();
        return true;
    }
}

/// <summary>Owner-only residents in city focus, driven by the latest authoritative assignments.</summary>
public partial class ResidentView : Node3D
{
    private enum Activity { Walk, Sow, Harvest, Gather, Saw, Dig, Chisel, Fish, Herd, Tend, Carry, Craft }

    private readonly GameScreen _game;
    private readonly Dictionary<(int Tile, int Seat), Actor> _actors = new();
    private readonly List<LandmarkProp> _landmarks = new();
    private int _city = -1, _pop;
    private int[] _worked = Array.Empty<int>();
    private string[] _buildings = Array.Empty<string>();
    private float _time;

    private readonly Mesh _bodyMesh = new CapsuleMesh { Height = 0.18f, Radius = 0.065f, RadialSegments = 7, Rings = 2 };
    private readonly Mesh _headMesh = new SphereMesh { Height = 0.1f, Radius = 0.053f, RadialSegments = 7, Rings = 3 };
    private readonly Mesh _hatMesh = new CylinderMesh { TopRadius = 0.04f, BottomRadius = 0.074f, Height = 0.043f, RadialSegments = 7, Rings = 1 };
    private readonly Mesh _toolMesh = new BoxMesh { Size = new Vector3(0.034f, 0.18f, 0.035f) };
    private readonly Mesh _crateMesh = new BoxMesh { Size = new Vector3(0.063f, 0.053f, 0.05f) };
    private readonly Material _skin = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.69f, 0.55f), Roughness = 0.88f };
    private readonly Material _hat = new StandardMaterial3D { AlbedoColor = new Color(0.65f, 0.46f, 0.28f), Roughness = 1 };
    private readonly Material _tool = new StandardMaterial3D { AlbedoColor = new Color(0.56f, 0.43f, 0.31f), Roughness = 1 };
    private Material _cloth = null!;

    private sealed class Actor
    {
        public required Node3D Root;
        public required Node3D Body;
        public required MeshInstance3D Tool;
        public bool Positioned;
    }
    private sealed record LandmarkProp(MeshInstance3D Node, Vector3 Plot, Activity Activity, float Phase);

    public ResidentView(GameScreen game) => _game = game;

    public override void _Ready() => _cloth = new StandardMaterial3D
    {
        AlbedoColor = _game.Map.OwnerColor(_game.W.MyId).Lerp(new Color(0.87f, 0.82f, 0.64f), 0.55f),
        Roughness = 0.94f,
    };

    public override void _Process(double delta)
    {
        var city = _game.CitizenCity;
        if (city?.Detail is not { } detail || _game.Map.Lod < 1)
        {
            if (_actors.Count > 0) ClearActors();
            return;
        }
        if (_city != city.Id || _pop != city.Pop || !detail.Worked.SequenceEqual(_worked))
            SyncResidents(city, detail);
        if (!city.Buildings.SequenceEqual(_buildings)) SyncLandmarks(city);
        _time += (float)delta;
        foreach (var (seat, actor) in _actors)
        {
            int tile = seat.Tile;
            bool idle = tile == city.Tile;
            var ground = _game.Map.Center(tile);
            var activity = idle ? TownActivity(city, seat.Seat) : FieldActivity(_game.W.Tiles[tile]);
            Animate(actor, ground, tile, seat.Seat, idle, activity, delta);
        }
        var center = _game.Map.Center(city.Tile);
        foreach (var prop in _landmarks) AnimateLandmark(prop, center);
    }

    private void SyncResidents(CityDto city, CityDetail detail)
    {
        if (_city != city.Id) ClearActors();
        _city = city.Id; _pop = city.Pop; _worked = detail.Worked.ToArray();
        var seats = _worked.Select(tile => (Tile: tile, Seat: 0))
            .Concat(Enumerable.Range(0, Math.Max(0, _pop - _worked.Length)).Select(i => (Tile: city.Tile, Seat: i))).ToHashSet();
        var reusable = new Queue<Actor>();
        foreach (var old in _actors.Keys.Where(key => !seats.Contains(key)).ToArray())
        {
            reusable.Enqueue(_actors[old]);
            _actors.Remove(old);
        }
        foreach (var seat in seats.OrderBy(t => t.Tile).ThenBy(t => t.Seat))
            if (!_actors.ContainsKey(seat)) _actors.Add(seat, reusable.TryDequeue(out var actor) ? actor : NewActor());
        foreach (var unused in reusable) unused.Root.QueueFree();
    }

    private void ClearActors()
    {
        foreach (var actor in _actors.Values) actor.Root.QueueFree();
        _actors.Clear();
        _city = -1;
        ClearLandmarks();
    }

    private void ClearLandmarks()
    {
        foreach (var prop in _landmarks) prop.Node.QueueFree();
        _landmarks.Clear();
        _buildings = Array.Empty<string>();
    }

    private void SyncLandmarks(CityDto city)
    {
        ClearLandmarks();
        _buildings = city.Buildings.ToArray();
        foreach (var (id, plot) in Town.BuildingActivityPlots(city.Buildings))
        {
            var activity = BuildingActivity(id);
            var mesh = activity == Activity.Craft ? _toolMesh : _crateMesh;
            var prop = Part(mesh, activity == Activity.Tend ? _hat : _tool, Vector3.Zero);
            AddChild(prop);
            Vector3 beside = plot + new Vector3(0.14f, 0.045f, 0.09f);
            _landmarks.Add(new LandmarkProp(prop, beside, activity, _landmarks.Count * 2.39996f));
        }
    }

    private void AnimateLandmark(LandmarkProp prop, Vector3 center)
    {
        float wave = _time + prop.Phase;
        var (x, y, z, tilt) = prop.Activity switch
        {
            Activity.Carry => (Mathf.Sin(wave * 1.3f) * 0.094f, 0.01f, Mathf.Cos(wave * 1.3f) * 0.02f, 0.02f),
            Activity.Craft => (0f, Math.Max(0, Mathf.Sin(wave * 3.1f)) * 0.065f, 0f, Mathf.Sin(wave * 3.1f) * 0.67f),
            Activity.Tend => (0f, 0.033f + Mathf.Sin(wave * 1.2f) * 0.024f, 0f, Mathf.Sin(wave) * 0.16f),
            Activity.Fish => (Mathf.Sin(wave * 1.9f) * 0.036f, 0.014f, Mathf.Cos(wave * 1.9f) * 0.05f, Mathf.Sin(wave * 1.7f) * 0.12f),
            _ => (Mathf.Sin(wave * 0.9f) * 0.043f, 0.015f, 0f, Mathf.Sin(wave * 1.3f) * 0.18f),
        };
        prop.Node.Position = center + prop.Plot + new Vector3(x, y, z);
        prop.Node.Rotation = new Vector3(0, prop.Phase, tilt);
    }

    private Actor NewActor()
    {
        var root = new Node3D();
        var body = new Node3D();
        AddChild(root);
        root.AddChild(body);
        body.AddChild(Part(_bodyMesh, _cloth, new Vector3(0, 0.101f, 0)));
        body.AddChild(Part(_headMesh, _skin, new Vector3(0, 0.219f, 0)));
        body.AddChild(Part(_hatMesh, _hat, new Vector3(0, 0.269f, 0)));
        var tool = Part(_toolMesh, _tool, new Vector3(0.084f, 0.11f, 0.041f));
        body.AddChild(tool);
        return new Actor { Root = root, Body = body, Tool = tool };
    }

    private static MeshInstance3D Part(Mesh mesh, Material material, Vector3 position) => new()
    {
        Mesh = mesh, MaterialOverride = material, Position = position,
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
    };

    private static Activity FieldActivity(in TileData tile) => tile.Improvement switch
    {
        Improvement.Farm => Activity.Harvest,
        Improvement.Mine or Improvement.OilWell => Activity.Dig,
        Improvement.Quarry => Activity.Chisel,
        Improvement.LumberMill => Activity.Saw,
        Improvement.Plantation => Activity.Tend,
        Improvement.Pasture => Activity.Herd,
        Improvement.Camp => Activity.Gather,
        Improvement.Cottage => Activity.Craft,
        Improvement.FishingBoats => Activity.Fish,
        _ when tile.IsWater => Activity.Fish,
        _ when tile.Forest => Activity.Gather,
        _ when tile.Relief == Relief.Hills => Activity.Dig,
        _ when tile.Terrain == Terrain.Grassland || tile.Terrain == Terrain.Plains => Activity.Sow,
        _ => Activity.Walk,
    };

    private static Activity TownActivity(CityDto city, int seat)
    {
        if (city.Buildings.Length == 0 || seat % 3 == 0) return Activity.Walk;
        return BuildingActivity(city.Buildings[seat % city.Buildings.Length]);
    }

    private static Activity BuildingActivity(string id)
    {
        var building = Rules.Building(id);
        if (building == null) return Activity.Craft;
        if (building.Food > 0 || building.WithFood > 0) return Activity.Carry;
        if (building.ProdPct > 0 || building.DefPct > 0) return Activity.Craft;
        if (building.Coastal) return Activity.Fish;
        if (building.Faith > 0 || building.SciPct > 0) return Activity.Tend;
        return Activity.Walk;
    }

    private void Animate(Actor actor, Vector3 ground, int tile, int seat, bool idle, Activity act, double delta)
    {
        float phase = tile * 0.37f + seat * 2.39996f;
        float time = _time + phase;
        float angle = idle ? phase + _time * (seat % 2 == 0 ? 0.45f : -0.38f) : phase;
        float radius = idle ? Math.Min(0.36f, 0.12f + 0.065f * Mathf.Sqrt(seat)) : 0.48f;
        var place = ground + new Vector3(Mathf.Cos(angle) * radius, 0.023f, Mathf.Sin(angle) * radius);
        float walk = act == Activity.Walk || act == Activity.Herd || act == Activity.Carry ? 0.1f : 0.036f;
        place += new Vector3(Mathf.Sin(time * 1.4f) * walk, 0, Mathf.Cos(time) * walk);
        actor.Root.Position = !actor.Positioned || actor.Root.Position.DistanceTo(place) > 5
            ? place : actor.Root.Position.Lerp(place, (float)(1 - Math.Exp(-7 * delta)));
        actor.Positioned = true;
        actor.Root.Rotation = new Vector3(0, -angle + Mathf.Pi / 2, 0);

        var (pitch, swing, frequency) = act switch
        {
            Activity.Sow => (0.43f, 0.72f, 1.9f),
            Activity.Harvest => (0.41f, 1.00f, 2.9f),
            Activity.Gather => (0.24f, 0.65f, 2.0f),
            Activity.Saw => (0.22f, 0.40f, 5.2f),
            Activity.Dig => (0.56f, 1.12f, 2.4f),
            Activity.Chisel => (0.24f, 0.92f, 3.6f),
            Activity.Fish => (0.08f, 0.46f, 1.1f),
            Activity.Herd => (0.02f, 0.38f, 1.5f),
            Activity.Tend => (0.36f, 0.56f, 1.8f),
            Activity.Carry => (0.06f, 0.22f, 3.0f),
            Activity.Craft => (0.18f, 0.86f, 2.9f),
            _ => (0.02f, 0.32f, 3.0f),
        };
        float wave = Mathf.Sin(time * frequency);
        actor.Body.Rotation = new Vector3(pitch * (0.65f + 0.35f * wave), 0, Mathf.Cos(time * frequency) * 0.04f);
        actor.Body.Position = Vector3.Up * Mathf.Abs(wave) * (act == Activity.Walk ? 0.023f : 0.01f);
        actor.Tool.Visible = act != Activity.Walk;
        actor.Tool.Rotation = new Vector3(wave * swing - 0.45f, 0, act == Activity.Saw ? 1.27f : 0.16f);
    }
}
