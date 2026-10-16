// 学習したCOM（強化学習）。候補となる操作を並べて特徴量にし、小さなニューラルネットで点数を付けて選ぶ。
// 学習用プログラム（Training/TWTrainEnv）と Unity のゲーム本体の両方で同じコードを使う。
// 操作の速さ（次の手までの時間・目移り・触手をつなぐ時間）は SimpleAI と同じ「人間の手」の制約に従う。
using System;
using System.Collections.Generic;
using System.Globalization;

namespace TW.Sim
{
    public enum PolicyActionType { Connect, Retract, Cut, Wait }

    /// <summary>選べる操作1つ</summary>
    public struct PolicyAction
    {
        public PolicyActionType Type;
        public int Src, Dst;        // Connect: 発射元・接続先 / Retract・Cut: 触手の根元・先
        public int Tentacle;        // Retract・Cut: 触手ID
        public int Split;           // Cut: 切れ目
        public int EndX, EndY;      // 操作の終わりのカーソル位置
        public double Travel;       // カーソルが動く距離（盤面の単位）
        public int ExtraTicks;      // 目移り・なぞる動作
        public int LookCell;        // 操作の終わりに見ている細胞

        public Command ToCommand(int player)
        {
            switch (Type)
            {
                case PolicyActionType.Connect: return Command.Connect(player, Src, Dst);
                case PolicyActionType.Retract: return Command.Retract(player, Tentacle);
                case PolicyActionType.Cut: return Command.Cut(player, Tentacle, Split);
            }
            return default(Command);
        }
    }

    /// <summary>候補の列挙と特徴量</summary>
    public static class PolicyFeatures
    {
        public const int GlobalSize = 16;
        public const int CandSize = 48;
        /// <summary>初版の重みファイル（twpolicy 1）の大きさ。読み込むときは増えた入力の重みを0で埋める</summary>
        public const int GlobalSizeV1 = 12, CandSizeV1 = 32;
        public const int MaxCandidates = 96;
        /// <summary>「何もしない」を選んだときに待つ時間（tick）</summary>
        public const int WaitTicks = 6;

        /// <summary>手の状態（カーソル位置・見ている細胞・強さ）</summary>
        public struct Hand
        {
            public int CursorX, CursorY, LookCell;
            public ComProfile Profile;
            public int Gaze { get { return Profile.GazeTicks; } }
            public int Swipe { get { return 3 + Profile.GazeTicks / 2; } }
            public double TicksPerUnit { get { return Profile.MoveCentis * Balance.TicksPerSecond / 100.0; } }
        }

