using System;
using System.Collections.Generic;
using System.IO;

namespace ZulipQQ.Core;

/// <summary>文件窗口“筛选”下拉的分类。</summary>
public enum FileKind
{
    Image,
    Video,
    Document,
    Archive,
    Other,
}

/// <summary>按扩展名判断文件分类和图标。</summary>
public static class FileKinds
{
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".csv", ".ppt", ".pptx",
        ".txt", ".md", ".rtf", ".odt", ".ods", ".odp",
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz",
    };

    public static FileKind Of(string path)
    {
        if (ZulipHtml.IsImagePath(path))
            return FileKind.Image;
        if (ZulipHtml.IsVideoPath(path))
            return FileKind.Video;
        var ext = Path.GetExtension(path);
        if (DocumentExtensions.Contains(ext))
            return FileKind.Document;
        return ArchiveExtensions.Contains(ext) ? FileKind.Archive : FileKind.Other;
    }

    /// <summary>文件图标的资源名（<c>Icon.*</c>）。</summary>
    public static string IconKey(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "Icon.FilePdf",
        ".doc" or ".docx" or ".odt" or ".rtf" => "Icon.FileWord",
        ".xls" or ".xlsx" or ".csv" or ".ods" => "Icon.FileExcel",
        ".ppt" or ".pptx" or ".odp" => "Icon.FilePowerpoint",
        _ => Of(path) switch
        {
            FileKind.Image => "Icon.Image",
            FileKind.Video => "Icon.Video",
            FileKind.Archive => "Icon.FileZip",
            FileKind.Document => "Icon.File",
            _ => "Icon.FileUnknown",
        },
    };
}
