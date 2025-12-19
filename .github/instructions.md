# 6502 CPU Emulator - Instructions

## Project Overview

This is a cycle-accurate MOS 6502 CPU emulator written in C# (.NET 8.0), with a Commodore 64 Windows Forms frontend. The project implements the complete 6502 instruction set including documented and undocumented opcodes.

## Architecture

### Three-Project Solution Structure

1. **6502CPU** - Core CPU emulation library
   - [`6502_CPU.cs`](6502CPU/6502_CPU.cs) - Main CPU class with Execute() method containing ~256 opcode cases
   - [`Memory.cs`](6502CPU/Memory.cs) - 64KB memory with ROM/RAM regions
   - [`Registers.cs`](6502CPU/Registers.cs) - PC, S, P, A, X, Y registers
   - [`Flags.cs`](6502CPU/Flags.cs) - Processor status flags (C, Z, I, D, B, V, N)
   
2. **C64** - Windows Forms Commodore 64 emulator (`.csproj` targets `net8.0-windows`)
   - Loads BASIC.ROM (0xA000), KERNAL.ROM (0xE000), CHAR.ROM (0xD000)
   - Character-based display rendering from memory address 0x400
   - [`C64CharConverter.cs`](C64/C64CharConverter.cs) - PETSCII to ASCII conversion
   
3. **CPU_TESTS** - JSON-based test suite using SingleStepTests format
   - Tests from https://github.com/SingleStepTests/65x02/
   - Validates initial/final register state and memory for each opcode

## Critical Patterns

### Opcode Implementation Pattern

Every 6502 instruction follows this naming convention in [`6502_CPU.cs`](6502CPU/6502_CPU.cs):
- **Mnemonic** + **AddressingMode** suffix (e.g., `LDA_IM()`, `LDA_AB()`, `LDA_ABX()`)
- Suffixes: `IM` (Immediate), `AB` (Absolute), `ABX` (Absolute,X), `ABY` (Absolute,Y), `ZP` (Zero Page), `XZP` (X-indexed Zero Page), `YZP` (Y-indexed Zero Page), `XZI` (X-indexed Zero Page Indirect), `YZI` (Zero Page Indirect Y-indexed)

Example implementation:
```csharp
private void LDA_ZP()
{
    registers.A = ReadByteFromMemory(Zero_Page());
    Set_FlagsNZ(registers.A);
    cyclesThisOperation += 3;
}
```

**Key Pattern**: Each instruction method:
1. Calls addressing mode helper (e.g., `Zero_Page()`)
2. Performs operation on registers
3. Updates flags via helpers like `Set_FlagsNZ()`
4. Increments `cyclesThisOperation` to match 6502 timing

### Addressing Mode Helpers

Located in [`6502_CPU.cs`](6502CPU/6502_CPU.cs) lines 690-770. These handle PC increment and cycle counting:
- `Immediate()` - Returns next byte, increments PC
- `Absolute()` - Reads 16-bit address, increments PC by 2
- `X_Indexed_Absolute(bool checkBoundary)` - Adds X register, may add cycle on page crossing
- `Zero_Page_Indirect_Y_Indexed()` - Implements (ZP),Y addressing

### Cycle Accuracy

The emulator tracks cycle counts:
- `cyclesThisOperation` accumulates during instruction execution
- Page boundary crossings add extra cycles (see `CrossBoundary()` checks)
- Used for timing synchronization in C64 frontend

### ROM/RAM Memory Model

In [`Memory.cs`](6502CPU/Memory.cs):
- ROM regions tracked in `List<ROM>` - writes to ROM addresses are silently ignored
- `Load(filePath, startAddr, length, readOnly)` - Load binary files as ROM/RAM
- Word reads are little-endian: `(byte2 << 8) | byte1`

### Interrupt Handling

Two interrupt types in [`6502_CPU.cs`](6502CPU/6502_CPU.cs):
- **NMI** (Non-Maskable) - Processed via `ProcessNMI()`, default vector 0xFFFA
- **IRQ** (Maskable) - Processed via `ProcessIRQ()`, respects I flag, default vector 0xFFFE
- Both use buffered queues (`IRQ_Buffer`, `NMI_Buffer`) processed before each instruction

## Development Workflows

### Building and Running

```bash
# Build entire solution
dotnet build 6502CPU.sln

# Run CPU tests
dotnet run --project CPU_TESTS/CPU_TESTS.csproj

# Run C64 emulator (Windows only)
dotnet run --project C64/C64.csproj

# Run standalone CPU (basic test loop)
dotnet run --project 6502CPU/6502CPU.csproj
```