        static double Dist(int ax, int ay, int bx, int by)
        {
            double dx = (ax - bx) / (double)Balance.Unit, dy = (ay - by) / (double)Balance.Unit;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        static void PointOn(Simulation sim, Tentacle t, int node, out int x, out int y)
        {
            var s = sim.Cells[t.Src]; var d = sim.Cells[t.Dst];
            int len = Math.Max(1, t.Length);
            node = Math.Max(0, Math.Min(len, node));
            x = s.PX + (d.PX - s.PX) * node / len;
            y = s.PY + (d.PY - s.PY) * node / len;
        }

        public static int Need(Cell d, int player)
        {
            if (d.Owner == player) return 0;
            if (d.Owner >= 0) return d.Energy;
            if (d.GaugeOwner == player) return d.CaptureValue - d.Gauge;
            return d.Gauge + d.CaptureValue;
        }

        static bool UnderAttack(Simulation sim, Cell d)
        {
            foreach (var t in sim.Tentacles)
                if (!t.Dead && t.Dst == d.Id && t.Owner != d.Owner && t.State == TentacleState.Attached) return true;
            return false;
        }

        /// <summary>候補の操作を並べる（先頭は必ず「何もしない」）</summary>
        public static List<PolicyAction> Candidates(Simulation sim, int player, Hand hand, List<PolicyAction> into = null)
        {
            var list = into ?? new List<PolicyAction>();
            list.Clear();
            list.Add(new PolicyAction { Type = PolicyActionType.Wait, EndX = hand.CursorX, EndY = hand.CursorY, LookCell = hand.LookCell });

            // 引く・切る（自分の触手）
            foreach (var t in sim.Tentacles)
            {
                if (t.Dead || t.Owner != player || !t.IsActive) continue;
                if (list.Count >= MaxCandidates) break;
                int px, py; PointOn(sim, t, 1, out px, out py);
                list.Add(new PolicyAction
                {
                    Type = PolicyActionType.Retract, Src = t.Src, Dst = t.Dst, Tentacle = t.Id,
                    EndX = px, EndY = py, Travel = Dist(hand.CursorX, hand.CursorY, px, py), ExtraTicks = hand.Gaze + hand.Swipe, LookCell = -1
                });
                // 相手（敵・中立）の残りを一気に削り切れるなら、その量だけ流す切断
                var d = sim.Cells[t.Dst];
                if (t.State == TentacleState.Attached && d.Owner != player)
                {
                    int give = Need(d, player) + 2;
                    if (give > 0 && give <= t.Ext && list.Count < MaxCandidates)
                    {
                        int split = t.Ext - give;
                        PointOn(sim, t, split, out px, out py);
                        list.Add(new PolicyAction
                        {
                            Type = PolicyActionType.Cut, Src = t.Src, Dst = t.Dst, Tentacle = t.Id, Split = split,
                            EndX = px, EndY = py, Travel = Dist(hand.CursorX, hand.CursorY, px, py), ExtraTicks = hand.Gaze + hand.Swipe, LookCell = -1
                        });
                    }
                }
            }

            // つなぐ
            foreach (var c in sim.Cells)
            {
                if (c.Owner != player) continue;
                if (sim.ActiveCount(c.Id) >= Balance.MaxTentacles[c.Grade]) continue;
                double toSrc = Dist(hand.CursorX, hand.CursorY, c.PX, c.PY);
                foreach (var d in sim.Cells)
                {
                    if (list.Count >= MaxCandidates) break;
                    if (d.Id == c.Id) continue;
                    if (sim.CanConnect(player, c.Id, d.Id) != null) continue;
                    if (c.Energy - sim.PathNodes(c, d) < 2) continue;   // 伸ばし切れない
                    if (d.Owner == player && sim.FindActive(d.Id, c.Id) != null) continue;   // 味方同士の逆向きは元の触手が戻るだけ
                    list.Add(new PolicyAction
                    {
                        Type = PolicyActionType.Connect, Src = c.Id, Dst = d.Id,
                        EndX = d.PX, EndY = d.PY, Travel = toSrc + Dist(c.PX, c.PY, d.PX, d.PY),
                        ExtraTicks = (hand.LookCell == c.Id ? 0 : hand.Gaze) + hand.Gaze, LookCell = d.Id
                    });
                }
            }
            return list;
        }

        /// <summary>盤面全体の特徴量（自分から見て）</summary>
        public static void Global(Simulation sim, int player, float[] g)
        {
            Array.Clear(g, 0, GlobalSize);
            int n = sim.Cells.Count;
            float myE = 0, opE = 0, myC = 0, opC = 0, neC = 0, myT = 0, opT = 0, myG = 0, opG = 0;
            foreach (var c in sim.Cells)
            {
                if (c.Owner == player) { myE += c.Energy; myC++; myG += c.Grade; }
                else if (c.Owner >= 0) { opE += c.Energy; opC++; opG += c.Grade; }
                else neC++;
            }
            foreach (var t in sim.Tentacles)
            {
                if (t.Dead || !t.IsActive) continue;
                if (t.Owner == player) myT++; else opT++;
            }
            g[0] = myE / 400f; g[1] = opE / 400f;
            g[2] = myC / n; g[3] = opC / n; g[4] = neC / n;
            g[5] = myT / 10f; g[6] = opT / 10f;
            g[7] = Math.Min(1f, sim.Tick / (float)Math.Max(1, Math.Min(sim.TimeLimitTicks, Balance.TimeLimitTicks)));
            g[8] = myC > 0 ? myG / myC / 5f : 0; g[9] = opC > 0 ? opG / opC / 5f : 0;
            g[10] = n / 30f; g[11] = sim.PlayerCount / 4f;
            // 2版で追加：力関係と、攻め合いの様子
            g[12] = myE + opE > 0 ? myE / (myE + opE) : 0.5f;
            g[13] = myC + opC > 0 ? myC / (myC + opC) : 0.5f;
            int attacked = 0, attacking = 0;
            foreach (var t in sim.Tentacles)
            {
                if (t.Dead || t.State != TentacleState.Attached) continue;
                var d = sim.Cells[t.Dst];
                if (t.Owner != player && d.Owner == player) attacked++;
                if (t.Owner == player && d.Owner >= 0 && d.Owner != player) attacking++;
            }
            g[14] = Math.Min(1f, attacked / 10f);
            g[15] = Math.Min(1f, attacking / 10f);
        }

        static double NearestEnemy(Simulation sim, Cell c, int player)
        {
            double best = 20;
            foreach (var e in sim.Cells)
                if (e.Owner >= 0 && e.Owner != player) best = Math.Min(best, Dist(c.PX, c.PY, e.PX, e.PY));
            return best;
        }

        static float EnergyNear(Simulation sim, Cell c, int exclude, bool mine, int player)
        {
            float sum = 0;
            foreach (var e in sim.Cells)
            {
                if (e.Id == exclude || e.Id == c.Id || e.Owner < 0) continue;
                if ((e.Owner == player) != mine) continue;
                if (Dist(c.PX, c.PY, e.PX, e.PY) <= 5) sum += e.Energy;
            }
            return sum;
        }

        /// <summary>候補1つの特徴量</summary>
        public static void Candidate(Simulation sim, int player, Hand hand, PolicyAction a, float[] f, int offset)
        {
            Array.Clear(f, offset, CandSize);
            f[offset + (int)a.Type] = 1;
            f[offset + 31] = 1;
            if (a.Type == PolicyActionType.Wait) return;
            var s = sim.Cells[a.Src]; var d = sim.Cells[a.Dst];
            f[offset + 4] = s.Energy / 200f;
            f[offset + 5] = s.Grade / 5f;
            f[offset + 6] = sim.ActiveCount(s.Id) / (float)Balance.MaxTentacles[s.Grade];
            f[offset + 7] = UnderAttack(sim, s) ? 1 : 0;
            int feeds = 0, clash = 0, reverse = 0, chain = 0;
            foreach (var t in sim.Tentacles)
            {
                if (t.Dead || !t.IsActive) continue;
                if (t.Dst == s.Id && t.Owner == player) feeds++;
                if (t.Src == d.Id && t.Dst == s.Id) { reverse = 1; if (t.Owner != player) clash = 1; }
                if (t.Src == d.Id && t.Owner == player && sim.Cells[t.Dst].Owner == player && t.Dst != s.Id) chain = 1;
            }
            f[offset + 8] = Math.Min(1f, feeds / 3f);
            f[offset + 9] = d.Owner == player ? 1 : 0;
            f[offset + 10] = d.Owner >= 0 && d.Owner != player ? 1 : 0;
            f[offset + 11] = d.Owner < 0 ? 1 : 0;
            f[offset + 12] = d.Energy / 200f;
            f[offset + 13] = d.Grade / 5f;
            f[offset + 14] = Need(d, player) / 100f;
            f[offset + 15] = UnderAttack(sim, d) ? 1 : 0;
            f[offset + 16] = d.Owner >= 0 ? sim.ActiveCount(d.Id) / (float)Balance.MaxTentacles[d.Grade] : 0;
            f[offset + 17] = reverse;
            f[offset + 18] = clash;
            int nodes = sim.PathNodes(s, d);
            f[offset + 19] = nodes / 30f;
            f[offset + 20] = (s.Energy - nodes) / 100f;
            f[offset + 21] = (float)((a.Travel * hand.TicksPerUnit + a.ExtraTicks) / 100.0);
            f[offset + 22] = d.Energy >= d.MaxEnergy ? 1 : 0;
            f[offset + 23] = chain;
            if (a.Type != PolicyActionType.Connect)
            {
                var t = sim.FindTentacle(a.Tentacle);
                if (t != null)
                {
                    f[offset + 25] = t.Length / 30f;
                    f[offset + 26] = t.Length > 0 ? t.Ext / (float)t.Length : 0;
                    f[offset + 27] = Math.Min(1f, t.Packets.Count / 10f);
                    f[offset + 30] = t.State == TentacleState.Attached ? 1 : 0;
                }
                if (a.Type == PolicyActionType.Cut && t != null) f[offset + 29] = (t.Ext - a.Split) / 100f;
            }
            f[offset + 28] = d.Owner >= 0 && d.Owner != player ? 1 : 0;

            // 2版で追加：攻めが通るかどうかの判断材料
            int focus = 0, raceIn = 0, reinforce = 0, onSrc = 0, inflight = 0;
            foreach (var t in sim.Tentacles)
            {
                if (t.Dead || !t.IsActive) continue;
                if (t.Dst == d.Id)
                {
                    if (t.Owner == player) { if (t.Src != s.Id) focus++; foreach (var pk in t.Packets) inflight += pk.Amount; }
                    else if (t.Owner == d.Owner) reinforce++;
                    else raceIn++;
                }
                if (t.Dst == s.Id && t.Owner != player && t.State == TentacleState.Attached) onSrc++;
            }
            f[offset + 32] = Math.Min(1f, focus / 3f);
            f[offset + 33] = Math.Min(1f, raceIn / 3f);
            f[offset + 34] = Math.Min(1f, onSrc / 3f);
            f[offset + 35] = Math.Min(1f, reinforce / 3f);
            f[offset + 36] = (float)(NearestEnemy(sim, d, player) / 20.0);
            f[offset + 37] = (float)(NearestEnemy(sim, s, player) / 20.0);
            f[offset + 38] = Math.Max(-1f, Math.Min(1f, (s.Energy - nodes - Need(d, player)) / 100f));
            f[offset + 39] = Math.Min(1f, inflight / 100f);
            f[offset + 40] = d.Owner < 0 && d.GaugeOwner >= 0 && d.GaugeOwner != player && d.CaptureValue > 0 ? d.Gauge / (float)d.CaptureValue : 0;
            f[offset + 41] = d.Owner < 0 ? d.CaptureValue / 100f : 0;
            f[offset + 42] = s.Energy >= s.MaxEnergy * 9 / 10 ? 1 : 0;
            f[offset + 43] = d.MaxEnergy / 200f;
            f[offset + 44] = EnergyNear(sim, d, s.Id, false, player) / 200f;
            f[offset + 45] = EnergyNear(sim, d, s.Id, true, player) / 200f;
            f[offset + 46] = (float)(Dist(s.PX, s.PY, d.PX, d.PY) / 20.0);
        }
    }

