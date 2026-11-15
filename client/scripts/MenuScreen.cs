using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using IH.Shared;

namespace IH.Client;

/// <summary>Connection form, list of games on the server, and the pre-game room.</summary>
public partial class MenuScreen : Control
{
    private readonly Main _main;
    private readonly LineEdit _name = new(), _host = new(), _port = new(), _gameName = new() { Text = "Новая игра" };
    private readonly Label _status = Ui.Lbl("", 14, Ui.Bad);
    private readonly VBoxContainer _connectBox, _lobbyBox, _roomBox, _games = Ui.V(4), _slots = Ui.V(4);
    private readonly OptionButton _mapSize = new(), _mode = new(), _mapType = new();
    private readonly SpinBox _ai = new() { MinValue = 0, MaxValue = 8, Value = 2 }, _humans = new() { MinValue = 1, MaxValue = 8, Value = 4 },
        _timer = new() { MinValue = 0, MaxValue = 600, Step = 15, Value = 0, Suffix = " с" };
    private readonly Label _roomTitle = Ui.Lbl("", 18, Ui.Gold), _roomInfo = Ui.Lbl("", 13, Ui.Dim);
    private Button _startBtn = null!, _readyBtn = null!;
    private readonly OptionButton _hostSave = new();
    private readonly Label _hostInfo = Ui.Lbl("Друзья подключаются к вашему IP из Radmin VPN. Откройте входящий UDP-порт в брандмауэре.", 12, Ui.Dim);
    private readonly System.Collections.Generic.List<string> _saves = LocalServer.Saves();
    private double _connectAt = -1;
    private bool _steamHostPending;
    private int _sandboxPort;
    private readonly OptionButton _steamSave = new();
    private readonly Button _steamMode = new() { Text = "Steam" }, _radminMode = new() { Text = "Radmin" };
    private readonly VBoxContainer _lanBox, _steamBox, _steamGames = Ui.V(4);
    private readonly VBoxContainer _lanGames = Ui.V(3);
    private readonly HashSet<(string Address, int Port)> _lanEndpoints = new();
    private readonly Label _steamStatus = Ui.Lbl("", 13, Ui.Dim);
    public bool AutoHost, AutoJoin;
    public void SetMapType(int t) => _mapType.Selected = Mathf.Clamp(t, 0, 2);
    public void SelectLatestSave() { if (_saves.Count > 0) _hostSave.Selected = 1; }

    private static readonly (string label, int w, int h)[] Sizes =
    {
        ("Маленькая 40x26", 40, 26), ("Средняя 56x36", 56, 36), ("Большая 80x52", 80, 52), ("Огромная 112x72", 112, 72),
    };

