// Tentacle Wars Online - マップ定義・JSON入出力・検証・基本マップ（Unity非依存）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TW.Sim
{
    public class CellDef
    {
        public float X, Y;
        public int Owner;                       // -1 = 中立、0.. = プレイヤー
        public int Energy;
        public int Max = Balance.DefaultMaxEnergy;
        public CellDef() { }
        public CellDef(float x, float y, int owner, int energy, int max = Balance.DefaultMaxEnergy)
        { X = x; Y = y; Owner = owner; Energy = energy; Max = max; }
        public CellDef Clone() { return new CellDef(X, Y, Owner, Energy, Max); }
    }

    public class ObstacleDef
    {
        public float X, Y, R;
        public ObstacleDef() { }
        public ObstacleDef(float x, float y, float r) { X = x; Y = y; R = r; }
        public ObstacleDef Clone() { return new ObstacleDef(X, Y, R); }
    }

    public class MapDef
    {
        // 盤面の広さ（全マップ共通）。中心が原点
        public const float FieldHalfWidth = 9.5f, FieldHalfHeight = 5.5f;
        public const float CellMargin = 0.8f;   // 細胞の中心を置ける範囲は端からこれだけ内側

        public string Id = "";                  // "builtin/duel" や "custom/xxxx"
        public string Name = "";
        public string Author = "";
        public int Players = 2;
        public bool BuiltIn;
        public List<CellDef> Cells = new List<CellDef>();
        public List<ObstacleDef> Obstacles = new List<ObstacleDef>();

        public MapDef Clone()
        {
            var m = new MapDef { Id = Id, Name = Name, Author = Author, Players = Players, BuiltIn = BuiltIn };
            foreach (var c in Cells) m.Cells.Add(c.Clone());
            foreach (var o in Obstacles) m.Obstacles.Add(o.Clone());
            return m;
        }
    }

    // ======================================================================
    // 検証
    // ======================================================================
    public static class MapValidator
    {
        public const int MinPlayers = 2, MaxPlayers = 4;
        public const int MaxCells = 48, MaxObstacles = 16;
        public const float MinCellDistance = 1.3f;
        public const float ObstacleClearance = 0.6f;   // 障害物の縁と細胞の中心の最小距離
        public const float MinObstacleRadius = 0.3f, MaxObstacleRadius = 2.5f;
        public const int MaxNameLength = 24;

        /// <summary>問題点を日本語で返す。空なら遊べる。</summary>
        public static List<string> Validate(MapDef m)
        {
            var e = new List<string>();
            if (m == null) { e.Add("マップがありません"); return e; }
            if (string.IsNullOrEmpty(m.Name) || m.Name.Trim().Length == 0) e.Add("マップ名が空です");
            else if (m.Name.Length > MaxNameLength) e.Add("マップ名は" + MaxNameLength + "文字以内にしてください");
            if (m.Players < MinPlayers || m.Players > MaxPlayers) e.Add("人数は" + MinPlayers + "〜" + MaxPlayers + "人にしてください");
            if (m.Cells.Count > MaxCells) e.Add("細胞は" + MaxCells + "個までです");
            if (m.Obstacles.Count > MaxObstacles) e.Add("障害物は" + MaxObstacles + "個までです");

            float hw = MapDef.FieldHalfWidth - MapDef.CellMargin, hh = MapDef.FieldHalfHeight - MapDef.CellMargin;
            var perPlayer = new int[MaxPlayers];
            for (int i = 0; i < m.Cells.Count; i++)
            {
                var c = m.Cells[i];
                string n = "細胞" + (i + 1);
                if (Math.Abs(c.X) > hw + 1e-4f || Math.Abs(c.Y) > hh + 1e-4f) e.Add(n + "が盤面の外にあります");
                if (c.Owner < -1 || c.Owner >= m.Players) e.Add(n + "の持ち主が人数に合っていません");
                else if (c.Owner >= 0) perPlayer[c.Owner]++;
                if (c.Max < 10 || c.Max > Balance.DefaultMaxEnergy) e.Add(n + "の上限は10〜" + Balance.DefaultMaxEnergy + "にしてください");
                if (c.Energy < 0 || c.Energy > c.Max) e.Add(n + "のエネルギーは0〜上限にしてください");
                if (c.Owner >= 0 && c.Energy < 1) e.Add(n + "（プレイヤーの細胞）のエネルギーは1以上にしてください");
                for (int j = i + 1; j < m.Cells.Count; j++)
                {
                    var d = m.Cells[j];
                    float dx = c.X - d.X, dy = c.Y - d.Y;
                    if (dx * dx + dy * dy < MinCellDistance * MinCellDistance) e.Add(n + "と細胞" + (j + 1) + "が近すぎます");
                }
                for (int k = 0; k < m.Obstacles.Count; k++)
                {
                    var o = m.Obstacles[k];
                    float dx = c.X - o.X, dy = c.Y - o.Y, lim = o.R + ObstacleClearance;
                    if (dx * dx + dy * dy < lim * lim) e.Add(n + "が障害物" + (k + 1) + "に近すぎます");
                }
            }
            for (int p = 0; p < m.Players && p < MaxPlayers; p++)
                if (perPlayer[p] == 0) e.Add("プレイヤー" + (p + 1) + "の細胞がありません");
            for (int k = 0; k < m.Obstacles.Count; k++)
            {
                var o = m.Obstacles[k];
                if (o.R < MinObstacleRadius - 1e-4f || o.R > MaxObstacleRadius + 1e-4f)
                    e.Add("障害物" + (k + 1) + "の半径は" + MinObstacleRadius + "〜" + MaxObstacleRadius + "にしてください");
                if (Math.Abs(o.X) > MapDef.FieldHalfWidth || Math.Abs(o.Y) > MapDef.FieldHalfHeight) e.Add("障害物" + (k + 1) + "が盤面の外にあります");
            }
            return e;
        }
    }

    // ======================================================================
    // JSON 入出力（依存なしの最小実装）
    // ======================================================================
    public static class MapJson
    {
        public const string Format = "tentacle-wars-map";
        public const int Version = 1;

        public static string ToJson(MapDef m)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"format\": \"").Append(Format).Append("\",\n");
            sb.Append("  \"version\": ").Append(Version).Append(",\n");
            sb.Append("  \"name\": ").Append(Str(m.Name)).Append(",\n");
            sb.Append("  \"author\": ").Append(Str(m.Author)).Append(",\n");
            sb.Append("  \"players\": ").Append(m.Players).Append(",\n");
            sb.Append("  \"cells\": [\n");
            for (int i = 0; i < m.Cells.Count; i++)
            {
                var c = m.Cells[i];
                sb.Append("    { \"x\": ").Append(Num(c.X)).Append(", \"y\": ").Append(Num(c.Y))
                  .Append(", \"owner\": ").Append(c.Owner).Append(", \"energy\": ").Append(c.Energy)
                  .Append(", \"max\": ").Append(c.Max).Append(" }").Append(i < m.Cells.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("  ],\n");
            sb.Append("  \"obstacles\": [\n");
            for (int i = 0; i < m.Obstacles.Count; i++)
            {
                var o = m.Obstacles[i];
                sb.Append("    { \"x\": ").Append(Num(o.X)).Append(", \"y\": ").Append(Num(o.Y))
                  .Append(", \"r\": ").Append(Num(o.R)).Append(" }").Append(i < m.Obstacles.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        /// <summary>JSONからマップを読む。失敗時は null と理由。検証（MapValidator）は別途行う。</summary>
        public static MapDef FromJson(string json, out string error)
        {
            error = null;
            object root;
            try { root = new Parser(json).ParseRoot(); }
            catch (Exception ex) { error = "JSONの書式が正しくありません（" + ex.Message + "）"; return null; }
            var o = root as Dictionary<string, object>;
            if (o == null) { error = "JSONの一番外側が { } ではありません"; return null; }
            object f;
            if (o.TryGetValue("format", out f) && f is string && (string)f != Format) { error = "Tentacle Wars のマップではありません"; return null; }
            try
            {
                var m = new MapDef();
                m.Name = GetStr(o, "name", "");
                m.Author = GetStr(o, "author", "");
                m.Players = GetInt(o, "players", 2);
                object cells;
                if (!o.TryGetValue("cells", out cells) || !(cells is List<object>)) { error = "cells（細胞の一覧）がありません"; return null; }
                foreach (var ce in (List<object>)cells)
                {
                    var co = ce as Dictionary<string, object>;
                    if (co == null) { error = "cells の中身が正しくありません"; return null; }
                    m.Cells.Add(new CellDef(GetFloat(co, "x", 0), GetFloat(co, "y", 0), GetInt(co, "owner", -1),
                        GetInt(co, "energy", 10), GetInt(co, "max", Balance.DefaultMaxEnergy)));
                }
                object obs;
                if (o.TryGetValue("obstacles", out obs) && obs is List<object>)
                    foreach (var oe in (List<object>)obs)
                    {
                        var oo = oe as Dictionary<string, object>;
                        if (oo == null) { error = "obstacles の中身が正しくありません"; return null; }
                        m.Obstacles.Add(new ObstacleDef(GetFloat(oo, "x", 0), GetFloat(oo, "y", 0), GetFloat(oo, "r", 1)));
                    }
                return m;
            }
            catch (Exception ex) { error = "値の読み取りに失敗しました（" + ex.Message + "）"; return null; }
        }

        static string Num(float v) { return Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture); }

        static string Str(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s ?? "")
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        static string GetStr(Dictionary<string, object> o, string k, string def)
        { object v; return o.TryGetValue(k, out v) && v is string ? (string)v : def; }
        static int GetInt(Dictionary<string, object> o, string k, int def)
        { object v; return o.TryGetValue(k, out v) && v is double ? (int)Math.Round((double)v) : def; }
        static float GetFloat(Dictionary<string, object> o, string k, float def)
        { object v; return o.TryGetValue(k, out v) && v is double ? (float)(double)v : def; }

        class Parser
        {
            readonly string s; int i;
            public Parser(string text) { s = text ?? ""; }

            public object ParseRoot()
            {
                Ws();
                var v = Value();
                Ws();
                if (i != s.Length) throw new FormatException("末尾に余分な文字があります");
                return v;
            }

            void Ws() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
            char Peek() { if (i >= s.Length) throw new FormatException("途中で終わっています"); return s[i]; }

            object Value()
            {
                char c = Peek();
                if (c == '{') return Obj();
                if (c == '[') return Arr();
                if (c == '"') return Str();
                if (c == 't' && Lit("true")) return true;
                if (c == 'f' && Lit("false")) return false;
                if (c == 'n' && Lit("null")) return null;
                return NumV();
            }

            bool Lit(string w)
            {
                if (string.CompareOrdinal(s, i, w, 0, w.Length) == 0) { i += w.Length; return true; }
                throw new FormatException("不明な値です");
            }

            Dictionary<string, object> Obj()
            {
                var d = new Dictionary<string, object>();
                i++; Ws();
                if (Peek() == '}') { i++; return d; }
                while (true)
                {
                    Ws();
                    if (Peek() != '"') throw new FormatException("項目名がありません");
                    string k = Str();
                    Ws();
                    if (Peek() != ':') throw new FormatException("':' がありません");
                    i++; Ws();
                    d[k] = Value();
                    Ws();
                    char c = Peek();
                    if (c == ',') { i++; continue; }
                    if (c == '}') { i++; return d; }
                    throw new FormatException("',' か '}' がありません");
                }
            }

            List<object> Arr()
            {
                var l = new List<object>();
                i++; Ws();
                if (Peek() == ']') { i++; return l; }
                while (true)
                {
                    Ws();
                    l.Add(Value());
                    Ws();
                    char c = Peek();
                    if (c == ',') { i++; continue; }
                    if (c == ']') { i++; return l; }
                    throw new FormatException("',' か ']' がありません");
                }
            }

            string Str()
            {
                var sb = new StringBuilder();
                i++;
                while (true)
                {
                    char c = Peek(); i++;
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    char e = Peek(); i++;
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new FormatException("\\u の後が足りません");
                            sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                        default: throw new FormatException("不明なエスケープです");
                    }
                }
            }

            double NumV()
            {
                int st = i;
                if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '-' || s[i] == '+')) i++;
                double v;
                if (!double.TryParse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    throw new FormatException("数値が読めません");
                return v;
            }
        }
    }

    // ======================================================================
    // 基本マップ
    // ======================================================================
    public static class Maps
    {
        /// <summary>対称配置の補助。P0 の位置で指定すると、他のプレイヤー分を対称に複製する。</summary>
        class Builder
        {
            public readonly MapDef M;
            readonly int sym;   // 2: 点対称, 3: 120度回転, 4: 上下左右の鏡映

            public Builder(string id, string name, int players)
            {
                sym = players;
                M = new MapDef { Id = "builtin/" + id, Name = name, Author = "公式", Players = players, BuiltIn = true };
            }

            List<float[]> Images(float x, float y)
            {
                var r = new List<float[]>();
                if (sym == 2) { r.Add(new[] { x, y }); r.Add(new[] { -x, -y }); }
                else if (sym == 4) { r.Add(new[] { x, y }); r.Add(new[] { -x, -y }); r.Add(new[] { x, -y }); r.Add(new[] { -x, y }); }
                else
                {
                    for (int k = 0; k < 3; k++)
                    {
                        double a = k * 2 * Math.PI / 3, cs = Math.Cos(a), sn = Math.Sin(a);
                        r.Add(new[] { (float)Math.Round(x * cs - y * sn, 2), (float)Math.Round(x * sn + y * cs, 2) });
                    }
                }
                return r;
            }

            /// <summary>プレイヤーの細胞（P0 の位置で指定。各プレイヤーに1つずつ）</summary>
            public Builder Player(float x, float y, int energy)
            {
                var im = Images(x, y);
                for (int p = 0; p < im.Count; p++) M.Cells.Add(new CellDef(im[p][0], im[p][1], p, energy));
                return this;
            }

            /// <summary>中立細胞（対称な位置にまとめて置く。軸上・中心では重複を省く）</summary>
            public Builder Neutral(float x, float y, int energy)
            {
                foreach (var p in Unique(Images(x, y))) M.Cells.Add(new CellDef(p[0], p[1], -1, energy));
                return this;
            }

            public Builder Wall(float x, float y, float r)
            {
                foreach (var p in Unique(Images(x, y))) M.Obstacles.Add(new ObstacleDef(p[0], p[1], r));
                return this;
            }

            static List<float[]> Unique(List<float[]> pts)
            {
                var r = new List<float[]>();
                foreach (var p in pts)
                {
                    bool dup = false;
                    foreach (var q in r) if (Math.Abs(p[0] - q[0]) < 0.01f && Math.Abs(p[1] - q[1]) < 0.01f) dup = true;
                    if (!dup) r.Add(p);
                }
                return r;
            }
        }

        static List<MapDef> all;

        /// <summary>基本マップの一覧（人数順）</summary>
        public static List<MapDef> BuiltIn
        {
            get
            {
                if (all != null) return all;
                all = new List<MapDef>
                {
                    // ---- 2人 ----
                    new Builder("duel", "対峙", 2)
                        .Player(-7.5f, -3.5f, 20).Player(-5.0f, -0.8f, 10)
                        .Neutral(-7.0f, 2.5f, 20).Neutral(-3.0f, -4.0f, 30).Neutral(-2.8f, 1.8f, 40)
                        .Neutral(-4.6f, 3.9f, 25).Neutral(-1.0f, -2.2f, 50).Neutral(0f, 0f, 100)
                        .Wall(-1.2f, 3.6f, 0.9f).M,
                    new Builder("corridor", "回廊", 2)
                        .Player(-8.2f, -3.8f, 20).Player(-8.2f, 0.6f, 10)
                        .Neutral(-5.8f, -1.6f, 20).Neutral(-5.5f, 2.8f, 25).Neutral(-3.2f, -4.0f, 30)
                        .Neutral(-2.8f, 0.2f, 40).Neutral(-1.2f, 3.6f, 35)
                        .Wall(0f, 0f, 1.2f).Wall(-1.4f, -2.2f, 0.6f).M,
                    new Builder("archipelago", "群島", 2)
                        .Player(-8.4f, -4.4f, 25)
                        .Neutral(-6.3f, -4.2f, 10).Neutral(-8.2f, -2.0f, 10).Neutral(-5.9f, -1.8f, 15)
                        .Neutral(-7.6f, 0.6f, 15).Neutral(-3.8f, -3.6f, 15).Neutral(-3.6f, -0.8f, 20)
                        .Neutral(-5.6f, 2.4f, 20).Neutral(-1.6f, 1.2f, 30).Neutral(-3.4f, 3.8f, 25)
                        .Neutral(-1.2f, -4.2f, 20).Neutral(0f, 0f, 60).M,
                    // ---- 3人 ----
                    new Builder("triad", "三角", 3)
                        .Player(0f, -4.6f, 20)
                        .Neutral(-1.7f, -3.4f, 15).Neutral(1.7f, -3.4f, 15).Neutral(0f, -2.0f, 30).Neutral(0f, 0f, 80).M,
                    new Builder("ring", "環", 3)
                        .Player(0f, -4.6f, 20)
                        .Neutral(2.77f, -1.6f, 30).Neutral(-1.5f, -3.6f, 15).Neutral(1.5f, -3.6f, 15).Neutral(0f, 0f, 100)
                        .Wall(0f, -2.0f, 0.6f).M,
                    new Builder("spokes", "放射", 3)
                        .Player(0f, -4.6f, 20)
                        .Neutral(0f, -3.0f, 15).Neutral(0f, -1.5f, 25).Neutral(2.6f, -1.5f, 40).Neutral(0f, 0f, 100)
                        .Wall(1.8f, -3.12f, 0.5f).Wall(-1.8f, -3.12f, 0.5f).M,
                    // ---- 4人 ----
                    new Builder("corners", "四隅", 4)
                        .Player(-8.3f, -4.4f, 20)
                        .Neutral(-5.8f, -4.2f, 15).Neutral(-8.2f, -1.8f, 15).Neutral(-5.2f, -1.6f, 30)
                        .Neutral(-2.5f, -2.0f, 25).Neutral(0f, -4.0f, 30).Neutral(-6.0f, 0f, 20).Neutral(0f, 0f, 100).M,
                    new Builder("cross", "十字", 4)
                        .Player(-8.3f, -4.4f, 20)
                        .Neutral(-6.0f, -3.0f, 20).Neutral(-3.0f, -4.4f, 25).Neutral(-7.6f, -1.2f, 15)
                        .Neutral(-2.4f, -1.6f, 35).Neutral(-2.0f, 0f, 30).Neutral(0f, 0f, 80)
                        .Wall(0f, -3.2f, 0.9f).Wall(-4.5f, 0f, 0.9f).M,
                    new Builder("lattice", "格子", 4)
                        .Player(-8.3f, -4.4f, 25)
                        .Neutral(-6.0f, -4.2f, 10).Neutral(-3.6f, -4.2f, 15).Neutral(-1.2f, -4.2f, 20)
                        .Neutral(-8.3f, -2.0f, 10).Neutral(-6.0f, -2.0f, 15).Neutral(-3.6f, -2.0f, 20).Neutral(-1.2f, -2.0f, 30)
                        .Neutral(-7.2f, 0f, 15).Neutral(-4.8f, 0f, 25).Neutral(-2.4f, 0f, 35).Neutral(0f, 0f, 50).M,
                };
                return all;
            }
        }

        public static MapDef Find(string id)
        {
            foreach (var m in BuiltIn) if (m.Id == id) return m;
            return null;
        }

        public static MapDef Duel() { return Find("builtin/duel").Clone(); }
    }
}
