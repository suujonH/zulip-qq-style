using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;

namespace ZulipQQ.Controls;

/// <summary>
/// 带格式的复制：剪贴板中同时放显示的文字（其它程序粘贴得到它）和 Markdown 原文（应用内的格式）。
/// 输入框 Ctrl+V 优先粘贴 Markdown，发送后与复制的内容一致；Ctrl+Shift+V 只粘贴文字。
/// </summary>
internal static class MarkdownClipboard
{
    private static readonly DataFormat<string> MarkdownFormat = DataFormat.CreateStringApplicationFormat("ZulipQQ.Markdown");

    /// <summary>复制 <paramref name="text"/> 与它的 Markdown；<paramref name="markdown"/> 为 null 时只复制文字。</summary>
    public static Task SetAsync(IClipboard clipboard, string text, string? markdown, Bitmap? bitmap = null)
    {
        var item = new DataTransferItem();
        if (bitmap is not null)
            item.SetBitmap(bitmap);
        if (text.Length > 0 || bitmap is null)
            item.SetText(text);
        if (!string.IsNullOrEmpty(markdown))
            item.Set(MarkdownFormat, markdown);
        var data = new DataTransfer();
        data.Add(item);
        return clipboard.SetDataAsync(data);
    }

    /// <summary>剪贴板中的 Markdown（由本程序复制）；没有时为 null。</summary>
    public static Task<string?> TryGetMarkdownAsync(IClipboard clipboard) => clipboard.TryGetValueAsync(MarkdownFormat);
}
