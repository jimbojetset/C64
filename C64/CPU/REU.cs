// ============================================================================
// Project:     C64
// File:        REU.cs
// Description: CMD RAM Expansion Unit emulator with register handling, DMA
//              transfer modes, interrupt signaling, and wraparound memory
//              access.
// Author:      James Booth
// Created:     2025
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Commodore 64 and related ROMs are property of their respective
//              rights holders. This emulator is for educational purposes only.
// ============================================================================

using System;

namespace C64
{

    /// <summary>
    /// CMD REU (RAM Expansion Unit) emulator.
    /// Provides 128KB–512KB of additional RAM accessible via DMA transfers.
    ///
    /// Register map ($DF00–$DFFF):
    ///   $DF00: REU Address Low (bits 0–7)
    ///   $DF01: REU Address High (bits 8–15)
    ///   $DF02: REU Address Bank (bits 16–22, bank select)
    ///   $DF03: CPU Address Low (bits 0–7)
    ///   $DF04: CPU Address High (bits 8–15)
    ///   $DF05: Transfer Length Low (bytes 0–7)
    ///   $DF06: Transfer Length High (bytes 8–15)
    ///   $DF07: Command (bit 0=direction, bit 1=execute, bit 4=IRQ enable, bit 5=complete flag)
    ///   $DF08: Unused
    ///   $DF09: Interrupt Control Register
    ///
    /// DMA transfer is cycle-accurate: 1 byte transferred per 2 CPU cycles (approximate).
    /// </summary>
    internal sealed class REU : IDisposable
    {
        // REU RAM configurations: 128KB, 256KB, or 512KB
        private byte[] _reuRam;
        private int _reuSizeKb;

        // Control registers
        private int _reuAddrReg;      // 17-bit REU address (combines $DF00, $DF01, $DF02)
        private int _cpuAddrReg;      // 16-bit CPU address (combines $DF03, $DF04)
        private int _transferLen;     // 16-bit transfer length (combines $DF05, $DF06)
        private byte _cmdReg;         // $DF07: command and status
        private byte _irqReg;         // $DF09: interrupt control

        // DMA state machine
        private bool _dmaActive;
        private int _dmaBytesRemaining;
        private int _dmaDirection;    // 0 = to CPU, 1 = to REU
        private int _dmaBytesSinceLastCycle = 0;
        private bool _addressWrap;

        // Interrupt signaling

        /// <summary>Gets or sets the callback invoked for irq request.</summary>
        public Action? OnIrqRequest { get; set; }  // Called when REU needs to raise IRQ

        /// <summary>Initializes a new REU instance.</summary>
        /// <param name="sizeKb">The REU capacity in kilobytes.</param>
        public REU(int sizeKb = 128)
        {
            _reuSizeKb = sizeKb;
            _reuRam = new byte[sizeKb * 1024];
            Reset();
        }

        /// <summary>Resets this instance to its initial state.</summary>
        public void Reset()
        {
            Array.Clear(_reuRam, 0, _reuRam.Length);
            _reuAddrReg = 0;
            _cpuAddrReg = 0;
            _transferLen = 0;
            _cmdReg = 0;
            _irqReg = 0;
            _dmaActive = false;
            _dmaBytesRemaining = 0;
            _dmaBytesSinceLastCycle = 0;
            _addressWrap = false;
        }

        /// <summary>Reads a value from the addressed device state.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <returns>The byte value produced by the operation.</returns>
        public byte Read(int addr)
        {
            // REU control register reads
            switch (addr & 0xFF)
            {
                case 0x00: return (byte)(_reuAddrReg & 0xFF);
                case 0x01: return (byte)((_reuAddrReg >> 8) & 0xFF);
                case 0x02: return (byte)((_reuAddrReg >> 16) & 0x7F);
                case 0x03: return (byte)(_cpuAddrReg & 0xFF);
                case 0x04: return (byte)((_cpuAddrReg >> 8) & 0xFF);
                case 0x05: return (byte)(_transferLen & 0xFF);
                case 0x06: return (byte)((_transferLen >> 8) & 0xFF);
                case 0x07:
                    // Status register: return current state
                    byte status = _cmdReg;
                    if (_addressWrap) status |= 0x40;           // Address wrap flag
                    if (!_dmaActive && (_cmdReg & 0x20) != 0) status |= 0x20;  // Complete flag
                    return status;
                case 0x09: return _irqReg;
                default: return 0;
            }
        }

