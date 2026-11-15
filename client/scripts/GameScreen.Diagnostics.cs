using System.Linq;
using Godot;
using IH.Shared;

namespace IH.Client;

public partial class GameScreen
{
    private MarginContainer _diagnostics = null!;
    private readonly Label _diagnosticText = Ui.Lbl("", 13, Ui.Dim);
    private int _diagnosticTile = -1;
    private string _lastDiagnosticCommand = "—";
    private double _nextDiagnosticUpdate;

    private void BuildDiagnostics()
    {
        var panel = Ui.Panel(12);
        var rows = Ui.V(8, Ui.H(8, Ui.Lbl("Диагностика", 16, Ui.Gold), Ui.Spacer(), Ui.Btn("Закрыть", () => _diagnostics.Hide())));
        panel.AddChild(rows);
        _diagnosticText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _diagnosticText.CustomMinimumSize = new Vector2(330, 0);
        rows.AddChild(_diagnosticText);
        if (_main.InSandbox)
        {
            var types = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            for (int i = 0; i < Rules.Units.Length; i++) types.AddItem(Rules.Units[i].Name, i);
            rows.AddChild(new HSeparator());
            rows.AddChild(Ui.Lbl("Локальная песочница", 15, Ui.Gold));
            rows.AddChild(Ui.H(6,
                Ui.Btn("Выдать ресурсы", () => SendSandbox(CmdType.SandboxResources), "+500 золота, +100 веры, +100 одобрения"),
                Ui.Btn("Обзор всей карты", () => SendSandbox(CmdType.SandboxReveal), "Включить или выключить полный обзор")));
            rows.AddChild(Ui.H(6, types, Ui.Btn("Создать на выбранной клетке",
                () => SendSandbox(CmdType.SandboxSpawn, _diagnosticTile, types.GetSelectedId()))));
        }
        _diagnostics = Ui.Dock(panel, SizeFlags.ShrinkEnd, SizeFlags.ShrinkCenter);
        AddChild(_diagnostics);
        _diagnostics.Hide();
    }

    private void SendSandbox(CmdType kind, int a = 0, int b = 0)
    {
        if (_main.InSandbox && LocalServer.SandboxToken is { } token)
            Net.SendCommand(kind, a, b, s: token);
    }

    private void UpdateDiagnostics(double now)
    {
        if (!_diagnostics.Visible || now < _nextDiagnosticUpdate) return;
        _nextDiagnosticUpdate = now + 0.25;
        double fps = Engine.GetFramesPerSecond();
        string ping = Net.PingMs < 0 ? "—" : $"{Net.PingMs} мс";
        string units = _diagnosticTile < 0 ? "" : string.Join(", ",
            W.UnitsAt(_diagnosticTile).OrderBy(u => u.Id).Select(u => $"{u.Def.Name} #{u.Id}"));
        string cell = _diagnosticTile < 0 ? "щёлкните клетку на карте" : $"#{_diagnosticTile}";
        _diagnosticText.Text = $"{fps:F0} FPS · {(fps > 0 ? 1000 / fps : 0):F1} мс/кадр · сеть {ping}\n" +
            $"Команда {_lastDiagnosticCommand}\nКлетка {cell}\nЮниты {(units.Length == 0 ? "—" : units)}";
    }
}
