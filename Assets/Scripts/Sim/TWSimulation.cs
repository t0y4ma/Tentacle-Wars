// Tentacle Wars Online - 対戦シミュレーション本体（Unity非依存・サーバー/クライアント共通）
using System;
using System.Collections.Generic;
using System.IO;

namespace TW.Sim
{
    /// <summary>バランス値はすべてここに集約する。</summary>
    public static class Balance
    {
        public const int TicksPerSecond = 20;
        public const int GradeCount = 6;
        public static readonly string[] GradeNames = { "Spore", "Embryo", "Pulsar", "Gamma", "Solar", "Dominator" };

        // 等級 i → i+1 は Energy >= Up[i]、等級 i+1 → i は Energy < Down[i]
        public static readonly int[] Up = { 15, 40, 80, 120, 160 };
        public static readonly int[] Down = { 5, 30, 60, 100, 140 };

        // 自己生産間隔(tick)。等級が上がるほど遅い
        public static readonly int[] SelfInterval = { 40, 50, 60, 80, 100, 120 };
        // 触手1本の送出間隔。粒は常に1ずつで、端数のある間隔を表すため 1/4 tick 単位で持つ。
        // Spore 2秒, Embryo 1秒, Pulsar 0.75秒, Gamma 0.5秒, Solar 0.25秒, Dominator 0.125秒(2.5tick)
        public const int SubTicks = 4;
        public static readonly int[] SendIntervalSub = { 160, 80, 60, 40, 20, 10 };
        public static readonly int[] MaxTentacles = { 1, 2, 2, 3, 3, 3 };

        // 触手を出している間の自己生産クールダウン倍率 k = 3/2
        public const int SlowNum = 3, SlowDen = 2;

        public const int ExtendTicksPerNode = 2;   // 伸長速度（2tickで1ノード）
        public const int ExtendCostPerNode = 1;    // 伸長コスト
        public const int RetractNodesPerTick = 1;  // 撤回時の返還速度
        public const int FlyNodesPerTick = 1;      // 切断後に先端側が流れ込む速度
        public const int PacketNodesPerTick = 1;   // 送出したエネルギー粒が触手上を進む速度
        // 座標は 1ワールド単位 = 100 の整数で扱う（端末ごとの小数誤差で盤面がずれないように）
        public const int Unit = 100;
        public const int NodeLengthU = 35;         // 1ノードの長さ
        public const int PathEndRadiusU = 50;      // ノード数計算に使う細胞の名目半径
        public const float NodeLength = NodeLengthU / (float)Unit;
        public const float PathEndRadius = PathEndRadiusU / (float)Unit;

        public const int CaptureEnergy = 10;       // 敵細胞占領後のエネルギー
        public const int DefaultMaxEnergy = 200;
        public const int TimeLimitTicks = 8 * 60 * TicksPerSecond;

        public static float CellRadius(int grade) { return 0.42f + 0.07f * grade; }

        /// <summary>中立細胞の占領に必要な量 = floor(3.3 * floor(e / 10))（整数演算で計算）</summary>
        public static int CaptureThreshold(int neutralEnergy) { return 33 * (neutralEnergy / 10) / 10; }

        public static int GradeFor(int energy)
        {
            int g = 0;
            while (g < GradeCount - 1 && energy >= Up[g]) g++;
            return g;
        }
    }

    public class Cell
    {
        public int Id;
        public int PX, PY;         // 整数座標（Balance.Unit 倍）
        public float X { get { return PX / (float)Balance.Unit; } }
        public float Y { get { return PY / (float)Balance.Unit; } }
        public int Owner;          // -1 = 中立
        public int Energy;
        public int Grade;
        public int MaxEnergy;
        public int SelfTimer;
        // 中立細胞用
        public int CaptureValue;   // 占領に必要な量（中立のエネルギーから算出）
        public int GaugeOwner = -1;
        public int Gauge;
        // オーバーフロー（次tickで流す）
        internal int PendingOverflow, NextOverflow;

        public bool IsNeutral { get { return Owner < 0; } }
        public float Radius { get { return Balance.CellRadius(Grade); } }
    }

    public enum TentacleState { Extending, Attached, Retracting, Flying }

