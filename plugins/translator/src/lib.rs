//! 翻译：消息右键菜单“翻译”先在消息下方显示“正在翻译…”，在工作线程上通过模型中心翻译，
//! 成功后用 replace 临时替换气泡的显示内容（宿主提供“显示原文”切换，不修改消息原文）。
//! 译文保存在数据目录的 translator.db 中，消息再次显示时重新附加；再次点击“翻译”重新翻译。

use std::path::Path;
use std::sync::OnceLock;

use parking_lot::Mutex;
use rusqlite::{Connection, params, params_from_iter};
use zulip_plugin_sdk::abi::{LOG_ERROR, LOG_INFO, LOG_WARNING};
use zulip_plugin_sdk::serde_json::{Value, json};
use zulip_plugin_sdk::{
    Message, MessagesShown, Plugin, SettingsButton, data_dir, export_plugin, log, model_center,
    post, select_account, set_annotation, settings, settings_text, str_of,
};

const MENU_TRANSLATE: i32 = 1;
const MODEL_FIELD: &str = "model";
const TEST_BUTTON: &str = "test";
const TEST_TEXT: &str = "你好";
const SCHEMA: &str = "CREATE TABLE IF NOT EXISTS translation(
    account INTEGER NOT NULL, message_id INTEGER NOT NULL, content TEXT NOT NULL, updated_at INTEGER NOT NULL,
    PRIMARY KEY(account, message_id));";

fn annotate(message_id: i64, annotation: &Value) {
    if let Err(error) = set_annotation(message_id, annotation) {
        log(LOG_WARNING, &format!("显示内容失败：{error}"));
    }
}

fn show_text(key: &str, text: &str) {
    if let Err(error) = settings_text(key, None, text) {
        log(LOG_WARNING, &format!("回复设置窗口失败：{error}"));
    }
}

// 目标语言与替换后的提示词。
fn prompt_of(settings: &Value) -> (String, String) {
    let language = match str_of(settings, "language").trim() {
        "" => "简体中文",
        language => language,
    }
    .to_owned();
    let prompt = str_of(settings, "prompt").replace("{language}", &language);
    (language, prompt)
}

struct Translator;

impl Plugin for Translator {
    fn on_message_menu(&self, menu_id: i32, message: &Message) {
        if menu_id != MENU_TRANSLATE {
            return;
        }
        if message.text.trim().is_empty() {
            annotate(message.id, &json!({"text": "这条消息没有可翻译的文字"}));
            return;
        }
        let settings = settings();
        let Some(model) = model_center::selection(&settings, MODEL_FIELD) else {
            annotate(
                message.id,
                &json!({"text": "请先在“设置 > 插件 > 翻译 > 设置”中选择模型"}),
            );
            return;
        };
        let (language, prompt) = prompt_of(&settings);
        annotate(
            message.id,
            &json!({"text": format!("正在翻译为{language}…")}),
        );
        let target = message.clone();
        if !post(move || translate(&target, &model, &prompt)) {
            annotate(message.id, &json!({"text": "待翻译的消息过多，请稍后再试"}));
        }
    }

    // 消息显示时重新附加保存的译文（回调所属的账号即消息所属的账号）。
    fn on_messages_shown(&self, shown: &MessagesShown) {
        for (message_id, translated) in saved(shown.account_id, &shown.message_ids) {
            annotate(message_id, &json!({ "replace": translated }));
        }
    }

    // 设置中的“测试”：values 为表单当前的值（尚未保存）。
    fn on_settings_button(&self, button: &SettingsButton) {
        if button.key != TEST_BUTTON {
            return;
        }
        let Some(model) = model_center::selection(&button.values, MODEL_FIELD) else {
            show_text(TEST_BUTTON, "请先选择模型");
            return;
        };
        let (language, prompt) = prompt_of(&button.values);
        show_text(
            TEST_BUTTON,
            &format!("正在把“{TEST_TEXT}”翻译为{language}…"),
        );
        if !post(move || test(&model, &prompt)) {
            show_text(TEST_BUTTON, "待翻译的消息过多，请稍后再试");
        }
    }
}

fn test(model: &Value, prompt: &str) {
    let text = match model_center::ask(model, prompt, TEST_TEXT) {
        Ok(reply) if !reply.trim().is_empty() => format!("成功：{}", reply.trim()),
        Ok(_) => "失败：模型没有返回文字".to_owned(),
        Err(error) => format!("失败：{error}"),
    };
    show_text(TEST_BUTTON, &text);
}

