// 簡易COM（ローカル対戦・切断時の代打ち用）
// 人間と同じく「手は1本」：1回に1つの操作しかできず、操作には時間がかかる。
//   つなぐ … 判断 ＋ 元の細胞へ視線を移す ＋ カーソルを動かす ＋ つなぎ先へ視線を移してドラッグ（遠いほど長い）
//   切る   … 判断 ＋ 触手へ視線を移す ＋ カーソルを切る位置へ動かす ＋ なぞる
// 強さは判断・視線・カーソルの速さで変える。
using System;
using System.Collections.Generic;

namespace TW.Sim
{
    /// <summary>COM（標準・学習）に共通の操作</summary>
    public interface IComPlayer
    {
        int Player { get; }
        ComProfile Profile { get; }
        Action<Command> Emit { get; set; }
        void ApplyProfile(ComProfile p);
        void Think(Simulation sim);
    }

    /// <summary>強さの設定から COM を作る（学習COMの重みが無ければ標準のCOM）</summary>
    public static class ComFactory
    {
        public static IComPlayer Create(int player, ComProfile p, int seed)
        {
            p = p.Clamped();
            if (p.Brain == ComProfile.BrainLearned && LearnedAI.SharedNet != null)
                return new LearnedAI(player, LearnedAI.SharedNet, seed).SetProfile(p);
            return new SimpleAI(player, seed).SetProfile(p);
        }
    }

    public class SimpleAI : IComPlayer
    {
        public int Player { get; private set; }
        readonly Random rng;

        // ---- 強さ ----
        // 3つの時間で決める（プリセット：よわい・ふつう・つよい。数値は自由に変えられる）
        public static readonly string[] LevelNames = { "よわい", "ふつう", "つよい" };
        public const int DefaultLevel = 1;
        public static readonly ComProfile[] Presets =
        {
            new ComProfile(24, 6, 12),   // よわい：次の手まで1.2秒・目移り0.3秒・距離1あたり0.12秒
            new ComProfile(16, 4, 8),    // ふつう：0.8秒・0.2秒・0.08秒
            new ComProfile(10, 3, 5),    // つよい：0.5秒・0.15秒・0.05秒
        };
        public static int ClampLevel(int level) { return level < 0 ? 0 : level >= LevelNames.Length ? LevelNames.Length - 1 : level; }

        ComProfile profile = Presets[DefaultLevel];
        public ComProfile Profile { get { return profile; } }

        public SimpleAI SetLevel(int level) { return SetProfile(Presets[ClampLevel(level)]); }
        public SimpleAI SetProfile(ComProfile p) { profile = p.Clamped(); return this; }
        public void ApplyProfile(ComProfile p) { SetProfile(p); }

        int react { get { return profile.ReactTicks; } }
        int gaze { get { return profile.GazeTicks; } }
        double ticksPerUnit { get { return profile.MoveCentis * Balance.TicksPerSecond / 100.0; } }
        int swipe { get { return 3 + profile.GazeTicks / 2; } }

        /// <summary>設定すると、操作を sim に直接積まずにここへ渡す（オンラインで部屋主が代打ちするとき）</summary>
        public Action<Command> Emit { get; set; }

        // 実行待ちの操作（手が動いている最中）
        bool hasPending;
        Command pending;
        int pendingAt;
        // カーソルの位置（盤面座標×Unit）。最初は自分の細胞の上にある
        int cursorX, cursorY;
        bool cursorKnown;
        int lookCell = -1;   // 今見ている細胞（-1 = 細胞以外）

        void Send(Simulation sim, Command c) { if (Emit != null) Emit(c); else sim.Enqueue(c); }

        public SimpleAI(int player, int seed = 0)
        {
            Player = player;
            rng = new Random(seed == 0 ? 1234 + player * 77 : seed);
        }

