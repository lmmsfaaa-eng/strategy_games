using IH.Shared;

namespace IH.Core;

public sealed partial class Game
{
    private static bool Matches(ContractDto deal, int a, int b) => Between(deal.From, deal.To, a, b);

    private DealClause[] LiveClauses(ActiveContract contract, int giver, bool includeDue = false)
    {
        var d = contract.Terms;
        var set = giver == d.From ? d.Offered : d.Requested;
        return set.Where(c => c.Turns == 0 || d.Started + c.Turns >= S.Turn + (includeDue ? 0 : 1)).ToArray();
    }

    private IEnumerable<DealClause> GrantedClauses(int giver, int recipient) =>
        S.Contracts.Where(c => Matches(c.Terms, giver, recipient)).SelectMany(c => LiveClauses(c, giver));

    public bool AllowsPassage(int owner, int visitor) => owner != visitor && !AtWar(owner, visitor) &&
        (Rel(owner, visitor).OpenBorders || GrantedClauses(owner, visitor).Any(c => c.Kind == (byte)DealKind.Passage));

    public bool Allied(int a, int b) => a != b && (Rel(a, b).Allied
        || GrantedClauses(a, b).Concat(GrantedClauses(b, a)).Any(c => c.Kind == (byte)DealKind.Alliance));

    public bool GuaranteesNonAggression(int giver, int recipient) => giver != recipient && !AtWar(giver, recipient)
        && GrantedClauses(giver, recipient).Any(c => c.Kind == (byte)DealKind.NonAggression);

    private void RestoreContracts()
    {
        foreach (var old in S.LuxuryAgreements.Where(x => x.Expires > S.Turn))
            S.Contracts.Add(new ActiveContract
            {
                Terms = new ContractDto { Id = S.NextId++, From = old.From, To = old.To,
                    Started = old.Expires - LuxuryTurns, Offered = new[] { new DealClause { Kind = (byte)DealKind.Resource, Turns = LuxuryTurns, Value = old.Resource } } },
                LastCreditFrom = old.Expires - LuxuryTurns, LastCreditTo = old.Expires - LuxuryTurns,
            });
        S.LuxuryAgreements.Clear();
    }

    private void CreateContract(Proposal pr)
    {
        var a = pr.Offered.Clauses.Where(c => c.Kind != (byte)DealKind.Friendship).ToArray();
        var b = pr.Requested.Clauses.Where(c => c.Kind != (byte)DealKind.Friendship).ToArray();
        if (a.Length + b.Length == 0) return;
        S.Contracts.Add(new ActiveContract
        {
            Terms = new ContractDto { Id = S.NextId++, From = pr.From, To = pr.To, Started = S.Turn, Offered = a, Requested = b },
            LastCreditFrom = S.Turn, LastCreditTo = S.Turn,
        });
    }

    private string? CmdOfferContract(Player from, int id, int replace, string encoded)
    {
        var to = DiplomaticTarget(from, id);
        if (to == null) return "С этой державой нет контакта";
        if (!TradeTerms.TryDecode(encoded, out var offered, out var requested)
            || (offered.IsEmpty && requested.IsEmpty)) return "Добавьте условия сделки";
        var proposal = new Proposal { From = from.Id, To = id, Kind = ProposalKind.Trade,
            Expires = S.Turn + ProposalTurns, Offered = offered, Requested = requested };
        bool gift = TradeTerms.Intent(offered, requested) == TradeIntent.Gift;
        if (ProposalError(proposal, gift) is { } error) return error;
        var previous = replace == 0 ? null
            : S.Proposals.FirstOrDefault(p => p.Id == replace && p.From == id && p.To == from.Id && p.Expires > S.Turn);
        if (replace != 0 && previous == null) return "Предложение уже неактуально";
        if (!gift && S.Proposals.Any(p => p.From == from.Id && p.To == id && p.Kind == ProposalKind.Trade && p.Expires > S.Turn))
            return "Сначала отзовите предыдущее предложение";
        if (previous != null) S.Proposals.Remove(previous);
        if (gift)
        {
            ApplyProposal(proposal);
            Emit(EventKind.ContractGift, s: $"«{from.Name}» отправила вам подарок.", a: from.Id, only: id);
        }
        else if (to.IsAI)
        {
            if (ProposalError(proposal, true) == null && SimpleAI.Accepts(this, to, proposal)) ApplyProposal(proposal);
            else Emit(EventKind.Info, s: $"«{to.Name}» отклоняет ваше предложение", only: from.Id);
        }
        else
        {
            proposal.Id = S.NextId++;
            S.Proposals.Add(proposal);
            Emit(EventKind.ContractOffer, a: proposal.Id, b: from.Id, only: id);
        }
        return null;
    }