    public class Tentacle
    {
        public int Id, Owner, Src, Dst;
        public TentacleState State;
        public int Length;      // 全経路のノード数
        public int Ext;         // Extending/Attached/Retracting: 根元から伸びているノード数
        public int FlyCount;    // Flying: 残っているノード数
        public int Head;        // Flying: 先頭の位置（接続先、または押し合いの接点）。ここから相手に吸収される
        public int Timer;
        public bool Dead;
        public readonly List<Packet> Packets = new List<Packet>();

        public bool IsActive { get { return State == TentacleState.Extending || State == TentacleState.Attached; } }

        /// <summary>描画用：経路上のノード区間 [from, to)</summary>
        public void GetSpan(out int from, out int to)
        {
            if (State == TentacleState.Flying) { from = Head - FlyCount; to = Head; }
            else { from = 0; to = Ext; }
        }
    }

    /// <summary>触手が通れない障害物（円）。整数座標。</summary>
    public struct Obstacle
    {
        public int PX, PY, PR;
        public float X { get { return PX / (float)Balance.Unit; } }
        public float Y { get { return PY / (float)Balance.Unit; } }
        public float R { get { return PR / (float)Balance.Unit; } }
    }

    /// <summary>触手上を移動中のエネルギー粒。Pos は根元からのノード位置。先端に届いた時点で反映される。</summary>
    public struct Packet { public int Pos; public int Amount; }

    public enum CommandType { Connect, Cut, Resign }

    public struct Command
    {
        public CommandType Type;
        public int Player;
        public int A;        // Connect: 発射元 / Cut: 触手ID
        public int B;        // Connect: 接続先 / Cut: 切れ目の番号（根元から何ノード目の後ろで切るか。0..Length）
        public static Command Connect(int p, int src, int dst) { return new Command { Type = CommandType.Connect, Player = p, A = src, B = dst }; }
        public static Command Cut(int p, int tentacle, int split) { return new Command { Type = CommandType.Cut, Player = p, A = tentacle, B = split }; }
        /// <summary>切断位置の割合（根元0〜先端1）から、最も近い切れ目で切るコマンドを作る（クライアント側で使う）</summary>
        public static Command CutAt(int p, Tentacle t, float fraction)
        {
            int split = (int)Math.Round(fraction * t.Length);
            return Cut(p, t.Id, split);
        }
        /// <summary>全体を根元へ引き戻す</summary>
        public static Command Retract(int p, int tentacle) { return Cut(p, tentacle, int.MaxValue); }
        /// <summary>降参。その陣営の細胞はエネルギーを持ったまま中立になり、触手は消える</summary>
        public static Command Resign(int p) { return new Command { Type = CommandType.Resign, Player = p }; }
    }

    public class Simulation
    {
        public readonly List<Cell> Cells = new List<Cell>();
        public readonly List<Tentacle> Tentacles = new List<Tentacle>();
        public readonly List<Obstacle> Obstacles = new List<Obstacle>();
        public readonly MapDef Map;
        public int PlayerCount { get; private set; }
        public int Tick { get; private set; }
        public bool Over { get; private set; }
        public int Winner { get; private set; } = -1;   // Over時: -1 = 引き分け
        public int TimeLimitTicks = Balance.TimeLimitTicks;

        readonly List<Command> queue = new List<Command>();
        int nextTentacleId = 1;

        public Simulation(MapDef map)
        {
            Map = map;
            PlayerCount = map.Players;
            for (int i = 0; i < map.Cells.Count; i++)
            {
                var d = map.Cells[i];
                var c = new Cell { Id = i, PX = ToUnit(d.X), PY = ToUnit(d.Y), Owner = d.Owner, MaxEnergy = d.Max, Energy = d.Energy };
                c.Grade = Balance.GradeFor(c.Energy);
                if (d.Owner < 0) c.CaptureValue = Balance.CaptureThreshold(c.Energy);
                Cells.Add(c);
            }
            foreach (var o in map.Obstacles)
                Obstacles.Add(new Obstacle { PX = ToUnit(o.X), PY = ToUnit(o.Y), PR = ToUnit(o.R) });
        }

        public static int ToUnit(float v) { return (int)Math.Round(v * Balance.Unit); }

        public void Enqueue(Command c) { queue.Add(c); }

        // ---------------- 問い合わせ ----------------

        public int ActiveCount(int cellId)
        {
            int n = 0;
            foreach (var t in Tentacles) if (!t.Dead && t.Src == cellId && t.IsActive) n++;
            return n;
        }

