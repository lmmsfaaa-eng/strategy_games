using IH.Shared;

namespace IH.Core;

public sealed partial class Game
{
    private int HouseCost(HousingDef house) => Math.Max(1, Bal.HouseCosts?.ElementAtOrDefault((int)house.Material) is > 0 and var cost ? cost : house.Cost);
    private int HousePlaces(HousingDef house) => Math.Max(1, Bal.HousePlaces?.ElementAtOrDefault((int)house.Material) is > 0 and var places ? places : house.Places);
    public int CityHousingCapacity(City c) => Math.Max(1, Bal.BaseHousingPlaces) + Housing.Materials.Sum(h => HousePlaces(h) * c.HouseCount(h.Material));
    public int HousingUsed(Player p) => CitiesOf(p.Id).Sum(c => c.Pop) + UnitsOf(p.Id).Count(u => Rules.IsStacked(u.Def));
    public bool HasRecruitHousing(Player p) => p.Neutral || HousingUsed(p) < CitiesOf(p.Id).Sum(CityHousingCapacity);
    private bool ActiveImprovement(Player p, Improvement kind) => S.Tiles.Any(t => t.Owner == p.Id && t.Improvement == kind && t.FireTurns == 0);
    public bool CanGrow(Player p, City c) => c.Pop < CityHousingCapacity(c) && HasRecruitHousing(p);
    private int CityProjectCost(string item) => Housing.Project(item) is { } house ? HouseCost(house) : Rules.ItemCost(item);

    private bool CanWorkCityTile(Player p, City c, int tile)
    {
        if (tile == c.Tile || !Grid.Valid(tile) || Grid.Distance(c.Tile, tile) > Rules.CityWorkRadius) return false;
        var t = S.Tiles[tile];
        if (t.Owner != p.Id || t.CityId >= 0) return false;
        if (At(tile).Any(u => u.Owner != p.Id && AtWar(p.Id, u.Owner))) return false;
        var (food, production, commerce) = Rules.TileYield(Seen(p, t));
        return food + production + commerce > 0;
    }

    private int[] AvailableCitizenTiles(Player p, City c)
    {
        var reserved = CitiesOf(p.Id).Where(other => other.Id != c.Id).SelectMany(other => other.Worked).ToHashSet();
        return Grid.Range(c.Tile, Rules.CityWorkRadius).Where(t => !reserved.Contains(t) && CanWorkCityTile(p, c, t)).ToArray();
    }

    private string? CmdAssignCitizen(Player p, int cityId, int from, int to)
    {
        var city = OwnCity(p, cityId, out var error);
        if (city == null) return error;
        if (from == -1 && to == -1)
        {
            city.CitizenAssignments.Clear();
            RefreshEconomy(p);
            return null;
        }
        RefreshEconomy(p);
        bool idle = from == city.Tile && city.Worked.Count < city.Pop - city.Unhappy;
        if (!idle && !city.Worked.Contains(from)) return "На исходной клетке нет свободного жителя";
        if (!AvailableCitizenTiles(p, city).Contains(to)) return "Эта клетка недоступна для работы города";
        if (city.Worked.Contains(to)) return "На этой клетке уже работает житель";
        var assignments = city.Worked.Where(t => t != from).Append(to).ToList();
        foreach (var other in CitiesOf(p.Id).Where(c => c.Id != city.Id))
            other.CitizenAssignments.RemoveAll(assignments.Contains);
        city.CitizenAssignments = assignments;
        RefreshEconomy(p);
        return null;
    }

    private string? CmdPurchaseProject(Player p, int cityId, string item, bool faith)
    {
        var city = OwnCity(p, cityId, out var error);
        if (city == null) return error;
        if (item.Length == 0) item = city.Queue.FirstOrDefault() ?? "";
        if (item == Rules.Wealth || !CanBuild(p, city, item)) return "Этот проект нельзя купить в данном городе";
        int unitType = item.StartsWith("u:", StringComparison.Ordinal) ? Rules.UnitIndex(item[2..]) : -1;
        if (unitType >= 0 && UnitProductionError(p, city, unitType) is { } blocked) return blocked;

        int queued = city.Queue.IndexOf(item);
        int cost = CityProjectCost(item);
        int progress = queued == 0 ? city.Prod : 0;
        int price = faith ? Rules.FaithPrice(cost, progress) : Rules.BuyPrice(cost, progress);
        if (price <= 0) return "Проект уже оплачен";
        if (faith ? p.Faith < price : p.Gold < price)
            return faith ? $"Недостаточно веры (нужно {price})" : $"Недостаточно золота (нужно {price})";

        if (faith) p.Faith -= price;
        else p.Gold -= price;
        if (queued >= 0) city.Queue.RemoveAt(queued);
        if (queued == 0) city.Prod = 0;
        DeliverProject(p, city, item, unitType);
        RefreshEconomy(p);
        return null;
    }

    private void DeliverProject(Player p, City c, string item, int type)
    {
        if (type >= 0)
        {
            if (Rules.Units[type].Role == UnitRole.Settler) c.Pop--;
            AddUnit(p.Id, type, c.Tile).Moves = 0;
        }
        else if (Housing.Project(item) is { } house)
            c.Houses[house.Material] = c.HouseCount(house.Material) + 1;
        else c.Buildings.Add(item[2..]);
        Emit(EventKind.BuildDone, c.Id, s: item, only: p.Id);
        if (c.Queue.Count == 0) Emit(EventKind.IdleCity, c.Id, s: c.Name, only: p.Id);
    }
}
