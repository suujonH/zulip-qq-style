//! 窗口配置、多窗口（打开 / 激活单例）、窗口位置保存。

use super::local_files;
use super::state::AppShared;
use dioxus::desktop::tao::dpi::{LogicalPosition, PhysicalPosition};
use dioxus::desktop::tao::event::{Event, WindowEvent};
use dioxus::desktop::tao::platform::windows::{IconExtWindows, WindowBuilderExtWindows};
use dioxus::desktop::tao::window::{Icon, Window};
use dioxus::desktop::{
    Config, LogicalSize, WeakDesktopContext, WindowBuilder, WindowCloseBehaviour,
    use_wry_event_handler, window,
};
use dioxus::dioxus_core::{Task, spawn_forever};
use dioxus::prelude::*;
use std::any::Any;
use std::cell::RefCell;
use std::collections::HashMap;
use std::path::PathBuf;
use std::rc::Rc;
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::watch;
use zulip_core::window::WindowBounds;

const THEME_CSS: &str = include_str!("../../assets/theme.css");
const UI_CSS: &str = include_str!("../../assets/ui.css");
const UI_JS: &str = include_str!("../../assets/ui.js");
/// winresource 给图标的资源 ID。
const SAVE_BOUNDS_DELAY: Duration = Duration::from_millis(500);

/// 窗口位置的保存位置，由设置层实现。
pub trait WindowBoundsStore: Send + Sync {
    fn load(&self, key: &str) -> Option<WindowBounds>;
    fn save(&self, key: &str, bounds: WindowBounds);
}

/// 窗口的创建参数。
#[derive(Clone, Debug)]
pub struct WindowSpec {
    /// 单例键。
    pub key: String,
    /// 窗口位置的保存键；同类窗口（聊天窗口）可以共用。
    pub bounds_key: String,
    pub title: String,
    pub size: (f64, f64),
    pub min_size: (f64, f64),
    pub resizable: bool,
    /// 关闭时只隐藏（主窗口配合托盘）。
    pub hide_on_close: bool,
    pub remember_bounds: bool,
    /// 通知类小窗：置顶、不进任务栏、打开和更新时不抢焦点。
    pub popup: bool,
}

impl WindowSpec {
    pub fn new(key: impl Into<String>, title: impl Into<String>, size: (f64, f64)) -> Self {
        let key = key.into();
        Self {
            bounds_key: key.clone(),
            key,
            title: title.into(),
            size,
            min_size: (320.0, 240.0),
            resizable: true,
            hide_on_close: false,
            remember_bounds: true,
            popup: false,
        }
    }

    pub fn min_size(mut self, size: (f64, f64)) -> Self {
        self.min_size = size;
        self
    }

    pub fn bounds_key(mut self, key: impl Into<String>) -> Self {
        self.bounds_key = key.into();
        self
    }

    /// 固定大小的窗口：不能调整，也不记住大小。
    pub fn fixed(mut self) -> Self {
        self.resizable = false;
        self.min_size = self.size;
        self.remember_bounds = false;
        self
    }

    pub fn popup(mut self) -> Self {
        self.popup = true;
        self
    }

    pub fn hide_on_close(mut self) -> Self {
        self.hide_on_close = true;
        self
    }
}

pub fn app_icon() -> Option<Icon> {
    Icon::from_resource(crate::platform::ICON_RESOURCE_ID, None)
        .inspect_err(|e| log::warn!("加载窗口图标失败：{e}"))
        .ok()
}

fn index_html() -> String {
    let pages = crate::pages::PAGES_CSS;
    format!(
        "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>Zulip</title>\
         <style>{THEME_CSS}\n{UI_CSS}\n{pages}</style><script>{UI_JS}</script></head>\
         <body><div id=\"main\"></div></body></html>"
    )
}

/// `%LOCALAPPDATA%`。环境变量优先：测试时用它把数据放到临时目录。
pub fn local_app_data() -> PathBuf {
    std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .or_else(dirs::data_local_dir)
        .unwrap_or_else(std::env::temp_dir)
}

/// 不放在数据目录里：搬迁数据目录时搬迁窗口的 WebView 正占用着这些文件。
fn webview_data_dir() -> PathBuf {
    local_app_data().join("ZulipRust.WebView2")
}

/// 窗口是否至少有一部分落在某块屏幕内。
fn on_screen(window: &Window, x: i32, y: i32) -> bool {
    window.available_monitors().any(|m| {
        let (p, s) = (m.position(), m.size());
        x + 64 > p.x && x < p.x + s.width as i32 && y >= p.y - 8 && y < p.y + s.height as i32
    })
}

