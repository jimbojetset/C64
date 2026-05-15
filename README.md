# 6502 CPU Emulator

A cycle-accurate MOS 6502 CPU emulator written in C# (.NET 8.0) with comprehensive instruction set support including all documented and undocumented opcodes.

This repository now contains a full C64 emulator stack built on top of the CPU core, including VIC-II video, SID audio, CIA timers/IO, keyboard/joystick input, IEC/disk support, and datasette support.

## Projects

### 6502CPU
The core emulator library implementing the complete 6502 processor. Features include:
- Cycle-accurate instruction execution
- 64KB addressable memory space
- Full register set (A, X, Y, S, PC) and status flags (N, V, B, D, I, Z, C)
- IRQ and NMI interrupt handling
- 151 documented opcodes
- 151 undocumented opcodes (including unstable variants)

### CPU_TESTS
Comprehensive test suite validating emulator accuracy against the [SingleStepTests/65x02](https://github.com/SingleStepTests/65x02) reference test data. Tests all opcodes with thousands of test cases per instruction.

### C64
Commodore 64 emulator application using SDL2 for display/input/audio. Key features include:
- Cycle-driven CPU/VIC/CIA integration
- VIC raster stepping with bus-steal/stall accounting
- SID synthesis with MODE/VOL semantics
- CIA timer/ICR/TOD handling, serial shift behavior, and NMI/IRQ paths
- IEC + virtual 1541 D64 file loading support
- TAP datasette pulse playback with motor/sense/read behavior
- Host file loading for PRG/T64/TAP/D64

## Quick Keymap

Most-used non-standard key mappings:

| C64 key / function | UK 101 | MacBook |
|---|---|---|
| RESTORE (NMI) | `PageUp` or `Pause` | `Fn + Up Arrow` (PageUp) |
| COMMODORE (`C=`) | `Right Alt` (AltGr) | `Right Option` |
| RUN/STOP | `Esc` | `Esc` |
| SHIFT LOCK | `Caps Lock` (toggle) | `Caps Lock` (toggle) |
| CLR/HOME | `Home` | `Fn + Left Arrow` (Home) |
| INST/DEL | `Insert` or `Backspace/Delete` | `Fn + Enter` (Insert, model dependent) or `Backspace` |

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

These opcodes are implemented but are disabled in the test suite by default, as they reflect authentic hardware variability rather than emulation bugs.

## Requirements

- .NET 8.0 SDK
- C# 12.0

## Building

```bash
dotnet build 6502CPU.sln
```

## Running Tests

```bash
cd CPU_TESTS
dotnet run
```

Tests automatically fetch test data from the [SingleStepTests repository](https://raw.githubusercontent.com/SingleStepTests/65x02/main/6502/v1/) and validate opcode behavior.

Important note:
- The current CPU test harness uses a Windows-style local path in `CPU_TESTS/Program.cs` for some runs. On macOS/Linux this may need adjusting to a local/relative dataset path if you want full offline runs.

## Running C64 Emulator

```bash
cd C64
dotnet run -c Release
```

### Runtime Hotkeys (Emulator Controls)

| Keyboard Shortcut | Action |
|---|---|
| `F12` or `Ctrl+R` | Hard reset CPU and peripherals |
| `F11` or `Ctrl+Q` or `Shift+Q` or `Alt+Q` | Debug dump (emulation state to stdout) |
| `Ctrl+O` | Open file dialog (load PRG/D64/T64/TAP) |
| `Ctrl+S` | Save memory range prompt |
| `Shift+S` | Screenshot (saved as `c64_screenshot_*.bmp`) |
| `Caps Lock` | Toggle C64 SHIFT LOCK |
| `Page Up` or `Pause` | Trigger RESTORE NMI |

## Keyboard Mapping (C64 vs UK 101 vs MacBook)

This emulator maps non-standard C64 keys to practical modern equivalents.

| C64 key / function | UK 101 keyboard | MacBook keyboard (ISO/ANSI common) | Notes |
|---|---|---|---|
| RESTORE (NMI) | `PageUp` or `Pause` | `Fn + Up Arrow` (PageUp) or `Fn + P` (Pause, if available) | Triggers NMI callback (not CIA matrix key) |
| COMMODORE (`C=`) | `Right Alt` (AltGr) | `Right Option` (or map external Right Alt) | Used as Commodore modifier; required for C=`+1..8` colors |
| RUN/STOP | `Esc` | `Esc` | Matrix mapped for software polling keyboard matrix |
| SHIFT LOCK | `Caps Lock` (toggle) | `Caps Lock` (toggle) | Latches C64 shift in matrix |
| CLR/HOME | `Home` | `Fn + Left Arrow` (Home) | Matrix key + PETSCII home behavior |
| INST/DEL | `Insert` or `Backspace/Delete` | `Fn + Enter` (Insert on many Mac mappings) or `Backspace` | Shares C64 INST/DEL key behavior |
| Cursor Left/Right | `Left` / `Right` arrows | `Left` / `Right` arrows | C64 cursor semantics |
| Cursor Up/Down | `Up` / `Down` arrows | `Up` / `Down` arrows | Also used for joystick mapping in many game profiles |
| F1/F3/F5/F7 | `F1/F3/F5/F7` | `Fn + F1/F3/F5/F7` (depending on macOS Fn mode) | C64 function pairs are handled in PETSCII layer |
| Color shortcuts `Ctrl+1..8` | `Left Ctrl + 1..8` | `Left Control + 1..8` | PETSCII control-color codes |
| Color shortcuts `C=+1..8` | `Right Alt + 1..8` | `Right Option + 1..8` | PETSCII Commodore-color codes |
| Joystick fire (port 2) | `Right Ctrl` or `Left Ctrl` or `Left Alt` | `Right Control`/`Left Control`/`Left Option` | `Right Alt/Option` is reserved for Commodore key |

Notes:
- On compact MacBook keyboards, `PageUp`, `Home`, and `Insert` are commonly available via `Fn` combinations and may vary by model/layout.
- If your host keyboard does not expose a usable right-side Alt/Option key, remap COMMODORE in `C64/Keyboard.cs`.
- A focused UK mapping reference also exists at `C64/KEYMAP_UK101.md`.

## References

- [SingleStepTests/65x02](https://github.com/SingleStepTests/65x02) - Test data used for validation

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

Copyright (c) 2025 James Booth
