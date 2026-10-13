// オンラインのロビー：名前を決め、部屋を作るか、一覧から入る／観戦する
using System;
using System.Collections.Generic;
using TW.Sim;
using UnityEngine;
using UnityEngine.UI;

public class TWLobbyScreen : TWScreen
{
    Text status;
    InputField nameField;
    RectTransform listContent, rejoinBox;
    float lastListRefresh;

    protected override void OnEnter()
    {
        var bg = Root.gameObject.AddComponent<Image>(); bg.color = TWUI.Ink;

        var back = TWUI.Button(Root, "← 戻る", () => { TWNet.Disconnect(); App.Show(App.Home); }, TWUI.Style.Quiet, 28);
        TWUI.Place(back.Rect, 0.015f, 0.915f, 0.11f, 0.985f);
        var title = TWUI.Label(Root, "オンライン対戦", 48, TWUI.Text, TextAnchor.MiddleLeft);
        TWUI.Place(title.rectTransform, 0.12f, 0.91f, 0.5f, 0.99f);
        status = TWUI.Label(Root, "", 24, TWUI.TextDim, TextAnchor.MiddleRight);
        TWUI.Place(status.rectTransform, 0.5f, 0.92f, 0.985f, 0.98f);

        // ---- 左：自分 ----
        var left = TWUI.Panel(Root, TWUI.Agar, true, "Me");
        TWUI.Place(left.rectTransform, 0.02f, 0.04f, 0.3f, 0.89f);
        var lp = TWUI.Rect("Body", left.transform); TWUI.Fill(lp, 22);
        var nl = TWUI.Label(lp, "あなたの名前", 24, TWUI.TextDim, TextAnchor.LowerLeft);
        TWUI.Place(nl.rectTransform, 0, 0.9f, 1, 0.97f);
        nameField = TWUI.Input(lp, "名前（" + TWProtocol.MaxNameLength + "文字まで）", 30);
        TWUI.Place((RectTransform)nameField.transform, 0, 0.8f, 1, 0.89f);
        nameField.characterLimit = TWProtocol.MaxNameLength;
        nameField.text = TWNet.Name;
        nameField.onEndEdit.AddListener(s =>
        {
            s = s.Trim();
            if (s == TWNet.Name) return;
            TWNet.Name = s;
            // 名前はつなぎ直したときに伝わる
            if (TWNet.Connected && TWNet.Room == null) { TWNet.Disconnect(); App.StartCoroutineSafe(Reconnect()); }
        });

        var create = TWUI.Button(lp, "部屋を作る", OpenCreate, TWUI.Style.Primary, 34);
        TWUI.Place(create.Rect, 0, 0.62f, 1, 0.74f);

        rejoinBox = TWUI.Rect("Rejoin", lp); TWUI.Place(rejoinBox, 0, 0.4f, 1, 0.58f);

        var note = TWUI.Label(lp, "試合中に切断しても、" + (int)TWServer.AwayGraceSeconds + "秒はそのまま待ちます。その後はCOMが代わりに動かします。同じブラウザで入り直せば席に戻れます。", 22, TWUI.TextDim, TextAnchor.UpperLeft);
        TWUI.Place(note.rectTransform, 0, 0.12f, 1, 0.38f);
        note.resizeTextForBestFit = false;

        if (Application.isEditor || Debug.isDebugBuild)
        {
            var host = TWUI.Button(lp, "この端末でサーバーも動かす（開発用）", () => { TWNet.StartHost(); }, TWUI.Style.Quiet, 22);
            TWUI.Place(host.Rect, 0, 0, 1, 0.08f);
        }

        // ---- 右：部屋の一覧 ----
        var right = TWUI.Panel(Root, TWUI.Agar, true, "Rooms");
        TWUI.Place(right.rectTransform, 0.32f, 0.04f, 0.98f, 0.89f);
        var head = TWUI.Label(right.transform, "部屋", 28, TWUI.TextDim, TextAnchor.MiddleLeft);
        TWUI.Place(head.rectTransform, 0.03f, 0.91f, 0.5f, 0.98f);
        var scroll = TWUI.Scroll(right.transform, out listContent, false, Vector2.zero, 10);
        TWUI.Place((RectTransform)scroll.transform, 0.01f, 0.02f, 0.99f, 0.9f);

        TWNet.Changed += Refresh;
        TWNet.ErrorReceived += OnError;
        TWNet.Disconnected += OnDisconnected;
        if (!TWNet.Connected) TWNet.Connect();
        Refresh();
    }

