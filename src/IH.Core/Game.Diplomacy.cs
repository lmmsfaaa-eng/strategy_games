using IH.Shared;

namespace IH.Core;

public sealed partial class Game
{
    private const int ProposalTurns = 5, LuxuryTurns = 10, DefenseTurns = 3;
    private const int SpyCost = 20, SpyTurns = 3, ReportTurns = 5;

    private Player? DiplomaticTarget(Player p, int id)
        => id >= 0 && id < S.Players.Count && id != p.Id && P(id).Alive && !P(id).Neutral && Rel(p.Id, id).Met
            ? P(id) : null;

    private bool CanTrade(Player a, Player b) =>
        !AtWar(a.Id, b.Id) && !a.Embargoes.Contains(b.Id) && !b.Embargoes.Contains(a.Id);

    private static bool Between(int from, int to, int a, int b) => (from == a && to == b) || (from == b && to == a);

    private string? CmdSetStatAccess(Player p, int id, int mask)
    {
        if (DiplomaticTarget(p, id) == null) return "С этой державой нет контакта";
        if (mask < 0 || mask > (int)StatAccess.All) return "Недопустимые категории данных";
        if (mask == 0) p.StatGrants.Remove(id);
        else p.StatGrants[id] = (StatAccess)mask;
        return null;
    }

    private StatAccess GrantedBy(Player owner, Player recipient) => owner.Id == recipient.Id
        ? StatAccess.All
        : owner.Alive && recipient.Alive && !owner.Neutral && !recipient.Neutral && Rel(owner.Id, recipient.Id).Met
            ? owner.StatGrants.GetValueOrDefault(recipient.Id)
            : StatAccess.None;

    private void StopTrade(int a, int b)
    {
        S.LuxuryAgreements.RemoveAll(x => Between(x.From, x.To, a, b));
        S.Proposals.RemoveAll(x => x.Kind == ProposalKind.Trade && Between(x.From, x.To, a, b));
    }

    private string? CmdSetEmbargo(Player p, int id, int enabled)
    {
        if (DiplomaticTarget(p, id) == null) return "С этой державой нет контакта";
        if (enabled is not (0 or 1)) return "Недопустимое состояние эмбарго";
        if (enabled == 1)
        {
            if (!p.Embargoes.Add(id)) return null;
            StopTrade(p.Id, id);
        }
        else if (!p.Embargoes.Remove(id)) return null;
        Emit(EventKind.Info, s: enabled == 1 ? $"«{p.Name}» вводит торговое эмбарго" : $"«{p.Name}» снимает эмбарго", only: id);
        return null;
    }

    private string? CmdCancelAlliance(Player p, int id)
    {
        var q = DiplomaticTarget(p, id);
        if (q == null || !Rel(p.Id, id).Allied) return "Оборонительный союз не заключён";
        Rel(p.Id, id).Allied = false;
        S.DefenseCalls.RemoveAll(x => Between(x.Recipient, x.Ally, p.Id, id));
        Emit(EventKind.Info, s: $"«{p.Name}» расторгает оборонительный союз", only: id);
        return null;
    }

    private string? CmdDeclareWar(Player p, int id)
    {
        var q = DiplomaticTarget(p, id);
        if (q == null) return "С этой державой нет контакта";
        var rel = Rel(p.Id, q.Id);
        if (rel.AtWar) return "Вы уже воюете";
        if (rel.Allied) return "Сначала расторгните оборонительный союз";
        DeclareWar(p, q);
        foreach (var ally in Active.Where(ally => ally.Id != p.Id && ally.Id != q.Id).ToList())
        {
            if (!Rel(q.Id, ally.Id).Allied) continue;
            var relationToAggressor = Rel(ally.Id, p.Id);
            if (relationToAggressor.AtWar || relationToAggressor.Allied) continue;
            var call = new DefenseCall { Id = S.NextId++, Recipient = ally.Id, Ally = q.Id, Aggressor = p.Id, Expires = S.Turn + DefenseTurns };
            S.DefenseCalls.Add(call);
            Emit(EventKind.Info, s: $"«{q.Name}» призывает вас к защите от державы «{p.Name}»", only: ally.Id);
        }
        return null;
    }

