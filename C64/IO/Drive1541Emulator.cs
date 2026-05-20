// ============================================================================
// Project:     C64
// File:        Drive1541Emulator.cs
// Description: Optional 1541 drive emulation path with a drive-side 6502,
//              6522 VIA register shell, IEC line coupling, and ROM loading.
// Author:      James Booth
// Created:     2025
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Commodore 64 and related ROMs are property of their respective
//              rights holders. This emulator is for educational purposes only.
// ============================================================================

using C64.CPU;

namespace C64
{
    /// <summary>
    /// Hosts the optional 1541 drive-side execution path.
    /// This class loads a 1541 DOS ROM, owns the drive CPU and VIA register state, and exposes IEC line levels to the C64 bus.
    /// </summary>
    internal sealed class Drive1541Emulator
    {
        private const int DriveClockHz = 1_000_000;
        private const int RamSize = 0x0800;
        private const int Via1Base = 0x1800;
        private const int Via2Base = 0x1C00;
        private const int RomBase = 0xC000;
        private const int MaxCyclesPerHostStep = 512;
        private static readonly bool TraceEnabled =
            string.Equals(Environment.GetEnvironmentVariable("C64_1541_TRACE"), "1", StringComparison.Ordinal);
        private static readonly bool VerboseTraceEnabled =
            string.Equals(Environment.GetEnvironmentVariable("C64_1541_VERBOSE"), "1", StringComparison.Ordinal);

        private readonly CPU_6510 cpu = new CPU_6510(DriveClockHz);
        private readonly Via6522 via1 = new Via6522();
        private readonly Via6522 via2 = new Via6522();
        private readonly DiskMechanism disk = new DiskMechanism();
        private readonly byte[] ram = new byte[RamSize];
        private readonly byte[] rom;
        private int cycleDebt;
        private bool hostDataRelease = true;
        private bool hostClockRelease = true;
        private bool hostAtnRelease = true;
        private bool previousTraceDataRelease = true;
        private bool previousTraceClockRelease = true;
        private bool previousTraceAtnRelease = true;
        private int iecTransitionTraceCount;
        private int via1SerialReadTraceCount;
        private int serialByteTraceCount;
        private int driveRamWriteTraceCount;
        private int driveRamPcTraceCount;
        private int driveRamVia2TraceCount;
        private int driveRamZeroPageTraceCount;
        private int driveRamWaitLoopTraceCount;
        private int driveRamRetryTraceCount;
        private ushort previousDriveRamPc = 0xFFFF;
        private bool lowLevelTraceWindowActive;
        private bool driveRamLoopTracePrinted;

        /// <summary>Initializes a new Drive1541Emulator instance.</summary>
        /// <param name="rom">The 1541 DOS ROM bytes, normally 16 KiB mapped at $C000-$FFFF.</param>
        private Drive1541Emulator(byte[] rom)
        {
            this.rom = rom;
            cpu.memory.OnMemoryRead = ReadMemory;
            cpu.memory.OnMemoryWrite = WriteMemory;
            Reset();
        }

        /// <summary>Attempts to create a 1541 emulator using a full ROM image or split $C000/$E000 ROM halves.</summary>
        /// <param name="romPaths">Candidate filesystem paths for a full 1541 DOS ROM image.</param>
        /// <returns>A drive emulator when a ROM is found; otherwise, null.</returns>
        public static Drive1541Emulator? TryCreate(params string[] romPaths)
        {
            Drive1541Emulator? splitRom = TryCreateFromSplitRom(romPaths);
            if (splitRom is not null)
                return splitRom;

            foreach (string path in romPaths)
            {
                if (!File.Exists(path))
                    continue;

                byte[] rom = File.ReadAllBytes(path);
                if (rom.Length == 0x4000)
                {
                    if (!HasValidVectors(rom))
                    {
                        Console.Error.WriteLine($"Ignoring 1541 ROM {Path.GetFileName(path)}: reset vector does not point into drive ROM.");
                        continue;
                    }

                    return new Drive1541Emulator(rom);
                }

                if (rom.Length == 0x2000)
                {
                    Console.Error.WriteLine($"Ignoring 1541 ROM {Path.GetFileName(path)}: 8 KiB image needs a matching $E000-$FFFF half.");
                }
            }

            return null;
        }

        /// <summary>Resets the drive CPU, RAM, VIA registers, and IEC output lines.</summary>
        public void Reset()
        {
            Array.Clear(ram);
            via1.Reset();
            via2.Reset();
            disk.Reset();
            cpu.memory.memory[0xFFFC] = ReadMemory(0xFFFC, 0);
            cpu.memory.memory[0xFFFD] = ReadMemory(0xFFFD, 0);
            cpu.ResetNow();
        }

        /// <summary>Attaches a D64 image path to the drive emulation context.</summary>
        /// <param name="path">The path of the attached disk image.</param>
        public void AttachD64(string path)
        {
            disk.Attach(D64Image.Load(path));
        }

        /// <summary>Ejects the currently attached disk image from the drive emulation context.</summary>
        public void Eject()
        {
            disk.Eject();
        }

        /// <summary>Updates the host-side IEC line release states sampled by the drive VIA.</summary>
        /// <param name="dataRelease">Whether the C64 has released the DATA line.</param>
        /// <param name="clockRelease">Whether the C64 has released the CLOCK line.</param>
        /// <param name="atnRelease">Whether the C64 has released the ATN line.</param>
        public void UpdateHostLines(bool dataRelease, bool clockRelease, bool atnRelease)
        {
            hostDataRelease = dataRelease;
            hostClockRelease = clockRelease;
            hostAtnRelease = atnRelease;
            TraceHostLineTransition(dataRelease, clockRelease, atnRelease);
            TraceDrivePcDuringHostWait(dataRelease, clockRelease, atnRelease);
            via1.SetControlInputs(ca1High: !atnRelease, cb1High: !clockRelease, ca2High: true, cb2High: !dataRelease);
        }

        /// <summary>Starts a focused host IEC trace window around a detected low-level loader handoff.</summary>
        /// <param name="dataRelease">Whether the C64 has released the DATA line.</param>
        /// <param name="clockRelease">Whether the C64 has released the CLOCK line.</param>
        /// <param name="atnRelease">Whether the C64 has released the ATN line.</param>
        public void BeginLowLevelTraceWindow(bool dataRelease, bool clockRelease, bool atnRelease)
        {
            iecTransitionTraceCount = 0;
            via1SerialReadTraceCount = 0;
            serialByteTraceCount = 0;
            driveRamWriteTraceCount = 0;
            driveRamPcTraceCount = 0;
            driveRamVia2TraceCount = 0;
            driveRamZeroPageTraceCount = 0;
            driveRamWaitLoopTraceCount = 0;
            driveRamRetryTraceCount = 0;
            previousDriveRamPc = 0xFFFF;
            driveRamLoopTracePrinted = false;
            lowLevelTraceWindowActive = true;
            previousTraceDataRelease = !dataRelease;
            previousTraceClockRelease = !clockRelease;
            previousTraceAtnRelease = !atnRelease;
            if (TraceEnabled)
            {
                Trace("low-level IEC trace window opened");
                TraceHostLineTransition(dataRelease, clockRelease, atnRelease);
            }
        }

