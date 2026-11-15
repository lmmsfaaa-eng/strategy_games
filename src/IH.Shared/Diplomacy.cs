using System.Globalization;

namespace IH.Shared;

[Flags]
public enum StatAccess : byte { None = 0, Economy = 1, Development = 2, Territory = 4, All = 7 }

public enum DealKind : byte { GoldPerTurn, Resource, Passage, Statistics, Alliance }
public enum ContractAction : byte { ProposeEnd, AcceptEnd, DeclineEnd, Breach }

public sealed class DealClause : IWire
{
    public byte Kind;
    public int Value, Turns;
    public void Sync(Wire w) { w.By(ref Kind); w.I(ref Value); w.I(ref Turns); }
}

public sealed class ContractDto : IWire
{
    public int Id, From, To, Started, EndRequestedBy = -1;
    public DealClause[] Offered = Array.Empty<DealClause>(), Requested = Array.Empty<DealClause>();
    public void Sync(Wire w)
    {
        w.I(ref Id); w.I(ref From); w.I(ref To); w.I(ref Started); w.I(ref EndRequestedBy);
        w.A(ref Offered); w.A(ref Requested);
    }
}

public sealed class IncomeLineDto : IWire
{
    public string Resource = "", Source = "";
    public int Delta;
    public void Sync(Wire w) { w.S(ref Resource); w.S(ref Source); w.I(ref Delta); }
}

public sealed class TradeSide : IWire
{
    public int Gold, Favor, Luxury;
    public string Tech = "";
    public DealClause[] Clauses = Array.Empty<DealClause>();
    public bool IsEmpty => Gold == 0 && Favor == 0 && Luxury == 0 && Tech.Length == 0 && Clauses.Length == 0;
    public bool HasAlliance => Clauses.Any(c => c.Kind == (byte)DealKind.Alliance);
    public void Sync(Wire w) { w.I(ref Gold); w.I(ref Favor); w.I(ref Luxury); w.S(ref Tech); w.A(ref Clauses); }
}

public static class TradeTerms
{
    public const int MaxAmount = 1_000_000;

    public static string ClauseName(DealClause clause)
    {
        string asset = (DealKind)clause.Kind switch
        {
            DealKind.GoldPerTurn => $"{clause.Value} золота за ход",
            DealKind.Resource => Rules.Res((Resource)clause.Value).Name,
            DealKind.Passage => "право прохода",
            DealKind.Statistics => "доступ к показателям",
            DealKind.Alliance => "оборонительный союз",
            _ => "",
        };
        return clause.Turns == 0 ? $"{asset} бессрочно" : $"{asset} на {clause.Turns} х.";
    }

    public static string Encode(TradeSide offered, TradeSide requested)
        => FormattableString.Invariant($"{offered.Gold}|{offered.Favor}|{offered.Luxury}|{offered.Tech}|{requested.Gold}|{requested.Favor}|{requested.Luxury}|{requested.Tech}|{Pack(offered.Clauses)}|{Pack(requested.Clauses)}");

    private static string Pack(DealClause[] clauses) => string.Join(';', clauses.Select(c => FormattableString.Invariant($"{c.Kind},{c.Value},{c.Turns}")));

    private static bool ReadClauses(string input, out DealClause[] clauses)
    {
        var result = new List<DealClause>();
        clauses = Array.Empty<DealClause>();
        if (input.Length == 0) return true;
        var seen = new HashSet<(DealKind Kind, int Key)>();
        foreach (string row in input.Split(';'))
        {
            var fields = row.Split(',');
            if (result.Count >= 20 || fields.Length != 3
                || !byte.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out byte kind)
                || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int turns)
                || turns is not (0 or 5 or 10 or 20)) return false;
            var category = (DealKind)kind;
            bool valid = category switch
            {
                DealKind.GoldPerTurn => value > 0 && value <= MaxAmount,
                DealKind.Resource => value > 0 && value < Rules.Resources.Length,
                DealKind.Statistics => value > 0 && value <= (int)StatAccess.All,
                DealKind.Passage or DealKind.Alliance => value == 1,
                _ => false,
            };
            if (!valid || !seen.Add((category, category == DealKind.Resource ? value : 0))) return false;
            result.Add(new DealClause { Kind = kind, Value = value, Turns = turns });
        }
        clauses = result.ToArray();
        return true;
    }

    public static bool TryDecode(string data, out TradeSide offered, out TradeSide requested)
    {
        offered = new TradeSide();
        requested = new TradeSide();
        if (data.Length > 1000) return false;
        var fields = data.Split('|');
        return (fields.Length is 8 or 10) && Read(0, offered) && Read(4, requested)
            && (fields.Length == 8 || (ReadClauses(fields[8], out offered.Clauses) && ReadClauses(fields[9], out requested.Clauses)));

        bool Read(int i, TradeSide side)
        {
            side.Tech = fields[i + 3];
            return int.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out side.Gold)
                && int.TryParse(fields[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out side.Favor)
                && int.TryParse(fields[i + 2], NumberStyles.None, CultureInfo.InvariantCulture, out side.Luxury)
                && side.Gold <= MaxAmount && side.Favor <= MaxAmount
                && (side.Luxury == 0 || (side.Luxury <= byte.MaxValue && Rules.Res((Resource)side.Luxury).Class == ResClass.Luxury))
                && (side.Tech.Length == 0 || Rules.Techs.Any(t => t.Id == side.Tech));
        }
    }
}

public sealed class LuxuryAgreementDto : IWire
{
    public int From, To, Resource, Expires;
    public void Sync(Wire w) { w.I(ref From); w.I(ref To); w.I(ref Resource); w.I(ref Expires); }
}

public sealed class DefenseCallDto : IWire
{
    public int Id, Ally, Aggressor, Expires;
    public string AggressorName = "";
    public void Sync(Wire w) { w.I(ref Id); w.I(ref Ally); w.I(ref Aggressor); w.I(ref Expires); w.S(ref AggressorName); }
}

public sealed class EspionageDto : IWire
{
    public int Target, Completes, Observed = -1, Expires, Gold, GoldRate, Era, Techs;
    public void Sync(Wire w)
    {
        w.I(ref Target); w.I(ref Completes); w.I(ref Observed); w.I(ref Expires);
        w.I(ref Gold); w.I(ref GoldRate); w.I(ref Era); w.I(ref Techs);
    }
}