    private void DeclareWar(Player aggressor, Player defender)
    {
        int a = aggressor.Id, b = defender.Id;
        var rel = Rel(a, b);
        rel.Met = true; rel.AtWar = true; rel.OpenBorders = false; rel.SinceTurn = S.Turn;
        StopTrade(a, b);
        S.Proposals.RemoveAll(x => Between(x.From, x.To, a, b));
        S.DefenseCalls.RemoveAll(x => Between(x.Recipient, x.Aggressor, a, b));
        Emit(EventKind.WarDeclared, a, b);
    }

    private bool ValidDefenseCall(DefenseCall c)
        => S.Turn < c.Expires && P(c.Recipient).Alive && P(c.Ally).Alive && P(c.Aggressor).Alive
            && Rel(c.Recipient, c.Ally).Allied && Rel(c.Ally, c.Aggressor).AtWar
            && !Rel(c.Recipient, c.Aggressor).AtWar && !Rel(c.Recipient, c.Aggressor).Allied;

    private string? CmdRespondDefense(Player p, int id, bool accept)
    {
        var call = S.DefenseCalls.FirstOrDefault(x => x.Id == id && x.Recipient == p.Id);
        if (call == null || !ValidDefenseCall(call)) return "Призыв к защите уже неактуален";
        S.DefenseCalls.Remove(call);
        if (accept) DeclareWar(p, P(call.Aggressor));
        Emit(EventKind.Info, s: accept ? $"«{p.Name}» приняла призыв к защите" : $"«{p.Name}» отклонила призыв к защите", only: call.Ally);
        return null;
    }

    private string? CmdPropose(Player from, int id, int kind, string terms)
    {
        var to = DiplomaticTarget(from, id);
        if (to == null) return "С этой державой нет контакта";
        if (kind < 0 || kind > (int)ProposalKind.Trade) return "Неизвестный вид предложения";
        var pr = new Proposal { From = from.Id, To = to.Id, Kind = (ProposalKind)kind, Expires = S.Turn + ProposalTurns };
        if (pr.Kind == ProposalKind.Trade
            && (!TradeTerms.TryDecode(terms, out pr.Offered, out pr.Requested) || (pr.Offered.IsEmpty && pr.Requested.IsEmpty)))
            return "Укажите допустимые условия сделки";
        if (ProposalError(pr, checkRequested: false) is { } error) return error;
        if (S.Proposals.Any(x => x.From == from.Id && x.To == to.Id && x.Kind == pr.Kind && x.Expires > S.Turn))
            return "Такое предложение уже отправлено";
        if (to.IsAI)
        {
            if (ProposalError(pr, checkRequested: true) == null && SimpleAI.Accepts(this, to, pr))
                ApplyProposal(pr);
            else
                Emit(EventKind.Info, s: $"«{to.Name}» отклоняет ваше предложение", only: from.Id);
            return null;
        }
        pr.Id = S.NextId++;
        S.Proposals.Add(pr);
        Emit(EventKind.Proposal, from.Id, kind, only: to.Id);
        return null;
    }

    private string? CmdWithdrawProposal(Player p, int id)
    {
        var pr = S.Proposals.FirstOrDefault(x => x.Id == id && x.From == p.Id && x.Expires > S.Turn);
        if (pr == null) return "Предложение уже неактуально";
        S.Proposals.Remove(pr);
        Emit(EventKind.Info, s: $"«{p.Name}» отзывает предложение", only: pr.To);
        return null;
    }

    private string? CmdRespond(Player p, int id, bool accept)
    {
        var pr = S.Proposals.FirstOrDefault(x => x.Id == id && x.To == p.Id && x.Expires > S.Turn);
        if (pr == null) return "Предложение уже неактуально";
        S.Proposals.Remove(pr);
        var from = P(pr.From);
        if (!accept)
        {
            Emit(EventKind.Info, s: $"«{p.Name}» отклоняет ваше предложение", only: from.Id);
            return null;
        }
        if (ProposalError(pr, checkRequested: true) is { } error)
        {
            Emit(EventKind.Info, s: $"Предложение державе «{p.Name}» закрыто: условия больше не выполнимы", only: from.Id);
            return error;
        }
        ApplyProposal(pr);
        return null;
    }