        public Tentacle FindActive(int src, int dst)
        {
            foreach (var t in Tentacles) if (!t.Dead && t.IsActive && t.Src == src && t.Dst == dst) return t;
            return null;
        }

        public Tentacle FindTentacle(int id)
        {
            foreach (var t in Tentacles) if (t.Id == id && !t.Dead) return t;
            return null;
        }

        public int PathNodes(Cell a, Cell b)
        {
            long dx = b.PX - a.PX, dy = b.PY - a.PY;
            long d = ISqrt(dx * dx + dy * dy) - 2 * Balance.PathEndRadiusU;
            if (d <= 0) return 1;
            return (int)Math.Max(1, (d + Balance.NodeLengthU - 1) / Balance.NodeLengthU);
        }

        /// <summary>整数の平方根（切り捨て）。どの端末でも同じ結果になる。</summary>
        public static long ISqrt(long n)
        {
            if (n <= 0) return 0;
            long x = (long)Math.Sqrt(n);
            while (x * x > n) x--;
            while ((x + 1) * (x + 1) <= n) x++;
            return x;
        }

        public bool PathClear(Cell a, Cell b)
        {
            foreach (var o in Obstacles)
                if (SegmentHitsCircle(a.PX, a.PY, b.PX, b.PY, o.PX, o.PY, o.PR)) return false;
            return true;
        }

        /// <summary>線分ABが円に食い込むか（整数演算）</summary>
        public static bool SegmentHitsCircle(long ax, long ay, long bx, long by, long ox, long oy, long r)
        {
            long dx = bx - ax, dy = by - ay;
            long cx = ox - ax, cy = oy - ay;
            long r2 = r * r;
            long len2 = dx * dx + dy * dy;
            long dot = cx * dx + cy * dy;
            if (len2 == 0 || dot <= 0) return cx * cx + cy * cy < r2;
            if (dot >= len2) { long ex = ox - bx, ey = oy - by; return ex * ex + ey * ey < r2; }
            long cross = cx * dy - cy * dx;
            return cross * cross < r2 * len2;
        }

        /// <summary>接続可能か。不可なら理由を返す（null = 可）。</summary>
        public string CanConnect(int player, int srcId, int dstId)
        {
            if (Over) return "終了";
            if (srcId == dstId || srcId < 0 || dstId < 0 || srcId >= Cells.Count || dstId >= Cells.Count) return "対象なし";
            var s = Cells[srcId];
            if (s.Owner != player) return "自分の細胞ではない";
            if (FindActive(srcId, dstId) != null) return "接続済み";
            if (ActiveCount(srcId) >= Balance.MaxTentacles[s.Grade]) return "触手の本数上限";
            if (!PathClear(s, Cells[dstId])) return "障害物";
            if (s.Energy < 1 + Balance.ExtendCostPerNode) return "エネルギー不足";
            return null;
        }

        // ---------------- 進行 ----------------

        public void Step()
        {
            if (Over) { queue.Clear(); return; }
            Tick++;

            // 同一tick内の処理順で有利不利が出ないよう、陣営の優先順を毎tick回す
            for (int k = 0; k < PlayerCount; k++)
            {
                int p = (FirstPlayer() + k) % PlayerCount;
                foreach (var c in queue) if (c.Player == p) Apply(c);
            }
            queue.Clear();

            // 前tickのオーバーフローを流す
            foreach (var c in Cells)
            {
                int amt = c.PendingOverflow;
                c.PendingOverflow = 0;
                if (amt > 0 && c.Owner >= 0) DistributeOverflow(c, amt);
            }

            int count = Tentacles.Count;
            for (int k = 0; k < PlayerCount; k++)
            {
                int p = (FirstPlayer() + k) % PlayerCount;
                for (int i = 0; i < count; i++)
                {
                    var t = Tentacles[i];
                    if (t.Dead || t.Owner != p) continue;
                    UpdateTentacle(t);
                }
            }

            foreach (var c in Cells)
            {
                if (c.Owner < 0) continue;
                int interval = Balance.SelfInterval[c.Grade];
                if (ActiveCount(c.Id) > 0) interval = interval * Balance.SlowNum / Balance.SlowDen;
                c.SelfTimer++;
                if (c.SelfTimer >= interval) { c.SelfTimer = 0; AddEnergy(c, 1); }
            }

            foreach (var c in Cells) { c.PendingOverflow += c.NextOverflow; c.NextOverflow = 0; }
            Tentacles.RemoveAll(t => t.Dead);
            CheckEnd();
        }

