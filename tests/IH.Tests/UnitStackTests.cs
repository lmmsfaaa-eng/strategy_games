using IH.Core;
using IH.Shared;
using Xunit;

namespace IH.Tests;

public class UnitStackTests
{
    private static string? Cmd(Game g, int pid, CmdType type, int a = 0, int b = 0, string s = "")
        => g.Execute(pid, new GameCommand(type, a, b, 0, s));

    private static Unit Add(Game g, int pid, string unit, int tile)
        => g.AddUnit(pid, Rules.UnitIndex(unit), tile);

    private static int OpenNeighbor(Game g, int source) => g.Grid.Nb[source]
        .First(t => g.S.Tiles[t].Passable && g.At(t).Count == 0);

    private static City FoundCapital(Game g, int pid)
    {
        var settler = g.UnitsOf(pid).Single(u => u.Def.Role == UnitRole.Settler);
        Assert.Null(Cmd(g, pid, CmdType.FoundCity, settler.Id));
        return g.CitiesOf(pid).Single();
    }

    [Fact]
    public void Full_civilian_stack_blocks_a_fourth_worker_but_allows_more_troops()
    {
        var g = GameTests.New(2, 0);
        var city = FoundCapital(g, 0);
        for (int i = 0; i < Rules.MaxCiviliansPerTile; i++) Add(g, 0, "worker", city.Tile);
        for (int i = 0; i < 9; i++) Add(g, 0, i % 2 == 0 ? "warrior" : "scout", city.Tile);
        int edge = OpenNeighbor(g, city.Tile);
        var arriving = Add(g, 0, "worker", edge);
        Assert.Null(g.PathFor(arriving, city.Tile));
        Assert.NotNull(Cmd(g, 0, CmdType.MoveUnit, arriving.Id, city.Tile));
        Assert.Equal(edge, arriving.Tile);
        Assert.Equal(arriving.MaxMoves, arriving.Moves);
        Assert.False(g.CanSpawnUnit(0, arriving.Type, city.Tile));
        Assert.Throws<InvalidOperationException>(() => Add(g, 0, "worker", city.Tile));
        var scout = Add(g, 0, "scout", edge);
        Assert.Null(Cmd(g, 0, CmdType.MoveUnit, scout.Id, city.Tile));
        Assert.Equal(city.Tile, scout.Tile);
        g.RemoveUnit(g.At(city.Tile).First(u => u.Def.Role == UnitRole.Worker));
        Assert.Null(Cmd(g, 0, CmdType.MoveUnit, arriving.Id, city.Tile));
        Assert.Equal(city.Tile, arriving.Tile);
    }

    [Fact]
    public void Completed_settler_waits_in_queue_without_spending_population()
    {
        var g = GameTests.New(2, 0);
        var city = FoundCapital(g, 0);
        city.Pop = 3;
        var guard = Add(g, 0, "worker", city.Tile);
        Add(g, 0, "worker", city.Tile);
        Add(g, 0, "worker", city.Tile);
        Assert.Null(Cmd(g, 0, CmdType.SetProduction, city.Id, s: "u:settler"));
        g.P(0).Gold = 1000;
        Assert.NotNull(Cmd(g, 0, CmdType.Buy, city.Id));
        Assert.Equal(1000, g.P(0).Gold);
        city.Prod = Rules.ItemCost("u:settler");
        Cmd(g, 0, CmdType.EndTurn); Cmd(g, 1, CmdType.EndTurn);
        Assert.Equal(3, city.Pop);
        Assert.Equal(Rules.ItemCost("u:settler"), city.Prod);
        Assert.Equal("u:settler", Assert.Single(city.Queue));
        g.RemoveUnit(guard);
        Cmd(g, 0, CmdType.EndTurn); Cmd(g, 1, CmdType.EndTurn);
        Assert.Equal(2, city.Pop);
        Assert.Empty(city.Queue);
        Assert.Contains(g.At(city.Tile), u => u.Def.Role == UnitRole.Settler);
    }

    [Fact]
    public void Capturing_three_civilians_leaves_room_for_the_attacker()
    {
        var g = GameTests.New(2, 0);
        FoundCapital(g, 0);
        var enemyCity = FoundCapital(g, 1);
        g.Rel(0, 1).AtWar = true;
        int spot = OpenNeighbor(g, enemyCity.Tile);
        var civilians = Enumerable.Range(0, 3).Select(_ => Add(g, 1, "worker", spot)).ToArray();
        var attacker = Add(g, 0, "warrior", OpenNeighbor(g, spot));
        Assert.Null(Cmd(g, 0, CmdType.MoveUnit, attacker.Id, spot));
        Assert.Equal(spot, attacker.Tile);
        Assert.Equal(4, g.At(spot).Count);
        Assert.Equal(spot, civilians[0].Tile);
        Assert.Equal(spot, civilians[1].Tile);
        Assert.Equal(spot, civilians[2].Tile);
        Assert.All(civilians, u => Assert.Equal(0, u.Owner));
        Assert.Empty(g.CheckInvariants());
    }

