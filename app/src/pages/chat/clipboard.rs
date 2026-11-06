//! 复制时文字对应的 Markdown。系统剪贴板里只放纯文字（arboard 不支持自定义格式），
//! 同一进程内粘贴时用 [`markdown_for_clipboard`] 取回 Markdown。

use std::sync::Mutex;

static LAST_COPY: Mutex<Option<(String, String)>> = Mutex::new(None);

fn normalize(text: &str) -> String {
    text.replace("\r\n", "\n")
}

pub fn remember(text: &str, markdown: &str) {
    if let Ok(mut last) = LAST_COPY.lock() {
        *last = Some((normalize(text), markdown.to_owned()));
    }
}

/// 剪贴板文字 `text` 来自聊天内容的复制时，它的 Markdown。
pub fn markdown_for_clipboard(text: &str) -> Option<String> {
    let last = LAST_COPY.lock().ok()?;
    let (copied, markdown) = last.as_ref()?;
    (*copied == normalize(text)).then(|| markdown.clone())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn markdown_matches_only_same_text() {
        remember("a\nb", "**a**\nb");
        assert_eq!(markdown_for_clipboard("a\r\nb").as_deref(), Some("**a**\nb"));
        assert_eq!(markdown_for_clipboard("c"), None);
    }
}
