// マップエディタ。細胞・障害物を置いて動かし、対称配置・検証・試遊・保存・JSON入出力ができる
using System;
using System.Collections.Generic;
using TW.Sim;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class TWEditorScreen : TWScreen
{
    enum Tool { Select, Cell, Obstacle, Erase }
    enum Kind { None, Cell, Obstacle }

    // ---- モデル（画面を離れても残る） ----
    MapDef map;
    readonly List<int> cellGroup = new List<int>(), cellImage = new List<int>();   // 対称で一緒に動く仲間と、その中での番号
    readonly List<int> obsGroup = new List<int>(), obsImage = new List<int>();
    int nextGroup = 1;
    bool dirty;
    bool symmetry = true;
    float snap = 0.1f;
    Tool tool = Tool.Cell;
    int placeOwner = -1, placeEnergy = 20, placeMax = Balance.DefaultMaxEnergy;
    float placeRadius = 0.9f;
    Kind selKind = Kind.None;
    int selIndex = -1;
    bool fromStageSelect;
    readonly List<Snapshot> undo = new List<Snapshot>();

    class Snapshot { public string Json; public int[] CG, CI, OG, OI; public int Players; }

    // ---- 表示 ----
    TWBoardView board;
    Simulation sim;
    bool needBuild;
    InputField nameField;
    TWUI.Segmented playersSeg, snapSeg;
    TWUI.Btn symBtn;
    readonly TWUI.Btn[] toolBtns = new TWUI.Btn[4];
    readonly List<TWUI.Btn> ownerBtns = new List<TWUI.Btn>();
    Text energyVal, maxVal, radiusVal, editTarget, counts, status, savedLabel;
    TWUI.Btn playBtn;

    // ---- 入力 ----
    bool dragging;
    Vector2 pointerWorld, dragOffset;
    bool pressedOnBoard;

    // ======================================================================
    // 開く
    // ======================================================================

    public void OpenNew()
    {
        var m = new MapDef { Name = "新しいマップ", Players = 2 };
        LoadModel(m, true);
        // 最初から遊べる形にしておく：両者の本拠地を1つずつ
        PlaceCell(new Vector2(-7.5f, -3.5f), 0, 20, Balance.DefaultMaxEnergy, true);
        PlaceCell(new Vector2(0f, 0f), -1, 40, Balance.DefaultMaxEnergy, true);
        dirty = false;
        undo.Clear();
        fromStageSelect = false;
    }

    public void Open(MapDef m)
    {
        LoadModel(m, false);
        fromStageSelect = true;
    }

    void LoadModel(MapDef m, bool sym)
    {
        map = m;
        cellGroup.Clear(); cellImage.Clear(); obsGroup.Clear(); obsImage.Clear();
        foreach (var c in m.Cells) { cellGroup.Add(nextGroup++); cellImage.Add(0); }
        foreach (var o in m.Obstacles) { obsGroup.Add(nextGroup++); obsImage.Add(0); }
        symmetry = sym || m.Cells.Count == 0;
        selKind = Kind.None; selIndex = -1;
        dirty = false;
        undo.Clear();
        needBuild = true;
    }

    // ======================================================================
    // 画面
    // ======================================================================

    protected override void OnEnter()
    {
        if (map == null) OpenNew();
        board = new TWBoardView("EditorBoard");
        board.Highlight = HighlightOf;
        needBuild = true;
        BuildUI();
        RefreshPanel();
    }

    public override void Exit()
    {
        if (board != null) board.Destroy();
        board = null;
        // 画面の部品は離れると壊されるので、参照も捨てる（次に開く前の OpenNew などで壊れた部品を触らないように）
        for (int i = 0; i < toolBtns.Length; i++) toolBtns[i] = null;
        ownerBtns.Clear();
        symBtn = null; playBtn = null; playersSeg = null; snapSeg = null; nameField = null;
    }

    void BuildUI()
    {
        // ---- 上部 ----
        var top = TWUI.Rect("Top", Root); TWUI.Place(top, 0, 0.915f, 1, 1);
        var topBg = top.gameObject.AddComponent<Image>(); topBg.color = new Color(TWUI.Ink.r, TWUI.Ink.g, TWUI.Ink.b, 0.92f);
        var back = TWUI.Button(top, "← 戻る", Back, TWUI.Style.Quiet, 26);
        TWUI.Place(back.Rect, 0.005f, 0.12f, 0.075f, 0.88f);
        nameField = TWUI.Input(top, "マップ名", 28);
        TWUI.Place((RectTransform)nameField.transform, 0.08f, 0.14f, 0.3f, 0.86f);
        nameField.characterLimit = MapValidator.MaxNameLength;
        nameField.text = map.Name;
        nameField.onEndEdit.AddListener(s => { if (s != map.Name) { PushUndo(); map.Name = s; Changed(false); } });

        var pl = TWUI.Label(top, "人数", 24, TWUI.TextDim, TextAnchor.MiddleRight);
        TWUI.Place(pl.rectTransform, 0.3f, 0.1f, 0.345f, 0.9f);
        playersSeg = TWUI.Segments(top, new[] { "2人", "3人", "4人" }, map.Players - 2, i => SetPlayers(i + 2), 24);
        TWUI.Place((RectTransform)playersSeg.Buttons[0].Rect.parent, 0.35f, 0.14f, 0.5f, 0.86f);
        symBtn = TWUI.Button(top, "", () => { symmetry = !symmetry; RefreshPanel(); }, TWUI.Style.Normal, 22);
        TWUI.Place(symBtn.Rect, 0.51f, 0.14f, 0.66f, 0.86f);
        var sl = TWUI.Label(top, "マス目", 24, TWUI.TextDim, TextAnchor.MiddleRight);
        TWUI.Place(sl.rectTransform, 0.66f, 0.1f, 0.715f, 0.9f);
        snapSeg = TWUI.Segments(top, new[] { "0.1", "0.5" }, snap > 0.3f ? 1 : 0, i => snap = i == 0 ? 0.1f : 0.5f, 22);
        TWUI.Place((RectTransform)snapSeg.Buttons[0].Rect.parent, 0.72f, 0.14f, 0.82f, 0.86f);
        var und = TWUI.Button(top, "元に戻す", Undo, TWUI.Style.Normal, 22);
        TWUI.Place(und.Rect, 0.83f, 0.14f, 0.91f, 0.86f);
        savedLabel = TWUI.Label(top, "", 22, TWUI.Amber, TextAnchor.MiddleCenter);
        TWUI.Place(savedLabel.rectTransform, 0.91f, 0.1f, 0.995f, 0.9f);

        // ---- 左パネル ----
        var left = TWUI.Panel(Root, TWUI.Agar, true, "Tools");
        TWUI.Place(left.rectTransform, 0.005f, 0.09f, 0.195f, 0.905f);
        var lp = TWUI.Rect("Body", left.transform); TWUI.Fill(lp, 16);

        Head(lp, "道具", 0.995f);
        string[] tl = { "選択・移動", "細胞を置く", "障害物を置く", "消す" };
        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            toolBtns[i] = TWUI.Button(lp, tl[i], () => { tool = (Tool)idx; RefreshPanel(); }, TWUI.Style.Normal, 22);
            float x0 = (i % 2) * 0.51f, y1 = 0.945f - (i / 2) * 0.078f;
            TWUI.Place(toolBtns[i].Rect, x0, y1 - 0.068f, x0 + 0.49f, y1);
        }

        Head(lp, "持ち主", 0.785f);
        ownerBtns.Clear();
        string[] on = { "中立", "P1", "P2", "P3", "P4" };
        for (int i = 0; i < 5; i++)
        {
            int owner = i - 1;
            var b = TWUI.Button(lp, on[i], () => SetOwner(owner), TWUI.Style.Normal, 22);
            float x0 = i * 0.2f;
            TWUI.Place(b.Rect, x0, 0.67f, x0 + 0.18f, 0.735f);
            ownerBtns.Add(b);
        }

        Head(lp, "エネルギー", 0.66f);
        energyVal = Stepper(lp, 0.545f, new[] { -10, -1, 1, 10 }, d => SetEnergy(d));
        Head(lp, "上限", 0.535f);
        maxVal = Stepper(lp, 0.42f, new[] { -50, -10, 10, 50 }, d => SetMax(d));
        Head(lp, "障害物の半径（0.1刻み）", 0.41f);
        radiusVal = Stepper(lp, 0.295f, new[] { -5, -1, 1, 5 }, d => SetRadius(d * 0.1f));

        editTarget = TWUI.Label(lp, "", 22, TWUI.Amber, TextAnchor.UpperLeft);
        TWUI.Place(editTarget.rectTransform, 0, 0.2f, 1, 0.285f);
        var del = TWUI.Button(lp, "選んだものを消す", () => { if (selKind != Kind.None) DeleteSelected(); }, TWUI.Style.Danger, 22);
        TWUI.Place(del.Rect, 0, 0.12f, 1, 0.19f);
        counts = TWUI.Label(lp, "", 22, TWUI.TextDim, TextAnchor.LowerLeft);
        TWUI.Place(counts.rectTransform, 0, 0, 1, 0.1f);

        // ---- 下部 ----
        var bottom = TWUI.Rect("Bottom", Root); TWUI.Place(bottom, 0.2f, 0.005f, 0.995f, 0.082f);
        status = TWUI.Label(bottom, "", 24, TWUI.Text, TextAnchor.MiddleLeft);
        TWUI.Place(status.rectTransform, 0, 0, 0.42f, 1);
        var nb = TWUI.Button(bottom, "新規", () => ConfirmDiscard(() => { OpenNew(); needBuild = true; App.Show(App.Editor); }), TWUI.Style.Quiet, 24);
        TWUI.Place(nb.Rect, 0.43f, 0.08f, 0.5f, 0.92f);
        var ib = TWUI.Button(bottom, "JSON読込", null, TWUI.Style.Normal, 24);
        TWUI.Place(ib.Rect, 0.51f, 0.08f, 0.62f, 0.92f);
        TWUI.OnPointerDown(ib.Button, () => TWWebFile.ArmOpen(ImportJson));
        var eb = TWUI.Button(bottom, "JSON書出", () => { Commit(); TWWebFile.Download(TWMapStore.FileNameFor(map), MapJson.ToJson(map)); }, TWUI.Style.Normal, 24);
        TWUI.Place(eb.Rect, 0.63f, 0.08f, 0.74f, 0.92f);
        var sb = TWUI.Button(bottom, "保存", Save, TWUI.Style.Normal, 26);
        TWUI.Place(sb.Rect, 0.75f, 0.08f, 0.86f, 0.92f);
        playBtn = TWUI.Button(bottom, "試遊", TestPlay, TWUI.Style.Primary, 28);
        TWUI.Place(playBtn.Rect, 0.87f, 0.08f, 1f, 0.92f);
    }

    static void Head(RectTransform parent, string text, float top)
    {
        var l = TWUI.Label(parent, text, 22, TWUI.TextDim, TextAnchor.LowerLeft);
        TWUI.Place(l.rectTransform, 0, top - 0.04f, 1, top + 0.005f);
    }

    Text Stepper(RectTransform parent, float y0, int[] deltas, Action<int> apply)
    {
        float h = 0.065f;
        string Fmt(int d) { return (d > 0 ? "+" : "−") + Mathf.Abs(d); }
        var b0 = TWUI.Button(parent, Fmt(deltas[0]), () => apply(deltas[0]), TWUI.Style.Normal, 20); TWUI.Place(b0.Rect, 0, y0, 0.18f, y0 + h);
        var b1 = TWUI.Button(parent, Fmt(deltas[1]), () => apply(deltas[1]), TWUI.Style.Normal, 20); TWUI.Place(b1.Rect, 0.2f, y0, 0.36f, y0 + h);
        var v = TWUI.Label(parent, "", 30, TWUI.Text, TextAnchor.MiddleCenter); TWUI.Place(v.rectTransform, 0.37f, y0, 0.63f, y0 + h);
        var b2 = TWUI.Button(parent, Fmt(deltas[2]), () => apply(deltas[2]), TWUI.Style.Normal, 20); TWUI.Place(b2.Rect, 0.64f, y0, 0.8f, y0 + h);
        var b3 = TWUI.Button(parent, Fmt(deltas[3]), () => apply(deltas[3]), TWUI.Style.Normal, 20); TWUI.Place(b3.Rect, 0.82f, y0, 1f, y0 + h);
        return v;
    }

    void RefreshPanel()
    {
        if (toolBtns[0] == null || toolBtns[0].Rect == null) return;   // 画面を開いていないとき（部品が無い・壊れている）
        for (int i = 0; i < 4; i++) toolBtns[i].SetSelected((int)tool == i);
        int curOwner = selKind == Kind.Cell ? map.Cells[selIndex].Owner : placeOwner;
        for (int i = 0; i < ownerBtns.Count; i++)
        {
            int owner = i - 1;
            var b = ownerBtns[i];
            b.SetInteractable(owner < map.Players);
            b.SetSelected(owner == curOwner);
            if (owner >= 0 && owner != curOwner && owner < map.Players)
            {
                var c = TWBoardView.PlayerColors[owner];
                b.Back.color = new Color(c.r, c.g, c.b, 0.3f); b.Label.color = c;
            }
        }
        if (playersSeg != null && playersSeg.Index != map.Players - 2) playersSeg.Select(map.Players - 2, false);
        symBtn.Label.text = symmetry ? "対称配置 ON（" + SymName() + "）" : "対称配置 OFF";
        symBtn.SetSelected(symmetry);

        if (selKind == Kind.Cell)
        {
            var c = map.Cells[selIndex];
            energyVal.text = c.Energy.ToString(); maxVal.text = c.Max.ToString(); radiusVal.text = "—";
            editTarget.text = "選んだ細胞を編集中" + (GroupSize(true, selIndex) > 1 ? "\n（対称の仲間もまとめて変わります）" : "");
        }
        else if (selKind == Kind.Obstacle)
        {
            var o = map.Obstacles[selIndex];
            energyVal.text = "—"; maxVal.text = "—"; radiusVal.text = o.R.ToString("0.0");
            editTarget.text = "選んだ障害物を編集中" + (GroupSize(false, selIndex) > 1 ? "\n（対称の仲間もまとめて変わります）" : "");
        }
        else
        {
            energyVal.text = placeEnergy.ToString(); maxVal.text = placeMax.ToString(); radiusVal.text = placeRadius.ToString("0.0");
            editTarget.text = "次に置くものの値";
        }
        counts.text = "細胞 " + map.Cells.Count + "/" + MapValidator.MaxCells + "　障害物 " + map.Obstacles.Count + "/" + MapValidator.MaxObstacles;

        var errs = MapValidator.Validate(map);
        if (errs.Count == 0) { status.text = "問題なし。試遊できます"; status.color = TWUI.Lime; }
        else { status.text = errs[0] + (errs.Count > 1 ? "（ほか" + (errs.Count - 1) + "件）" : ""); status.color = TWUI.Danger; }
        playBtn.SetInteractable(errs.Count == 0);
        savedLabel.text = dirty ? "未保存" : (map.Id.StartsWith("custom/") ? "保存済み" : "");
    }

    string SymName() { return map.Players == 2 ? "点対称" : map.Players == 3 ? "120°回転" : "上下左右"; }

    // ======================================================================
    // 対称
    // ======================================================================

    int SymCount { get { return symmetry ? map.Players : 1; } }

    /// <summary>基準位置を k 番目の対称位置へ写す</summary>
    Vector2 Image(Vector2 p, int k)
    {
        if (k == 0) return p;
        int n = map.Players;
        if (n == 2) return -p;
        if (n == 4)
        {
            if (k == 1) return -p;
            if (k == 2) return new Vector2(p.x, -p.y);
            return new Vector2(-p.x, p.y);
        }
        float a = k * 2 * Mathf.PI / 3, cs = Mathf.Cos(a), sn = Mathf.Sin(a);
        return new Vector2(Round2(p.x * cs - p.y * sn), Round2(p.x * sn + p.y * cs));
    }

    /// <summary>k 番目の対称位置から基準位置へ戻す</summary>
    Vector2 Inverse(Vector2 p, int k)
    {
        if (k == 0) return p;
        if (map.Players == 3) return Image(p, 3 - k);
        return Image(p, k);   // 鏡映・点対称は自分自身が逆
    }

    /// <summary>持ち主の写し方（P0 の位置が Pk の位置へ写るので、持ち主も同じく入れ替える）</summary>
    int OwnerImage(int owner, int k)
    {
        if (owner < 0 || k == 0) return owner;
        int n = map.Players;
        if (n == 3) return (owner + k) % 3;
        return owner ^ k;
    }
    int OwnerInverse(int owner, int k)
    {
        if (owner < 0 || k == 0) return owner;
        if (map.Players == 3) return ((owner - k) % 3 + 3) % 3;
        return owner ^ k;
    }

    static float Round2(float v) { return Mathf.Round(v * 100f) / 100f; }

    /// <summary>対称位置の一覧（軸上・中心では重ならないよう省く）。各要素は (位置, 番号k)</summary>
    List<KeyValuePair<Vector2, int>> Images(Vector2 p)
    {
        var r = new List<KeyValuePair<Vector2, int>>();
        for (int k = 0; k < SymCount; k++)
        {
            var q = Image(p, k);
            bool dup = false;
            foreach (var e in r) if ((e.Key - q).sqrMagnitude < 0.0004f) dup = true;
            if (!dup) r.Add(new KeyValuePair<Vector2, int>(q, k));
        }
        return r;
    }

    int GroupSize(bool cell, int index)
    {
        var g = cell ? cellGroup : obsGroup;
        int n = 0, id = g[index];
        foreach (var x in g) if (x == id) n++;
        return n;
    }

    // ======================================================================
    // 編集操作
    // ======================================================================

    Vector2 Snap(Vector2 p, bool cell)
    {
        p = new Vector2(Mathf.Round(p.x / snap) * snap, Mathf.Round(p.y / snap) * snap);
        float hw = MapDef.FieldHalfWidth - (cell ? MapDef.CellMargin : 0), hh = MapDef.FieldHalfHeight - (cell ? MapDef.CellMargin : 0);
        return new Vector2(Round2(Mathf.Clamp(p.x, -hw, hw)), Round2(Mathf.Clamp(p.y, -hh, hh)));
    }

    /// <summary>その位置に細胞を置けるか（理由を返す。null なら可）</summary>
    string CellSpotProblem(Vector2 p, int ignoreGroup)
    {
        for (int i = 0; i < map.Cells.Count; i++)
        {
            if (cellGroup[i] == ignoreGroup) continue;
            var c = map.Cells[i];
            if ((new Vector2(c.X, c.Y) - p).sqrMagnitude < MapValidator.MinCellDistance * MapValidator.MinCellDistance) return "ほかの細胞に近すぎます";
        }
        foreach (var o in map.Obstacles)
        {
            float lim = o.R + MapValidator.ObstacleClearance;
            if ((new Vector2(o.X, o.Y) - p).sqrMagnitude < lim * lim) return "障害物に近すぎます";
        }
        return null;
    }

    bool PlaceCell(Vector2 p, int owner, int energy, int max, bool quiet = false)
    {
        var imgs = Images(p);
        if (map.Cells.Count + imgs.Count > MapValidator.MaxCells) { if (!quiet) App.Toast("細胞は" + MapValidator.MaxCells + "個までです"); return false; }
        for (int a = 0; a < imgs.Count; a++)
        {
            string why = CellSpotProblem(imgs[a].Key, -1);
            for (int b = 0; b < a && why == null; b++)
                if ((imgs[a].Key - imgs[b].Key).sqrMagnitude < MapValidator.MinCellDistance * MapValidator.MinCellDistance) why = "対称の位置どうしが近すぎます";
            if (why != null) { if (!quiet) App.Toast(why, 1.4f); return false; }
        }
        if (!quiet) PushUndo();
        int g = nextGroup++;
        foreach (var e in imgs)
        {
            int ow = owner;
            if (owner >= 0 && symmetry) ow = OwnerImage(owner, e.Value);
            if (owner >= 0 && imgs.Count < SymCount) ow = -1;   // 軸上の点はどの陣営にも公平でないので中立にする
            int en = ow >= 0 ? Mathf.Max(1, energy) : energy;
            map.Cells.Add(new CellDef(e.Key.x, e.Key.y, ow, Mathf.Min(en, max), max));
            cellGroup.Add(g); cellImage.Add(e.Value);
        }
        if (owner >= 0 && imgs.Count < SymCount && !quiet) App.Toast("対称の軸上なので中立にしました", 1.6f);
        selKind = Kind.Cell; selIndex = map.Cells.Count - imgs.Count;
        Changed(true);
        return true;
    }

    bool PlaceObstacle(Vector2 p, float r)
    {
        var imgs = Images(p);
        if (map.Obstacles.Count + imgs.Count > MapValidator.MaxObstacles) { App.Toast("障害物は" + MapValidator.MaxObstacles + "個までです"); return false; }
        PushUndo();
        int g = nextGroup++;
        foreach (var e in imgs) { map.Obstacles.Add(new ObstacleDef(e.Key.x, e.Key.y, r)); obsGroup.Add(g); obsImage.Add(e.Value); }
        selKind = Kind.Obstacle; selIndex = map.Obstacles.Count - imgs.Count;
        Changed(true);
        return true;
    }

    void DeleteSelected()
    {
        PushUndo();
        if (selKind == Kind.Cell) RemoveGroup(true, cellGroup[selIndex]);
        else if (selKind == Kind.Obstacle) RemoveGroup(false, obsGroup[selIndex]);
        selKind = Kind.None; selIndex = -1;
        Changed(true);
    }

    void RemoveGroup(bool cell, int g)
    {
        if (cell)
        {
            for (int i = map.Cells.Count - 1; i >= 0; i--)
                if (cellGroup[i] == g) { map.Cells.RemoveAt(i); cellGroup.RemoveAt(i); cellImage.RemoveAt(i); }
        }
        else
        {
            for (int i = map.Obstacles.Count - 1; i >= 0; i--)
                if (obsGroup[i] == g) { map.Obstacles.RemoveAt(i); obsGroup.RemoveAt(i); obsImage.RemoveAt(i); }
        }
    }

    /// <summary>選んでいるものを移動（対称の仲間も一緒に）</summary>
    void MoveSelected(Vector2 p)
    {
        if (selKind == Kind.Cell)
        {
            int g = cellGroup[selIndex];
            p = Snap(p, true);
            Vector2 baseP = Inverse(p, cellImage[selIndex]);
            for (int i = 0; i < map.Cells.Count; i++)
                if (cellGroup[i] == g) { var q = Image(baseP, cellImage[i]); q = Snap(q, true); map.Cells[i].X = q.x; map.Cells[i].Y = q.y; }
        }
        else if (selKind == Kind.Obstacle)
        {
            int g = obsGroup[selIndex];
            p = Snap(p, false);
            Vector2 baseP = Inverse(p, obsImage[selIndex]);
            for (int i = 0; i < map.Obstacles.Count; i++)
                if (obsGroup[i] == g) { var q = Image(baseP, obsImage[i]); q = Snap(q, false); map.Obstacles[i].X = q.x; map.Obstacles[i].Y = q.y; }
        }
        Changed(selKind == Kind.Obstacle);
    }

    void SetOwner(int owner)
    {
        if (owner >= map.Players) return;
        if (selKind == Kind.Cell)
        {
            PushUndo();
            int g = cellGroup[selIndex];
            int baseOwner = OwnerInverse(owner, cellImage[selIndex]);
            bool onAxis = GroupSize(true, selIndex) < SymCount;
            for (int i = 0; i < map.Cells.Count; i++)
                if (cellGroup[i] == g)
                {
                    int ow = cellGroup.Count > 0 && GroupSize(true, selIndex) > 1 ? OwnerImage(baseOwner, cellImage[i]) : owner;
                    if (owner >= 0 && onAxis && symmetry) ow = -1;
                    map.Cells[i].Owner = ow;
                    if (ow >= 0 && map.Cells[i].Energy < 1) map.Cells[i].Energy = 1;
                }
            if (owner >= 0 && onAxis && symmetry) App.Toast("対称の軸上の細胞は中立にしかできません", 1.6f);
            Changed(false);
        }
        else placeOwner = owner;
        if (tool != Tool.Cell && selKind != Kind.Cell) tool = Tool.Cell;
        RefreshPanel();
    }

    void SetEnergy(int d)
    {
        if (selKind == Kind.Cell)
        {
            PushUndo();
            int g = cellGroup[selIndex];
            int v = Mathf.Clamp(map.Cells[selIndex].Energy + d, 0, map.Cells[selIndex].Max);
            for (int i = 0; i < map.Cells.Count; i++)
                if (cellGroup[i] == g) map.Cells[i].Energy = Mathf.Clamp(v, map.Cells[i].Owner >= 0 ? 1 : 0, map.Cells[i].Max);
            Changed(false);
        }
        else { placeEnergy = Mathf.Clamp(placeEnergy + d, 0, placeMax); RefreshPanel(); }
    }

    void SetMax(int d)
    {
        if (selKind == Kind.Cell)
        {
            PushUndo();
            int g = cellGroup[selIndex];
            int v = Mathf.Clamp(map.Cells[selIndex].Max + d, 10, Balance.DefaultMaxEnergy);
            for (int i = 0; i < map.Cells.Count; i++)
                if (cellGroup[i] == g) { map.Cells[i].Max = v; map.Cells[i].Energy = Mathf.Min(map.Cells[i].Energy, v); }
            Changed(false);
        }
        else { placeMax = Mathf.Clamp(placeMax + d, 10, Balance.DefaultMaxEnergy); placeEnergy = Mathf.Min(placeEnergy, placeMax); RefreshPanel(); }
    }

    void SetRadius(float d)
    {
        if (selKind == Kind.Obstacle)
        {
            PushUndo();
            int g = obsGroup[selIndex];
            float v = Mathf.Clamp(Round2(map.Obstacles[selIndex].R + d), MapValidator.MinObstacleRadius, MapValidator.MaxObstacleRadius);
            for (int i = 0; i < map.Obstacles.Count; i++) if (obsGroup[i] == g) map.Obstacles[i].R = v;
            Changed(true);
        }
        else { placeRadius = Mathf.Clamp(Round2(placeRadius + d), MapValidator.MinObstacleRadius, MapValidator.MaxObstacleRadius); RefreshPanel(); }
    }

    void SetPlayers(int n)
    {
        if (n == map.Players) return;
        PushUndo();
        map.Players = n;
        int demoted = 0;
        foreach (var c in map.Cells) if (c.Owner >= n) { c.Owner = -1; demoted++; }
        // 対称の種類が変わるので、仲間の関係はいったん解く
        for (int i = 0; i < cellGroup.Count; i++) { cellGroup[i] = nextGroup++; cellImage[i] = 0; }
        for (int i = 0; i < obsGroup.Count; i++) { obsGroup[i] = nextGroup++; obsImage[i] = 0; }
        if (placeOwner >= n) placeOwner = -1;
        if (demoted > 0) App.Toast("人数を減らしたので、" + demoted + "個の細胞を中立にしました");
        Changed(false);
    }

    void Changed(bool structural)
    {
        dirty = true;
        needBuild = true;   // 細胞の大きさ・障害物の位置も描き直す
        RefreshPanel();
    }

    /// <summary>入力中のマップ名を反映</summary>
    void Commit()
    {
        if (nameField != null && nameField.text != map.Name) map.Name = nameField.text;
    }

    // ---- 元に戻す ----

    void PushUndo()
    {
        undo.Add(new Snapshot
        {
            Json = MapJson.ToJson(map), Players = map.Players,
            CG = cellGroup.ToArray(), CI = cellImage.ToArray(), OG = obsGroup.ToArray(), OI = obsImage.ToArray()
        });
        if (undo.Count > 100) undo.RemoveAt(0);
    }

    void Undo()
    {
        if (undo.Count == 0) { App.Toast("これ以上戻せません", 1.2f); return; }
        var s = undo[undo.Count - 1];
        undo.RemoveAt(undo.Count - 1);
        string e;
        var m = MapJson.FromJson(s.Json, out e);
        if (m == null) return;
        m.Id = map.Id; m.BuiltIn = false;
        map = m;
        cellGroup.Clear(); cellGroup.AddRange(s.CG); cellImage.Clear(); cellImage.AddRange(s.CI);
        obsGroup.Clear(); obsGroup.AddRange(s.OG); obsImage.Clear(); obsImage.AddRange(s.OI);
        selKind = Kind.None; selIndex = -1;
        nameField.text = map.Name;
        playersSeg.Select(map.Players - 2, false);
        dirty = true; needBuild = true;
        RefreshPanel();
    }

    // ======================================================================
    // 保存・入出力・試遊
    // ======================================================================

    void Save()
    {
        Commit();
        if (string.IsNullOrEmpty(map.Name.Trim())) { map.Name = "名称未設定"; nameField.text = map.Name; }
        TWMapStore.Save(map);
        dirty = false;
        RefreshPanel();
        var errs = MapValidator.Validate(map);
        App.Toast(errs.Count == 0 ? "マイマップに保存しました" : "保存しました（問題が残っているので、まだ対戦には使えません）");
    }

    void ImportJson(string text)
    {
        string e;
        var m = MapJson.FromJson(text, out e);
        if (m == null) { App.Toast(e, 3f); return; }
        ConfirmDiscard(() =>
        {
            m.Id = "";
            LoadModel(m, false);
            dirty = true;
            App.Show(App.Editor);
            App.Toast("読み込みました。保存するとマイマップに追加されます");
        });
    }

    void TestPlay()
    {
        Commit();
        var errs = MapValidator.Validate(map);
        if (errs.Count > 0) { App.Toast(errs[0]); return; }
        App.Game.Launch(map.Clone(), 0, () => App.Show(App.Editor), "エディタへ");
        App.Show(App.Game);
    }

    void ConfirmDiscard(Action go)
    {
        if (!dirty) { go(); return; }
        App.Confirm("保存していない変更があります。\n破棄して進みますか？", "破棄する", go, true);
    }

    void Back()
    {
        Commit();
        ConfirmDiscard(() =>
        {
            dirty = false;
            if (fromStageSelect || map.Id.StartsWith("custom/"))
            {
                if (map.Id.StartsWith("custom/")) App.StageSelect.Focus(map);
                App.Show(App.StageSelect);
            }
            else App.Show(App.Home);
        });
    }

    // ======================================================================
    // 毎フレーム
    // ======================================================================

    float HighlightOf(int i)
    {
        if (selKind != Kind.Cell || selIndex < 0 || i >= cellGroup.Count) return 0;
        if (i == selIndex) return 1f;
        return cellGroup[i] == cellGroup[selIndex] ? 0.5f : 0f;
    }

    public override void Tick()
    {
        TWBoardView.FitCamera(App.Cam, 0.2f, 0.09f, 1f, 0.905f, 0.4f);
        HandleInput();
        if (needBuild)
        {
            sim = new Simulation(map);
            board.Build(sim);
            needBuild = false;
        }
        board.Render(sim, 0);
        board.FieldFrame(new Color(TWUI.Membrane.r, TWUI.Membrane.g, TWUI.Membrane.b, 0.6f));
        DrawEditorOverlays();
        board.EndFrame();
    }

    void DrawEditorOverlays()
    {
        // 選択の輪
        if (selKind == Kind.Obstacle && selIndex >= 0)
        {
            int g = obsGroup[selIndex];
            for (int i = 0; i < map.Obstacles.Count; i++)
                if (obsGroup[i] == g) board.Circle(new Vector2(map.Obstacles[i].X, map.Obstacles[i].Y), map.Obstacles[i].R + 0.15f, i == selIndex ? TWUI.Amber : new Color(TWUI.Amber.r, TWUI.Amber.g, TWUI.Amber.b, 0.5f));
        }
        if (selKind == Kind.Cell && selIndex >= 0)
        {
            var c = map.Cells[selIndex];
            board.Circle(new Vector2(c.X, c.Y), Balance.CellRadius(Balance.GradeFor(c.Energy)) + 0.3f, TWUI.Amber);
        }
        // 置く前の予告（対称の位置も表示。置けない場所は赤）
        if (OverUI() || dragging) return;
        if (tool == Tool.Cell || tool == Tool.Obstacle)
        {
            bool cell = tool == Tool.Cell;
            var p = Snap(pointerWorld, cell);
            if (Mathf.Abs(pointerWorld.x) > MapDef.FieldHalfWidth + 0.5f || Mathf.Abs(pointerWorld.y) > MapDef.FieldHalfHeight + 0.5f) return;
            var imgs = Images(p);
            for (int a = 0; a < imgs.Count; a++)
            {
                var q = imgs[a].Key;
                if (cell)
                {
                    bool bad = CellSpotProblem(q, -1) != null;
                    int ow = placeOwner >= 0 && symmetry ? OwnerImage(placeOwner, imgs[a].Value) : placeOwner;
                    if (placeOwner >= 0 && imgs.Count < SymCount) ow = -1;
                    var col = bad ? TWUI.Danger : TWBoardView.ColorOf(ow);
                    board.Circle(q, Balance.CellRadius(Balance.GradeFor(placeEnergy)), new Color(col.r, col.g, col.b, 0.8f), 0.07f);
                }
                else board.Circle(q, placeRadius, new Color(0.7f, 0.85f, 0.5f, 0.7f), 0.07f);
            }
        }
    }

    bool OverUI() { return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(); }

    int CellAt(Vector2 p)
    {
        int best = -1; float bestD = float.MaxValue;
        for (int i = 0; i < map.Cells.Count; i++)
        {
            var c = map.Cells[i];
            float d = Vector2.Distance(p, new Vector2(c.X, c.Y));
            if (d < Balance.CellRadius(Balance.GradeFor(c.Energy)) + 0.25f && d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    int ObstacleAt(Vector2 p)
    {
        for (int i = map.Obstacles.Count - 1; i >= 0; i--)
        {
            var o = map.Obstacles[i];
            if (Vector2.Distance(p, new Vector2(o.X, o.Y)) < o.R + 0.1f) return i;
        }
        return -1;
    }

    void HandleInput()
    {
        var pointer = Pointer.current;
        if (pointer == null || App.Cam == null) return;
        Vector2 screen = pointer.position.ReadValue();
        Vector3 w = App.Cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, 10));
        pointerWorld = new Vector2(w.x, w.y);

        var kb = Keyboard.current;
        bool typing = nameField != null && nameField.isFocused;
        if (kb != null && !typing)
        {
            if ((kb.deleteKey.wasPressedThisFrame || kb.backspaceKey.wasPressedThisFrame) && selKind != Kind.None) DeleteSelected();
            if (kb.zKey.wasPressedThisFrame && (kb.ctrlKey.isPressed || kb.leftCommandKey.isPressed)) Undo();
            if (kb.escapeKey.wasPressedThisFrame) { selKind = Kind.None; selIndex = -1; RefreshPanel(); }
        }

        if (pointer.press.wasPressedThisFrame)
        {
            pressedOnBoard = !OverUI();
            if (!pressedOnBoard) return;
            int ci = CellAt(pointerWorld), oi = ci < 0 ? ObstacleAt(pointerWorld) : -1;
            switch (tool)
            {
                case Tool.Erase:
                    if (ci >= 0) { selKind = Kind.Cell; selIndex = ci; DeleteSelected(); }
                    else if (oi >= 0) { selKind = Kind.Obstacle; selIndex = oi; DeleteSelected(); }
                    break;
                case Tool.Cell:
                    if (ci >= 0) StartDrag(Kind.Cell, ci);
                    else if (Inside(pointerWorld)) PlaceCell(Snap(pointerWorld, true), placeOwner, placeEnergy, placeMax);
                    break;
                case Tool.Obstacle:
                    if (oi >= 0) StartDrag(Kind.Obstacle, oi);
                    else if (Inside(pointerWorld)) PlaceObstacle(Snap(pointerWorld, false), placeRadius);
                    break;
                default:
                    if (ci >= 0) StartDrag(Kind.Cell, ci);
                    else if (oi >= 0) StartDrag(Kind.Obstacle, oi);
                    else { selKind = Kind.None; selIndex = -1; RefreshPanel(); }
                    break;
            }
        }

        if (dragging)
        {
            if (pointer.press.isPressed)
            {
                Vector2 target = pointerWorld + dragOffset;
                Vector2 cur = selKind == Kind.Cell ? new Vector2(map.Cells[selIndex].X, map.Cells[selIndex].Y) : new Vector2(map.Obstacles[selIndex].X, map.Obstacles[selIndex].Y);
                if ((Snap(target, selKind == Kind.Cell) - cur).sqrMagnitude > 0.0001f)
                {
                    if (!movedThisDrag) { PushUndo(); movedThisDrag = true; }
                    MoveSelected(target);
                }
            }
            else dragging = false;
        }
    }

    bool movedThisDrag;

    void StartDrag(Kind k, int index)
    {
        selKind = k; selIndex = index;
        Vector2 pos = k == Kind.Cell ? new Vector2(map.Cells[index].X, map.Cells[index].Y) : new Vector2(map.Obstacles[index].X, map.Obstacles[index].Y);
        dragOffset = pos - pointerWorld;
        dragging = true;
        movedThisDrag = false;
        RefreshPanel();
    }

    static bool Inside(Vector2 p) { return Mathf.Abs(p.x) <= MapDef.FieldHalfWidth && Mathf.Abs(p.y) <= MapDef.FieldHalfHeight; }
}
