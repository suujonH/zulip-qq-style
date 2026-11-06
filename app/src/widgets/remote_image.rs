//! 需要登录才能下载的图片（缩略图）：先显示占位，下载完显示图片。

use super::{downloaded_image, local_image};
use crate::state::use_app_state;
use dioxus::prelude::*;

#[component]
pub fn RemoteImage(
    #[props(into)] url: String,
    #[props(default = 72.0)] size: f64,
    /// 铺满父元素（网格），大小由样式决定。
    #[props(default)]
    fill: bool,
    onclick: Option<EventHandler<MouseEvent>>,
) -> Element {
    let state = use_app_state();
    let cached = state
        .session
        .read()
        .as_ref()
        .and_then(|session| downloaded_image(session, &url));
    let loaded = use_resource(use_reactive!(|url| {
        let session = state.session.peek().clone();
        async move { local_image(session?, url).await }
    }));
    let src = cached.or_else(|| loaded.read().clone().flatten());
    rsx! {
        div {
            class: match (onclick.is_some(), fill) {
                (true, true) => "remote-image fill clickable",
                (true, false) => "remote-image clickable",
                (false, true) => "remote-image fill",
                (false, false) => "remote-image",
            },
            style: if fill { String::new() } else { format!("width:{size}px;height:{size}px") },
            onclick: move |e| {
                if let Some(h) = onclick {
                    h.call(e);
                }
            },
            if let Some(src) = src {
                img { src: "{src}", draggable: "false", alt: "" }
            }
        }
    }
}