        /// <summary>そのtickで最初に処理する陣営。周期的な偏りが出ないよう tick のハッシュで決める（決定的）</summary>
        int FirstPlayer()
        {
            uint h = (uint)Tick * 2654435761u;
            return (int)((h >> 16) % (uint)PlayerCount);
        }

        void Apply(Command c)
        {
            switch (c.Type)
            {
                case CommandType.Connect:
                    if (CanConnect(c.Player, c.A, c.B) != null) return;
                    // 自分の細胞同士は片方向だけ：逆向きの触手は元の細胞へ戻す
                    var rev = FindActive(c.B, c.A);
                    if (rev != null && rev.Owner == c.Player) CutTentacle(rev, int.MaxValue);
                    var s = Cells[c.A];
                    Tentacles.Add(new Tentacle
                    {
                        Id = nextTentacleId++, Owner = c.Player, Src = c.A, Dst = c.B,
                        State = TentacleState.Extending, Length = PathNodes(s, Cells[c.B])
                    });
                    break;
                case CommandType.Cut:
                    var t = FindTentacle(c.A);
                    if (t == null || t.Owner != c.Player) return;
                    CutTentacle(t, c.B);
                    break;
                case CommandType.Resign:
                    foreach (var rt in Tentacles) if (rt.Owner == c.Player) rt.Dead = true;
                    foreach (var cell in Cells)
                        if (cell.Owner == c.Player)
                        {
                            cell.Owner = -1;
                            cell.CaptureValue = Balance.CaptureThreshold(cell.Energy);
                            cell.Gauge = 0; cell.GaugeOwner = -1; cell.SelfTimer = 0;
                            cell.PendingOverflow = 0; cell.NextOverflow = 0;
                        }
                    break;
            }
        }

        void CutTentacle(Tentacle t, int split)
        {
            var src = Cells[t.Src];
            if (t.State == TentacleState.Extending)
            {
                // 伸ばし直し中に積んでいた粒は発射元に戻す
                foreach (var p in t.Packets) AddEnergy(src, p.Amount);
                t.Packets.Clear();
                t.State = TentacleState.Retracting;
                return;
            }
            if (t.State != TentacleState.Attached) return;
            // 切断線に最も近いノード間の切れ目（クライアントが番号で指定）で分割。押し合い中は自分の先端まで
            if (split < 0) split = 0; else if (split > t.Ext) split = t.Ext;
            int fly = t.Ext - split;
            Tentacle flying = null;
            if (fly > 0)
            {
                flying = new Tentacle
                {
                    Id = nextTentacleId++, Owner = t.Owner, Src = t.Src, Dst = t.Dst,
                    State = TentacleState.Flying, Length = t.Length, FlyCount = fly, Head = t.Ext
                };
                Tentacles.Add(flying);
            }
            // 移動中の粒：先端側はそのまま接続先へ、根元側は発射元に戻る
            foreach (var p in t.Packets)
            {
                if (flying != null && p.Pos >= split) flying.Packets.Add(p);
                else AddEnergy(src, p.Amount);
            }
            t.Packets.Clear();
            t.State = TentacleState.Retracting;
            t.Ext = split;
            t.Timer = 0;
            if (split == 0) t.Dead = true;
        }

        /// <summary>向かい合う触手（接続先から発射元へ伸びているもの）の占めている長さと、押し合い相手（敵の触手）</summary>
        void OppositeInfo(Tentacle t, out int occupied, out Tentacle enemy)
        {
            occupied = 0; enemy = null;
            foreach (var o in Tentacles)
            {
                if (o.Dead || o == t || o.Src != t.Dst || o.Dst != t.Src || o.State == TentacleState.Flying) continue;
                if (o.Ext > occupied) occupied = o.Ext;
                if (o.Owner != t.Owner && o.IsActive) enemy = o;
            }
        }

        /// <summary>伸ばしてよい最大ノード数。敵の触手と押し合っていれば中間地点まで（先に作られた方が端数を取る）</summary>
        static int LimitOf(Tentacle t, Tentacle enemy)
        {
            if (enemy == null) return t.Length;
            return t.Id < enemy.Id ? (t.Length + 1) / 2 : t.Length / 2;
        }