        /// <summary>Gets a short diagnostic string for the 1541 VIA1 IEC output state.</summary>
        public string GetIecTraceState()
        {
            bool busDataRelease = hostDataRelease && DeviceDataRelease;
            bool busClockRelease = hostClockRelease && DeviceClockRelease;
            return $"bus data={(busDataRelease ? "H" : "L")} clock={(busClockRelease ? "H" : "L")} dev data={(DeviceDataRelease ? "H" : "L")} clock={(DeviceClockRelease ? "H" : "L")} via1 pb=${via1.PortBOutput:X2} ddr=${via1.DataDirectionB:X2} dout={(via1.DataOutputRelease ? "H" : "L")} atna={(via1.AtnAcknowledgeDrivesLow(hostAtnRelease) ? "L" : "H")}";
        }

        /// <summary>Gets whether the emulated drive releases the IEC DATA line.</summary>
        public bool DeviceDataRelease => via1.DataOutputRelease && !via1.AtnAcknowledgeDrivesLow(hostAtnRelease);

        /// <summary>Gets whether the emulated drive releases the IEC CLOCK line.</summary>
        public bool DeviceClockRelease => via1.DeviceClockRelease;

        /// <summary>Steps the drive CPU for approximately the supplied number of host cycles.</summary>
        /// <param name="hostCycles">The number of C64 CPU cycles that have elapsed.</param>
        public void Step(int hostCycles)
        {
            if (hostCycles <= 0)
                return;

            cycleDebt = Math.Min(cycleDebt + hostCycles, MaxCyclesPerHostStep);
            while (cycleDebt > 0)
            {
                cpu.SetIrqLine(via1.IrqAsserted || via2.IrqAsserted);
                ushort pcBefore = (ushort)cpu.registers.PC;
                int elapsed = cpu.StepInstruction();
                if (elapsed <= 0)
                    break;

                via1.Step(elapsed);
                via2.Step(elapsed);
                disk.Step(elapsed, via2);
                cpu.SetIrqLine(via1.IrqAsserted || via2.IrqAsserted);
                TraceIrqState();
                TraceSerialByteReceive(pcBefore);
                TraceDriveRamExecution(pcBefore);
                cycleDebt -= elapsed;
            }
        }

        /// <summary>Reads from the 1541 CPU memory map, including RAM, VIA registers, and DOS ROM.</summary>
        /// <param name="addr">The 1541 CPU address.</param>
        /// <param name="fallback">The fallback memory byte.</param>
        /// <returns>The byte visible to the drive CPU.</returns>
        private byte ReadMemory(ulong addr, byte fallback)
        {
            ushort a = (ushort)(addr & 0xFFFF);
            if (a < RamSize)
                return ram[a & (RamSize - 1)];

            if (IsViaAddress(a, Via1Base))
            {
                byte register = (byte)(a & 0x0F);
                byte value = via1.Read(register, hostDataRelease, hostClockRelease, hostAtnRelease);
                TraceVia1SerialRead(register, value);
                return value;
            }

            if (IsViaAddress(a, Via2Base))
            {
                byte register = (byte)(a & 0x0F);
                byte value = via2.Read(register, disk.ReadPortA(), disk.ReadPortB());
                TraceDriveRamVia2Access("R", register, value);
                return value;
            }

            if (a >= RomBase)
                return rom[a - RomBase];

            return fallback;
        }

        /// <summary>Writes to the 1541 CPU memory map, including RAM and VIA registers.</summary>
        /// <param name="addr">The 1541 CPU address.</param>
        /// <param name="value">The value written by the drive CPU.</param>
        /// <returns>True when the write was handled by the drive map; otherwise, false.</returns>
        private bool WriteMemory(ulong addr, byte value)
        {
            ushort a = (ushort)(addr & 0xFFFF);
            if (a < RamSize)
            {
                ram[a & (RamSize - 1)] = value;
                TraceDriveRamWrite(a, value);
                TraceDriveZeroPageWrite(a, value);
                TryAccelerateDriveJob(a, value);
                return true;
            }

            if (IsViaAddress(a, Via1Base))
            {
                TraceViaWrite("VIA1", (byte)(a & 0x0F), value, previousVia1Trace, previousVia1TraceSet);
                via1.Write((byte)(a & 0x0F), value);
                return true;
            }

            if (IsViaAddress(a, Via2Base))
            {
                TraceViaWrite("VIA2", (byte)(a & 0x0F), value, previousVia2Trace, previousVia2TraceSet);
                via2.Write((byte)(a & 0x0F), value);
                disk.UpdateControl(via2.PortBOutput, via2.DataDirectionB);
                TraceDriveRamVia2Access("W", (byte)(a & 0x0F), value);
                return true;
            }

            return a >= RomBase;
        }

        /// <summary>Handles the uploaded fast-loader's buffer-4 read job directly while the low-level trace window is active.</summary>
        /// <param name="addr">The zero-page address being written.</param>
        /// <param name="value">The job byte value.</param>
        private void TryAccelerateDriveJob(ushort addr, byte value)
        {
            if (!lowLevelTraceWindowActive || addr != 0x0004 || value != 0x80)
                return;

            byte track = ram[0x0E];
            byte sector = ram[0x0F];
            if (!disk.TryReadSector(track, sector, out byte[] sectorBytes))
            {
                Trace($"accelerated job read failed track={track} sector={sector}");
                return;
            }

            Array.Copy(sectorBytes, 0, ram, 0x0700, sectorBytes.Length);
            ram[0x0004] = 0x01;
            if (TraceEnabled)
                Console.WriteLine($"[1541] accelerated job read buffer=4 track={track} sector={sector} bytes={sectorBytes.Length} status=$01");
        }

        /// <summary>Determines whether an address selects a mirrored 6522 VIA register block.</summary>
        /// <param name="addr">The 1541 CPU address.</param>
        /// <param name="baseAddress">The base address of the VIA block.</param>
        /// <returns>True when the address is within the VIA block; otherwise, false.</returns>
        private static bool IsViaAddress(ushort addr, int baseAddress)
        {
            return (addr & 0xFC00) == baseAddress;
        }

