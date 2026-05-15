# C64 Non-Standard Key Mapping (UK 101 Keyboard)

This table lists C64 keys that do not have a direct modern-PC equivalent and their emulator mapping on a standard UK 101 keyboard.

| C64 key | UK 101 key mapping | Notes |
|---|---|---|
| RESTORE | `PageUp` or `Pause` | Triggers NMI (RESTORE behavior), not a matrix key |
| COMMODORE (`C=`) | `Right Alt` (AltGr) | Also used as Commodore modifier for color combos |
| RUN/STOP | `Esc` | Matrix key mapping for software that polls keyboard matrix |
| SHIFT LOCK | `Caps Lock` (toggle) | Latches Shift on/off in matrix |
| CLR/HOME | `Home` | Matrix key + PETSCII home behavior |
| INST/DEL | `Insert` or `Backspace/Delete` | Same C64 key function, mapped to matrix key |
| Cursor Left/Right | `Left` / `Right` arrows | Direct PETSCII + matrix mapping |
| Cursor Up/Down | `Up` / `Down` arrows | PETSCII mapping; arrows are also used by joystick mapping in many games |
| F1/F3/F5/F7 | `F1/F3/F5/F7` | C64-style function key pairs are handled in PETSCII layer |
| C64 color shortcuts (`Ctrl+1..8`, `C=+1..8`) | `Left Ctrl + 1..8`, `Right Alt + 1..8` | PETSCII control/Commodore color codes |

## Practical Notes

- `Right Ctrl` and `Left Alt` remain mapped for joystick fire to preserve game ergonomics.
- `Right Alt` is reserved for COMMODORE key semantics.
- If your host keyboard layout lacks a usable AltGr key, remap COMMODORE to another spare modifier in `Keyboard.cs`.
