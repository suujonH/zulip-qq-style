# app（bin `zulip`）开发指南

Dioxus 0.7 desktop + WebView2。界面基础层：`ui/`（控件、复用块、统一流程）、`shell/`（窗口外壳、主题）、`platform/`（托盘等系统能力）。

## 硬性规则

页面**只组合、不自造**。下表已有的东西，页面必须用，不得再写一份，也不得写新的 CSS 变体：

| 需要 | 必须用 |
|------|--------|
| 删除任何对象 | `overlays.delete(目标, 后果, 操作)` |
| 其它确认后执行 | `overlays.confirm_then(Confirm, 失败文字, 操作)` |
| 只要确认结果 | `overlays.confirm(Confirm)`，带复选框用 `overlays.ask` |
| 重命名、新建分组、备注 | `overlays.prompt_text(TextPrompt)` |
| 离开有未保存内容的页 | `overlays.confirm_discard()` |
| 手动保存页的保存提示 | `UnsavedBar` |
| 操作失败的提示 | `overlays.attempt` 或 `overlays.report_error(“xx失败”, 错误)` |
| 复制文字 | `overlays.copy_text` |
| 打开文件、另存为 | `overlays.open_file`、`overlays.save_as` |
| 选择用户、频道、话题、转发目标 | `overlays.pick(Picker)`；结果要自定义时用 `PickerShell` |
| 任何列表的行 | `ListRow` |
| 设置项 | `SettingCard` + `SettingRow` |
| 用户、频道、话题名片 | `Popover` + `ProfileCard` + `ProfileField` |
| 文件（含上传、下载进度） | `FileCard` |
| 右键菜单 | `ListRow` / `FileCard` 的 `menu`，其它区域 `ContextMenuArea` |
| 其它对话框 | `overlays.open_dialog` / `ask_dialog` + `DialogFrame` + `DialogButtons` |

文案也统一，不要改写：确认框确定在左、取消在右且没有标题；删除文案是“删除{目标}？{后果}”；失败提示是“{操作}失败：{原因}”；未保存是“有未保存的内容。/ 放弃变更 / 留在本页”；复制是“已复制”。

现有组件缺能力时，给组件加属性，不要在页面里绕开。

## 组件

都从 `crate::ui` 导入。完整用法看 `src/gallery.rs`。

- **基础控件**：`Icon`、`IconName`、`Avatar`、`OverlapStack`、`Button`、`IconButton`、`TextInput`、`InlineEdit`、`ShortcutRecorder`、`Checkbox`、`Toggle`、`Slider`、`Select`、`Tabs`、`Badge`、`RoleBadge`、`Tooltip`、`Popover`、`Expander`、`ExpandPanel`、`SlideBar`、`CircleReveal`、`TraceBorder`、`HighlightText`
- **弹层**：`use_overlays()` 返回可复制的 `Overlays`，在任何事件处理和异步任务里都能用；`toast`、`show_menu`、`open_dialog`、`open_popover` 都在其上。`Esc` 按菜单、卡片、对话框的顺序关闭最上层
- **滚动条**：给滚动容器加 class `scroll`，自动隐藏，悬停显示，点轨道翻页
- **列表**：`use_virtual_list` + `VirtualList`

```rust
let list = use_virtual_list(VirtualListConfig { estimate: 64.0, stick_to_bottom: true, start_at_bottom: true, ..Default::default() });
rsx! { VirtualList { handle: list, keys: keys(), render: move |i| rsx! { MessageRow { id: ids.peek()[i] } }, on_reach_top: load_history } }
```

`keys` 是稳定的 `Rc<Vec<u64>>`；行高不定，自动测量；头部插入保持位置；`list.scroll_to_key`、`list.scroll_to_bottom`、`list.at_bottom`。**`render` 里读信号不会让列表重新渲染**，行内会变化的状态（选中等）要由行组件自己读。

流程的典型写法：

```rust
Button { onclick: move |_| async move {
    if overlays.delete("话题「a」", Some("话题中的所有消息将被永久删除。"), || api.delete_topic(id)).await.is_some() {
        refresh();
    }
}, "删除" }
```

## 结构

```
src/main.rs      进程入口：等旧进程、应用更新、单实例，然后启动根窗口
src/startup.rs   --wait-pid、单实例（命名互斥量 + 激活事件）
src/services.rs  进程级服务 services()：db、settings、hub；write_setting；窗口位置、主题的保存；UpdateHost
src/state.rs     窗口内状态和事件 hook；程序级命令 AppCommand
src/root.rs      根窗口：搬迁 → 登录 → 主界面三个阶段（同一个窗口）；托盘、闪烁、通知、退出流程
src/windows.rs   所有子窗口的打开入口
src/widgets/     接了会话数据的共用组件：RemoteAvatar、AccountSwitcher、名片、会话菜单
src/pages/<name>/  每个页面一个目录：mod.rs + page.css
```