    /// <summary>候補ごとの点数を付ける小さなネット（2層の全結合）。重みは学習用プログラムが書き出したテキストから読む</summary>
    public class PolicyNet
    {
        public int Hidden;
        [ThreadStatic] static float[] buf1, buf2;
        float[] w1, b1, w2, b2, w3; float b3;   // 入力→隠れ→隠れ→点数

        public static PolicyNet Parse(string text)
        {
            // 書式: 1行目 "twpolicy 2 hidden G C"（初版は "twpolicy 1 hidden" で G=12, C=32）、以降 w1 b1 w2 b2 w3 b3 をそれぞれ1行に空白区切り
            var lines = text.Replace("\r", "").Split('\n');
            var head = lines[0].Trim().Split(' ');
            if (head.Length < 3 || head[0] != "twpolicy") throw new FormatException("twpolicy ではありません");
            var net = new PolicyNet { Hidden = int.Parse(head[2], CultureInfo.InvariantCulture) };
            int fileG = head.Length >= 5 ? int.Parse(head[3], CultureInfo.InvariantCulture) : PolicyFeatures.GlobalSizeV1;
            int fileC = head.Length >= 5 ? int.Parse(head[4], CultureInfo.InvariantCulture) : PolicyFeatures.CandSizeV1;
            Func<int, float[]> row = i =>
            {
                var parts = lines[i].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var r = new float[parts.Length];
                for (int k = 0; k < parts.Length; k++) r[k] = float.Parse(parts[k], CultureInfo.InvariantCulture);
                return r;
            };
            net.w1 = row(1); net.b1 = row(2); net.w2 = row(3); net.b2 = row(4); net.w3 = row(5); net.b3 = row(6)[0];
            int inSize = PolicyFeatures.GlobalSize + PolicyFeatures.CandSize;
            if (fileG > PolicyFeatures.GlobalSize || fileC > PolicyFeatures.CandSize) throw new FormatException("重みの入力が多すぎます");
            if ((fileG != PolicyFeatures.GlobalSize || fileC != PolicyFeatures.CandSize) && net.w1.Length == (fileG + fileC) * net.Hidden)
            {
                // 古い重み：増えた入力の重みを0で埋める（前と同じ点数になる）
                int oldIn = fileG + fileC; var w = new float[inSize * net.Hidden];
                for (int j = 0; j < net.Hidden; j++)
                {
                    for (int i = 0; i < fileG; i++) w[j * inSize + i] = net.w1[j * oldIn + i];
                    for (int i = 0; i < fileC; i++) w[j * inSize + PolicyFeatures.GlobalSize + i] = net.w1[j * oldIn + fileG + i];
                }
                net.w1 = w;
            }
            if (net.w1.Length != inSize * net.Hidden || net.w2.Length != net.Hidden * net.Hidden || net.w3.Length != net.Hidden)
                throw new FormatException("重みの大きさが合いません");
            return net;
        }

