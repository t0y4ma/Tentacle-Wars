# Tentacle Wars の COM を強化学習（PPO・自己対戦）で鍛える。
#   python train.py --env <TWTrainEnv の実行ファイル> --minutes 25
# ・約5分ごとに checkpoints/latest.pt と policy_latest.txt を保存する（強制終了しても続きから再開できる）
# ・指定した時間で保存して止まる（長時間回しっぱなしにしない）
# ・--random は通信の確認用（PyTorch なしでランダムに選ぶ）
import argparse, os, struct, subprocess, sys, time, json, random, math, copy
import numpy as np

MAGIC = 0x54574231


class EnvClient:
    """TWTrainEnv との通信"""

    def __init__(self, exe, games, seed, extra=()):
        cmd = [exe, "--games", str(games), "--seed", str(seed)] + list(extra)
        if exe.endswith(".exe") and os.name != "nt":
            cmd = ["mono"] + cmd
        self.p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, bufsize=1 << 20)
        self.r, self.w = self.p.stdout, self.p.stdin

    def _read(self, n):
        b = self.r.read(n)
        if len(b) != n:
            raise EOFError("環境が終了しました")
        return b

    def recv(self):
        magic, n_done = struct.unpack("<ii", self._read(8))
        assert magic == MAGIC, hex(magic)
        done = []
        for _ in range(n_done):
            slot, rew, kind = struct.unpack("<ifi", self._read(12))
            done.append((slot, rew, kind))
        n_dec, G, C = struct.unpack("<iii", self._read(12))
        decs = []
        for _ in range(n_dec):
            slot, K, shaping = struct.unpack("<iif", self._read(12))
            g = np.frombuffer(self._read(4 * G), dtype=np.float32).copy()
            c = np.frombuffer(self._read(4 * K * C), dtype=np.float32).reshape(K, C).copy()
            decs.append((slot, K, shaping, g, c))
        return done, decs, G, C

    def send_choices(self, choices):
        self.w.write(struct.pack("<ii", 1, len(choices)) + struct.pack("<%di" % len(choices), *choices))
        self.w.flush()

    def send_pool(self, paths):
        data = "\n".join(paths).encode("utf-8")
        self.w.write(struct.pack("<ii", 2, len(data)) + data)
        self.w.flush()

    def close(self):
        try:
            self.w.write(struct.pack("<i", 9)); self.w.flush()
        except Exception:
            pass
        try:
            self.p.wait(timeout=10)
        except Exception:
            self.p.kill()


def random_test(args):
    env = EnvClient(args.env, args.games, args.seed)
    t0, steps, results = time.time(), 0, []
    while time.time() - t0 < args.seconds:
        done, decs, G, C = env.recv()
        results += [r for _, r, _ in done]
        env.send_choices([random.randrange(K) for _, K, _, _, _ in decs])
        steps += len(decs)
    env.close()
    print(f"random: {steps} decisions in {time.time()-t0:.1f}s ({steps/(time.time()-t0):.0f}/s), games {len(results)}, win {sum(1 for r in results if r>0)}")


# ---------------------------------------------------------------- PPO

def build(G, C, H):
    import torch, torch.nn as nn

    class Policy(nn.Module):
        """候補ごとの点数（Unity 側の PolicyNet と同じ形：入力→H→H→1）"""
        def __init__(self):
            super().__init__()
            self.l1, self.l2, self.l3 = nn.Linear(G + C, H), nn.Linear(H, H), nn.Linear(H, 1)
            self.G, self.C = G, C

        def forward(self, g, c):   # g: [B,G], c: [B,K,C] → [B,K]
            x = torch.cat([g.unsqueeze(1).expand(-1, c.shape[1], -1), c], dim=-1)
            return self.l3(torch.relu(self.l2(torch.relu(self.l1(x))))).squeeze(-1)

    class Value(nn.Module):
        def __init__(self):
            super().__init__()
            self.net = nn.Sequential(nn.Linear(G, 128), nn.ReLU(), nn.Linear(128, 128), nn.ReLU(), nn.Linear(128, 1))

        def forward(self, g):
            return self.net(g).squeeze(-1)

    return Policy(), Value()


