using IH.Core;
using IH.Shared;

namespace IH.Tests;

public sealed class CityAndHousingTests
{
    private static (Game Game, Player Player, City City) FoundedCity()
    {
        var game = GameTests.New(1, 0);
        var player = game.P(0);
        int settler = game.UnitsOf(player.Id).Single(u => u.Def.Role == UnitRole.Settler).Id;
        Assert.Null(game.Execute(0, new GameCommand(CmdType.FoundCity, settler)));
        game.Bal.RainChancePct = game.Bal.ThunderstormChancePct = 0;
        return (game, player, game.CitiesOf(player.Id).Single());
    }

    [Fact]
    public void Immediate_catalog_purchases_complete_the_selected_item_and_preserve_other_progress()
    {
        var (game, player, city) = FoundedCity();
        player.Known.UnionWith(new[] { "agriculture", "pottery", "writing" });
        player.Gold = 1000;
        player.Faith = 1000;
        city.Queue.AddRange(new[] { "b:library", "b:granary" });
        city.Prod = 8;

        Assert.Null(game.Execute(0, new GameCommand(CmdType.Buy, city.Id, s: "b:granary")));
        Assert.Contains("granary", city.Buildings);
        Assert.Equal(1000 - Rules.BuyPrice(Rules.ItemCost("b:granary"), 0), player.Gold);
        Assert.Equal("b:library", Assert.Single(city.Queue));
        Assert.Equal(8, city.Prod);

        Assert.Null(game.Execute(0, new GameCommand(CmdType.BuyFaith, city.Id, s: "b:library")));
        Assert.Contains("library", city.Buildings);
        Assert.Equal(1000 - Rules.FaithPrice(Rules.ItemCost("b:library"), 8), player.Faith);
        Assert.Empty(city.Queue);
        Assert.Equal(0, city.Prod);
    }

    [Fact]
    public void Own_housing_is_shared_by_population_and_recruits_and_old_save_defaults_are_readable()
    {
        var (game, player, city) = FoundedCity();
        player.Known.Add("agriculture");
        player.Gold = 1000;
        city.Pop = 3;
        Assert.False(game.CanGrow(player, city));
        Assert.False(game.HasRecruitHousing(player));
        Assert.NotNull(game.Execute(0, new GameCommand(CmdType.Buy, city.Id, s: "u:warrior")));
        Assert.Equal(1000, player.Gold);

        Assert.Null(game.Execute(0, new GameCommand(CmdType.Buy, city.Id, s: "h:wood")));
        Assert.Equal(6, game.CityHousingCapacity(city));
        Assert.True(game.HasRecruitHousing(player));
        Assert.True(game.CanGrow(player, city));
        Assert.Equal(1, city.HouseCount(HouseMaterial.Wood));

        Assert.Null(game.Execute(0, new GameCommand(CmdType.Buy, city.Id, s: "u:warrior")));
        Assert.Equal(2, game.UnitsOf(0).Count(u => Rules.IsStacked(u.Def)));
        Assert.True(city.RemoveHouse(HouseMaterial.Wood));
        Assert.False(game.HasRecruitHousing(player));
        Assert.Equal(3, city.Pop);
        Assert.Equal(2, game.UnitsOf(0).Count(u => Rules.IsStacked(u.Def)));

        var oldCity = SaveGame.FromJson<City>("{\"Pop\":3,\"Unrest\":90,\"DisorderTurns\":3}");
        Assert.Empty(oldCity.Houses);
        Assert.Empty(oldCity.CitizenAssignments);
        Assert.Equal(Housing.BasePlaces, game.CityHousingCapacity(oldCity));
    }

    [Fact]
    public void Reassigning_a_citizen_persists_and_keeps_other_workers_on_their_tiles()
    {
        var (game, player, city) = FoundedCity();
        city.Pop = 2;
        city.Culture = 130;
        foreach (int tile in game.Grid.Nb[city.Tile])
        {
            game.S.Tiles[tile].Owner = 0;
            game.S.Tiles[tile].Terrain = Terrain.Grassland;
            game.S.Tiles[tile].Relief = Relief.Flat;
        }
        game.RefreshEconomy(player);

        int source = city.Worked[0], other = city.Worked[1];
        Assert.NotNull(game.Execute(0, new GameCommand(CmdType.AssignCitizen, city.Id, source, other)));
        int newTile = game.Grid.Nb[city.Tile].First(t => !city.Worked.Contains(t));
        Assert.Null(game.Execute(0, new GameCommand(CmdType.AssignCitizen, city.Id, source, newTile)));
        Assert.Equal(new[] { other, newTile }.Order(), city.Worked.Order());
        var state = SaveGame.FromJson<GameState>(SaveGame.ToJson(game.S));
        var loaded = new Game(state);
        loaded.RefreshEconomy(loaded.P(0));
        Assert.Equal(new[] { other, newTile }.Order(), state.Cities[city.Id].Worked.Order());
        Assert.Null(loaded.Execute(0, new GameCommand(CmdType.AssignCitizen, city.Id, -1, -1)));
        Assert.Empty(state.Cities[city.Id].CitizenAssignments);
    }

