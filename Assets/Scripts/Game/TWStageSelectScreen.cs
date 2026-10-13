// ステージ選択。基本マップ／マイマップを人数で絞り込み、席（あなた・COM）を決めて対戦を始める
using System.Collections.Generic;
using TW.Sim;
using UnityEngine;
using UnityEngine.UI;

public class TWStageSelectScreen : TWScreen
{
    int filterPlayers;          // 0 = すべて
    int source;                 // 0 = 基本, 1 = マイマップ
    MapDef selected;
    int localSeat;              // -1 = 観戦（全員COM）

    RectTransform gridContent, detail;
    GridLayoutGroup grid;
    float lastGridWidth;
    readonly List<KeyValuePair<MapDef, TWUI.Btn>> cards = new List<KeyValuePair<MapDef, TWUI.Btn>>();

    /// <summary>エディタで保存した直後などに、そのマップを選んだ状態で開く</summary>
    public void Focus(MapDef m)
    {
        source = m.BuiltIn ? 0 : 1;
        filterPlayers = 0;
        selected = m;
    }

    protected override void OnEnter()
    {
        cards.Clear();
        lastGridWidth = 0;

        var bg = Root.gameObject.AddComponent<Image>(); bg.color = TWUI.Ink;

        // ---- 上部 ----
        var back = TWUI.Button(Root, "← 戻る", () => App.Show(App.Home), TWUI.Style.Quiet, 28);
        TWUI.Place(back.Rect, 0.015f, 0.915f, 0.11f, 0.985f);
        var title = TWUI.Label(Root, "ステージ選択", 48, TWUI.Text, TextAnchor.MiddleLeft);
        TWUI.Place(title.rectTransform, 0.12f, 0.91f, 0.45f, 0.99f);

        // ---- 左：絞り込みと一覧 ----
        var filters = TWUI.Rect("Filters", Root); TWUI.Place(filters, 0.02f, 0.83f, 0.62f, 0.9f);
        var segP = TWUI.Segments(filters, new[] { "すべて", "2人", "3人", "4人" }, PlayersToIndex(filterPlayers),
            i => { filterPlayers = i == 0 ? 0 : i + 1; RefreshList(); }, 26);
        TWUI.Place((RectTransform)segP.Buttons[0].Rect.parent, 0, 0, 0.5f, 1);
        var segS = TWUI.Segments(filters, new[] { "基本マップ", "マイマップ" }, source, i => { source = i; RefreshList(); }, 26);
        TWUI.Place((RectTransform)segS.Buttons[0].Rect.parent, 0.53f, 0, 1, 1);

        var listPanel = TWUI.Panel(Root, TWUI.Agar, true, "ListPanel");
        TWUI.Place(listPanel.rectTransform, 0.02f, 0.11f, 0.62f, 0.81f);
        var scroll = TWUI.Scroll(listPanel.transform, out gridContent, true, new Vector2(340, 260), 18);
        TWUI.Fill((RectTransform)scroll.transform, 10);
        grid = gridContent.GetComponent<GridLayoutGroup>();

        var import = TWUI.Button(Root, "JSONを読み込む", OpenImport, TWUI.Style.Normal, 28);
        TWUI.Place(import.Rect, 0.02f, 0.025f, 0.2f, 0.09f);
        var newMap = TWUI.Button(Root, "新しいマップを作る", () => { App.Editor.OpenNew(); App.Show(App.Editor); }, TWUI.Style.Normal, 28);
        TWUI.Place(newMap.Rect, 0.21f, 0.025f, 0.39f, 0.09f);

        // ---- 右：選んだマップ ----
        var dp = TWUI.Panel(Root, TWUI.Agar, true, "Detail");
        TWUI.Place(dp.rectTransform, 0.64f, 0.025f, 0.985f, 0.9f);
        detail = TWUI.Rect("DetailBody", dp.transform); TWUI.Fill(detail, 22);

        RefreshList();
    }

    static int PlayersToIndex(int p) { return p == 0 ? 0 : p - 1; }

    List<MapDef> Listed()
    {
        var src = source == 0 ? Maps.BuiltIn : TWMapStore.LoadAll();
        var r = new List<MapDef>();
        foreach (var m in src) if (filterPlayers == 0 || m.Players == filterPlayers) r.Add(m);
        return r;
    }