        /// <summary>候補 k の点数（大きいほど良い）</summary>
        public float Score(float[] g, float[] cand, int offset)
        {
            int H = Hidden, G = PolicyFeatures.GlobalSize, C = PolicyFeatures.CandSize, I = G + C;
            // 毎回の確保を避ける（学習用の環境は並列で呼ぶので、スレッドごとに持つ）
            if (buf1 == null || buf1.Length < H) { buf1 = new float[H]; buf2 = new float[H]; }
            var h1 = buf1; var h2 = buf2;
            for (int j = 0; j < H; j++)
            {
                float s = b1[j];
                int row = j * I;
                for (int i = 0; i < G; i++) s += w1[row + i] * g[i];
                for (int i = 0; i < C; i++) s += w1[row + G + i] * cand[offset + i];
                h1[j] = s > 0 ? s : 0;
            }
            for (int j = 0; j < H; j++)
            {
                float s = b2[j];
                int row = j * H;
                for (int i = 0; i < H; i++) s += w2[row + i] * h1[i];
                h2[j] = s > 0 ? s : 0;
            }
            float o = b3;
            for (int i = 0; i < H; i++) o += w3[i] * h2[i];
            return o;
        }
    }

    /// <summary>学習したCOM。SimpleAI と同じく毎tick Think を呼ぶ</summary>
    public class LearnedAI : IComPlayer
    {
        /// <summary>ゲーム本体で使う重み（起動時に読み込む）。null なら学習COMは標準のCOMで代わる</summary>
        public static PolicyNet SharedNet;

