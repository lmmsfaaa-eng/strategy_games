using System.Globalization;

namespace IH.Shared;

[Flags]
public enum StatAccess : byte { None = 0, Economy = 1, Development = 2, Territory = 4, All = 7 }

public sealed class TradeSide : IWire
{
    public int Gold, Favor, Luxury;
    public string Tech = "";
    public bool IsEmpty => Gold == 0 && Favor == 0 && Luxury == 0 && Tech.Length == 0;
    public void Sync(Wire w) { w.I(ref Gold); w.I(ref Favor); w.I(ref Luxury); w.S(ref Tech); }
}

public static class TradeTerms
{
    public const int MaxAmount = 1_000_000;

    public static string Encode(TradeSide offered, TradeSide requested)
        => FormattableString.Invariant($"{offered.Gold}|{offered.Favor}|{offered.Luxury}|{offered.Tech}|{requested.Gold}|{requested.Favor}|{requested.Luxury}|{requested.Tech}");

    public static bool TryDecode(string data, out TradeSide offered, out TradeSide requested)
    {
        offered = new TradeSide();
        requested = new TradeSide();
        if (data.Length > 160) return false;
        var fields = data.Split('|');
        return fields.Length == 8 && Read(0, offered) && Read(4, requested);

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