/// 无边框窗口的配置。所有窗口都从这里创建，主题样式、图标、数据目录保持一致。
pub fn window_config(spec: &WindowSpec, shared: &AppShared) -> Config {
    let saved = spec
        .remember_bounds
        .then(|| shared.bounds.load(&spec.bounds_key))
        .flatten();
    let size = saved.map_or(spec.size, |b| (b.width, b.height));
    let builder = WindowBuilder::new()
        .with_title(&spec.title)
        .with_decorations(false)
        .with_undecorated_shadow(true)
        .with_resizable(spec.resizable)
        .with_inner_size(LogicalSize::new(size.0, size.1))
        .with_min_inner_size(LogicalSize::new(spec.min_size.0, spec.min_size.1))
        .with_window_icon(app_icon())
        .with_always_on_top(spec.popup)
        .with_skip_taskbar(spec.popup)
        .with_focused(!spec.popup);
    let dark_system = shared.starts_dark();
    let background = if dark_system {
        (0x1F, 0x1F, 0x1F, 255)
    } else {
        (0xEB, 0xEB, 0xEB, 255)
    };
    let close = if spec.hide_on_close {
        WindowCloseBehaviour::WindowHides
    } else {
        WindowCloseBehaviour::WindowCloses
    };
    let mut config = Config::new()
        .with_window(builder)
        .with_custom_index(index_html())
        .with_background_color(background)
        .with_disable_context_menu(false)
        .with_close_behaviour(close)
        .with_tray_icon_show_window_on_click(false)
        .with_exits_when_last_window_closes(true)
        .with_data_directory(webview_data_dir())
        .with_asynchronous_custom_protocol(local_files::SCHEME, |_, request, responder| {
            local_files::handle(request, responder);
        });
    if let Some(bounds) = saved {
        config = config.with_on_window(move |window: Arc<Window>, _| {
            if on_screen(&window, bounds.x, bounds.y) {
                window.set_outer_position(PhysicalPosition::new(bounds.x, bounds.y));
            }
        });
    }
    config
}

/// 已打开（或正在创建）的窗口。
enum WindowSlot {
    Pending,
    Open(WeakDesktopContext),
}

struct OpenWindow {
    slot: WindowSlot,
    /// 窗口参数的 `watch::Sender<T>`。
    args: Option<Box<dyn Any>>,
}

thread_local! {
    // 所有 VirtualDom 都在主线程上
    static WINDOWS: RefCell<HashMap<String, OpenWindow>> = RefCell::default();
}

/// 显示并激活窗口。
pub fn activate(window: &dioxus::desktop::DesktopService) {
    window.set_visible(true);
    window.set_minimized(false);
    window.set_focus();
}

/// 窗口已打开时把新参数交给它并激活。
fn reuse_window<T: 'static>(key: &str, args: &mut Option<T>, activate_window: bool) -> bool {
    WINDOWS.with_borrow_mut(|windows| {
        let Some(entry) = windows.get(key) else {
            return false;
        };
        let live = match &entry.slot {
            WindowSlot::Pending => None,
            WindowSlot::Open(weak) => match weak.upgrade() {
                Some(window) => Some(window),
                None => {
                    windows.remove(key);
                    return false;
                }
            },
        };
        if let (Some(args), Some(tx)) = (
            args.take(),
            entry
                .args
                .as_ref()
                .and_then(|tx| tx.downcast_ref::<watch::Sender<T>>()),
        ) {
            tx.send_replace(args);
        }
        if let Some(window) = live.filter(|_| activate_window) {
            activate(&window);
        }
        true
    })
}

fn create_window(
    spec: WindowSpec,
    root: fn() -> Element,
    args: Option<Box<dyn Any>>,
    setup: impl FnOnce(VirtualDom) -> VirtualDom,
) {
    let shared = consume_context::<AppShared>();
    let dom = setup(
        VirtualDom::new(root)
            .with_root_context(shared.clone())
            .with_root_context(shared.theme.clone())
            .with_root_context(WindowKey {
                bounds_key: spec.bounds_key.clone(),
                remember: spec.remember_bounds,
            }),
    );
    let pending = window().new_window(dom, window_config(&spec, &shared));
    let key = spec.key;
    WINDOWS.with_borrow_mut(|w| {
        w.insert(
            key.clone(),
            OpenWindow {
                slot: WindowSlot::Pending,
                args,
            },
        );
    });
    spawn_forever(async move {
        match pending.try_resolve().await {
            Ok(window) => WINDOWS.with_borrow_mut(|w| {
                if let Some(entry) = w.get_mut(&key) {
                    entry.slot = WindowSlot::Open(Rc::downgrade(&window));
                }
            }),
            Err(_) => WINDOWS.with_borrow_mut(|w| {
                w.remove(&key);
            }),
        }
    });
}

