// ============================================================================
// Project:     C64
// File:        REU.cs
// Description: Commodore 17xx RAM Expansion Unit emulator. Implements the
//              8726 DMA controller register map, all four transfer modes
//              (stash / fetch / swap / compare), $FF00 trigger, autoload,
//              address fixing, level-triggered IRQ, and size-aware bank
//              register read masking.
// Author:      James Booth
// Created:     2025
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Commodore 64 and related ROMs are property of their respective
//              rights holders. This emulator is for educational purposes only.
// ============================================================================

namespace C64
{
    /// <summary>
    /// Commodore 17xx-series RAM Expansion Unit (REU) emulator.
    /// Provides 128KB / 256KB / 512KB of additional RAM accessible via the
    /// 8726 DMA controller mapped at $DF00-$DF0A.
    ///
    /// Register map (CMD/Commodore-correct, used by VICE and real software):
    ///   $DF00: Status            (R)    bit7 IRQ pending, bit6 end-of-block,
    ///                                   bit5 fault (verify error), bit4 size id,
    ///                                   bits3-0 chip version; READ clears
    ///                                   pending IRQ and bits 7-5.
    ///   $DF01: Command           (R/W)  bit7 EXECUTE, bit6 reserved (=1),
    ///                                   bit5 AUTOLOAD (0 = reload regs on
    ///                                   completion), bit4 FF00 trigger
    ///                                   (1 = run now, 0 = wait for $FF00
    ///                                   write), bits3-2 reserved (=1),
    ///                                   bits1-0 TYPE (00 stash, 01 fetch,
    ///                                   10 swap, 11 compare).
    ///   $DF02: C64 address low
    ///   $DF03: C64 address high
    ///   $DF04: REU address low
    ///   $DF05: REU address high
    ///   $DF06: REU address bank   (bits according to size; unused bits read 1)
    ///   $DF07: Transfer length low
    ///   $DF08: Transfer length high
    ///   $DF09: Interrupt mask register (IMR) - bit7 master enable,
    ///                                   bit6 end-of-block IRQ, bit5 fault IRQ;
    ///                                   bits 0-4 read as 1.
    ///   $DF0A: Address control register (ACR) - bit7 fix C64 address,
    ///                                   bit6 fix REU address; bits 0-5 read 1.
    ///
    /// DMA transfer is paced from the per-cycle CPU stepper at the historical
    /// 1 byte per 2 cycles. CPU is not halted during DMA in this build.
    /// </summary>
    internal sealed class REU : IDisposable
    {
        /// REU RAM (128KB, 256KB, or 512KB).
        private readonly byte[] _reuRam;

        private readonly int _reuSizeKb;
        private readonly int _reuSizeMask;
        private readonly int _reuBankMask;
        private readonly byte _reuBankReadMask;

        /// Live (working) registers.
        private int _reuAddr;        /// 24-bit REU address (low/high/bank)
        private int _cpuAddr;        /// 16-bit CPU address (low/high)
        private int _transferLen;    /// 16-bit transfer length (low/high)
        private byte _commandReg;    /// $DF01 command register, last value written
        private byte _imrReg;        /// $DF09 IMR (top 3 bits significant)
        private byte _acrReg;        /// $DF0A ACR (top 2 bits significant)

        /// Shadow (start) registers, captured on EXECUTE so AUTOLOAD=0 can
        /// restore them when the transfer completes.
        private int _reuAddrShadow;
        private int _cpuAddrShadow;
        private int _transferLenShadow;

        /// Status latches that surface in $DF00.
        private bool _irqPending;
        private bool _endOfBlock;
        private bool _fault;

        /// DMA state machine.
        private bool _dmaActive;
        private int _dmaBytesRemaining;
        private int _dmaType;        /// 0 stash, 1 fetch, 2 swap, 3 compare
        private int _dmaCycleAccumulator;
        private bool _ff00Armed;     /// EXECUTE pending FF00 trigger

        /// <summary>Gets or sets the callback invoked when an REU IRQ is asserted.</summary>
        public Action? OnIrqRequest { get; set; }

        /// <summary>Initializes a new REU instance.</summary>
        /// <param name="sizeKb">The REU capacity in kilobytes (128, 256, or 512).</param>
        public REU(int sizeKb = 128)
        {
            if (sizeKb != 128 && sizeKb != 256 && sizeKb != 512)
                sizeKb = 128;

            _reuSizeKb = sizeKb;
            _reuRam = new byte[sizeKb * 1024];
            _reuSizeMask = (sizeKb * 1024) - 1;

            /// Bank-register mask: 128K -> 1 bit, 256K -> 2 bits, 512K -> 3 bits.
            /// Unused upper bits read back as 1 (open-bus high) so detection
            /// routines see e.g. $F8 on a 1700 and $F0 on a 1750.
            int bankBits = sizeKb switch
            {
                128 => 1,
                256 => 2,
                _ => 3,
            };
            _reuBankMask = (1 << bankBits) - 1;
            _reuBankReadMask = (byte)~_reuBankMask;
            Reset();
        }

