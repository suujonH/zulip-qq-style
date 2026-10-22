<#
.SYNOPSIS
    编译内置插件（NativeAOT）并复制到程序的 plugins 目录。

.DESCRIPTION
    目前只有 samples/plugins/KeywordFilter。每个插件放到 <OutDir>\<plugin.json 中的 id>\，
    只包含 plugin.json 与 DLL。需要 Visual Studio 的 C++ 工具（NativeAOT 链接需要）。

.EXAMPLE
    ./tools/build-plugins.ps1 -OutDir publish/ZulipQQ/plugins
    ./tools/build-plugins.ps1 -OutDir ZulipQQ/bin/Debug/net10.0-windows/plugins
#>
param(
    [Parameter(Mandatory)]
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$plugins = @('samples/plugins/KeywordFilter')

# 部分机器上 vcvars 找不到 vswhere（NativeAOT 用它定位链接器）
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path $installer) { $env:PATH = "$installer;$env:PATH" }

foreach ($plugin in $plugins) {
    $dir = Join-Path $root $plugin
    $manifest = Get-Content (Join-Path $dir 'plugin.json') -Raw -Encoding utf8 | ConvertFrom-Json
    $staging = Join-Path ([IO.Path]::GetTempPath()) "zulipqq-plugin-$($manifest.id)"
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

    Write-Host "编译插件 $($manifest.id)"
    dotnet publish (Get-ChildItem $dir -Filter *.csproj | Select-Object -First 1).FullName -c Release -o $staging
    if ($LASTEXITCODE -ne 0) { throw "编译插件失败：$($manifest.id)" }

    $target = Join-Path $OutDir $manifest.id
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $staging 'plugin.json') $target -Force
    Copy-Item (Join-Path $staging $manifest.library) $target -Force
    Remove-Item $staging -Recurse -Force
    Write-Host "已复制到 $target"
}
