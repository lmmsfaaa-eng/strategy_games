using IH.Core;
using IH.Shared;

namespace IH.Tests;

public class ProgressionTests
{
    [Fact]
    public void Removing_a_prerequisite_preserves_the_order_of_independent_goals()
    {
        var game = GameTests.New(1, 0);
        Assert.Null(Set(game, CmdType.SetResearch, ProgressionOrder.ToggleQueue, "writing"));
        Assert.Null(Set(game, CmdType.SetResearch, ProgressionOrder.ToggleQueue, "sailing"));
        Assert.Null(Set(game, CmdType.SetResearch, ProgressionOrder.ToggleQueue, "pottery"));
        Assert.Equal(new[] { "agriculture", "sailing" }, game.BuildView(0).Me.ResearchPlan);
    }
    private static string? Set(Game game, CmdType type, ProgressionOrder order, string goal = "")
        => game.Execute(0, new GameCommand(type, (int)order, 0, 0, goal));

    [Fact]
    public void Deep_goals_complete_their_prerequisites_in_order_after_reloading()
    {
        var game = GameTests.New(1, 0);
        var settler = game.UnitsOf(0).Single(unit => unit.Def.Role == UnitRole.Settler);
        Assert.Null(game.Execute(0, new GameCommand(CmdType.FoundCity, settler.Id)));

        Assert.Null(Set(game, CmdType.SetResearch, ProgressionOrder.ToggleQueue, "code_of_laws"));
        Assert.Null(Set(game, CmdType.SetCivic, ProgressionOrder.ToggleQueue, "c_state"));
        string[] science = { "agriculture", "pottery", "writing", "mysticism", "priesthood", "code_of_laws" };
        string[] culture = { "c_tribal", "c_craft", "c_trade", "c_state" };
        game = new Game(SaveGame.FromJson<GameState>(SaveGame.ToJson(game.S)));
        var player = game.P(0);

        for (int i = 0; i < science.Length; i++)
        {
            var view = game.BuildView(0).Me;
            Assert.Equal(science[i], player.Research);
            Assert.Equal(science[i..], view.ResearchPlan);
            Assert.Equal(culture.Skip(i), view.CivicPlan);
            if (i < culture.Length) Assert.Equal(culture[i], player.Civic);
            player.ResearchProgress = player.CivicProgress = 500;
            game.ForceEndTurn(0);
            Assert.Contains(science[i], player.Known);
            if (i < culture.Length) Assert.Contains(culture[i], player.Civics);
        }

        Assert.Null(player.Research);
        Assert.Null(player.ResearchGoal);
        Assert.Null(player.Civic);
        Assert.Null(player.CivicGoal);
    }

    [Fact]
    public void Acquiring_a_queued_tech_outside_turns_advances_the_goal_and_preserves_progress()
    {
        var game = GameTests.New(1, 0);
        var player = game.P(0);
        player.ResearchProgress = 40;
        Assert.Null(Set(game, CmdType.SetResearch, ProgressionOrder.ToggleQueue, "metal_casting"));
        Assert.Equal("mining", player.Research);

        game.Learn(player, Rules.Tech("mining")!);
        Assert.Equal("bronze_working", player.Research);
        Assert.Equal(40, player.ResearchProgress);
        Assert.Null(Set(game, CmdType.SetResearch, ProgressionOrder.ClearQueue));
        Assert.Null(player.Research);
        Assert.Empty(game.BuildView(0).Me.ResearchPlan);

        Assert.Null(Set(game, CmdType.SetCivic, ProgressionOrder.ToggleQueue, "c_state"));
        Assert.Null(Set(game, CmdType.SetCivic, ProgressionOrder.Immediate, "c_tribal"));
        Assert.Null(player.CivicGoal);
        Assert.Equal("c_tribal", player.Civic);
    }

    [Fact]
    public void Next_future_goal_queues_its_cross_branch_requirement_and_rejects_invalid_jumps()
    {
        var game = GameTests.New(1, 0);
        var player = game.P(0);
        player.Known.UnionWith(Rules.Techs.Select(t => t.Id));
        player.Future[0] = 1;

        Assert.Null(Set(game, CmdType.SetResearch, ProgressionOrder.ToggleQueue, "f0_2"));
        Assert.Equal(new[] { "f1_1", "f0_2" }, game.BuildView(0).Me.ResearchPlan);
        Assert.NotNull(Set(game, CmdType.SetResearch, ProgressionOrder.ToggleQueue, "f0_3"));
        Assert.NotNull(Set(game, CmdType.SetResearch, ProgressionOrder.ToggleQueue, "f0_1"));
        Assert.NotNull(Set(game, CmdType.SetResearch, ProgressionOrder.ToggleQueue, "f0_100000"));
        Assert.Equal("f0_2", player.ResearchQueue.Last());
        Assert.Equal("f1_1", player.Research);
    }
}
