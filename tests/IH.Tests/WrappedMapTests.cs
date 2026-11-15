using IH.Core;
using IH.Shared;
using Xunit;

namespace IH.Tests;

public class WrappedMapTests
{
    [Theory]
    [InlineData(11, 8)]
    [InlineData(11, 7)]
    public void Pathfinding_and_ranges_use_both_wrapped_seams(int width, int height)
    {
        var grid = new HexGrid(width, height);
        int west = grid.WrapIndex(-1, 0);
        int north = grid.WrapIndex(0, -1);
        Assert.Equal(width - 1, west);
        Assert.Equal(height - 1, grid.Y(north));
        Assert.Contains(0, grid.Nb[west]);
        Assert.Contains(0, grid.Nb[north]);
        Assert.Equal(new[] { west }, Assert.IsType<List<int>>(grid.FindPath(0, west, (_, _) => 1)));
        Assert.Equal(new[] { north }, Assert.IsType<List<int>>(grid.FindPath(0, north, (_, _) => 1)));
        Assert.Contains(west, grid.Range(0, 1));
        Assert.Contains(north, grid.Range(0, 1));

        for (int center = 0; center < grid.Count; center++)
        {
            var visited = new HashSet<int> { center };
            var last = new List<int> { center };
            for (int radius = 1; visited.Count < grid.Count; radius++)
            {
                last = last.SelectMany(tile => grid.Nb[tile]).Where(visited.Add).ToList();
                Assert.All(last, tile => Assert.Equal(radius, grid.Distance(center, tile)));
                Assert.Equal(visited.OrderBy(tile => tile), grid.Range(center, radius));
            }
        }
    }

    [Fact]
    public void Legacy_odd_height_save_keeps_its_tiles_and_allows_movement_across_both_seams()
    {
        var original = Game.Create(new GameSettings { MapW = 40, MapH = 27, Seed = 7, Simultaneous = true },
            new[] { ("Pilot", false) });
        var state = SaveGame.FromJson<GameState>(SaveGame.ToJson(original.S));
        int origin = 0;
        int west = original.Grid.WrapIndex(-1, 0);
        int north = original.Grid.WrapIndex(0, -1);
        foreach (int tile in new[] { origin, west, north })
            state.Tiles[tile] = new TileData { Owner = -1, CityId = -1, Terrain = Terrain.Plains };
        var warrior = state.Units.Values.First(unit => unit.Def.Id == "warrior");
        warrior.Tile = origin;
        var game = new Game(state);
        Assert.Equal(27, game.Grid.H);
        Assert.Equal(1080, game.S.Tiles.Length);
        Assert.Null(game.Execute(0, new GameCommand(CmdType.MoveUnit, warrior.Id, west)));
        Assert.Equal(west, warrior.Tile);
        warrior.Moves = warrior.MaxMoves;
        Assert.Null(game.Execute(0, new GameCommand(CmdType.MoveUnit, warrior.Id, origin)));
        warrior.Moves = warrior.MaxMoves;
        Assert.Null(game.Execute(0, new GameCommand(CmdType.MoveUnit, warrior.Id, north)));
        Assert.Equal(north, warrior.Tile);
    }
}
