using IH.Shared;

namespace IH.Core;

public sealed partial class Game
{
    public bool CanSpawnUnit(int owner, int type, int tile)
        => owner >= 0 && owner < S.Players.Count && P(owner).Alive && type >= 0 && type < Rules.Units.Length
            && HasUnitSpace(owner, tile) && FitsTile(owner, type, tile)
            && (CityAt(tile) is not { } city || city.Owner == owner)
            && (Rules.Units[type].Role == UnitRole.Missionary || CanEnterTerritory(owner, tile));

    private bool FitsTile(int owner, int type, int tile)
        => Rules.MoveCost(S.Tiles[tile], S.Tiles[tile], Rules.MoverFor(Rules.Units[type], P(owner).Has)) >= 0;

    private IEnumerable<int> NearestTiles(int from)
        => Enumerable.Range(0, Grid.Count).Where(t => t != from).OrderBy(t => Grid.Distance(from, t)).ThenBy(t => t);

    private void MigrateUnitStacks()
    {
        var overloaded = Enumerable.Range(0, Grid.Count).Where(t => At(t).Count > Rules.MaxUnitsPerTile).ToArray();
        if (overloaded.Length == 0) return;
        var placements = new List<(Unit Unit, int Tile)>();
        var available = Enumerable.Range(0, Grid.Count).Select(t => Math.Max(0, Rules.MaxUnitsPerTile - At(t).Count)).ToArray();
        var owners = Enumerable.Range(0, Grid.Count).Select(t => At(t).FirstOrDefault()?.Owner ?? -1).ToArray();
        foreach (int origin in overloaded)
        {
            var candidates = NearestTiles(origin).ToArray();
            foreach (var unit in At(origin).OrderBy(u => u.Id).Skip(Rules.MaxUnitsPerTile))
            {
                int tile = candidates.FirstOrDefault(t => available[t] > 0 && (owners[t] < 0 || owners[t] == unit.Owner)
                    && CanSpawnUnit(unit.Owner, unit.Type, t), -1);
                if (tile < 0)
                    throw new InvalidDataException($"Не удалось загрузить старое сохранение: для юнита {unit.Id} с клетки {origin} нет свободной подходящей клетки");
                available[tile]--;
                owners[tile] = unit.Owner;
                placements.Add((unit, tile));
            }
        }
        foreach (var (unit, tile) in placements)
        {
            Relocate(unit, tile);
            unit.Goto = -1; unit.Work = WorkKind.None; unit.WorkTurns = 0; unit.Fortified = false;
        }
    }
}
