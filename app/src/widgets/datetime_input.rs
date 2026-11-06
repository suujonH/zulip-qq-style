//! 用户输入的日期、时间文字：按日期筛选、修改提醒时间。

use chrono::{Local, NaiveDate, NaiveDateTime, TimeZone};

/// 文字中依次出现的数字，其余字符都当作分隔符：`2026年11月5日 18:30` → `[2026, 11, 5, 18, 30]`。
fn numbers(text: &str) -> Vec<u32> {
    text.split(|c: char| !c.is_ascii_digit())
        .filter(|part| !part.is_empty())
        .filter_map(|part| part.parse().ok())
        .collect()
}

/// 年月日，如 `2026-11-05`、`2026/1/5`、`2026年11月5日`。
pub fn parse_date(text: &str) -> Option<NaiveDate> {
    let n = numbers(text);
    if n.len() != 3 {
        return None;
    }
    NaiveDate::from_ymd_opt(i32::try_from(n[0]).ok()?, n[1], n[2])
}

/// 年月日加时分（可带秒），如 `2026-11-05 18:30`。
pub fn parse_datetime(text: &str) -> Option<NaiveDateTime> {
    let n = numbers(text);
    if !(5..=6).contains(&n.len()) {
        return None;
    }
    NaiveDate::from_ymd_opt(i32::try_from(n[0]).ok()?, n[1], n[2])?.and_hms_opt(
        n[3],
        n[4],
        n.get(5).copied().unwrap_or(0),
    )
}

/// 本地时间对应的 Unix 秒。
pub fn local_timestamp(time: NaiveDateTime) -> Option<i64> {
    Some(Local.from_local_datetime(&time).earliest()?.timestamp())
}

/// 这一天结束（本地 23:59:59）的 ISO 8601 文字，作为聊天记录筛选的截止时间。
pub fn end_of_day(date: NaiveDate) -> Option<String> {
    let time = Local
        .from_local_datetime(&date.and_hms_opt(23, 59, 59)?)
        .earliest()?;
    Some(time.format("%Y-%m-%dT%H:%M:%S%:z").to_string())
}

/// 输入框里的初始文字：`2026-11-05 18:30`。
pub fn input_text(timestamp: i64) -> String {
    chrono::DateTime::from_timestamp(timestamp, 0)
        .map(|t| t.with_timezone(&Local).format("%Y-%m-%d %H:%M").to_string())
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dates_accept_common_separators() {
        let expected = NaiveDate::from_ymd_opt(2026, 11, 5);
        assert_eq!(parse_date("2026-11-05"), expected);
        assert_eq!(parse_date(" 2026/11/5 "), expected);
        assert_eq!(parse_date("2026年11月5日"), expected);
    }

    #[test]
    fn invalid_dates_are_rejected() {
        assert_eq!(parse_date("abc"), None);
        assert_eq!(parse_date("2026-13-01"), None);
        assert_eq!(parse_date("2026-11"), None);
        assert_eq!(parse_date("2026-11-05 18:30"), None);
    }

    #[test]
    fn datetimes_have_optional_seconds() {
        let day = NaiveDate::from_ymd_opt(2026, 11, 5).unwrap();
        assert_eq!(
            parse_datetime("2026-11-05 18:30"),
            day.and_hms_opt(18, 30, 0)
        );
        assert_eq!(
            parse_datetime("2026-11-05T18:30:15"),
            day.and_hms_opt(18, 30, 15)
        );
        assert_eq!(parse_datetime("2026-11-05"), None);
        assert_eq!(parse_datetime("2026-11-05 25:00"), None);
    }

    #[test]
    fn end_of_day_is_the_last_second_with_offset() {
        let text = end_of_day(NaiveDate::from_ymd_opt(2026, 11, 5).unwrap()).unwrap();
        assert!(text.starts_with("2026-11-05T23:59:59"));
        assert!(text.len() > "2026-11-05T23:59:59".len());
    }

    #[test]
    fn input_text_round_trips_to_the_minute() {
        let time = parse_datetime("2026-11-05 18:30").unwrap();
        let timestamp = local_timestamp(time).unwrap();
        assert_eq!(input_text(timestamp), "2026-11-05 18:30");
    }
}
