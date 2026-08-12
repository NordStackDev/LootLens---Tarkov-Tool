# LootLens

A lightweight, transparent overlay for *Escape from Tarkov* that shows live item prices – flea market and trader prices – powered by the public [tarkov.dev](https://tarkov.dev) API.

Press **F6** in-game, type an item name, see the best sell options. That's it.

## Features

- 🔎 Instant item search (`Enter`)
- 💰 Flea 24h avg/low/high + top-3 best sell offers
- 🪟 Transparent, topmost overlay window (works with borderless fullscreen)
- ⌨️ Global hotkey: `F6` show/hide, `Esc` hide

## ⚠️ Safety & Transparency – please read

LootLens is built as a **pure external companion tool**. It does **not** interact with *Escape from Tarkov* in any way.

### What LootLens DOES

- Runs as a normal, separate Windows application (a WPF overlay window).
- Fetches price data **only** from the public tarkov.dev API over HTTPS.
- Displays that data in its own transparent, always-on-top window.
- Uses the standard Windows `RegisterHotKey` API for its global hotkey.
- *(Optional OCR mode)* Captures the screen using the same public Windows APIs as OBS Studio and Snipping Tool (`Windows.Graphics.Capture` / DXGI Desktop Duplication). It only *observes pixels* – exactly like a recording tool.

### What LootLens does NOT do

- ❌ No reading/writing of game memory (`OpenProcess`, `ReadProcessMemory`, …)
- ❌ No DLL injection, no API hooking, no DirectX/Vulkan hooks
- ❌ No kernel drivers
- ❌ No keyboard/mouse automation
- ❌ No reading, modifying or intercepting game files or network traffic
- ❌ No gameplay advantage beyond displaying publicly available price data

The **full source code is public** in this repository, so anyone – including Battlestate Games and BattlEye – can verify every line of the above. If you are unsure, build it from source yourself.

## Disclaimer

LootLens is an unofficial, fan-made tool. It is **not affiliated with, endorsed by, or connected to Battlestate Games**. *Escape from Tarkov* and all related assets are trademarks of Battlestate Games.

Use of any third-party software is at your own risk. This project is designed to stay strictly within the category of external companion apps that never touch the game client, and it complies with the spirit of the game's rules – but the final interpretation always rests with Battlestate Games.

## Requirements

- Windows 10/11
- .NET 10 (or use the self-contained release build)
- Game running in **Borderless/Windowed** mode for the overlay to appear on top

## Build from source

```bash
git clone https://github.com/DIT-USERNAME/lootlens
cd lootlens
dotnet build -c Release
# or publish a single-file exe:
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

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

## Credits

- Price data: [tarkov.dev](https://tarkov.dev) (community API)

LootLens is a Windows-only WPF desktop overlay for Escape from Tarkov price lookup.
It uses public market/trader data and is designed to stay anti-cheat-safe.
