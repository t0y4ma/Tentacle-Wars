// 簡易COM（ローカル検証・切断時の代打ち用）
using System;
using System.Collections.Generic;

namespace TW.Sim
{
    public class SimpleAI
    {
        public readonly int Player;
        readonly Random rng;
        int timer;
        public int ThinkIntervalTicks = 10;
        /// <summary>設定すると、操作を sim に直接積まずにここへ渡す（オンラインで部屋主が代打ちするとき）</summary>
        public Action<Command> Emit;

        void Send(Simulation sim, Command c) { if (Emit != null) Emit(c); else sim.Enqueue(c); }

        public SimpleAI(int player, int seed = 0)
        {
            Player = player;
            rng = new Random(seed == 0 ? 1234 + player * 77 : seed);
            timer = 0;   // 全COMが同じtickに考える（考える順による有利不利をなくす）
        }

        /// <summary>毎tick呼ぶ。出したいコマンドを sim に積む。</summary>
        public void Think(Simulation sim)
        {
            if (sim.Over) return;
            if (++timer < ThinkIntervalTicks) return;
            timer = 0;

            foreach (var c in sim.Cells)
            {
                if (c.Owner != Player) continue;
                if (TryFinishByCut(sim, c)) continue;
                TryRetreat(sim, c);
                TryConnect(sim, c);
            }
        }

        // 敵・中立の残量を切断で一気に削り切れるなら切る
        bool TryFinishByCut(Simulation sim, Cell c)
        {
            foreach (var t in sim.Tentacles)
            {
                if (t.Dead || t.Src != c.Id || t.State != TentacleState.Attached) continue;
                var d = sim.Cells[t.Dst];
                int need = Need(d);
                if (need <= 0) continue;
                if (d.Owner == Player) continue;
                int give = need + 2;
                if (give > t.Ext) continue;   // 押し合い中は自分の先端までしか流せない
                if (rng.NextDouble() < 0.3) continue;   // 少し迷う
                Send(sim, Command.Cut(Player, t.Id, t.Ext - give));
                return true;
            }
            return false;
        }

        // 勝ち目のない攻撃は引く
        void TryRetreat(Simulation sim, Cell c)
        {
            foreach (var t in sim.Tentacles)
            {
                if (t.Dead || t.Src != c.Id || t.State != TentacleState.Attached) continue;
                var d = sim.Cells[t.Dst];
                if (d.Owner >= 0 && d.Owner != Player && d.Energy > c.Energy * 2 + 20 && c.Energy < 15)
                    Send(sim, Command.Retract(Player, t.Id));
            }
        }

        int Need(Cell d)
        {
            if (d.Owner == Player) return 0;
            if (d.Owner >= 0) return d.Energy;
            if (d.GaugeOwner == Player) return d.CaptureValue - d.Gauge + 1;
            return d.Gauge + d.CaptureValue + 1;
        }

        void TryConnect(Simulation sim, Cell c)
        {
            bool full = c.Energy >= c.MaxEnergy * 9 / 10;
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
                        Send(sim, Command.Retract(Player, t.Id));
                        return;
                    }
                }
                return;
            }
            if (c.Energy < 8) return;

            Cell best = null; double bestScore = 0;
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
                if (score > bestScore) { bestScore = score; best = d; }
            }
            if (best != null) Send(sim, Command.Connect(Player, c.Id, best.Id));
        }

        static bool IsUnderAttack(Simulation sim, Cell d)
        {
            foreach (var t in sim.Tentacles)
                if (!t.Dead && t.Dst == d.Id && t.Owner != d.Owner && t.State == TentacleState.Attached) return true;
            return false;
        }
    }
}
