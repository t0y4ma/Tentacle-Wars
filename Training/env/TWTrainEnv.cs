// 強化学習用の対戦環境。たくさんの試合を並列に回し、学習するCOMの手が空くたびに局面を Python へ送る。
// 通信は標準入出力（バイナリ, リトルエンディアン）。
//   環境→Python: [int magic][int nDone]{int slot, float reward, int oppKind(0=SimpleAI,1=過去の自分)}*[int nDec][int G][int C]{int slot, int K, float shaping, float G[], float K*C[]}*
//   Python→環境: [int type]  type=1: [int n]{int choice}*   type=2: [int len][utf8 対戦相手の重みファイル（改行区切り）]   type=9: 終了
// 評価: TWTrainEnv --eval policy.txt [--games 160] … 学習したCOM（つよい）と、それより速い SimpleAI を、基本マップと固定のランダムマップで戦わせる
// マップ: 線対称のランダムマップを5種類作り、--map-every 試合ごとに作り直す（--builtin-pct の割合で基本マップも混ぜる）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TW.Sim;

public static class TWTrainEnv
{
    const int Magic = 0x54574231;

    class Slot
    {
        public int Id;
        public Simulation Sim;
        public LearnedAI Agent;
        public SimpleAI OppSimple;
        public LearnedAI OppNet;
        public int AgentSeat;
        public bool MidTick;           // 相手は考え終わり、学習COMの選択待ち
        public bool Waiting;           // Python の選択待ち
        public float LastShare;
        public float Shaping;
        public Random Rng;
    }

    static readonly object poolLock = new object();
    static List<PolicyNet> pool = new List<PolicyNet>();
    static double pSimple = 1.0;

    // 学習中に使うランダムマップ（一定試合数ごとに作り直す）
    const int MapSetSize = 5;
    static MapDef[] mapSet;
    static int mapGen;
    static double builtinFrac = 0.4;
    static readonly object mapLock = new object();

    /// <summary>評価で使う相手（つよいより速い）</summary>
    public static readonly ComProfile EvalOpp = new ComProfile(6, 2, 3);
    /// <summary>学習中の相手の強さの範囲（弱い側はよわいとふつうの間まで上げた）</summary>
    static readonly ComProfile OppWeak = new ComProfile(20, 5, 10), OppStrong = new ComProfile(6, 2, 3);

    public static int Main(string[] args)
    {
        int games = Arg(args, "--games", 128), seed = Arg(args, "--seed", 1);
        string eval = ArgS(args, "--eval", null);
        if (eval != null) return Eval(eval, games, seed, ArgS(args, "--vs", null));

        var stdout = new BinaryWriter(new BufferedStream(Console.OpenStandardOutput(), 1 << 20));
        var stdin = new BinaryReader(new BufferedStream(Console.OpenStandardInput(), 1 << 20));
        var master = new Random(seed);
        int mapEvery = Arg(args, "--map-every", 1500), sinceMaps = 0;
        builtinFrac = Arg(args, "--builtin-pct", 40) / 100.0;
        var mapRng = new Random(master.Next());
        RegenMaps(mapRng);
        var slots = new Slot[games];
        for (int i = 0; i < games; i++) { slots[i] = new Slot { Id = i, Rng = new Random(master.Next()) }; NewGame(slots[i]); }

        var done = new List<KeyValuePair<int, float>>();
        while (true)
        {
            // 選択待ちでない試合を、次の判断まで（または終局まで）進める
            done.Clear();
            var doneLocal = new KeyValuePair<int, float>?[games];
            var doneKind = new int[games];
            Parallel.For(0, games, i =>
            {
                var s = slots[i];
                if (s.Waiting) return;
                float? r = Advance(s);
                if (r.HasValue) { doneLocal[i] = new KeyValuePair<int, float>(i, r.Value); doneKind[i] = s.OppNet != null ? 1 : 0; NewGame(s); Advance(s); }
            });
            var kinds = new List<int>();
            foreach (var d in doneLocal) if (d.HasValue) { done.Add(d.Value); kinds.Add(doneKind[d.Value.Key]); }
            sinceMaps += done.Count;
            if (mapEvery > 0 && sinceMaps >= mapEvery) { sinceMaps = 0; RegenMaps(mapRng); }

            stdout.Write(Magic);
            stdout.Write(done.Count);
            for (int k = 0; k < done.Count; k++) { stdout.Write(done[k].Key); stdout.Write(done[k].Value); stdout.Write(kinds[k]); }
            var dec = slots.Where(s => s.Waiting).ToList();
            stdout.Write(dec.Count);
            stdout.Write(PolicyFeatures.GlobalSize);
            stdout.Write(PolicyFeatures.CandSize);
            foreach (var s in dec)
            {
                var a = s.Agent;
                stdout.Write(s.Id);
                stdout.Write(a.Cands.Count);
                stdout.Write(s.Shaping); s.Shaping = 0;
                for (int k = 0; k < PolicyFeatures.GlobalSize; k++) stdout.Write(a.Global[k]);
                int n = a.Cands.Count * PolicyFeatures.CandSize;
                for (int k = 0; k < n; k++) stdout.Write(a.CandFeatures[k]);
            }
            stdout.Flush();

            // Python からの指示
            while (true)
            {
                int type;
                try { type = stdin.ReadInt32(); } catch (EndOfStreamException) { return 0; }
                if (type == 9) return 0;
                if (type == 2)
                {
                    int len = stdin.ReadInt32();
                    string text = Encoding.UTF8.GetString(stdin.ReadBytes(len));
                    var nets = new List<PolicyNet>();
                    foreach (var path in text.Split('\n').Where(p => p.Trim().Length > 0))
                    {
                        try { nets.Add(PolicyNet.Parse(File.ReadAllText(path.Trim()))); } catch (Exception e) { Console.Error.WriteLine("pool load failed: " + path + " " + e.Message); }
                    }
                    lock (poolLock) { pool = nets; pSimple = nets.Count == 0 ? 1.0 : 0.4; }
                    continue;
                }
                if (type == 1)
                {
                    int n = stdin.ReadInt32();
                    if (n != dec.Count) throw new InvalidDataException("choices " + n + " != " + dec.Count);
                    for (int k = 0; k < n; k++)
                    {
                        int c = stdin.ReadInt32();
                        var s = dec[k];
                        s.Agent.Act(s.Sim, c);
                        s.Waiting = false;
                    }
                    break;
                }
                throw new InvalidDataException("unknown type " + type);
            }
        }
    }

