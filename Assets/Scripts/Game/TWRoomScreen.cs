// オンラインの部屋（待機中）：席・マップ・設定・チャット。部屋主が開始する
using System.Collections.Generic;
using TW.Sim;
using UnityEngine;
using UnityEngine.UI;

public class TWRoomScreen : TWScreen
{
    Text title, hostLabel, spectators, chatLog;
    RawImage preview;
    Text mapName, timeLabel, specLabel;
    RectTransform seatsBox, hostControls, myControls;
    InputField chatInput;
    string lastMapJson;
    TWUI.Btn readyBtn, startBtn;

    protected override void OnEnter()
    {
        var bg = Root.gameObject.AddComponent<Image>(); bg.color = TWUI.Ink;
        var leave = TWUI.Button(Root, "← 部屋を出る", () => TWNet.Send(new TWLeaveRoom()), TWUI.Style.Quiet, 26);
        TWUI.Place(leave.Rect, 0.015f, 0.915f, 0.14f, 0.985f);
        title = TWUI.Label(Root, "", 44, TWUI.Text, TextAnchor.MiddleLeft);
        TWUI.Place(title.rectTransform, 0.15f, 0.91f, 0.65f, 0.99f);
        hostLabel = TWUI.Label(Root, "", 24, TWUI.TextDim, TextAnchor.MiddleRight);
        TWUI.Place(hostLabel.rectTransform, 0.6f, 0.92f, 0.985f, 0.98f);

        // ---- 左：マップと設定 ----
        var left = TWUI.Panel(Root, TWUI.Agar, true, "MapPanel");
        TWUI.Place(left.rectTransform, 0.015f, 0.03f, 0.37f, 0.9f);
        var lp = TWUI.Rect("Body", left.transform); TWUI.Fill(lp, 18);
        var frame = TWUI.Rect("PreviewFrame", lp); TWUI.Place(frame, 0, 0.62f, 1, 1);
        preview = new GameObject("Preview", typeof(RectTransform)).AddComponent<RawImage>();
        preview.transform.SetParent(frame, false);
        TWUI.Fill(preview.rectTransform);
        var arf = preview.gameObject.AddComponent<AspectRatioFitter>();
        arf.aspectMode = AspectRatioFitter.AspectMode.FitInParent; arf.aspectRatio = MapDef.FieldHalfWidth / MapDef.FieldHalfHeight;
        mapName = TWUI.Label(lp, "", 34, TWUI.Text, TextAnchor.MiddleLeft);
        TWUI.Place(mapName.rectTransform, 0, 0.53f, 1, 0.61f);
        timeLabel = TWUI.Label(lp, "", 24, TWUI.TextDim, TextAnchor.MiddleLeft);
        TWUI.Place(timeLabel.rectTransform, 0, 0.47f, 1, 0.53f);
        specLabel = TWUI.Label(lp, "", 24, TWUI.TextDim, TextAnchor.MiddleLeft);
        TWUI.Place(specLabel.rectTransform, 0, 0.42f, 1, 0.47f);
        hostControls = TWUI.Rect("HostControls", lp); TWUI.Place(hostControls, 0, 0, 1, 0.4f);

        // ---- 中：席 ----
        var mid = TWUI.Panel(Root, TWUI.Agar, true, "Seats");
        TWUI.Place(mid.rectTransform, 0.38f, 0.03f, 0.7f, 0.9f);
        var mp = TWUI.Rect("Body", mid.transform); TWUI.Fill(mp, 18);
        var sh = TWUI.Label(mp, "席", 26, TWUI.TextDim, TextAnchor.MiddleLeft);
        TWUI.Place(sh.rectTransform, 0, 0.94f, 1, 1);
        seatsBox = TWUI.Rect("SeatRows", mp); TWUI.Place(seatsBox, 0, 0.42f, 1, 0.94f);
        spectators = TWUI.Label(mp, "", 22, TWUI.TextDim, TextAnchor.UpperLeft);
        TWUI.Place(spectators.rectTransform, 0, 0.27f, 1, 0.41f);
        myControls = TWUI.Rect("MyControls", mp); TWUI.Place(myControls, 0, 0, 1, 0.26f);

        // ---- 右：チャット ----
        var right = TWUI.Panel(Root, TWUI.Agar, true, "Chat");
        TWUI.Place(right.rectTransform, 0.71f, 0.03f, 0.985f, 0.9f);
        var rp = TWUI.Rect("Body", right.transform); TWUI.Fill(rp, 16);
        var ch = TWUI.Label(rp, "チャット", 26, TWUI.TextDim, TextAnchor.MiddleLeft);
        TWUI.Place(ch.rectTransform, 0, 0.94f, 1, 1);
        var logBox = TWUI.Rect("LogBox", rp); TWUI.Place(logBox, 0, 0.12f, 1, 0.93f);
        logBox.gameObject.AddComponent<RectMask2D>();
        chatLog = TWUI.Label(logBox, "", 24, TWUI.Text, TextAnchor.LowerLeft);
        TWUI.Fill(chatLog.rectTransform);
        chatLog.resizeTextForBestFit = false;
        chatLog.verticalOverflow = VerticalWrapMode.Overflow;
        chatInput = TWUI.Input(rp, "メッセージ", 24);
        TWUI.Place((RectTransform)chatInput.transform, 0, 0, 0.74f, 0.1f);
        chatInput.characterLimit = TWProtocol.MaxChatLength;
        chatInput.onEndEdit.AddListener(s =>
        {
            if (UnityEngine.InputSystem.Keyboard.current != null &&
                (UnityEngine.InputSystem.Keyboard.current.enterKey.wasPressedThisFrame || UnityEngine.InputSystem.Keyboard.current.numpadEnterKey.wasPressedThisFrame)) SendChat();
        });
        var send = TWUI.Button(rp, "送信", SendChat, TWUI.Style.Normal, 24);
        TWUI.Place(send.Rect, 0.76f, 0, 1, 0.1f);

        lastMapJson = null;
        TWNet.Changed += Refresh;
        TWNet.ErrorReceived += OnError;
        TWNet.Kicked += OnKicked;
        TWNet.GameStarted += OnGameStarted;
        TWNet.Disconnected += OnDisconnected;
        Refresh();
    }

