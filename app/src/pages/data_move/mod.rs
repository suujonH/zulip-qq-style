//! 数据目录搬迁：打开数据库之前进行，完成后继续正常启动。

use crate::root::RootStage;
use crate::services::{boot, init_or_report, load_theme};
use crate::shell::{ThemeHub, WindowShell};
use crate::ui::*;
use dioxus::prelude::*;
use std::path::PathBuf;
use tokio::sync::{oneshot, watch};
use zulip_store::MoveError;

/// 字节数的显示文字。
fn size_text(bytes: u64) -> String {
    const MB: f64 = 1024.0 * 1024.0;
    format!("{:.1} MB", bytes as f64 / MB)
}

#[derive(Clone, Copy)]
enum Phase {
    Moving,
    Failed,
}

#[component]
pub fn DataMovePage(target: PathBuf) -> Element {
    let stage = use_context::<RootStage>();
    let theme = use_context::<ThemeHub>();
    let retry_theme = theme.clone();
    let mut phase = use_signal(|| Phase::Moving);
    let mut progress = use_signal(|| (0u64, 0u64));
    let mut failure = use_signal(String::new);
    let source = use_hook(|| boot().location.resolve_root());

    use_future({
        let target = target.clone();
        let source = source.clone();
        move || {
            let target = target.clone();
            let source = source.clone();
            let theme = theme.clone();
            async move {
                let (tx, mut rx) = watch::channel((0u64, 0u64));
                let (done_tx, mut done) = oneshot::channel::<Result<(), MoveError>>();
                tokio::task::spawn_blocking(move || {
                    let result = boot()
                        .location
                        .perform_move(&source, &target, |copied, total| {
                            let _ = tx.send((copied, total));
                        });
                    let _ = done_tx.send(result);
                });
                let result = loop {
                    tokio::select! {
                        changed = rx.changed() => {
                            if changed.is_ok() {
                                progress.set(*rx.borrow_and_update());
                            }
                        }
                        result = &mut done => break result,
                    }
                };
                match result {
                    Ok(Ok(())) => {}
                    Ok(Err(error)) => {
                        log::error!("移动数据目录失败：{error}");
                        failure.set(error.to_string());
                        phase.set(Phase::Failed);
                        return;
                    }
                    Err(_) => {
                        failure.set("移动已中断".to_owned());
                        phase.set(Phase::Failed);
                        return;
                    }
                }
                if init_or_report() {
                    load_theme(&theme);
                    stage.finish_moving();
                } else {
                    std::process::exit(1);
                }
            }
        }
    });

    let (copied, total) = progress();
    let percent = if total > 0 {
        copied as f64 * 100.0 / total as f64
    } else {
        0.0
    };
    rsx! {
        WindowShell { resizable: false, on_close: move |_| {},
            div { class: "data-move",
                match phase() {
                    Phase::Moving => rsx! {
                        div { class: "text-title", "正在移动到“{target.display()}”，完成后 Zulip 将自动启动。" }
                        div { class: "data-move-bar",
                            div { class: "data-move-fill", style: "width:{percent}%" }
                        }
                        div { class: "text-sub", "{size_text(copied)} / {size_text(total)}" }
                    },
                    Phase::Failed => rsx! {
                        div { class: "text-title", "移动失败" }
                        div { class: "text-sub", "{failure}" }
                        div { class: "text-sub", "将继续使用原来的位置：{source.display()}" }
                        div { class: "data-move-actions",
                            Button {
                                kind: ButtonKind::Primary,
                                onclick: move |_| {
                                    if init_or_report() {
                                        load_theme(&retry_theme);
                                        stage.finish_moving();
                                    } else {
                                        std::process::exit(1);
                                    }
                                },
                                "继续"
                            }
                        }
                    },
                }
            }
        }
    }
}