        void UpdateTentacle(Tentacle t)
        {
            var src = Cells[t.Src];
            var dst = Cells[t.Dst];
            switch (t.State)
            {
                case TentacleState.Extending:
                    {
                        MovePackets(t, dst, t.Ext, false);   // 伸ばし直し中は、すでに送った粒は先端で待つ
                        int occupied; Tentacle enemy;
                        OppositeInfo(t, out occupied, out enemy);
                        int limit = LimitOf(t, enemy);
                        if (t.Ext >= limit) { t.Ext = limit; t.State = TentacleState.Attached; t.Timer = 0; break; }
                        // 敵の触手が中間地点を越えていれば、押し戻しながら伸びる。味方の触手（引いている途中）なら空くのを待つ
                        bool push = enemy != null && enemy.Ext > LimitOf(enemy, t);
                        if (t.Ext >= t.Length - occupied && !push) { t.Timer = 0; break; }
                        if (++t.Timer < Balance.ExtendTicksPerNode) return;
                        t.Timer = 0;
                        if (src.Energy >= 1 + Balance.ExtendCostPerNode)
                        {
                            SpendEnergy(src, Balance.ExtendCostPerNode);
                            t.Ext++;
                            if (enemy != null && t.Ext + enemy.Ext > t.Length)
                            {
                                // 押し戻された分のノードは相手の細胞へ戻る
                                int back = t.Ext + enemy.Ext - t.Length;
                                enemy.Ext -= back;
                                AddEnergy(Cells[enemy.Src], back);
                            }
                            if (t.Ext >= limit) { t.State = TentacleState.Attached; t.Timer = 0; }
                        }
                        else
                        {
                            foreach (var p in t.Packets) AddEnergy(src, p.Amount);
                            t.Packets.Clear();
                            t.State = TentacleState.Retracting;   // エネルギー不足で中止
                        }
                    }
                    break;

                case TentacleState.Attached:
                    {
                        int occupied; Tentacle enemy;
                        OppositeInfo(t, out occupied, out enemy);
                        if (t.Ext < LimitOf(t, enemy))
                        {
                            // 押し合いの相手がいなくなったので、接続先まで伸ばし直す
                            t.State = TentacleState.Extending;
                            t.Timer = 0;
                            break;
                        }
                        // 先端（接続先、または押し合いの接点）に着いた粒が相手に届く
                        MovePackets(t, dst, t.Ext, true);
                        if (t.Dead) return;
                        t.Timer += Balance.SubTicks;
                        if (t.Timer < Balance.SendIntervalSub[src.Grade]) return;
                        t.Timer -= Balance.SendIntervalSub[src.Grade];   // 端数を次回へ持ち越す
                        t.Packets.Add(new Packet { Pos = 0, Amount = 1 });
                    }
                    break;

                case TentacleState.Retracting:
                    {
                        int n = Math.Min(Balance.RetractNodesPerTick, t.Ext);
                        t.Ext -= n;
                        if (n > 0) AddEnergy(src, n);
                        if (t.Ext <= 0) t.Dead = true;
                    }
                    break;

                case TentacleState.Flying:
                    {
                        // 切った先は先頭（接続先、または押し合いの接点）から相手に吸収される
                        MovePackets(t, dst, t.Head, true);
                        int n = Math.Min(Balance.FlyNodesPerTick, t.FlyCount);
                        t.FlyCount -= n;
                        if (n > 0) Deliver(t.Owner, dst, n);
                        if (t.FlyCount <= 0 && t.Packets.Count == 0) t.Dead = true;
                    }
                    break;
            }
        }

        /// <summary>粒を進める。end（先端）に着いた粒は deliver なら接続先へ反映し、そうでなければ先端で待たせる</summary>
        void MovePackets(Tentacle t, Cell dst, int end, bool deliver)
        {
            for (int i = 0; i < t.Packets.Count; i++)
            {
                var p = t.Packets[i];
                p.Pos = Math.Min(p.Pos + Balance.PacketNodesPerTick, deliver ? int.MaxValue : end);
                t.Packets[i] = p;
            }
            if (!deliver) return;
            while (t.Packets.Count > 0 && t.Packets[0].Pos >= end)
            {
                int amt = t.Packets[0].Amount;
                t.Packets.RemoveAt(0);
                Deliver(t.Owner, dst, amt);
                if (t.Dead) return;   // 反映の結果この触手が消えた
            }
        }