def export_policy(policy, path, H):
    """Unity が読むテキスト形式（twpolicy）"""
    sd = {k: v.detach().float().cpu().numpy() for k, v in policy.state_dict().items()}
    rows = [sd["l1.weight"].reshape(-1), sd["l1.bias"], sd["l2.weight"].reshape(-1), sd["l2.bias"], sd["l3.weight"].reshape(-1), sd["l3.bias"]]
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        G = policy.G; C = policy.C
        f.write(f"twpolicy 2 {H} {G} {C}\n")
        for r in rows:
            f.write(" ".join(f"{x:.6g}" for x in r) + "\n")
    os.replace(tmp, path)


def load_policy_txt(policy, path):
    """twpolicy テキストを読み込んで重みに入れる（過去最良へ巻き戻すとき用）"""
    import torch
    with open(path, encoding="utf-8") as f:
        lines = f.read().strip().split("\n")
    head = lines[0].split()
    fG, fC = (int(head[3]), int(head[4])) if len(head) >= 5 else (12, 32)
    rows = [np.array(l.split(), dtype=np.float32) for l in lines[1:7]]
    H = rows[1].shape[0]
    rows[0] = widen_np(rows[0].reshape(H, fG + fC), fG, fC, policy.G, policy.C)
    names = ["l1.weight", "l1.bias", "l2.weight", "l2.bias", "l3.weight", "l3.bias"]
    sd = policy.state_dict()
    for n, r in zip(names, rows):
        sd[n].copy_(torch.from_numpy(np.ascontiguousarray(r).reshape(sd[n].shape)))


def widen_np(w, fG, fC, G, C):
    """入力が増えたとき、古い重みを新しい位置へ写し、増えた所は0にする（[H, fG+fC] → [H, G+C]）"""
    if fG == G and fC == C:
        return w
    out = np.zeros((w.shape[0], G + C), dtype=np.float32)
    out[:, :fG] = w[:, :fG]; out[:, G:G + fC] = w[:, fG:fG + fC]
    return out


def migrate_ck(ck, G, C):
    """古いチェックポイント（入力 12+32）を新しい入力の大きさに合わせる。変えたら True"""
    import torch
    w = ck["policy"]["l1.weight"]
    if w.shape[1] == G + C:
        return False
    fG, fC = 12, w.shape[1] - 12
    for key in ("policy", "ema"):
        if key in ck:
            ck[key]["l1.weight"] = torch.from_numpy(widen_np(ck[key]["l1.weight"].cpu().numpy(), fG, fC, G, C))
    vw = ck["value"]["net.0.weight"].cpu().numpy()
    nv = np.zeros((vw.shape[0], G), dtype=np.float32); nv[:, :vw.shape[1]] = vw
    ck["value"]["net.0.weight"] = torch.from_numpy(nv)
    return True


