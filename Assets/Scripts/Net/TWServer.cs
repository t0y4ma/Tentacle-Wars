// サーバー側：ロビー・部屋・試合の進行。試合はサーバーが正本を持ち、操作だけを全員へ配る
using System.Collections.Generic;
using Mirror;
using TW.Sim;
using UnityEngine;

public class TWServer
{
    public const float AwayGraceSeconds = 30f;   // 切断からCOMの代打ちが始まるまで
    public const float CountdownSeconds = 3f;
    public const int SendEveryTicks = 2;         // 0.1秒ごとに確定分を配る
    public const int HashEveryTicks = 100;       // 5秒ごとにチェックサム
    public const int MaxRooms = 64;
    public const int MaxSpectators = 8;
    public static readonly int[] TimeLimits = { 300, 480, 720, 0 };   // 0 = 無制限

    class Client
    {
        public NetworkConnectionToClient Conn;
        public string Token = "";
        public string Name = "ゲスト";
        public bool Hello;
        public Room Room;
        public int Seat = -1;          // 部屋の中での席（-1 = 観戦）
        public float LastChat;
        public float CmdWindowStart; public int CmdCount;
    }

    class Seat
    {
        public Client Client;          // 接続中の人（いなければ null）
        public string Token;           // 座っている人（切断中も残る）
        public string Name;
        public bool Com, Ready, Away, Takeover, Resigned;
        public TW.Sim.ComProfile Profile = TW.Sim.SimpleAI.Presets[TW.Sim.SimpleAI.DefaultLevel];
        public float AwaySince;
        public bool Free { get { return Client == null && Token == null && !Com; } }
        public void Clear() { Client = null; Token = null; Name = null; Com = Ready = Away = Takeover = Resigned = false; Profile = TW.Sim.SimpleAI.Presets[TW.Sim.SimpleAI.DefaultLevel]; }
    }

    class Room
    {
        public int Id; public string Name; public string Password = "";
        public Client Host;
        public MapDef Map; public string MapJson; public int TimeLimitSec = 480; public bool AllowSpectators = true;
        public Seat[] Seats;
        public readonly List<Client> Spectators = new List<Client>();
        public TWRoomPhase Phase;
        public float CountdownUntil;
        public Simulation Sim;
        public float Acc;
        public readonly List<Command> Pending = new List<Command>();
        public readonly List<TWCmdEntry> Unsent = new List<TWCmdEntry>();
        public int LastSentTick, HashTick; public uint Hash;
        public string DutyKey = "";
    }

    readonly Dictionary<int, Client> clients = new Dictionary<int, Client>();
    readonly Dictionary<int, Room> rooms = new Dictionary<int, Room>();
    int nextRoomId = 1;
    bool listDirty;
    float listTimer;

    public void Register()
    {
        NetworkServer.RegisterHandler<TWHello>(OnHello, false);
        NetworkServer.RegisterHandler<TWCreateRoom>(OnCreateRoom, false);
        NetworkServer.RegisterHandler<TWJoinRoom>(OnJoinRoom, false);
        NetworkServer.RegisterHandler<TWLeaveRoom>((c, m) => { var cl = Get(c); if (cl != null) Leave(cl); }, false);
        NetworkServer.RegisterHandler<TWRoomSettings>(OnSettings, false);
        NetworkServer.RegisterHandler<TWTakeSeat>(OnTakeSeat, false);
        NetworkServer.RegisterHandler<TWSetCom>(OnSetCom, false);
        NetworkServer.RegisterHandler<TWSetReady>(OnReady, false);
        NetworkServer.RegisterHandler<TWStartGame>(OnStart, false);
        NetworkServer.RegisterHandler<TWChat>(OnChat, false);
        NetworkServer.RegisterHandler<TWCmd>(OnCmd, false);
        NetworkServer.RegisterHandler<TWResign>(OnResign, false);
        NetworkServer.RegisterHandler<TWSnapshotRequest>(OnSnapshotRequest, false);
    }

    public void OnConnect(NetworkConnectionToClient conn) { clients[conn.connectionId] = new Client { Conn = conn }; }