        /// <summary>Attempts to create a 1541 emulator from split 8 KiB ROM files named 1541-c000.bin and 1541-e000.bin.</summary>
        /// <param name="romPaths">Candidate full-ROM paths whose directories are searched for split ROM halves.</param>
        /// <returns>A drive emulator when both ROM halves are valid; otherwise, null.</returns>
        private static Drive1541Emulator? TryCreateFromSplitRom(string[] romPaths)
        {
            foreach (string path in romPaths)
            {
                string? directory = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(directory))
                    directory = ".";

                string? lowerPath = FindSplitRomHalf(directory, upperHalf: false);
                string? upperPath = FindSplitRomHalf(directory, upperHalf: true);
                if (lowerPath is null || upperPath is null)
                    continue;

                byte[] lower = File.ReadAllBytes(lowerPath);
                byte[] upper = File.ReadAllBytes(upperPath);
                if (lower.Length != 0x2000 || upper.Length != 0x2000)
                {
                    Console.Error.WriteLine("Ignoring split 1541 ROM: both halves must be 8 KiB.");
                    continue;
                }

                byte[] rom = new byte[0x4000];
                Array.Copy(lower, 0, rom, 0x0000, lower.Length);
                Array.Copy(upper, 0, rom, 0x2000, upper.Length);
                if (!HasValidVectors(rom))
                {
                    Console.Error.WriteLine("Ignoring split 1541 ROM: reset vector does not point into drive ROM.");
                    continue;
                }

                return new Drive1541Emulator(rom);
            }

            return null;
        }

        /// <summary>Finds a 1541 split ROM half in a directory, preferring explicit address-labelled filenames.</summary>
        /// <param name="directory">The ROM directory to search.</param>
        /// <param name="upperHalf">Whether to find the $E000-$FFFF half; false finds the $C000-$DFFF half.</param>
        /// <returns>The matching ROM path, or null when no suitable half is present.</returns>
        private static string? FindSplitRomHalf(string directory, bool upperHalf)
        {
            if (!Directory.Exists(directory))
                return null;

            string[] paths = Directory.GetFiles(directory, "1541*.bin");
            return paths
                .Where(path => IsSplitRomHalf(path, upperHalf))
                .OrderBy(path => SplitRomPreference(path, upperHalf))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        /// <summary>Determines whether an 8 KiB 1541 ROM file looks like the requested split ROM half.</summary>
        /// <param name="path">The ROM path to inspect.</param>
        /// <param name="upperHalf">Whether to check for the $E000-$FFFF half; false checks for the $C000-$DFFF half.</param>
        /// <returns>True when the ROM appears to be the requested half; otherwise, false.</returns>
        private static bool IsSplitRomHalf(string path, bool upperHalf)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length != 0x2000)
                return false;

