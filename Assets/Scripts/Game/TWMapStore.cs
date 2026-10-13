// 自作マップの保存（ブラウザでも消えないよう PlayerPrefs に JSON で持つ）と、一覧用の縮小画像
using System;
using System.Collections.Generic;
using TW.Sim;
using UnityEngine;

public static class TWMapStore
{
    const string IndexKey = "TW.MyMaps";
    const string Prefix = "TW.MyMap.";

    static List<string> Ids()
    {
        var s = PlayerPrefs.GetString(IndexKey, "");
        var l = new List<string>();
        foreach (var p in s.Split(',')) if (!string.IsNullOrEmpty(p)) l.Add(p);
        return l;
    }

    static void SaveIds(List<string> ids) { PlayerPrefs.SetString(IndexKey, string.Join(",", ids)); PlayerPrefs.Save(); }

    /// <summary>保存済みの自作マップ（新しい順）</summary>
    public static List<MapDef> LoadAll()
    {
        var r = new List<MapDef>();
        var ids = Ids();
        for (int i = ids.Count - 1; i >= 0; i--)
        {
            string err;
            var m = MapJson.FromJson(PlayerPrefs.GetString(Prefix + ids[i], ""), out err);
            if (m == null) continue;
            m.Id = "custom/" + ids[i];
            m.BuiltIn = false;
            r.Add(m);
        }
        return r;
    }

    /// <summary>保存して ID を返す。基本マップや新規なら新しい ID で保存する</summary>
    public static string Save(MapDef m)
    {
        string key = (m.Id != null && m.Id.StartsWith("custom/")) ? m.Id.Substring(7) : Guid.NewGuid().ToString("N").Substring(0, 12);
        m.Id = "custom/" + key;
        m.BuiltIn = false;
        PlayerPrefs.SetString(Prefix + key, MapJson.ToJson(m));
        var ids = Ids();
        ids.Remove(key);
        ids.Add(key);
        SaveIds(ids);
        return m.Id;
    }

    public static void Delete(string id)
    {
        if (id == null || !id.StartsWith("custom/")) return;
        string key = id.Substring(7);
        PlayerPrefs.DeleteKey(Prefix + key);
        var ids = Ids();
        ids.Remove(key);
        SaveIds(ids);
    }

    public static string FileNameFor(MapDef m)
    {
        var name = string.IsNullOrEmpty(m.Name) ? "map" : m.Name;
        foreach (var c in "\\/:*?\"<>| ") name = name.Replace(c, '_');
        return name + ".twmap.json";
    }

    // ---------------- 縮小画像 ----------------

    static readonly Dictionary<string, Texture2D> previewCache = new Dictionary<string, Texture2D>();

    public static void ForgetPreview(string id) { if (id != null) previewCache.Remove(id); }

    /// <summary>マップの縮小画像（盤面の縦横比 19:11）</summary>
    public static Texture2D Preview(MapDef m, int width = 380)
    {
        string key = m.Id + "|" + width + "|" + MapJson.ToJson(m).GetHashCode();
        Texture2D cached;
        if (previewCache.TryGetValue(key, out cached) && cached != null) return cached;

        float hw = MapDef.FieldHalfWidth, hh = MapDef.FieldHalfHeight;
        int height = Mathf.RoundToInt(width * hh / hw);
        float scale = width / (hw * 2);
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color[width * height];
        Color bg = TWUI.Hex("0B1D22"), bg2 = TWUI.Hex("10292F");
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float u = x / (float)width - 0.5f, v = y / (float)height - 0.5f;
                px[y * width + x] = Color.Lerp(bg2, bg, Mathf.Clamp01((u * u + v * v) * 3f));
            }

        Action<float, float, float, Color, float> disc = (wx, wy, r, c, soft) =>
        {
            float cx = (wx + hw) * scale, cy = (wy + hh) * scale, rp = r * scale;
            int x0 = Mathf.Max(0, (int)(cx - rp - 2)), x1 = Mathf.Min(width - 1, (int)(cx + rp + 2));
            int y0 = Mathf.Max(0, (int)(cy - rp - 2)), y1 = Mathf.Min(height - 1, (int)(cy + rp + 2));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    float a = Mathf.Clamp01((rp - d) / soft + 0.5f) * c.a;
                    if (a <= 0) continue;
                    var o = px[y * width + x];
                    px[y * width + x] = new Color(Mathf.Lerp(o.r, c.r, a), Mathf.Lerp(o.g, c.g, a), Mathf.Lerp(o.b, c.b, a), 1);
                }
        };

        foreach (var o in m.Obstacles) disc(o.X, o.Y, o.R, new Color(0.45f, 0.55f, 0.36f, 0.85f), 1.2f);
        foreach (var c in m.Cells)
        {
            float r = Balance.CellRadius(Balance.GradeFor(c.Energy));
            var col = TWBoardView.ColorOf(c.Owner);
            disc(c.X, c.Y, r * 1.6f, new Color(col.r, col.g, col.b, 0.18f), 3f);
            disc(c.X, c.Y, r, col, 1.2f);
        }
        tex.SetPixels(px);
        tex.Apply();
        previewCache[key] = tex;
        return tex;
    }
}