    public void OnDisconnect(NetworkConnectionToClient conn)
    {
        var c = Get(conn);
        if (c == null) return;
        Leave(c, false);
        clients.Remove(conn.connectionId);
    }

    Client Get(NetworkConnectionToClient conn) { Client c; return clients.TryGetValue(conn.connectionId, out c) ? c : null; }

    static void Error(Client c, string text) { c.Conn.Send(new TWError { Text = text }); }

    static string Clean(string s, int max, string fallback)
    {
        s = (s ?? "").Replace("\n", " ").Replace("\r", " ").Trim();
        if (s.Length > max) s = s.Substring(0, max);
        return s.Length == 0 ? fallback : s;
    }

    // ======================================================================
    // ロビー
    // ======================================================================

    void OnHello(NetworkConnectionToClient conn, TWHello m)
    {
        var c = Get(conn);
        if (c == null) return;
        if (m.Protocol != TWProtocol.Version)
        {
            conn.Send(new TWWelcome { Ok = false, Error = "ゲームのバージョンが違います。ページを再読み込みしてください" });
            return;
        }
        c.Hello = true;
        c.Token = Clean(m.Token, 64, System.Guid.NewGuid().ToString("N"));
        c.Name = Clean(m.Name, TWProtocol.MaxNameLength, "ゲスト");
        int rejoin = -1; string rejoinName = "";
        foreach (var r in rooms.Values)
        {
            if (r.Phase == TWRoomPhase.Waiting) continue;
            foreach (var s in r.Seats) if (s.Token == c.Token && s.Client == null && !s.Resigned) { rejoin = r.Id; rejoinName = r.Name; }
        }
        conn.Send(new TWWelcome { Ok = true, RejoinRoom = rejoin, RejoinRoomName = rejoinName });
        conn.Send(BuildList());
    }

    TWRoomList BuildList()
    {
        var list = new List<TWRoomInfo>();
        foreach (var r in rooms.Values)
        {
            int players = 0;
            foreach (var s in r.Seats) if (!s.Free) players++;
            list.Add(new TWRoomInfo
            {
                Id = r.Id, Name = r.Name, Locked = r.Password.Length > 0, Phase = (byte)r.Phase,
                Players = players, Seats = r.Seats.Length, MapName = r.Map.Name, Spectators = r.Spectators.Count, AllowSpectators = r.AllowSpectators
            });
        }
        list.Sort((a, b) => a.Id.CompareTo(b.Id));
        return new TWRoomList { Rooms = list.ToArray() };
    }

    void OnCreateRoom(NetworkConnectionToClient conn, TWCreateRoom m)
    {
        var c = Get(conn);
        if (c == null || !c.Hello) return;
        if (rooms.Count >= MaxRooms) { Error(c, "部屋が多すぎます。しばらくしてからお試しください"); return; }
        string err;
        var map = ParseMap(m.MapJson, out err);
        if (map == null) { Error(c, err); return; }
        if (c.Room != null) Leave(c);
        var r = new Room
        {
            Id = nextRoomId++, Name = Clean(m.Name, TWProtocol.MaxRoomNameLength, c.Name + "の部屋"),
            Password = Clean(m.Password, 16, ""), Map = map, MapJson = MapJson.ToJson(map),
            TimeLimitSec = ClampTime(m.TimeLimitSec), AllowSpectators = m.AllowSpectators, Host = c
        };
        r.Seats = NewSeats(map.Players);
        rooms[r.Id] = r;
        Sit(r, c, 0);
        SendState(r);
        SystemChat(r, c.Name + " さんが部屋を作りました");
        listDirty = true;
    }

    static int ClampTime(int sec) { foreach (var t in TimeLimits) if (t == sec) return sec; return 480; }

    static Seat[] NewSeats(int n) { var s = new Seat[n]; for (int i = 0; i < n; i++) s[i] = new Seat(); return s; }

    static MapDef ParseMap(string json, out string err)
    {
        var map = MapJson.FromJson(json, out err);
        if (map == null) return null;
        var problems = MapValidator.Validate(map);
        if (problems.Count > 0) { err = "マップに問題があります：" + problems[0]; return null; }
        return map;
    }

