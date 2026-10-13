// 通信の入口。NetworkManager（SimpleWebTransport）を用意し、サーバー／クライアントの受け口を登録する
using System;
using System.Collections.Generic;
using Mirror;
using Mirror.SimpleWeb;
using TW.Sim;
using UnityEngine;

public class TWNetManager : NetworkManager
{
    public TWServer Server { get; private set; }

    public override void OnStartServer()
    {
        base.OnStartServer();
        Server = new TWServer();
        Server.Register();
        Debug.Log("[TW] サーバー起動 port=" + ((SimpleWebTransport)transport).port);
    }

    public override void OnStopServer() { base.OnStopServer(); Server = null; }

    public override void OnServerConnect(NetworkConnectionToClient conn)
    {
        base.OnServerConnect(conn);
        if (Server != null) Server.OnConnect(conn);
    }

    public override void OnServerDisconnect(NetworkConnectionToClient conn)
    {
        if (Server != null) Server.OnDisconnect(conn);
        base.OnServerDisconnect(conn);
    }

    public override void OnStartClient() { base.OnStartClient(); TWNet.RegisterClientHandlers(); }

    public override void OnClientConnect()
    {
        base.OnClientConnect();
        NetworkClient.Send(new TWHello { Protocol = TWProtocol.Version, Token = TWNet.Token, Name = TWNet.Name });
    }

    public override void OnClientDisconnect() { base.OnClientDisconnect(); TWNet.OnDisconnected(); }

    public override void Update()
    {
        base.Update();
        if (Server != null) Server.Tick(Time.unscaledDeltaTime);
    }
}

/// <summary>接続先の設定（Resources/TW/netconfig の key=value。なければエディタは localhost）</summary>
public static class TWNetConfig
{
    public static string Address = "localhost";
    public static ushort ServerPort = 7790;   // サーバーが待ち受けるポート
    public static bool UseWss = false;        // ブラウザから wss:// でつなぐか（Caddy 経由）
    public static ushort ClientPort = 0;      // 0 = サーバーと同じ。wss で 443 を使うときは 443
    public static string Url = "";            // 指定があればこの URL へつなぐ（例 wss://example.org/tentacle。Caddy がパスで振り分ける）

    static bool loaded;
    public static void Load()
    {
        if (loaded) return;
        loaded = true;
        var ta = Resources.Load<TextAsset>("TW/netconfig");
        if (ta != null)
            foreach (var raw in ta.text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                ushort u;
                if (k == "address") Address = v;
                else if (k == "port" && ushort.TryParse(v, out u)) ServerPort = u;
                else if (k == "wss") UseWss = v == "true";
                else if (k == "clientPort" && ushort.TryParse(v, out u)) ClientPort = u;
                else if (k == "url") Url = v;
            }
#if UNITY_EDITOR
        // エディタでは手元のサーバーにつなぐ（PlayerPrefs の TW.EditorRemote=1 なら本番サーバーへ）
        if (PlayerPrefs.GetInt("TW.EditorRemote", 0) == 0) { Address = "localhost"; UseWss = false; ClientPort = 0; Url = ""; }
#endif
        // サーバー起動時の上書き： -port 7790
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            ushort u;
            if (args[i] == "-port" && ushort.TryParse(args[i + 1], out u)) ServerPort = u;
        }
    }
}

/// <summary>クライアント側の状態とイベント。画面はここを見て描く</summary>
public static class TWNet
{
    public static TWNetManager Manager { get; private set; }

    public static string Token
    {
        get
        {
            var t = PlayerPrefs.GetString("TW.Token", "");
            if (string.IsNullOrEmpty(t)) { t = Guid.NewGuid().ToString("N"); PlayerPrefs.SetString("TW.Token", t); PlayerPrefs.Save(); }
            return t;
        }
    }

    public static string Name
    {
        get { return PlayerPrefs.GetString("TW.Name", ""); }
        set { PlayerPrefs.SetString("TW.Name", value ?? ""); PlayerPrefs.Save(); }
    }

