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
    private readonly Channel[] _channels;
    private readonly string[] _args;
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 56, TextAlign = ContentAlignment.MiddleCenter };
    private readonly CheckedListBox _mods = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly ComboBox _profile = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly FlowLayoutPanel _choices = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(24) };
    private bool _busy, _loadingMods;

    public LauncherWindow(string root, string[] args)
    {
        _args = args;
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _channels = new[]
        {
            new Channel("Kyzua", root, "kyzua", "lmmsfaaa-eng/strategy_games", "IH_KYZUA_UPDATE_URL",
                Path.Combine(appData, "InfiniteHorizonsKyzua", "mods")),
            new Channel("kazakhpoxuy", root, "original", "kazakhpox/strategy_games", "IH_UPDATE_URL",
                Path.Combine(appData, "Godot", "app_userdata", "Infinite Horizons", "mods")),
        };
        _channels[1].AdoptLegacyInstallation(Path.Combine(root, "game"));
        Text = "Infinite Horizons";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(420, 300);
        ClientSize = new Size(460, 325);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 11);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };
        var play = new TabPage("Выбор версии");
        foreach (var channel in _channels)
        {
            var button = new Button { Text = channel.Name, Width = 300, Height = 55, Margin = new Padding(0, 0, 0, 12) };
            button.Click += async (_, _) => await Start(channel, tabs);
            _choices.Controls.Add(button);
        }
        play.Controls.Add(_choices);
        var mods = new TabPage("Моды") { Padding = new Padding(12) };
        mods.Controls.Add(_mods);
        mods.Controls.Add(_profile);
        _profile.Items.AddRange(_channels.Select(c => c.Name).Cast<object>().ToArray());
        _profile.SelectedIndexChanged += (_, _) => RefreshMods();
        _mods.ItemCheck += SaveModSelection;
        _profile.SelectedIndex = 0;
        tabs.TabPages.Add(play);
        tabs.TabPages.Add(mods);
        Controls.Add(tabs);
        Controls.Add(_status);
        FormClosing += (_, e) => e.Cancel = _busy;
    }

    private void RefreshMods()
    {
        _loadingMods = true;
        try
        {
            var channel = _channels[_profile.SelectedIndex];
            var enabled = channel.ReadSelection();
            _mods.Items.Clear();
            foreach (var mod in channel.ListMods())
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
        try { _channels[_profile.SelectedIndex].WriteSelection(enabled); _status.Text = ""; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            e.NewValue = e.CurrentValue;
            _status.Text = $"Не удалось сохранить настройки модов: {error.Message}";
        }
    }

    private async Task Start(Channel selected, Control controls)
    {
        if (_busy) return;
        _busy = true;
        controls.Enabled = false;
        _status.Text = "Проверяем обновления обеих версий…";
        var other = _channels.Single(c => c != selected);
        var otherVersion = other.Latest(Http);
        try
        {
            string? warning = await Update(selected, await selected.Latest(Http));
            if (selected.InstalledVersion == null)
            {
                _status.Text = warning ?? $"{selected.Name}: версия недоступна. Проверьте интернет.";
                return;
            }
            selected.Launch(_args);
            if (other.InstalledVersion != null)
                await Update(other, await otherVersion);
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
            if (!IsDisposed) controls.Enabled = true;
        }
    }

    private async Task<string?> Update(Channel channel, string? latest)
    {
        if (latest == null || latest == channel.InstalledVersion) return null;
        try
        {
            var progress = new Progress<string>(status => _status.Text = $"{channel.Name} · {latest}\n{status}");
            await channel.Install(Http, latest, progress);
            return null;
        }
        catch (Exception error) when (error is IOException or HttpRequestException or UnauthorizedAccessException or OperationCanceledException)
        {
            string warning = $"{channel.Name}: не удалось обновить. {error.Message}";
            _status.Text = warning;
            return warning;
        }
    }
}

internal sealed record ModRow(string File, string Name)
{
    public override string ToString() => Name;
}

internal sealed class Channel
{
    private const string Version = "version.txt", Archive = "InfiniteHorizons-win-x64.zip", Game = "InfiniteHorizons.exe";
    private readonly string _root, _game, _mods, _url;
    private string Selection => Path.Combine(_mods, "launcher-selection.json");
    public string Name { get; }

    public Channel(string name, string root, string id, string repository, string overrideVariable, string mods)
    {
        Name = name;
        _root = Path.Combine(root, "versions", id);
        _game = Path.Combine(_root, "game");
        _mods = mods;
        string? endpoint = Environment.GetEnvironmentVariable(overrideVariable);
        _url = (endpoint ?? $"https://github.com/{repository}/releases/latest/download/").TrimEnd('/') + "/";
    }

    public string? InstalledVersion => File.Exists(Path.Combine(_game, Game)) &&
        File.Exists(Path.Combine(_game, Version)) ? File.ReadAllText(Path.Combine(_game, Version)).Trim() : null;

    public void AdoptLegacyInstallation(string legacy)
    {
        if (!Directory.Exists(legacy) || Directory.Exists(_game)) return;
        try
        {
            Directory.CreateDirectory(_root);
            Directory.Move(legacy, _game);
        }
        catch (IOException) { /* the old installation remains usable by the old launcher */ }
        catch (UnauthorizedAccessException) { }
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
            string value = (await http.GetStringAsync(_url + Version, timeout.Token)).Trim();
            return value is { Length: > 0 and <= 100 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? value : null;
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
            using (var response = await http.GetAsync(_url + Archive, HttpCompletionOption.ResponseHeadersRead))
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
