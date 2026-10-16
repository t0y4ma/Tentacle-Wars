// コードで uGUI を組むための部品と配色（顕微鏡のスライドグラス／培地をイメージ）
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

public static class TWUI
{
    // ---- 配色 ----
    public static readonly Color Ink = Hex("071419");          // 背景の一番暗い色
    public static readonly Color Agar = Hex("0E2329");         // パネル（培地）
    public static readonly Color AgarLight = Hex("163640");    // 浮いたパネル・行
    public static readonly Color Membrane = Hex("3E7C7A");     // パネルの縁（細胞膜）
    public static readonly Color Text = Hex("E6F0E2");         // 本文
    public static readonly Color TextDim = Hex("8FA9A6");      // 補足
    public static readonly Color Lime = Hex("9BE15D");         // 決定・あなた
    public static readonly Color Amber = Hex("F2B544");        // 選択中・注意
    public static readonly Color Danger = Hex("F06A5E");       // 削除・エラー

    public static Font Font { get; private set; }
    static Sprite rounded, ring, circle;

    public static Color Hex(string h)
    {
        Color c; ColorUtility.TryParseHtmlString("#" + h, out c); return c;
    }

    public static void Init()
    {
        if (Font != null) return;
        Font = Resources.Load<Font>("TW/MPLUSRounded1c-Bold");
        if (Font == null) Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        rounded = MakeRounded(64, 18, false);
        ring = MakeRounded(64, 18, true);
        circle = MakeCircle(64);
    }

    public static Sprite Rounded { get { Init(); return rounded; } }
    public static Sprite Ring { get { Init(); return ring; } }
    public static Sprite Circle { get { Init(); return circle; } }

