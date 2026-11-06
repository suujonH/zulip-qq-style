use std::collections::HashMap;

use rusqlite::params;

use crate::db::Db;

/// 键值设置：程序级或某个账号的设置。读取只访问内存（打开数据库时全部读入），写入排队保存，都不阻塞。
#[derive(Clone)]
pub struct SettingRepo {
    db: Db,
    account_id: Option<i64>,
}

impl SettingRepo {
    pub fn app(db: &Db) -> Self {
        Self {
            db: db.clone(),
            account_id: None,
        }
    }

    pub fn account(db: &Db, account_id: i64) -> Self {
        Self {
            db: db.clone(),
            account_id: Some(account_id),
        }
    }

    pub fn get(&self, key: &str) -> Option<String> {
        self.db
            .settings()
            .read()
            .get(&self.account_id)?
            .get(key)
            .cloned()
    }

    pub fn set(&self, key: &str, value: &str) {
        self.db
            .settings()
            .write()
            .entry(self.account_id)
            .or_default()
            .insert(key.to_owned(), value.to_owned());
        self.db.write(
            "INSERT INTO setting(account_id, key, value) VALUES(?1, ?2, ?3)
             ON CONFLICT(COALESCE(account_id, 0), key) DO UPDATE SET value = excluded.value",
            params![self.account_id, key, value],
        );
    }

    pub fn remove(&self, key: &str) {
        if let Some(values) = self.db.settings().write().get_mut(&self.account_id) {
            values.remove(key);
        }
        self.db.write(
            "DELETE FROM setting WHERE account_id IS ?1 AND key = ?2",
            params![self.account_id, key],
        );
    }

    pub fn get_all(&self) -> HashMap<String, String> {
        self.db
            .settings()
            .read()
            .get(&self.account_id)
            .cloned()
            .unwrap_or_default()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::account::tests::add_account;

    #[test]
    fn app_and_account_settings_are_separate_and_persist() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("t.db");
        let db = Db::open(&path).unwrap();
        let account = add_account(&db);
        let other = crate::account::tests::add_named_account(&db, "other@x");
        let app = SettingRepo::app(&db);
        let a = SettingRepo::account(&db, account);
        let b = SettingRepo::account(&db, other);

        assert_eq!(app.get("k"), None);
        app.set("k", "app");
        a.set("k", "a");
        a.set("k", "a2");
        a.set("gone", "x");
        a.remove("gone");
        assert_eq!(app.get("k").as_deref(), Some("app"));
        assert_eq!(a.get("k").as_deref(), Some("a2"));
        assert_eq!(b.get("k"), None);
        assert_eq!(a.get_all().len(), 1);

        drop((db, app, a, b));
        let db = Db::open(&path).unwrap();
        assert_eq!(SettingRepo::app(&db).get("k").as_deref(), Some("app"));
        assert_eq!(
            SettingRepo::account(&db, account).get("k").as_deref(),
            Some("a2")
        );
        assert_eq!(SettingRepo::account(&db, account).get("gone"), None);
    }
}