    void Sit(Room r, Client c, int seat)
    {
        var s = r.Seats[seat];
        s.Client = c; s.Token = c.Token; s.Name = c.Name; s.Com = false; s.Ready = false; s.Away = false; s.Takeover = false;
        c.Room = r; c.Seat = seat;
        r.Spectators.Remove(c);
    }

    void OnJoinRoom(NetworkConnectionToClient conn, TWJoinRoom m)
    {
        var c = Get(conn);
        if (c == null || !c.Hello) return;
        Room r;
        if (!rooms.TryGetValue(m.RoomId, out r)) { Error(c, "その部屋はもうありません"); conn.Send(BuildList()); return; }
        if (c.Room == r) return;

        // 試合中に戻ってきた本人は合言葉なしで席に戻す
        if (r.Phase != TWRoomPhase.Waiting && !m.Spectate)
        {
            for (int i = 0; i < r.Seats.Length; i++)
            {
                var s = r.Seats[i];
                if (s.Token == c.Token && s.Client == null && !s.Resigned)
                {
                    if (c.Room != null) Leave(c);
                    s.Client = c; s.Away = false; s.Takeover = false; s.Name = c.Name;
                    c.Room = r; c.Seat = i;
                    if (r.Host == null) r.Host = c;
                    SendState(r);
                    SendGameStart(r, c, true);
                    UpdateDuty(r);
                    SystemChat(r, c.Name + " さんが戻りました");
                    listDirty = true;
                    return;
                }
            }
        }

        if (r.Password.Length > 0 && m.Password != r.Password) { Error(c, "合言葉が違います"); return; }

        if (m.Spectate || r.Phase != TWRoomPhase.Waiting)
        {
            if (!r.AllowSpectators) { Error(c, r.Phase != TWRoomPhase.Waiting ? "試合中で、観戦は許可されていません" : "この部屋は観戦できません"); return; }
            if (r.Spectators.Count >= MaxSpectators) { Error(c, "観戦の人数がいっぱいです"); return; }
            if (c.Room != null) Leave(c);
            c.Room = r; c.Seat = -1;
            r.Spectators.Add(c);
            if (r.Host == null) r.Host = c;
            SendState(r);
            if (r.Phase != TWRoomPhase.Waiting) SendGameStart(r, c, true);
            listDirty = true;
            return;
        }

        int free = -1;
        for (int i = 0; i < r.Seats.Length && free < 0; i++) if (r.Seats[i].Free) free = i;
        if (free < 0) { Error(c, r.AllowSpectators ? "満席です（観戦で入れます）" : "満席です"); return; }
        if (c.Room != null) Leave(c);
        Sit(r, c, free);
        SendState(r);
        SystemChat(r, c.Name + " さんが入りました");
        listDirty = true;
    }

    /// <summary>部屋から抜ける（切断も含む）。試合中なら席は残して戻れるようにする</summary>
    void Leave(Client c, bool notify = true)
    {
        var r = c.Room;
        if (r == null) return;
        if (c.Seat >= 0 && c.Seat < r.Seats.Length && r.Seats[c.Seat].Client == c)
        {
            var s = r.Seats[c.Seat];
            if (r.Phase == TWRoomPhase.Waiting) s.Clear();
            else { s.Client = null; s.Away = true; s.AwaySince = Time.unscaledTime; }
        }
        r.Spectators.Remove(c);
        c.Room = null; c.Seat = -1;
        if (notify && c.Conn != null) { c.Conn.Send(new TWKicked { Reason = "" }); c.Conn.Send(BuildList()); }

        if (r.Host == c) r.Host = PickHost(r);
        if (r.Host == null && !AnyAwayHuman(r)) { DeleteRoom(r, "部屋がなくなりました"); return; }
        SystemChat(r, c.Name + " さんが抜けました");
        SendState(r);
        UpdateDuty(r);
        listDirty = true;
    }

