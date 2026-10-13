// アプリ全体の入口。画面（ホーム・ステージ選択・対戦・エディタ）を切り替える
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public abstract class TWScreen
{
    protected TWApp App { get { return TWApp.I; } }
    protected RectTransform Root;
    public void Enter(RectTransform root) { Root = root; OnEnter(); }
    protected abstract void OnEnter();
    public virtual void Exit() { }
    public virtual void Tick() { }
}

public class TWApp : MonoBehaviour
{
    public static TWApp I { get; private set; }

    public Camera Cam { get; private set; }
    public Canvas Canvas { get; private set; }
    RectTransform screenRoot;
    TWScreen current;
    Text toast;
    Image toastPlate;
    float toastUntil;

    public TWHomeScreen Home { get; private set; }
    public TWStageSelectScreen StageSelect { get; private set; }
    public TWGameScreen Game { get; private set; }
    public TWEditorScreen Editor { get; private set; }
    public TWLobbyScreen Lobby { get; private set; }
    public TWRoomScreen Room { get; private set; }

    /// <summary>サーバー専用ビルド（または -server 引数）では画面を作らずサーバーだけ動かす</summary>
    public static bool IsServerOnly
    {
        get
        {
#if UNITY_SERVER
            return true;
#else
            return Application.isBatchMode || System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-server") >= 0;
#endif
        }
    }

    void Awake()
    {
        I = this;
        if (IsServerOnly)
        {
            Application.targetFrameRate = 30;
            TWNet.StartServer();
            enabled = false;
            return;
        }
        Application.targetFrameRate = 60;
        TWUI.Init();
        Cam = Camera.main;
        if (Cam != null)
        {
            Cam.orthographic = true;
            Cam.clearFlags = CameraClearFlags.SolidColor;
            Cam.backgroundColor = TWUI.Ink;
        }
        Canvas = TWUI.CreateCanvas("UI", 10);
        Canvas.transform.SetParent(transform, false);

        // 画面の上に重ねる通知（トースト）
        var overlay = TWUI.CreateCanvas("Overlay", 100);
        overlay.transform.SetParent(transform, false);
        toastPlate = TWUI.Panel(overlay.transform, TWUI.Ink, true, "ToastPlate");
        TWUI.Place(toastPlate.rectTransform, 0.28f, 0.12f, 0.72f, 0.19f);
        toastPlate.raycastTarget = false;
        foreach (var g in toastPlate.GetComponentsInChildren<Graphic>()) g.raycastTarget = false;
        toast = TWUI.Label(toastPlate.transform, "", 28, TWUI.Text, TextAnchor.MiddleCenter, "Toast");
        TWUI.Fill(toast.rectTransform, 10);
        toast.resizeTextForBestFit = false;
        var shadow = toast.gameObject.AddComponent<Outline>();
        shadow.effectColor = new Color(0, 0, 0, 0.8f); shadow.effectDistance = new Vector2(2, -2);

        Home = new TWHomeScreen();
        StageSelect = new TWStageSelectScreen();
        Game = new TWGameScreen();
        Editor = new TWEditorScreen();
        Lobby = new TWLobbyScreen();
        Room = new TWRoomScreen();
    }

    public void StartCoroutineSafe(IEnumerator e) { StartCoroutine(e); }

    void Start() { if (!IsServerOnly) Show(Home); }

    public void Show(TWScreen s)
    {
        if (current != null) current.Exit();
        if (screenRoot != null) TWUI.Destroy(screenRoot.gameObject);
        screenRoot = TWUI.Rect("Screen", Canvas.transform);
        TWUI.Fill(screenRoot);
        current = s;
        s.Enter(screenRoot);
    }

    public void Toast(string msg, float seconds = 2.2f)
    {
        toast.text = msg;
        toastUntil = Time.unscaledTime + seconds;
    }

    void Update()
    {
        if (current != null) current.Tick();
        float left = toastUntil - Time.unscaledTime;
        float a = Mathf.Clamp01(left / 0.4f);
        toast.color = new Color(TWUI.Text.r, TWUI.Text.g, TWUI.Text.b, a);
        toastPlate.gameObject.SetActive(a > 0.01f);
        toastPlate.color = new Color(TWUI.Ink.r, TWUI.Ink.g, TWUI.Ink.b, 0.92f * a);
    }

    // ---------------- 共通のダイアログ ----------------

    /// <summary>暗幕付きのダイアログ枠。閉じるときは返り値の GameObject を TWUI.Destroy する</summary>
    public RectTransform Modal(float x0, float y0, float x1, float y1, out GameObject handle)
    {
        var dim = TWUI.Rect("Modal", Canvas.transform);
        TWUI.Fill(dim);
        var img = dim.gameObject.AddComponent<Image>();
        img.color = new Color(0, 0, 0, 0.6f);
        var panel = TWUI.Panel(dim, TWUI.Agar, true, "Dialog");
        TWUI.Place(panel.rectTransform, x0, y0, x1, y1);
        handle = dim.gameObject;
        return panel.rectTransform;
    }

    /// <summary>はい／いいえの確認</summary>
    public void Confirm(string message, string yes, System.Action onYes, bool danger = false)
    {
        GameObject h;
        var p = Modal(0.32f, 0.36f, 0.68f, 0.64f, out h);
        var msg = TWUI.Label(p, message, 32, TWUI.Text, TextAnchor.MiddleCenter);
        TWUI.Place(msg.rectTransform, 0.06f, 0.38f, 0.94f, 0.92f);
        var no = TWUI.Button(p, "やめる", () => TWUI.Destroy(h), TWUI.Style.Normal);
        TWUI.Place(no.Rect, 0.06f, 0.08f, 0.47f, 0.32f);
        var ok = TWUI.Button(p, yes, () => { TWUI.Destroy(h); onYes(); }, danger ? TWUI.Style.Danger : TWUI.Style.Primary);
        TWUI.Place(ok.Rect, 0.53f, 0.08f, 0.94f, 0.32f);
    }
}