    public static bool Connected { get { return NetworkClient.isConnected; } }
    public static bool Welcomed { get; private set; }
    public static TWWelcome Welcome { get; private set; }
    public static TWRoomInfo[] Rooms { get; private set; } = new TWRoomInfo[0];
    public static TWRoomState? Room { get; private set; }
    public static readonly List<TWChatLine> Chat = new List<TWChatLine>();
    public static TWOnlineMatch Match { get; private set; }
    public static TWGameOver? LastGameOver { get; private set; }
    public static string LastError;

    public static event Action Changed;             // 一覧・部屋・チャットが変わった
    public static event Action<string> ErrorReceived;
    public static event Action<string> Kicked;      // 部屋から出された／抜けた（理由が空なら自分で抜けた）
    public static event Action GameStarted;
    public static event Action<TWGameOver> GameOver;
    public static event Action Disconnected;

    static void Raise(Action a) { if (a != null) a(); }

    /// <summary>NetworkManager を用意する（まだなら）</summary>
    public static TWNetManager EnsureManager()
    {
        if (Manager != null) return Manager;
        TWNetConfig.Load();
        var go = new GameObject("Network");
        go.SetActive(false);
        var swt = go.AddComponent<SimpleWebTransport>();
        swt.port = TWNetConfig.ServerPort;
        swt.maxMessageSize = 64 * 1024;
        swt.clientUseWss = TWNetConfig.UseWss;
        var cws = swt.clientWebsocketSettings;
        if (TWNetConfig.ClientPort != 0) { cws.ClientPortOption = WebsocketPortOption.SpecifyPort; cws.CustomClientPort = TWNetConfig.ClientPort; }
        else cws.ClientPortOption = WebsocketPortOption.DefaultSameAsServer;
        swt.clientWebsocketSettings = cws;
        var nm = go.AddComponent<TWNetManager>();
        nm.transport = swt;
        nm.autoCreatePlayer = false;
        nm.playerPrefab = null;
        nm.networkAddress = TWNetConfig.Address;
        nm.maxConnections = 400;
        nm.headlessStartMode = HeadlessStartOptions.DoNothing;
        nm.editorAutoStart = false;
        go.SetActive(true);
        Manager = nm;
        return nm;
    }

    public static void Connect()
    {
        var nm = EnsureManager();
        if (NetworkClient.active) return;
        Welcomed = false;
        if (!string.IsNullOrEmpty(TWNetConfig.Url)) nm.StartClient(new Uri(TWNetConfig.Url));
        else nm.StartClient();
    }

    /// <summary>開発用：この端末でサーバーも動かす（エディタで1人確認するとき）</summary>
    public static void StartHost()
    {
        var nm = EnsureManager();
        if (NetworkClient.active) nm.StopClient();
        Welcomed = false;
        nm.StartHost();
    }

    public static void StartServer() { EnsureManager().StartServer(); }

    public static void Disconnect()
    {
        if (Manager == null) return;
        if (NetworkServer.active && NetworkClient.isConnected) Manager.StopHost();
        else if (NetworkClient.active) Manager.StopClient();
    }

    public static void Send<T>(T msg) where T : struct, NetworkMessage
    {
        if (NetworkClient.isConnected) NetworkClient.Send(msg);
    }

    public static void SendCmd(Command c, int comSeat = -1)
    {
        Send(new TWCmd { Type = (byte)c.Type, A = c.A, B = c.B, Seat = comSeat });
    }