    void RefreshList()
    {
        foreach (Transform c in gridContent) TWUI.Destroy(c.gameObject);
        cards.Clear();
        var maps = Listed();
        MapDef keep = null;
        foreach (var m in maps)
        {
            if (selected != null && m.Id == selected.Id) keep = m;
            var mm = m;
            var b = TWUI.Button(gridContent, "", () => Select(mm), TWUI.Style.Normal, 24, "Card");
            TWUI.Destroy(b.Label.gameObject);
            var img = new GameObject("Preview", typeof(RectTransform)).AddComponent<RawImage>();
            img.transform.SetParent(b.Rect, false);
            img.texture = TWMapStore.Preview(m);
            img.raycastTarget = false;
            TWUI.Place(img.rectTransform, 0.04f, 0.27f, 0.96f, 0.95f);
            var name = TWUI.Label(b.Rect, m.Name, 28, TWUI.Text, TextAnchor.MiddleLeft, "Name");
            TWUI.Place(name.rectTransform, 0.05f, 0.02f, 0.62f, 0.25f);
            var info = TWUI.Label(b.Rect, m.Players + "人・細胞" + m.Cells.Count, 22, TWUI.TextDim, TextAnchor.MiddleRight, "Info");
            TWUI.Place(info.rectTransform, 0.63f, 0.02f, 0.95f, 0.25f);
            b.Label = name;   // 選択時の配色に合わせて名前の色を変える
            cards.Add(new KeyValuePair<MapDef, TWUI.Btn>(m, b));
        }
        if (maps.Count == 0)
        {
            var empty = TWUI.Label(gridContent, source == 1 ? "マイマップはまだありません。\nエディタで作るか、JSONを読み込んでください。" : "該当するマップがありません。",
                28, TWUI.TextDim, TextAnchor.MiddleCenter, "Empty");
            empty.rectTransform.sizeDelta = new Vector2(700, 200);
        }
        Select(keep ?? (maps.Count > 0 ? maps[0] : null));
        lastGridWidth = 0;   // カードの大きさを測り直す
    }

    void Select(MapDef m)
    {
        if (m == null || selected == null || selected.Id != m.Id || selected.Players != m.Players) localSeat = 0;
        selected = m;
        foreach (var kv in cards)
        {
            kv.Value.SetSelected(m != null && kv.Key.Id == m.Id);
            // 縮小画像を見せるため、選択中でも背景は控えめに
            if (m != null && kv.Key.Id == m.Id) kv.Value.Back.color = new Color(TWUI.Amber.r, TWUI.Amber.g, TWUI.Amber.b, 0.85f);
        }
        RefreshDetail();
    }

    void RefreshDetail()
    {
        foreach (Transform c in detail) TWUI.Destroy(c.gameObject);
        if (selected == null)
        {
            var none = TWUI.Label(detail, "マップを選んでください", 30, TWUI.TextDim, TextAnchor.MiddleCenter);
            TWUI.Fill(none.rectTransform);
            return;
        }
        var m = selected;

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
            TWUI.Place(lbl.rectTransform, 0.08f, 0, 0.4f, 1);
            var seg = TWUI.Segments(row, new[] { "あなた", "COM" }, localSeat == p ? 0 : 1, i =>
            {
                localSeat = i == 0 ? seat : (localSeat == seat ? -1 : localSeat);
                RefreshDetail();
            }, 24);
            TWUI.Place((RectTransform)seg.Buttons[0].Rect.parent, 0.42f, 0.05f, 1, 0.95f);
        }
        var watch = TWUI.Label(detail, localSeat < 0 ? "全員COM：観戦します" : "", 22, TWUI.Amber, TextAnchor.MiddleLeft);
        TWUI.Place(watch.rectTransform, 0, seatTop - 4 * (rowH + gap) - 0.04f, 1, seatTop - 4 * (rowH + gap));

        // 操作
        var errs = MapValidator.Validate(m);
        var start = TWUI.Button(detail, errs.Count == 0 ? (localSeat < 0 ? "観戦を始める" : "対戦開始") : "このマップには問題があります",
            () => StartGame(), TWUI.Style.Primary, 36);
        TWUI.Place(start.Rect, 0, 0.1f, 1, 0.2f);
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
                RefreshList();
                App.Toast("削除しました");
            }, true), TWUI.Style.Danger, 26);
            TWUI.Place(del.Rect, 0.68f, 0, 1, 0.08f);
        }
    }

    void StartGame()
    {
        if (selected == null) return;
        var map = selected;
        App.Game.Launch(map, localSeat, () => { Focus(map); App.Show(App.StageSelect); });
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

    public override void Tick()
    {
        // カードの大きさは一覧の実際の幅から決める（3列。狭ければ2列）
        if (grid == null) return;
        float w = ((RectTransform)grid.transform).rect.width;
        if (w < 50 || Mathf.Abs(w - lastGridWidth) < 2) return;
        lastGridWidth = w;
        int cols = w > 900 ? 3 : 2;
        float cw = (w - grid.padding.left - grid.padding.right - grid.spacing.x * (cols - 1)) / cols;
        float pvH = cw * 0.92f * MapDef.FieldHalfHeight / MapDef.FieldHalfWidth;
        grid.cellSize = new Vector2(Mathf.Floor(cw), Mathf.Floor(pvH / 0.68f));
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = cols;
        LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)grid.transform);
    }
}
