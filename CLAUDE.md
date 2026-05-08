# StickerApp — Architecture Reference

## AGENT ENTRY POINT — READ THIS FIRST, DON'T EXPLORE

All source files are listed in the table below. **Do NOT glob, grep, or explore the project** to discover structure — it's fully described here. Jump directly to the relevant file(s) for the task and start working.

### Task → File routing
| Task | Go to |
|------|-------|
| Window appearance / layout / XAML | `StickerApp/StickerWindow.xaml` |
| Drag, resize, physics, animations | `StickerApp/StickerWindow.xaml.cs` |
| Hotkeys, app lifecycle, tray icon | `StickerApp/App.xaml.cs` |
| Saving / loading stickers | `StickerApp/StickerStore.cs` |
| Sticker data shape (fields) | `StickerApp/StickerData.cs` |
| Win32 / P/Invoke / WndProc constants | `StickerApp/Win32.cs` |
| Global settings / color picker dialog | `StickerApp/AppSettings.cs`, `StickerApp/SettingsWindow.xaml.cs` |
| Ball overlay feature | `StickerApp/BallOverlayWindow.xaml.cs` |
| Done button overlay | `StickerApp/DoneButtonWindow.xaml.cs` |
| Rubber ball (throwable, bounces, sound) | `StickerApp/RubberBallWindow.xaml.cs` |

## Stack
- .NET 9.0 WPF + WinForms (tray icon), C# 12, no external deps except System.Text.Json

## Files
| File | Purpose |
|------|---------|
| `App.xaml.cs` | Entry point, tray icon, global low-level hooks (mouse/keyboard), sticker lifecycle |
| `StickerWindow.xaml/.cs` | Individual sticker window — WndProc, animations, physics drag, input handling, destroy |
| `Win32.cs` | All P/Invoke: hooks, messages, hit-test constants, structs |
| `StickerData.cs` | POCO: Id(Guid), Title, AccentColor, X, Y, Width, Height, Tasks(List<TaskItem>) |
| `StickerStore.cs` | JSON persistence in `%APPDATA%\StickerApp\stickers\{guid}.json` |
| `AppSettings.cs` | Global settings: DefaultAccentColor → `%APPDATA%\StickerApp\settings.json` |
| `SettingsWindow.xaml/.cs` | Color picker dialog |
| `BallOverlayWindow.xaml/.cs` | Ball overlay feature window |
| `DoneButtonWindow.xaml/.cs` | Done button overlay window |
| `RubberBallWindow.xaml/.cs` | Throwable rubber ball: walls = VirtualScreen + primary WorkArea.Bottom floor; sleeps when idle (no render loop); SoundPlayer pool plays generated boing WAV |

## Key flows

### Hotkeys (App.xaml.cs — MouseHookCallback)
- Create sticker: `WM_XBUTTONDOWN` + XBUTTON2 + Ctrl + Alt → `CreateSticker()`
- Keyboard hook routes to `GetStickerAtCursor()?.HandleKeyDown(kb)`

### Sticker window
- `WS_EX_NOACTIVATE` — never steals focus
- `WM_MOUSEACTIVATE` → `MA_NOACTIVATE`
- `WM_NCHITTEST` → `CalcHitTest()` — resize zones (BorderSize=6, CornerSize=14) + drag (HeaderH=38)
- `WM_ENTERSIZEMOVE/EXITSIZEMOVE` → drag physics (scale 1.06, rotation ±16°, elastic snap-back)
- Outer `<Grid Background="#01000000">` — invisible alpha=1 layer needed for resize hit-test
- `StickerBorder` (Margin=6) has `DropShadowEffect` BlurRadius=32, ShadowDepth=10

### Destroy flow
- `OnDestroyClick` → `StickerStore.Delete` → `Destroyed?.Invoke` → `Close()`
- `App.OnStickerDestroyed` removes from `_stickers` list

### Animations
- Spawn: ScaleTransform 0.3→1.0, ElasticEase, 480ms
- Idle sway: RotateTransform ±1.2°, SineEase, 4.2s loop, random phase offset
- Drag lift: scale 1.0→1.06, QuadraticEase, 130ms
- Drag snap-back: ElasticEase 2 oscillations, 750ms rotation + 550ms scale

### Input (hover keyboard, StickerWindow.cs — HandleKeyDown)
- Enter → CommitInput (add task)
- Escape → ClearInput
- Backspace → BackspaceInput
- Other → ToUnicodeEx → append to _inputBuffer

## Persistence
- `StickerStore.LoadAll()` on startup → `RestoreStickers()`
- `PersistBounds()` on every LocationChanged/SizeChanged