    public static void RegisterClientHandlers()
    {
        NetworkClient.ReplaceHandler<TWWelcome>(m =>
        {
            Welcome = m; Welcomed = m.Ok;
            if (!m.Ok) { LastError = m.Error; if (ErrorReceived != null) ErrorReceived(m.Error); }
            Raise(Changed);
        }, false);
        NetworkClient.ReplaceHandler<TWRoomList>(m => { Rooms = m.Rooms ?? new TWRoomInfo[0]; Raise(Changed); }, false);
        NetworkClient.ReplaceHandler<TWRoomState>(m =>
        {
            bool entered = Room == null || Room.Value.RoomId != m.RoomId;
            if (entered) Chat.Clear();
            Room = m;
            if (Match != null) Match.OnRoomState(m);
            Raise(Changed);
        }, false);
        NetworkClient.ReplaceHandler<TWChatLine>(m => { Chat.Add(m); if (Chat.Count > 60) Chat.RemoveAt(0); Raise(Changed); }, false);
        NetworkClient.ReplaceHandler<TWError>(m => { LastError = m.Text; if (ErrorReceived != null) ErrorReceived(m.Text); }, false);
        NetworkClient.ReplaceHandler<TWKicked>(m =>
        {
            Room = null; Match = null;
            if (Kicked != null) Kicked(m.Reason);
            Raise(Changed);
        }, false);
        NetworkClient.ReplaceHandler<TWGameStart>(m =>
        {
            string err;
            var map = MapJson.FromJson(m.MapJson, out err);
            if (map == null) { Debug.LogError("[TW] 地図を読めません: " + err); return; }
            Match = new TWOnlineMatch(map, m);
            LastGameOver = null;
            Raise(GameStarted);
        }, false);
        NetworkClient.ReplaceHandler<TWTickBundle>(m => { if (Match != null) Match.OnBundle(m); }, false);
        NetworkClient.ReplaceHandler<TWSnapshot>(m => { if (Match != null) Match.OnSnapshot(m.State); }, false);
        NetworkClient.ReplaceHandler<TWComDuty>(m => { if (Match != null) Match.SetDuty(m.Seats, m.Profiles); }, false);
        NetworkClient.ReplaceHandler<TWGameOver>(m =>
        {
            LastGameOver = m;
            if (Match != null) Match.OnGameOver(m);
            if (GameOver != null) GameOver(m);
        }, false);
    }

    public static void OnDisconnected()
    {
        Welcomed = false; Room = null; Match = null;
        Rooms = new TWRoomInfo[0];
        Raise(Disconnected);
        Raise(Changed);
    }
}

/// <summary>オンライン試合のクライアント側。サーバーが確定した tick まで、同じシミュレーションを進める</summary>
public class TWOnlineMatch
{
    public readonly MapDef Map;
    public readonly Simulation Sim;
    public readonly int MySeat;
    public string[] Names;
    public TWSeatInfo[] Seats;
    public bool WaitingSnapshot { get; private set; }
    public int ConfirmedTick { get; private set; }
    public bool Finished { get; private set; }
    public int Winner { get; private set; } = -1;
    public float StartAt { get; private set; }     // ローカル時刻。これより前はカウントダウン
    public int Desyncs { get; private set; }

    readonly Dictionary<int, List<TWCmdEntry>> cmds = new Dictionary<int, List<TWCmdEntry>>();
    readonly Dictionary<int, uint> hashes = new Dictionary<int, uint>();
    readonly Dictionary<int, SimpleAI> duty = new Dictionary<int, SimpleAI>();
    float acc;

    public TWOnlineMatch(MapDef map, TWGameStart start)
    {
        Map = map;
        Sim = new Simulation(map);
        Sim.TimeLimitTicks = start.TimeLimitTicks;
        MySeat = start.YourSeat;
        Names = start.Names ?? new string[map.Players];
        WaitingSnapshot = start.Late;
        StartAt = Time.unscaledTime + start.Countdown;
    }

    public bool IsComDuty { get { return duty.Count > 0; } }

    public void OnRoomState(TWRoomState s)
    {
        Seats = s.Seats;
        if (s.Seats != null && Names != null)
            for (int i = 0; i < s.Seats.Length && i < Names.Length; i++)
                Names[i] = s.Seats[i].Com ? "COM" : s.Seats[i].Name;
    }

    public void OnBundle(TWTickBundle b)
    {
        if (b.Cmds != null)
            foreach (var c in b.Cmds)
            {
                if (c.Tick <= Sim.Tick && !WaitingSnapshot) continue;
                List<TWCmdEntry> l;
                if (!cmds.TryGetValue(c.Tick, out l)) { l = new List<TWCmdEntry>(); cmds[c.Tick] = l; }
                l.Add(c);
            }
        if (b.HashTick > 0) hashes[b.HashTick] = b.Hash;
        if (b.UpTo > ConfirmedTick) ConfirmedTick = b.UpTo;
    }

