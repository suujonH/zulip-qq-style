using System.Runtime.InteropServices;

namespace ZulipQQ.Plugins;

// 与 sdk/zulipqq_plugin.h 一一对应（x64，自然对齐）。字段顺序和类型必须与头文件一致，
// 大小与偏移由 AbiTests 断言；指针一律用 nint。

/// <summary>头文件中的常量。</summary>
public static class Zq
{
    /// <summary>
    /// 2：多账号（zq_message、zq_outgoing 末尾的 account_id，账号相关的宿主函数与导出函数）。
    /// 3：插件设置、消息附加内容、打开链接 / 运行程序、编辑 / 删除消息、修改频道与用户、插件间共享资源。
    /// </summary>
    public const int ApiVersion = 3;

    public const int EventIgnore = 0;
    public const int EventBlock = 1;

    public const int Ok = 0;
    public const int ErrUnknown = -1;
    public const int ErrInvalidArg = -2;
    public const int ErrPermission = -3;
    public const int ErrNotLoggedIn = -4;
    public const int ErrNetwork = -5;
    public const int ErrServer = -6;
    public const int ErrNotFound = -7;
    public const int ErrWrongContext = -8;

    public const int LogDebug = 0;
    public const int LogInfo = 10;
    public const int LogInfoSuccess = 11;
    public const int LogInfoRecv = 12;
    public const int LogInfoSend = 13;
    public const int LogWarning = 20;
    public const int LogError = 30;
    public const int LogFatal = 40;

    public const int ConvChannel = 1;
    public const int ConvDirect = 2;

    public const uint MsgOutgoing = 0x01;
    public const uint MsgMentioned = 0x02;
    public const uint MsgWildcardMention = 0x04;
    public const uint MsgStarred = 0x08;
    public const uint MsgRead = 0x10;
    public const uint MsgHistorical = 0x20;
}

[StructLayout(LayoutKind.Sequential)]
public struct ZqConversation
{
    public int Type;
    public int DmUserCount;
    public long StreamId;
    public nint StreamName;
    public nint Topic;
    public nint DmUserIds;
}

[StructLayout(LayoutKind.Sequential)]
public struct ZqMessage
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

[StructLayout(LayoutKind.Sequential)]
public struct ZqUser
{
    public uint StructSize;
    public int Role;
    public long UserId;
    public nint FullName;
    public nint Email;
    public int IsBot;
    public int IsActive;
    public nint Remark;
}

[StructLayout(LayoutKind.Sequential)]
public struct ZqChannel
{
    public uint StructSize;
    public int IsPrivate;
    public long StreamId;
    public nint Name;
    public nint Description;
}

[StructLayout(LayoutKind.Sequential)]
public struct ZqTopic
{
    public uint StructSize;
    public int Reserved;
    public long StreamId;
    public long MaxMessageId;
    public nint Name;
}

[StructLayout(LayoutKind.Sequential)]
public struct ZqOutgoing
{
    public uint StructSize;
    public uint Reserved;
    public ZqConversation Conv;
    public nint Content;
    public long AccountId;
}

[StructLayout(LayoutKind.Sequential)]
public struct ZqAccount
{
    public uint StructSize;
    public int IsPrimary;
    public long AccountId;
    public long UserId;
    public nint FullName;
    public nint Email;
    public nint RealmUrl;
}

/// <summary>宿主函数表：函数指针的类型见 <see cref="HostApi"/> 中对应的方法。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZqHostApi
{
    public uint StructSize;
    public uint ApiVersion;
    public nint Ctx;
    public nint AddLog;
    public nint IsLoggedIn;
    public nint GetLoginUserId;
    public nint GetLoginInfo;
    public nint GetRealmUrl;
    public nint GetDataDir;
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
    public nint SetOutgoingContent;
    public nint GetAccounts;
    public nint GetCurrentAccount;
    public nint SelectAccount;
    public nint GetSettings;
    public nint SetAnnotation;
    public nint Launch;
    public nint EditMessage;
    public nint DeleteMessage;
    public nint UpdateChannel;
    public nint UpdateUser;
    public nint SetSettingsFeedback;
    public nint PublishResources;
    public nint RequestResource;
    public nint ReplyResource;
    public nint ReplyPanel;
}