        void DistributeOverflow(Cell c, int amt)
        {
            var outs = new List<Tentacle>();
            foreach (var t in Tentacles)
            {
                if (t.Dead || t.State != TentacleState.Attached || t.Src != c.Id) continue;
                var d = Cells[t.Dst];
                if (d.Owner == c.Owner && d.Energy >= d.MaxEnergy) continue;   // 上限の味方には流さない
                outs.Add(t);
            }
            if (outs.Count == 0) return;   // 流せなければ破棄
            int per = amt / outs.Count, rem = amt % outs.Count;
            for (int i = 0; i < outs.Count; i++)
            {
                int a = per + (i < rem ? 1 : 0);
                if (a > 0) outs[i].Packets.Add(new Packet { Pos = 0, Amount = a });
            }
        }

        /// <summary>owner 陣営から dst へ amt のエネルギーを届ける。</summary>
        void Deliver(int owner, Cell dst, int amt)
        {
            if (dst.Owner == owner) { AddEnergy(dst, amt); return; }

            if (dst.Owner < 0)
            {
                if (dst.GaugeOwner < 0 || dst.GaugeOwner == owner)
                {
                    dst.GaugeOwner = owner;
                    dst.Gauge += amt;
                    if (dst.Gauge > dst.CaptureValue)
                    {
                        // 中立がため込んでいたエネルギーを持ったまま占領者の色に変わる。
                        // 2以上の粒（Dominatorの送出・オーバーフロー）で必要量を越えた分は上乗せする
                        int surplus = dst.Gauge - dst.CaptureValue - 1;
                        dst.Owner = owner;
                        dst.Gauge = 0; dst.GaugeOwner = -1;
                        dst.Energy = Math.Min(dst.Energy + surplus, dst.MaxEnergy);
                        dst.Grade = Balance.GradeFor(dst.Energy);
                        dst.SelfTimer = 0;
                    }
                }
                else
                {
                    dst.Gauge -= amt;
                    if (dst.Gauge < 0) { dst.GaugeOwner = owner; dst.Gauge = -dst.Gauge; }
                    else if (dst.Gauge == 0) dst.GaugeOwner = -1;
                }
                return;
            }

            dst.Energy -= amt;
            if (dst.Energy <= 0) Capture(dst, owner);
            else UpdateGrade(dst);
        }

        void Capture(Cell c, int newOwner)
        {
            int refund = 0;
            foreach (var t in Tentacles)
            {
                if (t.Dead || t.Src != c.Id || t.State == TentacleState.Flying) continue;
                refund += t.Ext;
                t.Dead = true;
            }
            c.Owner = newOwner;
            c.Energy = Math.Min(Balance.CaptureEnergy + refund, c.MaxEnergy);
            c.Grade = Balance.GradeFor(c.Energy);
            c.SelfTimer = 0;
            c.PendingOverflow = 0; c.NextOverflow = 0;
        }

        void AddEnergy(Cell c, int amt)
        {
            c.Energy += amt;
            if (c.Energy > c.MaxEnergy) { c.NextOverflow += c.Energy - c.MaxEnergy; c.Energy = c.MaxEnergy; }
            UpdateGrade(c);
        }

        void SpendEnergy(Cell c, int amt) { c.Energy -= amt; UpdateGrade(c); }

        static void UpdateGrade(Cell c)
        {
            while (c.Grade < Balance.GradeCount - 1 && c.Energy >= Balance.Up[c.Grade]) c.Grade++;
            while (c.Grade > 0 && c.Energy < Balance.Down[c.Grade - 1]) c.Grade--;
        }

        // ---------------- 状態の保存・復元（途中参加・ずれの修正用） ----------------

        const byte StateVersion = 2;

        /// <summary>盤面の状態を書き出す（地図そのものは含まない）</summary>
        public byte[] SaveState()
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(StateVersion);
            w.Write(Tick); w.Write(Over); w.Write(Winner); w.Write(nextTentacleId); w.Write(TimeLimitTicks);
            w.Write(Cells.Count);
            foreach (var c in Cells)
            {
                w.Write((sbyte)c.Owner); w.Write(c.Energy); w.Write((byte)c.Grade); w.Write(c.MaxEnergy); w.Write(c.SelfTimer);
                w.Write(c.CaptureValue); w.Write((sbyte)c.GaugeOwner); w.Write(c.Gauge); w.Write(c.PendingOverflow); w.Write(c.NextOverflow);
            }
            w.Write(Tentacles.Count);
            foreach (var t in Tentacles)
            {
                w.Write(t.Id); w.Write((sbyte)t.Owner); w.Write((short)t.Src); w.Write((short)t.Dst); w.Write((byte)t.State);
                w.Write((short)t.Length); w.Write((short)t.Ext); w.Write((short)t.FlyCount); w.Write((short)t.Head); w.Write(t.Timer); w.Write(t.Dead);
                w.Write((short)t.Packets.Count);
                foreach (var p in t.Packets) { w.Write((short)p.Pos); w.Write((short)p.Amount); }
            }
            w.Write(queue.Count);
            foreach (var q in queue) { w.Write((byte)q.Type); w.Write((sbyte)q.Player); w.Write(q.A); w.Write(q.B); }
            w.Flush();
            return ms.ToArray();
        }