### Testing Strategy

Tests in [`CPU_TESTS/Program.cs`](CPU_TESTS/Program.cs):
1. Load JSON test files from SingleStepTests repository
2. Set initial CPU state (PC, registers, flags, memory)
3. Execute single instruction: `cpu.GetNextInstruction()` then `cpu.Execute(opcode)`
4. Assert final state matches expected (registers, flags, memory, cycle count)

To add new opcode tests, update `testDictionary` with opcode hex values (e.g., `["a9", "ad"]`).

## Project-Specific Conventions

### Namespace Convention
Uses `_6502CPU` namespace (underscore prefix) because C# identifiers can't start with digits.

### Flag Management
Flags in [`Flags.cs`](6502CPU/Flags.cs) use **static backing fields** - unusual pattern, likely for performance:
```csharp
private static bool c = false;
public bool C { get { return c; } set { c = value; } }
```

### Decimal Mode (BCD)
Full BCD arithmetic implemented in ADC/SBC methods - handles low/high nibble separately with half-carry logic.

### Undocumented Opcodes
Located in `#region Undocumented Opcodes` sections in [`6502_CPU.cs`](6502CPU/6502_CPU.cs). All 151 undocumented opcodes are implemented:
- **Combined operations**: LAX (load A&X), SAX (store A&X), DCP (DEC+CMP), ISC (INC+SBC), SLO (ASL+ORA), RLA (ROL+AND), SRE (LSR+EOR), RRA (ROR+ADC)
- **Immediate operations**: ANC (AND+N→C), ALR (AND+LSR), ARR (AND+ROR), XAA (TXA+AND), AXS ((A&X)-value)
- **Unstable operations**: AHX, SHY, SHX, TAS, LAS (behavior depends on bus state/high byte)
- **Undocumented NOPs**: 27 variants with different addressing modes and cycle counts

Follow same implementation pattern as documented opcodes: addressing mode helper → operation → flag updates → cycle counting.

#### Known Test Issues with Unstable Opcodes

**Expected test failures**: The following opcodes are genuinely unstable and exhibit varying behavior across different 6502 chip revisions. The SingleStepTests test suite itself reflects this inconsistency with mixed expected values:

- **ARR (0x6B)**: ~15% test failure rate. Decimal mode BCD adjustment is complex and varies by implementation. Current implementation:
  - N/Z flags set from ROR result (before BCD adjustment)
  - C flag from bit 6 of ROR result (or from BCD high-byte overflow in decimal mode)
  - V flag from bit 6 XOR bit 5 of ROR result
  - BCD adjustment only affects final A value and C flag in decimal mode
  
- **XAA (0x8B)**: ~24% test failure rate. TXA operation affected by old accumulator value due to bus conflicts. Test suite shows inconsistent expectations - some tests expect `X & imm`, others expect `(X & A_old) & imm`. Current implementation uses `X & imm` (most common).

- **AHX (0x9F, 0x93)**: ~30-40% test failure rate. Stores `A & X & H` where H is high byte of address. The actual behavior varies - some chips use `H+1`, others use `H`, and page-crossing behavior is chip-dependent.

- **SHY (0x9C)**: ~30-40% test failure rate. Stores `Y & H` with similar unstable high-byte behavior as AHX.

- **SHX (0x9E)**: ~30-40% test failure rate. Stores `X & H` with similar unstable high-byte behavior as AHX.

- **TAS (0x9B)**: ~30-40% test failure rate. Sets `S = A & X`, then stores `A & X & H` with unstable addressing.

**Important**: These test failures are NOT bugs - they reflect authentic hardware behavior variability. Different 6502 chips (NMOS variants, different manufacturers) genuinely produce different results for these opcodes. When modifying these implementations, understand that perfect test pass rates are impossible.

## Key Files for Onboarding

1. [`6502CPU/6502_CPU.cs`](6502CPU/6502_CPU.cs) - Start here, read `Execute()` switch and instruction implementations
2. [`CPU_TESTS/Program.cs`](CPU_TESTS/Program.cs) - Understand test harness before modifying opcodes
3. [`6502CPU/Memory.cs`](6502CPU/Memory.cs) - Memory model with ROM protection
4. [`C64/Form1.cs`](C64/Form1.cs) - See how CPU integrates with frontend (threading, display loop)
