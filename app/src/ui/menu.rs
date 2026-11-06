//! 右键菜单与子菜单。

use super::geometry::{Placement, Rect, client_rect, place, viewport};
use super::icon::{Icon, IconName};
use super::overlay::{Overlays, use_overlays};
use dioxus::dioxus_core::Task;
use dioxus::prelude::*;
use std::rc::Rc;
use std::time::Duration;

const SUBMENU_GAP: f64 = 5.0;
/// 子菜单上移外框到第一项的距离，第一项与父菜单项在同一行。
const SUBMENU_LIFT: f64 = 5.0;
const SUBMENU_DELAY_MS: u64 = 120;

/// 菜单项。链式设置图标、禁用、勾选。
#[derive(Clone)]
pub struct MenuAction {
    label: String,
    icon: Option<IconName>,
    hint: Option<String>,
    checked: Option<bool>,
    disabled: bool,
    danger: bool,
    on_select: Rc<dyn Fn()>,
}

#[derive(Clone)]
pub struct MenuSubmenu {
    label: String,
    icon: Option<IconName>,
    disabled: bool,
    items: Rc<Vec<MenuEntry>>,
}

#[derive(Clone)]
pub enum MenuEntry {
    Action(MenuAction),
    Submenu(MenuSubmenu),
    /// 自定义内容（表情行等）。回调的参数用来关闭菜单。
    Custom(Rc<dyn Fn(Callback<()>) -> Element>),
    Separator,
}

impl MenuEntry {
    pub fn item(label: impl Into<String>, on_select: impl Fn() + 'static) -> Self {
        Self::Action(MenuAction {
            label: label.into(),
            icon: None,
            hint: None,
            checked: None,
            disabled: false,
            danger: false,
            on_select: Rc::new(on_select),
        })
    }

    pub fn submenu(label: impl Into<String>, items: Vec<MenuEntry>) -> Self {
        Self::Submenu(MenuSubmenu {
            label: label.into(),
            icon: None,
            disabled: false,
            items: Rc::new(items),
        })
    }

    pub fn custom(content: impl Fn(Callback<()>) -> Element + 'static) -> Self {
        Self::Custom(Rc::new(content))
    }

    pub fn icon(mut self, name: IconName) -> Self {
        match &mut self {
            Self::Action(a) => a.icon = Some(name),
            Self::Submenu(s) => s.icon = Some(name),
            Self::Custom(_) | Self::Separator => {}
        }
        self
    }

    pub fn disabled(mut self, disabled: bool) -> Self {
        match &mut self {
            Self::Action(a) => a.disabled = disabled,
            Self::Submenu(s) => s.disabled = disabled,
            Self::Custom(_) | Self::Separator => {}
        }
        self
    }

    /// 勾选项：对勾显示在图标列，未勾选时也占位。
    pub fn checked(mut self, checked: bool) -> Self {
        if let Self::Action(a) = &mut self {
            a.checked = Some(checked);
        }
        self
    }

    pub fn danger(mut self) -> Self {
        if let Self::Action(a) = &mut self {
            a.danger = true;
        }
        self
    }

    /// 右侧的说明文字（快捷键等）。
    pub fn hint(mut self, text: impl Into<String>) -> Self {
        if let Self::Action(a) = &mut self {
            a.hint = Some(text.into());
        }
        self
    }
}

/// 只要有一项带图标或勾选位，所有项都留出图标列。
fn needs_icon_column(items: &[MenuEntry]) -> bool {
    items.iter().any(|entry| match entry {
        MenuEntry::Action(a) => a.icon.is_some() || a.checked.is_some(),
        MenuEntry::Submenu(s) => s.icon.is_some(),
        MenuEntry::Custom(_) | MenuEntry::Separator => false,
    })
}

#[derive(Clone)]
pub(super) struct MenuState {
    pub anchor: Rect,
    pub items: Rc<Vec<MenuEntry>>,
}

#[derive(Clone)]
struct Items(Rc<Vec<MenuEntry>>);

impl PartialEq for Items {
    fn eq(&self, other: &Self) -> bool {
        Rc::ptr_eq(&self.0, &other.0)
    }
}

