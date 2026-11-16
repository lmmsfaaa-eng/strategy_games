using IH.Shared;

namespace IH.Core;

public sealed partial class Game
{
    private readonly HashSet<int> _sandboxReveal = new();

    // The session server authenticates sandbox commands before calling this entry point.
    public string? ExecuteSandbox(int pid, GameCommand cmd)
    {
        var player = P(pid);
        if (!player.Alive || player.Neutral) return "Держава больше не участвует в партии";
        switch (cmd.Kind)
        {
            case CmdType.SandboxResources:
                player.Gold = (int)Math.Min(int.MaxValue, (long)player.Gold + 500);
                player.Faith = (int)Math.Min(int.MaxValue, (long)player.Faith + 100);
                player.Favor = (int)Math.Min(int.MaxValue, (long)player.Favor + 100);
                return null;
            case CmdType.SandboxReveal:
                if (!_sandboxReveal.Add(pid)) _sandboxReveal.Remove(pid);
                return null;
            case CmdType.SandboxSpawn:
                if ((uint)cmd.A >= S.Tiles.Length || (uint)cmd.B >= Rules.Units.Length)
                    return "Выберите клетку и тип юнита";
                if (!HasUnitSpace(pid, cmd.A, cmd.B)) return "На клетке нет свободного места";
                if (!CanSpawnUnit(pid, cmd.B, cmd.A)) return "Юнит не подходит для этой клетки";
                AddUnit(pid, cmd.B, cmd.A);
                return null;
            default:
                return "Неизвестная команда песочницы";
        }
    }
}
