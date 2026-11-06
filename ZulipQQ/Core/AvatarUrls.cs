namespace ZulipQQ.Core;

/// <summary>头像地址。</summary>
public static class AvatarUrls
{
    /// <summary>
    /// 查看大图用的地址：Zulip 上传的头像 <c>xxx.png</c> 只有 100 像素，同目录的 <c>xxx-medium.png</c> 是 500 像素。
    /// 其它地址（如 Gravatar）原样返回。
    /// </summary>
    public static string Large(string url)
    {
        var query = url.IndexOf('?');
        var path = query < 0 ? url : url[..query];
        if (!path.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("-medium.png", System.StringComparison.OrdinalIgnoreCase))
            return url;
        return path[..^".png".Length] + "-medium.png" + (query < 0 ? "" : url[query..]);
    }
}