            bool hasVectors = HasValidUpperHalfVectors(bytes);
            return upperHalf ? hasVectors : !hasVectors;
        }

        /// <summary>Scores split ROM filenames so explicit address-labelled files win over generic names.</summary>
        /// <param name="path">The ROM path to score.</param>
        /// <param name="upperHalf">Whether the file is being considered for the upper ROM half.</param>
        /// <returns>A lower score for more preferred filenames.</returns>
        private static int SplitRomPreference(string path, bool upperHalf)
        {
            string name = Path.GetFileName(path).ToUpperInvariant();
            if (upperHalf && name.Contains("E000", StringComparison.Ordinal))
                return 0;
            if (!upperHalf && name.Contains("C000", StringComparison.Ordinal))
                return 0;
            if (!upperHalf && name.Equals("1541.BIN", StringComparison.Ordinal))
                return 1;
            return 2;
        }

        /// <summary>Determines whether a normalized 1541 ROM image has CPU vectors that point into ROM space.</summary>
        /// <param name="rom">The normalized 16 KiB ROM image mapped at $C000-$FFFF.</param>
        /// <returns>True when the reset vector points into the ROM image; otherwise, false.</returns>
        private static bool HasValidVectors(byte[] rom)
        {
            ushort resetVector = (ushort)(rom[0x3FFC] | (rom[0x3FFD] << 8));
            return resetVector >= RomBase;
        }

        /// <summary>Determines whether an 8 KiB upper ROM half contains a reset vector into 1541 ROM space.</summary>
        /// <param name="rom">The 8 KiB ROM half mapped at $E000-$FFFF.</param>
        /// <returns>True when the reset vector points into 1541 ROM space; otherwise, false.</returns>
        private static bool HasValidUpperHalfVectors(byte[] rom)
        {
            ushort resetVector = (ushort)(rom[0x1FFC] | (rom[0x1FFD] << 8));
            return resetVector >= RomBase;
        }

        private bool previousVia1Irq;
        private bool previousVia2Irq;
        private int irqTraceCount;
        private int hostWaitPcTraceCount;
        private readonly byte[] previousVia1Trace = new byte[16];
        private readonly byte[] previousVia2Trace = new byte[16];
        private readonly bool[] previousVia1TraceSet = new bool[16];
        private readonly bool[] previousVia2TraceSet = new bool[16];

        /// <summary>Writes sparse IRQ transition diagnostics when C64_1541_TRACE=1 is set.</summary>
        private void TraceIrqState()
        {
            if (!VerboseTraceEnabled)
                return;

            bool via1Irq = via1.IrqAsserted;
            bool via2Irq = via2.IrqAsserted;
            if (via1Irq != previousVia1Irq || via2Irq != previousVia2Irq)
            {
                previousVia1Irq = via1Irq;
                previousVia2Irq = via2Irq;
                irqTraceCount++;
                if (irqTraceCount <= 32 || irqTraceCount % 512 == 0)
                    Console.WriteLine($"[1541] IRQ via1={via1Irq} via2={via2Irq} pc=${cpu.registers.PC:X4}");
            }
        }

        /// <summary>Writes an optional 1541 trace line when C64_1541_TRACE=1 is set.</summary>
        /// <param name="message">The diagnostic message to write.</param>
        private static void Trace(string message)
        {
            if (TraceEnabled)
                Console.WriteLine($"[1541] {message}");
        }

        /// <summary>Writes sparse host IEC line transition diagnostics when summary tracing is enabled.</summary>
        /// <param name="dataRelease">Whether the C64 has released DATA.</param>
        /// <param name="clockRelease">Whether the C64 has released CLOCK.</param>
        /// <param name="atnRelease">Whether the C64 has released ATN.</param>
        private void TraceHostLineTransition(bool dataRelease, bool clockRelease, bool atnRelease)
        {
            if (!TraceEnabled)
                return;

            bool changed = dataRelease != previousTraceDataRelease ||
                clockRelease != previousTraceClockRelease ||
                atnRelease != previousTraceAtnRelease;
            if (!changed)
                return;

            previousTraceDataRelease = dataRelease;
            previousTraceClockRelease = clockRelease;
            previousTraceAtnRelease = atnRelease;
            iecTransitionTraceCount++;
            if (iecTransitionTraceCount <= 32 || iecTransitionTraceCount == 128 || iecTransitionTraceCount == 512)
            {
                Console.WriteLine($"[1541] host IEC data={(dataRelease ? "H" : "L")} clock={(clockRelease ? "H" : "L")} atn={(atnRelease ? "H" : "L")} {GetIecTraceState()}");
            }
        }

        /// <summary>Writes sparse drive PC diagnostics while the host is holding DATA low during a low-level wait.</summary>
        /// <param name="dataRelease">Whether the C64 has released DATA.</param>
        /// <param name="clockRelease">Whether the C64 has released CLOCK.</param>
        /// <param name="atnRelease">Whether the C64 has released ATN.</param>
        private void TraceDrivePcDuringHostWait(bool dataRelease, bool clockRelease, bool atnRelease)
        {
            if (!TraceEnabled || dataRelease || !atnRelease)
                return;

            hostWaitPcTraceCount++;
            if (hostWaitPcTraceCount <= 16 || hostWaitPcTraceCount == 64 || hostWaitPcTraceCount == 256)
                Console.WriteLine($"[1541] host wait pc=${cpu.registers.PC:X4} host clock={(clockRelease ? "H" : "L")} {GetIecTraceState()}");
        }

        /// <summary>Writes a narrow VIA1 ORB read trace while the 1541 ROM is receiving IEC bits.</summary>
        /// <param name="register">The VIA register being read.</param>
        /// <param name="value">The value returned to the drive CPU.</param>
        private void TraceVia1SerialRead(byte register, byte value)
        {
            if (!TraceEnabled || (register & 0x0F) != 0x00)
                return;

            ushort pc = (ushort)cpu.registers.PC;
            if (pc < 0xEA00 || pc > 0xEA70)
                return;

            via1SerialReadTraceCount++;
            if (via1SerialReadTraceCount <= 64 || via1SerialReadTraceCount == 128 || via1SerialReadTraceCount == 256)
            {
                Console.WriteLine($"[1541] VIA1 ORB read pc=${pc:X4} value=${value:X2} host data={(hostDataRelease ? "H" : "L")} clock={(hostClockRelease ? "H" : "L")} atn={(hostAtnRelease ? "H" : "L")} {GetIecTraceState()}");
            }
        }

        /// <summary>Writes a byte-level trace when the 1541 ROM finishes the serial receive routine.</summary>
        /// <param name="pcBefore">The drive CPU PC before the instruction that just executed.</param>
        private void TraceSerialByteReceive(ushort pcBefore)
        {
            if (!TraceEnabled || pcBefore != 0xEA28)
                return;

            serialByteTraceCount++;
            if (serialByteTraceCount <= 32 || serialByteTraceCount == 64 || serialByteTraceCount == 128)
            {
                Console.WriteLine($"[1541] serial byte rx value=${ram[0x85]:X2} host data={(hostDataRelease ? "H" : "L")} clock={(hostClockRelease ? "H" : "L")} atn={(hostAtnRelease ? "H" : "L")} {GetIecTraceState()}");
            }
        }

        /// <summary>Writes a capped trace when uploaded drive code is written to RAM.</summary>
        /// <param name="addr">The drive RAM address.</param>
        /// <param name="value">The byte written.</param>
        private void TraceDriveRamWrite(ushort addr, byte value)
        {
            if (!TraceEnabled || !lowLevelTraceWindowActive || addr < 0x0300 || addr >= 0x0600)
                return;

            driveRamWriteTraceCount++;
            bool focused = addr >= 0x03B0 && addr <= 0x03D0;
            if (focused || driveRamWriteTraceCount <= 96 || driveRamWriteTraceCount == 128 || driveRamWriteTraceCount == 256)
                Console.WriteLine($"[1541] RAM ${addr:X4} <= ${value:X2} pc=${cpu.registers.PC:X4}");
        }

        /// <summary>Writes a focused trace for uploaded drive code state bytes.</summary>
        /// <param name="addr">The drive RAM address.</param>
        /// <param name="value">The byte written.</param>
        private void TraceDriveZeroPageWrite(ushort addr, byte value)
        {
            if (!TraceEnabled || !lowLevelTraceWindowActive || addr != 0x0004)
                return;

            ushort pc = (ushort)cpu.registers.PC;
            driveRamZeroPageTraceCount++;
            if (driveRamZeroPageTraceCount <= 64 || driveRamZeroPageTraceCount == 128 || driveRamZeroPageTraceCount == 256)
                Console.WriteLine($"[1541] RAM zp04 <= ${value:X2} pc=${pc:X4} {via2.DebugInterruptState()}");
        }

        /// <summary>Writes a capped trace when execution reaches uploaded drive RAM code.</summary>
        /// <param name="pcBefore">The drive CPU PC before the instruction that just executed.</param>
        private void TraceDriveRamExecution(ushort pcBefore)
        {
            if (!TraceEnabled || !lowLevelTraceWindowActive || pcBefore < 0x0300 || pcBefore >= 0x0600)
                return;

            driveRamPcTraceCount++;
            bool pcChanged = pcBefore != previousDriveRamPc;
            previousDriveRamPc = pcBefore;
            bool waitLoop = pcBefore == 0x03C0 || pcBefore == 0x03C2;
            if (waitLoop)
            {
                driveRamWaitLoopTraceCount++;
                if (driveRamWaitLoopTraceCount <= 8 ||
                    driveRamWaitLoopTraceCount == 32 ||
                    driveRamWaitLoopTraceCount == 128 ||
                    driveRamWaitLoopTraceCount == 512 ||
                    driveRamWaitLoopTraceCount == 2048 ||
                    driveRamWaitLoopTraceCount == 8192)
                {
                    Console.WriteLine($"[1541] RAM wait pc=${pcBefore:X4} zp04=${ram[0x04]:X2} irqLine={(via1.IrqAsserted || via2.IrqAsserted)} {via2.DebugInterruptState()} {disk.DebugState()}");
                }

                return;
            }

            bool retryLoop = pcBefore == 0x03BB ||
                pcBefore == 0x03BC ||
                pcBefore == 0x03BE ||
                pcBefore == 0x03C4 ||
                pcBefore == 0x03C6;
            if (retryLoop)
            {
                driveRamRetryTraceCount++;
                if (driveRamRetryTraceCount > 24 &&
                    driveRamRetryTraceCount != 64 &&
                    driveRamRetryTraceCount != 256 &&
                    driveRamRetryTraceCount != 1024)
                    return;
            }

            if (!driveRamLoopTracePrinted && pcBefore >= 0x03B0 && pcBefore <= 0x03D0)
            {
                driveRamLoopTracePrinted = true;
                Console.WriteLine($"[1541] RAM loop bytes 03B0={FormatRamBytes(0x03B0, 0x30)} zp0e=${ram[0x0E]:X2} zp0f=${ram[0x0F]:X2}");
            }
            if (pcChanged || driveRamPcTraceCount == 64 || driveRamPcTraceCount == 128 || driveRamPcTraceCount == 256 || driveRamPcTraceCount == 512)
            {
                byte op0 = ram[pcBefore & (RamSize - 1)];
                byte op1 = ram[(pcBefore + 1) & (RamSize - 1)];
                byte op2 = ram[(pcBefore + 2) & (RamSize - 1)];
                Console.WriteLine($"[1541] RAM exec pc=${pcBefore:X4} op=${op0:X2} {op1:X2} {op2:X2} a=${cpu.registers.A:X2} x=${cpu.registers.X:X2} y=${cpu.registers.Y:X2} zp0e=${ram[0x0E]:X2} zp0f=${ram[0x0F]:X2}");
            }
        }

        /// <summary>Formats drive RAM bytes for compact diagnostics.</summary>
        /// <param name="start">Start address in drive RAM.</param>
        /// <param name="count">Number of bytes to format.</param>
        /// <returns>A hexadecimal byte list.</returns>
        private string FormatRamBytes(int start, int count)
        {
            return string.Join(' ', Enumerable.Range(0, count).Select(i => ram[(start + i) & (RamSize - 1)].ToString("X2")));
        }

        /// <summary>Writes a capped trace for disk VIA accesses from uploaded drive code.</summary>
        /// <param name="kind">Read/write label.</param>
        /// <param name="register">The VIA2 register.</param>
        /// <param name="value">The byte read or written.</param>
        private void TraceDriveRamVia2Access(string kind, byte register, byte value)
        {
            if (!TraceEnabled || !lowLevelTraceWindowActive)
                return;

            ushort pc = (ushort)cpu.registers.PC;
            if (pc < 0x0300 || pc >= 0x0600)
                return;

            byte index = (byte)(register & 0x0F);
            if (index != 0x00 &&
                index != 0x01 &&
                index != 0x0B &&
                index != 0x0C &&
                index != 0x0D &&
                index != 0x0E)
                return;

            driveRamVia2TraceCount++;
            if (driveRamVia2TraceCount <= 96 || driveRamVia2TraceCount == 128 || driveRamVia2TraceCount == 256)
                Console.WriteLine($"[1541] RAM VIA2 {kind} reg=${index:X1} value=${value:X2} pc=${pc:X4} {via2.DebugInterruptState()}");
        }

        /// <summary>Writes selected VIA register writes when verbose 1541 tracing is enabled.</summary>
        /// <param name="via">The VIA label.</param>
        /// <param name="register">The low register index.</param>
        /// <param name="value">The value written by the drive CPU.</param>
        /// <param name="previous">The previous traced register values for this VIA.</param>
        /// <param name="previousSet">Whether the corresponding previous value is initialized.</param>
        private static void TraceViaWrite(string via, byte register, byte value, byte[] previous, bool[] previousSet)
        {
            if (!VerboseTraceEnabled)
                return;

            int index = register & 0x0F;
            if (previousSet[index] && previous[index] == value)
                return;

            previous[index] = value;
            previousSet[index] = true;

            string? name = register switch
            {
                0x00 => "ORB",
                0x01 => "ORA",
                0x02 => "DDRB",
                0x03 => "DDRA",
                0x0B => "ACR",
                0x0C => "PCR",
                0x0D => "IFR",
                0x0E => "IER",
                _ => null
            };
            if (name is not null)
                Console.WriteLine($"[1541] {via} {name} <= ${value:X2}");
        }

        /// <summary>
        /// Provides a first-pass 1541 disk mechanism: motor/head control, D64 sector-to-GCR track conversion, and byte-ready signalling.
        /// </summary>
        private sealed class DiskMechanism
        {
            private const int MinHalfTrack = 2;
            private const int MaxHalfTrack = 70;
            private const int CyclesPerGcrByte = 32;

            private static readonly byte[] Gcr =
            {
                0x0A, 0x0B, 0x12, 0x13,
                0x0E, 0x0F, 0x16, 0x17,
                0x09, 0x19, 0x1A, 0x1B,
                0x0D, 0x1D, 0x1E, 0x15
            };

            private D64Image? image;
            private byte[] trackBytes = Array.Empty<byte>();
            private int halfTrack = 36;
            private int currentTrack = -1;
            private int byteIndex;
            private int cycleRemainder;
            private int stepperPhase = -1;
            private bool motorOn;
            private bool byteReadyHigh = true;
            private byte currentByte = 0x55;

            /// <summary>Resets the mechanism to a plausible power-on head position and clears cached track data.</summary>
            public void Reset()
            {
                halfTrack = 36;
                currentTrack = -1;
                byteIndex = 0;
                cycleRemainder = 0;
                stepperPhase = -1;
                motorOn = false;
                byteReadyHigh = true;
                currentByte = 0x55;
                trackBytes = Array.Empty<byte>();
                Trace("disk reset");
            }

            /// <summary>Attaches a D64 image and prepares the current track stream.</summary>
            /// <param name="image">The D64 image to expose through the mechanism.</param>
            public void Attach(D64Image image)
            {
                this.image = image;
                currentTrack = -1;
                EnsureTrackLoaded();
                Trace("disk image attached");
            }

            /// <summary>Ejects media from the mechanism.</summary>
            public void Eject()
            {
                image = null;
                currentTrack = -1;
                trackBytes = Array.Empty<byte>();
                currentByte = 0x55;
                Trace("disk image ejected");
            }

            /// <summary>Updates motor and stepper state from VIA2 port B control outputs.</summary>
            /// <param name="portB">The VIA2 port B output latch.</param>
            /// <param name="ddrB">The VIA2 port B data-direction register.</param>
            public void UpdateControl(byte portB, byte ddrB)
            {
                bool newMotorOn = (portB & 0x04) == 0;
                if (newMotorOn != motorOn)
                {
                    motorOn = newMotorOn;
                    Trace($"motor {(motorOn ? "on" : "off")} halfTrack={halfTrack} track={CurrentTrackNumber()} portB=${portB:X2} ddrB=${ddrB:X2}");
                }

                int phase = portB & 0x03;
                if (stepperPhase < 0)
                {
                    stepperPhase = phase;
                    return;
                }

                int delta = (phase - stepperPhase + 4) & 0x03;
                if (delta == 1 && halfTrack < MaxHalfTrack)
                {
                    halfTrack++;
                    Trace($"step in halfTrack={halfTrack} track={CurrentTrackNumber()}");
                }
                else if (delta == 3 && halfTrack > MinHalfTrack)
                {
                    halfTrack--;
                    Trace($"step out halfTrack={halfTrack} track={CurrentTrackNumber()}");
                }

                stepperPhase = phase;
                EnsureTrackLoaded();
            }

            /// <summary>Advances disk rotation and signals VIA2 when a new GCR byte is available.</summary>
            /// <param name="cycles">The number of elapsed drive CPU cycles.</param>
            /// <param name="via">The disk VIA receiving byte-ready control edges.</param>
            public void Step(int cycles, Via6522 via)
            {
                if (!motorOn || trackBytes.Length == 0)
                {
                    via.SetControlInputs(ca1High: true, cb1High: true, ca2High: true, cb2High: true);
                    return;
                }

                cycleRemainder += cycles;
                while (cycleRemainder >= CyclesPerGcrByte)
                {
                    cycleRemainder -= CyclesPerGcrByte;
                    currentByte = trackBytes[byteIndex++];
                    if (byteIndex >= trackBytes.Length)
                        byteIndex = 0;

                    byteReadyHigh = !byteReadyHigh;
                    via.SetControlInputs(ca1High: byteReadyHigh, cb1High: IsSyncHigh(), ca2High: true, cb2High: true);
                    TraceByteReady();
                }
            }

            /// <summary>Reads the current GCR byte exposed through VIA2 port A.</summary>
            /// <returns>The current disk data byte.</returns>
            public byte ReadPortA()
            {
                return currentByte;
            }

            /// <summary>Gets a compact diagnostic view of disk rotation state.</summary>
            public string DebugState()
            {
                return $"disk motor={(motorOn ? "on" : "off")} byteReady={(byteReadyHigh ? "H" : "L")} data=${currentByte:X2} index={byteIndex}";
            }

            /// <summary>Reads disk-side status bits exposed through VIA2 port B inputs.</summary>
            /// <returns>The port B input bits.</returns>
            public byte ReadPortB()
            {
                byte value = 0xFF;
                if (!IsSyncHigh())
                    value &= 0x7F;
                return value;
            }

            /// <summary>Reads a raw D64 sector for compatibility paths that emulate a completed DOS job.</summary>
            /// <param name="track">The one-based disk track.</param>
            /// <param name="sector">The zero-based sector number.</param>
            /// <param name="sectorBytes">Receives the 256-byte sector payload.</param>
            /// <returns>True when a mounted image contains the requested sector; otherwise, false.</returns>
            public bool TryReadSector(int track, int sector, out byte[] sectorBytes)
            {
                sectorBytes = Array.Empty<byte>();
                return image is not null && image.TryReadSector(track, sector, out sectorBytes);
            }

            /// <summary>Determines whether the disk sync input should read high.</summary>
            /// <returns>False while passing a simple sync-mark byte; otherwise, true.</returns>
            private bool IsSyncHigh()
            {
                return currentByte != 0xFF;
            }

            /// <summary>Ensures the cached GCR stream matches the current whole track.</summary>
            private void EnsureTrackLoaded()
            {
                int track = Math.Clamp((halfTrack + 1) / 2, 1, 35);
                if (track == currentTrack)
                    return;

                currentTrack = track;
                byteIndex = 0;
                cycleRemainder = 0;
                currentByte = 0x55;
                trackBytes = image is null ? Array.Empty<byte>() : BuildGcrTrack(image, track);
                Trace($"track loaded track={track} bytes={trackBytes.Length}");
            }

            private int byteReadyTraceCount;

            /// <summary>Writes sparse byte-ready diagnostics for the current track stream.</summary>
            private void TraceByteReady()
            {
                if (!TraceEnabled)
                    return;

                byteReadyTraceCount++;
                if (byteReadyTraceCount <= 8 || byteReadyTraceCount == 512 || byteReadyTraceCount == 4096)
                    Console.WriteLine($"[1541] byte ready track={CurrentTrackNumber()} index={byteIndex} data=${currentByte:X2} sync={!IsSyncHigh()}");
            }

            /// <summary>Gets the current whole track number derived from the half-track position.</summary>
            /// <returns>The one-based whole track number.</returns>
            private int CurrentTrackNumber()
            {
                return Math.Clamp((halfTrack + 1) / 2, 1, 35);
            }

            /// <summary>Builds an approximate GCR byte stream for a D64 track.</summary>
            /// <param name="image">The source D64 image.</param>
            /// <param name="track">The one-based disk track number.</param>
            /// <returns>The encoded GCR track byte stream.</returns>
            private static byte[] BuildGcrTrack(D64Image image, int track)
            {
                int sectors = D64Image.GetSectorCount(track);
                image.TryGetDiskId(out byte id1, out byte id2);
                var output = new List<byte>(sectors * 420);
                for (int sector = 0; sector < sectors; sector++)
                {
                    if (!image.TryReadSector(track, sector, out byte[] sectorBytes))
                        sectorBytes = new byte[256];

                    byte headerChecksum = (byte)(sector ^ track ^ id2 ^ id1);
                    AppendSync(output);
                    AppendGcr(output, new byte[] { 0x08, headerChecksum, (byte)sector, (byte)track, id2, id1, 0x0F, 0x0F });
                    AppendGap(output, 9);

                    byte dataChecksum = 0;
                    foreach (byte b in sectorBytes)
                        dataChecksum ^= b;

                    var data = new byte[260];
                    data[0] = 0x07;
                    Array.Copy(sectorBytes, 0, data, 1, sectorBytes.Length);
                    data[257] = dataChecksum;
                    data[258] = 0x00;
                    data[259] = 0x00;
                    AppendSync(output);
                    AppendGcr(output, data);
                    AppendGap(output, 16);
                }

                return output.ToArray();
            }

            /// <summary>Appends a short sync mark to a GCR stream.</summary>
            /// <param name="output">The GCR output buffer.</param>
            private static void AppendSync(List<byte> output)
            {
                for (int i = 0; i < 5; i++)
                    output.Add(0xFF);
            }

            /// <summary>Appends a repeated gap byte to a GCR stream.</summary>
            /// <param name="output">The GCR output buffer.</param>
            /// <param name="count">The number of gap bytes to append.</param>
            private static void AppendGap(List<byte> output, int count)
            {
                for (int i = 0; i < count; i++)
                    output.Add(0x55);
            }

            /// <summary>Encodes raw 1541 sector bytes into 5-byte GCR groups.</summary>
            /// <param name="output">The GCR output buffer.</param>
            /// <param name="data">The raw bytes to encode.</param>
            private static void AppendGcr(List<byte> output, byte[] data)
            {
                for (int i = 0; i < data.Length; i += 4)
                {
                    byte b0 = data[i];
                    byte b1 = i + 1 < data.Length ? data[i + 1] : (byte)0;
                    byte b2 = i + 2 < data.Length ? data[i + 2] : (byte)0;
                    byte b3 = i + 3 < data.Length ? data[i + 3] : (byte)0;

                    ulong packed =
                        ((ulong)Gcr[b0 >> 4] << 35) |
                        ((ulong)Gcr[b0 & 0x0F] << 30) |
                        ((ulong)Gcr[b1 >> 4] << 25) |
                        ((ulong)Gcr[b1 & 0x0F] << 20) |
                        ((ulong)Gcr[b2 >> 4] << 15) |
                        ((ulong)Gcr[b2 & 0x0F] << 10) |
                        ((ulong)Gcr[b3 >> 4] << 5) |
                        Gcr[b3 & 0x0F];

                    output.Add((byte)(packed >> 32));
                    output.Add((byte)(packed >> 24));
                    output.Add((byte)(packed >> 16));
                    output.Add((byte)(packed >> 8));
                    output.Add((byte)packed);
                }
            }
        }

        /// <summary>
        /// Models the 6522 VIA registers, timers, interrupt flags, control lines, and data-direction controlled ports.
        /// </summary>
        private sealed class Via6522
        {
            private const byte IfrCa2 = 0x01;
            private const byte IfrCa1 = 0x02;
            private const byte IfrShift = 0x04;
            private const byte IfrCb2 = 0x08;
            private const byte IfrCb1 = 0x10;
            private const byte IfrTimer2 = 0x20;
            private const byte IfrTimer1 = 0x40;
            private const byte IfrIrq = 0x80;

            private byte portB = 0xFF;
            private byte portA = 0xFF;
            private byte ddrB;
            private byte ddrA;
            private byte shiftRegister;
            private byte acr;
            private byte pcr;
            private byte ifr;
            private byte ier;
            private ushort timer1Latch = 0xFFFF;
            private int timer1Counter = 0xFFFF;
            private bool timer1Running;
            private bool timer1HasInterrupted;
            private ushort timer2Latch = 0xFFFF;
            private int timer2Counter = 0xFFFF;
            private bool timer2Running;
            private bool timer2HasInterrupted;
            private bool ca1High = true;
            private bool ca2High = true;
            private bool cb1High = true;
            private bool cb2High = true;

            /// <summary>Gets whether this VIA releases the IEC DATA line.</summary>
            public bool DeviceDataRelease => DataOutputRelease;

            /// <summary>Gets whether this VIA releases the IEC CLOCK line.</summary>
            public bool DeviceClockRelease => (ddrB & 0x08) == 0 || (portB & 0x08) == 0;

            /// <summary>Gets whether the serial DATA output driver is released.</summary>
            public bool DataOutputRelease => (ddrB & 0x02) == 0 || (portB & 0x02) == 0;

            /// <summary>Gets whether the ATN acknowledge output is released from the serial DATA line.</summary>
            public bool AtnAcknowledgeRelease => !AtnAcknowledgeDrivesLow(true);

            /// <summary>Gets whether the ATN acknowledge gate pulls DATA low for the supplied ATN line state.</summary>
            /// <param name="hostAtnRelease">Whether the C64 has released ATN.</param>
            /// <returns>True when ATNA drives the DATA line low; otherwise, false.</returns>
            public bool AtnAcknowledgeDrivesLow(bool hostAtnRelease)
            {
                if ((ddrB & 0x10) == 0)
                    return false;

                bool atnaSet = (portB & 0x10) != 0;
                return atnaSet == hostAtnRelease;
            }

            /// <summary>Gets the VIA port B output latch.</summary>
            public byte PortBOutput => portB;

            /// <summary>Gets the VIA port B data-direction register.</summary>
            public byte DataDirectionB => ddrB;

            /// <summary>Gets whether the VIA IRQ output is currently asserted.</summary>
            public bool IrqAsserted => (ReadInterruptFlags() & IfrIrq) != 0;

            /// <summary>Gets a compact diagnostic view of interrupt-relevant VIA state.</summary>
            public string DebugInterruptState()
            {
                return $"via ifr=${ReadInterruptFlags():X2}/${ifr:X2} ier=${ier:X2} acr=${acr:X2} pcr=${pcr:X2} t1={(timer1Running ? "run" : "stop")}:${timer1Counter:X4} ca1={(ca1High ? "H" : "L")} cb1={(cb1High ? "H" : "L")}";
            }

            /// <summary>Resets VIA registers to their power-on defaults.</summary>
            public void Reset()
            {
                portB = 0xFF;
                portA = 0xFF;
                ddrB = 0x00;
                ddrA = 0x00;
                shiftRegister = 0x00;
                acr = 0x00;
                pcr = 0x00;
                ifr = 0x00;
                ier = 0x00;
                timer1Latch = 0xFFFF;
                timer1Counter = 0xFFFF;
                timer1Running = false;
                timer1HasInterrupted = false;
                timer2Latch = 0xFFFF;
                timer2Counter = 0xFFFF;
                timer2Running = false;
                timer2HasInterrupted = false;
                ca1High = true;
                ca2High = true;
                cb1High = true;
                cb2High = true;
            }

            /// <summary>Updates the VIA control input pins and raises edge-sensitive interrupt flags when configured edges occur.</summary>
            /// <param name="ca1High">The new CA1 input level.</param>
            /// <param name="cb1High">The new CB1 input level.</param>
            /// <param name="ca2High">The new CA2 input level.</param>
            /// <param name="cb2High">The new CB2 input level.</param>
            public void SetControlInputs(bool ca1High, bool cb1High, bool ca2High, bool cb2High)
            {
                SetEdgeFlag(this.ca1High, ca1High, (pcr & 0x01) != 0, IfrCa1);
                SetEdgeFlag(this.cb1High, cb1High, (pcr & 0x10) != 0, IfrCb1);
                if ((pcr & 0x0E) == 0x00 || (pcr & 0x0E) == 0x02)
                    SetEdgeFlag(this.ca2High, ca2High, (pcr & 0x04) != 0, IfrCa2);
                if ((pcr & 0xE0) == 0x00 || (pcr & 0xE0) == 0x20)
                    SetEdgeFlag(this.cb2High, cb2High, (pcr & 0x40) != 0, IfrCb2);

                this.ca1High = ca1High;
                this.cb1High = cb1High;
                this.ca2High = ca2High;
                this.cb2High = cb2High;
            }

            /// <summary>Advances VIA timers and raises timer interrupt flags on underflow.</summary>
            /// <param name="cycles">The number of elapsed drive CPU cycles.</param>
            public void Step(int cycles)
            {
                StepTimer1(cycles);
                StepTimer2(cycles);
            }

            /// <summary>Reads a VIA register, merging IEC input lines into port B.</summary>
            /// <param name="register">The low register index.</param>
            /// <param name="hostDataRelease">Whether the C64 has released DATA.</param>
            /// <param name="hostClockRelease">Whether the C64 has released CLOCK.</param>
            /// <param name="hostAtnRelease">Whether the C64 has released ATN.</param>
            /// <returns>The register value visible to the drive CPU.</returns>
            public byte Read(byte register, bool hostDataRelease, bool hostClockRelease, bool hostAtnRelease)
            {
                bool busDataRelease = hostDataRelease && DataOutputRelease && !AtnAcknowledgeDrivesLow(hostAtnRelease);
                bool busClockRelease = hostClockRelease && DeviceClockRelease;
                byte portBInput = BuildIecPortBInput(busDataRelease, busClockRelease, hostAtnRelease);
                return Read(register, portAInput: 0xFF, portBInput);
            }

            /// <summary>Reads a VIA register with explicit port A and port B external input values.</summary>
            /// <param name="register">The low register index.</param>
            /// <param name="portAInput">The external input bits for port A.</param>
            /// <param name="portBInput">The external input bits for port B.</param>
            /// <returns>The register value visible to the drive CPU.</returns>
            public byte Read(byte register, byte portAInput, byte portBInput)
            {
                switch (register & 0x0F)
                {
                    case 0x00:
                        ClearInterruptFlags((byte)(IfrCb1 | IfrCb2));
                        return ReadPort(portB, ddrB, portBInput);
                    case 0x01:
                    case 0x0F:
                        ClearInterruptFlags((byte)(IfrCa1 | IfrCa2));
                        return ReadPort(portA, ddrA, portAInput);
                    case 0x02:
                        return ddrB;
                    case 0x03:
                        return ddrA;
                    case 0x04:
                        ClearInterruptFlags(IfrTimer1);
                        return (byte)(timer1Counter & 0xFF);
                    case 0x05:
                        return (byte)((timer1Counter >> 8) & 0xFF);
                    case 0x06:
                        return (byte)(timer1Latch & 0xFF);
                    case 0x07:
                        return (byte)(timer1Latch >> 8);
                    case 0x08:
                        ClearInterruptFlags(IfrTimer2);
                        return (byte)(timer2Counter & 0xFF);
                    case 0x09:
                        return (byte)((timer2Counter >> 8) & 0xFF);
                    case 0x0A:
                        return shiftRegister;
                    case 0x0B:
                        return acr;
                    case 0x0C:
                        return pcr;
                    case 0x0D:
                        return ReadInterruptFlags();
                    case 0x0E:
                        return (byte)(ier | 0x80);
                    default:
                        return 0x00;
                }
            }

            /// <summary>Writes a VIA register.</summary>
            /// <param name="register">The low register index.</param>
            /// <param name="value">The value written by the drive CPU.</param>
            public void Write(byte register, byte value)
            {
                switch (register & 0x0F)
                {
                    case 0x00:
                        portB = value;
                        ClearInterruptFlags((byte)(IfrCb1 | IfrCb2));
                        break;
                    case 0x01:
                    case 0x0F:
                        portA = value;
                        ClearInterruptFlags((byte)(IfrCa1 | IfrCa2));
                        break;
                    case 0x02:
                        ddrB = value;
                        break;
                    case 0x03:
                        ddrA = value;
                        break;
                    case 0x04:
                        timer1Latch = (ushort)((timer1Latch & 0xFF00) | value);
                        break;
                    case 0x05:
                        timer1Latch = (ushort)((timer1Latch & 0x00FF) | (value << 8));
                        timer1Counter = timer1Latch;
                        timer1Running = true;
                        timer1HasInterrupted = false;
                        ClearInterruptFlags(IfrTimer1);
                        break;
                    case 0x06:
                        timer1Latch = (ushort)((timer1Latch & 0xFF00) | value);
                        break;
                    case 0x07:
                        timer1Latch = (ushort)((timer1Latch & 0x00FF) | (value << 8));
                        ClearInterruptFlags(IfrTimer1);
                        break;
                    case 0x08:
                        timer2Latch = (ushort)((timer2Latch & 0xFF00) | value);
                        break;
                    case 0x09:
                        timer2Latch = (ushort)((timer2Latch & 0x00FF) | (value << 8));
                        timer2Counter = timer2Latch;
                        timer2Running = true;
                        timer2HasInterrupted = false;
                        ClearInterruptFlags(IfrTimer2);
                        break;
                    case 0x0A:
                        shiftRegister = value;
                        ClearInterruptFlags(IfrShift);
                        break;
                    case 0x0B:
                        acr = value;
                        break;
                    case 0x0C:
                        pcr = value;
                        break;
                    case 0x0D:
                        ClearInterruptFlags((byte)(value & 0x7F));
                        break;
                    case 0x0E:
                        if ((value & 0x80) != 0)
                            ier |= (byte)(value & 0x7F);
                        else
                            ier &= (byte)~(value & 0x7F);
                        break;
                }
            }

            /// <summary>Advances Timer 1 and raises its interrupt flag on underflow.</summary>
            /// <param name="cycles">The number of elapsed drive CPU cycles.</param>
            private void StepTimer1(int cycles)
            {
                if (!timer1Running)
                    return;

                timer1Counter -= cycles;
                if (timer1Counter >= 0)
                    return;

                bool continuous = (acr & 0x40) != 0;
                if (continuous)
                {
                    do
                    {
                        timer1Counter += timer1Latch + 2;
                    }
                    while (timer1Counter < 0);
                    SetInterruptFlag(IfrTimer1);
                    timer1HasInterrupted = true;
                    return;
                }

                if (!timer1HasInterrupted)
                    SetInterruptFlag(IfrTimer1);
                timer1HasInterrupted = true;
                timer1Running = false;
            }

            /// <summary>Advances Timer 2 and raises its interrupt flag on underflow in timed mode.</summary>
            /// <param name="cycles">The number of elapsed drive CPU cycles.</param>
            private void StepTimer2(int cycles)
            {
                if (!timer2Running || (acr & 0x20) != 0)
                    return;

                timer2Counter -= cycles;
                if (timer2Counter >= 0)
                    return;

                if (!timer2HasInterrupted)
                    SetInterruptFlag(IfrTimer2);
                timer2HasInterrupted = true;
                timer2Running = false;
            }

            /// <summary>Builds port B input bits with a conservative IEC serial-port mapping.</summary>
            /// <param name="hostDataRelease">Whether the C64 has released DATA.</param>
            /// <param name="hostClockRelease">Whether the C64 has released CLOCK.</param>
            /// <param name="hostAtnRelease">Whether the C64 has released ATN.</param>
            /// <returns>The port B input bits.</returns>
            private static byte BuildIecPortBInput(bool hostDataRelease, bool hostClockRelease, bool hostAtnRelease)
            {
                byte input = 0x00;
                if (!hostDataRelease)
                    input |= 0x01;
                if (!hostClockRelease)
                    input |= 0x04;
                if (!hostAtnRelease)
                    input |= 0x80;

                return input;
            }

            /// <summary>Raises an interrupt flag when an input transition matches the configured edge.</summary>
            /// <param name="oldHigh">The previous input level.</param>
            /// <param name="newHigh">The new input level.</param>
            /// <param name="positiveEdge">Whether the VIA is configured for a positive edge.</param>
            /// <param name="flag">The interrupt flag to set on a matching edge.</param>
            private void SetEdgeFlag(bool oldHigh, bool newHigh, bool positiveEdge, byte flag)
            {
                if (oldHigh == newHigh)
                    return;

                if ((positiveEdge && !oldHigh && newHigh) || (!positiveEdge && oldHigh && !newHigh))
                    SetInterruptFlag(flag);
            }

            /// <summary>Sets one or more interrupt flags.</summary>
            /// <param name="flags">The interrupt flag mask to set.</param>
            private void SetInterruptFlag(byte flags)
            {
                ifr |= (byte)(flags & 0x7F);
            }

            /// <summary>Clears one or more interrupt flags.</summary>
            /// <param name="flags">The interrupt flag mask to clear.</param>
            private void ClearInterruptFlags(byte flags)
            {
                ifr &= (byte)~(flags & 0x7F);
            }

            /// <summary>Reads IFR with bit 7 reflecting enabled pending interrupts.</summary>
            /// <returns>The interrupt flags visible to the drive CPU.</returns>
            private byte ReadInterruptFlags()
            {
                byte pending = (byte)(ifr & 0x7F);
                return (pending & ier) != 0 ? (byte)(pending | IfrIrq) : pending;
            }

            /// <summary>Combines output-latch and external input bits according to the VIA data-direction register.</summary>
            /// <param name="port">The VIA output latch.</param>
            /// <param name="ddr">The VIA data-direction register.</param>
            /// <param name="input">The externally supplied input bits.</param>
            /// <returns>The port value visible to the drive CPU.</returns>
            private static byte ReadPort(byte port, byte ddr, byte input)
            {
                return (byte)((port & ddr) | (input & ~ddr));
            }
        }
    }
}
