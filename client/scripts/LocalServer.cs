using System.Collections.Generic;
using System.Linq;
using Godot;

namespace IH.Client;

/// <summary>Lets the hosting player start the dedicated server from the client instead of a console.</summary>
public static class LocalServer
{
    private static int _pid = -1;
    public static string? SandboxToken { get; private set; }

    public static bool Running => _pid > 0 && OS.IsProcessRunning(_pid);
    public static string SaveDir => OS.GetUserDataDir().PathJoin("saves");
    /// <summary>Custom branch files; kept in the user's data folder so game updates never touch them.</summary>
    public static string ModsDir => OS.GetUserDataDir().PathJoin("mods");

    /// <summary>Program and leading arguments, or null when no server build can be found.</summary>
    private static (string program, string[] args)? Locate()
    {
        var candidates = new List<string>();
        string env = OS.GetEnvironment("IH_SERVER");
        if (env.Length > 0) candidates.Add(env);
        string exeDir = OS.GetExecutablePath().GetBaseDir();
        foreach (string name in new[] { "IH.Server.exe", "IH.Server", "IH.Server.dll" })
        {
            candidates.Add(exeDir.PathJoin("server").PathJoin(name));
            candidates.Add(exeDir.PathJoin(name));
        }
        // running from the editor: use the build output of the repository
        string repo = ProjectSettings.GlobalizePath("res://").TrimEnd('/').GetBaseDir();
        foreach (string cfg in new[] { "Release", "Debug" })
            candidates.Add(repo.PathJoin($"src/IH.Server/bin/{cfg}/net8.0/IH.Server.dll"));

        foreach (string path in candidates)
        {
            if (!FileAccess.FileExists(path)) continue;
            return path.EndsWith(".dll") ? ("dotnet", new[] { path }) : (path, System.Array.Empty<string>());
        }
        return null;
    }

    /// <summary>Saved games of earlier locally hosted sessions, newest first.</summary>
    public static List<string> Saves()
    {
        var res = new List<(string path, ulong time)>();
        using var dir = DirAccess.Open(SaveDir);
        if (dir == null) return new List<string>();
        foreach (string f in dir.GetFiles())
            if (f.EndsWith(".json")) res.Add((SaveDir.PathJoin(f), FileAccess.GetModifiedTime(SaveDir.PathJoin(f))));
        return res.OrderByDescending(x => x.time).Select(x => x.path).ToList();
    }

    /// <summary>Starts the server. Returns null on success or a message for the player.</summary>
    public static string? Start(int port, string? loadPath, bool sandbox = false)
    {
        if (Running) return sandbox || SandboxToken != null ? "Сначала остановите текущий локальный сервер" : null;
        var found = Locate();
        if (found == null)
            return "Не найден IH.Server. Положите собранный сервер в папку server рядом с игрой или запустите его вручную (см. README).";
        DirAccess.MakeDirRecursiveAbsolute(SaveDir);
        var args = found.Value.args.ToList();
        // the server dies with the client, so save every turn
        args.AddRange(new[] { "--port", port.ToString(), "--saves", SaveDir, "--autosave", "1", "--mods", ModsDir });
        SandboxToken = sandbox ? System.Guid.NewGuid().ToString("N") : null;
        if (SandboxToken != null) args.AddRange(new[] { "--sandbox-token", SandboxToken });
        _startupErrorFile = null;
        if (!string.IsNullOrEmpty(loadPath))
        {
            _startupErrorFile = OS.GetUserDataDir().PathJoin($"load-error-{System.Guid.NewGuid():N}.txt");
            args.AddRange(new[] { "--load", loadPath, "--startup-error-file", _startupErrorFile });
        }
        _pid = OS.CreateProcess(found.Value.program, args.ToArray());
        return _pid > 0 ? null : "Не удалось запустить сервер";
    }

    private static string? _startupErrorFile;

    public static string? ConsumeStartupError()
    {
        string? path = _startupErrorFile;
        _startupErrorFile = null;
        if (path == null || !Godot.FileAccess.FileExists(path)) return null;
        string text = Godot.FileAccess.GetFileAsString(path);
        DirAccess.RemoveAbsolute(path);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public static void Stop()
    {
        if (Running) OS.Kill(_pid);
        _pid = -1;
        SandboxToken = null;
    }

    /// <summary>Addresses friends can use: Radmin VPN hands out 26.x.x.x, otherwise ordinary LAN addresses.</summary>
    public static string AddressHint()
    {
        var all = IP.GetLocalAddresses().Where(a => a.Contains('.') && !a.StartsWith("127.") && !a.StartsWith("169.254.")).Distinct().ToList();
        var radmin = all.Where(a => a.StartsWith("26.")).ToList();
        if (radmin.Count > 0) return "Ваш адрес в Radmin VPN: " + string.Join(", ", radmin);
        return all.Count > 0 ? "Radmin VPN не найден. Адреса в локальной сети: " + string.Join(", ", all.Take(3)) : "Сетевые адреса не найдены";
    }
}