    public override void Exit()
    {
        TWNet.Changed -= Refresh;
        TWNet.ErrorReceived -= OnError;
        TWNet.Kicked -= OnKicked;
        TWNet.GameStarted -= OnGameStarted;
        TWNet.Disconnected -= OnDisconnected;
    }

    void OnError(string e) { App.Toast(e, 3f); }
    void OnKicked(string reason) { if (!string.IsNullOrEmpty(reason)) App.Toast(reason, 3f); App.Show(App.Lobby); }
    void OnDisconnected() { App.Toast("サーバーとの接続が切れました", 3f); App.Show(App.Lobby); }

    void OnGameStarted()
    {
        App.Game.LaunchOnline(TWNet.Match);
        App.Show(App.Game);
    }

    void SendChat()
    {
        var t = chatInput.text.Trim();
        if (t.Length == 0) return;
        TWNet.Send(new TWChat { Text = t });
        chatInput.text = "";
        chatInput.ActivateInputField();
    }

    public override void Tick()
    {
        if (TWNet.Room == null) { App.Show(App.Lobby); return; }
        // 試合中の部屋に観戦や再接続で入ったとき
        if (TWNet.Match != null && TWNet.Room.Value.Phase != (byte)TWRoomPhase.Waiting) OnGameStarted();
    }

    static ComProfile ProfileOf(TWSeatInfo s) { return new ComProfile(s.ComReact, s.ComGaze, s.ComMove).Clamped(); }
    static void SendCom(int seat, ComProfile p) { TWNet.Send(new TWSetCom { Seat = seat, Com = true, React = p.ReactTicks, Gaze = p.GazeTicks, Move = p.MoveCentis }); }