        /// <summary>SaveState で書き出した状態に戻す。同じ地図で作った Simulation に対して使う</summary>
        public bool LoadState(byte[] data)
        {
            try
            {
                var r = new BinaryReader(new MemoryStream(data));
                if (r.ReadByte() != StateVersion) return false;
                Tick = r.ReadInt32(); Over = r.ReadBoolean(); Winner = r.ReadInt32(); nextTentacleId = r.ReadInt32(); TimeLimitTicks = r.ReadInt32();
                if (r.ReadInt32() != Cells.Count) return false;
                foreach (var c in Cells)
                {
                    c.Owner = r.ReadSByte(); c.Energy = r.ReadInt32(); c.Grade = r.ReadByte(); c.MaxEnergy = r.ReadInt32(); c.SelfTimer = r.ReadInt32();
                    c.CaptureValue = r.ReadInt32(); c.GaugeOwner = r.ReadSByte(); c.Gauge = r.ReadInt32(); c.PendingOverflow = r.ReadInt32(); c.NextOverflow = r.ReadInt32();
                }
                Tentacles.Clear();
                int tn = r.ReadInt32();
                for (int i = 0; i < tn; i++)
                {
                    var t = new Tentacle
                    {
                        Id = r.ReadInt32(), Owner = r.ReadSByte(), Src = r.ReadInt16(), Dst = r.ReadInt16(), State = (TentacleState)r.ReadByte(),
                        Length = r.ReadInt16(), Ext = r.ReadInt16(), FlyCount = r.ReadInt16(), Head = r.ReadInt16(), Timer = r.ReadInt32(), Dead = r.ReadBoolean()
                    };
                    int pc = r.ReadInt16();
                    for (int k = 0; k < pc; k++) t.Packets.Add(new Packet { Pos = r.ReadInt16(), Amount = r.ReadInt16() });
                    Tentacles.Add(t);
                }
                queue.Clear();
                int qn = r.ReadInt32();
                for (int i = 0; i < qn; i++)
                    queue.Add(new Command { Type = (CommandType)r.ReadByte(), Player = r.ReadSByte(), A = r.ReadInt32(), B = r.ReadInt32() });
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>盤面のチェックサム（FNV-1a）。サーバーと一致しなければずれている</summary>
        public uint StateHash()
        {
            uint h = 2166136261;
            foreach (var b in SaveState()) { h ^= b; h *= 16777619; }
            return h;
        }

        public int Score(int player)
        {
            int s = 0;
            foreach (var c in Cells) if (c.Owner == player) s += c.Energy;
            foreach (var t in Tentacles)
            {
                if (t.Dead || t.Owner != player) continue;
                s += t.State == TentacleState.Flying ? t.FlyCount : t.Ext;
                foreach (var p in t.Packets) s += p.Amount;
            }
            return s;
        }

        public int CellCount(int player)
        {
            int n = 0;
            foreach (var c in Cells) if (c.Owner == player) n++;
            return n;
        }

        void CheckEnd()
        {
            int alive = 0, last = -1;
            for (int p = 0; p < PlayerCount; p++) if (CellCount(p) > 0) { alive++; last = p; }
            if (alive <= 1) { Over = true; Winner = alive == 1 ? last : -1; return; }
            if (Tick >= TimeLimitTicks)
            {
                Over = true;
                int best = -1, bestScore = -1; bool tie = false;
                for (int p = 0; p < PlayerCount; p++)
                {
                    int s = Score(p);
                    if (s > bestScore) { bestScore = s; best = p; tie = false; }
                    else if (s == bestScore) tie = true;
                }
                Winner = tie ? -1 : best;
            }
        }
    }
}