        public int Player { get; private set; }
        public Action<Command> Emit { get; set; }
        public ComProfile Profile { get { return hand.Profile; } }
        /// <summary>学習中は外から選ぶ（null ならネットで一番点数の高いものを選ぶ）</summary>
        public Func<LearnedAI, int> Chooser;
        public PolicyNet Net;

        readonly Random rng;
        PolicyFeatures.Hand hand;
        bool handKnown, hasPending;
        Command pending; bool pendingIsWait;
        int pendingAt;

        // 学習用プログラムが読む、いま判断している局面
        public readonly List<PolicyAction> Cands = new List<PolicyAction>();
        public readonly float[] Global = new float[PolicyFeatures.GlobalSize];
        public float[] CandFeatures = new float[PolicyFeatures.MaxCandidates * PolicyFeatures.CandSize];

        public LearnedAI(int player, PolicyNet net, int seed = 0)
        {
            Player = player; Net = net;
            rng = new Random(seed == 0 ? 4321 + player * 31 : seed);
            hand.Profile = SimpleAI.Presets[SimpleAI.DefaultLevel];
            hand.LookCell = -1;
        }

        public LearnedAI SetProfile(ComProfile p) { hand.Profile = p.Clamped(); return this; }
        public void ApplyProfile(ComProfile p) { SetProfile(p); }

