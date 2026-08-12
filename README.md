# LootLens

LootLens is a Windows-only WPF desktop overlay for Escape from Tarkov price lookup.
It uses public market/trader data and is designed to stay anti-cheat-safe.

## OCR mode & safety

OCR mode is an optional local feature that reads text from the screen area around your mouse cursor and matches it against the cached item list.

### APIs used
- `RegisterHotKey` / `UnregisterHotKey` (user32): global hotkeys (`F6` overlay toggle, `F7` OCR toggle).
- `GetCursorPos` (user32): read current cursor position to center OCR capture region.
- `System.Drawing.Graphics.CopyFromScreen`: capture a rectangular screen region (same model as screen capture software).
- `System.Drawing.Bitmap.LockBits` + `Marshal.Copy`: copy BGRA pixel bytes from the captured bitmap.
- `Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer` (`BitmapPixelFormat.Bgra8`, `BitmapAlphaMode.Premultiplied`): convert capture bytes for OCR.
- `Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages` + `RecognizeAsync`: run Windows OCR on captured image content.

### Why this is not game interaction
- LootLens does **not** open the game process.
- LootLens does **not** read or write game memory.
- LootLens does **not** inject DLLs or hook game APIs.
- LootLens does **not** automate input or use low-level keyboard/mouse hooks.
- LootLens only reads on-screen pixels through public Windows capture APIs and compares recognized text to public item data.

This behavior is equivalent to normal screen capture/companion utilities (for example OBS-style capture workflows), not cheat like process interaction.
