using IH.Core;
using IH.Server;
using IH.Shared;

namespace IH.Tests;

public class ContractDiplomacyTests
{
    private static TradeSide Clause(DealKind kind, int turns = 0) => new()
    {
        Clauses = new[] { new DealClause { Kind = (byte)kind, Value = 1, Turns = turns } },
    };

    private static string? Offer(Game game, int from, int to, TradeSide offered, TradeSide requested) =>
        game.Execute(from, new GameCommand(CmdType.OfferContract, to, s: TradeTerms.Encode(offered, requested)));

    [Fact]
    public void Friendship_requires_consent_and_ends_freely_for_both_nations()
    {
        var game = GameTests.New(2, 0);
        var relation = game.Rel(0, 1);
        relation.Met = relation.OpenBorders = relation.Allied = true;
        Assert.Equal(0, game.BuildView(0).Me.FavorRate);
        var friendship = Clause(DealKind.Friendship);
        Assert.NotNull(Offer(game, 0, 1, friendship, new TradeSide()));
        Assert.Null(Offer(game, 0, 1, friendship, friendship));
        Assert.False(relation.Friendly);
        int offer = Assert.Single(game.S.Proposals).Id;
        Assert.Null(game.Execute(1, new GameCommand(CmdType.Respond, offer, 1)));
        Assert.True(relation.Friendly);
        Assert.Equal(1, game.BuildView(0).Me.FavorRate);
        Assert.Equal(1, game.BuildView(1).Me.FavorRate);
        Assert.Empty(game.S.Contracts);
        int before = game.P(0).Favor;
        game.ForceEndTurn(0);
        Assert.Equal(before, game.P(0).Favor);
        game.ForceEndTurn(1);
        Assert.Equal(before + 1, game.P(0).Favor);
        Assert.Equal(1, game.P(1).Favor);
        game.ForceEndTurn(1);
        Assert.True(game.P(1).EndedTurn);
        Assert.Null(game.Execute(1, new GameCommand(CmdType.EndFriendship, 0)));
        Assert.False(relation.Friendly);
        Assert.Equal(0, game.BuildView(0).Me.FavorRate);
        Assert.Equal(0, game.BuildView(1).Me.FavorRate);
        Assert.Equal(-1, game.P(1).Reputation);
    }

    [Fact]
    public void Demands_are_consented_to_and_one_sided_guarantees_only_bind_the_giver()
    {
        var game = GameTests.New(2, 0);
        game.Rel(0, 1).Met = true;
        var guarantee = Clause(DealKind.NonAggression, 5);
        Assert.Equal(TradeIntent.Demand, TradeTerms.Intent(new TradeSide(), guarantee));
        Assert.Null(Offer(game, 1, 0, new TradeSide(), guarantee));
        Assert.False(game.GuaranteesNonAggression(0, 1));
        int offer = Assert.Single(game.S.Proposals).Id;
        Assert.Null(game.Execute(0, new GameCommand(CmdType.Respond, offer, 1)));
        Assert.True(game.GuaranteesNonAggression(0, 1));
        Assert.False(game.GuaranteesNonAggression(1, 0));
        Assert.NotNull(game.Execute(0, new GameCommand(CmdType.DeclareWar, 1)));
        Assert.Null(game.Execute(1, new GameCommand(CmdType.DeclareWar, 0)));
        Assert.Equal(-1, game.P(0).Reputation);
        Assert.Equal(-1, game.P(1).Reputation);
        Assert.Empty(game.S.Contracts);
    }

    [Fact]
    public void Guarantee_stops_allied_call_until_explicit_breach()
    {
        var game = GameTests.New(3, 0);
        game.Rel(0, 1).Met = game.Rel(0, 2).Met = game.Rel(1, 2).Met = true;
        game.Rel(0, 2).Allied = true;
        var guarantee = Clause(DealKind.NonAggression);
        Assert.Null(Offer(game, 0, 1, guarantee, guarantee));
        int proposal = Assert.Single(game.S.Proposals).Id;
        Assert.Null(game.Execute(1, new GameCommand(CmdType.Respond, proposal, 1)));
        Assert.True(game.GuaranteesNonAggression(0, 1));
        Assert.True(game.GuaranteesNonAggression(1, 0));
        int contract = Assert.Single(game.S.Contracts).Terms.Id;
        Assert.Null(game.Execute(1, new GameCommand(CmdType.DeclareWar, 2)));
        int call = Assert.Single(game.S.DefenseCalls).Id;
        Assert.NotNull(game.Execute(0, new GameCommand(CmdType.RespondDefense, call, 1)));
        Assert.Null(game.Execute(0, new GameCommand(CmdType.ManageContract, contract, (int)ContractAction.Breach)));
        Assert.Null(game.Execute(0, new GameCommand(CmdType.RespondDefense, call, 1)));
        Assert.True(game.AtWar(0, 1));
    }

