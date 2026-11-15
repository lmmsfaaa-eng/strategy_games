using IH.Core;
using IH.Shared;
using Xunit;

namespace IH.Tests;

public class MovementOrdersTests
{
    [Fact]
    public void AutoExplore_reveals_tiles_each_turn_and_keeps_orders_private()
    {
        var g = OpenMap();
        var player = g.P(0);
        var scout = g.UnitsOf(0).First(u => u.Def.Id == "warrior");
        var start = scout.Tile;
        g.UpdateVisibility(player);
        int known = player.KnownTiles.Count(t => t != 0);
        Assert.Null(g.Execute(0, new GameCommand(CmdType.AutoExplore, scout.Id)));
        Assert.True(scout.AutoExplore);
        Assert.NotEqual(start, scout.Tile);
        Assert.True(player.KnownTiles.Count(t => t != 0) > known);
        Assert.True(g.ToDto(scout, own: true).AutoExplore);
        Assert.False(g.ToDto(scout, own: false).AutoExplore);

        start = scout.Tile;
        Assert.Null(g.Execute(0, new GameCommand(CmdType.EndTurn)));
        Assert.Null(g.Execute(1, new GameCommand(CmdType.EndTurn)));
        Assert.NotEqual(start, scout.Tile);
        Assert.True(scout.AutoExplore);
        Assert.Null(g.Execute(0, new GameCommand(CmdType.Wake, scout.Id)));
        Assert.False(scout.AutoExplore);
    }

    [Fact]
    public void AutoExplore_stops_before_engaging_a_visible_enemy()
    {
        var g = OpenMap();
        var scout = g.UnitsOf(0).First(u => u.Def.Id == "warrior");
        int tile = scout.Tile;
        int neighbor = g.Grid.Nb[tile].First(t => g.At(t).Count == 0);
        var foe = g.AddUnit(1, Rules.UnitIndex("warrior"), neighbor);
        g.Rel(0, 1).AtWar = true;

        Assert.Null(g.Execute(0, new GameCommand(CmdType.AutoExplore, scout.Id)));
        Assert.False(scout.AutoExplore);
        Assert.Equal(tile, scout.Tile);
        Assert.Equal(scout.MaxMoves, scout.Moves);
        Assert.Equal(100, foe.Hp);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueuedRangedAttack_requires_current_sight_on_the_following_turn(bool keepSpotter)
    {
        var g = OpenMap();
        var start = g.UnitsOf(0).First().Tile;
        var target = g.Grid.Range(start, 2).First(t => g.Grid.Distance(start, t) == 2 && g.At(t).Count == 0);
        var spotterTile = g.Grid.Nb[target].First(t => g.At(t).Count == 0);
        var mage = g.AddUnit(0, Rules.UnitIndex("battle_mage"), start);
        var spotter = g.AddUnit(0, Rules.UnitIndex("warrior"), spotterTile);
        var foe = g.AddUnit(1, Rules.UnitIndex("warrior"), target);
        g.Rel(0, 1).AtWar = true;
        mage.Moves = 0;

        Assert.Null(g.Execute(0, new GameCommand(CmdType.MoveUnit, mage.Id, target)));
        Assert.True(mage.QueuedRangedAttack);
        if (!keepSpotter) g.RemoveUnit(spotter);
        Assert.Null(g.Execute(0, new GameCommand(CmdType.EndTurn)));
        Assert.Null(g.Execute(1, new GameCommand(CmdType.EndTurn)));

        Assert.Equal(start, mage.Tile);
        Assert.Equal(-1, mage.Goto);
        Assert.Equal(keepSpotter, foe.Hp < 100);
    }

    [Theory]
    [InlineData(2, 2, new[] { 2 }, 1)]
    [InlineData(2, 2, new[] { 2, 2, 2 }, 3)]
    [InlineData(0, 2, new[] { 2 }, 2)]
    [InlineData(1, 4, new[] { 4, 2, 2 }, 2)]
    [InlineData(4, 4, new[] { 1, 1, 1, 1, 1 }, 2)]
    [InlineData(4, 4, new[] { Rules.EmbarkCost, 2, Rules.EmbarkCost, 2 }, 3)]
    public void Arrival_turn_uses_remaining_moves_and_real_step_costs(int remaining, int max, int[] costs, int turn)
        => Assert.Equal(turn, Rules.ArrivalTurn(remaining, max, costs));

    private static Game OpenMap()
    {
        var g = GameTests.New(2, 0);
        for (int tile = 0; tile < g.S.Tiles.Length; tile++)
        {
            g.S.Tiles[tile].Terrain = Terrain.Grassland;
            g.S.Tiles[tile].Relief = Relief.Flat;
            g.S.Tiles[tile].Forest = false;
        }
        return g;
    }
}