    System.Collections.IEnumerator Reconnect()
    {
        yield return null;
        yield return null;
        TWNet.Connect();
    }

    public override void Exit()
    {
        TWNet.Changed -= Refresh;
        TWNet.ErrorReceived -= OnError;
        TWNet.Disconnected -= OnDisconnected;
    }

    void OnError(string e) { App.Toast(e, 3f); }
    void OnDisconnected() { Refresh(); }

    public override void Tick()
    {
        if (TWNet.Room != null) { App.Show(App.Room); return; }
        // つながらないときの表示
        string s;
        if (TWNet.Connected && TWNet.Welcomed) s = "<color=#9BE15D>●</color> 接続中";
        else if (Mirror.NetworkClient.active) s = "接続しています…";
        else s = "<color=#F06A5E>●</color> 未接続　<color=#8FA9A6>（自動で再接続します）</color>";
        status.text = s;
        if (!Mirror.NetworkClient.active && Time.unscaledTime - lastListRefresh > 3f)
        {
            lastListRefresh = Time.unscaledTime;
            TWNet.Connect();
        }
    }

    void Refresh()
    {
        if (listContent == null) return;
        foreach (Transform c in listContent) TWUI.Destroy(c.gameObject);
        foreach (Transform c in rejoinBox) TWUI.Destroy(c.gameObject);

        if (TWNet.Welcomed && TWNet.Welcome.RejoinRoom > 0)
        {
            int id = TWNet.Welcome.RejoinRoom;
            var rb = TWUI.Button(rejoinBox, "「" + TWNet.Welcome.RejoinRoomName + "」の試合に戻る", () =>
                TWNet.Send(new TWJoinRoom { RoomId = id }), TWUI.Style.Normal, 28);
            TWUI.Fill(rb.Rect);
            rb.SetSelected(true);
        }

        var rooms = TWNet.Rooms;
        if (rooms.Length == 0)
        {
            var e = TWUI.Label(listContent, TWNet.Connected ? "まだ部屋がありません。左の「部屋を作る」から始めましょう。" : "サーバーにつながっていません", 28, TWUI.TextDim, TextAnchor.MiddleCenter);
            e.gameObject.AddComponent<LayoutElement>().preferredHeight = 200;
            return;
        }
        foreach (var r in rooms) AddRow(r);
    }

    void AddRow(TWRoomInfo r)
    {
        var row = TWUI.Panel(listContent, TWUI.AgarLight, false, "Room" + r.Id);
        row.gameObject.AddComponent<LayoutElement>().preferredHeight = 96;
        bool playing = r.Phase != (byte)TWRoomPhase.Waiting;
        var name = TWUI.Label(row.transform, r.Name + (r.Locked ? "　<color=#F2B544>合言葉あり</color>" : ""), 30, TWUI.Text, TextAnchor.MiddleLeft);
        TWUI.Place(name.rectTransform, 0.02f, 0.5f, 0.55f, 0.95f);
        var info = TWUI.Label(row.transform, r.MapName + "　" + r.Players + "/" + r.Seats + "人　" + (playing ? "<color=#F2B544>試合中</color>" : "待機中") +
            (r.Spectators > 0 ? "　観戦 " + r.Spectators : ""), 22, TWUI.TextDim, TextAnchor.MiddleLeft);
        TWUI.Place(info.rectTransform, 0.02f, 0.05f, 0.6f, 0.5f);

        int id = r.Id; bool locked = r.Locked;
        if (!playing)
        {
            var join = TWUI.Button(row.transform, "入る", () => Join(id, locked, false), TWUI.Style.Primary, 26);
            TWUI.Place(join.Rect, 0.62f, 0.18f, 0.8f, 0.82f);
            join.SetInteractable(r.Players < r.Seats);
        }
        if (r.AllowSpectators)
        {
            var watch = TWUI.Button(row.transform, "観戦", () => Join(id, locked, true), TWUI.Style.Normal, 26);
            TWUI.Place(watch.Rect, 0.82f, 0.18f, 0.98f, 0.82f);
        }
    }