    [Fact]
    public void Timed_guarantee_survives_embargo_and_expires_after_five_turns()
    {
        var game = GameTests.New(2, 0);
        game.Rel(0, 1).Met = true;
        Assert.Null(Offer(game, 0, 1, Clause(DealKind.NonAggression, 5), new TradeSide()));
        Assert.Null(game.Execute(0, new GameCommand(CmdType.SetEmbargo, 1, 1)));
        for (int turn = 0; turn < 5; turn++)
        {
            Assert.True(game.GuaranteesNonAggression(0, 1));
            Assert.NotNull(game.Execute(0, new GameCommand(CmdType.DeclareWar, 1)));
            game.ForceEndTurn(0);
            game.ForceEndTurn(1);
        }
        Assert.False(game.GuaranteesNonAggression(0, 1));
        Assert.Null(game.Execute(0, new GameCommand(CmdType.DeclareWar, 1)));
        Assert.Equal(100, game.P(0).Reputation);
    }

    [Fact]
    public void Waiver_replays_other_claims_and_preserves_legacy_reputation_across_save()
    {
        var game = GameTests.New(3, 0);
        game.P(0).Reputation = 60;
        game.P(0).HasBreachedContract = true;
        var passage = Clause(DealKind.Passage);
        foreach (int victim in new[] { 1, 2 })
        {
            game.Rel(0, victim).Met = true;
            Assert.Null(Offer(game, 0, victim, passage, new TradeSide()));
            int contract = Assert.Single(game.S.Contracts).Terms.Id;
            Assert.Null(game.Execute(0, new GameCommand(CmdType.ManageContract, contract, (int)ContractAction.Breach)));
        }
        Assert.Equal(7, game.P(0).Reputation);
        game = new Game(SaveGame.FromJson<GameState>(SaveGame.ToJson(game.S)));
        Assert.True(game.BuildView(1).Players.Single(p => p.Id == 0).HasClaim);
        Assert.True(game.BuildView(2).Players.Single(p => p.Id == 0).HasClaim);
        Assert.NotNull(game.Execute(1, new GameCommand(CmdType.WaiveClaims, 2)));
        Assert.Null(game.Execute(1, new GameCommand(CmdType.WaiveClaims, 0)));
        Assert.Equal(21, game.P(0).Reputation);
        Assert.False(game.BuildView(1).Players.Single(p => p.Id == 0).HasClaim);
        Assert.True(game.BuildView(2).Players.Single(p => p.Id == 0).HasClaim);
        Assert.NotNull(game.Execute(1, new GameCommand(CmdType.WaiveClaims, 0)));
        Assert.Null(game.Execute(2, new GameCommand(CmdType.WaiveClaims, 0)));
        Assert.Equal(60, game.P(0).Reputation);
        Assert.True(game.P(0).HasBreachedContract);
    }

    [Fact]
    public void Wire_delivers_demand_friendship_and_claims_to_their_respective_viewers()
    {
        var game = GameTests.New(3, 0);
        game.Rel(0, 1).Met = game.Rel(0, 2).Met = true;

        var guarantee = Clause(DealKind.NonAggression, 10);
        Assert.Null(Offer(game, 0, 1, new TradeSide(), guarantee));
        var demand = Assert.Single(Snapshot(1).Me!.Proposals);
        Assert.Equal(TradeIntent.Demand, TradeTerms.Intent(demand.Offered, demand.Requested));
        Assert.Equal(10, Assert.Single(demand.Requested.Clauses).Turns);
        Assert.Empty(Snapshot(2).Me!.Proposals);
        Assert.Null(game.Execute(1, new GameCommand(CmdType.Respond, demand.Id, 0)));

        var friendship = Clause(DealKind.Friendship);
        Assert.Null(Offer(game, 0, 1, friendship, friendship));
        int offer = Assert.Single(Snapshot(1).Me!.Proposals).Id;
        Assert.Null(game.Execute(1, new GameCommand(CmdType.Respond, offer, 1)));

        Assert.Null(Offer(game, 0, 1, guarantee, new TradeSide()));
        int contract = Assert.Single(game.S.Contracts).Terms.Id;
        Assert.Null(game.Execute(0, new GameCommand(CmdType.ManageContract, contract, (int)ContractAction.Breach)));

        var from = Snapshot(0);
        var to = Snapshot(1);
        var observer = Snapshot(2);
        Assert.True(from.Players.Single(p => p.Id == 1).Friendly);
        Assert.True(to.Players.Single(p => p.Id == 0).Friendly);
        Assert.Equal(1, from.Me!.FavorRate);
        Assert.Equal(1, to.Me!.FavorRate);
        Assert.True(to.Players.Single(p => p.Id == 0).HasClaim);
        Assert.False(observer.Players.Single(p => p.Id == 0).HasClaim);
        Assert.Equal(to.Players.Single(p => p.Id == 0).Reputation, observer.Players.Single(p => p.Id == 0).Reputation);

        DeltaMsg Snapshot(int viewer)
        {
            var latest = new DeltaTracker().Build(game, viewer, game.BuildView(viewer), new(), -1)!;
            byte[] packet = Codec.Encode(MsgType.Delta, latest);
            return Codec.Decode<DeltaMsg>(packet, 0, packet.Length);
        }
    }
}
