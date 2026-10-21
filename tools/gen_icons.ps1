#requires -Version 7
<#
.SYNOPSIS
  从 TDesign Icons（tdesign-icons-svg）生成 ZulipQQ/Styles/Icons.axaml。

.DESCRIPTION
  需要新图标时，在 $icons 中追加一行后重新运行本脚本，不要手改 Icons.axaml。
  TDesign 没有的图标写在 $manual 中，原样输出。
  某个名字下载返回 404 时，换成语义最接近的 TDesign 图标，并在 $icons 的注释中说明。
#>
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\ZulipQQ\Styles\Icons.axaml')
)

$ErrorActionPreference = 'Stop'
$version = '0.4.7'

# 资源 Key 后缀 → TDesign 图标名（按输出顺序排列）；值以 'manual:' 开头的取自 $manual
$icons = [ordered]@{
    QqFilled        = 'logo-qq-filled'
    Chat            = 'chat'
    ChatFilled      = 'chat-filled'
    User            = 'user'
    UserFilled      = 'user-filled'
    Mobile          = 'mobile'
    ViewList        = 'view-list'
    Search          = 'search'
    Add             = 'add'
    UserSetting     = 'user-setting'
    ChevronRight    = 'chevron-right'
    Moon            = 'moon'
    Layout          = 'layout'
    Minus           = 'minus'
    Rectangle       = 'rectangle'
    Close           = 'close'
    NotificationOff = 'manual:NotificationOff'
    Edit            = 'edit'
    Check           = 'check'
    ChevronDown     = 'chevron-down'
    ArrowRightUp    = 'arrow-right-up'
    Forbid          = 'manual:Forbid'
    ChevronUpDouble = 'chevron-up-double'
    ChevronLeft     = 'chevron-left'
    Lock            = 'lock-on'
    Usergroup       = 'usergroup'
    Smile           = 'smile'
    Folder          = 'folder'
    Image           = 'image'
    File            = 'file'
    Time            = 'time'
    History         = 'history'
    More            = 'ellipsis'
    Star            = 'star'
    StarFilled      = 'star-filled'
    Setting         = 'setting'
    Palette         = 'palette'
    Share           = 'share'
    Link            = 'link'
    Download        = 'download'
    Logout          = 'logout'
    Pin             = 'pin'
    Filter          = 'filter'
    Location        = 'location'
    Video           = 'video'
    Quote           = 'quote'
    ErrorCircle     = 'error-circle'
    Loading         = 'loading'
}

# TDesign 没有的图标：注释 + 属性
$manual = @{
    NotificationOff = @{
        Comment = 'notification + 斜线（TDesign 没有 notification-off，用铃铛加斜线组合出免打扰图标）'
        Attrs   = ' StrokeData="M5 8V13L3 16V19H21V16L19 13V8C19 4.13401 15.866 1 12 1C8.13401 1 5 4.13401 5 8Z M8.5 19H15.5C15.5 20.933 13.933 22.5 12 22.5C10.067 22.5 8.5 20.933 8.5 19Z M2 2L22 22"'
    }
    Forbid          = @{
        Comment = '圆圈 + 斜线（TDesign 没有 forbid，按 QQ 屏蔽群消息的图标组合）'
        Attrs   = ' StrokeData="M21 12A9 9 0 1 1 3 12A9 9 0 1 1 21 12Z M5.63604 5.63604L18.364 18.364"'
    }
}

function Convert-Svg([string]$svg) {
    $strokes = @(); $fills = @(); $evenOdd = $false
    foreach ($m in [regex]::Matches($svg, '<path\b[^>]*>')) {
        $p = $m.Value
        $d = [regex]::Match($p, '\sd="([^"]+)"').Groups[1].Value
        if ($p -match 'stroke="currentColor"') { $strokes += $d }
        elseif ($p -match 'fill="currentColor"') {
            $fills += $d
            if ($p -match 'fill-rule="evenodd"') { $evenOdd = $true }
        }
    }
    $attrs = ''
    if ($strokes.Count) { $attrs += " StrokeData=`"$($strokes -join ' ')`"" }
    if ($fills.Count) {
        $prefix = if ($evenOdd) { 'F0 ' } else { 'F1 ' }
        $attrs += " FillData=`"$prefix$($fills -join ' ')`""
    }
    if (-not $attrs) { throw '图标中没有可用的 path' }
    return $attrs
}

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("<!-- 由 TDesign Icons (https://tdesign.tencent.com/) 的 tdesign-icons-svg@$version 包生成，图标网格 24x24。请勿手改，修改 tools/gen_icons.ps1 后重新生成 -->")
[void]$sb.AppendLine('<ResourceDictionary xmlns="https://github.com/avaloniaui"')
[void]$sb.AppendLine('                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"')
[void]$sb.AppendLine('                    xmlns:c="using:ZulipQQ.Controls">')

foreach ($key in $icons.Keys) {
    $name = $icons[$key]
    if ($name.StartsWith('manual:')) {
        $entry = $manual[$name.Substring(7)]
        [void]$sb.AppendLine("    <!-- $($entry.Comment) -->")
        [void]$sb.AppendLine("    <c:TdIconData x:Key=`"Icon.$key`"$($entry.Attrs) />")
        continue
    }

    $url = "https://unpkg.com/tdesign-icons-svg@$version/src/$name.svg"
    try {
        $svg = (Invoke-WebRequest -UseBasicParsing $url -TimeoutSec 30).Content
    }
    catch {
        throw "下载 $name 失败（$url）：$($_.Exception.Message)"
    }
    [void]$sb.AppendLine("    <!-- $name -->")
    [void]$sb.AppendLine("    <c:TdIconData x:Key=`"Icon.$key`"$(Convert-Svg $svg) />")
}

[void]$sb.AppendLine('</ResourceDictionary>')
[System.IO.File]::WriteAllText((Resolve-Path -LiteralPath (Split-Path $Out)).Path + '\' + (Split-Path $Out -Leaf), $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Host "已生成 $Out（$($icons.Count) 个图标）"
