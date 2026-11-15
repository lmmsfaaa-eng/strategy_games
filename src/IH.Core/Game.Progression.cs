using IH.Shared;

namespace IH.Core;

public sealed partial class Game
{
    private string? SetResearch(Player p, ProgressionOrder order, string id)
    {
        if (order == ProgressionOrder.CancelGoal)
        {
            p.ResearchGoal = null;
            return null;
        }
        if (order == ProgressionOrder.SetGoal)
        {
            var tech = Rules.Tech(id);
            if (tech == null || (tech.Branch >= 0 && id != Rules.FutureId(tech.Branch, p.Future[tech.Branch] + 1)))
                return "Эта технология сейчас недоступна";
            var plan = ResearchPlan(p, id);
            if (plan.Length == 0) return "Эта технология уже изучена или недоступна";
            p.ResearchGoal = id;
            p.Research = plan[0];
            return null;
        }
        if (order != ProgressionOrder.Immediate || !Rules.AvailableTechs(p.Known, p.Future).Any(t => t.Id == id))
            return "Эта технология сейчас недоступна";
        p.ResearchGoal = null;
        p.Research = id;
        return null;
    }

    private string? SetCivic(Player p, ProgressionOrder order, string id)
    {
        if (order == ProgressionOrder.CancelGoal)
        {
            p.CivicGoal = null;
            return null;
        }
        if (order == ProgressionOrder.SetGoal)
        {
            var civic = Rules.Civic(id);
            if (civic == null || (civic.Level > 0 && id != $"cf_{p.CivicLevel + 1}"))
                return "Этот институт сейчас недоступен";
            var plan = CivicPlan(p, id);
            if (plan.Length == 0) return "Этот институт уже принят или недоступен";
            p.CivicGoal = id;
            p.Civic = plan[0];
            return null;
        }
        if (order != ProgressionOrder.Immediate || !Rules.AvailableCivics(p.Civics, p.CivicLevel).Any(c => c.Id == id))
            return "Этот институт сейчас недоступен";
        p.CivicGoal = null;
        p.Civic = id;
        return null;
    }

    private static string[] ResearchPlan(Player p, string? goal = null)
        => BuildPlan(goal ?? p.ResearchGoal, id => Rules.HasTech(p.Known, p.Future, id), id => Rules.Tech(id)?.Prereqs);

    private static string[] CivicPlan(Player p, string? goal = null)
        => BuildPlan(goal ?? p.CivicGoal, id => Rules.HasCivic(p.Civics, p.CivicLevel, id), id => Rules.Civic(id)?.Prereqs);

    private static void AdvanceResearchPlan(Player p)
    {
        if (p.ResearchGoal == null) return;
        p.Research = ResearchPlan(p).FirstOrDefault();
        if (p.Research == null) p.ResearchGoal = null;
    }

    private static void AdvanceCivicPlan(Player p)
    {
        if (p.CivicGoal == null) return;
        p.Civic = CivicPlan(p).FirstOrDefault();
        if (p.Civic == null) p.CivicGoal = null;
    }

    private static string[] BuildPlan(string? goal, Func<string, bool> known, Func<string, string[]?> prerequisites)
    {
        if (goal == null) return Array.Empty<string>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var queued = new HashSet<string>(StringComparer.Ordinal);
        var plan = new List<string>();
        return Visit(goal) ? plan.ToArray() : Array.Empty<string>();

        bool Visit(string id)
        {
            if (known(id) || queued.Contains(id)) return true;
            // Bound the walk even when a save has a malformed endless-tree target.
            if (queued.Count + visiting.Count >= 256 || !visiting.Add(id)) return false;
            if (prerequisites(id) is not { } requirements || !requirements.All(Visit)) return false;
            visiting.Remove(id);
            queued.Add(id);
            plan.Add(id);
            return true;
        }
    }
}