/// 打开单例窗口：已打开时激活并返回 false。
///
/// `root` 是窗口的根组件，内容通常包在 [`WindowShell`](super::WindowShell) 中；
/// `setup` 用 `VirtualDom::with_root_context` 传参数。
pub fn open_singleton(
    spec: WindowSpec,
    root: fn() -> Element,
    setup: impl FnOnce(VirtualDom) -> VirtualDom,
) -> bool {
    if reuse_window::<()>(&spec.key, &mut None, !spec.popup) {
        return false;
    }
    create_window(spec, root, None, setup);
    true
}

/// 窗口参数的上下文，配合 [`use_window_args`]。
struct WindowArgs<T>(watch::Sender<T>);

impl<T> Clone for WindowArgs<T> {
    fn clone(&self) -> Self {
        Self(self.0.clone())
    }
}

/// 带参数打开单例窗口：已打开时更新参数并激活，窗口内用 [`use_window_args`] 读取。
pub fn open_with_args<T: Clone + 'static>(
    spec: WindowSpec,
    root: fn() -> Element,
    args: T,
    setup: impl FnOnce(VirtualDom) -> VirtualDom,
) {
    let mut args = Some(args);
    if reuse_window(&spec.key, &mut args, !spec.popup) {
        return;
    }
    let Some(args) = args else { return };
    let tx = watch::Sender::new(args);
    let context = WindowArgs(tx.clone());
    create_window(spec, root, Some(Box::new(tx)), |dom| {
        setup(dom.with_root_context(context))
    });
}

/// 窗口的参数；重复打开时随之更新。
pub fn use_window_args<T: Clone + 'static>() -> Signal<T> {
    let args = use_context::<WindowArgs<T>>();
    let mut value = use_signal(|| args.0.borrow().clone());
    use_future(move || {
        let mut rx = args.0.subscribe();
        async move {
            while rx.changed().await.is_ok() {
                let next = rx.borrow_and_update().clone();
                value.set(next);
            }
        }
    });
    value
}

/// 关闭所有子窗口。窗口的组件随之销毁，`use_drop` 里的收尾（保存草稿）会执行。
pub fn close_all_windows() {
    let windows = WINDOWS.with_borrow_mut(std::mem::take);
    for entry in windows.into_values() {
        if let WindowSlot::Open(weak) = entry.slot
            && let Some(window) = weak.upgrade()
        {
            window.close();
        }
    }
}

/// 当前窗口的位置保存键。主窗口由 `main` 提供，其它窗口由打开窗口的函数提供。
#[derive(Clone, Debug)]
pub struct WindowKey {
    pub bounds_key: String,
    pub remember: bool,
}

/// 窗口移动、调整大小后保存位置。`paused` 返回 true 时跳过（缩进屏幕边缘时）。
pub fn use_save_window_bounds() {
    let key = use_context::<WindowKey>();
    let shared = use_context::<AppShared>();
    let mut pending = use_signal(|| None::<Task>);
    let this_window = use_hook(window);
    use_wry_event_handler(move |event, _| {
        if !key.remember {
            return;
        }
        let Event::WindowEvent {
            event: WindowEvent::Moved(_) | WindowEvent::Resized(_),
            window_id,
            ..
        } = event
        else {
            return;
        };
        if *window_id != this_window.id() {
            return;
        }
        if let Some(task) = pending.take() {
            task.cancel();
        }
        let shared = shared.clone();
        let window = this_window.clone();
        let bounds_key = key.bounds_key.clone();
        let task = spawn(async move {
            tokio::time::sleep(SAVE_BOUNDS_DELAY).await;
            if window.is_maximized() || window.is_minimized() || super::edge_hidden(&window) {
                return;
            }
            let Ok(position) = window.outer_position() else {
                return;
            };
            let size: LogicalSize<f64> = window.inner_size().to_logical(window.scale_factor());
            shared.bounds.save(
                &bounds_key,
                WindowBounds {
                    x: position.x,
                    y: position.y,
                    width: size.width,
                    height: size.height,
                },
            );
        });
        pending.set(Some(task));
    });
}

/// 把窗口移到逻辑坐标（预览页用来验证）。
pub fn move_window(x: f64, y: f64) {
    window().set_outer_position(LogicalPosition::new(x, y));
}
