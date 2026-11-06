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
- 推送 `v*` 形式的 tag（例如 `v0.2.0`，必须是语义版本）后创建 Release，附带 `ZulipQQ-v<版本>-win-x64.zip` 和自动更新清单 `ZulipQQ-update.json`。tag 含 `-` 的是预发布版，不会推送给已安装的程序。

## 自动更新

从 Release 安装的程序启动后在后台检查最新的正式版，有新版本时静默下载并校验，在用户空闲一分钟后（或下次启动时）退出、替换、启动新版本。设置的“关于”页面有“确认版本”按钮可以手动检查。

- 替换由独立的 `ZulipQQ.Updater.exe` 完成，只动 `ZulipQQ.files.sha256` 中列出的文件，程序目录中的其它文件不受影响。
- 新版本启动并确认正常后才删除旧文件；否则恢复旧文件并启动旧版本。
- 新版本启动失败后不再自动尝试该版本；下载的包校验失败、替换失败累计 3 次后同样不再尝试。手动“确认版本”会重试，发布更高的版本后自动恢复。
- 运行状态在 `%LocalAppData%\ZulipQQ.Update`，日志在数据目录的 `logs\updater.log`。

## 目录

| 目录 | 内容 |
|---|---|
| `ZulipQQ/` | 主程序 |
| `ZulipQQ.Update/` | 自动更新的核心逻辑（版本比较、校验、替换与回滚） |
| `ZulipQQ.Updater/` | 独立更新器 |
| `ZulipQQ.Tests/` | 单元测试 |
| `sdk/` | 插件 C 头文件 |
| `samples/plugins/` | 内置插件与示例 |
| `tools/` | 插件编译、种子数据、图标生成脚本 |