fn translate(message: &Message, model: &Value, prompt: &str) {
    if select_account(message.account_id).is_err() {
        return;
    }
    let result = model_center::ask(model, prompt, &message.text);
    if let Err(error) = &result {
        log(LOG_ERROR, &format!("翻译消息 {} 失败：{error}", message.id));
    }
    let translated = result.as_deref().map(str::trim).unwrap_or_default();
    let annotation = if translated.is_empty() {
        let reason = result
            .as_ref()
            .err()
            .map_or("模型没有返回文字", String::as_str);
        json!({"text": format!("翻译失败：{reason}")})
    } else {
        save(message.account_id, message.id, translated);
        json!({ "replace": translated })
    };
    match set_annotation(message.id, &annotation) {
        Err(error) => log(LOG_WARNING, &format!("显示译文失败：{error}")),
        Ok(()) if result.is_ok() => log(LOG_INFO, &format!("已翻译消息 {}", message.id)),
        Ok(()) => {}
    }
}

fn open(path: &Path) -> rusqlite::Result<Connection> {
    let connection = Connection::open(path)?;
    connection.execute_batch(SCHEMA)?;
    Ok(connection)
}

fn database() -> Option<&'static Mutex<Connection>> {
    static DB: OnceLock<Option<Mutex<Connection>>> = OnceLock::new();
    DB.get_or_init(|| {
        let dir = data_dir().ok()?;
        match open(&Path::new(&dir).join("translator.db")) {
            Ok(connection) => Some(Mutex::new(connection)),
            Err(error) => {
                log(LOG_WARNING, &format!("打开数据库失败：{error}"));
                None
            }
        }
    })
    .as_ref()
}

fn query_saved(
    connection: &Connection,
    account: i64,
    message_ids: &[i64],
) -> rusqlite::Result<Vec<(i64, String)>> {
    let marks = vec!["?"; message_ids.len()].join(",");
    let sql = format!(
        "SELECT message_id, content FROM translation WHERE account = ? AND message_id IN ({marks})"
    );
    let mut statement = connection.prepare(&sql)?;
    let args = std::iter::once(account).chain(message_ids.iter().copied());
    statement
        .query_map(params_from_iter(args), |row| Ok((row.get(0)?, row.get(1)?)))?
        .collect()
}

// 保存的译文：(消息 id, 译文)。
fn saved(account: i64, message_ids: &[i64]) -> Vec<(i64, String)> {
    if message_ids.is_empty() {
        return Vec::new();
    }
    let Some(db) = database() else {
        return Vec::new();
    };
    query_saved(&db.lock(), account, message_ids).unwrap_or_else(|error| {
        log(LOG_WARNING, &format!("读取译文失败：{error}"));
        Vec::new()
    })
}

fn upsert(
    connection: &Connection,
    account: i64,
    message_id: i64,
    translated: &str,
) -> rusqlite::Result<()> {
    connection.execute(
        "INSERT INTO translation(account, message_id, content, updated_at) VALUES(?1, ?2, ?3, ?4)
         ON CONFLICT(account, message_id) DO UPDATE SET content = excluded.content, updated_at = excluded.updated_at",
        params![account, message_id, translated, chrono::Utc::now().timestamp()],
    )?;
    Ok(())
}

fn save(account: i64, message_id: i64, translated: &str) {
    let Some(db) = database() else {
        return;
    };
    if let Err(error) = upsert(&db.lock(), account, message_id, translated) {
        log(LOG_WARNING, &format!("保存译文失败：{error}"));
    }
}

export_plugin!(Translator, "翻译");

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn prompt_replaces_language() {
        let (language, prompt) =
            prompt_of(&json!({"language": " 英语 ", "prompt": "译为{language}：{language}"}));
        assert_eq!(language, "英语");
        assert_eq!(prompt, "译为英语：英语");
        assert_eq!(prompt_of(&json!({})).0, "简体中文");
    }

    #[test]
    fn saves_translations_per_account() {
        let connection = Connection::open_in_memory().unwrap();
        connection.execute_batch(SCHEMA).unwrap();
        upsert(&connection, 1, 10, "旧").unwrap();
        upsert(&connection, 1, 10, "新").unwrap();
        upsert(&connection, 2, 10, "别的账号").unwrap();
        upsert(&connection, 1, 11, "b").unwrap();
        let mut rows = query_saved(&connection, 1, &[10, 11, 12]).unwrap();
        rows.sort();
        assert_eq!(rows, vec![(10, "新".to_owned()), (11, "b".to_owned())]);
    }
}
