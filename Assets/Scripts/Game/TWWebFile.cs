// ファイルの読み込み・書き出し・クリップボード。WebGL ではブラウザの機能（jslib）、エディタではファイルダイアログを使う
using System;
using System.Runtime.InteropServices;
using UnityEngine;

public class TWWebFile : MonoBehaviour
{
    static TWWebFile instance;
    Action<string> onOpened;

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern void TW_ArmOpenFile(string gameObject, string method, string accept);
    [DllImport("__Internal")] static extern void TW_DownloadText(string fileName, string text);
    [DllImport("__Internal")] static extern void TW_ArmCopyText(string text);
#endif

    static TWWebFile Instance
    {
        get
        {
            if (instance == null)
            {
                var go = new GameObject("TWWebFile");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<TWWebFile>();
            }
            return instance;
        }
    }

    /// <summary>ファイルを選んで中身を受け取る。ボタンを押し下げた瞬間（PointerDown）に呼ぶこと（ブラウザの制約）</summary>
    public static void ArmOpen(Action<string> onOpened)
    {
        var self = Instance;
        self.onOpened = onOpened;
#if UNITY_WEBGL && !UNITY_EDITOR
        TW_ArmOpenFile(self.gameObject.name, nameof(OnFileOpened), ".json,application/json");
#elif UNITY_EDITOR
        self.StartCoroutine(self.EditorOpen());
#else
        Debug.LogWarning("この環境ではファイル選択に対応していません。JSONを貼り付けてください");
#endif
    }

#if UNITY_EDITOR
    System.Collections.IEnumerator EditorOpen()
    {
        yield return null;   // 入力処理が終わってからダイアログを出す
        string path = UnityEditor.EditorUtility.OpenFilePanel("マップを読み込む", "", "json");
        if (!string.IsNullOrEmpty(path)) OnFileOpened(System.IO.File.ReadAllText(path));
    }
#endif

    /// <summary>jslib から呼ばれる</summary>
    public void OnFileOpened(string text)
    {
        var cb = onOpened;
        onOpened = null;
        if (cb != null) cb(text);
    }

    /// <summary>テキストをファイルとして保存（ダウンロード）する</summary>
    public static void Download(string fileName, string text)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        TW_DownloadText(fileName, text);
#elif UNITY_EDITOR
        string path = UnityEditor.EditorUtility.SaveFilePanel("マップを書き出す", "", fileName, "json");
        if (!string.IsNullOrEmpty(path)) System.IO.File.WriteAllText(path, text);
#else
        GUIUtility.systemCopyBuffer = text;
#endif
    }

    /// <summary>クリップボードへコピー。ボタンを押し下げた瞬間（PointerDown）に呼ぶこと</summary>
    public static void ArmCopy(string text)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        TW_ArmCopyText(text);
#else
        GUIUtility.systemCopyBuffer = text;
#endif
    }
}
