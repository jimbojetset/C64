# C64 Emulator

A cycle-accurate MOS 6510 CPU emulator written in C# (.NET 8.0) with comprehensive instruction set support including all documented and undocumented opcodes.

This repository now contains a full C64 emulator stack built on top of the CPU core, including VIC-II video, SID audio, CIA timers/IO, keyboard/joystick input, IEC/disk support, and datasette support.

## Projects

### C64
Commodore 64 emulator application using SDL2 for display/input/audio. Key features include:
- Cycle-driven CPU/VIC/CIA integration
- VIC raster stepping with bus-steal/stall accounting
- SID synthesis with MODE/VOL semantics
- CIA timer/ICR/TOD handling, serial shift behavior, and NMI/IRQ paths
- Keyboard and SDL-compatible game controller joystick input
- IEC + virtual 1541 D64 file loading support, including selected command/status and direct block-access operations
- TAP datasette pulse playback with motor/sense/read behavior
- Host PRG/SID/CRT/T64/TAP/D64 loading and PRG saving, including ImGui picker windows for bundled software

## Quick Keymap

Most-used non-standard key mappings:

| C64 key / function | UK 101 |
|---|---|
| RESTORE (NMI) | `PageUp` or `Pause` |
| COMMODORE (`C=`) | `Right Alt` (AltGr) |
| RUN/STOP | `Esc` |
| SHIFT LOCK | `Caps Lock` (toggle) |
| CLR/HOME | `Home` |
| INST/DEL | `Insert` or `Backspace/Delete` |
| Load software / ROM | `Ctrl+L` |
| Save BASIC program | `Ctrl+S` |
| Pause/unpause emulator | `Ctrl+P` |
| Select audio device | `Ctrl+A` |
| Mute/unmute audio | `Ctrl+Q` |

For the complete mapping and notes, see the full table in the keyboard section below.

## Opcode Coverage

### Documented Opcodes (151)
All official 6502 opcodes are fully implemented:
- **Load/Store**: LDA, LDX, LDY, STA, STX, STY
- **Transfer**: TAX, TAY, TXA, TYA, TSX, TXS
- **Arithmetic**: ADC, SBC, INC, INX, INY, DEC, DEX, DEY
- **Logic**: AND, EOR, ORA, BIT
- **Shift/Rotate**: ASL, LSR, ROL, ROR
- **Compare**: CMP, CPX, CPY
- **Branch**: BCC, BCS, BEQ, BMI, BNE, BPL, BVC, BVS
- **Jump/Call**: JMP, JSR, RTS, RTI
- **Stack**: PHA, PHP, PLA, PLP
- **Flags**: CLC, CLD, CLI, CLV, SEC, SED, SEI
- **Control**: BRK, NOP

### Undocumented Opcodes (151)
All undocumented opcodes are implemented:
- **Stable opcodes**: LAX, SAX, DCP, ISC, SLO, RLA, SRE, RRA, ANC, ALR, ARR, AXS, LAS
- **Undocumented NOPs**: 27 variants with different addressing modes and cycle counts
- **Undocumented SBC**: Duplicate SBC instruction (opcode 0xEB)

### Unstable Opcodes
The following opcodes exhibit hardware-dependent behavior and may produce varying results across different 6502 chip revisions:
- **XAA (0x8B)**: Transfer X to A then AND with immediate value
- **AHX (0x9F, 0x93)**: Store A AND X AND H with unstable high-byte addressing
- **SHY (0x9C)**: Store Y AND H with unstable high-byte addressing
- **SHX (0x9E)**: Store X AND H with unstable high-byte addressing
- **TAS (0x9B)**: Transfer A AND X to stack pointer, then store with unstable addressing

These opcodes are implemented with behaviour that reflects authentic hardware variability.

## Requirements

- .NET 8.0 SDK
- C# 12.0

For the C64 emulator application:
- Native SDL2 runtime library available to the OS dynamic loader
- OpenGL-capable graphics environment
- C64 ROM files in `C64/ROMS`:
  - `basic.901226-01.bin`
  - `kernal.901227-03.bin`
  - `characters.901225-01.bin`

NuGet packages restored by the project files:

| Project | Package | Version | Purpose |
|---|---|---|---|
| `C64` | `Sayers.SDL2.Core` | `1.0.11` | SDL2 bindings for video, input, and audio |
| `C64` | `Silk.NET.OpenGL` | `2.21.0` | OpenGL bindings used by the main display presenter and ImGui picker windows |
| `C64` | `ImGui.NET` | `1.91.6.1` | ImGui UI used by the audio-device, software picker, and save windows |

## Building

```bash
dotnet build C64.sln
```

## Running C64 Emulator

```bash
cd C64
dotnet run -c Release
```

### Software Loading And Saving

Press `Ctrl+L` while the emulator is running to open the OS-native file picker for supported C64 software, disks, tapes, cartridges, and SID files. The emulator pauses while the picker is open; selecting a file unpauses the emulator and uses the existing extension-based loader to reset, load, and run the selected software. Closing or cancelling the picker restores the previous pause state. `.sid`, `.psid`, and `.rsid` files are parsed as SID tunes and started directly with a small in-memory player driver. `.crt` files are inserted as cartridges and reset into, with standard 8K/16K/Ultimax, EasyFlash, and Magic Desk banking supported.

Press `Ctrl+S` to open the ImGui save dialog for the current BASIC program. The emulator pauses while the save dialog is active, then restores the previous pause state after saving or cancelling. Files are saved into `C64/Software` as standard `.prg` files with a two-byte little-endian load address followed by the saved program bytes.