    private string? CmdManageContract(Player p, int id, ContractAction action)
    {
        var contract = S.Contracts.FirstOrDefault(c => c.Terms.Id == id && (c.Terms.From == p.Id || c.Terms.To == p.Id));
        if (contract == null) return "Договор уже не действует";
        var d = contract.Terms;
        int other = d.From == p.Id ? d.To : d.From;
        switch (action)
        {
            case ContractAction.Breach:
                if (IsOnlyIncomingGuarantee(contract, p.Id, other)) return "Нарушить гарантию может только держава, которая её дала";
                Breach(contract, p.Id);
                return null;
            case ContractAction.ProposeEnd:
                if (d.EndRequestedBy >= 0) return "Предложение уже ожидает ответа";
                if (P(other).IsAI)
                {
                    if (LiveClauses(contract, p.Id).Length == 0) EndByAgreement(contract);
                    else Emit(EventKind.ContractGift, s: $"«{P(other).Name}» отклонила прекращение договора.", only: p.Id);
                    return null;
                }
                d.EndRequestedBy = p.Id;
                Emit(EventKind.ContractHarm, s: $"«{p.Name}» предлагает прекратить договор. Ответьте в окне сделки.", only: other);
                return null;
            case ContractAction.AcceptEnd:
            case ContractAction.DeclineEnd:
                if (d.EndRequestedBy != other) return "Предложения о прекращении нет";
                if (action == ContractAction.AcceptEnd) EndByAgreement(contract);
                else
                {
                    d.EndRequestedBy = -1;
                    Emit(EventKind.Info, s: $"«{p.Name}» отклоняет прекращение договора.", only: other);
                }
                return null;
            default: return "Неизвестное действие с договором";
        }
    }

    private void EndByAgreement(ActiveContract contract)
    {
        S.Contracts.Remove(contract);
        Emit(EventKind.Info, s: "Договор прекращён по взаимному согласию.", only: contract.Terms.From);
        Emit(EventKind.Info, s: "Договор прекращён по взаимному согласию.", only: contract.Terms.To);
    }

    private void Breach(ActiveContract contract, int who)
    {
        var d = contract.Terms;
        int victim = d.From == who ? d.To : d.From;
        var p = P(who);
        p.ReputationClaims ??= new ReputationHistory
        {
            Score = p.Reputation, LastCredit = p.LastReputationCredit, HasBreached = p.HasBreachedContract,
        };
        RecordReputation(p, new ReputationEvent { Turn = S.Turn, Victim = victim });
        var lost = LiveClauses(contract, who, includeDue: true);
        if (lost.Length == 0) lost = LiveClauses(contract, victim, includeDue: true);
        S.Contracts.Remove(contract);
        Emit(EventKind.ContractBreach, s: $"«{p.Name}» нарушила договор с державой «{P(victim).Name}».", a: who, b: victim);
        string detail = string.Join(", ", lost.Select(TradeTerms.ClauseName));
        Emit(EventKind.ContractHarm, s: $"«{p.Name}» прекратила выполнять договор. Вы теряете: {detail}.", a: who, b: victim, only: victim);
    }

    private bool IsOnlyIncomingGuarantee(ActiveContract contract, int actor, int other) =>
        LiveClauses(contract, actor).Length == 0 && LiveClauses(contract, other) is { Length: > 0 } incoming
        && incoming.All(c => c.Kind == (byte)DealKind.NonAggression);

    private void BreachContractsBetween(int actor, int other, bool preserveGuarantees = false)
    {
        foreach (var contract in S.Contracts.Where(c => Matches(c.Terms, actor, other)).ToArray())
        {
            if (preserveGuarantees && LiveClauses(contract, actor).Concat(LiveClauses(contract, other))
                    .Any(c => c.Kind == (byte)DealKind.NonAggression)) continue;
            if (IsOnlyIncomingGuarantee(contract, actor, other)) S.Contracts.Remove(contract);
            else Breach(contract, actor);
        }
    }

    private static bool OwesClaim(Player offender, int victim) =>
        offender.ReputationClaims?.Events.Any(e => e.Victim == victim) == true;

