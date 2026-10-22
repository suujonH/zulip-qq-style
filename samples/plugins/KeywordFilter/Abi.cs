using System.Runtime.InteropServices;

namespace KeywordFilter;

// 与 sdk/zulipqq_plugin.h 对应的结构体（只声明本插件用到的部分；字段顺序与类型必须与头文件一致）

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
}

[StructLayout(LayoutKind.Sequential)]
internal struct ZqOutgoing
{
    public uint StructSize;
    public uint Reserved;
    public ZqConversation Conv;
    public nint Content;
}

/// <summary>宿主函数表；函数指针按头文件顺序排列，本插件只调用其中几个。</summary>
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
    public nint GetMessages;
    public nint GetUsers;
    public nint GetUser;
    public nint GetChannels;
    public nint GetTopics;
    public nint AddReaction;
    public nint SetStarred;
    public nint MarkRead;
    public nint UploadFile;
    public delegate* unmanaged[Cdecl]<nint, byte*, int> SetOutgoingContent;
}

internal static class Zq
{
    public const int ApiVersion = 1;
    public const int Ok = 0;
    public const int ErrInvalidArg = -2;
    public const int EventIgnore = 0;
    public const int EventBlock = 1;
    public const uint MsgOutgoing = 0x01;

    public const int LogDebug = 0;
    public const int LogInfo = 10;
    public const int LogWarning = 20;
    public const int LogError = 30;
}
