use dioxus::prelude::*;

pub use super::icon_data::IconName;

/// TDesign 线框图标，颜色继承 `color`。
#[component]
pub fn Icon(
    name: IconName,
    #[props(default = 16.0)] size: f64,
    #[props(default, into)] class: String,
) -> Element {
    let (stroke, fill, even_odd) = name.paths();
    rsx! {
        svg {
            class: "icon {class}",
            width: "{size}",
            height: "{size}",
            view_box: "0 0 24 24",
            if !fill.is_empty() {
                path { class: "solid", d: fill, fill_rule: if even_odd { "evenodd" } else { "nonzero" } }
            }
            if !stroke.is_empty() {
                path { class: "stroke", d: stroke }
            }
        }
    }
}