    static Sprite MakeRounded(int size, int radius, bool outline)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float cx = Mathf.Clamp(x + 0.5f, radius, size - radius), cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                float a = Mathf.Clamp01(radius - d + 0.5f);
                if (outline) a *= Mathf.Clamp01(d - (radius - 3f) + 0.5f);
                px[y * size + x] = new Color(1, 1, 1, a);
            }
        tex.SetPixels(px); tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect,
            new Vector4(radius + 2, radius + 2, radius + 2, radius + 2));
    }

    static Sprite MakeCircle(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color[size * size];
        float r = size / 2f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                px[y * size + x] = new Color(1, 1, 1, Mathf.Clamp01(r - d - 0.5f));
            }
        tex.SetPixels(px); tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100);
    }

    // ---- 土台 ----

    public static Canvas CreateCanvas(string name, int order)
    {
        Init();
        var go = new GameObject(name, typeof(RectTransform));
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = order;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        go.AddComponent<GraphicRaycaster>();
        EnsureEventSystem();
        return canvas;
    }

    public static void EnsureEventSystem()
    {
        if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() != null) return;
        var es = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        UnityEngine.Object.DontDestroyOnLoad(es);
    }

    public static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        return rt;
    }

    /// <summary>親に対する比率(0..1)で配置し、外側に余白(px)を取る</summary>
    public static RectTransform Place(RectTransform rt, float x0, float y0, float x1, float y1, float pad = 0)
    {
        rt.anchorMin = new Vector2(x0, y0); rt.anchorMax = new Vector2(x1, y1);
        rt.offsetMin = new Vector2(pad, pad); rt.offsetMax = new Vector2(-pad, -pad);
        rt.pivot = new Vector2(0.5f, 0.5f);
        return rt;
    }

    public static RectTransform Fill(RectTransform rt, float pad = 0) { return Place(rt, 0, 0, 1, 1, pad); }

    /// <summary>親の (ax, ay) の位置に、左端を合わせた正方形を置く（丸い点など）</summary>
    public static RectTransform Square(RectTransform rt, float ax, float ay, float size)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(ax, ay);
        rt.pivot = new Vector2(0, 0.5f);
        rt.sizeDelta = new Vector2(size, size);
        rt.anchoredPosition = Vector2.zero;
        return rt;
    }

    // ---- 部品 ----

    /// <summary>角丸パネル。rim=true で細胞膜のような縁を付ける</summary>
    public static Image Panel(Transform parent, Color color, bool rim = true, string name = "Panel")
    {
        var rt = Rect(name, parent);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = Rounded; img.type = Image.Type.Sliced; img.color = color;
        if (rim)
        {
            var r = Rect("Rim", rt); Fill(r);
            var ri = r.gameObject.AddComponent<Image>();
            ri.sprite = Ring; ri.type = Image.Type.Sliced; ri.color = new Color(Membrane.r, Membrane.g, Membrane.b, 0.55f);
            ri.raycastTarget = false;
        }
        return img;
    }

    public static Text Label(Transform parent, string text, int size, Color color, TextAnchor align = TextAnchor.MiddleLeft, string name = "Label")
    {
        var rt = Rect(name, parent);
        var t = rt.gameObject.AddComponent<Text>();
        t.font = Font; t.text = text; t.fontSize = size; t.color = color; t.alignment = align;
        t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Truncate;
        t.resizeTextForBestFit = true; t.resizeTextMinSize = 10; t.resizeTextMaxSize = size;
        t.raycastTarget = false;
        t.supportRichText = true;
        return t;
    }

    public enum Style { Primary, Normal, Quiet, Danger }

    public class Btn
    {
        public Button Button; public Image Back; public Text Label; public RectTransform Rect;
        public Style Style;
        bool selected;
        public void SetSelected(bool on) { selected = on; Apply(); }
        public void SetInteractable(bool on) { Button.interactable = on; Apply(); }
        public void Apply()
        {
            Color bg, fg;
            switch (Style)
            {
                case Style.Primary: bg = Lime; fg = Ink; break;
                case Style.Danger: bg = new Color(Danger.r, Danger.g, Danger.b, 0.18f); fg = Danger; break;
                case Style.Quiet: bg = new Color(0, 0, 0, 0); fg = TextDim; break;
                default: bg = AgarLight; fg = Text; break;
            }
            if (selected) { bg = Amber; fg = Ink; }
            if (!Button.interactable) { bg = new Color(bg.r, bg.g, bg.b, bg.a * 0.35f); fg = new Color(fg.r, fg.g, fg.b, 0.4f); }
            Back.color = bg; Label.color = fg;
        }
    }

    public static Btn Button(Transform parent, string text, Action onClick, Style style = Style.Normal, int size = 30, string name = null)
    {
        var rt = Rect(name ?? ("Btn_" + text), parent);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = Rounded; img.type = Image.Type.Sliced;
        var b = rt.gameObject.AddComponent<Button>();
        var cb = b.colors;
        cb.normalColor = Color.white; cb.highlightedColor = new Color(1.12f, 1.12f, 1.12f); cb.pressedColor = new Color(0.8f, 0.8f, 0.8f);
        cb.selectedColor = Color.white; cb.disabledColor = Color.white; cb.colorMultiplier = 1.1f; cb.fadeDuration = 0.08f;
        b.colors = cb;
        b.targetGraphic = img;
        var lbl = Label(rt, text, size, Color.white, TextAnchor.MiddleCenter);
        Fill(lbl.rectTransform, 6);
        var btn = new Btn { Button = b, Back = img, Label = lbl, Rect = rt, Style = style };
        if (onClick != null) b.onClick.AddListener(() => onClick());
        btn.Apply();
        return btn;
    }

    /// <summary>押した瞬間（ポインタを下ろした時）に呼ぶ。ブラウザのファイル選択・コピーはこれでないと動かない</summary>
    public static void OnPointerDown(Component c, Action action)
    {
        var et = c.gameObject.GetComponent<EventTrigger>() ?? c.gameObject.AddComponent<EventTrigger>();
        var entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
        entry.callback.AddListener(_ => action());
        et.triggers.Add(entry);
    }

    public static InputField Input(Transform parent, string placeholder, int size = 28, bool multiline = false)
    {
        var bg = Panel(parent, Ink, true, "Input");
        var field = bg.gameObject.AddComponent<InputField>();
        bg.gameObject.AddComponent<TWWebInput>();   // ブラウザでは HTML の入力欄で入力する
        var text = Label(bg.transform, "", size, Text, multiline ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft, "Text");
        text.resizeTextForBestFit = false;
        text.supportRichText = false;
        Fill(text.rectTransform); text.rectTransform.offsetMin = new Vector2(14, 4); text.rectTransform.offsetMax = new Vector2(-14, -4);
        if (!multiline) text.verticalOverflow = VerticalWrapMode.Overflow;
        var ph = Label(bg.transform, placeholder, size, new Color(TextDim.r, TextDim.g, TextDim.b, 0.7f), multiline ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft, "Placeholder");
        ph.resizeTextForBestFit = false;
        Fill(ph.rectTransform); ph.rectTransform.offsetMin = new Vector2(14, 4); ph.rectTransform.offsetMax = new Vector2(-14, -4);
        if (!multiline) ph.verticalOverflow = VerticalWrapMode.Overflow;
        field.textComponent = text;
        field.placeholder = ph;
        field.targetGraphic = bg;
        field.lineType = multiline ? InputField.LineType.MultiLineNewline : InputField.LineType.SingleLine;
        if (multiline) { text.verticalOverflow = VerticalWrapMode.Overflow; bg.gameObject.AddComponent<RectMask2D>(); }
        return field;
    }

    /// <summary>縦スクロールの一覧。content に要素を足していく</summary>
    public static ScrollRect Scroll(Transform parent, out RectTransform content, bool grid, Vector2 cell, float spacing = 16)
    {
        var rt = Rect("Scroll", parent);
        var sr = rt.gameObject.AddComponent<ScrollRect>();
        var vp = Rect("Viewport", rt); Fill(vp);
        vp.gameObject.AddComponent<RectMask2D>();
        var vpImg = vp.gameObject.AddComponent<Image>(); vpImg.color = new Color(0, 0, 0, 0);
        content = Rect("Content", vp);
        content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1); content.pivot = new Vector2(0.5f, 1);
        content.offsetMin = Vector2.zero; content.offsetMax = Vector2.zero;
        if (grid)
        {
            var g = content.gameObject.AddComponent<GridLayoutGroup>();
            g.cellSize = cell; g.spacing = new Vector2(spacing, spacing);
            g.padding = new RectOffset(8, 8, 8, 8);
            g.childAlignment = TextAnchor.UpperLeft;
        }
        else
        {
            var v = content.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = spacing; v.padding = new RectOffset(8, 8, 8, 8);
            v.childControlWidth = true; v.childControlHeight = true; v.childForceExpandWidth = true; v.childForceExpandHeight = false;
        }
        var fit = content.gameObject.AddComponent<ContentSizeFitter>();
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        sr.viewport = vp; sr.content = content; sr.horizontal = false; sr.vertical = true;
        sr.movementType = ScrollRect.MovementType.Clamped; sr.scrollSensitivity = 40;
        return sr;
    }

    public static Image Dot(Transform parent, Color c, string name = "Dot")
    {
        var rt = Rect(name, parent);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = Circle; img.color = c; img.raycastTarget = false;
        return img;
    }

    /// <summary>選択肢の横並び（どれか1つを選ぶ）</summary>
    public class Segmented
    {
        public readonly List<Btn> Buttons = new List<Btn>();
        public int Index { get; private set; }
        public Action<int> Changed;
        public void Select(int i, bool notify = true)
        {
            Index = i;
            for (int k = 0; k < Buttons.Count; k++) Buttons[k].SetSelected(k == i);
            if (notify && Changed != null) Changed(i);
        }
    }

    public static Segmented Segments(Transform parent, string[] labels, int initial, Action<int> changed, int size = 26)
    {
        var seg = new Segmented();
        var row = Rect("Segments", parent);
        var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.spacing = 8; h.childControlWidth = true; h.childControlHeight = true; h.childForceExpandWidth = true; h.childForceExpandHeight = true;
        for (int i = 0; i < labels.Length; i++)
        {
            int idx = i;
            var b = Button(row, labels[i], () => seg.Select(idx), Style.Normal, size);
            seg.Buttons.Add(b);
        }
        seg.Select(initial, false);
        seg.Changed = changed;
        return seg;
    }

    /// <summary>子をすべて消す（foreach で回しながら Destroy すると親から外すたびに順番がずれて1つおきに残る）</summary>
    public static void Clear(Transform parent)
    {
        if (parent == null) return;
        for (int i = parent.childCount - 1; i >= 0; i--) Destroy(parent.GetChild(i).gameObject);
    }

    public static void Destroy(GameObject go)
    {
        if (go == null) return;
        go.transform.SetParent(null);
        UnityEngine.Object.Destroy(go);
    }
}
