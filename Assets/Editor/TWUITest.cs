// デバッグ用：実際のUIのクリック判定（EventSystemのレイキャスト）を通してクリックする
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public static class TWUITest
{
    /// <summary>表示中の文字が text と一致する要素の中心をクリックする。index は同じ文字が複数あるときの何番目か</summary>
    public static string Click(string text, int index = 0, bool contains = false)
    {
        var found = Find(text, contains);
        if (found.Count <= index) return "見つからない: " + text + "（" + found.Count + "件）";
        return ClickRect((RectTransform)found[index].transform);
    }

    /// <summary>名前（GameObject名）で探してクリックする</summary>
    public static string ClickName(string goName, int index = 0)
    {
        var list = new List<RectTransform>();
        foreach (var rt in Object.FindObjectsByType<RectTransform>(FindObjectsSortMode.None))
            if (rt.gameObject.activeInHierarchy && rt.name == goName) list.Add(rt);
        list.Sort((a, b) => ScreenCenter(b).y != ScreenCenter(a).y ? ScreenCenter(b).y.CompareTo(ScreenCenter(a).y) : ScreenCenter(a).x.CompareTo(ScreenCenter(b).x));
        if (list.Count <= index) return "見つからない: " + goName + "（" + list.Count + "件）";
        return ClickRect(list[index]);
    }

    public static string ClickRect(RectTransform rt) { return ClickAt(ScreenCenter(rt)); }

    /// <summary>プレイ開始直後に1回呼ぶ。Unityが裏にあってもフレームを進め、スクリーンショットにUIが写るようにする</summary>
    public static string Prepare()
    {
        Application.runInBackground = true;
        int n = 0;
        foreach (var c in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            if (c.isRootCanvas && c.renderMode == RenderMode.ScreenSpaceOverlay) { c.renderMode = RenderMode.ScreenSpaceCamera; c.worldCamera = Camera.main; c.planeDistance = 1; c.sortingOrder += 1000; n++; }
        return "prepared canvases=" + n;
    }

    /// <summary>画面座標をクリック。レイキャストで一番上に当たったものへ押下→離す→クリックを送る</summary>
    public static string ClickAt(Vector2 screen)
    {
        var es = EventSystem.current;
        if (es == null) return "EventSystem がない";
        Canvas.ForceUpdateCanvases();
        var ped = new PointerEventData(es) { position = screen, button = PointerEventData.InputButton.Left };
        var hits = new List<RaycastResult>();
        es.RaycastAll(ped, hits);
        if (hits.Count == 0) return "何にも当たらない @" + screen;
        var hit = hits[0];
        ped.pointerCurrentRaycast = hit; ped.pointerPressRaycast = hit;
        ped.pressPosition = screen;
        var go = hit.gameObject;
        var down = ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerDownHandler);
        var clickTarget = ExecuteEvents.GetEventHandler<IPointerClickHandler>(go);
        ped.pointerPress = down ?? clickTarget; ped.rawPointerPress = go;
        ExecuteEvents.Execute(ped.pointerPress, ped, ExecuteEvents.pointerUpHandler);
        if (clickTarget != null && (down == null || down == clickTarget)) ExecuteEvents.Execute(clickTarget, ped, ExecuteEvents.pointerClickHandler);
        return "当たった: " + PathOf(go.transform) + " → クリック先: " + (clickTarget != null ? PathOf(clickTarget.transform) : "なし");
    }

    /// <summary>表示中のボタンと文字の一覧（デバッグ用）</summary>
    public static string Dump(string under = null)
    {
        var sb = new StringBuilder();
        foreach (var t in Object.FindObjectsByType<Text>(FindObjectsSortMode.None))
        {
            if (!t.gameObject.activeInHierarchy || string.IsNullOrEmpty(t.text)) continue;
            if (under != null && !PathOf(t.transform).Contains(under)) continue;
            var c = ScreenCenter(t.rectTransform);
            sb.AppendLine(c.x.ToString("0") + "," + c.y.ToString("0") + "  " + t.text.Replace("\n", " ") + "  [" + PathOf(t.transform) + "]");
        }
        return sb.ToString();
    }

    static List<Text> Find(string text, bool contains)
    {
        var r = new List<Text>();
        foreach (var t in Object.FindObjectsByType<Text>(FindObjectsSortMode.None))
            if (t.gameObject.activeInHierarchy && (contains ? t.text.Contains(text) : t.text == text)) r.Add(t);
        // 画面の上から順、同じ高さなら左から
        r.Sort((a, b) =>
        {
            Vector2 pa = ScreenCenter(a.rectTransform), pb = ScreenCenter(b.rectTransform);
            if (Mathf.Abs(pa.y - pb.y) > 2) return pb.y.CompareTo(pa.y);
            return pa.x.CompareTo(pb.x);
        });
        return r;
    }

    public static Vector2 ScreenCenter(RectTransform rt)
    {
        var canvas = rt.GetComponentInParent<Canvas>();
        Camera cam = canvas != null && canvas.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.rootCanvas.worldCamera : null;
        return RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(rt.rect.center));
    }

    static string PathOf(Transform t)
    {
        var s = t.name;
        for (var p = t.parent; p != null && p.GetComponent<Canvas>() == null; p = p.parent) s = p.name + "/" + s;
        return s;
    }
}
