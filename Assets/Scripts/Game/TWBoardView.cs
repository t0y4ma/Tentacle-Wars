// 盤面の描画（対戦・エディタ・タイトル背景で共用）。シミュレーションの状態を読んで描くだけで、状態は持たない
using System.Collections.Generic;
using TW.Sim;
using UnityEngine;

public class TWBoardView
{
    public static readonly Color[] PlayerColors =
    {
        new Color(0.45f, 0.95f, 0.45f),
        new Color(1.00f, 0.42f, 0.40f),
        new Color(0.45f, 0.65f, 1.00f),
        new Color(0.80f, 0.50f, 1.00f),
    };
    public static readonly string[] PlayerColorNames = { "緑", "赤", "青", "紫" };
    public static readonly Color NeutralColor = new Color(0.58f, 0.60f, 0.64f);
    public const float BodyRadiusInTex = 0.31f;   // 細胞テクスチャ1ユニット中の体の半径

    public static Color ColorOf(int owner) { return owner < 0 ? NeutralColor : PlayerColors[owner % PlayerColors.Length]; }

    static Sprite[] cellSprites;
    static Sprite nodeSprite, glowSprite, obstacleSprite, bgSprite;
    static Material spriteMat;

    class CellView
    {
        public Transform Root;
        public SpriteRenderer Body, Glow;
        public TextMesh Label;
        public float Spin, Scale;
    }

    public readonly Transform Root;
    readonly List<CellView> cells = new List<CellView>();
    readonly List<SpriteRenderer> obstacles = new List<SpriteRenderer>();
    readonly List<SpriteRenderer> pool = new List<SpriteRenderer>();
    SpriteRenderer background;
    int used;

    /// <summary>細胞ごとの強調（0〜1）。選択中の細胞などに使う</summary>
    public System.Func<int, float> Highlight;
    public bool ShowNeutralGauge = true;

    static void LoadAssets()
    {
        if (cellSprites != null) return;
        cellSprites = new Sprite[6];
        for (int i = 0; i < 6; i++) cellSprites[i] = Resources.Load<Sprite>("TW/cell_" + i);
        nodeSprite = Resources.Load<Sprite>("TW/node");
        glowSprite = Resources.Load<Sprite>("TW/glow");
        obstacleSprite = Resources.Load<Sprite>("TW/obstacle");
        bgSprite = Resources.Load<Sprite>("TW/background");
        spriteMat = Resources.Load<Material>("TW/SpriteUnlit");
    }

    public TWBoardView(string name)
    {
        LoadAssets();
        TWUI.Init();
        Root = new GameObject(name).transform;
        background = MakeSprite("Background", bgSprite, -100, Root);
        background.transform.localScale = new Vector3(26, 26, 1);
        // 盤面の外枠は必要な画面（エディタ）だけ FieldFrame で描く
    }

    public void Destroy() { if (Root != null) Object.Destroy(Root.gameObject); }

