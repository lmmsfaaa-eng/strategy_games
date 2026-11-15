using IH.Shared;

namespace IH.Core;

public sealed partial class Game
{
    private const int MaxProgressionQueue = 256;

    private string? SetResearch(Player p, ProgressionOrder order, string id)
    {
        if (order != ProgressionOrder.ClearQueue)
        {
            var tech = Rules.Tech(id);
            if (tech == null || (tech.Branch >= 0 && id != Rules.FutureId(tech.Branch, p.Future[tech.Branch] + 1)) ||
                (order == ProgressionOrder.Immediate && !Rules.AvailableTechs(p.Known, p.Future).Any(t => t.Id == id)))
                return "Эта технология сейчас недоступна";
        }
        return SetProgressionOrder(p.ResearchQueue, ref p.Research, order, id,
            step => Rules.HasTech(p.Known, p.Future, step), step => Rules.Tech(step)?.Prereqs)
            ? null : "Не удалось изменить очередь технологий";
    }

    private string? SetCivic(Player p, ProgressionOrder order, string id)
    {
        if (order != ProgressionOrder.ClearQueue)
        {
            var civic = Rules.Civic(id);
            if (civic == null || (civic.Level > 0 && id != $"cf_{p.CivicLevel + 1}") ||
                (order == ProgressionOrder.Immediate && !Rules.AvailableCivics(p.Civics, p.CivicLevel).Any(c => c.Id == id)))
                return "Этот институт сейчас недоступен";
        }
        return SetProgressionOrder(p.CivicQueue, ref p.Civic, order, id,
            step => Rules.HasCivic(p.Civics, p.CivicLevel, step), step => Rules.Civic(step)?.Prereqs)
            ? null : "Не удалось изменить очередь институтов";
    }

    private static bool SetProgressionOrder(List<string> queue, ref string? active, ProgressionOrder order, string id,
        Func<string, bool> known, Func<string, string[]?> prerequisites)
    {
        switch (order)
        {
            case ProgressionOrder.Immediate:
                queue.Clear();
                queue.Add(id);
                break;
            case ProgressionOrder.ClearQueue:
                queue.Clear();
                break;
            case ProgressionOrder.ToggleQueue when queue.Contains(id):
                // The queue is topological, so every dependent appears after the item that removes it.
                var removed = new HashSet<string>(StringComparer.Ordinal) { id };
                foreach (string step in queue)
                    if (prerequisites(step)?.Any(removed.Contains) == true) removed.Add(step);
                queue.RemoveAll(removed.Contains);
                break;
            case ProgressionOrder.ToggleQueue:
                var additions = BuildPlan(new[] { id }, step => known(step) || queue.Contains(step), prerequisites);
                if (additions.Count == 0 || queue.Count + additions.Count > MaxProgressionQueue) return false;
                queue.AddRange(additions);
                break;
            default:
                return false;
        }
        active = queue.FirstOrDefault();
        return true;
    }

    private static void RestoreProgressionQueues(Player p)
    {
        p.ResearchQueue = BuildPlan(Goals(p.ResearchQueue, p.ResearchGoal ?? p.Research),
            id => Rules.HasTech(p.Known, p.Future, id), id => Rules.Tech(id)?.Prereqs);
        p.CivicQueue = BuildPlan(Goals(p.CivicQueue, p.CivicGoal ?? p.Civic),
            id => Rules.HasCivic(p.Civics, p.CivicLevel, id), id => Rules.Civic(id)?.Prereqs);
        p.ResearchGoal = null;
        p.CivicGoal = null;
        p.Research = p.ResearchQueue.FirstOrDefault();
        p.Civic = p.CivicQueue.FirstOrDefault();

        static IEnumerable<string> Goals(List<string>? queue, string? legacy)
            => queue is { Count: > 0 } ? queue : legacy != null ? new[] { legacy } : Array.Empty<string>();
    }

    private static void AdvanceResearchPlan(Player p)
    {
        p.ResearchQueue.RemoveAll(id => Rules.HasTech(p.Known, p.Future, id));
        p.Research = p.ResearchQueue.FirstOrDefault();
    }

    private static void AdvanceCivicPlan(Player p)
    {
        p.CivicQueue.RemoveAll(id => Rules.HasCivic(p.Civics, p.CivicLevel, id));
        p.Civic = p.CivicQueue.FirstOrDefault();
    }

    private static List<string> BuildPlan(IEnumerable<string> goals, Func<string, bool> known, Func<string, string[]?> prerequisites)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var queued = new HashSet<string>(StringComparer.Ordinal);
        var plan = new List<string>();
        return goals.All(Visit) ? plan : new List<string>();

        bool Visit(string id)
        {
            if (known(id) || queued.Contains(id)) return true;
            if (queued.Count + visiting.Count >= MaxProgressionQueue || !visiting.Add(id)) return false;
            if (prerequisites(id) is not { } requirements || !requirements.All(Visit)) return false;
            visiting.Remove(id);
            queued.Add(id);
            plan.Add(id);
            return true;
        }
    }
}
