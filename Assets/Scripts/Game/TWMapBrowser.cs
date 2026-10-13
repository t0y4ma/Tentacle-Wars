// マップの一覧（人数・基本/マイマップで絞り込み、カードで並べる）。ステージ選択とオンラインのマップ選択で共通
using System;
using System.Collections.Generic;
using TW.Sim;
using UnityEngine;
using UnityEngine.UI;

public class TWMapBrowser
{
    public int FilterPlayers;      // 0 = すべて
    public int Source;             // 0 = 基本, 1 = マイマップ
    public MapDef Selected { get; private set; }
    /// <summary>選択が変わったとき（null もありうる）</summary>
    public Action<MapDef> SelectionChanged;
    /// <summary>true なら問題のあるマイマップは出さない（オンラインで使えないため）</summary>
    public bool OnlyValid;

    RectTransform gridContent;
    GridLayoutGroup grid;
    float lastGridWidth;
    TWUI.Segmented segPlayers, segSource;
    readonly List<KeyValuePair<MapDef, TWUI.Btn>> cards = new List<KeyValuePair<MapDef, TWUI.Btn>>();

    /// <summary>filters に絞り込みのボタン、list に一覧を作る</summary>
    public void Build(RectTransform filters, RectTransform list)
    {
        cards.Clear();
        lastGridWidth = 0;
        segPlayers = TWUI.Segments(filters, new[] { "すべて", "2人", "3人", "4人" }, PlayersToIndex(FilterPlayers),
            i => { FilterPlayers = i == 0 ? 0 : i + 1; Refresh(); }, 26);
        TWUI.Place((RectTransform)segPlayers.Buttons[0].Rect.parent, 0, 0, 0.5f, 1);
        segSource = TWUI.Segments(filters, new[] { "基本マップ", "マイマップ" }, Source, i => { Source = i; Refresh(); }, 26);
        TWUI.Place((RectTransform)segSource.Buttons[0].Rect.parent, 0.53f, 0, 1, 1);

        var listPanel = TWUI.Panel(list, TWUI.Agar, true, "ListPanel");
        TWUI.Fill(listPanel.rectTransform);
        var scroll = TWUI.Scroll(listPanel.transform, out gridContent, true, new Vector2(340, 260), 18);
        TWUI.Fill((RectTransform)scroll.transform, 10);
        grid = gridContent.GetComponent<GridLayoutGroup>();
        // 一覧の幅が変わったらカードの大きさを測り直す
        gridContent.gameObject.AddComponent<TWMapBrowserDriver>().Browser = this;
        Refresh();
    }

    /// <summary>このマップを選んだ状態にする（一覧の種類と絞り込みも合わせる）</summary>
    public void Focus(MapDef m)
    {
        if (m == null) return;
        Source = m.BuiltIn ? 0 : 1;
        FilterPlayers = 0;
        Selected = m;
        // 一覧がまだ画面にあるときだけ作り直す（閉じた画面の古いボタンは触らない）
        if (gridContent != null && segPlayers != null) { segPlayers.Select(0, false); segSource.Select(Source, false); Refresh(); }
    }

    static int PlayersToIndex(int p) { return p == 0 ? 0 : p - 1; }

    List<MapDef> Listed()
    {
        var src = Source == 0 ? Maps.BuiltIn : TWMapStore.LoadAll();
        var r = new List<MapDef>();
        foreach (var m in src)
        {
            if (FilterPlayers != 0 && m.Players != FilterPlayers) continue;
            if (OnlyValid && !m.BuiltIn && MapValidator.Validate(m).Count > 0) continue;
            r.Add(m);
        }
        return r;
    }

    public void Refresh()
    {
        if (gridContent == null) return;
        TWUI.Clear(gridContent);
        cards.Clear();
        var maps = Listed();
        MapDef keep = null;
        foreach (var m in maps)
        {
            if (Selected != null && m.Id == Selected.Id) keep = m;
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
            var empty = TWUI.Label(gridContent, Source == 1 ? "マイマップはまだありません。\nエディタで作るか、JSONを読み込んでください。" : "該当するマップがありません。",
                28, TWUI.TextDim, TextAnchor.MiddleCenter, "Empty");
            empty.rectTransform.sizeDelta = new Vector2(700, 200);
        }
        lastGridWidth = 0;
        Tick();
        Select(keep ?? (maps.Count > 0 ? maps[0] : null));
        if (keep != null) ScrollTo(keep);
    }

    /// <summary>そのマップのカードが見える位置までスクロールする</summary>
    void ScrollTo(MapDef m)
    {
        var scroll = gridContent.GetComponentInParent<ScrollRect>();
        if (scroll == null) return;
        LayoutRebuilder.ForceRebuildLayoutImmediate(gridContent);
        foreach (var kv in cards)
        {
            if (kv.Key.Id != m.Id) continue;
            var card = kv.Value.Rect;
            float top = -card.anchoredPosition.y - card.rect.height * 0.5f - grid.padding.top;
            float max = Mathf.Max(0, gridContent.rect.height - scroll.viewport.rect.height);
            gridContent.anchoredPosition = new Vector2(gridContent.anchoredPosition.x, Mathf.Clamp(top, 0, max));
            break;
        }
    }

    public void Select(MapDef m)
    {
        Selected = m;
        foreach (var kv in cards)
        {
            bool on = m != null && kv.Key.Id == m.Id;
            kv.Value.SetSelected(on);
            // 縮小画像を見せるため、選択中でも背景は控えめに
            if (on) kv.Value.Back.color = new Color(TWUI.Amber.r, TWUI.Amber.g, TWUI.Amber.b, 0.85f);
        }
        if (SelectionChanged != null) SelectionChanged(m);
    }

    /// <summary>カードの大きさを一覧の実際の幅から決める（3列。狭ければ2列）</summary>
    public void Tick()
    {
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

/// <summary>一覧の幅の変化を毎フレーム見る</summary>
public class TWMapBrowserDriver : MonoBehaviour
{
    public TWMapBrowser Browser;
    void Update() { if (Browser != null) Browser.Tick(); }
}
