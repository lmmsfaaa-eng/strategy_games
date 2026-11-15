using System.Net;
using IH.Bot;
using IH.Core;
using IH.Server;
using IH.Shared;
using Xunit;

namespace IH.Tests;

[Collection("network")]
public class NetworkTests
{
    [Fact]
    public void Diplomatic_data_and_intelligence_stay_private_in_live_deltas_and_reconnect()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ih-test-" + Guid.NewGuid().ToString("N"));
        var server = new GameServer(new ServerOptions { Port = 0, Quiet = true, SaveDir = dir });
        var host = new NetClient { Name = "Хост" };
        var ally = new NetClient { Name = "Союзник" };
        var observer = new NetClient { Name = "Наблюдатель" };
        var rejoin = new NetClient { Name = ally.Name };
        var impostor = new NetClient { Name = ally.Name };
        var clients = new[] { host, ally, observer };
        server.Start();
        try
        {
            foreach (var client in clients) client.Connect("127.0.0.1", server.Port);
            Assert.True(Until(server, clients, () => clients.All(c => c.State == NetState.Lobby)));
            host.Send(MsgType.CreateGame, new GameSettings { MapW = 40, MapH = 26, MaxPlayers = 3, AiCount = 0, Seed = 17 });
            Assert.True(Until(server, clients, () => host.Room != null));
            foreach (var client in clients.Skip(1))
            {
                client.Send(MsgType.JoinGame, new IntMsg { V = host.Room!.Info.Id });
                Assert.True(Until(server, clients, () => client.Room != null));
            }
            host.Send(MsgType.StartGame);
            Assert.True(Until(server, clients, () => clients.All(c => c.World is { Turn: > 0 })));
            var session = server.Sessions.Values.Single();
            var game = session.Game!;
            int owner = host.World!.MyId, recipient = ally.World!.MyId, bystander = observer.World!.MyId;
            game.Rel(owner, recipient).Met = true;
            game.Rel(owner, bystander).Met = true;
            game.P(owner).Gold = 777;
            game.P(owner).Favor = 40;
            game.P(owner).Civics.Add("c_diplomacy");
            host.SendCommand(CmdType.SetStatAccess, recipient, (int)StatAccess.Economy);
            Assert.True(Until(server, clients, () => ally.World.Players[owner].Gold == 777 && observer.World.Players[owner].Met));
            Assert.Equal(-1, observer.World.Players[owner].Gold);

            host.SendCommand(CmdType.StartEspionage, bystander);
            Assert.True(Until(server, clients, () => host.World.Me.Espionage.Length == 1));
            for (int turn = 0; turn < 3; turn++)
                foreach (var p in game.Active.ToList()) Assert.Null(game.Execute(p.Id, new GameCommand(CmdType.EndTurn)));
            session.Flush();
            Assert.True(Until(server, clients, () => clients.All(c => c.World!.Turn == game.S.Turn)
                && host.World.Me.Espionage.Single().Observed == game.S.Turn));
            Assert.Empty(ally.World.Me.Espionage);
            Assert.Empty(observer.World.Me.Espionage);

            host.SendCommand(CmdType.SetStatAccess, recipient, 0);
            Assert.True(Until(server, clients, () => ally.World.Players[owner].SharedStats == 0));
            Assert.Equal(-1, ally.World.Players[owner].Gold);
            rejoin.Token = ally.Token;
            ally.Disconnect();
            Assert.True(Until(server, new[] { host, observer }, () => !game.P(recipient).Connected));

            var afterDisconnect = new[] { host, observer, impostor };
            string? error = null;
            impostor.ErrorReceived += text => error = text;
            impostor.Connect("127.0.0.1", server.Port);
            Assert.True(Until(server, afterDisconnect, () => impostor.State == NetState.Lobby));
            impostor.Send(MsgType.JoinGame, new IntMsg { V = session.Id });
            Assert.True(Until(server, afterDisconnect, () => error != null));
            Assert.Null(impostor.World);
            Assert.False(game.P(recipient).Connected);

            rejoin.Connect("127.0.0.1", server.Port);
            Assert.True(Until(server, new[] { host, observer, rejoin }, () => rejoin.World is { Turn: > 0 }));
            Assert.Equal(recipient, rejoin.World!.MyId);
            Assert.Equal(-1, rejoin.World.Players[owner].Gold);
            Assert.Empty(rejoin.World.Me.Espionage);
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
            impostor.Dispose();
            rejoin.Dispose();
            server.Stop();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SandboxCommandsRequireLocalServerHostAndToken(bool sandbox)
    {
        const string secret = "0123456789abcdef0123456789abcdef";
        var dir = Path.Combine(Path.GetTempPath(), "ih-test-" + Guid.NewGuid().ToString("N"));
        var server = new GameServer(new ServerOptions { Port = 0, Quiet = true, SaveDir = dir, SandboxToken = sandbox ? secret : null });
        var host = new NetClient { Name = "Хост" };
        var guest = new NetClient { Name = "Гость" };
        var clients = new[] { host, guest };
        server.Start();
        try
        {
            foreach (var client in clients) client.Connect("127.0.0.1", server.Port);
            Assert.True(Until(server, clients, () => clients.All(c => c.State == NetState.Lobby)));
            host.Send(MsgType.CreateGame, new GameSettings { MapW = 40, MapH = 26, MaxPlayers = 2, Seed = 17 });
            Assert.True(Until(server, clients, () => host.Room != null));
            guest.Send(MsgType.JoinGame, new IntMsg { V = host.Room!.Info.Id });
            Assert.True(Until(server, clients, () => guest.Room != null));
            host.Send(MsgType.StartGame);
            Assert.True(Until(server, clients, () => clients.All(c => c.World is { Turn: > 0 })));
            var game = server.Sessions.Values.Single().Game!;

            Assert.False(RunCommand(guest, CmdType.SandboxResources, secret));
            Assert.False(RunCommand(host, CmdType.SandboxResources, "неверный ключ"));
            int gold = game.P(host.World!.MyId).Gold;
            Assert.Equal(sandbox, RunCommand(host, CmdType.SandboxResources, secret));
            Assert.Equal(gold + (sandbox ? 500 : 0), game.P(host.World.MyId).Gold);
            Assert.Equal(sandbox, RunCommand(host, CmdType.SandboxReveal, secret));
            if (sandbox)
            {
                Assert.True(Until(server, clients, () => host.World.Vis.All(v => v == 2)));
                Assert.Contains(guest.World!.Vis, v => v != 2);
                int startingTile = game.UnitsOf(host.World.MyId).First().Tile;
                int warrior = Rules.UnitIndex("warrior");
                Assert.True(RunCommand(host, CmdType.SandboxSpawn, secret, startingTile, warrior));
                Assert.False(RunCommand(host, CmdType.SandboxSpawn, secret, startingTile, warrior));
                Assert.Equal(Rules.MaxUnitsPerTile, game.At(startingTile).Count);
            }

            bool RunCommand(NetClient client, CmdType type, string token, int a = 0, int b = 0)
            {
                bool? result = null;
                void Receive(int seq, bool ok, string error, int rtt) => result = ok;
                client.CommandResult += Receive;
                client.SendCommand(type, a, b, s: token);
                Assert.True(Until(server, clients, () => result.HasValue));
                client.CommandResult -= Receive;
                return result!.Value;
            }
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
            server.Stop();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private static GameServer StartServer(string saveDir)
    {
        var server = new GameServer(new ServerOptions { Port = 0, Quiet = Environment.GetEnvironmentVariable("IH_LOG") == null, SaveDir = saveDir, AutosaveEvery = 5, AfkSeconds = 2 });
        server.Start();
        return server;
    }

    /// <summary>The client mirror must equal exactly what the server thinks this player may know.</summary>
    private static void AssertMirrors(Game g, int pid, ClientWorld w)
    {
        var view = g.BuildView(pid);
        var p = g.P(pid);
        var bad = Enumerable.Range(0, p.KnownTiles.Length).Where(i => (p.KnownTiles[i] == 0 ? 0 : (p.KnownTiles[i] & TileData.VisibleBit) != 0 ? 2 : 1) != w.Vis[i]).ToList();
        Assert.True(bad.Count == 0, $"pid {pid} alive={p.Alive} turn srv {g.S.Turn} cli {w.Turn}: {bad.Count} tiles differ, e.g. {string.Join(",", bad.Take(8).Select(i => $"{i}:srv{(p.KnownTiles[i] >> 62)}/cli{w.Vis[i]}"))}; units srv {view.Units.Count} cli {w.Units.Count}; myunits {g.UnitsOf(pid).Count()} cities {g.CitiesOf(pid).Count()}");
        for (int i = 0; i < p.KnownTiles.Length; i++)
        {
            ulong k = p.KnownTiles[i];
            int vis = k == 0 ? 0 : (k & TileData.VisibleBit) != 0 ? 2 : 1;
            Assert.Equal(vis, w.Vis[i]);
            if (k != 0) Assert.Equal(TileData.Unpack(k), w.Tiles[i]);
        }
        Assert.Equal(view.Units.Select(u => (u.Id, u.Tile, u.Hp)).OrderBy(x => x), w.Units.Values.Select(u => (u.Id, u.Tile, u.Hp)).OrderBy(x => x));
        Assert.Equal(p.KnownCities.Values.Select(c => (c.Id, c.Owner, c.Pop)).OrderBy(x => x), w.Cities.Values.Select(c => (c.Id, c.Owner, c.Pop)).OrderBy(x => x));
        Assert.Equal(p.Gold, w.Me.Gold);
        Assert.Equal(g.S.Turn, w.Turn);
        // nothing hidden ever reached the client
        Assert.All(w.Units.Values, u => Assert.Equal(2, w.Vis[u.Tile]));
    }

    private static void Pump(GameServer server, IEnumerable<NetClient> clients, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            server.Tick();
            foreach (var c in clients) c.Poll();
            Thread.Sleep(2);
        }
    }

    /// <summary>Pumps until the (lossy, delayed) link has been silent for a while, i.e. everything in flight has arrived.</summary>
    private static void Settle(GameServer server, List<NetClient> clients)
    {
        var limit = DateTime.UtcNow.AddSeconds(15);
        long last = -1; var quietSince = DateTime.UtcNow;
        while (DateTime.UtcNow < limit)
        {
            Pump(server, clients, 50);
            long total = clients.Sum(c => (long)c.DeltasReceived);
            if (total != last || clients.Any(c => c.PendingCommands > 0)) { last = total; quietSince = DateTime.UtcNow; }
            else if ((DateTime.UtcNow - quietSince).TotalMilliseconds > 700) return;
        }
    }

    /// <summary>Pumps until the condition holds; fixed sleeps are too fragile on a busy machine.</summary>
    private static bool Until(GameServer server, IEnumerable<NetClient> clients, Func<bool> condition, int timeoutMs = 8000)
    {
        var list = clients.ToList();
        var limit = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > limit) return false;
            Pump(server, list, 10);
        }
        return true;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Bots_play_over_a_degraded_link_and_stay_in_sync(bool simultaneous)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ih-test-" + Guid.NewGuid().ToString("N"));
        var server = StartServer(dir);
        using var proxy = new LagProxy(0, new IPEndPoint(IPAddress.Loopback, server.Port), 25, 20, 3);
        try
        {
            var report = StressTest.Run("127.0.0.1", proxy.Port, 3, 1, 12, simultaneous, TimeSpan.FromSeconds(120), null, server.Tick);
            Assert.True(report.Completed, $"game stalled at turn {report.Turn}");
            Assert.True(proxy.Dropped > 0, "the proxy should have dropped packets");
            Assert.True(report.AvgRtt >= 40, $"commands should feel the artificial latency (avg {report.AvgRtt} ms)");

            // let the bots finish what they are doing, then compare every mirror with the server
            var session = server.Sessions.Values.Single();
            var g = session.Game!;
            foreach (var b in report.Bots) b.Paused = true;
            Settle(server, report.Bots.Select(b => b.Net).ToList());
            // seats are assigned in arrival order, which the jittery link may shuffle
            Assert.Equal(new[] { 0, 1, 2 }, report.Bots.Select(b => b.World!.MyId).OrderBy(x => x));
            foreach (var b in report.Bots) AssertMirrors(g, b.World!.MyId, b.World);
            Assert.True(File.Exists(Path.Combine(dir, $"game{session.Id}.json")), "autosave missing");

            // reconnect: a brand new connection with the old seat token gets the full state back
            string token = report.Bots[0].Net.Token;
            int seat = report.Bots[0].World!.MyId;
            report.Bots[0].Net.Disconnect();
            Assert.True(Until(server, report.Bots.Skip(1).Select(b => b.Net), () => !g.P(seat).Connected), "server did not notice the disconnect");
            var again = new NetClient { Name = "Бот1", Token = token };
            again.Connect("127.0.0.1", proxy.Port);
            var all = report.Bots.Skip(1).Select(b => b.Net).Append(again).ToList();
            var until = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < until && !(again.State == NetState.InGame && again.World is { Turn: > 0 })) Pump(server, all, 20);
            Assert.Equal(NetState.InGame, again.State);
            Settle(server, all);
            Assert.True(g.P(seat).Connected);
            Assert.Equal(seat, again.World!.MyId);
            AssertMirrors(g, seat, again.World!);

            // a stranger cannot take a seat in a running game
            var stranger = new NetClient { Name = "Чужак" };
            string? error = null;
            stranger.ErrorReceived += e => error = e;
            stranger.Connect("127.0.0.1", server.Port);
            all.Add(stranger);
            Assert.True(Until(server, all, () => stranger.State == NetState.Lobby), "stranger could not reach the lobby");
            stranger.Send(MsgType.JoinGame, new IntMsg { V = session.Id });
            stranger.SendCommand(CmdType.EndTurn);
            Assert.True(Until(server, all, () => error != null), "the server never refused the stranger");
            Assert.Null(stranger.World);

            // the save file restores into an identical simulation
            session.Save();
            var save = SaveGame.FromJson<SessionSave>(File.ReadAllText(Path.Combine(dir, $"game{session.Id}.json")));
            Assert.Equal(SaveGame.ToJson(g.S), SaveGame.ToJson(new Game(save.State).S));
            Assert.Equal(3, save.Slots.Count);
            foreach (var c in all) c.Dispose();
        }
        finally
        {
            server.Stop();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>In-memory stand-in for Steam P2P: unreliable datagrams addressed by 64-bit ids.</summary>
    private sealed class FakeLink : IPeerLink
    {
        public readonly ulong Me;
        private readonly Dictionary<ulong, Action<ulong, byte[], int>> _net;
        public int Sent;
        public FakeLink(ulong me, Dictionary<ulong, Action<ulong, byte[], int>> net) { Me = me; _net = net; }
        public void Send(ulong peer, byte[] data, int length)
        {
            Sent++;
            if (Sent % 17 == 0) return; // the real thing drops packets too
            if (_net.TryGetValue(peer, out var deliver)) deliver(Me, data.AsSpan(0, length).ToArray(), length);
        }
    }

    [Fact]
    public void Game_runs_through_a_peer_tunnel_instead_of_direct_udp()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ih-test-" + Guid.NewGuid().ToString("N"));
        var server = StartServer(dir);
        var net = new Dictionary<ulong, Action<ulong, byte[], int>>();
        const ulong hostId = 76561190000000001, aId = 76561190000000002, bId = 76561190000000003;
        using var hostTunnel = new TunnelHost(new FakeLink(hostId, net), server.Port);
        using var tunA = new TunnelClient(new FakeLink(aId, net), hostId);
        using var tunB = new TunnelClient(new FakeLink(bId, net), hostId);
        net[hostId] = hostTunnel.OnPeerData; net[aId] = tunA.OnPeerData; net[bId] = tunB.OnPeerData;
        var a = new NetClient { Name = "A" }; var b = new NetClient { Name = "B" };
        try
        {
            void Run(int ms)
            {
                var until = DateTime.UtcNow.AddMilliseconds(ms);
                while (DateTime.UtcNow < until)
                {
                    server.Tick(); hostTunnel.Poll(); tunA.Poll(); tunB.Poll(); a.Poll(); b.Poll();
                    Thread.Sleep(2);
                }
            }
            // the clients only ever talk to their own loopback port
            a.Connect("127.0.0.1", tunA.LocalPort); b.Connect("127.0.0.1", tunB.LocalPort);
            for (int i = 0; i < 100 && (a.State != NetState.Lobby || b.State != NetState.Lobby); i++) Run(100);
            Assert.Equal(NetState.Lobby, a.State);
            Assert.Equal(NetState.Lobby, b.State);
            Assert.Equal(2, hostTunnel.PeerCount);
            a.Send(MsgType.CreateGame, new GameSettings { MapW = 40, MapH = 28, AiCount = 1, MaxPlayers = 2, Seed = 11 });
            for (int i = 0; i < 100 && a.Room == null; i++) Run(100);
            b.Send(MsgType.JoinGame, new IntMsg { V = a.Room!.Info.Id });
            for (int i = 0; i < 100 && (b.Room == null || a.Room.Slots.Count(x => !x.AI) < 2); i++) Run(100);
            a.Send(MsgType.StartGame);
            for (int i = 0; i < 100 && (a.World == null || b.World == null); i++) Run(100);
            Assert.NotNull(a.World); Assert.NotNull(b.World);
            Assert.NotEqual(a.World!.MyId, b.World!.MyId);
            var audio = new byte[VoiceWire.PcmBytes];
            audio[0] = 12; audio[^1] = 34;
            bool heard = false;
            b.VoiceReceived += (speaker, sequence, frame) =>
                heard = speaker == a.World.MyId && sequence == 1 && frame.Span.SequenceEqual(audio);
            for (int i = 0; i < 20 && !heard; i++) { a.SendVoice(1, audio); Run(50); }
            Assert.True(heard);
            for (int turn = 0; turn < 5; turn++)
            {
                int want = turn + 2;
                a.SendCommand(CmdType.EndTurn); b.SendCommand(CmdType.EndTurn);
                for (int i = 0; i < 100 && (a.World.Turn < want || b.World.Turn < want); i++) Run(100);
                Run(150);
            }
            Assert.Equal(6, a.World.Turn);
            Assert.Equal(6, b.World.Turn);
            var g = server.Sessions.Values.Single().Game!;
            AssertMirrors(g, a.World.MyId, a.World);
            AssertMirrors(g, b.World.MyId, b.World);
        }
        finally
        {
            a.Dispose(); b.Dispose();
            server.Stop();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Disconnected_player_does_not_block_the_game_forever()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ih-test-" + Guid.NewGuid().ToString("N"));
        var server = StartServer(dir);
        try
        {
            var a = new NetClient { Name = "A" }; var b = new NetClient { Name = "B" };
            var both = new[] { a, b };
            a.Connect("127.0.0.1", server.Port); b.Connect("127.0.0.1", server.Port);
            Assert.True(Until(server, both, () => a.State == NetState.Lobby && b.State == NetState.Lobby));
            a.Send(MsgType.CreateGame, new GameSettings { MapW = 40, MapH = 28, AiCount = 0, MaxPlayers = 2, Seed = 3 });
            Assert.True(Until(server, both, () => a.Room != null));
            b.Send(MsgType.JoinGame, new IntMsg { V = a.Room!.Info.Id });
            Assert.True(Until(server, both, () => b.Room != null && a.Room.Slots.Length == 2));
            b.Send(MsgType.StartGame); // not the host
            Pump(server, both, 200);
            Assert.Null(a.World);
            a.Send(MsgType.StartGame);
            Assert.True(Until(server, both, () => a.World is { Turn: 1 } && b.World is { Turn: 1 }));
            Assert.Equal(1, a.World!.Turn);

            b.Disconnect();
            a.SendCommand(CmdType.EndTurn);
            Pump(server, new[] { a }, 500);
            Assert.Equal(1, a.World.Turn);          // still inside the grace period
            Assert.False(a.World.Players[1].Connected);
            Pump(server, new[] { a }, 2500);
            Assert.Equal(2, a.World.Turn);          // grace period over: B's turn was ended for them
            a.Dispose(); b.Dispose();
        }
        finally
        {
            server.Stop();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

/// <summary>Network tests depend on real time (latency, quiet periods), so they do not share the CPU with the long simulations.</summary>
[CollectionDefinition("network", DisableParallelization = true)]
public class NetworkCollection { }