        /// <summary>Writes a value to the addressed device state.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <param name="value">The value supplied to the operation.</param>
        public void Write(int addr, byte value)
        {
            switch (addr & 0xFF)
            {
                case 0x00:
                    _reuAddrReg = (_reuAddrReg & 0xFF00) | value;
                    break;
                case 0x01:
                    _reuAddrReg = (_reuAddrReg & 0x00FF) | ((value & 0xFF) << 8);
                    break;
                case 0x02:
                    _reuAddrReg = (_reuAddrReg & 0xFFFF) | ((value & 0x7F) << 16);
                    break;
                case 0x03:
                    _cpuAddrReg = (_cpuAddrReg & 0xFF00) | value;
                    break;
                case 0x04:
                    _cpuAddrReg = (_cpuAddrReg & 0x00FF) | ((value & 0xFF) << 8);
                    break;
                case 0x05:
                    _transferLen = (_transferLen & 0xFF00) | value;
                    break;
                case 0x06:
                    _transferLen = (_transferLen & 0x00FF) | ((value & 0xFF) << 8);
                    break;
                case 0x07:
                    _cmdReg = (byte)(value & 0xF7);  // Bit 3 is reserved
                    if ((value & 0x02) != 0)  // Execute DMA
                    {
                        StartDmaTransfer();
                    }
                    break;
                case 0x09:
                    _irqReg = (byte)(value & 0xF0);
                    break;
            }
        }

        /// <summary>Starts an REU DMA transfer from the command register.</summary>
        private void StartDmaTransfer()
        {
            if (_transferLen == 0) _transferLen = 65536;  // 0 means 64KB transfer

            _dmaActive = true;
            _dmaBytesRemaining = _transferLen;
            _dmaDirection = (_cmdReg & 0x01);  // 0 = to CPU, 1 = to REU
            _dmaBytesSinceLastCycle = 0;
            _addressWrap = false;
            _cmdReg &= 0xDF;  // Clear complete flag
        }

        /// <summary>
        /// Step DMA transfer by a cycle count. Called from the main CPU cycle callback.
        /// Approximate rate: 1 byte per 2 CPU cycles (realistic for C64 REU).
        /// </summary>
        /// <param name="cycles">The number of emulated CPU cycles to advance.</param>
        /// <param name="memory">The CPU memory map used by the operation.</param>
        public void StepDma(int cycles, CPU.Memory memory)
        {
            if (!_dmaActive) return;

            _dmaBytesSinceLastCycle += cycles;

            // Transfer 1 byte per 2 cycles (roughly 500KB/s at 1MHz)
            while (_dmaBytesSinceLastCycle >= 2 && _dmaBytesRemaining > 0)
            {
                _dmaBytesSinceLastCycle -= 2;

                if (_dmaDirection == 0)  // To CPU RAM
                {
                    byte data = ReadReuByte(_reuAddrReg);
                    memory.WriteByte((ulong)_cpuAddrReg, data);
                }
                else  // To REU RAM
                {
                    byte data = memory.ReadByte((ulong)_cpuAddrReg);
                    WriteReuByte(_reuAddrReg, data);
                }

                _reuAddrReg++;
                if (_reuAddrReg >= (_reuSizeKb * 1024))
                {
                    _reuAddrReg = 0;
                    _addressWrap = true;
                }

                _cpuAddrReg++;
                if (_cpuAddrReg > 0xFFFF)
                {
                    _cpuAddrReg = 0;
                }

                _dmaBytesRemaining--;

                if (_dmaBytesRemaining == 0)
                {
                    _dmaActive = false;
                    _cmdReg |= 0x20;  // Set complete flag

                    // Signal IRQ if enabled
                    if ((_irqReg & 0x80) != 0)
                    {
                        OnIrqRequest?.Invoke();
                    }
                }
            }
        }

        /// <summary>Reads reu byte.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <returns>The byte value produced by the operation.</returns>
        private byte ReadReuByte(int addr)
        {
            addr &= ((_reuSizeKb * 1024) - 1);
            return _reuRam[addr];
        }

        /// <summary>Writes reu byte.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <param name="value">The value supplied to the operation.</param>
        private void WriteReuByte(int addr, byte value)
        {
            addr &= ((_reuSizeKb * 1024) - 1);
            _reuRam[addr] = value;
        }

        /// <summary>Releases resources owned by this instance.</summary>
        public void Dispose()
        {
            // No unmanaged resources
        }
    }
}
