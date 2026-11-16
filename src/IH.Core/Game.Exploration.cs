using IH.Shared;

namespace IH.Core;

public sealed partial class Game
{
    private string? CmdAutoExplore(Player p, int unitId)
    {
        var unit = Own(p, unitId, out var error);
        if (unit == null) return error;
        if (unit.MaxMoves <= 0) return "Этот юнит не может перемещаться";
        SetMoveOrder(unit, -1);
        unit.AutoExplore = true;
        ContinueExploring(p, unit);
        return null;
    }

    private void ContinueExploring(Player p, Unit unit)
    {
        while (true)
        {
            var visible = VisibleFrom(unit);
            foreach (int tile in visible) RememberVisibleTile(p, tile);
            if (visible.Any(t => HasVisibleThreat(unit, t)))
            {
                StopExploring(unit, "замечен противник");
                return;
            }
            if (unit.Moves <= 0) return;
            int next = FindExplorationStep(p, unit, visible.ToHashSet());
            if (next < 0)
            {
                StopExploring(unit, "нет доступных неизведанных клеток");
                return;
            }
            // The immediate step must be safe even if a remembered route is now obstructed.
            if (At(next).Any(other => other.Owner != unit.Owner) || !Step(unit, next))
            {
                StopExploring(unit, "путь перекрыт");
                return;
            }
        }
    }

    private void StopExploring(Unit unit, string reason)
    {
        unit.AutoExplore = false;
        Emit(EventKind.Info, s: $"{unit.Def.Name}: авторазведка остановлена — {reason}.", only: unit.Owner);
    }

    private bool HasVisibleThreat(Unit unit, int tile)
        => (CityAt(tile) is { } city && AtWar(unit.Owner, city.Owner)) ||
            At(tile).Any(other => other.Def.Str > 0 && AtWar(unit.Owner, other.Owner)
                && (!other.Def.X.Stealth || Spotted(other, unit.Owner)));

    private int FindExplorationStep(Player p, Unit unit, HashSet<int> visible)
    {
        var costs = new int[Grid.Count];
        Array.Fill(costs, int.MaxValue);
        var queue = new PriorityQueue<(int Tile, int FirstStep), (int Cost, int Tile)>();
        var mover = MoverOf(unit);
        costs[unit.Tile] = 0;
        queue.Enqueue((unit.Tile, -1), (0, unit.Tile));
        while (queue.TryDequeue(out var node, out var priority))
        {
            if (priority.Cost != costs[node.Tile]) continue;
            // Step onto a charted frontier to reveal its surroundings through normal sight.
            if (node.FirstStep >= 0 && Grid.Nb[node.Tile].Any(n => p.KnownTiles[n] == 0))
                return node.FirstStep;
            var from = TileData.Unpack(p.KnownTiles[node.Tile]);
            foreach (int next in Grid.Nb[node.Tile])
            {
                if (p.KnownTiles[next] == 0) continue;
                var target = TileData.Unpack(p.KnownTiles[next]);
                if (target.Owner >= 0 && target.Owner != p.Id &&
                    (AtWar(p.Id, target.Owner) || target.CityId >= 0 ||
                     (unit.Def.Role != UnitRole.Missionary && !Rel(p.Id, target.Owner).OpenBorders)))
                    continue;
                var stack = At(next);
                if (visible.Contains(next) && CiviliansFull(next, unit.Def) && stack[0].Owner == p.Id) continue;
                if (visible.Contains(next) && stack.Any(other => other.Owner != p.Id
                    && (!other.Def.X.Stealth || Spotted(other, p.Id)))) continue;
                int step = Rules.MoveCost(from, target, mover);
                if (step < 0) continue;
                int total = priority.Cost + step;
                if (total >= costs[next]) continue;
                costs[next] = total;
                queue.Enqueue((next, node.FirstStep < 0 ? next : node.FirstStep), (total, next));
            }
        }
        return -1;
    }
}