        void Send(Simulation sim, Command c) { if (Emit != null) Emit(c); else sim.Enqueue(c); }

        /// <summary>手が空いていて、次の操作を決める番か</summary>
        public bool NeedsDecision(Simulation sim)
        {
            if (sim.Over) return false;
            if (!handKnown) PlaceHand(sim);
            if (hasPending && sim.Tick >= pendingAt)
            {
                hasPending = false;
                if (!pendingIsWait) Send(sim, pending);
            }
            return !hasPending;
        }

        /// <summary>局面を特徴量にする（NeedsDecision が true のとき）</summary>
        public void Observe(Simulation sim)
        {
            PolicyFeatures.Candidates(sim, Player, hand, Cands);
            PolicyFeatures.Global(sim, Player, Global);
            for (int k = 0; k < Cands.Count; k++) PolicyFeatures.Candidate(sim, Player, hand, Cands[k], CandFeatures, k * PolicyFeatures.CandSize);
        }

        /// <summary>候補 k を実行する（手が動き終わったら sim に積まれる）</summary>
        public void Act(Simulation sim, int k)
        {
            if (k < 0 || k >= Cands.Count) k = 0;
            var a = Cands[k];
            int think = hand.Profile.ReactTicks * 3 / 4 + rng.Next(hand.Profile.ReactTicks / 2 + 1);
            if (a.Type == PolicyActionType.Wait)
            {
                pendingIsWait = true;
                pendingAt = sim.Tick + PolicyFeatures.WaitTicks;
            }
            else
            {
                pendingIsWait = false;
                pending = a.ToCommand(Player);
                pendingAt = sim.Tick + think + (int)Math.Round(a.Travel * hand.TicksPerUnit) + a.ExtraTicks;
                hand.CursorX = a.EndX; hand.CursorY = a.EndY; hand.LookCell = a.LookCell;
            }
            hasPending = true;
        }

        /// <summary>毎tick呼ぶ（ゲーム本体用）</summary>
        public void Think(Simulation sim)
        {
            if (!NeedsDecision(sim)) return;
            Observe(sim);
            int k;
            if (Chooser != null) k = Chooser(this);
            else
            {
                k = 0; float best = float.NegativeInfinity;
                for (int i = 0; i < Cands.Count; i++)
                {
                    float s = Net.Score(Global, CandFeatures, i * PolicyFeatures.CandSize);
                    if (s > best) { best = s; k = i; }
                }
            }
            Act(sim, k);
        }

        void PlaceHand(Simulation sim)
        {
            foreach (var c in sim.Cells)
                if (c.Owner == Player) { hand.CursorX = c.PX; hand.CursorY = c.PY; hand.LookCell = c.Id; break; }
            handKnown = true;
        }
    }
}
