# ZulipQQ

仿 QQ 界面的 [Zulip](https://zulip.com/) 桌面客户端（Windows），基于 Avalonia 12 与 .NET 10。

- Zulip 的话题（Topic）对应 QQ 的“群”，频道（Channel）对应“群分组”。
- 会话置顶、免打扰、好友分组、备注、下载记录等保存在本地 SQLite。
- 支持原生 DLL 插件（`sdk/zulipqq_plugin.h`），内置模型中心、翻译、回复助手三个插件。

## 构建

需要 .NET 10 SDK（Windows）。

```powershell
dotnet build ZulipQQ.slnx
dotnet test --solution ZulipQQ.slnx
dotnet run --project ZulipQQ
```

编译内置插件（NativeAOT，需要 Visual Studio 的 C++ 工具）：

```powershell
./tools/build-plugins.ps1 -OutDir ZulipQQ/bin/Debug/net10.0-windows/plugins
```

## 发布

- 推送到 `main` 或提交 PR 时，CI 编译、测试，并上传 win-x64 自包含的 `ZulipQQ.exe` 构建产物。
- 推送 `v*` 形式的 tag（例如 `v0.1.0`）后创建 Release，附带 `ZulipQQ-v<版本>-win-x64.zip`。

## 目录

| 目录 | 内容 |
|---|---|
| `ZulipQQ/` | 主程序 |
| `ZulipQQ.Tests/` | 单元测试 |
| `sdk/` | 插件 C 头文件 |
| `samples/plugins/` | 内置插件与示例 |
| `tools/` | 插件编译、种子数据、图标生成脚本 |
