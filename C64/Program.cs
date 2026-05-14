using _6502CPU;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static SDL2.SDL;

namespace C64
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            NativeLibrary.SetDllImportResolver(typeof(SDL2.SDL).Assembly, ResolveNativeLibrary);

            string? loadPath = null;

            foreach (string arg in args)
            {
                if (loadPath is null && File.Exists(arg))
                    loadPath = arg;
            }

            try
            {
                using var emu = new C64Emulator();
                if (loadPath is not null)
                    emu.QueueLoadAndRun(loadPath);
                emu.Run();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Fatal: {ex}");
                return 1;
            }
        }

        private static IntPtr ResolveNativeLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName != "SDL2") return IntPtr.Zero;

            string[] candidates;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                candidates = new[]
                {
                    "/opt/homebrew/lib/libSDL2.dylib",      // Apple Silicon Homebrew
                    "/opt/homebrew/opt/sdl2/lib/libSDL2.dylib",
                    "/usr/local/lib/libSDL2.dylib",         // Intel Homebrew / manual install
                    "/usr/local/opt/sdl2/lib/libSDL2.dylib",
                    "libSDL2.dylib",
                };
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                candidates = new[]
                {
                    "libSDL2-2.0.so.0",
                    "libSDL2.so",
                };
            }
            else
            {
                candidates = new[] { "SDL2.dll" };
            }

            foreach (string c in candidates)
            {
                if (NativeLibrary.TryLoad(c, out IntPtr handle))
                    return handle;
            }
            return IntPtr.Zero;
        }
    }

    internal sealed class C64Emulator : IDisposable
    {
        private readonly _6502_CPU cpu;
        private const int Clock_PAL = 985_248;   // 6510 @ PAL
        private const int Clock_NTSC = 1_022_727; // 6510 @ NTSC

        private const int CiaTickHz = 1000;
        private static readonly bool VerboseIoTrace = false;
        private static readonly bool TraceVicStates =
            string.Equals(Environment.GetEnvironmentVariable("C64_TRACE_VIC"), "1", StringComparison.Ordinal);

        private readonly Display display;
        private readonly Keyboard keyboard;
        private readonly Sound sound;

        private readonly object cia1Lock = new object();
        private ushort cia1TimerALatch = 0xFFFF;
        private ushort cia1TimerACounter = 0xFFFF;
        private ushort cia1TimerBLatch = 0xFFFF;
        private ushort cia1TimerBCounter = 0xFFFF;
        private byte cia1Cra;
        private byte cia1Crb;
        private byte cia1IcrMask;
        private byte cia1IcrStatus;

        private bool cia1CntHigh = true;

        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private Thread? cpuThread;
        private Thread? irqThread;

        private byte cia1PortA = 0xFF;
        private byte cia1PortB = 0xFF;
        private byte cia1Ddra = 0x00;
        private byte cia1Ddrb = 0x00;

        private readonly object cia2Lock = new object();
        private byte cia2PortA = 0x17;
        private byte cia2Ddra = 0x3F;
        private ushort cia2TimerALatch = 0xFFFF;
        private ushort cia2TimerACounter = 0xFFFF;
        private ushort cia2TimerBLatch = 0xFFFF;
        private ushort cia2TimerBCounter = 0xFFFF;
        private byte cia2Cra;
        private byte cia2Crb;

        private readonly ConcurrentQueue<(string Path, bool AutoRun)> pendingLoads
            = new ConcurrentQueue<(string Path, bool AutoRun)>();

        public C64Emulator()
        {
            cpu = new _6502_CPU(Clock_PAL);
            cpu.memory.LoadBankedROM(Path.Combine("ROMS", "basic.901226-01.bin"), Memory.BankSlot.Basic);
            cpu.memory.LoadBankedROM(Path.Combine("ROMS", "kernal.901227-03.bin"), Memory.BankSlot.Kernal);
            cpu.memory.LoadBankedROM(Path.Combine("ROMS", "characters.901225-01.bin"), Memory.BankSlot.Char);
            display = new Display(cpu);
            keyboard = new Keyboard(cpu);
            sound = new Sound();
            keyboard.OnHardReset = HardReset;
            keyboard.OnLoad = LoadProgram;
            keyboard.OnSave = SaveProgram;
            keyboard.OnDump = () => Console.Error.WriteLine(BuildDebugStateLine("[DUMP]"));

            byte[] kernal = cpu.memory.GetBankedROM(Memory.BankSlot.Kernal)!;
            kernal[0xFCF5 - 0xE000] = 0xEA;
            kernal[0xFCF6 - 0xE000] = 0xEA;
            kernal[0xFCF7 - 0xE000] = 0xEA;

            InitHardware();

            cpu.OnReset = InitHardware;

            display.RasterCompare = 0;

            cpu.memory.OnIOWrite = OnIOWrite;
            cpu.memory.OnIORead = OnIORead;
            cpu.memory.OnIOPostRead = OnIOPostRead;
        }

        private void InitHardware()
        {
            byte[] m = cpu.memory.memory;

            Array.Clear(m, 0x0000, m.Length);
            cpu.memory.ClearIoUnderRam();

            m[0x0000] = 0x2F;
            m[0x0001] = 0x37;

            cia1PortA = 0xFF;
            cia1PortB = 0xFF;
            cia1Ddra = 0x00;
            cia1Ddrb = 0x00;
            m[0xDC00] = cia1PortA;
            m[0xDC01] = cia1PortB;
            m[0xDC02] = cia1Ddra;
            m[0xDC03] = cia1Ddrb;

            lock (cia1Lock)
            {
                cia1TimerALatch = 0xFFFF;
                cia1TimerACounter = 0xFFFF;
                cia1TimerBLatch = 0xFFFF;
                cia1TimerBCounter = 0xFFFF;
                cia1Cra = 0x00;
                cia1Crb = 0x00;
                cia1IcrMask = 0x00;
                cia1IcrStatus = 0x00;
                cia1CntHigh = true;
            }
            m[0xDC04] = 0xFF;
            m[0xDC05] = 0xFF;
            m[0xDC06] = 0xFF;
            m[0xDC07] = 0xFF;
            m[0xDC0D] = 0x00;
            m[0xDC0E] = 0x00;
            m[0xDC0F] = 0x00;

            m[0x0281] = 0x00; m[0x0282] = 0x08; // MEMSTR = $0800
            m[0x0283] = 0x00; m[0x0284] = 0xA0; // MEMSIZ = $A000
            m[0x0288] = 0x04;                   // screen page = $0400

            m[0xDC00] = 0xFF;
            m[0xDC01] = 0xFF;

            cia2PortA = 0x17;
            cia2Ddra = 0x3F;
            m[0xDD00] = 0x17;
            m[0xDD02] = 0x3F;
            lock (cia2Lock)
            {
                cia2TimerALatch = 0xFFFF;
                cia2TimerACounter = 0xFFFF;
                cia2TimerBLatch = 0xFFFF;
                cia2TimerBCounter = 0xFFFF;
                cia2Cra = 0x00;
                cia2Crb = 0x00;
            }
            m[0xDD04] = 0xFF;
            m[0xDD05] = 0xFF;
            m[0xDD06] = 0xFF;
            m[0xDD07] = 0xFF;
            m[0xDD0E] = 0x00;
            m[0xDD0F] = 0x00;

            m[0xD011] = 0x1B; // DEN, RSEL, YSCROLL=3
            m[0xD016] = 0xC8; // (top bits), CSEL, XSCROLL=0
            m[0xD018] = 0x14; // screen $0400, char ROM shadow $1000
            m[0xD020] = 0x0E; // border  = light blue
            m[0xD021] = 0x06; // bg 0    = blue
            m[0xD022] = 0x01; // bg 1    = white
            m[0xD023] = 0x02; // bg 2    = red
            m[0xD024] = 0x03; // bg 3    = cyan

            m[0xD015] = 0x00;
            m[0xD017] = 0x00;
            m[0xD01B] = 0x00;
            m[0xD01C] = 0x00;
            m[0xD01D] = 0x00;
            m[0xD019] = 0x00;
            m[0xD01A] = 0x00;
            m[0xD01E] = 0x00;
            m[0xD01F] = 0x00;

            for (int a = 0xD800; a <= 0xDBE7; a++) m[a] = 0x0E;

            //for (int a = 0x0400; a <= 0x07E7; a++) m[a] = 0x20;

            keyboard.Reset();
            sound.Reset();

            display.EndReset();
        }

        private bool OnIOWrite(ulong addr, byte value)
        {
            if (VerboseIoTrace)
            {
                byte oldVal = cpu.memory.memory[addr];
                if (oldVal != value && addr >= 0xD000 && addr <= 0xDFFF)
                {
                    Console.Error.WriteLine($"[{cpu.registers.PC:X4}] ${addr:X4} write: ${oldVal:X2} -> ${value:X2}");
                    Console.Error.Flush();
                }
            }

            switch (addr)
            {
                case 0xD012:
                    display.RasterCompare = (display.RasterCompare & 0x100) | value;
                    return true;
                case 0xD011:
                    {
                        byte oldD011 = cpu.memory.memory[0xD011];
                        display.RasterCompare = (display.RasterCompare & 0xFF) | ((value & 0x80) << 1);
                        byte oldHigh = (byte)(cpu.memory.memory[0xD011] & 0x80);
                        byte newVal = (byte)((value & 0x7F) | oldHigh);
                        cpu.memory.memory[0xD011] = (byte)((value & 0x7F) | oldHigh);
                        if (VerboseIoTrace && oldD011 != newVal)
                            Console.Error.WriteLine($"[D011 HANDLER] oldVal=${oldD011:X2}, incoming=${value:X2}, computed=${newVal:X2}, stored");
                        return true;
                    }
                case 0xD016:
                    {
                        cpu.memory.memory[0xD016] = value;
                        return true;
                    }
                case 0xD018:
                    {
                        cpu.memory.memory[0xD018] = value;
                        return true;
                    }
                case 0xD019:
                    {
                        byte cur = cpu.memory.memory[0xD019];
                        byte next = (byte)(cur & ~value);
                        if ((next & 0x0F) == 0) next &= 0x7F;
                        cpu.memory.memory[0xD019] = next;
                        return true;
                    }
                case 0xDC0D:
                    {
                        bool raiseIrq = false;
                        lock (cia1Lock)
                        {
                            byte bits = (byte)(value & 0x1F);
                            if ((value & 0x80) != 0)
                                cia1IcrMask |= bits;
                            else
                                cia1IcrMask = (byte)(cia1IcrMask & ~bits);

                            if ((cia1IcrStatus & cia1IcrMask & 0x1F) != 0)
                            {
                                bool wasSet = (cia1IcrStatus & 0x80) != 0;
                                cia1IcrStatus |= 0x80;
                                if (!wasSet) raiseIrq = true;
                            }
                            else
                            {
                                cia1IcrStatus = (byte)(cia1IcrStatus & 0x7F);
                            }
                            cpu.memory.memory[0xDC0D] = cia1IcrStatus;
                        }
                        if (raiseIrq) cpu.InitiateIRQ(0xFFFE);
                        return true;
                    }
                case 0xDC04:
                    {
                        lock (cia1Lock)
                        {
                            cia1TimerALatch = (ushort)((cia1TimerALatch & 0xFF00) | value);
                            if ((cia1Cra & 0x01) == 0)
                                cia1TimerACounter = cia1TimerALatch;
                            cpu.memory.memory[0xDC04] = (byte)(cia1TimerACounter & 0xFF);
                            cpu.memory.memory[0xDC05] = (byte)(cia1TimerACounter >> 8);
                        }
                        return true;
                    }
                case 0xDC05:
                    {
                        lock (cia1Lock)
                        {
                            cia1TimerALatch = (ushort)((cia1TimerALatch & 0x00FF) | (value << 8));
                            if ((cia1Cra & 0x01) == 0)
                                cia1TimerACounter = cia1TimerALatch;
                            cpu.memory.memory[0xDC04] = (byte)(cia1TimerACounter & 0xFF);
                            cpu.memory.memory[0xDC05] = (byte)(cia1TimerACounter >> 8);
                        }
                        return true;
                    }
                case 0xDC0E:
                    {
                        lock (cia1Lock)
                        {
                            if ((value & 0x10) != 0)
                            {
                                cia1TimerACounter = cia1TimerALatch;
                                cpu.memory.memory[0xDC04] = (byte)(cia1TimerACounter & 0xFF);
                                cpu.memory.memory[0xDC05] = (byte)(cia1TimerACounter >> 8);
                            }

                            cia1Cra = (byte)(value & 0xEF);
                            cpu.memory.memory[0xDC0E] = cia1Cra;
                        }
                        return true;
                    }
                case 0xDC06:
                    {
                        lock (cia1Lock)
                        {
                            cia1TimerBLatch = (ushort)((cia1TimerBLatch & 0xFF00) | value);
                            if ((cia1Crb & 0x01) == 0)
                                cia1TimerBCounter = cia1TimerBLatch;
                            cpu.memory.memory[0xDC06] = (byte)(cia1TimerBCounter & 0xFF);
                            cpu.memory.memory[0xDC07] = (byte)(cia1TimerBCounter >> 8);
                        }
                        return true;
                    }
                case 0xDC07:
                    {
                        lock (cia1Lock)
                        {
                            cia1TimerBLatch = (ushort)((cia1TimerBLatch & 0x00FF) | (value << 8));
                            if ((cia1Crb & 0x01) == 0)
                                cia1TimerBCounter = cia1TimerBLatch;
                            cpu.memory.memory[0xDC06] = (byte)(cia1TimerBCounter & 0xFF);
                            cpu.memory.memory[0xDC07] = (byte)(cia1TimerBCounter >> 8);
                        }
                        return true;
                    }
                case 0xDC0F:
                    {
                        lock (cia1Lock)
                        {
                            if ((value & 0x10) != 0)
                            {
                                cia1TimerBCounter = cia1TimerBLatch;
                                cpu.memory.memory[0xDC06] = (byte)(cia1TimerBCounter & 0xFF);
                                cpu.memory.memory[0xDC07] = (byte)(cia1TimerBCounter >> 8);
                            }

                            cia1Crb = (byte)(value & 0xEF);
                            cpu.memory.memory[0xDC0F] = cia1Crb;
                        }
                        return true;
                    }
                case 0xDC00:
                    cia1PortA = value;
                    cpu.memory.memory[addr] = value;
                    return true;
                case 0xDC01:
                    cia1PortB = value;
                    cpu.memory.memory[addr] = value;
                    return true;
                case 0xDC02:
                    cia1Ddra = value;
                    cpu.memory.memory[addr] = value;
                    return true;
                case 0xDC03:
                    cia1Ddrb = value;
                    cpu.memory.memory[addr] = value;
                    return true;
                case 0xDD00:
                    cia2PortA = value;
                    cpu.memory.memory[addr] = value;
                    return true;
                case 0xDD02:
                    cia2Ddra = (byte)(value & 0x3F);
                    cpu.memory.memory[addr] = value;
                    return true;
                case 0xDD04:
                    lock (cia2Lock)
                    {
                        cia2TimerALatch = (ushort)((cia2TimerALatch & 0xFF00) | value);
                        if ((cia2Cra & 0x01) == 0)
                            cia2TimerACounter = cia2TimerALatch;
                        cpu.memory.memory[0xDD04] = (byte)(cia2TimerACounter & 0xFF);
                        cpu.memory.memory[0xDD05] = (byte)(cia2TimerACounter >> 8);
                    }
                    return true;
                case 0xDD05:
                    lock (cia2Lock)
                    {
                        cia2TimerALatch = (ushort)((cia2TimerALatch & 0x00FF) | (value << 8));
                        if ((cia2Cra & 0x01) == 0)
                            cia2TimerACounter = cia2TimerALatch;
                        cpu.memory.memory[0xDD04] = (byte)(cia2TimerACounter & 0xFF);
                        cpu.memory.memory[0xDD05] = (byte)(cia2TimerACounter >> 8);
                    }
                    return true;
                case 0xDD06:
                    lock (cia2Lock)
                    {
                        cia2TimerBLatch = (ushort)((cia2TimerBLatch & 0xFF00) | value);
                        if ((cia2Crb & 0x01) == 0)
                            cia2TimerBCounter = cia2TimerBLatch;
                        cpu.memory.memory[0xDD06] = (byte)(cia2TimerBCounter & 0xFF);
                        cpu.memory.memory[0xDD07] = (byte)(cia2TimerBCounter >> 8);
                    }
                    return true;
                case 0xDD07:
                    lock (cia2Lock)
                    {
                        cia2TimerBLatch = (ushort)((cia2TimerBLatch & 0x00FF) | (value << 8));
                        if ((cia2Crb & 0x01) == 0)
                            cia2TimerBCounter = cia2TimerBLatch;
                        cpu.memory.memory[0xDD06] = (byte)(cia2TimerBCounter & 0xFF);
                        cpu.memory.memory[0xDD07] = (byte)(cia2TimerBCounter >> 8);
                    }
                    return true;
                case 0xDD0E:
                    lock (cia2Lock)
                    {
                        if ((value & 0x10) != 0)
                        {
                            cia2TimerACounter = cia2TimerALatch;
                            cpu.memory.memory[0xDD04] = (byte)(cia2TimerACounter & 0xFF);
                            cpu.memory.memory[0xDD05] = (byte)(cia2TimerACounter >> 8);
                        }
                        cia2Cra = (byte)(value & 0xEF);
                        cpu.memory.memory[0xDD0E] = cia2Cra;
                    }
                    return true;
                case 0xDD0F:
                    lock (cia2Lock)
                    {
                        if ((value & 0x10) != 0)
                        {
                            cia2TimerBCounter = cia2TimerBLatch;
                            cpu.memory.memory[0xDD06] = (byte)(cia2TimerBCounter & 0xFF);
                            cpu.memory.memory[0xDD07] = (byte)(cia2TimerBCounter >> 8);
                        }
                        cia2Crb = (byte)(value & 0xEF);
                        cpu.memory.memory[0xDD0F] = cia2Crb;
                    }
                    return true;
            }

            // SID registers are mirrored across $D400-$D7FF in 32-byte blocks.
            // Accept mirrored writes so routines using alternate SID mirrors
            // (common in some games/effects code) are not lost.
            if (addr >= 0xD400 && addr <= 0xD7FF)
            {
                int sidReg = (int)((addr - 0xD400) & 0x1F);
                sound.WriteRegister(sidReg, value);
                cpu.memory.memory[addr] = value;
                return true;
            }
            return false;
        }

        private byte OnIORead(ulong addr, byte fallback)
        {
            switch (addr)
            {
                case 0xD011:
                    {
                        byte low = (byte)(cpu.memory.memory[0xD011] & 0x7F);
                        byte hi = (byte)(((display.CurrentRasterLine >> 8) & 0x01) << 7);
                        return (byte)(low | hi);
                    }
                case 0xD012:
                    return (byte)(display.CurrentRasterLine & 0xFF);
                case 0xDC00:
                    return ReadCia1PortA();
                case 0xDC01:
                    return ReadCia1PortB();
                case 0xDC02:
                    return cia1Ddra;
                case 0xDC03:
                    return cia1Ddrb;
                case 0xDD00:
                    {
                        byte external = 0xFF;
                        byte v = MergeCiaPortRead(cia2PortA, cia2Ddra, external);
                        return (byte)(v | 0xC0);
                    }
                case 0xDD02:
                    return cia2Ddra;
                case 0xDD04:
                    lock (cia2Lock)
                        return (byte)(cia2TimerACounter & 0xFF);
                case 0xDD05:
                    lock (cia2Lock)
                        return (byte)(cia2TimerACounter >> 8);
                case 0xDD06:
                    lock (cia2Lock)
                        return (byte)(cia2TimerBCounter & 0xFF);
                case 0xDD07:
                    lock (cia2Lock)
                        return (byte)(cia2TimerBCounter >> 8);
                case 0xDD0E:
                    return cia2Cra;
                case 0xDD0F:
                    return cia2Crb;
                case 0xDC04:
                    {
                        lock (cia1Lock)
                            return (byte)(cia1TimerACounter & 0xFF);
                    }
                case 0xDC05:
                    {
                        lock (cia1Lock)
                            return (byte)(cia1TimerACounter >> 8);
                    }
                case 0xDC06:
                    {
                        lock (cia1Lock)
                            return (byte)(cia1TimerBCounter & 0xFF);
                    }
                case 0xDC07:
                    {
                        lock (cia1Lock)
                            return (byte)(cia1TimerBCounter >> 8);
                    }
                case 0xD01E:
                case 0xD01F:
                    {
                        byte value = cpu.memory.memory[addr];
                        cpu.memory.memory[addr] = 0;
                        return value;
                    }
                case 0xDC0D:
                    {
                        lock (cia1Lock)
                        {
                            byte value = cia1IcrStatus;
                            cia1IcrStatus = 0x00;
                            cpu.memory.memory[0xDC0D] = 0x00;
                            return value;
                        }
                    }
                case 0xDD0D:
                    {
                        byte value = cpu.memory.memory[addr];
                        cpu.memory.memory[addr] = 0;
                        return value;
                    }
                default:
                    // SID readback registers are mirrored across $D400-$D7FF.
                    // We only provide meaningful values for $19-$1C (POT/POT/OSC3/ENV3).
                    if (addr >= 0xD400 && addr <= 0xD7FF)
                        return sound.ReadRegister((int)((addr - 0xD400) & 0x1F));
                    return fallback;
            }
        }

        private byte ReadCia1PortA()
        {
            byte external = 0xFF;
            external &= keyboard.ScanMatrix(cia1PortB, cia1Ddrb);
            external &= keyboard.Joystick2;
            return MergeCiaPortRead(cia1PortA, cia1Ddra, external);
        }

        private byte ReadCia1PortB()
        {
            byte external = keyboard.ScanMatrix(cia1PortA, cia1Ddra);
            return MergeCiaPortRead(cia1PortB, cia1Ddrb, external);
        }

        private static byte MergeCiaPortRead(byte latch, byte ddr, byte external)
        {
            byte outBits = (byte)((latch & external) & ddr);
            byte inBits = (byte)(external & (byte)~ddr);
            return (byte)(outBits | inBits);
        }


        private static int CountUnderflows(ref ushort counter, ushort latch, uint ticks, bool oneShot, ref byte control)
        {
            if (ticks == 0 || (control & 0x01) == 0)
                return 0;

            int underflows = 0;
            uint remaining = ticks;
            while (remaining > 0 && (control & 0x01) != 0)
            {
                uint stepsToUnderflow = (uint)counter + 1u;
                if (remaining < stepsToUnderflow)
                {
                    counter = (ushort)(counter - remaining);
                    break;
                }

                remaining -= stepsToUnderflow;
                underflows++;
                counter = latch;
                if (oneShot)
                    control = (byte)(control & ~0x01);
            }

            return underflows;
        }

        private void StepCia1Timers(uint cycles)
        {
            if (cycles == 0) return;

            bool raiseIrq = false;
            lock (cia1Lock)
            {
                uint cntPulses = cia1CntHigh ? cycles : 0u;

                uint ticksA = (cia1Cra & 0x20) == 0 ? cycles : cntPulses;
                int underA = CountUnderflows(
                    ref cia1TimerACounter,
                    cia1TimerALatch,
                    ticksA,
                    (cia1Cra & 0x08) != 0,
                    ref cia1Cra);
                if (underA > 0)
                {
                    cia1IcrStatus |= 0x01;
                    if ((cia1IcrMask & 0x01) != 0)
                    {
                        cia1IcrStatus |= 0x80;
                        raiseIrq = true;
                    }
                }

                if ((cia1Cra & 0x40) != 0 && underA > 0)
                    cntPulses = Math.Max(cntPulses, (uint)underA);

                int tbMode = (cia1Crb >> 5) & 0x03;
                uint ticksB = 0;
                if (tbMode == 0)
                    ticksB = cycles;
                else if (tbMode == 1)
                    ticksB = cntPulses;
                else if (tbMode == 2)
                    ticksB = (uint)underA;
                else
                    ticksB = cia1CntHigh ? (uint)underA : 0;

                if (ticksB > 0)
                {
                    int underB = CountUnderflows(
                        ref cia1TimerBCounter,
                        cia1TimerBLatch,
                        ticksB,
                        (cia1Crb & 0x08) != 0,
                        ref cia1Crb);
                    if (underB > 0)
                    {
                        cia1IcrStatus |= 0x02;
                        if ((cia1IcrMask & 0x02) != 0)
                        {
                            cia1IcrStatus |= 0x80;
                            raiseIrq = true;
                        }
                    }
                }

                if ((cia1IcrStatus & cia1IcrMask & 0x1F) != 0)
                    cia1IcrStatus |= 0x80;
                else
                    cia1IcrStatus = (byte)(cia1IcrStatus & 0x7F);

                cpu.memory.memory[0xDC04] = (byte)(cia1TimerACounter & 0xFF);
                cpu.memory.memory[0xDC05] = (byte)(cia1TimerACounter >> 8);
                cpu.memory.memory[0xDC06] = (byte)(cia1TimerBCounter & 0xFF);
                cpu.memory.memory[0xDC07] = (byte)(cia1TimerBCounter >> 8);
                cpu.memory.memory[0xDC0D] = cia1IcrStatus;
                cpu.memory.memory[0xDC0E] = cia1Cra;
                cpu.memory.memory[0xDC0F] = cia1Crb;
            }

            if (raiseIrq)
                cpu.InitiateIRQ(0xFFFE);
        }

        private void StepCia2Timers(uint cycles)
        {
            if (cycles == 0) return;

            lock (cia2Lock)
            {
                int underA = CountUnderflows(
                    ref cia2TimerACounter,
                    cia2TimerALatch,
                    cycles,
                    (cia2Cra & 0x08) != 0,
                    ref cia2Cra);

                uint ticksB = cycles;
                int tbMode = (cia2Crb >> 5) & 0x03;
                if (tbMode == 2)
                    ticksB = (uint)Math.Max(underA, 0);
                else if (tbMode != 0)
                    ticksB = 0;

                if (ticksB > 0)
                {
                    CountUnderflows(
                        ref cia2TimerBCounter,
                        cia2TimerBLatch,
                        ticksB,
                        (cia2Crb & 0x08) != 0,
                        ref cia2Crb);
                }

                cpu.memory.memory[0xDD04] = (byte)(cia2TimerACounter & 0xFF);
                cpu.memory.memory[0xDD05] = (byte)(cia2TimerACounter >> 8);
                cpu.memory.memory[0xDD06] = (byte)(cia2TimerBCounter & 0xFF);
                cpu.memory.memory[0xDD07] = (byte)(cia2TimerBCounter >> 8);
                cpu.memory.memory[0xDD0E] = cia2Cra;
                cpu.memory.memory[0xDD0F] = cia2Crb;
            }
        }

        private string BuildDebugStateLine(string prefix)
        {
            ulong pc = cpu.registers.PC;
            byte[] m = cpu.memory.memory;
            byte a = cpu.registers.A;
            byte x = cpu.registers.X;
            byte y = cpu.registers.Y;
            byte s = cpu.registers.S;
            byte p = cpu.registers.P;

            ushort ta;
            ushort tb;
            byte cra;
            byte crb;
            byte icrMask;
            byte icrStatus;
            lock (cia1Lock)
            {
                ta = cia1TimerACounter;
                tb = cia1TimerBCounter;
                cra = cia1Cra;
                crb = cia1Crb;
                icrMask = cia1IcrMask;
                icrStatus = cia1IcrStatus;
            }

            return
                $"{prefix} PC=${pc:X4} A=${a:X2} X=${x:X2} Y=${y:X2} S=${s:X2} P=${p:X2} " +
                $"RAST={display.CurrentRasterLine:D3}/{display.RasterCompare:D3} DD00=${m[0xDD00]:X2} DD02=${m[0xDD02]:X2} D011=${m[0xD011]:X2} D012=${m[0xD012]:X2} D016=${m[0xD016]:X2} D018=${m[0xD018]:X2} D019=${m[0xD019]:X2} D01A=${m[0xD01A]:X2} " +
                $"CIA TA=${ta:X4} TB=${tb:X4} CRA=${cra:X2} CRB=${crb:X2} ICRM=${icrMask:X2} ICRS=${icrStatus:X2} DC0D=${m[0xDC0D]:X2}";
        }

        private void OnIOPostRead(ulong addr)
        {
        }

        public void Run()
        {
            string? audioDevice = Sound.PromptForDevice();
            display.Init();
            sound.Init(audioDevice);

            var token = cts.Token;

            if (TraceVicStates)
                _ = Task.Run(() => TraceVicStatesAsync(token));

            cpuThread = new Thread(() =>
            {
                try { cpu.Run(); }
                catch (Exception ex) { Console.Error.WriteLine($"CPU thread crashed: {ex}"); }
            })
            {
                IsBackground = true,
                Name = "6502",
                Priority = ThreadPriority.AboveNormal
            };
            cpuThread.Start();

            display.Start(token);
            sound.Start(token);

            irqThread = new Thread(() => IrqLoop(token))
            {
                IsBackground = true,
                Name = "CIA-1 IRQ"
            };
            irqThread.Start();

            // Run the exact same reset path used by Ctrl+R after all worker
            // threads are alive. This avoids startup-only races where the
            // display can remain in reset on first launch.
            HardReset();

            var startupWait = Stopwatch.StartNew();
            while (display.IsResetting && startupWait.ElapsedMilliseconds < 3000)
                SDL_Delay(1);

            if (display.IsResetting)
            {
                Console.Error.WriteLine("[BOOT] initial reset timed out; retrying startup reset");
                HardReset();

                startupWait.Restart();
                while (display.IsResetting && startupWait.ElapsedMilliseconds < 3000)
                    SDL_Delay(1);

                if (display.IsResetting)
                    Console.Error.WriteLine("[BOOT] startup reset still pending; Ctrl+R will force another reset");
            }

            bool quit = false;
            uint nextDraw = SDL_GetTicks();
            const uint drawIntervalMs = 16; // ~60 Hz upper bound; vsync paces actual present

            while (!quit)
            {
                while (SDL_PollEvent(out SDL_Event ev) != 0)
                {
                    switch (ev.type)
                    {
                        case SDL_EventType.SDL_QUIT:
                            quit = true;
                            break;
                        case SDL_EventType.SDL_KEYDOWN:
                        case SDL_EventType.SDL_KEYUP:
                            if (keyboard.HandleSdlEvent(ev)) quit = true;
                            break;
                        case SDL_EventType.SDL_DROPFILE:
                            {
                                IntPtr p = ev.drop.file;
                                string? droppedPath = Marshal.PtrToStringUTF8(p);
                                if (!string.IsNullOrWhiteSpace(droppedPath))
                                    pendingLoads.Enqueue((droppedPath, true));
                                break;
                            }
                    }
                }

                while (pendingLoads.TryDequeue(out var entry))
                {
                    if (entry.AutoRun)
                        _ = Task.Run(() => ResetLoadRun(entry.Path));
                    else
                        DoLoad(entry.Path);
                }

                uint now = SDL_GetTicks();
                if ((int)(now - nextDraw) >= 0)
                {
                    display.RedrawScreen();
                    nextDraw = now + drawIntervalMs;
                }
                else
                {
                    SDL_Delay(1);
                }
            }

            cts.Cancel();
        }

        public void Dispose()
        {
            try { cts.Cancel(); } catch { }
            sound.Dispose();
            display.Dispose();
            SDL_Quit();
        }

        private void IrqLoop(CancellationToken token)
        {
            long ticksPerTick = Stopwatch.Frequency / CiaTickHz;
            long next = Stopwatch.GetTimestamp() + ticksPerTick;
            long remCyclesNumerator = 0;
            long lastStamp = Stopwatch.GetTimestamp();
            while (!token.IsCancellationRequested)
            {
                keyboard.DrainQueue();

                long now = Stopwatch.GetTimestamp();
                long elapsedTicks = now - lastStamp;
                if (elapsedTicks < 0) elapsedTicks = 0;
                lastStamp = now;

                if (display.IsResetting)
                {
                    long pauseRemaining = next - Stopwatch.GetTimestamp();
                    if (pauseRemaining > 0)
                    {
                        while (Stopwatch.GetTimestamp() < next)
                            Thread.SpinWait(32);
                    }
                    next += ticksPerTick;
                    continue;
                }

                long numer = elapsedTicks * Clock_PAL + remCyclesNumerator;
                if (numer >= Stopwatch.Frequency)
                {
                    uint cycles = (uint)(numer / Stopwatch.Frequency);
                    remCyclesNumerator = numer % Stopwatch.Frequency;
                    StepCia1Timers(cycles);
                    StepCia2Timers(cycles);
                }
                else
                {
                    remCyclesNumerator = numer;
                }

                long remaining = next - Stopwatch.GetTimestamp();
                if (remaining > 0)
                {
                    long remainingMs = remaining * 1000 / Stopwatch.Frequency;
                    if (remainingMs > 2)
                        Thread.Sleep((int)(remainingMs - 1));
                    while (Stopwatch.GetTimestamp() < next)
                        Thread.SpinWait(32);
                }
                next += ticksPerTick;
            }
        }



        private void HardReset()
        {
            display.BeginReset();

            keyboard.Reset();

            cpu.RequestReset();
        }

        /// <summary>
        /// Drag-and-drop flow: reset the emulator (Ctrl+R equivalent), wait
        /// for the KERNAL to reach the READY prompt, load the file, then
        /// type RUN + RETURN.  Runs on a background task so we don't block
        /// the SDL event pump while waiting for the boot sequence.
        /// </summary>
        private async Task ResetLoadRun(string path)
        {
            HardReset();

            // Wait until reset is complete and BASIC reaches READY.
            // Fixed wall-clock delays are brittle across host speeds/builds.
            await WaitForReadyPromptAsync(timeoutMs: 6000).ConfigureAwait(false);

            DoLoad(path);

            // Small settle so the load has fully landed in memory before
            // BASIC tokenises RUN.
            await Task.Delay(50).ConfigureAwait(false);

            await TypePetsciiLikeHumanAsync(
                new byte[] { (byte)'R', (byte)'U', (byte)'N', 0x0D },
                minInterKeyMs: 110,
                maxInterKeyMs: 220,
                enterExtraMs: 120).ConfigureAwait(false);
        }

        private async Task TypePetsciiLikeHumanAsync(
            ReadOnlyMemory<byte> text,
            int minInterKeyMs,
            int maxInterKeyMs,
            int enterExtraMs = 0)
        {
            for (int i = 0; i < text.Length; i++)
            {
                byte key = text.Span[i];
                keyboard.EnqueuePetscii(key);

                int delay = Random.Shared.Next(minInterKeyMs, maxInterKeyMs + 1);
                if (key == 0x0D)
                    delay += enterExtraMs;

                await Task.Delay(delay).ConfigureAwait(false);
            }
        }

        private async Task WaitForReadyPromptAsync(int timeoutMs)
        {
            const int pollMs = 20;
            int waitedMs = 0;

            while (display.IsResetting && waitedMs < timeoutMs)
            {
                await Task.Delay(pollMs).ConfigureAwait(false);
                waitedMs += pollMs;
            }

            while (waitedMs < timeoutMs)
            {
                if (HasReadyPromptOnScreen())
                    return;

                await Task.Delay(pollMs).ConfigureAwait(false);
                waitedMs += pollMs;
            }
        }

        private bool HasReadyPromptOnScreen()
        {
            // C64 screen RAM stores screen codes, not ASCII.
            // READY. in upper-case screen code sequence.
            ReadOnlySpan<byte> ready = stackalloc byte[] { 18, 5, 1, 4, 25, 46 };
            byte[] mem = cpu.memory.memory;
            const int start = 0x0400;
            const int end = 0x07E7;

            for (int i = start; i <= end - ready.Length + 1; i++)
            {
                bool match = true;
                for (int j = 0; j < ready.Length; j++)
                {
                    if (mem[i + j] != ready[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                    return true;
            }

            return false;
        }

        public void QueueLoadAndRun(string path) => pendingLoads.Enqueue((path, true));

        public void QueueLoad(string path) => pendingLoads.Enqueue((path, false));

        private async Task TraceVicStatesAsync(CancellationToken token)
        {
            string? lastState = null;

            while (!token.IsCancellationRequested)
            {
                byte[] m = cpu.memory.memory;
                string mode = ((m[0xD011] & 0x20) != 0, (m[0xD016] & 0x10) != 0, (m[0xD011] & 0x40) != 0) switch
                {
                    (true, true, _) => "mc-bitmap",
                    (true, false, _) => "hires-bitmap",
                    (false, true, false) => "mc-text",
                    (false, false, true) => "ecm-text",
                    _ => "std-text",
                };

                string state =
                    $"mode={mode} DD00=${m[0xDD00]:X2} DD02=${m[0xDD02]:X2} D011=${m[0xD011]:X2} D016=${m[0xD016]:X2} D018=${m[0xD018]:X2} RAST={display.CurrentRasterLine:D3}";

                if (!string.Equals(state, lastState, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine($"[VIC] {state}");
                    lastState = state;
                }

                try
                {
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void LoadProgram()
        {
            Task.Run(() =>
            {
                try
                {
                    Console.Write("Load file (.prg, .t64, .tap, .bas, .txt) - path: ");
                    string? path = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(path)) return;
                    path = path.Trim().Trim('"', '\'');
                    pendingLoads.Enqueue((path, false));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Load prompt failed: {ex.Message}");
                }
            });
        }

        private void DoLoad(string path)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Load failed: file not found: {path}");
                return;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                if (ext == ".bas" || ext == ".txt")
                {
                    LoadText(path);
                    Console.WriteLine($"Loaded {Path.GetFileName(path)}");
                }
                else if (ext == ".t64")
                {
                    var entries = TapeLoader.ReadT64(File.ReadAllBytes(path));
                    LoadTapeEntries(entries, Path.GetFileName(path));
                }
                else if (ext == ".tap")
                {
                    var entries = TapeLoader.ReadTap(File.ReadAllBytes(path));
                    LoadTapeEntries(entries, Path.GetFileName(path));
                }
                else
                {
                    LoadPrg(path);
                    Console.WriteLine($"Loaded {Path.GetFileName(path)}");
                }
            }
            catch (TurboTapeException ex)
            {
                Console.Error.WriteLine($"Tape load failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Load failed: {ex.Message}");
            }
        }

        private void LoadPrg(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 3)
                throw new InvalidDataException("PRG file is too small (need 2-byte header + body).");

            ushort loadAddr = (ushort)(data[0] | (data[1] << 8));
            int progLen = data.Length - 2;
            byte[] mem = cpu.memory.memory;

            if (loadAddr + progLen > mem.Length)
                throw new InvalidDataException("PRG file would load past end of memory.");

            for (int i = 0; i < progLen; i++)
                cpu.memory.WriteRamByte((ulong)(loadAddr + i), data[2 + i]);

            if (loadAddr == 0x0801)
            {
                int endAddr = loadAddr + progLen;
                cpu.memory.WriteByte(0x002D, (byte)(endAddr & 0xFF));
                cpu.memory.WriteByte(0x002E, (byte)(endAddr >> 8));
                cpu.memory.WriteByte(0x002F, (byte)(endAddr & 0xFF));
                cpu.memory.WriteByte(0x0030, (byte)(endAddr >> 8));
                cpu.memory.WriteByte(0x0031, (byte)(endAddr & 0xFF));
                cpu.memory.WriteByte(0x0032, (byte)(endAddr >> 8));
            }

        }

        private void DumpGraphicsStateToFile()
        {
            try
            {
                byte[] mem = cpu.memory.memory;
                byte d011 = mem[0xD011];
                byte d016 = mem[0xD016];
                byte d018 = mem[0xD018];
                byte d020 = mem[0xD020];
                byte d021 = mem[0xD021];
                byte dd00 = mem[0xDD00];
                byte d015 = mem[0xD015];
                byte p1 = mem[0x0001];

                bool screenOn = (d011 & 0x10) != 0;
                bool bmm = (d011 & 0x20) != 0;
                bool ecm = (d011 & 0x40) != 0;
                bool mcm = (d016 & 0x10) != 0;
                int bank = (3 - (dd00 & 0x03)) * 0x4000;
                int screenAddr = bank + ((d018 >> 4) & 0x0F) * 0x400;
                int charAddr = bank + ((d018 >> 1) & 0x07) * 0x800;

                Console.Error.WriteLine("=== GRAPHICS STATE ===");
                Console.Error.WriteLine($"CPU PC: ${cpu.registers.PC:X4}, SP: ${cpu.registers.S:X2}");
                Console.Error.WriteLine($"Processor Port ($0001): ${p1:X2}");
                Console.Error.WriteLine($"Screen ON (DEN): {screenOn}");
                Console.Error.WriteLine($"BMM (Bitmap): {bmm}, ECM (ExtBg): {ecm}, MCM (Multicolor): {mcm}");
                if (bmm && mcm) Console.Error.WriteLine("  → Multicolor Bitmap Mode");
                else if (bmm) Console.Error.WriteLine("  → Hires Bitmap Mode");
                else if (ecm) Console.Error.WriteLine("  → Extended Bg Text Mode");
                else if (mcm) Console.Error.WriteLine("  → Multicolor Text Mode");
                else Console.Error.WriteLine("  → Standard Text Mode");
                Console.Error.WriteLine($"D011: ${d011:X2}, D016: ${d016:X2}, D018: ${d018:X2}");
                Console.Error.WriteLine($"Screen RAM: ${screenAddr:X4}, Char/Bitmap: ${charAddr:X4}");
                Console.Error.WriteLine($"VIC Bank (CIA2 $DD00): ${bank:X4}");
                Console.Error.WriteLine($"Border (D020): ${d020:X2}, BG0 (D021): ${d021:X2}");
                Console.Error.WriteLine($"Sprites Enabled (D015): ${d015:X2}");

                // Check memory at potential VIC-II addresses
                Console.Error.WriteLine($"\nVIC-II registers (raw memory):");
                Console.Error.WriteLine($"  D011 ($D011): ${mem[0xD011]:X2}");
                Console.Error.WriteLine($"  D016 ($D016): ${mem[0xD016]:X2}");
                Console.Error.WriteLine($"  D018 ($D018): ${mem[0xD018]:X2}");
                Console.Error.WriteLine($"  DD00 ($DD00): ${mem[0xDD00]:X2}");

                // Check if there's any data at screen RAM
                Console.Error.WriteLine($"\nScreen RAM analysis:");
                Console.Error.WriteLine($"  First 10 bytes at ${screenAddr:X4}: {string.Join(" ", Enumerable.Range(0, 10).Select(i => mem[screenAddr + i].ToString("X2")))}");
                Console.Error.WriteLine($"  Color RAM at $D800: {string.Join(" ", Enumerable.Range(0, 10).Select(i => mem[0xD800 + i].ToString("X2")))}");

                Console.Error.WriteLine("======================");

                // Also write to file for debugging when console is hidden
                try
                {
                    System.IO.File.WriteAllLines("/tmp/c64_graphics_state.txt", new[] {
                        $"CPU PC: ${cpu.registers.PC:X4}, SP: ${cpu.registers.S:X2}",
                        $"Screen ON: {screenOn}, D011: ${d011:X2}, D016: ${d016:X2}, D018: ${d018:X2}",
                        $"Screen RAM: ${screenAddr:X4}, First 10: {string.Join(" ", Enumerable.Range(0, 10).Select(i => mem[screenAddr + i].ToString("X2")))}"
                    });
                }
                catch { }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error dumping graphics state: {ex}");
            }
        }

        private void LoadTapeEntries(List<TapeEntry> entries, string archiveName)
        {
            // Load all entries in order.  Multi-file games (e.g. loader + main)
            // store parts as separate directory entries; loading them sequentially
            // places everything in memory before the first SYS / RUN.
            int loaded = 0;
            foreach (TapeEntry entry in entries)
            {
                byte[] mem = cpu.memory.memory;
                int dataLen = entry.Data.Length;

                if (entry.LoadAddress + dataLen > mem.Length)
                {
                    Console.Error.WriteLine(
                        $"  Skipping '{entry.Name}': would load past end of memory.");
                    continue;
                }

                for (int i = 0; i < dataLen; i++)
                    cpu.memory.WriteRamByte((ulong)(entry.LoadAddress + i), entry.Data[i]);

                if (entry.IsBasic)
                {
                    // Update BASIC end-of-program pointers.
                    int endAddr = entry.LoadAddress + dataLen;
                    cpu.memory.WriteByte(0x002D, (byte)(endAddr & 0xFF));
                    cpu.memory.WriteByte(0x002E, (byte)(endAddr >> 8));
                    cpu.memory.WriteByte(0x002F, (byte)(endAddr & 0xFF));
                    cpu.memory.WriteByte(0x0030, (byte)(endAddr >> 8));
                    cpu.memory.WriteByte(0x0031, (byte)(endAddr & 0xFF));
                    cpu.memory.WriteByte(0x0032, (byte)(endAddr >> 8));
                }

                Console.WriteLine(
                    $"  FOUND  {entry.Name,-16}  ${entry.LoadAddress:X4}–" +
                    $"${entry.LoadAddress + dataLen - 1:X4}  ({dataLen} bytes)");
                loaded++;
            }

            if (loaded == 0)
            {
                Console.Error.WriteLine($"Tape load failed: no entries could be loaded from {archiveName}.");
                return;
            }

            Console.WriteLine($"Loaded {archiveName}  ({loaded} block{(loaded == 1 ? "" : "s")})");
        }

        private void LoadText(string path)
        {
            foreach (var rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.TrimEnd('\r');
                foreach (char ch in line)
                {
                    byte pet = AsciiCharToPetscii(ch);
                    if (pet != 0) keyboard.EnqueuePetscii(pet);
                }
                keyboard.EnqueuePetscii(0x0D); // RETURN
            }
        }

        private static byte AsciiCharToPetscii(char ch)
        {
            if (ch >= 'a' && ch <= 'z') return (byte)('A' + (ch - 'a'));
            if (ch >= ' ' && ch <= '~') return (byte)ch;
            return 0;
        }

        private void SaveProgram()
        {
            byte[] mem = cpu.memory.memory;
            int endAddr = mem[0x2D] | (mem[0x2E] << 8);
            int progLen = endAddr - 0x0801;
            if (progLen <= 2)
            {
                Console.WriteLine("No BASIC program in memory.");
                return;
            }

            Console.Write("Save .prg - path: ");
            string? path = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(path)) return;
            path = path.Trim().Trim('"', '\'');
            if (!path.EndsWith(".prg", StringComparison.OrdinalIgnoreCase))
                path += ".prg";

            try
            {
                using var fs = File.Create(path);
                fs.WriteByte(0x01); // load address lo
                fs.WriteByte(0x08); // load address hi -> $0801
                fs.Write(mem, 0x0801, progLen);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Save failed: {ex.Message}");
            }
        }
    }
}