// ステージ選択。基本マップ／マイマップを人数で絞り込み、席（あなた・COM）を決めて対戦を始める
using System.Collections.Generic;
using TW.Sim;
using UnityEngine;
using UnityEngine.UI;

public class TWStageSelectScreen : TWScreen
{
    readonly TWMapBrowser browser = new TWMapBrowser();
    MapDef selected;
    int localSeat;              // -1 = 観戦（全員COM）
    ComProfile[] comProfiles;   // 席ごとのCOMの強さ

    /// <summary>最後に決めたCOMの強さ（新しくCOMにした席の初期値）。次に開いたときも同じにする</summary>
    public static ComProfile LastProfile
    {
        get
        {
            var d = SimpleAI.Presets[SimpleAI.DefaultLevel];
            var parts = PlayerPrefs.GetString("TW.ComProfile", "").Split(',');
            int r, g, m;
            if (parts.Length == 3 && int.TryParse(parts[0], out r) && int.TryParse(parts[1], out g) && int.TryParse(parts[2], out m)) return new ComProfile(r, g, m).Clamped();
            return d;
        }
        set { var p = value.Clamped(); PlayerPrefs.SetString("TW.ComProfile", p.ReactTicks + "," + p.GazeTicks + "," + p.MoveCentis); PlayerPrefs.Save(); }
    }

    RectTransform detail;

    /// <summary>エディタで保存した直後などに、そのマップを選んだ状態で開く</summary>
    public void Focus(MapDef m)
    {
        browser.Focus(m);
        selected = m;
    }

    protected override void OnEnter()
    {
        var bg = Root.gameObject.AddComponent<Image>(); bg.color = TWUI.Ink;

        // ---- 上部 ----
        var back = TWUI.Button(Root, "← 戻る", () => App.Show(App.Home), TWUI.Style.Quiet, 28);
        TWUI.Place(back.Rect, 0.015f, 0.915f, 0.11f, 0.985f);
        var title = TWUI.Label(Root, "ステージ選択", 48, TWUI.Text, TextAnchor.MiddleLeft);
        TWUI.Place(title.rectTransform, 0.12f, 0.91f, 0.45f, 0.99f);

        // ---- 左：絞り込みと一覧 ----
        var filters = TWUI.Rect("Filters", Root); TWUI.Place(filters, 0.02f, 0.83f, 0.62f, 0.9f);
        var list = TWUI.Rect("List", Root); TWUI.Place(list, 0.02f, 0.11f, 0.62f, 0.81f);

        var import = TWUI.Button(Root, "JSONを読み込む", OpenImport, TWUI.Style.Normal, 28);
        TWUI.Place(import.Rect, 0.02f, 0.025f, 0.2f, 0.09f);
        var newMap = TWUI.Button(Root, "新しいマップを作る", () => { App.Editor.OpenNew(); App.Show(App.Editor); }, TWUI.Style.Normal, 28);
        TWUI.Place(newMap.Rect, 0.21f, 0.025f, 0.39f, 0.09f);

        // ---- 右：選んだマップ ----
        var dp = TWUI.Panel(Root, TWUI.Agar, true, "Detail");
        TWUI.Place(dp.rectTransform, 0.64f, 0.025f, 0.985f, 0.9f);
        detail = TWUI.Rect("DetailBody", dp.transform); TWUI.Fill(detail, 22);

        browser.SelectionChanged = m =>
        {
            if (m == null || selected == null || selected.Id != m.Id || selected.Players != m.Players) localSeat = 0;
            selected = m;
            RefreshDetail();
        };
        browser.Build(filters, list);
    }