## 状态 hook（`crate::state`）

`WindowShell` 提供窗口状态，窗口内的组件直接用：

| hook | 用途 |
|------|------|
| `use_app_state()` | `AppState`：`account`（`Signal<Option<i64>>`）、`session`（`Signal<Option<Arc<Session>>>`）、`accounts`（全部账号及状态） |
| `use_session()` | 当前会话（响应式） |
| `use_session_data(filter, read)` | 从会话读数据成信号：`filter` 为真的事件、`Resynced`、切换账号时重读，成批事件合并，结果不变不渲染；类型要 `Default + PartialEq` |
| `use_session_event(handler)` | 收这个窗口账号的原始事件；落后和切换账号都收到 `SessionEvent::Resynced`，这时全量重读 |
| `use_local_setting(读函数)` | 本机设置成信号；修改统一走 `services::write_setting(\|s\| s.set_xxx(v))`，所有窗口都会更新 |
| `use_app_command(handler)`、`send_command(AppCommand)` | 程序级命令：显示主窗口、退出、重启等，任何线程都能发 |

窗口跟随当前账号。根上下文有 `AccountScope(id)` 时绑定这个账号，账号结束时窗口自动关闭（`windows.rs` 里打开窗口时设置）。

退出流程先关闭所有子窗口再结束会话，要收尾的组件（保存草稿等）写在 `use_drop` 里。

## 窗口入口（`crate::windows`）

页面只调这些函数，窗口大小、标题、单例规则都登记在 `windows.rs`：

`open_chat(账号, 会话, 定位消息)`（唯一的打开会话入口：按设置开窗口或放主窗口右侧）、`open_chat_window`、`open_settings(SettingsTarget)`、`open_image_viewer(账号, 地址列表, 序号)`、`open_favorites`、`open_files`、`open_history(账号, 会话, 搜索词)`、`open_reminders`、`open_friend_manager`、`open_system_notices`、`open_search_more`、`open_add_account`、`show_notification`、`open_tray_messages`。

窗口内用 `shell::use_window_args::<T>()` 读参数；窗口已打开时再次调用会更新参数并激活。

## 加页面

1. 占位页已经在 `src/pages/<name>/`，直接替换 `mod.rs` 的内容，样式写在同目录的 `page.css`（颜色、字号只用 `assets/theme.css` 的变量）
2. 全新的页面：建 `src/pages/<name>/mod.rs` 和 `page.css`，在 `src/pages/mod.rs` 登记模块和样式；要开窗口时在 `windows.rs` 加入口：`open_with_args(WindowSpec::new(键, 标题, 大小), 根组件, 参数, setup)`，同一个键只开一个；根组件内容放进 `WindowShell`
3. 主题用 `use_theme()`
4. 点到人、话题用 `widgets::open_user_card`、`open_topic_card`；会话的右键菜单项用 `widgets::conversation_menu`、`level_menu_items`、`topic_more_submenu`；要登录才能下载的头像用 `RemoteAvatar`

聊天区的接口是 `ChatPane { conversation, focus }`（`key` 是 Dioxus 保留的属性名），换会话时用 `key: "{conversation}"` 重新挂载。

## 平台能力（`crate::platform`）

`use_tray`、`flash_taskbar`、`is_foreground`、`idle_time`、`play_notification_sound`、`system_prefers_dark`、`open_path`、`LaunchAtStartup`、`copy_text` / `paste_text` / `copy_image` / `paste_image`、`pick_file` / `pick_files` / `pick_folder` / `save_file`、`use_edge_hide`。页面一般不直接用 `open_path`、`save_file`、`copy_text`，走上面的流程。

## 用 CDP 验证

```powershell
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = '--remote-debugging-port=9222'
$env:ZULIP_GALLERY = '1'   # 打开组件预览；不设时是主窗口
$env:LOCALAPPDATA = 'D:	mp\zulip-test'   # 数据目录放到别处，不影响真实数据；只对这次启动设置
cargo run -p test-server -- 9991   # 假服务器 http://127.0.0.1:9991，账号 me@zulip.test，任意密码
cargo run -p zulip
```

`http://127.0.0.1:9222/json` 列出每个窗口的 WebSocket 地址，用 `Runtime.evaluate` 读状态、`Input.dispatchMouseEvent` 操作、`Page.captureScreenshot` 截图。

CDP 的鼠标事件不经过系统，以下必须用真实鼠标（`SetCursorPos` + `mouse_event`）验证：窗口拖动与缩放、光标形状、托盘。构建前先结束正在运行的 `zulip.exe`，否则链接失败。

新控件先加进 `src/gallery.rs` 再验证。