    void Join(int id, bool locked, bool spectate)
    {
        if (!locked) { TWNet.Send(new TWJoinRoom { RoomId = id, Spectate = spectate }); return; }
        GameObject h;
        var p = App.Modal(0.34f, 0.36f, 0.66f, 0.64f, out h);
        var t = TWUI.Label(p, "合言葉", 32, TWUI.Text, TextAnchor.MiddleLeft);
        TWUI.Place(t.rectTransform, 0.06f, 0.75f, 0.94f, 0.94f);
        var input = TWUI.Input(p, "合言葉を入力", 30);
        TWUI.Place((RectTransform)input.transform, 0.06f, 0.45f, 0.94f, 0.7f);
        var cancel = TWUI.Button(p, "やめる", () => TWUI.Destroy(h), TWUI.Style.Quiet, 26);
        TWUI.Place(cancel.Rect, 0.06f, 0.08f, 0.45f, 0.32f);
        var ok = TWUI.Button(p, spectate ? "観戦する" : "入る", () =>
        {
            TWNet.Send(new TWJoinRoom { RoomId = id, Password = input.text, Spectate = spectate });
            TWUI.Destroy(h);
        }, TWUI.Style.Primary, 28);
        TWUI.Place(ok.Rect, 0.55f, 0.08f, 0.94f, 0.32f);
    }

    // ---------------- 部屋を作る ----------------

    void OpenCreate()
    {
        if (!TWNet.Connected || !TWNet.Welcomed) { App.Toast("サーバーにつながっていません"); return; }
        GameObject h;
        var p = App.Modal(0.22f, 0.12f, 0.78f, 0.88f, out h);
        var t = TWUI.Label(p, "部屋を作る", 40, TWUI.Text, TextAnchor.MiddleLeft);
        TWUI.Place(t.rectTransform, 0.05f, 0.88f, 0.95f, 0.97f);

        var nl = TWUI.Label(p, "部屋の名前", 24, TWUI.TextDim, TextAnchor.LowerLeft); TWUI.Place(nl.rectTransform, 0.05f, 0.81f, 0.5f, 0.86f);
        var name = TWUI.Input(p, (string.IsNullOrEmpty(TWNet.Name) ? "ゲスト" : TWNet.Name) + "の部屋", 28);
        TWUI.Place((RectTransform)name.transform, 0.05f, 0.72f, 0.6f, 0.81f);
        name.characterLimit = TWProtocol.MaxRoomNameLength;
        var pl = TWUI.Label(p, "合言葉（空なら誰でも入れます）", 24, TWUI.TextDim, TextAnchor.LowerLeft); TWUI.Place(pl.rectTransform, 0.63f, 0.81f, 0.95f, 0.86f);
        var pass = TWUI.Input(p, "なし", 28);
        TWUI.Place((RectTransform)pass.transform, 0.63f, 0.72f, 0.95f, 0.81f);
        pass.characterLimit = 16;

        MapDef chosen = Maps.BuiltIn[0];
        var ml = TWUI.Label(p, "マップ", 24, TWUI.TextDim, TextAnchor.LowerLeft); TWUI.Place(ml.rectTransform, 0.05f, 0.64f, 0.5f, 0.69f);
        var mapLabel = TWUI.Label(p, "", 30, TWUI.Text, TextAnchor.MiddleLeft); TWUI.Place(mapLabel.rectTransform, 0.05f, 0.55f, 0.6f, 0.64f);
        Action showMap = () => mapLabel.text = chosen.Name + "　<color=#8FA9A6>" + chosen.Players + "人</color>";
        showMap();
        var pick = TWUI.Button(p, "マップを選ぶ", () => TWMapPicker.Open(m => { chosen = m; showMap(); }), TWUI.Style.Normal, 26);
        TWUI.Place(pick.Rect, 0.63f, 0.55f, 0.95f, 0.64f);

        var tl = TWUI.Label(p, "制限時間", 24, TWUI.TextDim, TextAnchor.LowerLeft); TWUI.Place(tl.rectTransform, 0.05f, 0.46f, 0.5f, 0.51f);
        int timeIdx = 1;
        var seg = TWUI.Segments(p, new[] { "5分", "8分", "12分", "無制限" }, timeIdx, i => timeIdx = i, 24);
        TWUI.Place((RectTransform)seg.Buttons[0].Rect.parent, 0.05f, 0.37f, 0.95f, 0.46f);

        bool spect = true;
        var sl = TWUI.Label(p, "観戦", 24, TWUI.TextDim, TextAnchor.LowerLeft); TWUI.Place(sl.rectTransform, 0.05f, 0.28f, 0.5f, 0.33f);
        var sseg = TWUI.Segments(p, new[] { "観戦を許可する", "観戦させない" }, 0, i => spect = i == 0, 24);
        TWUI.Place((RectTransform)sseg.Buttons[0].Rect.parent, 0.05f, 0.19f, 0.95f, 0.28f);

        var cancel = TWUI.Button(p, "やめる", () => TWUI.Destroy(h), TWUI.Style.Quiet, 28);
        TWUI.Place(cancel.Rect, 0.05f, 0.04f, 0.3f, 0.14f);
        var ok = TWUI.Button(p, "作成", () =>
        {
            TWNet.Send(new TWCreateRoom
            {
                Name = name.text, Password = pass.text, MapJson = MapJson.ToJson(chosen),
                TimeLimitSec = TWServer.TimeLimits[timeIdx], AllowSpectators = spect
            });
            TWUI.Destroy(h);
        }, TWUI.Style.Primary, 30);
        TWUI.Place(ok.Rect, 0.6f, 0.04f, 0.95f, 0.14f);
    }
}

