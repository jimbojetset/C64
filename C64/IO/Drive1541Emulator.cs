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

        private readonly CPU_6510 cpu = new CPU_6510(DriveClockHz);
        private readonly Via6522 via1 = new Via6522();
        private readonly Via6522 via2 = new Via6522();
        private readonly byte[] ram = new byte[RamSize];
        private readonly byte[] rom;
        private D64Image? diskImage;
        private int cycleDebt;
        private bool hostDataRelease = true;
        private bool hostClockRelease = true;
        private bool hostAtnRelease = true;

        /// <summary>Initializes a new Drive1541Emulator instance.</summary>
        /// <param name="rom">The 1541 DOS ROM bytes, normally 16 KiB mapped at $C000-$FFFF.</param>
        private Drive1541Emulator(byte[] rom)
        {
            this.rom = NormalizeRom(rom);
            cpu.memory.OnMemoryRead = ReadMemory;
            cpu.memory.OnMemoryWrite = WriteMemory;
            Reset();
        }

        /// <summary>Attempts to create a 1541 emulator using the first available ROM path.</summary>
        /// <param name="romPaths">Candidate filesystem paths for the 1541 DOS ROM.</param>
        /// <returns>A drive emulator when a ROM is found; otherwise, null.</returns>
        public static Drive1541Emulator? TryCreate(params string[] romPaths)
        {
            foreach (string path in romPaths)
            {
                if (!File.Exists(path))
                    continue;

                byte[] rom = File.ReadAllBytes(path);
                if (rom.Length == 0x2000 || rom.Length == 0x4000)
                    return new Drive1541Emulator(rom);
            }

            return null;
        }

        /// <summary>Resets the drive CPU, RAM, VIA registers, and IEC output lines.</summary>
        public void Reset()
        {
            Array.Clear(ram);
            via1.Reset();
            via2.Reset();
            cpu.memory.memory[0xFFFC] = ReadMemory(0xFFFC, 0);
            cpu.memory.memory[0xFFFD] = ReadMemory(0xFFFD, 0);
            cpu.ResetNow();
        }

        /// <summary>Attaches a D64 image path to the drive emulation context.</summary>
        /// <param name="path">The path of the attached disk image.</param>
        public void AttachD64(string path)
        {
            diskImage = D64Image.Load(path);
        }

        /// <summary>Ejects the currently attached disk image from the drive emulation context.</summary>
        public void Eject()
        {
            diskImage = null;
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
        }

        /// <summary>Gets whether the emulated drive releases the IEC DATA line.</summary>
        public bool DeviceDataRelease => via1.DeviceDataRelease;

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
                int elapsed = cpu.StepInstruction();
                if (elapsed <= 0)
                    break;

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
                return via1.Read((byte)(a & 0x0F), hostDataRelease, hostClockRelease, hostAtnRelease);

            if (IsViaAddress(a, Via2Base))
                return via2.Read((byte)(a & 0x0F), true, true, true);

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
                return true;
            }

            if (IsViaAddress(a, Via1Base))
            {
                via1.Write((byte)(a & 0x0F), value);
                return true;
            }

            if (IsViaAddress(a, Via2Base))
            {
                via2.Write((byte)(a & 0x0F), value);
                return true;
            }

            return a >= RomBase;
        }

        /// <summary>Determines whether an address selects a mirrored 6522 VIA register block.</summary>
        /// <param name="addr">The 1541 CPU address.</param>
        /// <param name="baseAddress">The base address of the VIA block.</param>
        /// <returns>True when the address is within the VIA block; otherwise, false.</returns>
        private static bool IsViaAddress(ushort addr, int baseAddress)
        {
            return (addr & 0xFC00) == baseAddress;
        }

        /// <summary>Normalizes supported 1541 ROM dump sizes to a 16 KiB image mapped at $C000-$FFFF.</summary>
        /// <param name="source">The ROM bytes read from disk.</param>
        /// <returns>A 16 KiB ROM image.</returns>
        private static byte[] NormalizeRom(byte[] source)
        {
            byte[] normalized = new byte[0x4000];
            if (source.Length == normalized.Length)
            {
                Array.Copy(source, normalized, normalized.Length);
                return normalized;
            }

            Array.Copy(source, 0, normalized, 0x2000, source.Length);
            Array.Copy(source, 0, normalized, 0, source.Length);
            return normalized;
        }

        /// <summary>
        /// Minimal 6522 VIA register model used to connect the 1541 ROM to IEC lines.
        /// Timers, IRQs, shift-register behavior, and disk mechanics are intentionally left as follow-on work.
        /// </summary>
        private sealed class Via6522
        {
            private byte portB = 0xFF;
            private byte portA = 0xFF;
            private byte ddrB;
            private byte ddrA;

            /// <summary>Gets whether this VIA releases the IEC DATA line.</summary>
            public bool DeviceDataRelease => (ddrB & 0x02) == 0 || (portB & 0x02) != 0;

            /// <summary>Gets whether this VIA releases the IEC CLOCK line.</summary>
            public bool DeviceClockRelease => (ddrB & 0x08) == 0 || (portB & 0x08) != 0;

            /// <summary>Resets VIA registers to their power-on defaults.</summary>
            public void Reset()
            {
                portB = 0xFF;
                portA = 0xFF;
                ddrB = 0x00;
                ddrA = 0x00;
            }

            /// <summary>Reads a VIA register, merging IEC input lines into port B.</summary>
            /// <param name="register">The low register index.</param>
            /// <param name="hostDataRelease">Whether the C64 has released DATA.</param>
            /// <param name="hostClockRelease">Whether the C64 has released CLOCK.</param>
            /// <param name="hostAtnRelease">Whether the C64 has released ATN.</param>
            /// <returns>The register value visible to the drive CPU.</returns>
            public byte Read(byte register, bool hostDataRelease, bool hostClockRelease, bool hostAtnRelease)
            {
                return (register & 0x0F) switch
                {
                    0x00 => ReadPortB(hostDataRelease, hostClockRelease, hostAtnRelease),
                    0x01 => ReadPort(portA, ddrA, 0xFF),
                    0x02 => ddrB,
                    0x03 => ddrA,
                    _ => 0x00
                };
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
                        break;
                    case 0x01:
                        portA = value;
                        break;
                    case 0x02:
                        ddrB = value;
                        break;
                    case 0x03:
                        ddrA = value;
                        break;
                }
            }

            /// <summary>Reads port B with a conservative IEC serial-port mapping.</summary>
            /// <param name="hostDataRelease">Whether the C64 has released DATA.</param>
            /// <param name="hostClockRelease">Whether the C64 has released CLOCK.</param>
            /// <param name="hostAtnRelease">Whether the C64 has released ATN.</param>
            /// <returns>The merged port B value.</returns>
            private byte ReadPortB(bool hostDataRelease, bool hostClockRelease, bool hostAtnRelease)
            {
                byte input = 0xFF;
                if (!hostDataRelease)
                    input &= 0xFE;
                if (!hostClockRelease)
                    input &= 0xFB;
                if (!hostAtnRelease)
                    input &= 0xEF;

                return ReadPort(portB, ddrB, input);
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