#[component]
pub(super) fn MenuHost(
    state: Signal<Option<MenuState>, UnsyncStorage>,
    on_close: Callback<()>,
) -> Element {
    let current = state.read().clone();
    rsx! {
        if let Some(menu) = current {
            div {
                key: "{menu.anchor.x},{menu.anchor.y}",
                class: "overlay-backdrop",
                style: "z-index:300",
                onmousedown: move |e| {
                    e.stop_propagation();
                    on_close.call(());
                },
                oncontextmenu: move |e| {
                    e.prevent_default();
                    e.stop_propagation();
                    on_close.call(());
                },
            }
            MenuPanel {
                items: Items(menu.items),
                anchor: menu.anchor,
                submenu: false,
                on_close,
            }
        }
    }
}

#[component]
fn MenuPanel(items: Items, anchor: Rect, submenu: bool, on_close: Callback<()>) -> Element {
    let mut pos = use_signal(|| None::<(f64, f64)>);
    let mut open_sub = use_signal(|| None::<(usize, Rect, Rc<Vec<MenuEntry>>)>);
    let mut hover_task = use_signal(|| None::<Task>);
    let icon_column = needs_icon_column(&items.0);
    let style = pos().map_or_else(
        || "left:0;top:0".to_string(),
        |(x, y)| format!("left:{x}px;top:{y}px"),
    );
    let open_index = open_sub.read().as_ref().map(|(i, _, _)| *i);
    let mut on_hover = move |target: Option<(usize, Rc<Vec<MenuEntry>>, Rc<MountedData>)>| {
        if let Some(task) = hover_task.take() {
            task.cancel();
        }
        let task = spawn(async move {
            tokio::time::sleep(Duration::from_millis(SUBMENU_DELAY_MS)).await;
            match target {
                Some((index, items, el)) => {
                    if let Some(rect) = client_rect(&el).await {
                        open_sub.set(Some((index, rect, items)));
                    }
                }
                None => open_sub.set(None),
            }
        });
        hover_task.set(Some(task));
    };
    rsx! {
        div {
            class: if pos().is_some() { "menu scroll" } else { "menu scroll unplaced" },
            style,
            role: "menu",
            oncontextmenu: move |e| {
                e.prevent_default();
                e.stop_propagation();
            },
            onmousedown: move |e| e.stop_propagation(),
            onmounted: move |e: MountedEvent| async move {
                if let Some(rect) = client_rect(&e.data()).await {
                    let (anchor, placement, gap) = if submenu {
                        (Rect { y: anchor.y - SUBMENU_LIFT, ..anchor }, Placement::RightStart, SUBMENU_GAP)
                    } else {
                        (anchor, Placement::BottomStart, 0.0)
                    };
                    pos.set(Some(place(anchor, (rect.w, rect.h), viewport(), placement, gap)));
                }
            },
            for (index, entry) in items.0.iter().cloned().enumerate() {
                match entry {
                    MenuEntry::Separator => rsx! { div { key: "{index}", class: "menu-separator" } },
                    MenuEntry::Custom(content) => rsx! { div { key: "{index}", onmouseenter: move |_| on_hover(None), {content(on_close)} } },
                    MenuEntry::Action(action) => rsx! {
                        ActionRow { key: "{index}", action: ActionProp(action), icon_column, on_close, on_hover: move |_| on_hover(None) }
                    },
                    MenuEntry::Submenu(sub) => rsx! {
                        SubmenuRow {
                            key: "{index}",
                            sub: SubmenuProp(sub),
                            icon_column,
                            open: open_index == Some(index),
                            on_hover: move |(items, el)| on_hover(Some((index, items, el))),
                        }
                    },
                }
            }
        }
        if let Some((index, rect, sub_items)) = open_sub.read().clone() {
            MenuPanel { key: "{index}", items: Items(sub_items), anchor: rect, submenu: true, on_close }
        }
    }
}

#[derive(Clone)]
struct ActionProp(MenuAction);

impl PartialEq for ActionProp {
    fn eq(&self, other: &Self) -> bool {
        Rc::ptr_eq(&self.0.on_select, &other.0.on_select)
    }
}