    public void OnSnapshot(byte[] state)
    {
        if (!Sim.LoadState(state)) { Debug.LogError("[TW] スナップショットを読めません"); return; }
        WaitingSnapshot = false;
        var old = new List<int>(cmds.Keys);
        foreach (var t in old) if (t <= Sim.Tick) cmds.Remove(t);
        var oldH = new List<int>(hashes.Keys);
        foreach (var t in oldH) if (t <= Sim.Tick) hashes.Remove(t);
        if (Sim.Tick > ConfirmedTick) ConfirmedTick = Sim.Tick;
        acc = 0;
    }

    public void OnGameOver(TWGameOver m) { Finished = true; Winner = m.Winner; }

    /// <summary>部屋主として代打ちする席</summary>
    public void SetDuty(int[] seats, int[] profiles)
    {
        seats = seats ?? new int[0];
        var keep = new HashSet<int>(seats);
        var remove = new List<int>();
        foreach (var k in duty.Keys) if (!keep.Contains(k)) remove.Add(k);
        foreach (var k in remove) duty.Remove(k);
        for (int i = 0; i < seats.Length; i++)
        {
            int seat = seats[i];
            var pf = profiles != null && i * 3 + 2 < profiles.Length ? new ComProfile(profiles[i * 3], profiles[i * 3 + 1], profiles[i * 3 + 2]) : SimpleAI.Presets[SimpleAI.DefaultLevel];
            if (duty.ContainsKey(seat)) { duty[seat].SetProfile(pf); continue; }
            var ai = new SimpleAI(seat, UnityEngine.Random.Range(1, 99999)).SetProfile(pf);
            ai.Emit = c => TWNet.SendCmd(c, seat);
            duty[seat] = ai;
        }
    }

    /// <summary>毎フレーム呼ぶ。確定している tick まで進める。戻り値は次 tick までの進み（描画の補間用）</summary>
    public float Advance(float dt)
    {
        const float step = 1f / Balance.TicksPerSecond;
        if (WaitingSnapshot || Time.unscaledTime < StartAt) return 0;
        int lag = ConfirmedTick - Sim.Tick;
        // 遅れているときは少し速めて追いつく（通信が一瞬止まった後など）
        float rate = lag > 8 ? 3f : lag > 4 ? 1.5f : 1f;
        acc += dt * rate;
        int guard = 0;
        while (acc >= step && Sim.Tick < ConfirmedTick && guard++ < 30)
        {
            acc -= step;
            StepOnce();
            if (WaitingSnapshot) return 0;
        }
        if (Sim.Tick >= ConfirmedTick && acc > step) acc = step;   // 確定待ちの間に借りを貯めない
        return Mathf.Clamp01(acc / step);
    }

    void StepOnce()
    {
        int t = Sim.Tick + 1;
        List<TWCmdEntry> l;
        if (cmds.TryGetValue(t, out l))
        {
            foreach (var c in l)
            {
                var cmd = new Command { Type = (CommandType)c.Type, Player = c.Player, A = c.A, B = c.B };
                Sim.Enqueue(cmd);
            }
            cmds.Remove(t);
        }
        Sim.Step();
        uint h;
        if (hashes.TryGetValue(Sim.Tick, out h))
        {
            hashes.Remove(Sim.Tick);
            if (Sim.StateHash() != h)
            {
                Desyncs++;
                Debug.LogWarning("[TW] 盤面がずれました (tick " + Sim.Tick + ")。サーバーから取り直します");
                WaitingSnapshot = true;
                TWNet.Send(new TWSnapshotRequest());
                return;
            }
        }
        // 部屋主は COM・切断中の席を代打ちする（考えた結果はサーバーへ送る）
        foreach (var ai in duty.Values) ai.Think(Sim);
    }
}