    public MenuScreen(Main main)
    {
        _main = main;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var panel = Ui.Panel(24, Ui.Col(0x10172A, 0.98f), Ui.PanelEdge, 12);
        panel.CustomMinimumSize = new Vector2(560, 0);
        center.AddChild(panel);
        var root = Ui.V(10);
        panel.AddChild(root);

        root.AddChild(Ui.Lbl("INFINITE HORIZONS", 30, Ui.Gold, true));
        string version = OS.GetExecutablePath().GetBaseDir().PathJoin("version.txt");
        if (FileAccess.FileExists(version)) root.AddChild(Ui.Lbl(FileAccess.GetFileAsString(version).Trim(), 12, Ui.Dim));
        root.AddChild(new HSeparator());

        _name.Text = main.Setting("name", ""); _name.PlaceholderText = "Ваше имя"; _name.MaxLength = 20;
        _host.Text = main.Setting("host", "127.0.0.1"); _host.PlaceholderText = "IP хоста в Radmin VPN, напр. 26.123.45.67";
        _port.Text = main.Setting("port", "27015");
        foreach (var e in new[] { _name, _host, _port, _gameName }) e.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        _steamMode.Pressed += () => SetMode(1);
        _radminMode.Pressed += () => SetMode(0);
        _steamMode.SizeFlagsHorizontal = _radminMode.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var lanScroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 75), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _lanGames.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        lanScroll.AddChild(_lanGames);
        _lanBox = Ui.V(8,
            Ui.H(8, Ui.Lbl("Друзья в Radmin", 16, Ui.Gold, true), Ui.Spacer(), Ui.Btn("↻", DiscoverLan, "Обновить список")),
            lanScroll,
            Row("Адрес сервера", _host), Row("Порт (UDP)", _port),
            Ui.Btn("Подключиться", ConnectPressed, size: 16, variation: "Primary"),
            new HSeparator(),
            Ui.Lbl("Или станьте хостом — сервер запустится на этом компьютере:", 13, Ui.Dim),
            Row("Партия", _hostSave),
            Ui.Btn("Создать сервер и подключиться", HostPressed, size: 16, variation: "Primary"),
            Ui.Btn("Локальная песочница", HostSandboxPressed, "Тестовая партия с ботами. Команды песочницы доступны по F12."),
            _hostInfo);
        var steamScroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 110) };
        steamScroll.AddChild(_steamGames);
        _steamGames.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _steamStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _steamStatus.CustomMinimumSize = new Vector2(500, 0);
        _steamBox = Ui.V(8,
            _steamStatus,
            Ui.H(8, Ui.Lbl("Друзья в Steam", 16, Ui.Gold), Ui.Spacer(), Ui.Btn("↻", SteamRefreshPressed, "Обновить список")),
            steamScroll,
            new HSeparator(),
            Ui.Lbl("Или станьте хостом — игра появится в списке у друзей, VPN и проброс портов не нужны:", 13, Ui.Dim),
            Row("Партия", _steamSave),
            Ui.Btn("Создать игру в Steam", HostSteamPressed, size: 16, variation: "Primary"));
        _connectBox = Ui.V(8, _name, Ui.H(4, _steamMode, _radminMode), new HSeparator(), _lanBox, _steamBox);
        root.AddChild(_connectBox);
        _steamSave.AddItem("Новая партия");
        foreach (string save in _saves) _steamSave.AddItem("Продолжить: " + save.GetFile());
        SteamNet.Changed += RefreshSteam;
        TreeExiting += () => SteamNet.Changed -= RefreshSteam;
        _main.Net.LanServerFound += AddLanServer;
        TreeExiting += () => _main.Net.LanServerFound -= AddLanServer;
        SetMode(main.Setting("conn", "1") == "1" ? 1 : 0, false);
        _hostInfo.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _hostInfo.CustomMinimumSize = new Vector2(500, 0);
        _hostSave.AddItem("Новая партия");
        foreach (string save in _saves) _hostSave.AddItem("Продолжить: " + save.GetFile() + "  (" + Time.GetDatetimeStringFromUnixTime((long)FileAccess.GetModifiedTime(save), true) + ")");

        for (int i = 0; i < Sizes.Length; i++) _mapSize.AddItem(Sizes[i].label, i);
        _mapSize.Selected = 1;
        _mode.AddItem("Одновременные ходы", 0); _mode.AddItem("Ходы по очереди", 1);
        _mapType.AddItem("Один континент", 0); _mapType.AddItem("Два континента", 1); _mapType.AddItem("Архипелаг (нужен флот)", 2);
        var gamesScroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 130) };
        gamesScroll.AddChild(_games);
        _games.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _lobbyBox = Ui.V(8,
            Ui.Lbl("Игры на сервере", 16, Ui.Gold), gamesScroll, new HSeparator(),
            Ui.Lbl("Создать игру", 16, Ui.Gold),
            Row("Название", _gameName), Row("Карта", _mapSize), Row("Тип карты", _mapType), Row("Режим ходов", _mode),
            Row("Мест для людей", _humans), Row("Противников-ИИ", _ai), Row("Таймер хода (0 — нет)", _timer),
            Ui.H(8, Ui.Btn("Создать", CreatePressed, size: 16, variation: "Primary"), Ui.Btn("Отключиться", () => _main.Net.Disconnect())));
        root.AddChild(_lobbyBox);

        _startBtn = Ui.Btn("Начать игру", () => _main.Net.Send(MsgType.StartGame), size: 16, variation: "Primary");
        _readyBtn = Ui.Btn("Готов", () =>
        {
            var room = _main.Net.Room;
            if (room != null) _main.Net.Send(MsgType.Ready, new BoolMsg { V = !room.Slots[room.YourSlot].Ready });
        });
        _roomBox = Ui.V(8, _roomTitle, _roomInfo, _slots,
            Ui.H(8, _startBtn, _readyBtn, Ui.Btn("Выйти", () => _main.Net.Send(MsgType.LeaveGame))));
        root.AddChild(_roomBox);
        root.AddChild(BuildMods());
        root.AddChild(_status);
        Refresh();
    }

    private static Control Row(string label, Control field)
    {
        var l = Ui.Lbl(label, 14);
        l.CustomMinimumSize = new Vector2(200, 0);
        field.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return Ui.H(8, l, field);
    }

    private Control BuildMods()
    {
        var list = Ui.V(3);
        var mods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["shamanism.json"] = "Шаманство", ["magic.json"] = "Магия",
        };
        string selection = LocalServer.ModSelection;
        var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (System.IO.File.Exists(selection))
                chosen.UnionWith(JsonSerializer.Deserialize<string[]>(System.IO.File.ReadAllText(selection)) ?? Array.Empty<string>());
            if (System.IO.Directory.Exists(LocalServer.ModsDir))
                foreach (string path in System.IO.Directory.EnumerateFiles(LocalServer.ModsDir, "*.json"))
                {
                    string name = System.IO.Path.GetFileName(path);
                    if (!name.Equals("launcher-selection.json", StringComparison.OrdinalIgnoreCase))
                        mods.TryAdd(name, System.IO.Path.GetFileNameWithoutExtension(path));
                }
        }
        catch (Exception e) when (e is JsonException or System.IO.IOException or UnauthorizedAccessException)
        {
            ShowError("Не удалось загрузить список модов");
        }
        foreach (var (name, title) in mods)
        {
            var option = new CheckBox { Text = title, ButtonPressed = chosen.Contains(name) };
            option.Toggled += on =>
            {
                if (on) chosen.Add(name); else chosen.Remove(name);
                try
                {
                    var parent = System.IO.Path.GetDirectoryName(selection)!;
                    System.IO.Directory.CreateDirectory(parent);
                    System.IO.File.WriteAllText(selection + ".new", JsonSerializer.Serialize(chosen.ToArray()));
                    System.IO.File.Move(selection + ".new", selection, overwrite: true);
                }
                catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
                {
                    if (on) chosen.Remove(name); else chosen.Add(name);
                    option.SetPressedNoSignal(!on);
                    ShowError("Не удалось сохранить выбор модов");
                }
            };
            list.AddChild(option);
        }
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 78), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(list);
        return Ui.V(3, Ui.Section("Моды"), scroll);
    }

    public void SetPlayerName(string name) => _name.Text = name;
    public void SetAddress(string host, string port) { _host.Text = host; _port.Text = port; }

    public void ShowError(string text)
    {
        _status.AddThemeColorOverride("font_color", Ui.Bad);
        _status.Text = text;
    }

    // ------------------------------------------------------------- Steam
    public void UseSteam() => SetMode(1);

    private void DiscoverLan()
    {
        if (!int.TryParse(_port.Text, out int port) || port is < 1 or > 65535) { ShowError("Проверьте UDP-порт"); return; }
        _lanEndpoints.Clear();
        Ui.Clear(_lanGames);
        _lanGames.AddChild(Ui.Lbl("Можно подключиться напрямую по адресу", 12, Ui.Dim));
        _main.Net.DiscoverLan(port);
    }

    private void AddLanServer(string address, int port)
    {
        if (!_lanEndpoints.Add((address, port))) return;
        if (_lanEndpoints.Count == 1) Ui.Clear(_lanGames);
        _lanGames.AddChild(Ui.H(6, Ui.Lbl($"{address}:{port}", 13), Ui.Spacer(),
            Ui.Btn("Подключиться", () => { _host.Text = address; _port.Text = port.ToString(); ConnectPressed(); })));
    }

    private void SetMode(int mode, bool save = true)
    {
        _lanBox.Visible = mode == 0;
        _steamBox.Visible = mode == 1;
        _steamMode.ThemeTypeVariation = mode == 1 ? "TabOn" : "Tab";
        _radminMode.ThemeTypeVariation = mode == 0 ? "TabOn" : "Tab";
        if (save) _main.SaveSetting("conn", mode.ToString());
        if (mode != 1) { DiscoverLan(); return; }
        if (SteamNet.Init()) SteamNet.RefreshGames();
        RefreshSteam();
    }

    private void SteamRefreshPressed()
    {
        if (SteamNet.Init()) SteamNet.RefreshGames();
        RefreshSteam();
    }

    private void RefreshSteam()
    {
        _steamStatus.Text = SteamNet.Ready ? "" : SteamNet.Status;
        _steamStatus.AddThemeColorOverride("font_color", SteamNet.Ready ? Ui.Good : Ui.Bad);
        Ui.Clear(_steamGames);
        if (!SteamNet.Ready) { _steamGames.AddChild(Ui.Lbl("Steam недоступен.", 13, Ui.Dim)); return; }
        if (SteamNet.Searching) { _steamGames.AddChild(Ui.Lbl("Поиск игр…", 13, Ui.Dim)); return; }
        if (SteamNet.Games.Count == 0)
            _steamGames.AddChild(Ui.Lbl("Игр не найдено. Попросите хоста создать игру и нажмите «Обновить список».", 13, Ui.Dim));
        foreach (var g in SteamNet.Games)
        {
            ulong id = g.HostId;
            var info = Ui.Lbl($"{g.HostName} — {g.Info}", 14);
            info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _steamGames.AddChild(Ui.H(8, info, Ui.Btn("Подключиться", () => JoinSteam(id))));
        }
    }

    private void JoinSteam(ulong hostId)
    {
        string name = _name.Text.Trim();
        if (name.Length == 0) { ShowError("Введите имя"); return; }
        _status.Text = "";
        int port = SteamNet.Join(hostId);
        _main.Connect(name, "127.0.0.1", port, $"steam:{hostId}:{name}");
    }

    public void HostSteamPressed()
    {
        if (!SteamNet.Init()) { RefreshSteam(); ShowError("Steam недоступен"); return; }
        if (!int.TryParse(_port.Text.Trim(), out int port) || port < 1 || port > 65535) { port = 27015; _port.Text = "27015"; }
        if (_name.Text.Trim().Length == 0) { ShowError("Введите имя"); return; }
        int pick = _steamSave.Selected - 1;
        string? error = LocalServer.Start(port, pick >= 0 && pick < _saves.Count ? _saves[pick] : null);
        if (error != null) { ShowError(error); return; }
        _host.Text = "127.0.0.1";
        _steamHostPending = true;
        _status.AddThemeColorOverride("font_color", Ui.Dim);
        _status.Text = "Запуск сервера…";
        _connectAt = Time.GetTicksMsec() / 1000.0 + 1.2;
    }

    public void HostPressed()
    {
        if (!int.TryParse(_port.Text.Trim(), out int port) || port < 1 || port > 65535) { ShowError("Порт — число от 1 до 65535"); return; }
        if (_name.Text.Trim().Length == 0) { ShowError("Введите имя"); return; }
        int pick = _hostSave.Selected - 1;
        string? error = LocalServer.Start(port, pick >= 0 && pick < _saves.Count ? _saves[pick] : null);
        if (error != null) { ShowError(error); return; }
        _host.Text = "127.0.0.1";
        _hostInfo.Text = LocalServer.AddressHint() + $", порт {port} (UDP). Сохранения: {LocalServer.SaveDir}";
        _hostInfo.AddThemeColorOverride("font_color", Ui.Gold);
        _status.AddThemeColorOverride("font_color", Ui.Dim);
        _status.Text = "Запуск сервера…";
        _connectAt = Time.GetTicksMsec() / 1000.0 + 1.2; // give the process a moment to open its socket
    }

    public void HostSandboxPressed()
    {
        if (_connectAt >= 0) return;
        if (_name.Text.Trim().Length == 0) _name.Text = "Игрок";
        const int port = 27016;
        string? error = LocalServer.Start(port, null, sandbox: true);
        if (error != null) { ShowError(error); return; }
        _sandboxPort = port;
        _status.AddThemeColorOverride("font_color", Ui.Dim);
        _status.Text = "Запуск локальной песочницы…";
        _connectAt = Time.GetTicksMsec() / 1000.0 + 1.2;
    }

    public override void _Process(double delta)
    {
        if (_connectAt < 0 || Time.GetTicksMsec() / 1000.0 < _connectAt) return;
        _connectAt = -1;
        if (!LocalServer.Running)
        {
            _steamHostPending = false;
            _sandboxPort = 0;
            ShowError(LocalServer.ConsumeStartupError() ?? "Сервер не запустился (возможно, порт занят)");
            return;
        }
        if (_sandboxPort > 0)
        {
            int port = _sandboxPort;
            _sandboxPort = 0;
            AutoHost = true;
            _main.Connect(_name.Text.Trim(), "127.0.0.1", port, "sandbox:local");
            return;
        }
        if (_steamHostPending)
        {
            _steamHostPending = false;
            SteamNet.StartHosting(int.Parse(_port.Text.Trim()));
            _main.Connect(_name.Text.Trim(), "127.0.0.1", int.Parse(_port.Text.Trim()), $"steam:host:{_name.Text.Trim()}");
            return;
        }
        ConnectPressed();
    }

    public void ConnectPressed()
    {
        string name = _name.Text.Trim(), host = _host.Text.Trim();
        if (name.Length == 0) { ShowError("Введите имя"); return; }
        if (host.Length == 0) { ShowError("Введите адрес сервера"); return; }
        if (!int.TryParse(_port.Text.Trim(), out int port) || port < 1 || port > 65535) { ShowError("Порт — число от 1 до 65535"); return; }
        _status.Text = "";
        _main.Connect(name, host, port);
    }

    private void CreatePressed()
    {
        var size = Sizes[Mathf.Clamp(_mapSize.Selected, 0, Sizes.Length - 1)];
        bool sandbox = _main.InSandbox;
        _main.Net.Send(MsgType.CreateGame, new GameSettings
        {
            Name = sandbox ? "Песочница" : _gameName.Text, MapW = size.w, MapH = size.h,
            AiCount = sandbox ? 2 : (int)_ai.Value, MaxPlayers = sandbox ? 1 : (int)_humans.Value,
            Simultaneous = _mode.Selected == 0, TurnTimerSec = (int)_timer.Value, MapType = _mapType.Selected,
        });
    }

    public void Refresh()
    {
        var net = _main.Net;
        _connectBox.Visible = net.State is NetState.Offline or NetState.Connecting or NetState.Reconnecting;
        _lobbyBox.Visible = net.State == NetState.Lobby;
        _roomBox.Visible = net.State == NetState.Room;
        if (net.State == NetState.Connecting)
        {
            _status.AddThemeColorOverride("font_color", Ui.Dim);
            _status.Text = "Подключение…";
        }
        else if (net.State == NetState.Reconnecting)
        {
            _status.AddThemeColorOverride("font_color", Ui.Dim);
            _status.Text = "Связь потеряна, переподключение…";
        }
        else if (net.State is NetState.Lobby or NetState.Room && _status.Text.EndsWith("…")) _status.Text = "";

        if (net.State == NetState.Lobby)
        {
            Ui.Clear(_games);
            if (net.Lobby.Games.Length == 0) _games.AddChild(Ui.Lbl("Пока нет ни одной игры — создайте свою.", 13, Ui.Dim));
            foreach (var g in net.Lobby.Games)
            {
                int id = g.Id;
                string state = g.Started ? $"идёт, ход {g.Turn}" : $"набор игроков {g.Players}/{g.MaxPlayers}";
                var info = Ui.Lbl($"{g.Name} — {state}", 14);
                info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                _games.AddChild(Ui.H(8, info, Ui.Btn(g.Started ? "Вернуться" : "Войти",
                    () => _main.Net.Send(MsgType.JoinGame, new IntMsg { V = id }),
                    g.Started ? "Занять своё место по сохранённому ключу" : null)));
            }
            if (AutoHost) { AutoHost = false; CreatePressed(); CallDeferred(nameof(AutoStart)); }
            if (AutoJoin)
            {
                foreach (var game in net.Lobby.Games)
                {
                    if (game.Started) continue;
                    AutoJoin = false;
                    _main.Net.Send(MsgType.JoinGame, new IntMsg { V = game.Id });
                    break;
                }
            }
        }
        if (net.State == NetState.Room && net.Room != null)
        {
            var room = net.Room;
            var st = room.Settings;
            _roomTitle.Text = room.Info.Name;
            _roomInfo.Text = $"Карта {st.MapW}x{st.MapH} · {(st.Simultaneous ? "одновременные ходы" : "ходы по очереди")}" +
                             (st.TurnTimerSec > 0 ? $" · таймер {st.TurnTimerSec} с" : "");
            Ui.Clear(_slots);
            for (int i = 0; i < room.Slots.Length; i++)
            {
                var s = room.Slots[i];
                string tag = s.AI ? "ИИ" : i == room.HostSlot ? "хост" : s.Ready ? "готов" : "не готов";
                _slots.AddChild(Ui.Lbl($"{i + 1}. {s.Name}  [{tag}]{(i == room.YourSlot ? "  ← вы" : "")}", 15,
                    s.AI ? Ui.Dim : Ui.Col(Rules.PlayerColors[i % Rules.PlayerColors.Length])));
            }
            bool host = room.YourSlot == room.HostSlot;
            _startBtn.Visible = host;
            _readyBtn.Visible = !host;
            _readyBtn.Text = room.Slots[room.YourSlot].Ready ? "Не готов" : "Готов";
        }
    }

    private void AutoStart() => _main.Net.Send(MsgType.StartGame);
}
