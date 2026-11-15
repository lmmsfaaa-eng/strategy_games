using IH.Shared;

namespace IH.Core;

public sealed partial class Game
{
    private int ProjectedGold(Player p) => (int)Math.Clamp(p.GoldRate + ContractIncome(p.Id).Sum(x => (long)x.Change), int.MinValue, int.MaxValue);

    private IncomeLineDto[] BuildIncomeLines(Player p)
    {
        var lines = new List<IncomeLineDto>();
        void Add(string resource, string source, int delta)
        {
            if (delta != 0) lines.Add(new IncomeLineDto { Resource = resource, Source = source, Delta = delta });
        }
        void Reconcile(string resource, string source, int total)
        {
            long missing = total - lines.Where(x => x.Resource == resource).Sum(x => (long)x.Delta);
            if (missing is >= int.MinValue and <= int.MaxValue) Add(resource, source, (int)missing);
        }
        foreach (var city in CitiesOf(p.Id).OrderBy(c => c.Id))
        {
            Add("gold", city.Name, (int)Math.Round(city.GoldRate));
            Add("science", city.Name, (int)Math.Round(city.SciRate));
            Add("culture", city.Name, (int)Math.Round(city.CulRate));
        }
        Add("science", "Научное эхо", (int)Math.Round(p.EchoBonus));
        Add("gold", "Содержание и давление", (int)Math.Floor(p.GoldRate - p.Income));
        foreach (var (other, change) in ContractIncome(p.Id).GroupBy(x => x.Other).Select(g => (g.Key, g.Sum(x => x.Change))))
            Add("gold", "Договор с державой «" + P(other).Name + "»", change);
        Reconcile("gold", "Прочие поступления и округление", ProjectedGold(p));
        Reconcile("science", "Прочие поступления и округление", (int)Math.Round(p.SciRate + p.EchoBonus));
        Reconcile("culture", "Прочие поступления и округление", (int)Math.Round(p.CultureRate));
        Add("faith", "Города и последователи", (int)p.FaithRate);
        Add("favor", "Единоверцы", p.Brethren);
        Reconcile("favor", "Институты и отношения с державами", (int)p.FavorRate);
        foreach (var branch in Rules.Branches)
            Add(branch.Id, "Узлы, здания и подключённые ресурсы", p.PowerRate.GetValueOrDefault(branch.Id));
        return lines.ToArray();
    }
}