        /// <summary>毎tick呼ぶ。操作を決め、手が動き終わったら sim に積む。</summary>
        public void Think(Simulation sim)
        {
            if (sim.Over) return;
            if (!cursorKnown) PlaceCursorAtHome(sim);

            if (hasPending)
            {
                if (sim.Tick < pendingAt) return;   // まだ手が動いている
                hasPending = false;
                Send(sim, pending);
            }

            // ---- 次の操作を選ぶ（一番価値の高いもの1つ） ----
            Command best = default(Command);
            double bestScore = 0;
            int bestX = 0, bestY = 0;       // 操作の終わりでカーソルがある位置
            double bestTravel = 0;          // カーソルが動く距離（単位）
            int bestExtra = 0;              // 視線の移動・なぞる動作など
            int bestLook = -1;              // 操作の終わりで見ている細胞

            foreach (var c in sim.Cells)
            {
                if (c.Owner != Player) continue;

                // 敵・中立の残量を切断で一気に削り切れるなら切る
                foreach (var t in sim.Tentacles)
                {
                    if (t.Dead || t.Src != c.Id || t.State != TentacleState.Attached) continue;
                    var d = sim.Cells[t.Dst];
                    if (d.Owner == Player) continue;
                    int need = Need(d);
                    if (need <= 0) continue;
                    int give = need + 2;
                    if (give > t.Ext) continue;   // 押し合い中は自分の先端までしか流せない
                    if (rng.NextDouble() < 0.3) continue;   // 少し迷う
                    int split = t.Ext - give;
                    int px, py; PointOn(sim, t, split, out px, out py);
                    Consider(200, Command.Cut(Player, t.Id, split), px, py, Dist(cursorX, cursorY, px, py), gaze + swipe, -1,
                        ref best, ref bestScore, ref bestX, ref bestY, ref bestTravel, ref bestExtra, ref bestLook);
                }

                // 勝ち目のない攻撃は引く
                foreach (var t in sim.Tentacles)
                {
                    if (t.Dead || t.Src != c.Id || t.State != TentacleState.Attached) continue;
                    var d = sim.Cells[t.Dst];
                    if (d.Owner >= 0 && d.Owner != Player && d.Energy > c.Energy * 2 + 20 && c.Energy < 15)
                    {
                        int px, py; PointOn(sim, t, 1, out px, out py);
                        Consider(150, Command.Retract(Player, t.Id), px, py, Dist(cursorX, cursorY, px, py), gaze + swipe, -1,
                            ref best, ref bestScore, ref bestX, ref bestY, ref bestTravel, ref bestExtra, ref bestLook);
                    }
                }

                ConsiderConnect(sim, c, ref best, ref bestScore, ref bestX, ref bestY, ref bestTravel, ref bestExtra, ref bestLook);
            }

            if (bestScore <= 0) return;
            hasPending = true;
            pending = best;
            int think = react * 3 / 4 + rng.Next(react / 2 + 1);   // 判断時間は ±25% ばらつく
            pendingAt = sim.Tick + think + (int)Math.Round(bestTravel * ticksPerUnit) + bestExtra;
            cursorX = bestX; cursorY = bestY;
            lookCell = bestLook;
        }

        void ConsiderConnect(Simulation sim, Cell c, ref Command best, ref double bestScore, ref int bestX, ref int bestY, ref double bestTravel, ref int bestExtra, ref int bestLook)
        {
            bool full = c.Energy >= c.MaxEnergy * 9 / 10;
            double toSrc = Dist(cursorX, cursorY, c.PX, c.PY);
            if (sim.ActiveCount(c.Id) >= Balance.MaxTentacles[c.Grade])
            {
                // 満タン同士のループで枠が埋まっていたら1本引いて攻撃に回す
                if (!full) return;
                foreach (var t in sim.Tentacles)
                {
                    if (t.Dead || t.Src != c.Id || t.State != TentacleState.Attached) continue;
                    var d = sim.Cells[t.Dst];
                    if (d.Owner == Player && d.Energy >= d.MaxEnergy * 9 / 10 && !IsUnderAttack(sim, d) && rng.NextDouble() < 0.5)
                    {
                        int px, py; PointOn(sim, t, 1, out px, out py);
                        Consider(40, Command.Retract(Player, t.Id), px, py, Dist(cursorX, cursorY, px, py), gaze + swipe, -1,
                            ref best, ref bestScore, ref bestX, ref bestY, ref bestTravel, ref bestExtra, ref bestLook);
                        return;
                    }
                }
                return;
            }
            if (c.Energy < 8) return;

            foreach (var d in sim.Cells)
            {
                if (d.Id == c.Id) continue;
                if (sim.CanConnect(Player, c.Id, d.Id) != null) continue;
                int nodes = sim.PathNodes(c, d);
                if (c.Energy - nodes < 4) continue;
                double dist = nodes;
                double score;
                if (d.Owner == Player)
                {
                    // 自分の細胞同士は片方向だけ（逆向きにつなぐと相手側の触手が戻ってしまう）
                    if (sim.FindActive(d.Id, c.Id) != null) continue;
                    // 輪：つなぎ先がさらに別の味方へ送っていれば、流れが巡るので価値が高い
                    bool chain = false;
                    foreach (var t in sim.Tentacles)
                        if (!t.Dead && t.Src == d.Id && t.State == TentacleState.Attached && sim.Cells[t.Dst].Owner == Player) chain = true;
                    bool underAttack = IsUnderAttack(sim, d);
                    score = (chain ? 45 : 25) + (underAttack ? 40 : 0) - dist;
                    if (d.Energy >= d.MaxEnergy) score -= 100;
                }
                else
                {
                    int need = Need(d);
                    score = (d.Owner < 0 ? 70 : 80) - need * (full ? 0.25 : 0.8) - dist;
                    if (!full && need > c.Energy * 3 + 30) score -= 60;
                    if (full) score += 30;
                }
                score += rng.NextDouble() * 8;
                // カーソルを元の細胞へ動かす距離 ＋ つなぎ先までドラッグする距離
                double travel = toSrc + Dist(c.PX, c.PY, d.PX, d.PY);
                // 視線：元の細胞を見ていなければそこへ移し、つなぎ先へも移す
                int looks = (lookCell == c.Id ? 0 : gaze) + gaze;
                Consider(score, Command.Connect(Player, c.Id, d.Id), d.PX, d.PY, travel, looks, d.Id,
                    ref best, ref bestScore, ref bestX, ref bestY, ref bestTravel, ref bestExtra, ref bestLook);
            }
        }