    static Client PickHost(Room r)
    {
        foreach (var s in r.Seats) if (s.Client != null) return s.Client;
        return r.Spectators.Count > 0 ? r.Spectators[0] : null;
    }

    static bool AnyAwayHuman(Room r)
    {
        if (r.Phase == TWRoomPhase.Waiting) return false;
        foreach (var s in r.Seats) if (s.Away && !s.Resigned) return true;
        return false;
    }

    void DeleteRoom(Room r, string reason)
    {
        foreach (var c in Members(r))
        {
            c.Room = null; c.Seat = -1;
            c.Conn.Send(new TWKicked { Reason = reason });
            c.Conn.Send(BuildList());
        }
        rooms.Remove(r.Id);
        listDirty = true;
    }

    static List<Client> Members(Room r)
    {
        var l = new List<Client>();
        foreach (var s in r.Seats) if (s.Client != null) l.Add(s.Client);
        l.AddRange(r.Spectators);
        return l;
    }

    // ======================================================================
    // 部屋（待機中）
    // ======================================================================

    bool IsHost(Client c) { return c != null && c.Room != null && c.Room.Host == c; }

    void OnSettings(NetworkConnectionToClient conn, TWRoomSettings m)
    {
        var c = Get(conn);
        if (!IsHost(c) || c.Room.Phase != TWRoomPhase.Waiting) return;
        var r = c.Room;
        if (!string.IsNullOrEmpty(m.MapJson))
        {
            string err;
            var map = ParseMap(m.MapJson, out err);
            if (map == null) { Error(c, err); return; }
            if (map.Players != r.Seats.Length)
            {
                var seats = NewSeats(map.Players);
                for (int i = 0; i < r.Seats.Length; i++)
                {
                    var old = r.Seats[i];
                    if (i < seats.Length) { seats[i] = old; continue; }
                    if (old.Client != null) { old.Client.Seat = -1; r.Spectators.Add(old.Client); }
                }
                r.Seats = seats;
            }
            r.Map = map; r.MapJson = MapJson.ToJson(map);
        }
        r.TimeLimitSec = ClampTime(m.TimeLimitSec);
        r.AllowSpectators = m.AllowSpectators;
        foreach (var s in r.Seats) s.Ready = false;
        SendState(r);
        listDirty = true;
    }

    void OnTakeSeat(NetworkConnectionToClient conn, TWTakeSeat m)
    {
        var c = Get(conn);
        if (c == null || c.Room == null || c.Room.Phase != TWRoomPhase.Waiting) return;
        var r = c.Room;
        if (m.Seat == c.Seat) return;
        if (m.Seat < 0)
        {
            if (!r.AllowSpectators && !IsHost(c)) { Error(c, "この部屋は観戦できません"); return; }
            if (c.Seat >= 0) r.Seats[c.Seat].Clear();
            c.Seat = -1;
            if (!r.Spectators.Contains(c)) r.Spectators.Add(c);
        }
        else
        {
            if (m.Seat >= r.Seats.Length || !r.Seats[m.Seat].Free) { Error(c, "その席は空いていません"); return; }
            if (c.Seat >= 0) r.Seats[c.Seat].Clear();
            Sit(r, c, m.Seat);
        }
        SendState(r);
        listDirty = true;
    }

    void OnSetCom(NetworkConnectionToClient conn, TWSetCom m)
    {
        var c = Get(conn);
        if (!IsHost(c) || c.Room.Phase != TWRoomPhase.Waiting) return;
        var r = c.Room;
        if (m.Seat < 0 || m.Seat >= r.Seats.Length) return;
        var s = r.Seats[m.Seat];
        if (m.Com && (s.Free || s.Com)) { s.Com = true; s.Name = "COM"; s.Ready = true; s.Profile = new TW.Sim.ComProfile(m.React, m.Gaze, m.Move, m.Brain).Clamped(); }   // COM の席なら強さの変更
        else if (!m.Com && s.Com) s.Clear();
        SendState(r);
        listDirty = true;
    }