    void Refresh()
    {
        if (TWNet.Room == null || title == null) return;
        var r = TWNet.Room.Value;
        bool host = r.YouAreHost;
        title.text = r.Name + (r.Locked ? "　<size=26><color=#F2B544>合言葉あり</color></size>" : "");
        string hostName = "";
        foreach (var s in r.Seats) if (s.Host) hostName = s.Name;
        if (hostName.Length == 0) foreach (var sp in r.Spectators ?? new string[0]) if (sp.EndsWith("（部屋主）")) hostName = sp.Replace("（部屋主）", "");
        hostLabel.text = "部屋主：" + hostName + (host ? "（あなた）" : "");

        // マップ
        if (r.MapJson != lastMapJson)
        {
            lastMapJson = r.MapJson;
            string err;
            var map = MapJson.FromJson(r.MapJson, out err);
            if (map != null)
            {
                map.Id = "room/" + r.MapJson.GetHashCode();
                preview.texture = TWMapStore.Preview(map, 640);
                mapName.text = map.Name + "　<size=24><color=#8FA9A6>" + map.Players + "人</color></size>";
            }
        }
        timeLabel.text = "制限時間　" + (r.TimeLimitSec <= 0 ? "無制限" : (r.TimeLimitSec / 60) + "分");
        specLabel.text = r.AllowSpectators ? "観戦できます" : "観戦できません";

        TWUI.Clear(hostControls);
        if (host)
        {
            var pick = TWUI.Button(hostControls, "マップを変える", () => TWMapPicker.Open(m =>
                TWNet.Send(new TWRoomSettings { MapJson = MapJson.ToJson(m), TimeLimitSec = r.TimeLimitSec, AllowSpectators = r.AllowSpectators }), r.MapJson), TWUI.Style.Normal, 26);
            TWUI.Place(pick.Rect, 0, 0.72f, 1, 0.92f);
            int idx = System.Array.IndexOf(TWServer.TimeLimits, r.TimeLimitSec);
            var seg = TWUI.Segments(hostControls, new[] { "5分", "8分", "12分", "無制限" }, idx < 0 ? 1 : idx, i =>
                TWNet.Send(new TWRoomSettings { MapJson = "", TimeLimitSec = TWServer.TimeLimits[i], AllowSpectators = r.AllowSpectators }), 22);
            TWUI.Place((RectTransform)seg.Buttons[0].Rect.parent, 0, 0.46f, 1, 0.64f);
            var sseg = TWUI.Segments(hostControls, new[] { "観戦を許可", "観戦させない" }, r.AllowSpectators ? 0 : 1, i =>
                TWNet.Send(new TWRoomSettings { MapJson = "", TimeLimitSec = r.TimeLimitSec, AllowSpectators = i == 0 }), 22);
            TWUI.Place((RectTransform)sseg.Buttons[0].Rect.parent, 0, 0.22f, 1, 0.4f);
        }
        else
        {
            var w = TWUI.Label(hostControls, "マップと設定は部屋主が決めます", 22, TWUI.TextDim, TextAnchor.UpperLeft);
            TWUI.Place(w.rectTransform, 0, 0.6f, 1, 0.9f);
        }

        // 席
        TWUI.Clear(seatsBox);
        int n = r.Seats.Length;
        float rowH = 1f / 4f;
        for (int i = 0; i < n; i++)
        {
            var s = r.Seats[i];
            int seat = i;
            var row = TWUI.Panel(seatsBox, i == r.YourSeat ? new Color(TWUI.Lime.r, TWUI.Lime.g, TWUI.Lime.b, 0.12f) : TWUI.AgarLight, false, "Seat" + i);
            TWUI.Place(row.rectTransform, 0, 1 - (i + 1) * rowH, 1, 1 - i * rowH, 4);
            var dot = TWUI.Dot(row.transform, TWBoardView.PlayerColors[i]);
            TWUI.Square(dot.rectTransform, 0.02f, 0.5f, 28);
            string label;
            if (s.Com) label = "COM　<size=22><color=#F2B544>" + ProfileOf(s).Name + "</color></size>";
            else if (!s.Occupied) label = "<color=#8FA9A6>空き</color>";
            else label = s.Name + (s.Host ? "　<size=20><color=#F2B544>部屋主</color></size>" : "") + (i == r.YourSeat ? "　<size=20><color=#9BE15D>あなた</color></size>" : "");
            var lbl = TWUI.Label(row.transform, label, 28, TWUI.Text, TextAnchor.MiddleLeft);
            TWUI.Place(lbl.rectTransform, 0.02f, 0.5f, 0.62f, 1);
            lbl.rectTransform.offsetMin = new Vector2(44, 0);
            string st = s.Com ? "準備OK" : !s.Occupied ? "" : s.Host ? "" : s.Ready ? "<color=#9BE15D>準備OK</color>" : "準備中";
            var stl = TWUI.Label(row.transform, st, 22, TWUI.TextDim, TextAnchor.MiddleLeft);
            TWUI.Place(stl.rectTransform, 0.02f, 0, 0.62f, 0.5f);
            stl.rectTransform.offsetMin = new Vector2(44, 0);

            if (!s.Occupied && !s.Com)
            {
                if (i != r.YourSeat)
                {
                    var sit = TWUI.Button(row.transform, "ここに座る", () => TWNet.Send(new TWTakeSeat { Seat = seat }), TWUI.Style.Normal, 20);
                    TWUI.Place(sit.Rect, 0.63f, 0.52f, 0.98f, 0.92f);
                }
                if (host)
                {
                    var com = TWUI.Button(row.transform, "COMを入れる", () => SendCom(seat, TWStageSelectScreen.LastProfile), TWUI.Style.Normal, 20);
                    TWUI.Place(com.Rect, 0.63f, 0.08f, 0.98f, 0.48f);
                }
            }
            else if (s.Com && host)
            {
                // 席ごとのCOMの強さ（押すとドロップダウン）
                var rowRect = row.rectTransform;
                var cur = ProfileOf(s);
                var lv = TWUI.Button(row.transform, "強さ：" + cur.Name + " ▼", () => TWComMenu.Open(rowRect, cur, pf =>
                {
                    TWStageSelectScreen.LastProfile = pf;
                    SendCom(seat, pf);
                }), TWUI.Style.Normal, 20);
                TWUI.Place(lv.Rect, 0.5f, 0.52f, 0.98f, 0.92f);
                var rm = TWUI.Button(row.transform, "COMを外す", () => TWNet.Send(new TWSetCom { Seat = seat, Com = false }), TWUI.Style.Quiet, 20);
                TWUI.Place(rm.Rect, 0.63f, 0.08f, 0.98f, 0.48f);
            }
        }

        var specs = r.Spectators ?? new string[0];
        spectators.text = "観戦：" + (specs.Length == 0 ? "なし" : string.Join("、", specs));

        // 自分の操作
        TWUI.Clear(myControls);
        bool seated = r.YourSeat >= 0;
        if (seated && r.AllowSpectators)
        {
            var toSpec = TWUI.Button(myControls, "観戦に回る", () => TWNet.Send(new TWTakeSeat { Seat = -1 }), TWUI.Style.Quiet, 22);
            TWUI.Place(toSpec.Rect, 0, 0.62f, 0.4f, 0.95f);
        }
        if (host)
        {
            bool canStart = true;
            foreach (var s in r.Seats) if (!s.Com && (!s.Occupied || !s.Connected || (!s.Host && !s.Ready))) canStart = false;
            startBtn = TWUI.Button(myControls, canStart ? "対戦を始める" : "全員がそろうのを待っています", () => TWNet.Send(new TWStartGame()), TWUI.Style.Primary, 30);
            TWUI.Place(startBtn.Rect, 0, 0.05f, 1, 0.55f);
            startBtn.SetInteractable(canStart);
        }
        else if (seated)
        {
            bool ready = r.Seats[r.YourSeat].Ready;
            readyBtn = TWUI.Button(myControls, ready ? "準備OK（取り消す）" : "準備OK", () => TWNet.Send(new TWSetReady { Ready = !ready }), ready ? TWUI.Style.Normal : TWUI.Style.Primary, 30);
            TWUI.Place(readyBtn.Rect, 0, 0.05f, 1, 0.55f);
        }
        else
        {
            var w = TWUI.Label(myControls, "観戦で待機中。空いている席があれば座れます", 22, TWUI.TextDim, TextAnchor.MiddleCenter);
            TWUI.Place(w.rectTransform, 0, 0.05f, 1, 0.55f);
        }

        // チャット
        var sb = new System.Text.StringBuilder();
        int from = Mathf.Max(0, TWNet.Chat.Count - 18);
        for (int i = from; i < TWNet.Chat.Count; i++)
        {
            var l = TWNet.Chat[i];
            if (l.System) sb.Append("<color=#8FA9A6>").Append(Escape(l.Text)).Append("</color>\n");
            else
            {
                string col = l.Seat >= 0 && l.Seat < TWBoardView.PlayerColors.Length ? ColorUtility.ToHtmlStringRGB(TWBoardView.PlayerColors[l.Seat]) : "E6F0E2";
                sb.Append("<color=#").Append(col).Append(">").Append(Escape(l.From)).Append("</color>　").Append(Escape(l.Text)).Append('\n');
            }
        }
        chatLog.text = sb.ToString().TrimEnd();
    }

    /// <summary>リッチテキストのタグとして解釈されないようにする</summary>
    static string Escape(string s) { return (s ?? "").Replace("<", "＜").Replace(">", "＞"); }
}
