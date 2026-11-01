using Avalonia;
using ZulipQQ.Zulip;

namespace ZulipQQ.Controls;

/// <summary>
/// 图片所属账号的客户端：可继承的附加属性，由会话窗口在根元素上设置（主窗口切换账号时更新）。
/// <see cref="Avatar"/>、<see cref="RemoteImage"/> 用它解析组织内的相对地址并带认证下载，值变化时重新加载。
/// </summary>
public sealed class RealmImage
{
    public static readonly AttachedProperty<ZulipClient?> ClientProperty =
        AvaloniaProperty.RegisterAttached<RealmImage, AvaloniaObject, ZulipClient?>("Client", inherits: true);

    private RealmImage()
    {
    }

    public static ZulipClient? GetClient(AvaloniaObject element) => element.GetValue(ClientProperty);

    public static void SetClient(AvaloniaObject element, ZulipClient? value) => element.SetValue(ClientProperty, value);
}