    void OnReady(NetworkConnectionToClient conn, TWSetReady m)
    {
        var c = Get(conn);
        if (c == null || c.Room == null || c.Room.Phase != TWRoomPhase.Waiting || c.Seat < 0) return;
        c.Room.Seats[c.Seat].Ready = m.Ready;
        SendState(c.Room);
    }

    void OnStart(NetworkConnectionToClient conn, TWStartGame m)
    {
        var c = Get(conn);
        if (!IsHost(c) || c.Room.Phase != TWRoomPhase.Waiting) return;
        var r = c.Room;
        foreach (var s in r.Seats)
        {
            if (s.Free || (s.Client == null && !s.Com)) { Error(c, "空いている席があります（COMを入れるか、人が座るのを待ってください）"); return; }
            if (!s.Com && s.Client != r.Host && !s.Ready) { Error(c, "準備ができていない人がいます"); return; }
        }
        r.Phase = TWRoomPhase.Countdown;
        r.CountdownUntil = Time.unscaledTime + CountdownSeconds;
        r.Sim = new Simulation(r.Map);
        r.Sim.TimeLimitTicks = r.TimeLimitSec <= 0 ? int.MaxValue : r.TimeLimitSec * Balance.TicksPerSecond;
        r.Pending.Clear(); r.Unsent.Clear(); r.LastSentTick = 0; r.HashTick = 0; r.Acc = 0;
        foreach (var s in r.Seats) { s.Away = s.Takeover = s.Resigned = false; }
        foreach (var mem in Members(r)) SendGameStart(r, mem, false);
        SendState(r);
        r.DutyKey = "";
        UpdateDuty(r);
        listDirty = true;
    }

    void SendGameStart(Room r, Client c, bool late)
    {
        var names = new string[r.Seats.Length];
        for (int i = 0; i < names.Length; i++) names[i] = r.Seats[i].Com ? "COM" : (r.Seats[i].Name ?? "?");
        c.Conn.Send(new TWGameStart
        {
            MapJson = r.MapJson, TimeLimitTicks = r.Sim.TimeLimitTicks, YourSeat = c.Seat, Names = names,
            Countdown = late ? 0 : Mathf.Max(0, r.CountdownUntil - Time.unscaledTime), Late = late
        });
        if (late) c.Conn.Send(new TWSnapshot { State = r.Sim.SaveState() });
    }

    void OnChat(NetworkConnectionToClient conn, TWChat m)
    {
        var c = Get(conn);
        if (c == null || c.Room == null || c.Room.Phase != TWRoomPhase.Waiting) return;
        if (Time.unscaledTime - c.LastChat < 0.6f) return;
        c.LastChat = Time.unscaledTime;
        var text = Clean(m.Text, TWProtocol.MaxChatLength, "");
        if (text.Length == 0) return;
        var line = new TWChatLine { From = c.Name, Seat = c.Seat, Text = text };
        foreach (var mem in Members(c.Room)) mem.Conn.Send(line);
    }

    void SystemChat(Room r, string text)
    {
        if (r.Phase != TWRoomPhase.Waiting) return;
        var line = new TWChatLine { From = "", Seat = -1, Text = text, System = true };
        foreach (var mem in Members(r)) mem.Conn.Send(line);
    }

    void SendState(Room r)
    {
        var seats = new TWSeatInfo[r.Seats.Length];
        for (int i = 0; i < seats.Length; i++)
        {
            var s = r.Seats[i];
            seats[i] = new TWSeatInfo
            {
                Name = s.Com ? "COM" : (s.Name ?? ""), Occupied = s.Token != null, Com = s.Com, Ready = s.Ready || s.Com,
                Connected = s.Client != null, Host = s.Client != null && s.Client == r.Host, Takeover = s.Takeover, Resigned = s.Resigned,
                ComReact = s.Profile.ReactTicks, ComGaze = s.Profile.GazeTicks, ComMove = s.Profile.MoveCentis, ComBrain = s.Profile.Brain
            };
        }
        var specs = new string[r.Spectators.Count];
        for (int i = 0; i < specs.Length; i++) specs[i] = r.Spectators[i].Name + (r.Spectators[i] == r.Host ? "（部屋主）" : "");
        foreach (var c in Members(r))
            c.Conn.Send(new TWRoomState
            {
                RoomId = r.Id, Name = r.Name, Locked = r.Password.Length > 0, Phase = (byte)r.Phase,
                MapJson = r.MapJson, TimeLimitSec = r.TimeLimitSec, AllowSpectators = r.AllowSpectators,
                Seats = seats, Spectators = specs, YourSeat = c.Seat, YouAreHost = c == r.Host
            });
    }