Native C64 `SAVE` commands also write standard `.prg` files into `C64/Software`. For example, `SAVE "HELLO",8` creates `HELLO.prg`. Disk-style prefixes and options such as `SAVE "0:HELLO,P",8` are normalized to a host filename like `HELLO.prg`.

### Audio Device Selection

At startup, the emulator automatically opens playback device `[0]` from SDL's audio device list. Press `Ctrl+A` while the emulator is running to open the ImGui audio-device selector and switch output devices. The emulator pauses while the selector is active and restores the previous pause state after selecting or cancelling.

### Runtime Hotkeys (Emulator Controls)

| Keyboard Shortcut | Action |
|---|---|
| `F12` or `Ctrl+R` | Hard reset CPU and peripherals |
| `Ctrl+A` | Open audio-device selector |
| `Ctrl+F` | Toggle fullscreen undistorted C64 viewport |
| `Ctrl+P` | Pause/unpause emulator |
| `Ctrl+Q` | Toggle audio mute; shows a small mute icon in the bottom-left corner while muted |
| `Ctrl+L` | Open native file picker to load and run software, disks, tapes, cartridges, or SID files |
| `Ctrl+S` | Open save dialog for current BASIC program; saves a `.prg` into `Software` |
| `Shift+S` | Full SDL window screenshot (saved as `c64_screenshot_*.png`) |
| `Ctrl+Shift+S` | Undistorted viewport screenshot (saved as `c64_viewport_screenshot_*.png`) |
| `Ctrl+Alt+Shift+S` | Sprite-index debug screenshot (saved as `c64_sprite_debug_screenshot_*.png`) |
| `Shift+Q` or `Alt+Q` or `Ctrl+W` | Quit emulator |
| `Caps Lock` | Toggle C64 SHIFT LOCK |
| `Page Up` or `Pause` | Trigger RESTORE NMI |

### Display Overlays

The emulator keeps small transparent status glyphs in the lower-left corner:

| Glyph | Meaning |
|---|---|
| Muted speaker | Audio is muted via `Ctrl+Q` |
| Green activity LED | Virtual drive activity on device 8 |

### Disk / 1541 Notes

`.d64` images are attached as virtual device 8. Simple program loads are supported, and the virtual drive also implements enough 1541-style command/channel behaviour for some direct-access disk software:

- command/status channel `15`
- status strings such as `00, OK,00,00`
- direct sector reads using `U1:` / `UA:`
- buffer pointer positioning using `B-P:`
- logical file channels through KERNAL `OPEN`, `CLOSE`, `CHKIN`, `CHKOUT`, `CHRIN`, `CHROUT`, and `CLRCHN`

This is not a cycle-exact 1541 emulator. Disk operations currently return data through a fast virtual path instead of modelling the 1541 CPU, DOS ROM, mechanical latency, IEC serial timing, or busy delays. Games such as Zork may therefore skip real-world waiting periods while still loading and accessing their disk data successfully.

## Keyboard Mapping (C64 vs UK 101)

This emulator is wired for UK keyboard layout and UK punctuation mode.

| C64 key / function | UK 101 keyboard | Notes |
|---|---|---|
| RESTORE (NMI) | `PageUp` or `Pause` | Triggers NMI callback (not CIA matrix key) |
| COMMODORE (`C=`) | `Right Alt` (AltGr) | Used as Commodore modifier; required for C=`+1..8` colors |
| RUN/STOP | `Esc` | Matrix mapped for software polling keyboard matrix |
| SHIFT LOCK | `Caps Lock` (toggle) | Latches C64 shift in matrix |
| CLR/HOME | `Home` | Matrix key + PETSCII home behavior |
| INST/DEL | `Insert` or `Backspace/Delete` | Shares C64 INST/DEL key behavior |
| Cursor Left/Right | `Left` / `Right` arrows | C64 cursor semantics |
| Cursor Up/Down | `Up` / `Down` arrows | Also used for joystick mapping in many game profiles |
| F1/F3/F5/F7 | `F1/F3/F5/F7` | C64 function pairs are handled in PETSCII layer |
| Color shortcuts `Ctrl+1..8` | `Left Ctrl + 1..8` | PETSCII control-color codes |
| Color shortcuts `C=+1..8` | `Right Alt + 1..8` | PETSCII Commodore-color codes |
| Joystick fire (port 2) | `Space` or `Right Ctrl` | `Right Alt` is reserved for Commodore key |
| Joystick directions (port 2) | Arrow keys | Also drives C64 cursor keys |
| UK punctuation `;:` | Host `;:` key | Mapped to C64 `:` key matrix position |
| UK punctuation `'@` | Host `'@` key | Mapped to C64 `;` key matrix position |

Notes:
- `Right Alt` (AltGr) is reserved for the C64 COMMODORE key.
- The emulator does not use macOS `Command` key aliases for control shortcuts.
- If your host keyboard lacks a usable right-side Alt key, remap COMMODORE in `C64/Keyboard.cs`.

### Joystick / Controller Input

Joystick input is currently mapped to C64 joystick port 2, which is the port used by many games.

| Input | C64 joystick action |
|---|---|
| Arrow keys | Up/down/left/right |
| `Left Ctrl` or `Right Ctrl` | Fire |
| SDL game controller D-pad | Up/down/left/right |
| SDL game controller left stick | Up/down/left/right, with dead zone |
| SDL game controller A/B/X/Y or shoulder buttons | Fire |

SDL-compatible controllers are detected at startup, and the emulator will open the first available controller. If that controller is disconnected, it will try to reopen another available one.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

Copyright (c) 2025 James Booth