/// <summary>マップを選ぶダイアログ（基本マップ＋マイマップ）</summary>
public static class TWMapPicker
{
    public static void Open(Action<MapDef> picked)
    {
        GameObject h;
        var p = TWApp.I.Modal(0.25f, 0.08f, 0.75f, 0.92f, out h);
        var t = TWUI.Label(p, "マップを選ぶ", 36, TWUI.Text, TextAnchor.MiddleLeft);
        TWUI.Place(t.rectTransform, 0.05f, 0.9f, 0.95f, 0.98f);
        RectTransform content;
        var scroll = TWUI.Scroll(p, out content, false, Vector2.zero, 8);
        TWUI.Place((RectTransform)scroll.transform, 0.03f, 0.12f, 0.97f, 0.89f);
        var all = new List<MapDef>(Maps.BuiltIn);
        foreach (var m in TWMapStore.LoadAll()) if (MapValidator.Validate(m).Count == 0) all.Add(m);
        foreach (var m in all)
        {
            var mm = m;
            var b = TWUI.Button(content, "", () => { TWUI.Destroy(h); picked(mm); }, TWUI.Style.Normal, 26, "Map");
            b.Rect.gameObject.AddComponent<LayoutElement>().preferredHeight = 120;
            TWUI.Destroy(b.Label.gameObject);
            var img = new GameObject("Preview", typeof(RectTransform)).AddComponent<RawImage>();
            img.transform.SetParent(b.Rect, false);
            img.texture = TWMapStore.Preview(m, 220);
            img.raycastTarget = false;
            TWUI.Place(img.rectTransform, 0.01f, 0.06f, 0.01f, 0.94f);
            img.rectTransform.offsetMax = new Vector2(176, img.rectTransform.offsetMax.y);
            var name = TWUI.Label(b.Rect, m.Name + "　<color=#8FA9A6>" + m.Players + "人" + (m.BuiltIn ? "" : "・マイマップ") + "</color>", 30, TWUI.Text, TextAnchor.MiddleLeft);
            TWUI.Place(name.rectTransform, 0.01f, 0, 0.98f, 1);
            name.rectTransform.offsetMin = new Vector2(196, 0);
        }
        var cancel = TWUI.Button(p, "閉じる", () => TWUI.Destroy(h), TWUI.Style.Quiet, 26);
        TWUI.Place(cancel.Rect, 0.35f, 0.02f, 0.65f, 0.1f);
    }
}
