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
