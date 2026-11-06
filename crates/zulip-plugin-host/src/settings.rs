//! 插件设置的值：保存在程序级设置 `plugin_settings:<id>` 中（JSON 对象），密码类的值用 DPAPI 加密。

use base64::Engine;
use base64::engine::general_purpose::STANDARD;
use serde_json::{Map, Value};
use zulip_plugin_sdk::str_of;
use zulip_store::SettingRepo;
use zulip_store::{protect_key, unprotect_key};

use crate::manifest::PluginManifest;
use crate::setting_field::{PluginSettingField, PluginSettingType, normalize_values};

const ENCRYPTED_PREFIX: &str = "dpapi:";

fn storage_key(plugin_id: &str) -> String {
    format!("plugin_settings:{plugin_id}")
}

/// 读取（解密、补全默认值）。没有保存过时为全部默认值。
pub(crate) fn load(settings: &SettingRepo, manifest: &PluginManifest) -> Map<String, Value> {
    let saved = settings
        .get(&storage_key(&manifest.id))
        .and_then(|json| serde_json::from_str::<Value>(&json).ok());
    let mut result = normalize_values(
        &manifest.settings,
        saved.as_ref().and_then(Value::as_object),
    );
    transform(&manifest.settings, &mut result, &decrypt);
    result
}

/// 保存（按清单整理后加密）。
pub(crate) fn save(settings: &SettingRepo, manifest: &PluginManifest, values: &Map<String, Value>) {
    let mut stored = normalize_values(&manifest.settings, Some(values));
    transform(&manifest.settings, &mut stored, &encrypt);
    settings.set(
        &storage_key(&manifest.id),
        &Value::Object(stored).to_string(),
    );
}

// 对所有密码项（含列表中的）的值做转换。
fn transform(
    fields: &[PluginSettingField],
    values: &mut Map<String, Value>,
    convert: &dyn Fn(&str) -> String,
) {
    for field in fields {
        let Some(value) = values.get_mut(&field.key) else {
            continue;
        };
        match (field.kind, value) {
            (PluginSettingType::Password, Value::String(text)) if !text.is_empty() => {
                *text = convert(text);
            }
            (PluginSettingType::List, Value::Array(rows)) => {
                for row in rows.iter_mut().filter_map(Value::as_object_mut) {
                    transform(&field.fields, row, convert);
                }
            }
            _ => {}
        }
    }
}

// 无法加密时不保存明文。
fn encrypt(plain: &str) -> String {
    match protect_key(plain) {
        Ok(data) => format!("{ENCRYPTED_PREFIX}{}", STANDARD.encode(data)),
        Err(error) => {
            log::warn!("无法加密插件设置：{error}");
            String::new()
        }
    }
}

// 无法解密（换了 Windows 账户）时为空。
fn decrypt(stored: &str) -> String {
    let Some(encoded) = stored.strip_prefix(ENCRYPTED_PREFIX) else {
        return stored.to_owned();
    };
    STANDARD
        .decode(encoded)
        .ok()
        .and_then(|data| unprotect_key(&data).ok())
        .unwrap_or_default()
}

/// 插件通过 set_settings_feedback 回复设置窗口。
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct SettingsFeedback {
    pub key: String,
    pub row: Option<i64>,
    pub text: Option<String>,
    /// tags 设置项的候选。
    pub options: Option<Vec<String>>,
    /// tags 某一项的按钮（item_button）的结果对应的项。
    pub item: Option<String>,
    /// item 按钮的结果；没有 status 时为 `None`。
    pub ok: Option<bool>,
}

impl SettingsFeedback {
    /// 格式错误或没有 key 时为 `None`。
    pub fn parse(json: &str) -> Option<Self> {
        let value: Value = serde_json::from_str(json).ok()?;
        let key = str_of(&value, "key");
        if key.is_empty() {
            return None;
        }
        let options = value["options"].as_array().map(|items| {
            let mut result: Vec<String> = Vec::new();
            for option in items.iter().filter_map(Value::as_str).map(str::trim) {
                if !option.is_empty() && !result.iter().any(|o| o == option) {
                    result.push(option.to_owned());
                }
            }
            result
        });
        Some(Self {
            key: key.to_owned(),
            row: value["row"].as_i64(),
            text: value["text"].as_str().map(str::to_owned),
            options,
            item: value["item"]
                .as_str()
                .filter(|i| !i.is_empty())
                .map(str::to_owned),
            ok: match str_of(&value, "status") {
                "ok" => Some(true),
                "error" => Some(false),
                _ => None,
            },
        })
    }
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;

    #[test]
    fn feedback_parse() {
        let f = SettingsFeedback::parse(
            r#"{"key": "m", "row": 2, "options": [" a ", "a", ""], "item": "x", "status": "error", "text": "坏"}"#,
        )
        .unwrap();
        assert_eq!(f.row, Some(2));
        assert_eq!(f.options, Some(vec!["a".to_owned()]));
        assert_eq!(f.ok, Some(false));
        assert_eq!(SettingsFeedback::parse("{}"), None);
        assert_eq!(SettingsFeedback::parse("坏"), None);
    }

    #[test]
    fn transform_reaches_list_rows() {
        let mut warnings = Vec::new();
        let fields = PluginSettingField::parse_list(
            &json!([
                {"key": "p", "type": "password"},
                {"key": "l", "type": "list", "fields": [{"key": "k", "type": "password"}, {"key": "n"}]},
            ]),
            &mut |w| warnings.push(w),
            true,
        );
        let mut values = json!({"p": "a", "l": [{"k": "b", "n": "c"}]})
            .as_object()
            .cloned()
            .unwrap();
        let encrypted_values = {
            transform(&fields, &mut values, &encrypt);
            values.clone()
        };
        assert!(
            encrypted_values["p"]
                .as_str()
                .unwrap()
                .starts_with(ENCRYPTED_PREFIX)
        );
        assert_eq!(encrypted_values["l"][0]["n"], "c");
        transform(&fields, &mut values, &decrypt);
        assert_eq!(
            Value::Object(values),
            json!({"p": "a", "l": [{"k": "b", "n": "c"}]})
        );
    }
}