    [Fact]
    public void Teleport_validates_the_entire_stack_before_charging()
    {
        var g = GameTests.New(2, 0);
        var city = FoundCapital(g, 0);
        var p = g.P(0);
        p.Nodes.UnionWith(Rules.Branches.SelectMany(b => b.Nodes).Select(n => n.Id));
        p.Power["magic"] = 50;
        int start = OpenNeighbor(g, city.Tile);
        var mage = Add(g, 0, "battle_mage", start);
        var companion = Add(g, 0, "worker", start);
        var guard = Add(g, 0, "worker", city.Tile);
        Add(g, 0, "worker", city.Tile);
        Add(g, 0, "worker", city.Tile);
        Assert.Contains("нет места", Cmd(g, 0, CmdType.Cast, mage.Id, city.Tile, "mg_teleport"));
        Assert.Equal(start, mage.Tile);
        Assert.Equal(start, companion.Tile);
        Assert.Equal(50, p.Power["magic"]);
        Assert.False(p.Cooldowns.ContainsKey("mg_teleport"));
        g.RemoveUnit(guard);
        Assert.Null(Cmd(g, 0, CmdType.Cast, mage.Id, city.Tile, "mg_teleport"));
        Assert.Equal(city.Tile, mage.Tile);
        Assert.Equal(city.Tile, companion.Tile);
    }

    [Fact]
    public void Summoning_with_full_housing_spends_no_power_or_actions()
    {
        var g = GameTests.New(2, 0);
        var city = FoundCapital(g, 0);
        var p = g.P(0);
        p.Nodes.UnionWith(Rules.Branches.SelectMany(b => b.Nodes).Select(n => n.Id));
        p.Power[Rules.ShamanismBranch] = 50;
        int target = OpenNeighbor(g, city.Tile);
        g.S.Tiles[target].Terrain = Terrain.Plains;
        g.S.Tiles[target].Relief = Relief.Flat;
        var shaman = Add(g, 0, "shaman", city.Tile);
        for (int i = 0; i < 3; i++) Add(g, 0, "warrior", target);
        g.UpdateVisibility(p);
        Assert.Contains("жиль", Cmd(g, 0, CmdType.Cast, shaman.Id, target, "sh_summon"));
        Assert.Equal(50, p.Power[Rules.ShamanismBranch]);
        Assert.Equal(shaman.MaxMoves, shaman.Moves);
        Assert.False(p.Cooldowns.ContainsKey("sh_summon"));
        Assert.DoesNotContain(g.UnitsOf(0), u => u.Def.Id == "spirit");
    }

    [Fact]
    public void Old_save_keeps_troops_and_relocates_only_excess_civilians()
    {
        var g = GameTests.New(1, 0);
        var city = FoundCapital(g, 0);
        for (int i = 0; i < 5; i++) Add(g, 0, "warrior", city.Tile);
        for (int i = 0; i < Rules.MaxCiviliansPerTile; i++) Add(g, 0, "worker", city.Tile);
        var overflow = new Unit { Id = g.S.NextId++, Type = Rules.UnitIndex("worker"), Tile = city.Tile, Owner = 0 };
        g.S.Units.Add(overflow.Id, overflow);
        string original = SaveGame.ToJson(g.S);
        int expected = g.Grid.Nb[city.Tile].Where(t => g.CanSpawnUnit(0, overflow.Type, t)).Min();
        var loaded = new Game(SaveGame.FromJson<GameState>(original));
        Assert.Equal(9, loaded.At(city.Tile).Count);
        Assert.Equal(expected, loaded.S.Units[overflow.Id].Tile);
        Assert.Equal(city.Tile, overflow.Tile);
        Assert.Empty(loaded.CheckInvariants());

        var blocked = SaveGame.FromJson<GameState>(original);
        for (int i = 0; i < blocked.Tiles.Length; i++)
            if (i != city.Tile) blocked.Tiles[i].Relief = Relief.Mountain;
        Assert.Throws<InvalidDataException>(() => new Game(blocked));
        Assert.Equal(city.Tile, blocked.Units[overflow.Id].Tile);
    }

    [Fact]
    public void Rites_open_religion_and_shamanism_independently()
    {
        var g = GameTests.New(2, 0);
        var city = FoundCapital(g, 0);
        var p = g.P(0);
        p.Faith = 500;
        Assert.NotNull(Cmd(g, 0, CmdType.FoundReligion, 0, 0));
        Assert.NotNull(Cmd(g, 0, CmdType.SetNode, s: "sh_spirits"));
        Assert.Equal(500, p.Faith);
        g.RefreshEconomy(p);
        Assert.Equal(0, p.BranchRate[Rules.ShamanismBranch]);

        p.Civics.Add(Rules.ReligionCivic);
        Assert.Null(Cmd(g, 0, CmdType.SetNode, s: "sh_spirits"));
        Assert.Empty(g.S.Religions);
        Assert.Null(Cmd(g, 0, CmdType.FoundReligion, 0, 0));
        for (int i = 0; i < Rules.MaxCiviliansPerTile; i++) Add(g, 0, "worker", city.Tile);
        int before = p.Faith;
        Assert.NotNull(Cmd(g, 0, CmdType.Recruit, city.Id));
        Assert.Equal(before, p.Faith);
    }

    [Fact]
    public void Legacy_shamanism_research_is_preserved()
    {
        var g = GameTests.New(2, 0);
        FoundCapital(g, 0);
        var p = g.P(0);
        p.BranchCurrent[Rules.ShamanismBranch] = "sh_spirits";
        p.BranchProgress[Rules.ShamanismBranch] = Rules.Node("sh_spirits")!.Cost;
        var loaded = new Game(SaveGame.FromJson<GameState>(SaveGame.ToJson(g.S)));
        Cmd(loaded, 0, CmdType.EndTurn); Cmd(loaded, 1, CmdType.EndTurn);
        Assert.Contains("sh_spirits", loaded.P(0).Nodes);
        Assert.DoesNotContain(Rules.ReligionCivic, loaded.P(0).Civics);
    }
}