    /// <summary>判断が必要になるか終局するまで進める。終局したら学習COMから見た報酬を返す</summary>
    static float? Advance(Slot s)
    {
        var sim = s.Sim;
        int guard = 0;
        while (!sim.Over && guard++ < 100000)
        {
            if (!s.MidTick)
            {
                if (s.OppSimple != null) s.OppSimple.Think(sim); else s.OppNet.Think(sim);
                s.MidTick = true;
                if (s.Agent.NeedsDecision(sim))
                {
                    s.Agent.Observe(sim);
                    float share = Share(s);
                    s.Shaping += share - s.LastShare; s.LastShare = share;
                    s.Waiting = true;
                    return null;
                }
            }
            sim.Step();
            s.MidTick = false;
        }
        if (sim.Winner == s.AgentSeat) return 1f;
        if (sim.Winner < 0) return 0f;
        return -1f;
    }

    static float Share(Slot s)
    {
        float me = s.Sim.Score(s.AgentSeat), all = 0;
        for (int p = 0; p < s.Sim.PlayerCount; p++) all += s.Sim.Score(p);
        return all > 0 ? me / all : 0.5f;
    }

    static readonly MapDef[] builtIn2 = Maps.BuiltIn.Where(m => m.Players == 2).ToArray();

    static void NewGame(Slot s)
    {
        var rng = s.Rng;
        MapDef map;
        if (rng.NextDouble() < builtinFrac) map = builtIn2[rng.Next(builtIn2.Length)];
        else lock (mapLock) map = mapSet[rng.Next(mapSet.Length)];
        s.Sim = new Simulation(map);
        s.AgentSeat = rng.Next(2);
        int opp = 1 - s.AgentSeat;
        s.Agent = new LearnedAI(s.AgentSeat, null, rng.Next(1, int.MaxValue)).SetProfile(Lerp(SimpleAI.Presets[0], OppStrong, rng.NextDouble()));
        s.OppSimple = null; s.OppNet = null;
        PolicyNet oppNet = null;
        lock (poolLock) { if (pool.Count > 0 && rng.NextDouble() >= pSimple) oppNet = pool[rng.Next(pool.Count)]; }
        if (oppNet != null) s.OppNet = new LearnedAI(opp, oppNet, rng.Next(1, int.MaxValue)).SetProfile(Lerp(OppWeak, OppStrong, rng.NextDouble()));
        else s.OppSimple = new SimpleAI(opp, rng.Next(1, int.MaxValue)).SetProfile(Lerp(OppWeak, OppStrong, rng.NextDouble()));
        s.MidTick = false; s.Waiting = false; s.Shaping = 0; s.LastShare = 0.5f;
    }

