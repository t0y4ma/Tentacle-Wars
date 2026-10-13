// サーバーとクライアントの間でやり取りするメッセージ。形を変えたらサーバーとクライアントを同時に更新すること（Protocol を上げる）
using Mirror;

public static class TWProtocol
{
    public const int Version = 3;   // 3: 押し合いは接点で届いた扱い・押し戻しながら伸びる（2: 押し合い・片方向の接続）
    public const int MaxNameLength = 12;
    public const int MaxRoomNameLength = 20;
    public const int MaxChatLength = 120;
}

public enum TWRoomPhase : byte { Waiting = 0, Countdown = 1, Playing = 2 }

// ---------------- クライアント → サーバー ----------------

public struct TWHello : NetworkMessage { public int Protocol; public string Token; public string Name; }
public struct TWCreateRoom : NetworkMessage { public string Name; public string Password; public string MapJson; public int TimeLimitSec; public bool AllowSpectators; }
public struct TWJoinRoom : NetworkMessage { public int RoomId; public string Password; public bool Spectate; }
public struct TWLeaveRoom : NetworkMessage { }
public struct TWRoomSettings : NetworkMessage { public string MapJson; public int TimeLimitSec; public bool AllowSpectators; }
public struct TWTakeSeat : NetworkMessage { public int Seat; }                 // -1 = 観戦席へ
public struct TWSetCom : NetworkMessage { public int Seat; public bool Com; }   // 部屋主のみ
public struct TWSetReady : NetworkMessage { public bool Ready; }
public struct TWStartGame : NetworkMessage { }                                  // 部屋主のみ
public struct TWChat : NetworkMessage { public string Text; }
/// <summary>試合中の操作。Seat は部屋主が代打ちの席を動かすときだけ使う（自分の操作は -1）</summary>
public struct TWCmd : NetworkMessage { public byte Type; public int A; public int B; public int Seat; }
public struct TWResign : NetworkMessage { }
public struct TWSnapshotRequest : NetworkMessage { }

// ---------------- サーバー → クライアント ----------------

public struct TWWelcome : NetworkMessage { public bool Ok; public string Error; public int RejoinRoom; public string RejoinRoomName; }

public struct TWRoomInfo
{
    public int Id; public string Name; public bool Locked; public byte Phase;
    public int Players; public int Seats; public string MapName; public int Spectators; public bool AllowSpectators;
}
public struct TWRoomList : NetworkMessage { public TWRoomInfo[] Rooms; }

public struct TWSeatInfo
{
    public string Name;
    public bool Occupied;     // 人が座っている（切断中も含む）
    public bool Com;          // COM の席
    public bool Ready;
    public bool Connected;
    public bool Host;
    public bool Takeover;     // 切断中で COM が代打ちしている
    public bool Resigned;
}

public struct TWRoomState : NetworkMessage
{
    public int RoomId; public string Name; public bool Locked; public byte Phase;
    public string MapJson; public int TimeLimitSec; public bool AllowSpectators;
    public TWSeatInfo[] Seats; public string[] Spectators;
    public int YourSeat;      // -1 = 観戦
    public bool YouAreHost;
}

public struct TWChatLine : NetworkMessage { public string From; public int Seat; public string Text; public bool System; }

public struct TWGameStart : NetworkMessage
{
    public string MapJson; public int TimeLimitTicks; public int YourSeat; public string[] Names;
    public float Countdown;   // 開始までの秒数（途中参加なら 0）
    public bool Late;         // 途中参加：続けて送られるスナップショットを待つ
}

public struct TWCmdEntry { public int Tick; public byte Player; public byte Type; public int A; public int B; }

/// <summary>「UpTo tick まで確定。各 tick でこの操作を適用」。HashTick が 0 でなければその tick 後の盤面のチェックサム</summary>
public struct TWTickBundle : NetworkMessage { public int UpTo; public TWCmdEntry[] Cmds; public int HashTick; public uint Hash; }

public struct TWSnapshot : NetworkMessage { public byte[] State; }
public struct TWComDuty : NetworkMessage { public int[] Seats; }                 // 部屋主が代打ちする席
public struct TWGameOver : NetworkMessage { public int Winner; public int Tick; }
public struct TWError : NetworkMessage { public string Text; }
public struct TWKicked : NetworkMessage { public string Reason; }               // 部屋がなくなった等でロビーへ戻す