    /// <summary>部屋主の端末に、代打ちすべき席（COM・切断中）を知らせる</summary>
    void UpdateDuty(Room r)
    {
        if (r.Phase == TWRoomPhase.Waiting || r.Host == null) return;
        var list = new List<int>();
        var profiles = new List<int>();
        for (int i = 0; i < r.Seats.Length; i++)
        {
            var s = r.Seats[i];
            if (s.Resigned || !(s.Com || s.Takeover)) continue;
            list.Add(i);
            var pf = s.Com ? s.Profile : TW.Sim.SimpleAI.Presets[TW.Sim.SimpleAI.DefaultLevel];   // 切断した人の代わりは「ふつう」
            profiles.Add(pf.ReactTicks); profiles.Add(pf.GazeTicks); profiles.Add(pf.MoveCentis); profiles.Add(pf.Brain);
        }
        string key = r.Host.Conn.connectionId + ":" + string.Join(",", list) + "/" + string.Join(",", profiles);
        if (key == r.DutyKey) return;
        r.DutyKey = key;
        foreach (var c in Members(r)) c.Conn.Send(new TWComDuty { Seats = c == r.Host ? list.ToArray() : new int[0], Profiles = c == r.Host ? profiles.ToArray() : new int[0] });
    }

    // ======================================================================
    // 試合
    // ======================================================================

    void OnCmd(NetworkConnectionToClient conn, TWCmd m)
    {
        var c = Get(conn);
        if (c == null || c.Room == null || c.Room.Phase != TWRoomPhase.Playing) return;
        var r = c.Room;
        // 連打の上限（1秒に20操作）
        if (Time.unscaledTime - c.CmdWindowStart > 1f) { c.CmdWindowStart = Time.unscaledTime; c.CmdCount = 0; }
        if (++c.CmdCount > 20) return;

        int player;
        if (m.Seat < 0)
        {
            if (c.Seat < 0 || r.Seats[c.Seat].Client != c || r.Seats[c.Seat].Resigned) return;
            player = c.Seat;
        }
        else
        {
            if (r.Host != c || m.Seat >= r.Seats.Length) return;
            var s = r.Seats[m.Seat];
            if (s.Resigned || !(s.Com || s.Takeover)) return;
            player = m.Seat;
        }
        if (m.Type == (byte)CommandType.Connect) r.Pending.Add(Command.Connect(player, m.A, m.B));
        else if (m.Type == (byte)CommandType.Cut) r.Pending.Add(Command.Cut(player, m.A, m.B));
    }

    void OnResign(NetworkConnectionToClient conn, TWResign m)
    {
        var c = Get(conn);
        if (c == null || c.Room == null || c.Room.Phase != TWRoomPhase.Playing || c.Seat < 0) return;
        var s = c.Room.Seats[c.Seat];
        if (s.Resigned) return;
        s.Resigned = true;
        c.Room.Pending.Add(Command.Resign(c.Seat));
        SendState(c.Room);
        UpdateDuty(c.Room);
    }

    void OnSnapshotRequest(NetworkConnectionToClient conn, TWSnapshotRequest m)
    {
        var c = Get(conn);
        if (c == null || c.Room == null || c.Room.Sim == null || c.Room.Phase == TWRoomPhase.Waiting) return;
        conn.Send(new TWSnapshot { State = c.Room.Sim.SaveState() });
    }