        /// <summary>Resets this instance to its initial state.</summary>
        public void Reset()
        {
            Array.Clear(_reuRam, 0, _reuRam.Length);
            _reuAddr = 0;
            _cpuAddr = 0;
            _transferLen = 0xFFFF;
            _commandReg = 0;
            _imrReg = 0;
            _acrReg = 0;
            _reuAddrShadow = 0;
            _cpuAddrShadow = 0;
            _transferLenShadow = 0;
            _irqPending = false;
            _endOfBlock = false;
            _fault = false;
            _dmaActive = false;
            _dmaBytesRemaining = 0;
            _dmaCycleAccumulator = 0;
            _ff00Armed = false;
        }

        /// <summary>Reads a value from the addressed device state.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <returns>The byte value produced by the operation.</returns>
        public byte Read(int addr)
        {
            switch (addr & 0x1F)
            {
                case 0x00:
                    {
                        /// Status: bit7 IRQ pending, bit6 end-of-block, bit5 fault,
                        /// bit4 size id (1 = 256K/512K, 0 = 128K), bits3-0 chip
                        /// version (0 == 8726). Reading clears bits 7-5 and the
                        /// pending IRQ.
                        byte status = 0x00;
                        if (_reuSizeKb >= 256) status |= 0x10;
                        if (_irqPending) status |= 0x80;
                        if (_endOfBlock) status |= 0x40;
                        if (_fault) status |= 0x20;

                        _irqPending = false;
                        _endOfBlock = false;
                        _fault = false;
                        return status;
                    }

                case 0x01:
                    /// Command register: unused/reserved bits read as 1.
                    return (byte)(_commandReg | 0x4C);

                case 0x02: return (byte)(_cpuAddr & 0xFF);
                case 0x03: return (byte)((_cpuAddr >> 8) & 0xFF);
                case 0x04: return (byte)(_reuAddr & 0xFF);
                case 0x05: return (byte)((_reuAddr >> 8) & 0xFF);
                case 0x06: return (byte)(_reuBankReadMask | ((_reuAddr >> 16) & _reuBankMask));
                case 0x07: return (byte)(_transferLen & 0xFF);
                case 0x08: return (byte)((_transferLen >> 8) & 0xFF);

                case 0x09:
                    /// IMR: top 3 bits significant, low 5 bits read 1.
                    return (byte)(_imrReg | 0x1F);

                case 0x0A:
                    /// ACR: top 2 bits significant, low 6 bits read 1.
                    return (byte)(_acrReg | 0x3F);

                default:
                    /// Mirrored / unused: open bus reads $FF.
                    return 0xFF;
            }
        }

        /// <summary>Writes a value to the addressed device state.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <param name="value">The value supplied to the operation.</param>
        public void Write(int addr, byte value)
        {
            switch (addr & 0x1F)
            {
                case 0x00:
                    /// Status register is read-only on real hardware.
                    return;

                case 0x01:
                    _commandReg = value;
                    if ((value & 0x80) != 0)
                    {
                        /// EXECUTE requested. FF00-trigger bit decides whether
                        /// to run immediately or arm for the next $FF00 write.
                        if ((value & 0x10) != 0)
                        {
                            _ff00Armed = false;
                            StartDmaTransfer();
                        }
                        else
                        {
                            _ff00Armed = true;
                        }
                    }
                    return;

                case 0x02:
                    _cpuAddr = (_cpuAddr & 0xFF00) | value;
                    return;

                case 0x03:
                    _cpuAddr = (_cpuAddr & 0x00FF) | (value << 8);
                    return;

                case 0x04:
                    _reuAddr = (_reuAddr & ~0xFF) | value;
                    return;

                case 0x05:
                    _reuAddr = (_reuAddr & ~0xFF00) | (value << 8);
                    return;

                case 0x06:
                    {
                        int bank = (value & _reuBankMask) << 16;
                        _reuAddr = (_reuAddr & 0xFFFF) | bank;
                        return;
                    }

                case 0x07:
                    _transferLen = (_transferLen & 0xFF00) | value;
                    return;

                case 0x08:
                    _transferLen = (_transferLen & 0x00FF) | (value << 8);
                    return;

                case 0x09:
                    _imrReg = (byte)(value & 0xE0);
                    UpdateIrqLine();
                    return;

                case 0x0A:
                    _acrReg = (byte)(value & 0xC0);
                    return;
            }
        }

        /// <summary>
        /// Called from the memory subsystem when the CPU writes to $FF00.
        /// If the REU has been armed for an FF00-triggered DMA, the transfer
        /// starts now.
        /// </summary>
        public void NotifyFF00Write()
        {
            if (!_ff00Armed) return;
            _ff00Armed = false;
            StartDmaTransfer();
        }