    private string? ProposalError(Proposal pr, bool checkRequested)
    {
        var from = P(pr.From);
        var to = DiplomaticTarget(from, pr.To);
        if (!from.Alive || to == null) return "Предложение уже неактуально";
        var rel = Rel(pr.From, pr.To);
        return pr.Kind switch
        {
            ProposalKind.Peace => rel.AtWar ? null : "Вы не воюете",
            ProposalKind.OpenBorders => rel.AtWar || rel.OpenBorders ? "Сейчас нельзя открыть границы" : null,
            ProposalKind.Alliance => rel.AtWar || rel.Allied ? "Сейчас нельзя заключить союз" : null,
            ProposalKind.Trade when !CanTrade(from, to) => "Торговля недоступна во время войны или эмбарго",
            ProposalKind.Trade => TradeError(from, to, pr.Offered, pr.Requested, checkRequested),
            _ => "Неизвестный вид предложения",
        };
    }

    private string? TradeError(Player from, Player to, TradeSide offered, TradeSide requested, bool checkRequested)
    {
        if (!CanSupplyTrade(from, to, offered, checkRequested))
            return "Предлагаемые активы недоступны или получатель не может их принять";
        if (!checkRequested) return null;
        if (!CanSupplyTrade(to, from, requested, true) ||
            (long)from.Gold - offered.Gold + requested.Gold > int.MaxValue ||
            (long)to.Gold - requested.Gold + offered.Gold > int.MaxValue ||
            (long)from.Favor - offered.Favor + requested.Favor > int.MaxValue ||
            (long)to.Favor - requested.Favor + offered.Favor > int.MaxValue)
            return "Условия сделки больше не выполнимы";
        return null;
    }

    private bool CanSupplyTrade(Player source, Player recipient, TradeSide side, bool verifyRecipient)
    {
        if ((side.Gold > 0 && source.Gold < side.Gold) || (side.Favor > 0 && source.Favor < side.Favor)) return false;
        if (side.Luxury != 0 && (!HasConnectedResource(source, side.Luxury)
            || S.LuxuryAgreements.Any(x => x.From == source.Id && x.To == recipient.Id && x.Resource == side.Luxury && x.Expires > S.Turn)))
            return false;
        if (side.Tech.Length == 0) return true;
        var tech = Rules.Tech(side.Tech);
        return tech != null && tech.Branch < 0 && source.Known.Contains(tech.Id)
            && (!verifyRecipient || (!recipient.Known.Contains(tech.Id) && tech.Prereqs.All(recipient.Has)));
    }

    private bool HasConnectedResource(Player source, int id)
    {
        var resource = (Resource)id;
        if (!Rules.ResourceVisible(resource, source.Has)) return false;
        foreach (var tile in S.Tiles)
            if (tile.Owner == source.Id && tile.Resource == resource && (Rules.ResourceWorked(tile) || tile.CityId >= 0))
                return true;
        return false;
    }

