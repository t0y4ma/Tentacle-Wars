// COMの強さのドロップダウン。プリセット（よわい・ふつう・つよい）と、3つの時間を細かく決められる
using System;
using TW.Sim;
using UnityEngine;
using UnityEngine.UI;

public static class TWComMenu
{
    static GameObject open;

    public static bool IsOpen { get { return open != null; } }

    public static void Close()
    {
        if (open != null) TWUI.Destroy(open);
        open = null;
    }

    /// <summary>anchor の下（入らなければ上）に開く。値を変えるたびに changed を呼ぶ。閉じたら closed を呼ぶ</summary>
    public static void Open(RectTransform anchor, ComProfile current, Action<ComProfile> changed, Action closed = null)
    {
        Close();
        var canvas = anchor.GetComponentInParent<Canvas>().rootCanvas;
        var root = (RectTransform)canvas.transform;

        // 外側を押したら閉じる（透明な幕）。パネルは幕の子にしない（パネル内のクリックが幕に伝わって閉じてしまうため）
        var holder = TWUI.Rect("ComMenu", root);
        TWUI.Fill(holder);
        holder.SetAsLastSibling();
        var blocker = TWUI.Rect("Blocker", holder);
        TWUI.Fill(blocker);
        var bimg = blocker.gameObject.AddComponent<Image>(); bimg.color = new Color(0, 0, 0, 0.001f);
        var bb = blocker.gameObject.AddComponent<Button>(); bb.transition = Selectable.Transition.None;
        open = holder.gameObject;
        Action close = () => { Close(); if (closed != null) closed(); };
        bb.onClick.AddListener(() => close());

        var panel = TWUI.Panel(holder, TWUI.Agar, true, "Panel");
        var pr = panel.rectTransform;
        pr.anchorMin = pr.anchorMax = new Vector2(0.5f, 0.5f);
        Vector2 size = new Vector2(620, 400);
        pr.sizeDelta = size;
        // ボタンが押されても幕に届かないよう、パネル自身もクリックを受ける
        panel.raycastTarget = true;

        // anchor の位置（root の座標）を求めて、その下に置く
        var corners = new Vector3[4];
        anchor.GetWorldCorners(corners);   // 0 左下, 1 左上, 2 右上, 3 右下
        Vector2 bl = root.InverseTransformPoint(corners[0]), tr = root.InverseTransformPoint(corners[2]);
        var rr = root.rect;
        float x = Mathf.Clamp((bl.x + tr.x) * 0.5f, rr.xMin + size.x * 0.5f + 10, rr.xMax - size.x * 0.5f - 10);
        float y = bl.y - 8 - size.y * 0.5f;
        if (y - size.y * 0.5f < rr.yMin + 10) y = tr.y + 8 + size.y * 0.5f;   // 下に入らなければ上に
        y = Mathf.Clamp(y, rr.yMin + size.y * 0.5f + 10, rr.yMax - size.y * 0.5f - 10);
        pr.anchoredPosition = new Vector2(x, y) - rr.center;

        var p = current.Clamped();
        var title = TWUI.Label(pr, "COMの強さ", 28, TWUI.Text, TextAnchor.MiddleLeft);
        TWUI.Place(title.rectTransform, 0.05f, 0.85f, 0.6f, 0.97f);

        TWUI.Segmented presets = null;
        Text reactV = null, gazeV = null, moveV = null;
        Action refresh = () =>
        {
            reactV.text = Sec(p.ReactTicks * 5);
            gazeV.text = Sec(p.GazeTicks * 5);
            moveV.text = Sec(p.MoveCentis);
            int idx = p.PresetIndex();
            presets.Select(idx, false);
        };
        Action apply = () => { p = p.Clamped(); refresh(); if (changed != null) changed(p); };

        presets = TWUI.Segments(pr, SimpleAI.LevelNames, p.PresetIndex(), i => { p = SimpleAI.Presets[i]; apply(); }, 24);
        TWUI.Place((RectTransform)presets.Buttons[0].Rect.parent, 0.05f, 0.68f, 0.95f, 0.82f);

        reactV = Row(pr, 0.5f, "次の手までの時間", () => { p.ReactTicks -= 1; apply(); }, () => { p.ReactTicks += 1; apply(); });
        gazeV = Row(pr, 0.34f, "目移りの時間", () => { p.GazeTicks -= 1; apply(); }, () => { p.GazeTicks += 1; apply(); });
        moveV = Row(pr, 0.18f, "触手をつなぐ時間（距離1あたり）", () => { p.MoveCentis -= 1; apply(); }, () => { p.MoveCentis += 1; apply(); });

        var ok = TWUI.Button(pr, "閉じる", () => close(), TWUI.Style.Quiet, 24);
        TWUI.Place(ok.Rect, 0.7f, 0.02f, 0.95f, 0.14f);
        refresh();
    }

    static string Sec(int centis) { return (centis / 100f).ToString("0.00") + "秒"; }

    static Text Row(RectTransform parent, float y, string label, Action minus, Action plus)
    {
        var l = TWUI.Label(parent, label, 22, TWUI.TextDim, TextAnchor.MiddleLeft);
        TWUI.Place(l.rectTransform, 0.05f, y, 0.56f, y + 0.13f);
        var m = TWUI.Button(parent, "−", () => minus(), TWUI.Style.Normal, 28);
        TWUI.Place(m.Rect, 0.57f, y + 0.01f, 0.67f, y + 0.12f);
        var v = TWUI.Label(parent, "", 26, TWUI.Text, TextAnchor.MiddleCenter);
        TWUI.Place(v.rectTransform, 0.67f, y, 0.85f, y + 0.13f);
        var pl = TWUI.Button(parent, "＋", () => plus(), TWUI.Style.Normal, 28);
        TWUI.Place(pl.Rect, 0.85f, y + 0.01f, 0.95f, y + 0.12f);
        return v;
    }
}