    /// <summary>2つの強さの間でばらつかせる（学習COM自身は よわい〜つよいより速い まで、どの設定でも上手く動くように）</summary>
    static ComProfile Lerp(ComProfile a, ComProfile b, double t)
    {
        return new ComProfile((int)Math.Round(a.ReactTicks + (b.ReactTicks - a.ReactTicks) * t),
                              (int)Math.Round(a.GazeTicks + (b.GazeTicks - a.GazeTicks) * t),
                              (int)Math.Round(a.MoveCentis + (b.MoveCentis - a.MoveCentis) * t));
    }

    static void RegenMaps(Random rng)
    {
        var set = new MapDef[MapSetSize];
        for (int i = 0; i < MapSetSize; i++) set[i] = RandomMap(rng);
        lock (mapLock) { mapSet = set; mapGen++; }
        Console.Error.WriteLine("[maps] 第" + mapGen + "世代のランダムマップを" + MapSetSize + "種類作りました（細胞 " + string.Join("/", set.Select(m => m.Cells.Count)) + "）");
    }

    /// <summary>線対称のランダムな2人用マップ（左右対称か上下対称）</summary>
    public static MapDef RandomMap(Random rng)
    {
        float hw = MapDef.FieldHalfWidth - MapDef.CellMargin, hh = MapDef.FieldHalfHeight - MapDef.CellMargin;
        for (int attempt = 0; attempt < 500; attempt++)
        {
            bool vertical = rng.NextDouble() < 0.6;   // true: 縦の線で左右対称（左右に分かれる） / false: 横の線で上下対称
            var m = new MapDef { Id = "random", Name = "random", Players = 2 };
            Func<float, float, (float, float)> mirror = (x, y) => vertical ? (-x, y) : (x, -y);
            // プレイヤーの細胞：対称の線から十分離す
            float px, py;
            if (vertical) { px = -hw + (float)rng.NextDouble() * 3f; py = (float)(rng.NextDouble() * 2 - 1) * hh; }
            else { py = -hh + (float)rng.NextDouble() * 1.5f; px = (float)(rng.NextDouble() * 2 - 1) * hw; }
            int pe = 15 + rng.Next(3) * 5;
            var (qx, qy) = mirror(px, py);
            m.Cells.Add(new CellDef(px, py, 0, pe)); m.Cells.Add(new CellDef(qx, qy, 1, pe));
            int pairs = 2 + rng.Next(5);
            for (int k = 0; k < pairs; k++)
            {
                float x = (float)(rng.NextDouble() * 2 - 1) * hw, y = (float)(rng.NextDouble() * 2 - 1) * hh;
                int e = 10 + rng.Next(8) * 5;
                var (mx, my) = mirror(x, y);
                m.Cells.Add(new CellDef(x, y, -1, e)); m.Cells.Add(new CellDef(mx, my, -1, e));
            }
            // 対称の線の上に置く中立細胞（0〜2個）
            int onAxis = rng.Next(3);
            for (int k = 0; k < onAxis; k++)
            {
                float t = (float)(rng.NextDouble() * 2 - 1);
                int e = 20 + rng.Next(7) * 10;
                m.Cells.Add(vertical ? new CellDef(0, t * hh, -1, e) : new CellDef(t * hw, 0, -1, e));
            }
            int walls = rng.Next(3);
            for (int k = 0; k < walls; k++)
            {
                float x = (float)(rng.NextDouble() * 2 - 1) * (hw - 1), y = (float)(rng.NextDouble() * 2 - 1) * (hh - 1);
                float r = 0.4f + (float)rng.NextDouble() * 0.8f;
                if (rng.NextDouble() < 0.3) { if (vertical) x = 0; else y = 0; }   // 線の上の壁
                m.Obstacles.Add(new ObstacleDef(x, y, r));
                var (mx, my) = mirror(x, y);
                if (Math.Abs(mx - x) > 0.05f || Math.Abs(my - y) > 0.05f) m.Obstacles.Add(new ObstacleDef(mx, my, r));
            }
            for (int i = 0; i < m.Cells.Count; i++) { m.Cells[i].X = (float)Math.Round(m.Cells[i].X, 2); m.Cells[i].Y = (float)Math.Round(m.Cells[i].Y, 2); }
            if (MapValidator.Validate(m).Count == 0) return m;
        }
        return builtIn2[rng.Next(builtIn2.Length)];
    }