    private string? CmdWaiveClaims(Player claimant, int id)
    {
        if (id < 0 || id >= S.Players.Count || id == claimant.Id || !OwesClaim(P(id), claimant.Id))
            return "Нет претензий к этой державе";
        var offender = P(id);
        var history = offender.ReputationClaims!;
        history.Events.RemoveAll(e => e.Victim == claimant.Id);
        offender.Reputation = history.Score;
        offender.LastReputationCredit = history.LastCredit;
        offender.HasBreachedContract = history.HasBreached;
        foreach (var entry in history.Events) ApplyReputation(offender, entry);
        if (history.Events.All(e => e.Victim < 0)) offender.ReputationClaims = null;
        Emit(EventKind.Info, s: $"«{claimant.Name}» отказалась от претензий к вам.", only: id);
        return null;
    }

    private static void RecordReputation(Player p, ReputationEvent entry)
    {
        var events = p.ReputationClaims?.Events;
        if (events != null && (entry.Victim >= 0 || events.Count == 0 || events[^1].Victim >= 0 || events[^1].Turn != entry.Turn))
            events.Add(entry);
        ApplyReputation(p, entry);
    }

    private static void ApplyReputation(Player p, ReputationEvent entry)
    {
        if (entry.Victim >= 0)
        {
            p.Reputation = Math.Min(25, (int)Math.Floor(0.35 * (p.Reputation < 0 ? 40 : p.Reputation)));
            p.HasBreachedContract = true;
        }
        else
        {
            if (p.Reputation >= 0 && entry.Turn - p.LastReputationCredit < 10) return;
            p.Reputation = p.HasBreachedContract
                ? Math.Min(90, p.Reputation + (int)Math.Ceiling(0.2 * (90 - p.Reputation)))
                : 100;
        }
        p.LastReputationCredit = entry.Turn;
    }

    private void TickContracts()
    {
        foreach (var contract in S.Contracts.OrderBy(x => x.Terms.Id).ToArray())
        {
            var d = contract.Terms;
            var from = P(d.From);
            var to = P(d.To);
            if (!from.Alive || !to.Alive) { S.Contracts.Remove(contract); continue; }
            var offering = LiveClauses(contract, d.From, includeDue: true);
            var requesting = LiveClauses(contract, d.To, includeDue: true);
            int outGold = Payment(offering), inGold = Payment(requesting);
            if (IsInDefault(from, offering, outGold, inGold)) { Breach(contract, from.Id); continue; }
            if (IsInDefault(to, requesting, inGold, outGold)) { Breach(contract, to.Id); continue; }

            from.Gold += inGold - outGold;
            to.Gold += outGold - inGold;
            Credit(from, offering, d.Started, ref contract.LastCreditFrom);
            Credit(to, requesting, d.Started, ref contract.LastCreditTo);
            if (LiveClauses(contract, d.From).Length + LiveClauses(contract, d.To).Length == 0)
                S.Contracts.Remove(contract);
        }

        bool IsInDefault(Player giver, DealClause[] clauses, int outgoing, int incoming) =>
            (outgoing > 0 && giver.Gold < outgoing) || (long)giver.Gold - outgoing + incoming > int.MaxValue
            || clauses.Any(c => c.Kind == (byte)DealKind.Resource && !HasConnectedResource(giver, c.Value));

        void Credit(Player player, DealClause[] due, int started, ref int last)
        {
            bool intervalCompleted = S.Turn - last >= 10;
            if (!due.Any(c => (c.Turns > 0 && started + c.Turns == S.Turn) || (c.Turns == 0 && intervalCompleted))) return;
            last = S.Turn;
            RecordReputation(player, new ReputationEvent { Turn = S.Turn });
        }
    }

    private static int Payment(IEnumerable<DealClause> clauses) =>
        clauses.Where(c => c.Kind == (byte)DealKind.GoldPerTurn).Sum(c => c.Value);

    private IEnumerable<(int Other, int Change)> ContractIncome(int pid)
    {
        foreach (var contract in S.Contracts)
        {
            var d = contract.Terms;
            if (pid != d.From && pid != d.To) continue;
            int other = pid == d.From ? d.To : d.From;
            int net = Payment(LiveClauses(contract, other)) - Payment(LiveClauses(contract, pid));
            if (net != 0) yield return (other, net);
        }
    }

    private IEnumerable<int> ContractImports(int pid) =>
        S.Contracts.Where(c => c.Terms.From == pid || c.Terms.To == pid)
            .SelectMany(c => LiveClauses(c, c.Terms.From == pid ? c.Terms.To : c.Terms.From))
            .Where(c => c.Kind == (byte)DealKind.Resource).Select(c => c.Value).Distinct();
}
