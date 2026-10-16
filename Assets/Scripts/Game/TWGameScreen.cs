// 対戦画面。ローカル（あなた＋COM／観戦）とオンライン（サーバーが確定した操作で進める）の両方を扱う
using System;
using System.Collections.Generic;
using TW.Sim;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class TWGameScreen : TWScreen
{
    MapDef map;
    int localPlayer;             // -1 = 観戦
    Action onBack;
    string backLabel = "ステージ選択へ";
    TWOnlineMatch online;        // null ならローカル対戦
    ComProfile[] comProfiles;    // 席ごとのCOMの強さ（null ならふつう）

    Simulation sim;
    readonly List<IComPlayer> ais = new List<IComPlayer>();
    TWBoardView board;
    float acc, speed = 1f, interp;
    bool paused, resultShown;

    // HUD
    readonly List<Text> chipTexts = new List<Text>();
    Text timer, hint, center;
    GameObject menu, result;
    RectTransform chipRow;
    RectTransform tipRect;       // 細胞のマウスオーバー表示
    Text tipText;

    // 入力
    enum DragMode { None, Connect, Swipe }
    DragMode drag;
    int dragSrc = -1, hoverCell = -1;
    Vector2 pointerWorld, swipePrev;
    readonly HashSet<int> cutThisSwipe = new HashSet<int>();
    readonly List<Vector2> swipeTrail = new List<Vector2>();

    /// <summary>ローカル対戦の準備。localSeat が -1 なら全員 COM で観戦</summary>
    public void Launch(MapDef map, int localSeat, Action onBack, string backLabel = "ステージ選択へ", ComProfile[] comProfiles = null)
    {
        this.map = map;
        this.comProfiles = comProfiles;
        localPlayer = localSeat;
        this.onBack = onBack;
        this.backLabel = backLabel;
        online = null;
    }

    /// <summary>オンライン対戦の準備（サーバーから試合開始が届いたとき）</summary>
    public void LaunchOnline(TWOnlineMatch match)
    {
        online = match;
        map = match.Map;
        localPlayer = match.MySeat;
        onBack = () => App.Show(App.Room);
        backLabel = "ルームへ";
    }

    bool Online { get { return online != null; } }

    protected override void OnEnter()
    {
        board = new TWBoardView("GameBoard");
        board.Highlight = i => drag == DragMode.Connect && (i == dragSrc || i == hoverCell) ? 1f : 0f;
        BuildHud();
        NewGame();
        if (Online)
        {
            TWNet.Kicked += OnKicked;
            TWNet.Disconnected += OnDisconnected;
            TWNet.GameStarted += OnNextGame;
        }
    }

    public override void Exit()
    {
        TWNet.Kicked -= OnKicked;
        TWNet.Disconnected -= OnDisconnected;
        TWNet.GameStarted -= OnNextGame;
        if (menu != null) { TWUI.Destroy(menu); menu = null; }
        if (board != null) board.Destroy();
        board = null;
        result = null;
    }

    void OnKicked(string reason) { if (!string.IsNullOrEmpty(reason)) App.Toast(reason, 3f); App.Show(App.Lobby); }
    void OnDisconnected() { App.Toast("サーバーとの接続が切れました", 3f); App.Show(App.Lobby); }
    void OnNextGame() { LaunchOnline(TWNet.Match); App.Show(App.Game); }

    void NewGame()
    {
        ais.Clear();
        if (Online) sim = online.Sim;
        else
        {
            sim = new Simulation(map);
            for (int p = 0; p < sim.PlayerCount; p++)
                if (p != localPlayer) ais.Add(TWLearnedCom.Create(p, comProfiles != null && p < comProfiles.Length ? comProfiles[p] : SimpleAI.Presets[SimpleAI.DefaultLevel]));
        }
        board.Build(sim);
        acc = 0; paused = false; resultShown = false; drag = DragMode.None;
        if (result != null) TWUI.Destroy(result);
        if (menu != null) TWUI.Destroy(menu);
        BuildChips();
    }

    // ---------------- HUD ----------------

    void BuildHud()
    {
        var top = TWUI.Rect("TopBar", Root); TWUI.Place(top, 0, 0.925f, 1, 1);
        var topBg = top.gameObject.AddComponent<Image>(); topBg.color = new Color(TWUI.Ink.r, TWUI.Ink.g, TWUI.Ink.b, 0.85f);

        chipRow = TWUI.Rect("Chips", top); TWUI.Place(chipRow, 0.01f, 0.1f, 0.66f, 0.9f);
        var h = chipRow.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.spacing = 14; h.childControlWidth = true; h.childControlHeight = true; h.childForceExpandWidth = true; h.childForceExpandHeight = true;

        timer = TWUI.Label(top, "", 34, TWUI.Text, TextAnchor.MiddleCenter, "Timer");
        TWUI.Place(timer.rectTransform, 0.66f, 0.05f, 0.76f, 0.95f);

        if (!Online)
        {
            TWUI.Btn sp = null;
            sp = TWUI.Button(top, "速度 ×1", () => { speed = speed >= 4 ? 1 : speed * 2; sp.Label.text = "速度 ×" + speed; }, TWUI.Style.Normal, 24);
            TWUI.Place(sp.Rect, 0.77f, 0.14f, 0.87f, 0.86f);
        }
        var mb = TWUI.Button(top, "メニュー", OpenMenu, TWUI.Style.Normal, 24);
        TWUI.Place(mb.Rect, 0.88f, 0.14f, 0.99f, 0.86f);

        hint = TWUI.Label(Root, "", 22, new Color(1, 1, 1, 0.55f), TextAnchor.LowerLeft, "Hint");
        TWUI.Place(hint.rectTransform, 0.01f, 0.005f, 0.8f, 0.04f);

        center = TWUI.Label(Root, "", 120, TWUI.Text, TextAnchor.MiddleCenter, "Center");
        TWUI.Place(center.rectTransform, 0.2f, 0.35f, 0.8f, 0.65f);
        var ol = center.gameObject.AddComponent<Outline>(); ol.effectColor = new Color(0, 0, 0, 0.8f); ol.effectDistance = new Vector2(3, -3);

        // 細胞のマウスオーバー表示（触手の本数・次のレベルまで）
        var plate = TWUI.Panel(Root, new Color(TWUI.Ink.r, TWUI.Ink.g, TWUI.Ink.b, 0.92f), true, "CellTip");
        tipRect = plate.rectTransform;
        tipRect.anchorMin = tipRect.anchorMax = new Vector2(0.5f, 0.5f);
        tipRect.pivot = new Vector2(0.5f, 0f);
        tipText = TWUI.Label(plate.transform, "", 24, TWUI.Text, TextAnchor.MiddleCenter, "Text");
        tipText.resizeTextForBestFit = false;
        tipText.horizontalOverflow = HorizontalWrapMode.Overflow; tipText.verticalOverflow = VerticalWrapMode.Overflow;
        TWUI.Fill(tipText.rectTransform);
        foreach (var g in plate.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
        plate.gameObject.SetActive(false);
    }

    void BuildChips()
    {
        TWUI.Clear(chipRow);
        chipTexts.Clear();
        for (int p = 0; p < sim.PlayerCount; p++)
        {
            var chip = TWUI.Panel(chipRow, p == localPlayer ? new Color(TWUI.Lime.r, TWUI.Lime.g, TWUI.Lime.b, 0.14f) : TWUI.Agar, false, "Chip" + p);
            var dot = TWUI.Dot(chip.transform, TWBoardView.PlayerColors[p]);
            TWUI.Square(dot.rectTransform, 0.03f, 0.5f, 26);
            var t = TWUI.Label(chip.transform, "", 24, TWUI.Text, TextAnchor.MiddleLeft);
            TWUI.Place(t.rectTransform, 0.03f, 0, 0.98f, 1);
            t.rectTransform.offsetMin = new Vector2(38, 0);
            chipTexts.Add(t);
        }
        hint.text = localPlayer < 0 ? "観戦中" :
            "自分の細胞からドラッグ：触手を伸ばす　／　触手をなぞる：切断（根元に近いほど相手に多く流れ込む）";
    }

    string WhoIs(int p)
    {
        if (Online)
        {
            string n = online.Names != null && p < online.Names.Length ? online.Names[p] : "?";
            if (online.Seats != null && p < online.Seats.Length)
            {
                var s = online.Seats[p];
                if (s.Resigned) n += "<color=#8FA9A6>（降参）</color>";
                else if (s.Takeover) n += "<color=#F2B544>（COM代打ち）</color>";
                else if (!s.Com && !s.Connected) n += "<color=#F2B544>（切断中）</color>";
            }
            return p == localPlayer ? n + "<color=#9BE15D>（あなた）</color>" : n;
        }
        return p == localPlayer ? "あなた" : "COM";
    }

    void UpdateHud()
    {
        for (int p = 0; p < chipTexts.Count; p++)
        {
            int cells = sim.CellCount(p);
            chipTexts[p].text = cells == 0 ? WhoIs(p) + "　<color=#8FA9A6>全滅</color>" : WhoIs(p) + "　細胞" + cells + "　" + sim.Score(p);
        }
        if (sim.TimeLimitTicks >= int.MaxValue / 2) timer.text = "∞";
        else
        {
            int sec = Mathf.Max(0, (sim.TimeLimitTicks - sim.Tick) / Balance.TicksPerSecond);
            timer.text = sec / 60 + ":" + (sec % 60).ToString("00");
        }
        if (localPlayer >= 0 && !sim.Over && sim.CellCount(localPlayer) == 0) hint.text = "全滅しました。決着まで観戦できます（メニューからやめられます）";

        // カウントダウン・同期待ち
        if (Online && !resultShown)
        {
            float left = online.StartAt - Time.unscaledTime;
            if (left > 0) center.text = Mathf.CeilToInt(left).ToString();
            else if (online.WaitingSnapshot) center.text = "<size=48>同期しています…</size>";
            else if (sim.Tick < 25 && online.ConfirmedTick > 0) center.text = "<size=72>スタート</size>";
            else center.text = "";
        }
        else center.text = "";
    }

    void OpenMenu()
    {
        if (menu != null) return;
        if (!Online) paused = true;
        var p = App.Modal(0.36f, 0.3f, 0.64f, 0.72f, out menu);
        var t = TWUI.Label(p, Online ? "メニュー" : "一時停止", 40, TWUI.Text, TextAnchor.MiddleCenter);
        TWUI.Place(t.rectTransform, 0.05f, 0.78f, 0.95f, 0.95f);
        var cont = TWUI.Button(p, "続ける", () => { TWUI.Destroy(menu); menu = null; paused = false; }, TWUI.Style.Primary, 30);
        TWUI.Place(cont.Rect, 0.1f, 0.54f, 0.9f, 0.72f);
        if (Online)
        {
            bool canResign = localPlayer >= 0 && !sim.Over && sim.CellCount(localPlayer) > 0 &&
                             !(online.Seats != null && localPlayer < online.Seats.Length && online.Seats[localPlayer].Resigned);
            var resign = TWUI.Button(p, "降参する", () => App.Confirm("降参しますか？\nあなたの細胞はすべて中立になります。", "降参する", () =>
            {
                TWNet.Send(new TWResign()); TWUI.Destroy(menu); menu = null;
            }, true), TWUI.Style.Normal, 28);
            TWUI.Place(resign.Rect, 0.1f, 0.32f, 0.9f, 0.5f);
            resign.SetInteractable(canResign);
            var quit = TWUI.Button(p, "部屋を出る", () => App.Confirm(localPlayer >= 0 && !sim.Over ?
                "部屋を出ますか？\n" + (int)TWServer.AwayGraceSeconds + "秒後からCOMが代わりに動かします。\nロビーから入り直せば席に戻れます。" : "部屋を出ますか？", "出る", () =>
            {
                TWUI.Destroy(menu); menu = null;
                TWNet.Send(new TWLeaveRoom());
            }, true), TWUI.Style.Danger, 28);
            TWUI.Place(quit.Rect, 0.1f, 0.1f, 0.9f, 0.28f);
        }
        else
        {
            var retry = TWUI.Button(p, "最初からやり直す", () => { TWUI.Destroy(menu); menu = null; NewGame(); }, TWUI.Style.Normal, 28);
            TWUI.Place(retry.Rect, 0.1f, 0.32f, 0.9f, 0.5f);
            var quit = TWUI.Button(p, "やめて" + backLabel.Replace("へ", "") + "へ", () => { TWUI.Destroy(menu); menu = null; onBack(); }, TWUI.Style.Danger, 28);
            TWUI.Place(quit.Rect, 0.1f, 0.1f, 0.9f, 0.28f);
        }
    }

    void ShowResult()
    {
        resultShown = true;
        if (menu != null) { TWUI.Destroy(menu); menu = null; }
        int winner = Online ? online.Winner : sim.Winner;
        var panel = TWUI.Panel(Root, new Color(TWUI.Agar.r, TWUI.Agar.g, TWUI.Agar.b, 0.95f), true, "Result");
        result = panel.gameObject;
        TWUI.Place(panel.rectTransform, 0.31f, 0.22f, 0.69f, 0.78f);
        string head; Color hc;
        if (winner < 0) { head = "引き分け"; hc = TWUI.Text; }
        else if (localPlayer < 0) { head = StripTags(WhoIs(winner)) + " の勝利"; hc = TWBoardView.PlayerColors[winner]; }
        else if (winner == localPlayer) { head = "勝利"; hc = TWUI.Lime; }
        else { head = "敗北"; hc = TWUI.Danger; }
        var t = TWUI.Label(panel.transform, head, 72, hc, TextAnchor.MiddleCenter);
        TWUI.Place(t.rectTransform, 0.05f, 0.72f, 0.95f, 0.95f);
        int sec = sim.Tick / Balance.TicksPerSecond;
        string reason = sim.Tick >= sim.TimeLimitTicks ? "時間切れ（合計エネルギーで判定）" : "決着まで " + sec / 60 + "分" + (sec % 60) + "秒";
        var r = TWUI.Label(panel.transform, reason, 24, TWUI.TextDim, TextAnchor.MiddleCenter);
        TWUI.Place(r.rectTransform, 0.05f, 0.63f, 0.95f, 0.72f);
        var sb = new System.Text.StringBuilder();
        for (int p = 0; p < sim.PlayerCount; p++)
            sb.Append("<color=#" + ColorUtility.ToHtmlStringRGB(TWBoardView.PlayerColors[p]) + ">●</color> ")
              .Append(WhoIs(p)).Append("　細胞 ").Append(sim.CellCount(p)).Append("　合計 ").Append(sim.Score(p)).Append('\n');
        var s = TWUI.Label(panel.transform, sb.ToString().TrimEnd(), 26, TWUI.Text, TextAnchor.MiddleCenter);
        TWUI.Place(s.rectTransform, 0.05f, 0.27f, 0.95f, 0.63f);
        if (Online)
        {
            var back = TWUI.Button(panel.transform, "ルームに戻る", () => onBack(), TWUI.Style.Primary, 30);
            TWUI.Place(back.Rect, 0.2f, 0.06f, 0.8f, 0.22f);
        }
        else
        {
            var again = TWUI.Button(panel.transform, "もう一度", NewGame, TWUI.Style.Primary, 30);
            TWUI.Place(again.Rect, 0.06f, 0.06f, 0.48f, 0.22f);
            var back = TWUI.Button(panel.transform, backLabel, () => onBack(), TWUI.Style.Normal, 28);
            TWUI.Place(back.Rect, 0.52f, 0.06f, 0.94f, 0.22f);
        }
    }

    static string StripTags(string s) { return System.Text.RegularExpressions.Regex.Replace(s ?? "", "<.*?>", ""); }

    // ---------------- 進行 ----------------

    public override void Tick()
    {
        TWBoardView.FitCamera(App.Cam, 0, 0.03f, 1, 0.925f);
        HandleInput();

        float dt = 1f / Balance.TicksPerSecond;
        if (Online)
        {
            interp = online.Advance(Time.unscaledDeltaTime);
            if (online.Finished && !resultShown && (sim.Over || sim.Tick >= online.ConfirmedTick)) ShowResult();
        }
        else
        {
            if (!paused)
            {
                acc += Time.deltaTime * speed;
                int guard = 0;
                while (acc >= dt && guard++ < 40)
                {
                    acc -= dt;
                    foreach (var ai in ais) ai.Think(sim);
                    sim.Step();
                }
            }
            interp = sim.Over || paused ? 0f : Mathf.Clamp01(acc / dt);
            if (sim.Over && !resultShown) ShowResult();
        }
        UpdateHud();
        UpdateTip();

        board.Render(sim, sim.Over ? 0f : interp);
        DrawOverlays();
        board.EndFrame();
    }

    void DrawOverlays()
    {
        if (drag == DragMode.Connect && dragSrc >= 0)
        {
            var s = sim.Cells[dragSrc];
            Vector2 a = new Vector2(s.X, s.Y);
            Vector2 b = hoverCell >= 0 ? new Vector2(sim.Cells[hoverCell].X, sim.Cells[hoverCell].Y) : pointerWorld;
            bool ok = hoverCell >= 0 && sim.CanConnect(localPlayer, dragSrc, hoverCell) == null;
            Color pc = hoverCell < 0 ? new Color(1, 1, 1, 0.5f) : ok ? new Color(0.7f, 1f, 0.7f, 0.9f) : new Color(1f, 0.4f, 0.4f, 0.8f);
            board.DottedLine(a, b, pc);
        }
        for (int k = 0; k < swipeTrail.Count; k++)
        {
            float f = (float)(k + 1) / swipeTrail.Count;
            board.Dot(swipeTrail[k], 0.12f * f, new Color(1, 1, 1, 0.8f * f), 30);
        }
    }

    /// <summary>マウスを乗せた細胞の情報（触手の本数・次のレベルまでの必要エネルギー）を細胞の上に出す</summary>
    void UpdateTip()
    {
        if (tipRect == null) return;
        int ci = -1;
        bool overUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        bool menuOpen = menu != null && menu.activeInHierarchy;
        if (Mouse.current != null && !overUi && !menuOpen && !resultShown && drag != DragMode.Swipe) ci = CellAt(pointerWorld, 0.1f);
        if (ci < 0) { if (tipRect.gameObject.activeSelf) tipRect.gameObject.SetActive(false); return; }

        var c = sim.Cells[ci];
        string text;
        if (c.Owner < 0)
        {
            text = "中立　占領に必要 " + c.CaptureValue;
            if (c.GaugeOwner >= 0 && c.Gauge > 0) text += "\n<color=#" + ColorUtility.ToHtmlStringRGB(TWBoardView.PlayerColors[c.GaugeOwner]) + ">" + WhoIs(c.GaugeOwner) + "</color> が " + c.Gauge + " ためています";
        }
        else
        {
            int g = c.Grade;
            string next = g >= Balance.GradeCount - 1 ? "最大レベル" : "次のレベルまで あと <color=#F2B544>" + Mathf.Max(0, Balance.Up[g] - c.Energy) + "</color>";
            text = "Lv" + (g + 1) + " " + Balance.GradeNames[g] + "　触手 <color=#9BE15D>" + sim.ActiveCount(c.Id) + "/" + Balance.MaxTentacles[g] + "</color>\n" + next;
        }
        if (tipText.text != text) tipText.text = text;
        if (!tipRect.gameObject.activeSelf) tipRect.gameObject.SetActive(true);
        tipRect.SetAsLastSibling();
        tipRect.sizeDelta = new Vector2(tipText.preferredWidth + 32, tipText.preferredHeight + 16);

        // 細胞の少し上に置く（上にはみ出すなら下に）
        var parent = (RectTransform)tipRect.parent;
        var canvas = parent.GetComponentInParent<Canvas>().rootCanvas;
        Camera uiCam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        Vector2 top, bottom;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, App.Cam.WorldToScreenPoint(new Vector3(c.X, c.Y + c.Radius + 0.12f, 0)), uiCam, out top);
        RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, App.Cam.WorldToScreenPoint(new Vector3(c.X, c.Y - c.Radius - 0.12f, 0)), uiCam, out bottom);
        var pr = parent.rect;
        Vector2 size = tipRect.sizeDelta;
        bool below = top.y + size.y > pr.yMax - 70;   // 上のHUDに重なるなら細胞の下へ
        tipRect.pivot = new Vector2(0.5f, below ? 1f : 0f);
        Vector2 at = below ? bottom : top;
        at.x = Mathf.Clamp(at.x, pr.xMin + size.x * 0.5f + 8, pr.xMax - size.x * 0.5f - 8);
        tipRect.anchoredPosition = at - pr.center;
    }

    int CellAt(Vector2 p, float extra)
    {
        int best = -1; float bestD = float.MaxValue;
        foreach (var c in sim.Cells)
        {
            float d = Vector2.Distance(p, new Vector2(c.X, c.Y));
            if (d < c.Radius + extra && d < bestD) { bestD = d; best = c.Id; }
        }
        return best;
    }

    void Issue(Command c)
    {
        if (Online) TWNet.SendCmd(c);
        else sim.Enqueue(c);
    }

    bool InputBlocked()
    {
        if (localPlayer < 0 || sim.Over || paused || resultShown) return true;
        if (Online && (Time.unscaledTime < online.StartAt || online.WaitingSnapshot)) return true;
        return false;
    }

    void HandleInput()
    {
        var pointer = Pointer.current;
        var cam = App.Cam;
        if (pointer == null || cam == null) return;
        Vector2 screen = pointer.position.ReadValue();
        Vector3 w = cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, 10));
        pointerWorld = new Vector2(w.x, w.y);

        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame && !resultShown) OpenMenu();
        if (InputBlocked()) { drag = DragMode.None; swipeTrail.Clear(); return; }

        bool pressed = pointer.press.wasPressedThisFrame;
        bool held = pointer.press.isPressed;
        bool released = pointer.press.wasReleasedThisFrame;

        if (pressed)
        {
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
            int c = CellAt(pointerWorld, 0.25f);
            if (c >= 0 && sim.Cells[c].Owner == localPlayer) { drag = DragMode.Connect; dragSrc = c; }
            else
            {
                drag = DragMode.Swipe;
                swipePrev = pointerWorld;
                cutThisSwipe.Clear();
                swipeTrail.Clear();
                swipeTrail.Add(pointerWorld);
            }
        }

        if (drag == DragMode.Connect)
        {
            hoverCell = CellAt(pointerWorld, 0.35f);
            if (hoverCell == dragSrc) hoverCell = -1;
            if (released)
            {
                if (hoverCell >= 0)
                {
                    string why = sim.CanConnect(localPlayer, dragSrc, hoverCell);
                    if (why == null) Issue(Command.Connect(localPlayer, dragSrc, hoverCell));
                    else App.Toast(why, 1.2f);
                }
                drag = DragMode.None; dragSrc = -1; hoverCell = -1;
            }
        }
        else if (drag == DragMode.Swipe)
        {
            if ((held || released) && (pointerWorld - swipePrev).sqrMagnitude > 0.0001f)
            {
                TryCut(swipePrev, pointerWorld);
                swipePrev = pointerWorld;
                swipeTrail.Add(pointerWorld);
                if (swipeTrail.Count > 24) swipeTrail.RemoveAt(0);
            }
            if (released) { drag = DragMode.None; swipeTrail.Clear(); }
        }
    }

    void TryCut(Vector2 p, Vector2 q)
    {
        foreach (var t in sim.Tentacles)
        {
            if (t.Dead || t.Owner != localPlayer || !t.IsActive || cutThisSwipe.Contains(t.Id)) continue;
            Vector2 a, b;
            TWBoardView.Ends(sim, t, out a, out b);
            // 伸ばしている途中や押し合い中は、自分の先端までが切れる範囲
            b = Vector2.Lerp(a, b, (float)t.Ext / t.Length);
            float u;
            if (SegmentIntersect(a, b, p, q, out u))
            {
                float frac = u * t.Ext / t.Length;
                Issue(Command.CutAt(localPlayer, t, frac));
                cutThisSwipe.Add(t.Id);
            }
        }
    }

    static bool SegmentIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d, out float u)
    {
        u = 0;
        Vector2 r = b - a, s = d - c;
        float den = r.x * s.y - r.y * s.x;
        if (Mathf.Abs(den) < 1e-6f) return false;
        Vector2 ac = c - a;
        float t1 = (ac.x * s.y - ac.y * s.x) / den;
        float t2 = (ac.x * r.y - ac.y * r.x) / den;
        if (t1 < 0 || t1 > 1 || t2 < 0 || t2 > 1) return false;
        u = t1;
        return true;
    }
}
