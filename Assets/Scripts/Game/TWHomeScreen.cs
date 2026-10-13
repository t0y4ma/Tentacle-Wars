// ホーム画面。背景では COM 同士の対戦がそのまま流れている
using TW.Sim;
using UnityEngine;
using UnityEngine.UI;

public class TWHomeScreen : TWScreen
{
    TWBoardView board;
    Simulation sim;
    SimpleAI[] ais;
    float acc, restartAt;

    protected override void OnEnter()
    {
        board = new TWBoardView("HomeBoard");
        board.ShowNeutralGauge = false;
        NewDemo();

        // 左側を暗くして文字を読みやすくする（盤面は右側に見せる）
        var shade = TWUI.Rect("Shade", Root); TWUI.Place(shade, 0, 0, 0.62f, 1);
        var simg = shade.gameObject.AddComponent<Image>();
        simg.sprite = MakeFade(); simg.color = new Color(TWUI.Ink.r, TWUI.Ink.g, TWUI.Ink.b, 0.92f); simg.raycastTarget = false;

        var title = TWUI.Label(Root, "TENTACLE\nWARS", 150, TWUI.Text, TextAnchor.LowerLeft, "Title");
        TWUI.Place(title.rectTransform, 0.06f, 0.56f, 0.6f, 0.9f);
        title.lineSpacing = 0.85f;
        var sub = TWUI.Label(Root, "細胞をつなぎ、送り、奪いあう。", 36, TWUI.Lime, TextAnchor.UpperLeft, "Sub");
        TWUI.Place(sub.rectTransform, 0.065f, 0.49f, 0.6f, 0.55f);

        var local = TWUI.Button(Root, "ローカル対戦", () => App.Show(App.StageSelect), TWUI.Style.Primary, 40);
        TWUI.Place(local.Rect, 0.06f, 0.33f, 0.32f, 0.43f);
        var online = TWUI.Button(Root, "オンライン対戦", () => App.Show(App.Lobby), TWUI.Style.Normal, 30);
        TWUI.Place(online.Rect, 0.06f, 0.24f, 0.32f, 0.31f);
        var edit = TWUI.Button(Root, "マップエディタ", () => { App.Editor.OpenNew(); App.Show(App.Editor); }, TWUI.Style.Normal, 30);
        TWUI.Place(edit.Rect, 0.06f, 0.15f, 0.32f, 0.22f);

        var hint = TWUI.Label(Root, "自分の細胞からドラッグで触手を伸ばし、触手をなぞって切る。", 22, TWUI.TextDim, TextAnchor.LowerLeft, "Hint");
        TWUI.Place(hint.rectTransform, 0.06f, 0.04f, 0.6f, 0.09f);
    }

    static Sprite fade;
    static Sprite MakeFade()
    {
        if (fade != null) return fade;
        var tex = new Texture2D(64, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (int x = 0; x < 64; x++) tex.SetPixel(x, 0, new Color(1, 1, 1, Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(30, 63, x))));
        tex.Apply();
        fade = Sprite.Create(tex, new Rect(0, 0, 64, 1), new Vector2(0.5f, 0.5f));
        return fade;
    }

    void NewDemo()
    {
        var maps = Maps.BuiltIn;
        var map = maps[Random.Range(0, maps.Count)];
        sim = new Simulation(map);
        ais = new SimpleAI[map.Players];
        for (int p = 0; p < map.Players; p++) ais[p] = new SimpleAI(p, Random.Range(1, 99999));
        board.Build(sim);
        restartAt = 0;
    }

    public override void Tick()
    {
        TWBoardView.FitCamera(App.Cam, 0.2f, 0, 1, 1, 0.4f);
        float dt = 1f / Balance.TicksPerSecond;
        acc += Time.deltaTime;
        int guard = 0;
        while (acc >= dt && guard++ < 10)
        {
            acc -= dt;
            foreach (var a in ais) a.Think(sim);
            sim.Step();
        }
        if (sim.Over)
        {
            if (restartAt == 0) restartAt = Time.time + 3f;
            else if (Time.time > restartAt) NewDemo();
        }
        board.Render(sim, Mathf.Clamp01(acc / dt));
        board.EndFrame();
    }

    public override void Exit()
    {
        if (board != null) board.Destroy();
        board = null;
    }
}