    /// <summary>評価。主な指標は、固定の乱数で作ったランダムマップ（games/2 枚）の各マップを両方の席で1回ずつ戦った合計勝率。
    /// 相手は速い SimpleAI（--vs のときはその重み）、自分は つよい。基本マップの成績は参考として別に出す。</summary>
    static int Eval(string policyPath, int games, int seed, string vsPath)
    {
        var net = PolicyNet.Parse(File.ReadAllText(policyPath));
        PolicyNet vs = vsPath != null ? PolicyNet.Parse(File.ReadAllText(vsPath)) : null;
        var strong = SimpleAI.Presets[2];
        var theirs = vs != null ? strong : EvalOpp;
        int nMaps = Math.Max(1, games / 2);
        var fixedRng = new Random(20261014);   // 評価用のランダムマップは毎回同じ（時間を追って比べられるように）
        var rand = new MapDef[nMaps];
        for (int i = 0; i < nMaps; i++) rand[i] = RandomMap(fixedRng);
        var r = Play(net, vs, rand, nMaps * 2, seed, strong, theirs);
        var baseMaps = Maps.BuiltIn.Where(m => m.Players == 2).ToArray();
        var b = Play(net, vs, baseMaps, baseMaps.Length * 20, seed + 7, strong, theirs);
        Func<int[], int, double> rate = (w, n) => (w[0] + (vs != null ? 0.5 * w[2] : 0)) * 100.0 / n;   // 重み同士は引き分けを0.5勝
        int mapsAllWin = 0, mapsAllLose = 0;
        for (int i = 0; i < nMaps; i++) { if (r.Item2[i, 0] == 2) mapsAllWin++; else if (r.Item2[i, 1] == 2) mapsAllLose++; }
        Console.WriteLine("builtin: " + rate(b.Item1, b.Item3).ToString("0.0") + "%");
        for (int i = 0; i < baseMaps.Length; i++) Console.WriteLine("  " + baseMaps[i].Id + ": " + b.Item2[i, 0] + "-" + b.Item2[i, 1]);
        Console.WriteLine("random maps " + nMaps + " (両席とも勝ち " + mapsAllWin + " / 両席とも負け " + mapsAllLose + ")");
        var w0 = r.Item1;
        Console.WriteLine("vs " + (vs != null ? "net" : "SimpleAI(" + EvalOpp.ReactTicks + "," + EvalOpp.GazeTicks + "," + EvalOpp.MoveCentis + ")") +
                          ": win " + w0[0] + " lose " + w0[1] + " draw " + w0[2] + "  rate " + rate(w0, r.Item3).ToString("0.0") + "%");
        return 0;
    }

    static Tuple<int[], int[,], int> Play(PolicyNet net, PolicyNet vs, MapDef[] maps, int games, int seed, ComProfile mine, ComProfile theirs)
    {
        int[] wins = new int[3];   // 勝ち・負け・引き分け
        var perMap = new int[maps.Length, 2];
        Parallel.For(0, games, g =>
        {
            var rng = new Random(seed * 100003 + g);
            int mi = g % maps.Length;
            var sim = new Simulation(maps[mi]);
            int seat = (g / maps.Length) % 2, opp = 1 - seat;
            var agent = new LearnedAI(seat, net, rng.Next(1, int.MaxValue)).SetProfile(mine);
            LearnedAI oppNet = vs != null ? new LearnedAI(opp, vs, rng.Next(1, int.MaxValue)).SetProfile(theirs) : null;
            var oppAi = vs == null ? new SimpleAI(opp, rng.Next(1, int.MaxValue)).SetProfile(theirs) : null;
            while (!sim.Over) { if (oppAi != null) oppAi.Think(sim); else oppNet.Think(sim); agent.Think(sim); sim.Step(); }
            int r = sim.Winner == seat ? 0 : sim.Winner < 0 ? 2 : 1;
            lock (wins) { wins[r]++; if (r == 0) perMap[mi, 0]++; else perMap[mi, 1]++; }
        });
        return Tuple.Create(wins, perMap, games);
    }

    static int Arg(string[] a, string k, int d) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? int.Parse(a[i + 1]) : d; }
    static string ArgS(string[] a, string k, string d) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : d; }
}
