using IH.Core;
using IH.Shared;

namespace IH.Tests;

public class WeatherTests
{
    private static Game ClearMap()
    {
        var game = Game.Create(new GameSettings { Seed = 7 }, new[] { ("Погода", false) },
            new BalanceConfig { RainChancePct = 0, ThunderstormChancePct = 0, ForestFireSpreadPct = 0 });
        game.S.WeatherSeed = 533;
        for (int tile = 0; tile < game.Grid.Count; tile++) game.S.Tiles[tile].Forest = false;
        return game;
    }

    private static void Advance(Game game, int turns = 1)
    {
        for (int i = 0; i < turns; i++) Assert.Null(game.Execute(0, new GameCommand(CmdType.EndTurn)));
    }

    private static void Woodland(Game game, int tile)
    {
        ref var land = ref game.S.Tiles[tile];
        land.Terrain = Terrain.Grassland;
        land.Relief = Relief.Flat;
        land.Forest = true;
        land.Improvement = Improvement.None;
    }

    [Fact]
    public void Fire_spreads_over_the_map_seam_and_new_ignitions_wait_a_turn()
    {
        var game = ClearMap();
        game.Bal.ForestFireSpreadPct = 100;
        int source = game.Grid.WrapIndex(0, 6), next = game.Grid.WrapIndex(-1, 6);
        int beyond = game.Grid.WrapIndex(-2, 6);
        foreach (int tile in new[] { source, next, beyond }) Woodland(game, tile);
        game.S.Tiles[source].FireTurns = 5;

        Advance(game);
        Assert.Equal(4, game.S.Tiles[source].FireTurns);
        Assert.Equal(5, game.S.Tiles[next].FireTurns);
        Assert.Equal(0, game.S.Tiles[beyond].FireTurns);
        Advance(game);
        Assert.Equal(5, game.S.Tiles[beyond].FireTurns);
    }

    [Fact]
    public void Five_burn_ticks_destroy_the_sawmill_and_forest_returns_twelve_turns_later_after_loading()
    {
        var game = ClearMap();
        int tile = game.Grid.WrapIndex(6, 6);
        Woodland(game, tile);
        game.S.Tiles[tile].Improvement = Improvement.LumberMill;
        game.S.Tiles[tile].FireTurns = (byte)game.Bal.ForestFireTurns;

        Advance(game, 4);
        Assert.Equal(1, game.S.Tiles[tile].FireTurns);
        Assert.True(game.S.Tiles[tile].Forest);
        Advance(game);
        Assert.Equal(0, game.S.Tiles[tile].FireTurns);
        Assert.False(game.S.Tiles[tile].Forest);
        Assert.Equal(Improvement.None, game.S.Tiles[tile].Improvement);

        game = new Game(SaveGame.FromJson<GameState>(SaveGame.ToJson(game.S)));
        Assert.Equal(533UL, game.S.WeatherSeed);
        Advance(game, 11);
        Assert.False(game.S.Tiles[tile].Forest);
        Advance(game);
        Assert.True(game.S.Tiles[tile].Forest);
        Assert.DoesNotContain(tile, game.S.ForestRegrowth.Keys);
    }

    [Fact]
    public void A_storm_washes_out_only_one_clay_house_and_its_fire_burns_one_wooden_house()
    {
        var game = ClearMap();
        var settler = game.UnitsOf(0).Single(unit => unit.Def.Role == UnitRole.Settler);
        Assert.Null(game.Execute(0, new GameCommand(CmdType.FoundCity, settler.Id)));
        var city = game.CitiesOf(0).Single();
        city.Houses[HouseMaterial.Wood] = 2;
        city.Houses[HouseMaterial.Clay] = 2;
        city.Houses[HouseMaterial.Stone] = 1;
        city.Houses[HouseMaterial.Concrete] = 1;
        game.Bal.ThunderstormChancePct = 100;
        game.Bal.LightningChancePct = 100;
        game.Bal.ClayWashChancePct = 100;

        Advance(game);
        var storm = game.S.Tiles[city.Tile];
        Assert.Equal(WeatherKind.Thunderstorm, storm.Weather);
        Assert.True(storm.Lightning);
        Assert.Equal(5, storm.FireTurns);
        Assert.Equal(1, city.HouseCount(HouseMaterial.Clay));
        Assert.Equal(2, city.HouseCount(HouseMaterial.Wood));

        game.Bal.ThunderstormChancePct = 0;
        game.Bal.RainChancePct = 100;
        Advance(game, 5);
        Assert.Equal(WeatherKind.Rain, game.S.Tiles[city.Tile].Weather);
        Assert.False(game.S.Tiles[city.Tile].Lightning);
        Assert.Equal(1, city.HouseCount(HouseMaterial.Clay));
        Assert.Equal(1, city.HouseCount(HouseMaterial.Wood));
        Assert.Equal(1, city.HouseCount(HouseMaterial.Stone));
        Assert.Equal(1, city.HouseCount(HouseMaterial.Concrete));
    }

    [Fact]
    public void Weather_and_lightning_are_packed_only_while_a_tile_is_visible()
    {
        var game = ClearMap();
        var observer = game.P(0);
        int origin = game.UnitsOf(0).First().Tile;
        int distant = Enumerable.Range(0, game.Grid.Count).First(tile => game.Grid.Distance(origin, tile) > 5);
        game.S.Tiles[distant].Weather = WeatherKind.Thunderstorm;
        game.S.Tiles[distant].Lightning = true;
        game.S.Tiles[distant].FireTurns = 3;
        Assert.False(game.BuildView(0).Visible[distant]);
        Assert.Equal(0UL, observer.KnownTiles[distant]);

        Assert.Null(game.ExecuteSandbox(0, new GameCommand(CmdType.SandboxReveal)));
        Assert.True(game.BuildView(0).Visible[distant]);
        var seen = TileData.Unpack(observer.KnownTiles[distant]);
        Assert.Equal(WeatherKind.Thunderstorm, seen.Weather);
        Assert.True(seen.Lightning);
        Assert.Equal(3, seen.FireTurns);

        Assert.Null(game.ExecuteSandbox(0, new GameCommand(CmdType.SandboxReveal)));
        Assert.False(game.BuildView(0).Visible[distant]);
        Assert.NotEqual(0UL, observer.KnownTiles[distant] & TileData.ExploredBit);
        Assert.Equal(0UL, observer.KnownTiles[distant] & TileData.WeatherMask);
    }
}
