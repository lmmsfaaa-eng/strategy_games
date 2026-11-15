using IH.Core;
using IH.Shared;

namespace IH.Tests;

public class ContractAndPurchaseTests
{
    [Fact]
    public void Gold_purchase_places_a_unit_immediately_and_full_stack_costs_nothing()
    {
        var g = GameTests.New(1, 0);
        var p = g.P(0);
        var settler = g.UnitsOf(0).First(u => u.Def.Role == UnitRole.Settler);
        Assert.Null(g.Execute(0, new GameCommand(CmdType.FoundCity, settler.Id)));
        var city = g.CitiesOf(0).Single();
        p.Gold = 500;
        Assert.Null(g.Execute(0, new GameCommand(CmdType.SetProduction, city.Id, s: "u:warrior")));
        int turn = g.S.Turn;
        Assert.Null(g.Execute(0, new GameCommand(CmdType.Buy, city.Id)));
        Assert.Equal(turn, g.S.Turn);
        Assert.Equal(2, g.At(city.Tile).Count);
        Assert.Equal(0, g.At(city.Tile).OrderBy(u => u.Id).Last().Moves);
        Assert.Empty(city.Queue);
        g.AddUnit(0, Rules.UnitIndex("warrior"), city.Tile);
        Assert.Null(g.Execute(0, new GameCommand(CmdType.SetProduction, city.Id, s: "u:warrior")));
        int gold = p.Gold;
        Assert.NotNull(g.Execute(0, new GameCommand(CmdType.Buy, city.Id)));
        Assert.Equal(gold, p.Gold);
        Assert.Equal("u:warrior", Assert.Single(city.Queue));
    }

    [Fact]
    public void Gift_and_breach_apply_directional_permissions_and_keep_details_private()
    {
        var g = GameTests.New(3, 0);
        g.Rel(0, 1).Met = true;
        g.Rel(0, 2).Met = true;
        g.P(0).Gold = 100;
        var gift = new TradeSide
        {
            Gold = 10,
            Clauses = new[]
            {
                new DealClause { Kind = (byte)DealKind.Passage, Value = 1 },
                new DealClause { Kind = (byte)DealKind.Statistics, Value = (int)StatAccess.Economy },
            },
        };
        Assert.Null(g.Execute(0, new GameCommand(CmdType.OfferContract, 1, s: TradeTerms.Encode(gift, new TradeSide()))));
        Assert.Equal(90, g.P(0).Gold);
        Assert.True(g.AllowsPassage(0, 1));
        Assert.False(g.AllowsPassage(1, 0));
        Assert.Equal((byte)StatAccess.Economy, g.BuildView(1).Players.Single(p => p.Id == 0).SharedStats);
        Assert.Empty(g.BuildView(2).Me.Contracts);
        int deal = Assert.Single(g.BuildView(1).Me.Contracts).Id;
        Assert.NotNull(g.Execute(2, new GameCommand(CmdType.ManageContract, deal, (int)ContractAction.Breach)));
        Assert.Null(g.Execute(0, new GameCommand(CmdType.ManageContract, deal, (int)ContractAction.Breach)));
        Assert.False(g.AllowsPassage(0, 1));
        Assert.Equal(14, g.P(0).Reputation);
        Assert.Contains(g.Events, e => e.Dto.Type == EventKind.ContractBreach && e.OnlyPlayer < 0);
        Assert.Contains(g.Events, e => e.Dto.Type == EventKind.ContractHarm && e.OnlyPlayer == 1);
    }

    [Fact]
    public void Recurring_payment_credits_fulfillment_and_missing_gold_breaks_term_atomically()
    {
        var g = GameTests.New(2, 0);
        g.Rel(0, 1).Met = true;
        var sender = g.P(0);
        sender.Gold = 100;
        var terms = new TradeSide { Clauses = new[] { new DealClause { Kind = (byte)DealKind.GoldPerTurn, Value = 5, Turns = 5 } } };
        Offer();
        for (int i = 0; i < 5; i++) NextTurn();
        Assert.Equal(100, sender.Reputation);
        Assert.Empty(g.S.Contracts);
        Offer();
        sender.Gold = 0;
        var receiver = g.P(1);
        int prior = receiver.Gold;
        NextTurn();
        Assert.Equal(prior, receiver.Gold);
        Assert.Equal(25, sender.Reputation);
        Assert.Empty(g.S.Contracts);
        sender.Gold = 200;
        Offer();
        for (int i = 0; i < 5; i++) NextTurn();
        Assert.Equal(25, sender.Reputation);
        Offer();
        for (int i = 0; i < 5; i++) NextTurn();
        Assert.Equal(38, sender.Reputation);

        void Offer() => Assert.Null(g.Execute(0, new GameCommand(CmdType.OfferContract, 1, s: TradeTerms.Encode(terms, new TradeSide()))));
        void NextTurn() { g.ForceEndTurn(0); g.ForceEndTurn(1); }
    }
}
