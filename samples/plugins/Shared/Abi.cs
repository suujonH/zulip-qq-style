using System.Runtime.InteropServices;

namespace PluginShared;

// 与 sdk/zulipqq_plugin.h 对应的结构体（只声明内置 AI 插件用到的部分；字段顺序与类型必须与头文件一致）

[StructLayout(LayoutKind.Sequential)]
internal struct ZqConversation
{
    public int Type;
    public int DmUserCount;
    public long StreamId;
    public nint StreamName;
    public nint Topic;
    public nint DmUserIds;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ZqMessage
{
    public uint StructSize;
    public uint Flags;
    public long MessageId;
    public long Timestamp;
    public long SenderId;
    public nint SenderName;
    public nint SenderEmail;
    public ZqConversation Conv;
    public nint ContentHtml;
    public nint ContentText;
    public long AccountId;
}

/// <summary>宿主函数表；函数指针按头文件顺序排列，只给用到的声明类型。</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ZqHostApi
{
    public uint StructSize;
    public uint ApiVersion;
    public nint Ctx;
    public delegate* unmanaged[Cdecl]<nint, int, byte*, byte*, void> AddLog;
    public nint IsLoggedIn;
    public nint GetLoginUserId;
    public nint GetLoginInfo;
    public nint GetRealmUrl;
    public delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<byte*, nint, void>, nint, int> GetDataDir;
    public nint SendChannelMessage;
    public nint SendDirectMessage;
    public nint GetMessage;
    public delegate* unmanaged[Cdecl]<nint, ZqConversation*, long, int, int, delegate* unmanaged[Cdecl]<ZqMessage*, nint, void>, nint, int> GetMessages;
    public nint GetUsers;
    public nint GetUser;
    public nint GetChannels;
    public nint GetTopics;
    public nint AddReaction;
    public nint SetStarred;
    public nint MarkRead;
    public nint UploadFile;
    public nint SetOutgoingContent;
    public nint GetAccounts;
    public nint GetCurrentAccount;
    public delegate* unmanaged[Cdecl]<nint, long, int> SelectAccount;
    public delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<byte*, nint, void>, nint, int> GetSettings;
    public delegate* unmanaged[Cdecl]<nint, long, byte*, int> SetAnnotation;
    public nint Launch;
    public nint EditMessage;
    public nint DeleteMessage;
    public nint UpdateChannel;
    public nint UpdateUser;
    public delegate* unmanaged[Cdecl]<nint, byte*, int> SetSettingsFeedback;
    public delegate* unmanaged[Cdecl]<nint, byte*, byte*, int> PublishResources;
    public delegate* unmanaged[Cdecl]<nint, byte*, delegate* unmanaged[Cdecl]<byte*, nint, void>, nint, int> RequestResource;
    public delegate* unmanaged[Cdecl]<nint, long, byte*, int> ReplyResource;
    public delegate* unmanaged[Cdecl]<nint, byte*, int> ReplyPanel;
}

internal static class Zq
{
    public const int ApiVersion = 3;
    public const int Ok = 0;
    public const int ErrInvalidArg = -2;
    public const int ErrPermission = -3;
    public const int ErrNetwork = -5;
    public const int ErrNotFound = -7;
    public const int ErrWrongContext = -8;

    public const int ConvChannel = 1;
    public const int ConvDirect = 2;

    public const uint MsgOutgoing = 0x01;

    public const int LogDebug = 0;
    public const int LogInfo = 10;
    public const int LogWarning = 20;
    public const int LogError = 30;
}