    SpriteRenderer MakeSprite(string n, Sprite s, int order, Transform parent)
    {
        var go = new GameObject(n);
        go.transform.SetParent(parent, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = s; sr.sortingOrder = order;
        if (spriteMat != null) sr.sharedMaterial = spriteMat;
        return sr;
    }

    /// <summary>細胞と障害物の数が変わったら呼ぶ（新しい試合・エディタでの追加削除）</summary>
    public void Build(Simulation sim)
    {
        foreach (var c in cells) Object.Destroy(c.Root.gameObject);
        foreach (var o in obstacles) Object.Destroy(o.gameObject);
        cells.Clear(); obstacles.Clear();

        foreach (var o in sim.Obstacles)
        {
            var sr = MakeSprite("Obstacle", obstacleSprite, -50, Root);
            sr.transform.position = new Vector3(o.X, o.Y, 0);
            sr.transform.localScale = Vector3.one * (o.R * 2 / 0.86f);
            sr.color = new Color(0.55f, 0.65f, 0.45f, 0.9f);
            obstacles.Add(sr);
        }

        foreach (var c in sim.Cells)
        {
            var v = new CellView();
            v.Root = new GameObject("Cell" + c.Id).transform;
            v.Root.SetParent(Root, false);
            v.Glow = MakeSprite("Glow", glowSprite, 5, v.Root);
            v.Body = MakeSprite("Body", cellSprites[c.Grade], 10, v.Root);
            var tgo = new GameObject("Label");
            tgo.transform.SetParent(v.Root, false);
            v.Label = tgo.AddComponent<TextMesh>();
            v.Label.font = TWUI.Font;
            v.Label.fontSize = 64;
            v.Label.anchor = TextAnchor.MiddleCenter;
            v.Label.alignment = TextAlignment.Center;
            v.Label.richText = true;
            var mr = tgo.GetComponent<MeshRenderer>();
            mr.sharedMaterial = TWUI.Font.material;
            mr.sortingOrder = 50;
            v.Spin = Random.Range(-12f, 12f);
            v.Scale = Balance.CellRadius(c.Grade) / BodyRadiusInTex;
            cells.Add(v);
        }
    }

    /// <summary>盤面（全マップ共通の広さ）を画面の指定範囲（0..1）に収める</summary>
    public static void FitCamera(Camera cam, float x0, float y0, float x1, float y1, float margin = 0.6f)
    {
        if (cam == null) return;
        float aspect = (float)Screen.width / Mathf.Max(1, Screen.height);
        float hw = MapDef.FieldHalfWidth + margin, hh = MapDef.FieldHalfHeight + margin;
        float s = Mathf.Max(hh / Mathf.Max(0.05f, y1 - y0), hw / (aspect * Mathf.Max(0.05f, x1 - x0)));
        cam.orthographic = true;
        cam.orthographicSize = s;
        float cx = (x0 + x1) / 2f, cy = (y0 + y1) / 2f;
        cam.transform.position = new Vector3(-(cx - 0.5f) * 2 * s * aspect, -(cy - 0.5f) * 2 * s, -10);
    }

    /// <summary>描画。interp は次tickまでの進み(0..1)。粒の補間に使う</summary>
    public void Render(Simulation sim, float interp)
    {
        float time = Time.time;
        background.transform.position = new Vector3(0, 0, 0);

        for (int i = 0; i < sim.Cells.Count && i < cells.Count; i++)
        {
            var c = sim.Cells[i];
            var v = cells[i];
            var col = ColorOf(c.Owner);
            v.Root.position = new Vector3(c.X, c.Y, 0);
            v.Body.sprite = cellSprites[c.Grade];
            v.Body.color = col;
            float target = c.Radius / BodyRadiusInTex;
            v.Scale = Mathf.Lerp(v.Scale, target, Mathf.Clamp01(Time.deltaTime * 6f));
            float pulse = 1f + 0.025f * Mathf.Sin(time * 3f + i);
            v.Body.transform.localScale = Vector3.one * v.Scale * pulse;
            v.Body.transform.localRotation = Quaternion.Euler(0, 0, time * v.Spin);

            float hl = Highlight != null ? Highlight(i) : 0f;
            v.Glow.color = new Color(col.r, col.g, col.b, Mathf.Max(c.Owner >= 0 ? 0.28f : 0.12f, hl * 0.8f));
            v.Glow.transform.localScale = Vector3.one * v.Scale * (1.05f + 0.25f * hl);

            if (c.Owner >= 0)
            {
                v.Label.text = c.Energy.ToString();
                v.Label.color = Color.white;
            }
            else
            {
                // 中立：上段に中のエネルギー、下段に占領ゲージ（ためた量/必要量、ためている陣営の色）
                if (ShowNeutralGauge)
                {
                    Color gc = c.GaugeOwner >= 0 ? Color.Lerp(ColorOf(c.GaugeOwner), Color.white, 0.3f) : new Color(0.7f, 0.72f, 0.75f);
                    v.Label.text = c.Energy + "\n<size=36><color=#" + ColorUtility.ToHtmlStringRGB(gc) + ">" + c.Gauge + "/" + c.CaptureValue + "</color></size>";
                }
                else v.Label.text = c.Energy.ToString();
                v.Label.color = new Color(0.88f, 0.9f, 0.92f);
            }
            v.Label.characterSize = 0.045f + 0.006f * c.Grade;
        }

        used = 0;
        foreach (var t in sim.Tentacles)
        {
            if (t.Dead) continue;
            Ends(sim, t, out var a, out var b);
            Vector2 dir = (b - a).normalized;
            Vector2 perp = new Vector2(-dir.y, dir.x);
            t.GetSpan(out int from, out int to);
            var col = ColorOf(t.Owner);
            for (int n = from; n < to; n++)
            {
                Vector2 pos = PathPoint(a, b, perp, n + 0.5f, t.Length, t.Id, time);
                float size = 0.2f;
                Color nc = col * 0.8f;
                if (t.State == TentacleState.Flying) { size = 0.24f; nc = Color.Lerp(col, Color.white, 0.35f); }
                else if (t.State == TentacleState.Retracting) nc = col * 0.6f;
                nc.a = 1f;
                Dot(pos, size, nc, 2);   // 触手は細胞（5〜10）より下に描く
            }
            // 送出中のエネルギー粒：シミュレーション上の位置をそのまま描く（先端に着いた瞬間に数字が変わる）
            Color pcol = Color.Lerp(col, Color.white, 0.65f); pcol.a = 1f;
            foreach (var p in t.Packets)
            {
                float pos = Mathf.Min(p.Pos + interp * Balance.PacketNodesPerTick, t.Length);
                Dot(PathPoint(a, b, perp, pos, t.Length, t.Id, time), p.Amount >= 2 ? 0.34f : 0.28f, pcol, 3);
            }
        }
    }

    /// <summary>描画の締め。Render の後に追加の点（プレビュー等）を Dot で描いてから呼ぶ</summary>
    public void EndFrame()
    {
        for (int k = used; k < pool.Count; k++)
            if (pool[k].enabled) pool[k].enabled = false;
    }

    public void Dot(Vector2 pos, float size, Color c, int order = 20)
    {
        if (used >= pool.Count) pool.Add(MakeSprite("Node", nodeSprite, 20, Root));
        var sr = pool[used++];
        sr.enabled = true;
        sr.sortingOrder = order;
        sr.transform.position = new Vector3(pos.x, pos.y, 0);
        sr.transform.localScale = Vector3.one * size;
        sr.color = c;
    }

    /// <summary>点線（接続プレビュー・エディタの補助線）</summary>
    public void DottedLine(Vector2 a, Vector2 b, Color c, float size = 0.1f, float step = 0.3f)
    {
        int dots = Mathf.Max(1, Mathf.FloorToInt(Vector2.Distance(a, b) / step));
        for (int k = 1; k < dots; k++) Dot(Vector2.Lerp(a, b, (float)k / dots), size, c, 30);
    }

    /// <summary>円の輪郭（エディタの選択表示）</summary>
    public void Circle(Vector2 center, float r, Color c, float size = 0.08f)
    {
        int n = Mathf.Max(12, Mathf.CeilToInt(r * 2 * Mathf.PI / 0.22f));
        for (int k = 0; k < n; k++)
        {
            float a = k * Mathf.PI * 2 / n + Time.time * 0.6f;
            Dot(center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r, size, c, 31);
        }
    }

    public static void Ends(Simulation sim, Tentacle t, out Vector2 a, out Vector2 b)
    {
        var s = sim.Cells[t.Src]; var d = sim.Cells[t.Dst];
        Vector2 ps = new Vector2(s.X, s.Y), pd = new Vector2(d.X, d.Y);
        Vector2 dir = (pd - ps).normalized;
        a = ps + dir * Balance.PathEndRadius;
        b = pd - dir * Balance.PathEndRadius;
    }

    /// <summary>触手経路上の点（pos は根元からのノード単位の位置）。ノードと粒で同じ揺れを使う</summary>
    public static Vector2 PathPoint(Vector2 a, Vector2 b, Vector2 perp, float pos, int length, int id, float time)
    {
        float f = pos / length;
        float env = Mathf.Sin(Mathf.PI * Mathf.Clamp01(f));
        float wig = 0.08f * env * Mathf.Sin(pos * 0.7f - time * 6f + id);
        return Vector2.Lerp(a, b, f) + perp * wig;
    }

    /// <summary>盤面の外枠を点線で描く（エディタ用）</summary>
    public void FieldFrame(Color c)
    {
        float hw = MapDef.FieldHalfWidth, hh = MapDef.FieldHalfHeight;
        DottedLine(new Vector2(-hw, -hh), new Vector2(hw, -hh), c, 0.07f, 0.35f);
        DottedLine(new Vector2(hw, -hh), new Vector2(hw, hh), c, 0.07f, 0.35f);
        DottedLine(new Vector2(hw, hh), new Vector2(-hw, hh), c, 0.07f, 0.35f);
        DottedLine(new Vector2(-hw, hh), new Vector2(-hw, -hh), c, 0.07f, 0.35f);
    }


}
