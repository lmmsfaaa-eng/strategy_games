using IH.Core;
using IH.Server;
using IH.Shared;
using Xunit;

namespace IH.Tests;

public class DiplomacyTests
{
    private static string? Cmd(Game g, int pid, CmdType kind, int a = 0, int b = 0, string s = "")
        => g.Execute(pid, new GameCommand(kind, a, b, s: s));

    private static Game WithContacts(int players = 3)
    {
        var g = GameTests.New(players, 0);
        for (int a = 0; a < players; a++)
            for (int b = a + 1; b < players; b++) g.Rel(a, b).Met = true;
        return g;
    }

    private static void Advance(Game g, int turns)
    {
        for (int turn = 0; turn < turns; turn++)
            foreach (var player in g.Active.ToList()) Assert.Null(Cmd(g, player.Id, CmdType.EndTurn));
    }

    private static int Propose(Game g, int from, int to, ProposalKind kind, TradeSide? offer = null, TradeSide? request = null)
    {
        Assert.Null(Cmd(g, from, CmdType.Propose, to, (int)kind,
            kind == ProposalKind.Trade ? TradeTerms.Encode(offer ?? new(), request ?? new()) : ""));
        return g.S.Proposals.Last().Id;
    }

    [Fact]
    public void Exchange_checks_both_sides_before_moving_assets_and_cannot_be_replayed()
    {
        var g = WithContacts();
        var seller = g.P(0);
        var buyer = g.P(1);
        seller.Known.Add("writing");
        buyer.Known.Add("pottery");
        buyer.Favor = 8;
        int deal = Propose(g, 0, 1, ProposalKind.Trade, new() { Tech = "writing", Gold = 7 }, new() { Favor = 8 });
        Assert.Empty(g.BuildView(2).Me.Proposals);
        Assert.NotNull(Cmd(g, 2, CmdType.Respond, deal, 1));
        buyer.Favor = 7;
        Assert.NotNull(Cmd(g, 1, CmdType.Respond, deal, 1));
        Assert.False(buyer.Has("writing"));
        Assert.Equal(30, seller.Gold);
        Assert.NotNull(Cmd(g, 1, CmdType.Respond, deal, 1));

        buyer.Favor = 8;
        deal = Propose(g, 0, 1, ProposalKind.Trade, new() { Tech = "writing", Gold = 7 }, new() { Favor = 8 });
        Assert.Null(Cmd(g, 1, CmdType.Respond, deal, 1));
        Assert.True(buyer.Has("writing"));
        Assert.Equal(23, seller.Gold);
        Assert.Equal(37, buyer.Gold);
        Assert.Equal(8, seller.Favor);
        Assert.Equal(0, buyer.Favor);
        Assert.NotNull(Cmd(g, 1, CmdType.Respond, deal, 1));
    }

    [Fact]
    public void Offers_expire_are_withdrawable_and_embargo_stops_trade()
    {
        var g = WithContacts();
        int id = Propose(g, 0, 1, ProposalKind.Trade, new() { Gold = 1 });
        Assert.NotNull(Cmd(g, 1, CmdType.WithdrawProposal, id));
        Assert.Null(Cmd(g, 0, CmdType.WithdrawProposal, id));
        Assert.NotNull(Cmd(g, 1, CmdType.Respond, id, 1));
        id = Propose(g, 0, 1, ProposalKind.Trade, new() { Gold = 1 });
        Advance(g, 4);
        Assert.Single(g.BuildView(1).Me.Proposals);
        Advance(g, 1);
        Assert.NotNull(Cmd(g, 1, CmdType.Respond, id, 1));

        id = Propose(g, 0, 1, ProposalKind.Trade, new() { Gold = 1 });
        Assert.Null(Cmd(g, 1, CmdType.SetEmbargo, 0, 1));
        Assert.Empty(g.S.Proposals);
        Assert.True(g.BuildView(0).Players[1].Embargoed);
        Assert.NotNull(Cmd(g, 1, CmdType.Respond, id, 1));
        Assert.NotNull(g.Execute(0, new GameCommand(CmdType.Gift, 1, 0, 1)));
        Assert.Null(Cmd(g, 1, CmdType.SetEmbargo, 0, 0));
        Assert.Null(g.Execute(0, new GameCommand(CmdType.Gift, 1, 0, 1)));
    }

    [Fact]
    public void Defense_call_is_private_and_does_not_trigger_reciprocal_calls()
    {
        var g = WithContacts(4);
        void Ally(int a, int b)
        {
            int proposal = Propose(g, a, b, ProposalKind.Alliance);
            Assert.Null(Cmd(g, b, CmdType.Respond, proposal, 1));
        }
        Ally(0, 3);
        Ally(1, 2);
        Assert.NotNull(Cmd(g, 1, CmdType.DeclareWar, 2));
        Assert.Null(Cmd(g, 0, CmdType.DeclareWar, 1));

        var call = Assert.Single(g.BuildView(2).Me.DefenseCalls);
        Assert.Equal(0, call.Aggressor);
        Assert.Empty(g.BuildView(0).Me.DefenseCalls);
        Assert.False(g.AtWar(0, 2));
        Assert.NotNull(Cmd(g, 3, CmdType.RespondDefense, call.Id, 1));
        Assert.Null(Cmd(g, 2, CmdType.RespondDefense, call.Id, 1));
        Assert.True(g.AtWar(0, 2));
        Assert.Empty(g.BuildView(3).Me.DefenseCalls);
        Assert.NotNull(Cmd(g, 2, CmdType.RespondDefense, call.Id, 1));
    }