    public void Tick(float dt)
    {
        var list = new List<Room>(rooms.Values);
        foreach (var r in list) TickRoom(r, dt);

        listTimer -= dt;
        if (listDirty && listTimer <= 0)
        {
            listDirty = false; listTimer = 0.5f;
            var msg = BuildList();
            foreach (var c in clients.Values) if (c.Hello && c.Room == null) c.Conn.Send(msg);
        }
    }

    void TickRoom(Room r, float dt)
    {
        if (r.Phase == TWRoomPhase.Countdown && Time.unscaledTime >= r.CountdownUntil) { r.Phase = TWRoomPhase.Playing; r.Acc = 0; SendState(r); }
        if (r.Phase != TWRoomPhase.Playing) return;

        // 切断した人：猶予が過ぎたら COM が代打ち
        bool hadHuman = false, humanLeft = false, changed = false;
        foreach (var s in r.Seats)
        {
            if (s.Away && !s.Takeover && !s.Resigned && Time.unscaledTime - s.AwaySince >= AwayGraceSeconds) { s.Takeover = true; changed = true; }
            if (s.Com) continue;   // 最初から COM の席は数えない
            hadHuman = true;
            // つながっている、または切断してもまだ猶予中なら、その人はまだいる
            if (s.Client != null || (s.Away && !s.Takeover && !s.Resigned)) humanLeft = true;
        }
        if (changed) { SendState(r); UpdateDuty(r); }
        // プレイヤーだった席がすべて代打ち（または降参して退出）になったら終わる
        if (hadHuman && !humanLeft) { DeleteRoom(r, "プレイヤーが全員いなくなったため、試合を終了しました"); return; }
        // 全員 COM の試合は、部屋に誰もいなくなったら終わる（代打ちを動かす部屋主もいないため）
        if (!hadHuman)
        {
            bool anyMember = false;
            foreach (var c in Members(r)) { anyMember = true; break; }
            if (!anyMember) { DeleteRoom(r, "部屋に誰もいなくなったため、試合を終了しました"); return; }
        }

        float step = 1f / Balance.TicksPerSecond;
        r.Acc += dt;
        int guard = 0;
        while (r.Acc >= step && guard++ < 10)
        {
            r.Acc -= step;
            int tick = r.Sim.Tick + 1;
            foreach (var cmd in r.Pending)
            {
                r.Sim.Enqueue(cmd);
                r.Unsent.Add(new TWCmdEntry { Tick = tick, Player = (byte)cmd.Player, Type = (byte)cmd.Type, A = cmd.A, B = cmd.B });
            }
            r.Pending.Clear();
            r.Sim.Step();
            if (r.Sim.Tick % HashEveryTicks == 0) { r.HashTick = r.Sim.Tick; r.Hash = r.Sim.StateHash(); }
            if (r.Sim.Tick - r.LastSentTick >= SendEveryTicks || r.Sim.Over) SendBundle(r);
            if (r.Sim.Over) { FinishGame(r); return; }
        }
    }

    void SendBundle(Room r)
    {
        var msg = new TWTickBundle { UpTo = r.Sim.Tick, Cmds = r.Unsent.ToArray(), HashTick = r.HashTick, Hash = r.Hash };
        r.Unsent.Clear();
        r.HashTick = 0;
        r.LastSentTick = r.Sim.Tick;
        foreach (var c in Members(r)) c.Conn.Send(msg);
    }

    void FinishGame(Room r)
    {
        var over = new TWGameOver { Winner = r.Sim.Winner, Tick = r.Sim.Tick };
        r.Phase = TWRoomPhase.Waiting;
        foreach (var c in Members(r)) c.Conn.Send(over);
        // 戻ってこなかった人の席を空け、次の試合に備える
        foreach (var s in r.Seats)
        {
            if (s.Com) { s.Takeover = s.Resigned = false; continue; }
            if (s.Client == null) s.Clear();
            else { s.Ready = false; s.Away = s.Takeover = s.Resigned = false; }
        }
        r.Sim = null;
        r.DutyKey = "";
        if (r.Host == null) r.Host = PickHost(r);
        if (r.Host == null) { DeleteRoom(r, "部屋がなくなりました"); return; }
        SendState(r);
        listDirty = true;
    }
}