def train(args):
    import torch
    dev = torch.device("cuda" if torch.cuda.is_available() and not args.cpu else "cpu")
    os.makedirs(args.out, exist_ok=True)
    ck_dir = os.path.join(args.out, "checkpoints"); os.makedirs(ck_dir, exist_ok=True)
    pool_dir = os.path.join(args.out, "pool"); os.makedirs(pool_dir, exist_ok=True)
    log_path = os.path.join(args.out, "train_log.csv")
    latest = os.path.join(ck_dir, "latest.pt")

    env = EnvClient(args.env, args.games, args.seed + int(time.time()) % 100000,
                    ["--map-every", str(args.map_every), "--builtin-pct", str(args.builtin_pct)])
    best_txt = os.path.join(args.out, "policy_best.txt")
    done, decs, G, C = env.recv()
    policy, value = build(G, C, args.hidden)
    policy.to(dev); value.to(dev)
    opt = torch.optim.Adam(list(policy.parameters()) + list(value.parameters()), lr=args.lr)
    state = {"updates": 0, "steps": 0, "games": 0, "pool": []}
    if os.path.exists(latest):
        ck = torch.load(latest, map_location=dev, weights_only=False)
        migrated = migrate_ck(ck, G, C)
        policy.load_state_dict(ck["policy"]); value.load_state_dict(ck["value"]); state = ck["state"]
        if migrated:
            print(f"[migrate] 特徴量が増えたので、重みを広げました（増えた所は0）。Adam の状態は作り直します", flush=True)
        else:
            opt.load_state_dict(ck["opt"])
        print(f"[resume] updates={state['updates']} steps={state['steps']} games={state['games']}", flush=True)
    for g in opt.param_groups:
        g["lr"] = args.lr   # 再開時もコマンドの学習率を使う
    # 重みの移動平均（評価・保存・対戦相手にはこちらを使い、一時的な崩れをならす）
    ema = copy.deepcopy(policy)
    if os.path.exists(latest) and "ema" in ck:
        ema.load_state_dict(ck["ema"])
    for p_ in ema.parameters():
        p_.requires_grad_(False)

    def send_pool():
        """過去の自分（間引いたもの）＋過去最良（重みを付けて多めに）を対戦相手にする"""
        paths = [p for p in state["pool"] if os.path.exists(p)]
        if os.path.exists(best_txt):
            paths += [best_txt] * args.best_weight
        if paths:
            env.send_pool(paths)

    def run_eval(policy_path, vs=None, games=None):
        cmd = [args.env, "--eval", policy_path, "--games", str(games or args.eval_games)] + (["--vs", vs] if vs else [])
        out = subprocess.run(cmd, capture_output=True, timeout=600).stdout.decode("utf-8", "replace")
        rate, base, info = float("nan"), float("nan"), ""
        for line in out.splitlines():
            if "rate" in line:
                rate = float(line.split("rate")[1].strip().rstrip("%"))
            if line.startswith("builtin"):
                base = float(line.split(":")[-1].strip().rstrip("%"))
            if line.startswith("random maps"):
                info = line.strip()
        maps = ";".join(l.strip() for l in out.splitlines() if l.startswith("  "))
        return rate, base, (info + " " + maps).strip()

    # 過去最良がまだ無ければ、指定したもの（無ければ今の重み）を過去最良にする
    if "best" in state and state["best"].get("metric") != 2 and os.path.exists(best_txt):
        r, b, _ = run_eval(best_txt, games=args.eval_games_full)   # 評価方法を変えたので、過去最良を測り直す
        state["best"]["rate"] = r; state["best"]["metric"] = 2
        print(f"[best] 過去最良を新しい評価で測り直しました: ランダムマップ {r:.1f}% (基本マップ {b:.1f}%)", flush=True)
    if "best" not in state:
        src = args.init_best if args.init_best and os.path.exists(args.init_best) else None
        if src is None:
            export_policy(ema, best_txt, args.hidden); src = best_txt
        elif os.path.abspath(src) != os.path.abspath(best_txt):
            import shutil; shutil.copyfile(src, best_txt)
        r, b, _ = run_eval(best_txt, games=args.eval_games_full)
        state["best"] = {"rate": r, "updates": -1, "src": os.path.basename(src), "metric": 2}
        print(f"[best] 初期の過去最良 {os.path.basename(src)}: 速いCOM {r:.1f}% (つよい・基本マップ {b:.1f}%)", flush=True)
    send_pool()

    def save(full=False):
        tmp = latest + ".tmp"
        export_policy(ema, os.path.join(args.out, "policy_latest.txt"), args.hidden)
        evaluate(full)   # 過去最良の入れ替えも含めてから、チェックポイントに書く
        torch.save({"policy": policy.state_dict(), "ema": ema.state_dict(), "value": value.state_dict(), "opt": opt.state_dict(), "state": state, "hidden": args.hidden}, tmp)
        os.replace(tmp, latest)
        print(f"[save] {time.strftime('%H:%M:%S')} updates={state['updates']} steps={state['steps']} games={state['games']}", flush=True)

    def evaluate(full=False):
        """今のCOM（重みの移動平均・一番良い手を選ぶ・つよい）を、固定のランダムマップで速いCOMと過去最良の両方と戦わせる。
        ふだんは少ない枚数（--eval-games）で測り、節目（各回の最後）と、過去最良を超えそうなときは多い枚数（--eval-games-full）で測り直す。
        過去最良の入れ替えは多い枚数の結果だけで決める：速いCOMへの合計勝率が過去最良を上回り、直接対決が --promote-vs 以上。"""
        if args.eval_games <= 0:
            return
        cur = os.path.join(args.out, "policy_latest.txt")
        best = state["best"]
        try:
            rate, base, maps = run_eval(cur, games=args.eval_games)
            vsb, _, _ = run_eval(cur, vs=best_txt, games=args.eval_games)
            n = args.eval_games // 2
            print(f"[eval] updates={state['updates']} ランダム{n}枚: 速いCOM {rate:.1f}% (過去最良 {best['rate']:.1f}%)  対 過去最良 {vsb:.1f}%  "
                  f"基本マップ {base:.1f}%  {maps}", flush=True)
            log_eval(n, rate, vsb, base, 0, maps)
            if not (full or rate > best["rate"] or rate < best["rate"] - args.revert_quick):
                return
            rate, base, maps = run_eval(cur, games=args.eval_games_full)
            vsb, _, _ = run_eval(cur, vs=best_txt, games=args.eval_games_full)
        except Exception as e:
            print(f"[eval] 失敗: {e}", flush=True); return
        n = args.eval_games_full // 2
        promote = rate > best["rate"] and vsb >= args.promote_vs
        print(f"[eval*] updates={state['updates']} ランダム{n}枚（節目）: 速いCOM {rate:.1f}% (過去最良 {best['rate']:.1f}%)  対 過去最良 {vsb:.1f}%  "
              f"基本マップ {base:.1f}%  {maps}", flush=True)
        if promote:
            import shutil
            shutil.copyfile(cur, best_txt)
            torch.save({"policy": ema.state_dict(), "value": value.state_dict(), "state": state, "hidden": args.hidden},
                       os.path.join(ck_dir, "best.pt"))
            state["best"] = {"rate": rate, "updates": state["updates"], "src": "latest", "metric": 2}
            print(f"[best] 過去最良を更新しました（updates={state['updates']}, ランダム{n}枚で速いCOM {rate:.1f}%）", flush=True)
            send_pool()
        elif rate < best["rate"] - args.revert_margin:
            revert(rate)
        log_eval(n, rate, vsb, base, int(promote), maps)

    def revert(rate):
        """崩れたので、学習中の重みと移動平均を過去最良に戻す（Adam の勢いも捨てる）"""
        nonlocal opt
        load_policy_txt(policy, best_txt); load_policy_txt(ema, best_txt)
        opt = torch.optim.Adam(list(policy.parameters()) + list(value.parameters()), lr=args.lr)
        state["reverts"] = state.get("reverts", 0) + 1
        print(f"[revert] ランダム200枚で {rate:.1f}% と過去最良 {state['best']['rate']:.1f}% より {args.revert_margin:.0f} ポイント以上低いので、"
              f"過去最良（updates={state['best']['updates']}）へ巻き戻しました（通算 {state['reverts']} 回）", flush=True)

    def log_eval(n, rate, vsb, base, promoted, maps):
        ev_path = os.path.join(args.out, "eval_log4.csv")
        new = not os.path.exists(ev_path)
        with open(ev_path, "a", encoding="utf-8") as f:
            if new: f.write("time,updates,games,maps_n,rate_random,vs_best,rate_builtin,promoted,maps\n")
            f.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')},{state['updates']},{state['games']},{n},{rate:.1f},{vsb:.1f},{base:.1f},{promoted},{maps}\n")

    t_start, t_save = time.time(), time.time()
    traj = {}            # slot → 現在の試合の手の記録
    recent = {0: [], 1: []}   # 相手の種類ごとの最近の勝敗
    recent_len = []           # 最近の試合の手数

    def policy_act(decs):
        Kmax = max(K for _, K, _, _, _ in decs)
        g = torch.tensor(np.stack([d[3] for d in decs]), device=dev)
        c = np.zeros((len(decs), Kmax, C), dtype=np.float32); mask = np.zeros((len(decs), Kmax), dtype=bool)
        for i, (_, K, _, _, cc) in enumerate(decs):
            c[i, :K] = cc; mask[i, :K] = True
        ct = torch.tensor(c, device=dev); mt = torch.tensor(mask, device=dev)
        with torch.no_grad():
            logits = policy(g, ct).masked_fill(~mt, -1e9)
            dist = torch.distributions.Categorical(logits=logits)
            a = dist.sample()
            return a.cpu().numpy(), dist.log_prob(a).cpu().numpy(), value(g).cpu().numpy()

    while True:
        # ---- 集める ----
        def gae(seg):
            """試合（またはその途中まで）の手の並びに対して利得を計算する"""
            last = 0.0
            for i in range(len(seg) - 1, -1, -1):
                st = seg[i]
                nv = 0.0 if st["done"] else st["next_v"]
                delta = st["r"] + args.gamma * nv - st["v"]
                last = delta + (0.0 if st["done"] else args.gamma * args.lam * last)
                st["adv"] = last; st["ret"] = last + st["v"]
            return seg

        buf = []
        for _ in range(args.rollout):
            for slot, rew, kind in done:
                tr = traj.pop(slot, None)
                if tr:
                    tr[-1]["r"] += rew; tr[-1]["done"] = True
                    buf.extend(gae(tr))
                    recent_len.append(len(tr)); recent_len[:] = recent_len[-500:]
                state["games"] += 1
                recent[kind].append(1 if rew > 0 else 0); recent[kind] = recent[kind][-500:]
            acts, logps, vals = policy_act(decs)
            shaping_coef = args.shaping * max(0.0, 1.0 - state["updates"] / args.shaping_decay)   # 途中点は学習が進むほど小さく
            for (slot, K, shaping, g, c), a, lp, v in zip(decs, acts, logps, vals):
                tr = traj.setdefault(slot, [])
                if tr:
                    tr[-1]["r"] += shaping_coef * shaping
                    tr[-1]["next_v"] = float(v)
                tr.append({"g": g, "c": c, "a": int(a), "lp": float(lp), "v": float(v), "r": 0.0, "done": False, "next_v": 0.0})
            env.send_choices([int(a) for a in acts])
            state["steps"] += len(decs)
            done, decs, _, _ = env.recv()
        # 試合の途中の手も学習に使う（最後の手は次の回へ持ち越し、その価値で補う）
        for slot, tr in traj.items():
            if len(tr) > 1:
                buf.extend(gae(tr[:-1])); traj[slot] = tr[-1:]
        n = len(buf)
        adv = np.array([st["adv"] for st in buf], dtype=np.float32)
        ret = np.array([st["ret"] for st in buf], dtype=np.float32)

        # ---- 学習 ----
        Kmax = max(s["c"].shape[0] for s in buf)
        g_all = torch.tensor(np.stack([s["g"] for s in buf]), device=dev)
        c_all = np.zeros((n, Kmax, C), dtype=np.float32); m_all = np.zeros((n, Kmax), dtype=bool)
        for i, s in enumerate(buf):
            K = s["c"].shape[0]; c_all[i, :K] = s["c"]; m_all[i, :K] = True
        c_all = torch.tensor(c_all, device=dev); m_all = torch.tensor(m_all, device=dev)
        a_all = torch.tensor([s["a"] for s in buf], device=dev)
        lp_old = torch.tensor([s["lp"] for s in buf], device=dev)
        adv_t = torch.tensor(adv, device=dev); ret_t = torch.tensor(ret, device=dev)
        adv_t = (adv_t - adv_t.mean()) / (adv_t.std() + 1e-8)
        for _ in range(args.epochs):
            perm = torch.randperm(n, device=dev)
            for st in range(0, n, args.minibatch):
                idx = perm[st:st + args.minibatch]
                logits = policy(g_all[idx], c_all[idx]).masked_fill(~m_all[idx], -1e9)
                dist = torch.distributions.Categorical(logits=logits)
                lp = dist.log_prob(a_all[idx])
                ratio = torch.exp(lp - lp_old[idx])
                pg = -torch.min(ratio * adv_t[idx], torch.clamp(ratio, 1 - args.clip, 1 + args.clip) * adv_t[idx]).mean()
                vl = ((value(g_all[idx]) - ret_t[idx]) ** 2).mean()
                ent = dist.entropy().mean()
                loss = pg + 0.5 * vl - args.ent * ent
                opt.zero_grad(); loss.backward()
                torch.nn.utils.clip_grad_norm_(list(policy.parameters()) + list(value.parameters()), 0.5)
                opt.step()
        state["updates"] += 1
        with torch.no_grad():
            for pe, pw in zip(ema.parameters(), policy.parameters()):
                pe.mul_(args.ema).add_(pw, alpha=1 - args.ema)

        w0 = np.mean(recent[0]) if recent[0] else float("nan"); w1 = np.mean(recent[1]) if recent[1] else float("nan")
        elapsed = time.time() - t_start
        ep_len = float(np.mean(recent_len)) if recent_len else float("nan")
        print(f"[upd {state['updates']}] steps {state['steps']} games {state['games']} buf {n} "
              f"winrate vsCOM {w0:.3f} vsSelf {w1:.3f} ent {float(ent.detach()):.3f} len {ep_len:.0f} shaping {shaping_coef:.2f} {elapsed/60:.1f}min", flush=True)
        new_log = not os.path.exists(log_path)
        with open(log_path, "a", encoding="utf-8") as f:
            if new_log: f.write("time,updates,steps,games,win_vs_com,win_vs_self,entropy,ep_len,shaping\n")
            f.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')},{state['updates']},{state['steps']},{state['games']},{w0:.4f},{w1:.4f},{float(ent.detach()):.4f},{ep_len:.1f},{shaping_coef:.3f}\n")

        # 過去の自分を対戦相手に加える
        if state["updates"] % args.pool_every == 0:
            pth = os.path.join(pool_dir, f"policy_{state['updates']:05d}.txt")
            export_policy(ema, pth, args.hidden)
            pool = state["pool"] + [pth]
            # 多すぎたら、前後の間隔が一番詰まっているものを間引く（古いものも少し残して相手の幅を持たせる）
            num = lambda q: int(os.path.basename(q).split("_")[1].split(".")[0])
            while len(pool) > args.pool_size:
                i = min(range(1, len(pool) - 1), key=lambda k: num(pool[k + 1]) - num(pool[k - 1]))
                del pool[i]
            state["pool"] = pool
            send_pool()

        if time.time() - t_save >= args.save_minutes * 60:
            save(); t_save = time.time()
        if elapsed >= args.minutes * 60:
            save(full=True)   # 各回の最後は節目として多い枚数で測る
            break
    env.close()
    print("[stop] 指定時間になったので保存して終了しました", flush=True)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--env", required=True)
    ap.add_argument("--out", default=os.path.dirname(os.path.abspath(__file__)))
    ap.add_argument("--games", type=int, default=256)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--minutes", type=float, default=25)
    ap.add_argument("--save-minutes", type=float, default=2.5)
    ap.add_argument("--rollout", type=int, default=64, help="1回の学習までに集める往復の回数")
    ap.add_argument("--hidden", type=int, default=128)
    ap.add_argument("--lr", type=float, default=1e-4)
    ap.add_argument("--gamma", type=float, default=0.995)
    ap.add_argument("--lam", type=float, default=0.95)
    ap.add_argument("--clip", type=float, default=0.2)
    ap.add_argument("--ent", type=float, default=0.01)
    ap.add_argument("--epochs", type=int, default=4)
    ap.add_argument("--minibatch", type=int, default=2048)
    ap.add_argument("--shaping", type=float, default=0.5)
    ap.add_argument("--shaping-decay", type=float, default=3000, help="途中点を0まで下げる更新回数")
    ap.add_argument("--pool-every", type=int, default=100)
    ap.add_argument("--pool-size", type=int, default=8)
    ap.add_argument("--eval-games", type=int, default=100, help="途中の評価の試合数（ランダムマップはこの半分の枚数・両席。0で評価しない）")
    ap.add_argument("--eval-games-full", type=int, default=400, help="節目の評価の試合数（ランダムマップ200枚・両席）")
    ap.add_argument("--ema", type=float, default=0.99, help="重みの移動平均の係数（0.99 で直近約100回分）")
    ap.add_argument("--revert-margin", type=float, default=3.0, help="節目の評価が過去最良よりこれだけ低ければ巻き戻す（ポイント）")
    ap.add_argument("--revert-quick", type=float, default=5.0, help="途中の評価が過去最良よりこれだけ低ければ、節目の評価で確かめる（ポイント）")
    ap.add_argument("--promote-vs", type=float, default=45.0, help="過去最良を入れ替えるのに必要な直接対決の勝率（%）")
    ap.add_argument("--map-every", type=int, default=1500, help="ランダムマップ5種類を作り直す試合数")
    ap.add_argument("--builtin-pct", type=int, default=40, help="基本マップを使う割合（%）")
    ap.add_argument("--best-weight", type=int, default=3, help="対戦相手の中の過去最良の重み（過去の自分1体あたりの何倍か）")
    ap.add_argument("--init-best", default=None, help="最初の過去最良にする重みファイル")
    ap.add_argument("--cpu", action="store_true")
    ap.add_argument("--random", action="store_true")
    ap.add_argument("--seconds", type=float, default=20)
    a = ap.parse_args()
    random_test(a) if a.random else train(a)