    void RefreshDetail()
    {
        TWUI.Clear(detail);
        if (selected == null)
        {
            var none = TWUI.Label(detail, "マップを選んでください", 30, TWUI.TextDim, TextAnchor.MiddleCenter);
            TWUI.Fill(none.rectTransform);
            return;
        }
        var m = selected;
        if (comProfiles == null) { comProfiles = new ComProfile[4]; for (int k = 0; k < comProfiles.Length; k++) comProfiles[k] = LastProfile; }

        // AspectRatioFitter は親いっぱいに広がるので、上部の枠の中に入れる
        var frame = TWUI.Rect("PreviewFrame", detail); TWUI.Place(frame, 0, 0.66f, 1, 1);
        var img = new GameObject("BigPreview", typeof(RectTransform)).AddComponent<RawImage>();
        img.transform.SetParent(frame, false);
        img.texture = TWMapStore.Preview(m, 640);
        TWUI.Fill(img.rectTransform);
        img.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        img.GetComponent<AspectRatioFitter>().aspectRatio = MapDef.FieldHalfWidth / MapDef.FieldHalfHeight;

        var name = TWUI.Label(detail, m.Name, 44, TWUI.Text, TextAnchor.MiddleLeft);
        TWUI.Place(name.rectTransform, 0, 0.585f, 1, 0.65f);
        int neutral = 0; foreach (var c in m.Cells) if (c.Owner < 0) neutral++;
        var info = TWUI.Label(detail, m.Players + "人用　細胞 " + m.Cells.Count + "（中立 " + neutral + "）　障害物 " + m.Obstacles.Count +
            (string.IsNullOrEmpty(m.Author) ? "" : "　作者 " + m.Author), 24, TWUI.TextDim, TextAnchor.MiddleLeft);
        TWUI.Place(info.rectTransform, 0, 0.545f, 1, 0.585f);

        // 席
        var seatsTitle = TWUI.Label(detail, "席", 26, TWUI.TextDim, TextAnchor.MiddleLeft);
        TWUI.Place(seatsTitle.rectTransform, 0, 0.5f, 0.3f, 0.54f);
        float rowH = 0.052f, gap = 0.006f, seatTop = 0.5f;
        for (int p = 0; p < m.Players; p++)
        {
            int seat = p;
            float top = seatTop - p * (rowH + gap);
            var row = TWUI.Rect("Seat" + p, detail); TWUI.Place(row, 0, top - rowH, 1, top);
            var dot = TWUI.Dot(row, TWBoardView.PlayerColors[p]);
            TWUI.Place(dot.rectTransform, 0, 0.15f, 0.06f, 0.85f);
            dot.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;
            var lbl = TWUI.Label(row, "P" + (p + 1) + "　" + TWBoardView.PlayerColorNames[p], 28, TWUI.Text, TextAnchor.MiddleLeft);
            TWUI.Place(lbl.rectTransform, 0.08f, 0, 0.29f, 1);
            // あなた／COM。COMの席でもう一度COMを押すと強さの設定が開く
            bool wasCom = localSeat != p;
            TWUI.Segmented seg = null;
            seg = TWUI.Segments(row, new[] { "あなた", "COM：" + comProfiles[p].Name + " ▼" }, localSeat == p ? 0 : 1, i =>
            {
                if (i == 0) { localSeat = seat; RefreshDetail(); return; }
                if (!wasCom) { localSeat = -1; RefreshDetail(); return; }
                var comBtn = seg.Buttons[1];
                TWComMenu.Open(row, comProfiles[seat], pf =>
                {
                    comProfiles[seat] = pf;
                    LastProfile = pf;
                    comBtn.Label.text = "COM：" + pf.Name + " ▼";
                });
            }, 22);
            TWUI.Place((RectTransform)seg.Buttons[0].Rect.parent, 0.3f, 0.05f, 1, 0.95f);
            ((RectTransform)seg.Buttons[0].Rect.parent).GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = true;
        }
        var watch = TWUI.Label(detail, localSeat < 0 ? "全員COM：観戦します" : "", 22, TWUI.Amber, TextAnchor.MiddleRight);
        TWUI.Place(watch.rectTransform, 0.3f, 0.5f, 1, 0.54f);

        // 操作
        var errs = MapValidator.Validate(m);
        var start = TWUI.Button(detail, errs.Count == 0 ? (localSeat < 0 ? "観戦を始める" : "対戦開始") : "このマップには問題があります",
            () => StartGame(), TWUI.Style.Primary, 36);
        TWUI.Place(start.Rect, 0, 0.095f, 1, 0.19f);
        start.SetInteractable(errs.Count == 0);

        if (m.BuiltIn)
        {
            var dup = TWUI.Button(detail, "複製して編集", () => { var c = m.Clone(); c.Id = ""; c.BuiltIn = false; c.Author = ""; c.Name = m.Name + "の改造"; App.Editor.Open(c); App.Show(App.Editor); }, TWUI.Style.Normal, 26);
            TWUI.Place(dup.Rect, 0, 0, 0.49f, 0.08f);
            var exp = TWUI.Button(detail, "JSONを書き出す", () => TWWebFile.Download(TWMapStore.FileNameFor(m), MapJson.ToJson(m)), TWUI.Style.Normal, 26);
            TWUI.Place(exp.Rect, 0.51f, 0, 1, 0.08f);
        }
        else
        {
            var edit = TWUI.Button(detail, "編集", () => { App.Editor.Open(m.Clone()); App.Show(App.Editor); }, TWUI.Style.Normal, 26);
            TWUI.Place(edit.Rect, 0, 0, 0.32f, 0.08f);
            var exp = TWUI.Button(detail, "書き出す", () => TWWebFile.Download(TWMapStore.FileNameFor(m), MapJson.ToJson(m)), TWUI.Style.Normal, 26);
            TWUI.Place(exp.Rect, 0.34f, 0, 0.66f, 0.08f);
            var del = TWUI.Button(detail, "削除", () => App.Confirm("「" + m.Name + "」を削除しますか？\n元に戻せません。", "削除する", () =>
            {
                TWMapStore.Delete(m.Id);
                selected = null;
                browser.Refresh();
                App.Toast("削除しました");
            }, true), TWUI.Style.Danger, 26);
            TWUI.Place(del.Rect, 0.68f, 0, 1, 0.08f);
        }
    }