        static void Consider(double score, Command cmd, int endX, int endY, double travel, int extra, int look,
            ref Command best, ref double bestScore, ref int bestX, ref int bestY, ref double bestTravel, ref int bestExtra, ref int bestLook)
        {
            if (score <= bestScore) return;
            bestScore = score; best = cmd; bestX = endX; bestY = endY; bestTravel = travel; bestExtra = extra; bestLook = look;
        }

        void PlaceCursorAtHome(Simulation sim)
        {
            foreach (var c in sim.Cells)
                if (c.Owner == Player) { cursorX = c.PX; cursorY = c.PY; break; }
            cursorKnown = true;
        }

        /// <summary>触手の根元から node 番目あたりの位置</summary>
        static void PointOn(Simulation sim, Tentacle t, int node, out int x, out int y)
        {
            var s = sim.Cells[t.Src]; var d = sim.Cells[t.Dst];
            int len = Math.Max(1, t.Length);
            node = Math.Max(0, Math.Min(len, node));
            x = s.PX + (d.PX - s.PX) * node / len;
            y = s.PY + (d.PY - s.PY) * node / len;
        }

        /// <summary>2点間の距離（盤面の単位）</summary>
        static double Dist(int ax, int ay, int bx, int by)
        {
            double dx = (ax - bx) / (double)Balance.Unit, dy = (ay - by) / (double)Balance.Unit;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        int Need(Cell d)
        {
            if (d.Owner == Player) return 0;
            if (d.Owner >= 0) return d.Energy;
            if (d.GaugeOwner == Player) return d.CaptureValue - d.Gauge;
            return d.Gauge + d.CaptureValue;
        }

        static bool IsUnderAttack(Simulation sim, Cell d)
        {
            foreach (var t in sim.Tentacles)
                if (!t.Dead && t.Dst == d.Id && t.Owner != d.Owner && t.State == TentacleState.Attached) return true;
            return false;
        }
    }

    /// <summary>COMの強さ。すべて整数（オンラインで送り、部屋主の端末で同じに動かすため）</summary>
    public struct ComProfile
    {
        /// <summary>次の手までの時間（判断。tick。実際は ±25% ばらつく）</summary>
        public int ReactTicks;
        /// <summary>目移りの時間（別の細胞・触手へ視線を移す。tick）</summary>
        public int GazeTicks;
        /// <summary>触手をつなぐ時間（カーソルを盤面の距離1だけ動かす時間。1/100秒）</summary>
        public int MoveCentis;

        /// <summary>考え方（0 = 標準のCOM、1 = 強化学習で鍛えたCOM）</summary>
        public int Brain;

        public const int ReactMin = 2, ReactMax = 100, GazeMin = 0, GazeMax = 40, MoveMin = 1, MoveMax = 50;
        public const int BrainStandard = 0, BrainLearned = 1;
        public static readonly string[] BrainNames = { "標準", "学習" };

        public ComProfile(int react, int gaze, int moveCentis, int brain = BrainStandard) { ReactTicks = react; GazeTicks = gaze; MoveCentis = moveCentis; Brain = brain; }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
        public ComProfile Clamped() { return new ComProfile(Clamp(ReactTicks, ReactMin, ReactMax), Clamp(GazeTicks, GazeMin, GazeMax), Clamp(MoveCentis, MoveMin, MoveMax), Clamp(Brain, BrainStandard, BrainLearned)); }
        public ComProfile WithBrain(int brain) { var p = this; p.Brain = brain; return p.Clamped(); }

        /// <summary>プリセットと同じならその番号、違えば -1（カスタム）</summary>
        public int PresetIndex()
        {
            for (int i = 0; i < SimpleAI.Presets.Length; i++)
            {
                var p = SimpleAI.Presets[i];
                if (p.ReactTicks == ReactTicks && p.GazeTicks == GazeTicks && p.MoveCentis == MoveCentis) return i;
            }
            return -1;
        }

        public string Name { get { int i = PresetIndex(); return (Brain == BrainLearned ? "学習・" : "") + (i >= 0 ? SimpleAI.LevelNames[i] : "カスタム"); } }
    }
}
