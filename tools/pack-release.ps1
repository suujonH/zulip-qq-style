<#
.SYNOPSIS
    把发布目录打包成自动更新使用的 Release 文件。

.DESCRIPTION
    在发布目录中生成文件清单 ZulipQQ.files.sha256（更新时只替换清单中的文件），检查主程序的版本与 -Version 一致，
    然后在 -OutDir 中生成 ZulipQQ-v<版本>-win-x64.zip 和 ZulipQQ-update.json（版本、包名、大小、SHA-256）。
    发布目录中必须已有 ZulipQQ.exe 和 ZulipQQ.Updater.exe。

.EXAMPLE
    ./tools/pack-release.ps1 -Version 0.2.0 -PublishDir publish/ZulipQQ -OutDir .
#>
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$PublishDir,

    [Parameter(Mandatory)]
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'

# 与 AppVersion.TryParse 相同的语义版本格式：格式不对的 tag 发布后，已安装的程序无法识别
$numeric = '(0|[1-9]\d*)'
$identifier = '(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)'
if ($Version -notmatch "^$numeric\.$numeric\.$numeric(-$identifier(\.$identifier)*)?$") {
    throw "版本号 '$Version' 不是合法的语义版本（例如 0.2.0 或 0.2.0-beta.1）"
}

$publish = (Resolve-Path -LiteralPath $PublishDir).Path
foreach ($required in 'ZulipQQ.exe', 'ZulipQQ.Updater.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $required))) {
        throw "发布目录中缺少 $required"
    }
}

# 程序实际的版本必须与清单一致，否则更新后程序会认为自己仍是旧版本
$productVersion = (Get-Item -LiteralPath (Join-Path $publish 'ZulipQQ.exe')).VersionInfo.ProductVersion
if (($productVersion -split '\+')[0] -ne $Version) {
    throw "ZulipQQ.exe 的版本 '$productVersion' 与 '$Version' 不一致"
}

$listName = 'ZulipQQ.files.sha256'
$listPath = Join-Path $publish $listName
Remove-Item -LiteralPath $listPath -ErrorAction SilentlyContinue
$lines = Get-ChildItem -LiteralPath $publish -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($publish, $_.FullName)
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $relative"
}
[IO.File]::WriteAllLines($listPath, [string[]]$lines, [Text.UTF8Encoding]::new($false))

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$out = (Resolve-Path -LiteralPath $OutDir).Path
$package = "ZulipQQ-v$Version-win-x64.zip"
$zip = Join-Path $out $package
Remove-Item -LiteralPath $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip

$manifest = [ordered]@{
    version = $Version
    package = $package
    sha256  = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    size    = (Get-Item -LiteralPath $zip).Length
}
$manifestPath = Join-Path $out 'ZulipQQ-update.json'
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

Write-Host "已生成 $zip（$($manifest.size) 字节）和 $manifestPath"
