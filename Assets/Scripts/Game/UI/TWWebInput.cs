// ブラウザ版の文字入力。uGUI の入力欄はブラウザでは日本語入力などが使えないため、
// 押された入力欄の上に HTML の入力欄を重ねて入力させ、打った文字をこちらへ戻す（TWInput.jslib）。
// エディタ・その他の環境では何もしない（uGUI の入力欄がそのまま使われる）。
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

[RequireComponent(typeof(InputField))]
public class TWWebInput : MonoBehaviour, IPointerDownHandler
{
#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern void TW_ArmInput(string go, int id, string text, float x, float y, float w, float h, float font, int multiline, int maxLen, int password, string colors);
    [DllImport("__Internal")] static extern void TW_CloseInput();
    const bool Enabled = true;
#else
    const bool Enabled = false;
#endif

    static readonly Dictionary<int, TWWebInput> all = new Dictionary<int, TWWebInput>();
    static int nextId, activeId;
    static int submittedFrame = -1;

    /// <summary>このフレームの入力終了が Enter によるものか（チャットの送信などに使う）</summary>
    public static bool SubmittedThisFrame { get { return submittedFrame == Time.frameCount; } }

    InputField field;
    int id;

    void Awake()
    {
        field = GetComponent<InputField>();
        id = ++nextId;
        all[id] = this;
        if (Enabled)
        {
            field.readOnly = true;   // 文字は HTML の入力欄で受け付ける
            TWWebInputReceiver.Ensure();
        }
    }

    void OnDisable()
    {
        if (activeId == id) Close();
    }

    void OnDestroy()
    {
        all.Remove(id);
        if (activeId == id) Close();
    }

    public void OnPointerDown(PointerEventData e)
    {
        if (!Enabled || field == null || !field.IsInteractable()) return;
        var rt = (RectTransform)transform;
        var canvas = GetComponentInParent<Canvas>().rootCanvas;
        Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var corners = new Vector3[4];
        rt.GetWorldCorners(corners);
        Vector2 bl = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
        Vector2 tr = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
        float font = field.textComponent != null ? field.textComponent.fontSize * canvas.scaleFactor : 24 * canvas.scaleFactor;
        string colors = "#" + ColorUtility.ToHtmlStringRGB(TWUI.Text) + ",#" + ColorUtility.ToHtmlStringRGB(TWUI.Ink) + ",#" + ColorUtility.ToHtmlStringRGB(TWUI.Lime);
        activeId = id;
#if UNITY_WEBGL && !UNITY_EDITOR
        SetCapture(false);   // キーを Unity が横取りしないように（HTML 側でも伝わらないよう止めている）
        TW_ArmInput(TWWebInputReceiver.Name, id, field.text, bl.x, bl.y, tr.x - bl.x, tr.y - bl.y, font,
            field.lineType == InputField.LineType.SingleLine ? 0 : 1, field.characterLimit,
            field.contentType == InputField.ContentType.Password ? 1 : 0, colors);
#endif
    }

    static void Close()
    {
        activeId = 0;
#if UNITY_WEBGL && !UNITY_EDITOR
        TW_CloseInput();
        SetCapture(true);
#endif
    }

    /// <summary>WebGLInput.captureAllKeyboardInput を切り替える（Unity の版によっては無いので、あるときだけ）</summary>
    static void SetCapture(bool on)
    {
        var type = System.Type.GetType("UnityEngine.WebGLInput, UnityEngine.WebGLModule") ?? System.Type.GetType("UnityEngine.WebGLInput, UnityEngine.CoreModule");
        var prop = type != null ? type.GetProperty("captureAllKeyboardInput") : null;
        if (prop != null) prop.SetValue(null, on, null);
    }

    // ---- ブラウザからの知らせ ----

    static TWWebInput Find(string payload, out string rest)
    {
        rest = "";
        int nl = payload.IndexOf('\n');
        if (nl < 0) return null;
        int fid;
        if (!int.TryParse(payload.Substring(0, nl), out fid)) return null;
        rest = payload.Substring(nl + 1);
        TWWebInput w;
        return all.TryGetValue(fid, out w) && w != null ? w : null;
    }

    internal static void HandleText(string payload)
    {
        string value;
        var w = Find(payload, out value);
        if (w != null && w.field != null) w.field.text = value;
    }

    internal static void HandleEnd(string payload)
    {
        string rest;
        var w = Find(payload, out rest);
        int nl = rest.IndexOf('\n');
        bool submit = nl > 0 && rest.Substring(0, nl) == "1";
        string value = nl >= 0 ? rest.Substring(nl + 1) : rest;
        activeId = 0;
#if UNITY_WEBGL && !UNITY_EDITOR
        SetCapture(true);
#endif
        if (w == null || w.field == null) return;
        w.field.text = value;
        if (submit) submittedFrame = Time.frameCount;
        w.field.onEndEdit.Invoke(w.field.text);
        if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == w.gameObject)
            EventSystem.current.SetSelectedGameObject(null);
    }
}

/// <summary>ブラウザ（SendMessage）からの受け口</summary>
public class TWWebInputReceiver : MonoBehaviour
{
    public const string Name = "TWWebInput";
    static TWWebInputReceiver instance;

    public static void Ensure()
    {
        if (instance != null) return;
        var go = new GameObject(Name);
        DontDestroyOnLoad(go);
        instance = go.AddComponent<TWWebInputReceiver>();
    }

    public void OnText(string payload) { TWWebInput.HandleText(payload); }
    public void OnEnd(string payload) { TWWebInput.HandleEnd(payload); }
}
