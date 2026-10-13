// 仮テクスチャの手続き生成（白〜灰色で作り、実行時に陣営色で着色する）
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class TWTextureGenerator
{
    const string Dir = "Assets/Resources/TW";

    [MenuItem("Tentacle Wars/仮テクスチャを生成")]
    public static void GenerateAll()
    {
        Directory.CreateDirectory(Dir);
        for (int g = 0; g < 6; g++) Save("cell_" + g, MakeCell(256, g));
        Save("node", MakeNode(64));
        Save("glow", MakeGlow(128));
        Save("obstacle", MakeObstacle(256));
        Save("background", MakeBackground(512));
        AssetDatabase.Refresh();
        foreach (var name in new[] { "cell_0", "cell_1", "cell_2", "cell_3", "cell_4", "cell_5", "node", "glow", "obstacle", "background" })
            ConfigureSprite(Dir + "/" + name + ".png");
        AssetDatabase.Refresh();
        Debug.Log("[TW] 仮テクスチャを生成しました: " + Dir);
    }

    static void Save(string name, Texture2D tex)
    {
        File.WriteAllBytes(Dir + "/" + name + ".png", tex.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(tex);
    }

    static void ConfigureSprite(string path)
    {
        var imp = AssetImporter.GetAtPath(path) as TextureImporter;
        if (imp == null) return;
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        imp.textureType = TextureImporterType.Sprite;
        imp.spriteImportMode = SpriteImportMode.Single;
        imp.spritePixelsPerUnit = tex != null ? tex.width : 256; // 1ユニット = テクスチャ1枚
        imp.mipmapEnabled = false;
        imp.alphaIsTransparency = true;
        imp.filterMode = FilterMode.Bilinear;
        imp.textureCompression = TextureImporterCompression.Uncompressed;
        imp.wrapMode = TextureWrapMode.Clamp;
        imp.SaveAndReimport();
    }

    static Texture2D NewTex(int size)
    {
        var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
        t.wrapMode = TextureWrapMode.Clamp;
        return t;
    }

    static float Smooth(float edge, float x, float aa) { return Mathf.Clamp01((edge - x) / aa + 0.5f); }

    static float SegDist(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
        return (p - (a + ab * t)).magnitude;
    }

    // 体の半径は 0.62（テクスチャ半径=1）。毛・装飾は外側へ
    public const float BodyRadius = 0.62f;

    static Texture2D MakeCell(int size, int grade)
    {
        var tex = NewTex(size);
        var px = new Color[size * size];
        float aa = 2.5f / size;
        float R = BodyRadius;
        int hairs = grade == 0 ? 0 : 10 + grade * 2;
        float hairLen = new[] { 0f, 0.13f, 0.17f, 0.2f, 0.3f, 0.32f }[grade];
        float hairW = new[] { 0f, 0.012f, 0.014f, 0.016f, 0.03f, 0.022f }[grade];

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            var p = new Vector2((x + 0.5f) / size * 2 - 1, (y + 0.5f) / size * 2 - 1);
            float r = p.magnitude;
            float ang = Mathf.Atan2(p.y, p.x);
            // 体の輪郭を少し揺らす（有機的に）
            float wob = 0.012f * Mathf.Sin(ang * 5 + grade) + 0.008f * Mathf.Sin(ang * 9 + 1.3f);
            float body = Smooth(R + wob, r, aa);

            // 体内の陰影：中心が明るく、膜が濃い
            float rn = r / R;
            float shade = Mathf.Lerp(1.0f, 0.78f, rn * rn);
            float membrane = Mathf.Clamp01(1 - Mathf.Abs(r - (R + wob - 0.035f)) / 0.03f);
            shade = Mathf.Lerp(shade, 0.55f, membrane * 0.9f);
            // 核
            float nuc = Smooth(0.17f, (p - new Vector2(-0.12f, 0.1f)).magnitude, aa);
            shade = Mathf.Lerp(shade, 0.62f, nuc * 0.6f);
            // ハイライト
            float hl = Smooth(0.13f, (p - new Vector2(-0.24f, 0.28f)).magnitude, 0.12f);
            shade = Mathf.Lerp(shade, 1.0f, hl * 0.25f);

            float a = body;
            float c = shade;

            // 毛
            float hair = 0f, deco = 0f;
            for (int i = 0; i < hairs; i++)
            {
                float ha = i * Mathf.PI * 2 / hairs + 0.15f * grade;
                var dir = new Vector2(Mathf.Cos(ha), Mathf.Sin(ha));
                var a0 = dir * (R - 0.02f);
                var a1 = dir * (R + hairLen);
                if (grade == 5)
                {
                    // 二股
                    var side = new Vector2(-dir.y, dir.x);
                    var mid = dir * (R + hairLen * 0.45f);
                    hair = Mathf.Max(hair, Smooth(hairW, SegDist(p, a0, mid), aa));
                    hair = Mathf.Max(hair, Smooth(hairW * 0.8f, SegDist(p, mid, a1 + side * 0.07f), aa));
                    hair = Mathf.Max(hair, Smooth(hairW * 0.8f, SegDist(p, mid, a1 - side * 0.07f), aa));
                }
                else
                {
                    float w = grade == 4 ? Mathf.Lerp(hairW, hairW * 0.4f, Mathf.Clamp01((r - R) / hairLen)) : hairW;
                    hair = Mathf.Max(hair, Smooth(w, SegDist(p, a0, a1), aa));
                }
                if (grade == 2) deco = Mathf.Max(deco, Smooth(0.035f, (p - a1).magnitude, aa));
                if (grade == 3)
                    for (int k = 1; k <= 3; k++)
                        deco = Mathf.Max(deco, Smooth(0.026f, (p - dir * (R + hairLen * k / 3f)).magnitude, aa));
            }
            float outer = 1 - body;
            float ha2 = Mathf.Max(hair, deco) * outer;
            if (ha2 > 0)
            {
                c = Mathf.Lerp(c, deco > hair ? 1.0f : 0.8f, ha2);
                a = Mathf.Max(a, ha2);
            }
            px[y * size + x] = new Color(c, c, c, a);
        }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    static Texture2D MakeNode(int size)
    {
        var tex = NewTex(size);
        var px = new Color[size * size];
        float aa = 2f / size;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            var p = new Vector2((x + 0.5f) / size * 2 - 1, (y + 0.5f) / size * 2 - 1);
            float r = p.magnitude;
            float a = Smooth(0.9f, r, aa);
            float c = Mathf.Lerp(1f, 0.7f, r);
            float hl = Smooth(0.25f, (p - new Vector2(-0.3f, 0.3f)).magnitude, 0.3f);
            c = Mathf.Lerp(c, 1f, hl * 0.4f);
            px[y * size + x] = new Color(c, c, c, a);
        }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    static Texture2D MakeGlow(int size)
    {
        var tex = NewTex(size);
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            var p = new Vector2((x + 0.5f) / size * 2 - 1, (y + 0.5f) / size * 2 - 1);
            float r = p.magnitude;
            float a = Mathf.Pow(Mathf.Clamp01(1 - r), 2.2f);
            px[y * size + x] = new Color(1, 1, 1, a);
        }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    static Texture2D MakeObstacle(int size)
    {
        var tex = NewTex(size);
        var px = new Color[size * size];
        float aa = 3f / size;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            var p = new Vector2((x + 0.5f) / size * 2 - 1, (y + 0.5f) / size * 2 - 1);
            float r = p.magnitude;
            float ang = Mathf.Atan2(p.y, p.x);
            float edge = 0.86f + 0.05f * Mathf.Sin(ang * 3 + 0.5f) + 0.03f * Mathf.Sin(ang * 7 + 2f);
            float a = Smooth(edge, r, aa);
            float n = Mathf.PerlinNoise(x * 0.04f, y * 0.04f);
            float c = Mathf.Lerp(0.55f, 0.85f, n) * Mathf.Lerp(1f, 0.7f, r / edge);
            float rim = Mathf.Clamp01(1 - Mathf.Abs(r - edge + 0.04f) / 0.04f);
            c = Mathf.Lerp(c, 1f, rim * 0.5f);
            px[y * size + x] = new Color(c * 0.9f, c, c * 0.75f, a * 0.92f);
        }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    static Texture2D MakeBackground(int size)
    {
        var tex = NewTex(size);
        var px = new Color[size * size];
        var rnd = new System.Random(7);
        var spots = new Vector3[60];
        for (int i = 0; i < spots.Length; i++)
            spots[i] = new Vector3((float)rnd.NextDouble(), (float)rnd.NextDouble(), 0.01f + (float)rnd.NextDouble() * 0.05f);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float u = (x + 0.5f) / size, v = (y + 0.5f) / size;
            float d = new Vector2(u - 0.5f, v - 0.5f).magnitude;
            float baseV = Mathf.Lerp(0.16f, 0.05f, d * 1.6f);
            float n = Mathf.PerlinNoise(u * 6, v * 6) * 0.04f;
            float s = 0;
            foreach (var sp in spots)
            {
                float dd = new Vector2(u - sp.x, v - sp.y).magnitude;
                s = Mathf.Max(s, Mathf.Clamp01(1 - Mathf.Abs(dd - sp.z) / 0.003f) * 0.05f);
            }
            float c = baseV + n + s;
            px[y * size + x] = new Color(c * 0.55f, c * 0.95f, c * 1.1f, 1);
        }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }
}
