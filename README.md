# 6502 CPU Emulator

A cycle-accurate MOS 6502 CPU emulator written in C# (.NET 8.0) with comprehensive instruction set support including all documented and undocumented opcodes.

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

## References

- [SingleStepTests/65x02](https://github.com/SingleStepTests/65x02) - Test data used for validation

## Copyright

Copyright (c) 2025 James Booth. All rights reserved.

This code is the property of James Booth and may not be used, copied, or distributed without permission.
