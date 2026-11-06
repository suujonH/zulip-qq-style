//! 窗口级按键：没有菜单、卡片、对话框打开时，Esc 关闭窗口，左右方向键交给页面。

use dioxus::desktop::use_window;
use dioxus::prelude::*;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ArrowKey {
    Left,
    Right,
}

// 捕获阶段先于弹层自己的 Esc 处理，所以这里同步检查弹层是否存在
const KEYS_JS: &str = r#"
window.zulipWindowKeys?.abort();
const control = new AbortController();
window.zulipWindowKeys = control;
document.addEventListener("keydown", (e) => {
  if (e.isComposing || e.ctrlKey || e.altKey || e.metaKey || e.shiftKey) return;
  if (document.querySelector(".dialog-layer, .overlay-card, .menu")) return;
  const typing = e.target instanceof Element && e.target.closest("input, textarea, [contenteditable=true]");
  if (e.key === "Escape" || (!typing && (e.key === "ArrowLeft" || e.key === "ArrowRight"))) dioxus.send(e.key);
}, { capture: true, signal: control.signal });
await new Promise(() => {});
"#;

/// Esc 关闭窗口；`on_arrow` 收左右方向键（焦点在输入框时不转发）。
pub fn use_window_keys(on_arrow: impl FnMut(ArrowKey) + 'static) {
    let window = use_window();
    let on_arrow = use_callback(on_arrow);
    use_future(move || {
        let window = window.clone();
        async move {
            let mut eval = document::eval(KEYS_JS);
            while let Ok(key) = eval.recv::<String>().await {
                match key.as_str() {
                    "Escape" => window.close(),
                    "ArrowLeft" => on_arrow.call(ArrowKey::Left),
                    "ArrowRight" => on_arrow.call(ArrowKey::Right),
                    _ => {}
                }
            }
        }
    });
}
