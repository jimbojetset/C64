

namespace C64.CPU
{

    internal sealed class REU : IDisposable
    {

        private readonly byte[] _reuRam;

        private readonly int _reuSizeKb;
        private readonly int _reuSizeMask;
        private readonly int _reuBankMask;
        private readonly byte _reuBankReadMask;

        private int _reuAddr;
        private int _cpuAddr;
        private int _transferLen;
        private byte _commandReg;
        private byte _imrReg;
        private byte _acrReg;

        private int _reuAddrShadow;
        private int _cpuAddrShadow;
        private int _transferLenShadow;

        private bool _irqPending;
        private bool _endOfBlock;
        private bool _fault;

        private int _dmaType;

        private int _pendingStallCycles;

        private bool _ff00Armed;

        public Action? OnIrqRequest { get; set; }

        public Action? OnExecuteNow { get; set; }

        /// <summary>Allocates REU expansion RAM at the requested size and resets its DMA registers.</summary>
        /// <param name="sizeKb">The REU capacity in kilobytes (128, 256, or 512).</param>
        public REU(int sizeKb = 128)
        {
            if (sizeKb != 128 && sizeKb != 256 && sizeKb != 512)
                sizeKb = 128;

            _reuSizeKb = sizeKb;
            _reuRam = new byte[sizeKb * 1024];
            _reuSizeMask = (sizeKb * 1024) - 1;

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

        /// <summary>Clears REU RAM, DMA registers, address shadows, status latches, and pending execution state.</summary>
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
            _pendingStallCycles = 0;
            _ff00Armed = false;
        }

        /// <summary>Reads an REU register and applies status-read side effects such as clearing IRQ and completion latches.</summary>
        /// <param name="addr">The emulated address to access.</param>
        public byte Read(int addr)
        {
            switch (addr & 0x1F)
            {
                case 0x00:
                    {

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

                    return (byte)(_commandReg | 0x4C);

                case 0x02: return (byte)(_cpuAddr & 0xFF);
                case 0x03: return (byte)((_cpuAddr >> 8) & 0xFF);
                case 0x04: return (byte)(_reuAddr & 0xFF);
                case 0x05: return (byte)((_reuAddr >> 8) & 0xFF);
                case 0x06: return (byte)(_reuBankReadMask | ((_reuAddr >> 16) & _reuBankMask));
                case 0x07: return (byte)(_transferLen & 0xFF);
                case 0x08: return (byte)((_transferLen >> 8) & 0xFF);

                case 0x09:

                    return (byte)(_imrReg | 0x1F);

                case 0x0A:

                    return (byte)(_acrReg | 0x3F);

                default:

                    return 0xFF;
            }
        }

        /// <summary>Writes an REU register and arms or immediately starts DMA according to the command bits.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <param name="value">The value supplied to the operation.</param>
        public void Write(int addr, byte value)
        {
            switch (addr & 0x1F)
            {
                case 0x00:

                    return;

                case 0x01:
                    _commandReg = value;
                    if ((value & 0x80) != 0)
                    {

                        if ((value & 0x10) != 0)
                        {
                            _ff00Armed = false;
                            OnExecuteNow?.Invoke();
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
        /// Called from the memory subsystem when the CPU writes to $FF00 with
        /// a real memory reference so the DMA can be executed in-place.
        /// </summary>
        /// <param name="memory">The CPU memory map used by the operation.</param>
        public void NotifyFF00Write(CPU.C64MemoryBus memory)
        {
            if (!_ff00Armed) return;
            _ff00Armed = false;
            RunDmaTransfer(memory);
        }

        /// <summary>
        /// Runs a DMA transfer to completion immediately and accumulates the
        /// CPU-stall cycle cost in <see cref="ConsumePendingStallCycles"/>.
        /// On real hardware the 8726 halts the 6510 while it transfers, so
        /// the CPU loses these cycles to the REU bus master.
        /// </summary>
        /// <param name="memory">The CPU memory map used by the operation.</param>
        public void RunDmaTransfer(CPU.C64MemoryBus memory)
        {
            _reuAddrShadow = _reuAddr;
            _cpuAddrShadow = _cpuAddr;
            _transferLenShadow = _transferLen;

            int len = _transferLen == 0 ? 0x10000 : _transferLen;
            _dmaType = _commandReg & 0x03;
            _endOfBlock = false;
            _fault = false;

            bool fixCpu = (_acrReg & 0x80) != 0;
            bool fixReu = (_acrReg & 0x40) != 0;

            int bytesTransferred = 0;

            for (int i = 0; i < len; i++)
            {
                switch (_dmaType)
                {
                    case 0:
                        WriteReuByte(_reuAddr, memory.ReadByte((ulong)_cpuAddr));
                        break;
                    case 1:
                        memory.WriteByte((ulong)_cpuAddr, ReadReuByte(_reuAddr));
                        break;
                    case 2:
                        {
                            byte cpuByte = memory.ReadByte((ulong)_cpuAddr);
                            byte reuByte = ReadReuByte(_reuAddr);
                            memory.WriteByte((ulong)_cpuAddr, reuByte);
                            WriteReuByte(_reuAddr, cpuByte);
                            break;
                        }
                    case 3:
                        {
                            byte cpuByte = memory.ReadByte((ulong)_cpuAddr);
                            byte reuByte = ReadReuByte(_reuAddr);
                            if (cpuByte != reuByte)
                            {
                                _fault = true;
                                bytesTransferred = i + 1;
                                if (!fixReu) _reuAddr = (_reuAddr + 1) & 0xFFFFFF;
                                if (!fixCpu) _cpuAddr = (_cpuAddr + 1) & 0xFFFF;
                                goto CompareEarlyExit;
                            }
                            break;
                        }
                }

                if (!fixReu) _reuAddr = (_reuAddr + 1) & 0xFFFFFF;
                if (!fixCpu) _cpuAddr = (_cpuAddr + 1) & 0xFFFF;
                bytesTransferred = i + 1;
            }

        CompareEarlyExit:
            _endOfBlock = true;

            if ((_commandReg & 0x20) == 0)
            {
                _reuAddr = _reuAddrShadow;
                _cpuAddr = _cpuAddrShadow;
                _transferLen = _transferLenShadow;
            }

            _commandReg &= 0x6F;

            int perByte = (_dmaType == 2) ? 2 : 1;
            _pendingStallCycles += 4 + (bytesTransferred * perByte);

            bool irq = false;
            if (_endOfBlock && (_imrReg & 0x40) != 0) irq = true;
            if (_fault && (_imrReg & 0x20) != 0) irq = true;
            if (irq && (_imrReg & 0x80) != 0)
            {
                _irqPending = true;
                UpdateIrqLine();
            }
        }

        /// <summary>
        /// Returns and clears any CPU stall cycles owed to the host from the
        /// most recent DMA burst. The host should bill these to the CPU via
        /// its external-stall API and step the other peripherals by the same
        /// count so their timing stays aligned.
        /// </summary>
        public int ConsumePendingStallCycles()
        {
            int cycles = _pendingStallCycles;
            _pendingStallCycles = 0;
            return cycles;
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

        /// <summary>Releases the synchronization object used by REU DMA transfers.</summary>
        public void Dispose()
        {

        }
    }
}
