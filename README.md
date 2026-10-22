# ZulipQQ

仿 QQ 界面的 [Zulip](https://zulip.com/) 桌面客户端（Windows），基于 Avalonia 12 与 .NET 10。

- Zulip 的话题（Topic）对应 QQ 的“群”，频道（Channel）对应“群分组”。
- Zulip 没有的能力（会话置顶、四级免打扰、好友分组、备注、下载记录等）保存在本地 SQLite。
- 提供原生 DLL 插件接口（参考 CoolQ），可获取 / 发送消息、接收新消息回调、过滤消息。

## 构建

需要 .NET 10 SDK（Windows）。

```powershell
dotnet build ZulipQQ.slnx
dotnet test --solution ZulipQQ.slnx
dotnet run --project ZulipQQ
```

## 插件

插件是导出 `zq_plugin_init` 的 x64 原生 DLL，接口定义见 [`sdk/zulipqq_plugin.h`](sdk/zulipqq_plugin.h)。
安装目录为 `<程序目录>\plugins\<id>\`，其中放 `plugin.json` 与 DLL；插件随程序启动，
可在“设置 > 插件”中启用、停用并查看日志。

| 示例 | 说明 |
|---|---|
| `samples/plugins/KeywordFilter` | C# NativeAOT，随程序内置：按 `keywords.txt` 屏蔽消息，发送时替换 `{time}` |
| `samples/plugins/EchoC` | C 语言（`build.cmd`）：私聊收到 `ping` 回复 `pong` |

本地编译内置插件（需要 Visual Studio 的 C++ 工具）：

```powershell
./tools/build-plugins.ps1 -OutDir ZulipQQ/bin/Debug/net10.0-windows/plugins
```

## 发布

推送 `v*` 形式的 tag（例如 `v0.1.0`）后，GitHub Actions 会编译 win-x64 自包含版本，
打包为 `ZulipQQ-v<版本>-win-x64.zip` 并创建 Release。

```powershell
git tag v0.1.0
git push origin v0.1.0
```

## 目录

| 目录 | 内容 |
|---|---|
| `ZulipQQ/` | 主程序 |
| `ZulipQQ.Tests/` | 单元测试 |
| `sdk/` | 插件 C 头文件 `zulipqq_plugin.h` |
| `samples/plugins/` | 示例插件 |
| `tools/` | 测试服务器说明、种子数据脚本、图标生成、插件编译脚本 |
