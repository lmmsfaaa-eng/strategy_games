using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IH.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string root = Path.GetFullPath(AppContext.BaseDirectory);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24];
        using var mutex = new Mutex(true, "InfiniteHorizonsLauncher-" + key, out bool unique);
        if (unique) Application.Run(new LauncherWindow(root, args));
    }
}

internal sealed class LauncherWindow : Form
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private const string GameTitle = "Infinite Horizons — Kuzua";
    private readonly GameInstallation _game;
    private readonly string[] _args;
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 56, TextAlign = ContentAlignment.MiddleCenter };
    private readonly CheckedListBox _mods = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(14, 6) };
    private readonly Button _play = new() { AutoSize = true, MinimumSize = new Size(230, 52), Anchor = AnchorStyles.None };
    private Task<string?>? _startupVersionCheck;
    private bool _busy, _loadingMods;

    public LauncherWindow(string root, string[] args)
    {
        _args = args;
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _game = new GameInstallation(root, Path.Combine(appData, "InfiniteHorizonsKyzua", "mods"));
        ShowVersion(_game.InstalledVersion);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(420, 300);
        ClientSize = new Size(460, 325);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 11);

        var play = new TabPage("Игра");
        var playLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        playLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        playLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        playLayout.Controls.Add(_play, 0, 0);
        _play.Click += async (_, _) => await StartGame();
        play.Controls.Add(playLayout);
        var mods = new TabPage("Моды") { Padding = new Padding(12) };
        mods.Controls.Add(_mods);
        _mods.ItemCheck += SaveModSelection;
        RefreshMods();
        _tabs.TabPages.Add(play);
        _tabs.TabPages.Add(mods);
        Controls.Add(_tabs);
        Controls.Add(_status);
        Shown += async (_, _) => await CheckOnOpen();
        FormClosing += (_, e) => e.Cancel = _busy;
    }

    private void ShowVersion(string? installed, string? available = null)
    {
        string version = installed ?? (available == null ? "версия не установлена" : $"доступна {available}");
        Text = $"{GameTitle} · {version}";
        _play.Text = installed == null ? "Установить и играть" : "Играть";
    }

    private async Task CheckOnOpen()
    {
        _startupVersionCheck = _game.Latest(Http);
        string? latest = await _startupVersionCheck;
        if (IsDisposed || _busy || latest == null) return;
        string? installed = _game.InstalledVersion;
        ShowVersion(installed, latest);
        if (installed != null && latest != installed)
            _status.Text = $"Доступно обновление {latest}";
    }

    private void RefreshMods()
    {
        _loadingMods = true;
        try
        {
            var enabled = _game.ReadSelection();
            _mods.Items.Clear();
            foreach (var mod in _game.ListMods())
                _mods.Items.Add(mod, enabled.Contains(mod.File));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            _status.Text = $"Не удалось прочитать список модов: {error.Message}";
        }
        finally { _loadingMods = false; }
    }

    private void SaveModSelection(object? sender, ItemCheckEventArgs e)
    {
        if (_loadingMods) return;
        var enabled = Enumerable.Range(0, _mods.Items.Count)
            .Where(i => i == e.Index ? e.NewValue == CheckState.Checked : _mods.GetItemChecked(i))
            .Select(i => ((ModRow)_mods.Items[i]).File).ToArray();
        try { _game.WriteSelection(enabled); _status.Text = ""; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            e.NewValue = e.CurrentValue;
            _status.Text = $"Не удалось сохранить настройки модов: {error.Message}";
        }
    }

    private async Task StartGame()
    {
        if (_busy) return;
        _busy = true;
        _tabs.Enabled = false;
        _status.Text = "Проверяем обновления…";
        try
        {
            string? latest = await (_startupVersionCheck is { IsCompleted: false } pending ? pending : _game.Latest(Http));
            string? warning = await Update(latest);
            string? installed = _game.InstalledVersion;
            ShowVersion(installed, latest);
            if (installed == null)
            {
                _status.Text = warning ?? "Версия недоступна. Проверьте подключение к интернету.";
                return;
            }
            _game.Launch(_args);
            _busy = false;
            Close();
        }
        catch (Exception error)
        {
            _status.Text = $"Не удалось запустить игру: {error.Message}";
        }
        finally
        {
            _busy = false;
            if (!IsDisposed) _tabs.Enabled = true;
        }
    }

    private async Task<string?> Update(string? latest)
    {
        if (latest == null || latest == _game.InstalledVersion) return null;
        try
        {
            var progress = new Progress<string>(status =>
            {
                if (!IsDisposed && _busy) _status.Text = $"{latest}\n{status}";
            });
            await _game.Install(Http, latest, progress);
            return null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException or OperationCanceledException)
        {
            string warning = $"Не удалось обновить игру. {error.Message}";
            _status.Text = warning;
            return warning;
        }
    }
}

