// 学習COM（強化学習で鍛えたCOM）の重みを読み込み、強さの設定から COM を作る
using TW.Sim;
using UnityEngine;

public static class TWLearnedCom
{
    static bool tried;

    /// <summary>学習COMの重みがあるか（Resources/TW/policy.txt）</summary>
    public static bool Available { get { Ensure(); return LearnedAI.SharedNet != null; } }

    public static void Ensure()
    {
        if (tried) return;
        tried = true;
        var ta = Resources.Load<TextAsset>("TW/policy");
        if (ta == null) { Debug.LogWarning("[TW] 学習COMの重みがありません。学習COMは標準のCOMで代わります"); return; }
        try { LearnedAI.SharedNet = PolicyNet.Parse(ta.text); }
        catch (System.Exception e) { Debug.LogError("[TW] 学習COMの重みを読めません: " + e.Message); }
    }

    public static IComPlayer Create(int player, ComProfile p)
    {
        Ensure();
        return ComFactory.Create(player, p, Random.Range(1, 99999));
    }
}