    private void ApplyProposal(Proposal pr)
    {
        var rel = Rel(pr.From, pr.To);
        var a = P(pr.From);
        var b = P(pr.To);
        switch (pr.Kind)
        {
            case ProposalKind.Peace:
                rel.AtWar = false; rel.SinceTurn = S.Turn;
                S.DefenseCalls.RemoveAll(x => Between(x.Ally, x.Aggressor, pr.From, pr.To));
                Emit(EventKind.PeaceMade, pr.From, pr.To);
                break;
            case ProposalKind.OpenBorders:
                rel.OpenBorders = true;
                Emit(EventKind.BordersOpened, pr.From, pr.To);
                break;
            case ProposalKind.Alliance:
                rel.Allied = true;
                Emit(EventKind.Info, s: $"«{b.Name}» заключает с вами оборонительный союз", only: a.Id);
                Emit(EventKind.Info, s: $"«{a.Name}» заключает с вами оборонительный союз", only: b.Id);
                break;
            case ProposalKind.Trade:
                a.Gold += pr.Requested.Gold - pr.Offered.Gold;
                b.Gold += pr.Offered.Gold - pr.Requested.Gold;
                a.Favor += pr.Requested.Favor - pr.Offered.Favor;
                b.Favor += pr.Offered.Favor - pr.Requested.Favor;
                DeliverTrade(a, b, pr.Offered);
                DeliverTrade(b, a, pr.Requested);
                Emit(EventKind.Info, s: $"Сделка с державой «{b.Name}» заключена", only: a.Id);
                Emit(EventKind.Info, s: $"Сделка с державой «{a.Name}» заключена", only: b.Id);
                break;
        }
    }

    private void DeliverTrade(Player from, Player to, TradeSide side)
    {
        if (side.Luxury != 0)
            S.LuxuryAgreements.Add(new LuxuryAgreementDto { From = from.Id, To = to.Id, Resource = side.Luxury, Expires = S.Turn + LuxuryTurns });
        if (side.Tech.Length > 0) Learn(to, Rules.Tech(side.Tech)!);
    }

    private string? CmdEspionage(Player p, int target)
    {
        if (DiplomaticTarget(p, target) == null) return "С этой державой нет контакта";
        if (!p.Has("c_diplomacy")) return "Сначала изучите институт «Дипломатическая служба»";
        if (p.Espionage.Any(x => x.Observed < 0)) return "У вас уже есть активная разведывательная миссия";
        if (p.Favor < SpyCost) return $"Нужно {SpyCost} одобрения";
        p.Favor -= SpyCost;
        p.Espionage.Add(new EspionageDto { Target = target, Completes = S.Turn + SpyTurns });
        return null;
    }

    private bool ValidLuxuryAgreement(LuxuryAgreementDto agreement)
        => agreement.Expires > S.Turn && P(agreement.From).Alive && P(agreement.To).Alive
            && CanTrade(P(agreement.From), P(agreement.To))
            && HasConnectedResource(P(agreement.From), agreement.Resource);

    private void RemoveDiplomacyForDefeated(int pid)
    {
        foreach (var other in S.Players.Where(x => x.Id != pid))
        {
            Rel(pid, other.Id).Allied = false;
            other.StatGrants.Remove(pid);
            other.Embargoes.Remove(pid);
            other.Espionage.RemoveAll(x => x.Target == pid && x.Observed < 0);
        }
        P(pid).StatGrants.Clear();
        P(pid).Embargoes.Clear();
        P(pid).Espionage.Clear();
        S.LuxuryAgreements.RemoveAll(x => x.From == pid || x.To == pid);
        S.DefenseCalls.RemoveAll(x => x.Recipient == pid || x.Ally == pid || x.Aggressor == pid);
    }

    private void TickDiplomacy()
    {
        S.Proposals.RemoveAll(x => x.Expires <= S.Turn);
        S.DefenseCalls.RemoveAll(c => !ValidDefenseCall(c));
        foreach (var p in Active)
        {
            p.Espionage.RemoveAll(x => x.Observed >= 0 && x.Expires <= S.Turn);
            var mission = p.Espionage.FirstOrDefault(x => x.Observed < 0 && x.Completes <= S.Turn);
            if (mission == null) continue;
            var target = P(mission.Target);
            p.Espionage.RemoveAll(x => x.Target == mission.Target && x.Observed >= 0);
            mission.Observed = S.Turn;
            mission.Expires = S.Turn + ReportTurns;
            mission.Gold = target.Gold;
            mission.GoldRate = target.GoldRate;
            mission.Era = Rules.EraOf(target.Known, target.Future);
            mission.Techs = target.TechCount;
            Emit(EventKind.Info, s: $"Готов доклад разведки о державе «{target.Name}»", only: p.Id);
        }
    }
}