        /// <summary>Captures shadow registers and starts a DMA transfer.</summary>
        private void StartDmaTransfer()
        {
            _reuAddrShadow = _reuAddr;
            _cpuAddrShadow = _cpuAddr;
            _transferLenShadow = _transferLen;

            /// Length register of 0 means 65536 bytes (one full bank).
            int len = _transferLen == 0 ? 0x10000 : _transferLen;

            _dmaActive = true;
            _dmaBytesRemaining = len;
            _dmaType = _commandReg & 0x03;
            _dmaCycleAccumulator = 0;
            _endOfBlock = false;
            _fault = false;
        }

        /// <summary>
        /// Step DMA transfer by a cycle count. Called from the main CPU cycle
        /// stepper. Approximate rate: 1 byte per 2 CPU cycles.
        /// </summary>
        /// <param name="cycles">The number of emulated CPU cycles to advance.</param>
        /// <param name="memory">The CPU memory map used by the operation.</param>
        public void StepDma(int cycles, CPU.Memory memory)
        {
            if (!_dmaActive) return;

            _dmaCycleAccumulator += cycles;

            bool fixCpu = (_acrReg & 0x80) != 0;
            bool fixReu = (_acrReg & 0x40) != 0;

            while (_dmaCycleAccumulator >= 2 && _dmaBytesRemaining > 0 && _dmaActive)
            {
                _dmaCycleAccumulator -= 2;

                switch (_dmaType)
                {
                    case 0: /// Stash: C64 -> REU
                        {
                            byte data = memory.ReadByte((ulong)_cpuAddr);
                            WriteReuByte(_reuAddr, data);
                            break;
                        }
                    case 1: /// Fetch: REU -> C64
                        {
                            byte data = ReadReuByte(_reuAddr);
                            memory.WriteByte((ulong)_cpuAddr, data);
                            break;
                        }
                    case 2: /// Swap: C64 <-> REU
                        {
                            byte cpuByte = memory.ReadByte((ulong)_cpuAddr);
                            byte reuByte = ReadReuByte(_reuAddr);
                            memory.WriteByte((ulong)_cpuAddr, reuByte);
                            WriteReuByte(_reuAddr, cpuByte);
                            break;
                        }
                    case 3: /// Compare: stops on first mismatch and sets FAULT.
                        {
                            byte cpuByte = memory.ReadByte((ulong)_cpuAddr);
                            byte reuByte = ReadReuByte(_reuAddr);
                            if (cpuByte != reuByte)
                            {
                                _fault = true;
                                _dmaBytesRemaining = 1; /// fall through to
                                                        /// completion below
                            }
                            break;
                        }
                }

                if (!fixReu)
                    _reuAddr = (_reuAddr + 1) & 0xFFFFFF;

                if (!fixCpu)
                    _cpuAddr = (_cpuAddr + 1) & 0xFFFF;

                _dmaBytesRemaining--;

                if (_dmaBytesRemaining == 0)
                {
                    _dmaActive = false;
                    _endOfBlock = true;

                    /// AUTOLOAD: when bit 5 is clear, restore the start
                    /// values so the next EXECUTE / FF00 repeats the same
                    /// transfer. When set, leave the registers showing the
                    /// final post-DMA values.
                    if ((_commandReg & 0x20) == 0)
                    {
                        _reuAddr = _reuAddrShadow;
                        _cpuAddr = _cpuAddrShadow;
                        _transferLen = _transferLenShadow;
                    }

                    /// Clear the EXECUTE bit; FF00-trigger bit is also
                    /// cleared so the next write to $FF00 won't re-arm.
                    _commandReg &= 0x6F;

                    /// IRQ assertion: end-of-block always latches; fault
                    /// latches if compare mismatched. Master + per-source
                    /// enables in IMR gate the actual line.
                    bool irq = false;
                    if (_endOfBlock && (_imrReg & 0x40) != 0) irq = true;
                    if (_fault && (_imrReg & 0x20) != 0) irq = true;
                    if (irq && (_imrReg & 0x80) != 0)
                    {
                        _irqPending = true;
                        UpdateIrqLine();
                    }
                }
            }
        }

        /// <summary>Fires the IRQ callback while a pending IRQ is latched.</summary>
        private void UpdateIrqLine()
        {
            if (_irqPending && (_imrReg & 0x80) != 0)
                OnIrqRequest?.Invoke();
        }

        /// <summary>Reads a single REU RAM byte with size-mask wrapping.</summary>
        private byte ReadReuByte(int addr) => _reuRam[addr & _reuSizeMask];

        /// <summary>Writes a single REU RAM byte with size-mask wrapping.</summary>
        private void WriteReuByte(int addr, byte value) => _reuRam[addr & _reuSizeMask] = value;

        /// <summary>Releases resources owned by this instance.</summary>
        public void Dispose()
        {
            /// No unmanaged resources.
        }
    }
}