    void StartGame()
    {
        if (selected == null) return;
        var map = selected;
        App.Game.Launch(map, localSeat, () => { Focus(map); App.Show(App.StageSelect); }, "ステージ選択へ", (ComProfile[])comProfiles.Clone());
        App.Show(App.Game);
    }

    // ---------------- JSON の読み込み ----------------

    void OpenImport()
    {
        GameObject h;
        var p = App.Modal(0.2f, 0.12f, 0.8f, 0.88f, out h);
        var t = TWUI.Label(p, "JSONを読み込む", 40, TWUI.Text, TextAnchor.MiddleLeft);
        TWUI.Place(t.rectTransform, 0.04f, 0.88f, 0.96f, 0.97f);
        var note = TWUI.Label(p, "ファイルを選ぶか、下の欄にJSONを貼り付けてください。読み込んだマップはマイマップに保存されます。", 24, TWUI.TextDim, TextAnchor.MiddleLeft);
        TWUI.Place(note.rectTransform, 0.04f, 0.8f, 0.96f, 0.88f);
        var input = TWUI.Input(p, "{ \"format\": \"tentacle-wars-map\", ... }", 22, true);
        TWUI.Place((RectTransform)input.transform, 0.04f, 0.26f, 0.96f, 0.78f);
        var err = TWUI.Label(p, "", 24, TWUI.Danger, TextAnchor.UpperLeft);
        TWUI.Place(err.rectTransform, 0.04f, 0.14f, 0.96f, 0.25f);

        System.Action<string> tryImport = text =>
        {
            string e;
            var m = MapJson.FromJson(text, out e);
            if (m == null) { err.text = e; return; }
            var problems = MapValidator.Validate(m);
            if (problems.Count > 0)
            {
                err.text = "このマップには問題があります：" + problems[0] + (problems.Count > 1 ? "（ほか" + (problems.Count - 1) + "件）" : "") +
                           "\nエディタで開いて直すこともできます。";
                return;
            }
            m.Id = "";
            TWMapStore.Save(m);
            TWUI.Destroy(h);
            Focus(m);
            App.Show(App.StageSelect);
            App.Toast("「" + m.Name + "」をマイマップに追加しました");
        };

        var file = TWUI.Button(p, "ファイルを選ぶ", null, TWUI.Style.Normal, 28);
        TWUI.Place(file.Rect, 0.04f, 0.03f, 0.3f, 0.12f);
        TWUI.OnPointerDown(file.Button, () => TWWebFile.ArmOpen(text => { input.text = text; tryImport(text); }));
        var openInEditor = TWUI.Button(p, "エディタで開く", () =>
        {
            string e;
            var m = MapJson.FromJson(input.text, out e);
            if (m == null) { err.text = e; return; }
            m.Id = ""; TWUI.Destroy(h);
            App.Editor.Open(m); App.Show(App.Editor);
        }, TWUI.Style.Normal, 28);
        TWUI.Place(openInEditor.Rect, 0.32f, 0.03f, 0.56f, 0.12f);
        var cancel = TWUI.Button(p, "閉じる", () => TWUI.Destroy(h), TWUI.Style.Quiet, 28);
        TWUI.Place(cancel.Rect, 0.58f, 0.03f, 0.72f, 0.12f);
        var ok = TWUI.Button(p, "読み込む", () => tryImport(input.text), TWUI.Style.Primary, 30);
        TWUI.Place(ok.Rect, 0.74f, 0.03f, 0.96f, 0.12f);
    }
}