#[derive(Clone)]
struct SubmenuProp(MenuSubmenu);

impl PartialEq for SubmenuProp {
    fn eq(&self, other: &Self) -> bool {
        Rc::ptr_eq(&self.0.items, &other.0.items)
    }
}

#[component]
fn ActionRow(
    action: ActionProp,
    icon_column: bool,
    on_close: Callback<()>,
    on_hover: Callback<()>,
) -> Element {
    let action = action.0;
    let mut class = String::from("menu-row");
    if action.disabled {
        class.push_str(" disabled");
    }
    if action.danger {
        class.push_str(" danger");
    }
    let disabled = action.disabled;
    let on_select = action.on_select.clone();
    rsx! {
        div {
            class,
            role: "menuitem",
            onmouseenter: move |_| on_hover.call(()),
            onclick: move |_| {
                if !disabled {
                    on_close.call(());
                    on_select();
                }
            },
            if icon_column {
                span { class: "menu-icon-slot",
                    if action.checked == Some(true) {
                        svg { class: "menu-check", view_box: "0 0 12 8", path { d: "M0.5,4 L4,7.5 L11.5,0.5" } }
                    } else if let Some(name) = action.icon {
                        Icon { name, size: 16.0, class: "menu-icon" }
                    }
                }
            }
            span { class: "menu-label", "{action.label}" }
            if let Some(hint) = action.hint {
                span { class: "menu-hint", "{hint}" }
            }
        }
    }
}

#[component]
fn SubmenuRow(
    sub: SubmenuProp,
    icon_column: bool,
    open: bool,
    on_hover: Callback<(Rc<Vec<MenuEntry>>, Rc<MountedData>)>,
) -> Element {
    let sub = sub.0;
    let mut mounted = use_signal(|| None::<Rc<MountedData>>);
    let mut class = String::from("menu-row");
    if sub.disabled {
        class.push_str(" disabled");
    }
    if open {
        class.push_str(" sub-open");
    }
    let disabled = sub.disabled;
    let items = sub.items.clone();
    rsx! {
        div {
            class,
            role: "menuitem",
            aria_haspopup: "menu",
            onmounted: move |e| mounted.set(Some(e.data())),
            onmouseenter: move |_| {
                let el = mounted.peek().clone();
                if let (false, Some(el)) = (disabled, el) {
                    on_hover.call((items.clone(), el));
                }
            },
            if icon_column {
                span { class: "menu-icon-slot",
                    if let Some(name) = sub.icon {
                        Icon { name, size: 16.0, class: "menu-icon" }
                    }
                }
            }
            span { class: "menu-label", "{sub.label}" }
            svg { class: "menu-arrow", view_box: "0 0 6 10", path { d: "M0.5,0.5 L5,5 L0.5,9.5" } }
        }
    }
}

/// 右键点击区域内时打开菜单。`items` 在打开时才调用。
#[component]
pub fn ContextMenuArea(
    items: Callback<(), Vec<MenuEntry>>,
    #[props(default, into)] class: String,
    children: Element,
) -> Element {
    let overlays = use_overlays();
    rsx! {
        div {
            class,
            oncontextmenu: move |e| open_row_menu(overlays, &e, Some(items)),
            {children}
        }
    }
}

/// 列表行、文件卡片的右键菜单：没有菜单时交给外层处理。
pub(super) fn open_row_menu(
    overlays: Overlays,
    event: &MouseEvent,
    menu: Option<Callback<(), Vec<MenuEntry>>>,
) {
    if let Some(items) = menu {
        event.prevent_default();
        event.stop_propagation();
        let at = event.client_coordinates();
        overlays.show_menu(at.x, at.y, items.call(()));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn icon_column_is_shared() {
        assert!(!needs_icon_column(&[
            MenuEntry::item("a", || {}),
            MenuEntry::Separator
        ]));
        assert!(needs_icon_column(&[
            MenuEntry::item("a", || {}),
            MenuEntry::item("b", || {}).icon(IconName::Copy)
        ]));
        assert!(needs_icon_column(&[
            MenuEntry::item("a", || {}).checked(false)
        ]));
    }
}