    [Fact]
    public void Data_grants_are_directional_and_revocation_clears_wire_delta_and_reconnect()
    {
        var g = WithContacts();
        g.P(0).Gold = 225;
        g.P(0).Known.Add("writing");
        var tracker = new DeltaTracker();
        DeltaMsg Snapshot(int viewer, DeltaTracker track)
        {
            var delta = track.Build(g, viewer, g.BuildView(viewer), new(), -1)!;
            byte[] packet = Codec.Encode(MsgType.Delta, delta);
            return Codec.Decode<DeltaMsg>(packet, 0, packet.Length);
        }
        var p = Snapshot(1, tracker).Players.Single(x => x.Id == 0);
        Assert.Equal(0, p.SharedStats);
        Assert.Equal(-1, p.Gold);
        Assert.Equal(-1, p.Era);
        Assert.Null(Cmd(g, 0, CmdType.SetStatAccess, 1, (int)StatAccess.Economy));
        p = Snapshot(1, tracker).Players.Single(x => x.Id == 0);
        Assert.Equal(225, p.Gold);
        Assert.Equal(-1, p.Techs);
        Assert.Equal(-1, p.Cities);
        var third = Snapshot(2, new DeltaTracker());
        Assert.Equal(-1, third.Players.Single(x => x.Id == 0).Gold);
        Assert.All(third.Me!.Demo, row => Assert.Equal(0, row.Peers));
        Assert.Equal(-1, g.BuildView(0).Players[1].Gold);

        Assert.Null(Cmd(g, 0, CmdType.SetStatAccess, 1, 0));
        p = Snapshot(1, tracker).Players.Single(x => x.Id == 0);
        Assert.Equal(0, p.SharedStats);
        Assert.Equal(-1, p.Gold);
        tracker.Reset();
        Assert.Equal(-1, Snapshot(1, tracker).Players.Single(x => x.Id == 0).Gold);
    }

    [Fact]
    public void Wire_omits_stat_categories_without_permission()
    {
        var player = new PlayerDto { SharedStats = (byte)StatAccess.Territory, Score = 110, Cities = 3 };
        var baseline = Wire.ToBytes(player);
        player.Gold = 654321;
        player.GoldRate = 100;
        player.Era = 6;
        player.Techs = 50;
        Assert.Equal(baseline, Wire.ToBytes(player));
    }

    [Fact]
    public void Espionage_charges_once_grants_a_private_snapshot_and_expires()
    {
        var g = WithContacts();
        g.P(0).Favor = 40;
        Assert.NotNull(Cmd(g, 0, CmdType.StartEspionage, 1));
        g.P(0).Civics.Add("c_diplomacy");
        Assert.Null(Cmd(g, 0, CmdType.StartEspionage, 1));
        Assert.Equal(20, g.P(0).Favor);
        Assert.NotNull(Cmd(g, 0, CmdType.StartEspionage, 2));
        Advance(g, 2);
        Assert.Equal(-1, Assert.Single(g.BuildView(0).Me.Espionage).Observed);
        Advance(g, 1);
        var ownView = g.BuildView(0);
        var report = Assert.Single(ownView.Me.Espionage);
        Assert.Equal(g.S.Turn, report.Observed);
        Assert.Equal(g.P(1).Gold, report.Gold);
        Assert.Equal(-1, ownView.Players[1].Gold);
        Assert.Empty(g.BuildView(1).Me.Espionage);
        Assert.Empty(g.BuildView(2).Me.Espionage);
        Advance(g, 5);
        Assert.Empty(g.BuildView(0).Me.Espionage);
    }

    [Fact]
    public void Legacy_offers_receive_a_deadline_and_default_to_no_sharing()
    {
        var original = WithContacts();
        original.S.Proposals.Add(new Proposal { Id = original.S.NextId++, From = 0, To = 1, Kind = ProposalKind.OpenBorders });
        var saved = SaveGame.ToJson(original.S);
        var g = new Game(SaveGame.FromJson<GameState>(saved));
        var received = Assert.Single(g.BuildView(1).Me.Proposals);
        Assert.Equal(g.S.Turn + 5, received.Expires);
        Assert.Equal(0, g.BuildView(1).Players[0].SharedStats);
        Assert.Empty(g.BuildView(0).Me.Espionage);
        Assert.Null(Cmd(g, 1, CmdType.Respond, received.Id, 1));
        Assert.True(g.Rel(0, 1).OpenBorders);
    }
}
