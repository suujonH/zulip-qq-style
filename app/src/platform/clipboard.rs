use arboard::{Clipboard, ImageData};
use std::borrow::Cow;
use std::io::Cursor;

/// 剪贴板中的图片（RGBA）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ClipboardImage {
    pub width: usize,
    pub height: usize,
    pub rgba: Vec<u8>,
}

impl ClipboardImage {
    /// 编码为 PNG（用于上传）。
    pub fn to_png(&self) -> Option<Vec<u8>> {
        let image =
            image::RgbaImage::from_raw(self.width as u32, self.height as u32, self.rgba.clone())?;
        let mut out = Cursor::new(Vec::new());
        image.write_to(&mut out, image::ImageFormat::Png).ok()?;
        Some(out.into_inner())
    }
}

pub fn copy_text(text: &str) -> Result<(), arboard::Error> {
    Clipboard::new()?.set_text(text)
}

pub fn paste_text() -> Option<String> {
    Clipboard::new().ok()?.get_text().ok()
}

pub fn copy_image(image: &ClipboardImage) -> Result<(), arboard::Error> {
    Clipboard::new()?.set_image(ImageData {
        width: image.width,
        height: image.height,
        bytes: Cow::Borrowed(&image.rgba),
    })
}

pub fn paste_image() -> Option<ClipboardImage> {
    let data = Clipboard::new().ok()?.get_image().ok()?;
    Some(ClipboardImage {
        width: data.width,
        height: data.height,
        rgba: data.bytes.into_owned(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn encodes_png() {
        let image = ClipboardImage {
            width: 1,
            height: 1,
            rgba: vec![1, 2, 3, 255],
        };
        let png = image.to_png().unwrap();
        assert_eq!(&png[1..4], b"PNG");
    }

    #[test]
    fn rejects_wrong_buffer_size() {
        let image = ClipboardImage {
            width: 2,
            height: 2,
            rgba: vec![0; 3],
        };
        assert_eq!(image.to_png(), None);
    }
}
