use std::path::{Path, PathBuf};

/// 数据目录中各项内容的位置。数据目录由 [`DataLocation`](crate::DataLocation) 决定。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AppPaths {
    root: PathBuf,
}

impl AppPaths {
    pub const DATABASE_FILE: &'static str = "zulip.db";

    pub fn new(root: impl Into<PathBuf>) -> Self {
        Self { root: root.into() }
    }

    /// 聊天信息（数据目录）：数据库、日志、缓存、打开过的文件、插件数据等。
    pub fn root(&self) -> &Path {
        &self.root
    }

    pub fn database(&self) -> PathBuf {
        self.root.join(Self::DATABASE_FILE)
    }

    pub fn cache_dir(&self) -> PathBuf {
        self.root.join("cache")
    }

    pub fn image_cache_dir(&self) -> PathBuf {
        self.root.join("cache").join("images")
    }

    pub fn log_dir(&self) -> PathBuf {
        self.root.join("logs")
    }

    /// 直接打开的聊天文件：`files\服务器\path_id 的目录部分\文件名`。
    pub fn files_dir(&self) -> PathBuf {
        self.root.join("files")
    }

    pub fn plugin_data_dir(&self, plugin_id: &str) -> PathBuf {
        self.root.join("plugin-data").join(plugin_id)
    }
}

/// 接收或下载的文件默认保存到的目录：当前用户的“下载”文件夹。
pub fn default_save_root() -> PathBuf {
    dirs::download_dir()
        .or_else(|| dirs::home_dir().map(|home| home.join("Downloads")))
        .unwrap_or_else(|| PathBuf::from("Downloads"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn locations_are_under_root() {
        let paths = AppPaths::new(r"C:\Data\ZulipRust");
        assert_eq!(paths.database(), Path::new(r"C:\Data\ZulipRust\zulip.db"));
        assert_eq!(
            paths.image_cache_dir(),
            Path::new(r"C:\Data\ZulipRust\cache\images")
        );
        assert_eq!(paths.log_dir(), Path::new(r"C:\Data\ZulipRust\logs"));
        assert_eq!(paths.files_dir(), Path::new(r"C:\Data\ZulipRust\files"));
        assert_eq!(
            paths.plugin_data_dir("com.a"),
            Path::new(r"C:\Data\ZulipRust\plugin-data\com.a")
        );
    }
}