internal sealed record ModRow(string File, string Name)
{
    public override string ToString() => Name;
}

internal sealed class GameInstallation
{
    private const string Version = "version.txt", Archive = "InfiniteHorizons-win-x64.zip", Game = "InfiniteHorizons.exe";
    private const string Releases = "https://github.com/lmmsfaaa-eng/strategy_games/releases/";
    private readonly string _root, _game, _mods;
    private readonly string? _updateOverride;
    private string Selection => Path.Combine(_mods, "launcher-selection.json");
    public GameInstallation(string root, string mods)
    {
        _root = Path.Combine(root, "versions", "kyzua");
        _game = Path.Combine(_root, "game");
        _mods = mods;
        string? endpoint = Environment.GetEnvironmentVariable("IH_KYZUA_UPDATE_URL");
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
            _updateOverride = url.AbsoluteUri.TrimEnd('/') + "/";
    }

    public string? InstalledVersion
    {
        get
        {
            string path = Path.Combine(_game, Version);
            if (!File.Exists(Path.Combine(_game, Game)) || !File.Exists(path)) return null;
            try { return ParseVersion(File.ReadAllText(path)); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    private static string? ParseVersion(string contents)
    {
        string version = contents.Trim();
        return version is { Length: > 0 and <= 100 } && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? version : null;
    }

    public HashSet<string> ReadSelection()
    {
        var files = File.Exists(Selection) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(Selection)) : null;
        return new HashSet<string>(files ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    public void WriteSelection(string[] files)
    {
        Directory.CreateDirectory(_mods);
        string stage = Selection + ".new";
        File.WriteAllText(stage, JsonSerializer.Serialize(files));
        File.Move(stage, Selection, overwrite: true);
    }

    public IEnumerable<ModRow> ListMods()
    {
        yield return new ModRow("shamanism.json", "Шаманство");
        yield return new ModRow("magic.json", "Магия");
        if (!Directory.Exists(_mods)) yield break;
        foreach (string path in Directory.GetFiles(_mods, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string file = Path.GetFileName(path);
            if (file.Equals("launcher-selection.json", StringComparison.OrdinalIgnoreCase)
                || file.Equals("shamanism.json", StringComparison.OrdinalIgnoreCase)
                || file.Equals("magic.json", StringComparison.OrdinalIgnoreCase)) continue;
            yield return new ModRow(file, Path.GetFileNameWithoutExtension(file));
        }
    }

    public async Task<string?> Latest(HttpClient http)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            return ParseVersion(await http.GetStringAsync((_updateOverride ?? Releases + "latest/download/") + Version, timeout.Token));
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) { return null; }
    }

    public async Task Install(HttpClient http, string version, IProgress<string> progress)
    {
        Directory.CreateDirectory(_root);
        string staging = Path.Combine(_root, ".install-" + Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(_root, ".backup-" + Guid.NewGuid().ToString("N"));
        string download = staging + ".zip";
        try
        {
            string archiveUrl = (_updateOverride ?? Releases + $"download/{Uri.EscapeDataString(version)}/") + Archive;
            using (var response = await http.GetAsync(archiveUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
                await using var file = File.Create(download);
                long total = response.Content.Headers.ContentLength ?? -1, size = 0;
                var buffer = new byte[65536];
                int count, last = -1;
                while ((count = await source.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    size += count;
                    int shown = total > 0 ? (int)(size * 100 / total) : (int)(size >> 20);
                    if (shown == last) continue;
                    last = shown;
                    progress.Report(total > 0 ? $"Загрузка: {shown}%" : $"Загружено {shown} МБ");
                }
                if (total >= 0 && total != size) throw new IOException("Неполная загрузка архива");
            }
            progress.Report("Распаковка…");
            await Task.Run(() => ZipFile.ExtractToDirectory(download, staging));
            if (!File.Exists(Path.Combine(staging, Game)) || !File.Exists(Path.Combine(staging, Version))
                || File.ReadAllText(Path.Combine(staging, Version)).Trim() != version)
                throw new InvalidDataException("Архив не соответствует версии из канала обновления");

            if (Directory.Exists(_game)) Directory.Move(_game, backup);
            try { Directory.Move(staging, _game); }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(_game)) Directory.Move(backup, _game);
                throw;
            }
            RemoveTemporary(backup);
        }
        finally
        {
            try { File.Delete(download); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            RemoveTemporary(staging);
        }
    }

    private void RemoveTemporary(string path)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase))
            return;
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Launch(IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(Path.Combine(_game, Game)) { WorkingDirectory = _game, UseShellExecute = false };
        start.Environment["IH_MODS_DIR"] = _mods;
        start.Environment["IH_MOD_SELECTION"] = Selection;
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var game = Process.Start(start) ?? throw new InvalidOperationException("Процесс игры не создан");
    }
}