    [Fact]
    public void Dormant_assignments_do_not_block_a_neighboring_city()
    {
        var (game, player, first) = FoundedCity();
        int secondTile = game.Grid.Range(first.Tile, 3).First(t => game.Grid.Distance(first.Tile, t) == 3 && game.At(t).Count == 0);
        game.S.Tiles[secondTile] = new TileData { Terrain = Terrain.Grassland, Owner = -1, CityId = -1 };
        var settler = game.AddUnit(0, Rules.UnitIndex("settler"), secondTile);
        Assert.Null(game.Execute(0, new GameCommand(CmdType.FoundCity, settler.Id)));
        var second = game.CitiesOf(0).Single(c => c.Id != first.Id);
        var shared = game.Grid.Range(secondTile, 2).Where(t => t != first.Tile && t != secondTile && game.Grid.Distance(first.Tile, t) <= 2).Take(2).ToArray();
        int destination = shared[0], retained = shared[1];
        int original = game.Grid.Nb[secondTile].First(t => !shared.Contains(t));
        foreach (int tile in game.Grid.Range(secondTile, 2))
            if (game.S.Tiles[tile].CityId < 0) game.S.Tiles[tile].Owner = -1;
        foreach (int tile in new[] { destination, retained, original })
            game.S.Tiles[tile] = new TileData { Terrain = Terrain.Grassland, Owner = 0, CityId = -1 };
        game.S.Tiles[retained].Improvement = Improvement.Farm;
        first.CitizenAssignments.AddRange(shared);
        first.Buffs.Add(new CityBuff { Amount = -30, Stat = "Happy", Until = game.S.Turn + 5 });
        second.Pop = 2;
        second.CitizenAssignments.Add(original);
        game.RefreshEconomy(player);

        Assert.Empty(first.Worked);
        Assert.Equal(new[] { original, retained }.Order(), second.Worked.Order());
        Assert.Null(game.Execute(0, new GameCommand(CmdType.AssignCitizen, second.Id, original, destination)));
        first.Buffs.Clear();
        game.RefreshEconomy(player);
        Assert.Equal(new[] { destination, retained }.Order(), second.Worked.Order());
        Assert.Empty(first.CitizenAssignments);
    }

    [Fact]
    public void Stone_requires_a_quarry_and_lumbermill_doubles_wood_production_only()
    {
        var (game, player, city) = FoundedCity();
        player.Known.UnionWith(new[] { "agriculture", "masonry" });
        player.Gold = 1000;
        Assert.NotNull(game.Execute(0, new GameCommand(CmdType.Buy, city.Id, s: "h:stone")));
        int workshop = game.Grid.Nb[city.Tile][0];
        game.S.Tiles[workshop] = new TileData { Terrain = Terrain.Grassland, Relief = Relief.Hills, Owner = 0, CityId = -1 };
        Assert.True(Rules.WorkFits(game.S.Tiles[workshop], WorkKind.Quarry));
        game.S.Tiles[workshop].Improvement = Improvement.Quarry;
        Assert.Null(game.Execute(0, new GameCommand(CmdType.Buy, city.Id, s: "h:stone")));
        Assert.Equal(1, city.HouseCount(HouseMaterial.Stone));

        game.S.Tiles[workshop].Forest = true;
        game.S.Tiles[workshop].Improvement = Improvement.LumberMill;
        Assert.Null(game.Execute(0, new GameCommand(CmdType.SetProduction, city.Id, s: "h:wood")));
        city.Prod = 0;
        game.RefreshEconomy(player);
        int rate = city.ProdRate;
        Assert.InRange(rate, 1, 12);
        Assert.Null(game.Execute(0, new GameCommand(CmdType.EndTurn)));
        Assert.Equal(rate * 2, city.Prod);
    }
}
