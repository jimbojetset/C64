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
            // Sayers.SDL2.Core P/Invokes the "SDL2" library, which on
            // macOS isn't on the default dyld search path when installed
            // via Homebrew. Probe the common locations so users don't
            // have to set DYLD_LIBRARY_PATH manually.
            NativeLibrary.SetDllImportResolver(typeof(SDL2.SDL).Assembly, ResolveNativeLibrary);

            try
            {
                using var emu = new C64Emulator();
                // Optional: a file path on the command line is auto-loaded
                // at startup, equivalent to dragging it onto the window.
                if (args.Length > 0 && File.Exists(args[0]))
                    emu.QueueLoad(args[0]);
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

        // VIC-II visible area is 320 x 200 pixels.
        private const int ScreenW = 320;
        private const int ScreenH = 200;

        // Surround the active 320x200 playfield with a PAL-style border.
        // This is what users expect visually from a real C64 display.
        private const int FrameW = 384;
        private const int FrameH = 272;
        private const int FramePlayfieldX = (FrameW - ScreenW) / 2;
        private const int FramePlayfieldY = (FrameH - ScreenH) / 2;

        // PAL VIC-II: 312 raster lines per frame, 50 frames per second.
        private const int PalRasterLines = 312;
        private const int RasterLinesPerSecond = PalRasterLines * 50;

        // CIA helper thread cadence. Timer A is decremented by computed
        // elapsed CPU cycles each tick, not by a fixed IRQ cadence.
        private const int CiaTickHz = 1000;

        // C64 palette in 0xAARRGGBB (Pepto's calibrated colours).
        private static readonly int[] C64Palette =
        {
            unchecked((int)0xFF000000), //  0 BLACK
            unchecked((int)0xFFFFFFFF), //  1 WHITE
            unchecked((int)0xFF68372B), //  2 RED
            unchecked((int)0xFF70A4B2), //  3 CYAN
            unchecked((int)0xFF6F3D86), //  4 PURPLE
            unchecked((int)0xFF588D43), //  5 GREEN
            unchecked((int)0xFF352879), //  6 BLUE
            unchecked((int)0xFFB8C76F), //  7 YELLOW
            unchecked((int)0xFF6F4F25), //  8 ORANGE
            unchecked((int)0xFF433900), //  9 BROWN
            unchecked((int)0xFF9A6759), // 10 LIGHT RED
            unchecked((int)0xFF444444), // 11 DARK GREY
            unchecked((int)0xFF6C6C6C), // 12 MEDIUM GREY
            unchecked((int)0xFF9AD284), // 13 LIGHT GREEN
            unchecked((int)0xFF6C5EB5), // 14 LIGHT BLUE
            unchecked((int)0xFF959595), // 15 LIGHT GREY
        };

        private byte[] charRom = Array.Empty<byte>();

        // The raster thread now drives rendering scanline-by-scanline, so
        // mid-frame writes from the game's raster IRQ handler (changing
        // $D018, $D020, $D021, sprite positions, etc.) take effect on the
        // remaining scanlines - making split-screen tricks and sprite
        // multiplexers visible.
        //
        // renderBuf  - the raster thread writes here, one scanline at a
        //              time, as that line is reached in the emulated frame.
        // displayBuf - the UI thread blits from here. We swap at vsync so
        //              the user always sees a complete frame, no tearing.
        private byte[] renderBuf = new byte[ScreenW * ScreenH * 4];
        private byte[] displayBuf = new byte[ScreenW * ScreenH * 4];
        private readonly object swapLock = new object();

        // Per-scanline foreground/sprite masks (320 entries each).
        // Cleared at the start of every scanline.
        private readonly bool[] fgLine = new bool[ScreenW];
        private readonly byte[] spriteLine = new byte[ScreenW];

        // PAL playfield: raster lines 51..250 inclusive are the visible
        // 200-line playfield. Lines outside this range are top/bottom
        // border; we just skip rendering them.
        private const int VisibleTop = 51;
        private const int VisibleBottom = 250;

        private int rasterCompare;
        private int currentRasterLine;
        private volatile bool resetInProgress;
        private volatile bool rasterResyncPending;

        // CIA-1 timer A / ICR state.
        private readonly object cia1Lock = new object();
        private ushort cia1TimerALatch = 0xFFFF;
        private ushort cia1TimerACounter = 0xFFFF;
        private ushort cia1TimerBLatch = 0xFFFF;
        private ushort cia1TimerBCounter = 0xFFFF;
        private byte cia1Cra;
        private byte cia1Crb;
        private byte cia1IcrMask;
        private byte cia1IcrStatus;
        // CIA CNT pin model. Without a full IEC/serial implementation we
        // approximate CNT as high and generate pulses when CIA serial
        // output mode is active (CRA bit 6), clocked by timer-A underflow.
        private bool cia1CntHigh = true;

        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private Thread? cpuThread;
        private Thread? rasterThread;
        private Thread? irqThread;

        private readonly ConcurrentQueue<byte> keyQueue = new ConcurrentQueue<byte>();

        // CIA-1 port A ($DC00) is read by games as joystick port 2.
        private byte joystick2 = 0xFF;

        // C64 keyboard matrix: each row is an active-low bitmask.
        private readonly byte[] keyboardMatrix = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

        // CIA-1 port latches / data direction registers.
        private byte cia1PortA = 0xFF;
        private byte cia1PortB = 0xFF;
        private byte cia1Ddra = 0x00;
        private byte cia1Ddrb = 0x00;

        // SDL handles
        private IntPtr window;
        private IntPtr renderer;
        private IntPtr texture;

        // Cross-thread queue of file paths waiting to be loaded. The
        // actual load runs on the main loop so it serialises with the
        // CPU thread's view of memory (and so we never block the SDL
        // event pump inside Console.ReadLine).
        private readonly ConcurrentQueue<string> pendingLoads = new ConcurrentQueue<string>();

        public C64Emulator()
        {
            cpu = new _6502_CPU(Clock_PAL);
            // Use banked-ROM loading so the 6510 processor port at $01
            // properly controls BASIC / KERNAL / CHARROM / I/O mapping.
            // ML games routinely bank BASIC or KERNAL out to gain RAM at
            // $A000+ / $E000+; without this they run for a few cycles
            // then RTS straight back into the BASIC READY prompt.
            cpu.memory.LoadBankedROM(Path.Combine("ROMS", "basic.901226-01.bin"), Memory.BankSlot.Basic);
            cpu.memory.LoadBankedROM(Path.Combine("ROMS", "kernal.901227-03.bin"), Memory.BankSlot.Kernal);
            cpu.memory.LoadBankedROM(Path.Combine("ROMS", "characters.901225-01.bin"), Memory.BankSlot.Char);
            charRom = File.ReadAllBytes(Path.Combine("ROMS", "characters.901225-01.bin"));

            // Fast-boot: skip RAMTAS. Patches the KERNAL ROM image once;
            // survives a soft reset because we never reload the ROM.
            byte[] kernal = cpu.memory.GetBankedROM(Memory.BankSlot.Kernal)!;
            kernal[0xFCF5 - 0xE000] = 0xEA;
            kernal[0xFCF6 - 0xE000] = 0xEA;
            kernal[0xFCF7 - 0xE000] = 0xEA;

            InitHardware();

            // Subsequent (Ctrl+R) resets re-run InitHardware on the CPU
            // thread so it doesn't race with instruction execution.
            cpu.OnReset = InitHardware;

            rasterCompare = 0;

            cpu.memory.OnIOWrite = OnIOWrite;
            cpu.memory.OnIORead = OnIORead;
            cpu.memory.OnIOPostRead = OnIOPostRead;
        }

        // Per-reset RAM / VIC / colour-RAM / screen-RAM setup. Replicates
        // what the KERNAL would leave behind after RAMTAS + IOINIT + CINT.
        private void InitHardware()
        {
            byte[] m = cpu.memory.memory;

            // Zero RAM ($0000-$FFFF). RAM exists underneath every ROM
            // window on a real C64, so clearing the full address space
            // is correct; ROM contents live in the banked-ROM buffers.
            Array.Clear(m, 0x0000, m.Length);

            // 6510 processor port: $00 = data direction (default $2F),
            // $01 = port value (default $37 -> LORAM=HIRAM=CHAREN=1,
            // i.e. BASIC + KERNAL + I/O all mapped). Must be set before
            // the first instruction executes or the reset vector fetch
            // at $FFFC/$FFFD would come from RAM instead of KERNAL ROM.
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

            // RAMTAS leaves behind these workspace pointers; mirror them.
            m[0x0281] = 0x00; m[0x0282] = 0x08; // MEMSTR = $0800
            m[0x0283] = 0x00; m[0x0284] = 0xA0; // MEMSIZ = $A000
            m[0x0288] = 0x04;                   // screen page = $0400

            // CIA-1 keyboard matrix: report "no keys pressed".
            m[0xDC00] = 0xFF;
            m[0xDC01] = 0xFF;

            // CIA-2 controls the VIC bank via $DD00 low 2 bits.
            // Default C64 setup is bank 0 ($0000-$3FFF), which with
            // $D018=$14 maps screen RAM at $0400 and char data at $1000.
            // If a game left this pointing elsewhere, reset must restore
            // it or the renderer can show a blank frame.
            m[0xDD00] = 0x17;
            m[0xDD02] = 0x3F;

            // VIC-II defaults that KERNAL sets in IOINIT.
            m[0xD011] = 0x1B; // DEN, RSEL, YSCROLL=3
            m[0xD016] = 0xC8; // (top bits), CSEL, XSCROLL=0
            m[0xD018] = 0x14; // screen $0400, char ROM shadow $1000
            m[0xD020] = 0x0E; // border  = light blue
            m[0xD021] = 0x06; // bg 0    = blue
            m[0xD022] = 0x01; // bg 1    = white
            m[0xD023] = 0x02; // bg 2    = red
            m[0xD024] = 0x03; // bg 3    = cyan
            // Sprite control registers: all sprites off, no expansion etc.
            m[0xD015] = 0x00;
            m[0xD017] = 0x00;
            m[0xD01B] = 0x00;
            m[0xD01C] = 0x00;
            m[0xD01D] = 0x00;
            m[0xD019] = 0x00;
            m[0xD01A] = 0x00;
            m[0xD01E] = 0x00;
            m[0xD01F] = 0x00;

            // Colour RAM: light blue (matches default text colour).
            for (int a = 0xD800; a <= 0xDBE7; a++) m[a] = 0x0E;

            // Screen RAM: space (so unwritten cells aren't '@').
            for (int a = 0x0400; a <= 0x07E7; a++) m[a] = 0x20;

            // Clear render/display buffers so the first post-reset frame
            // cannot show stale game pixels if the raster thread is mid-
            // frame when reset is requested.
            Array.Clear(renderBuf, 0, renderBuf.Length);
            Array.Clear(displayBuf, 0, displayBuf.Length);

            // Drain any queued keystrokes from the previous session.
            while (keyQueue.TryDequeue(out _)) { }

            // Resume the raster/IRQ helper threads only after all reset
            // state is fully committed, and restart raster timing from
            // line 0 so the first post-reset frame is coherent.
            currentRasterLine = 0;
            rasterCompare = 0;
            rasterResyncPending = true;
            resetInProgress = false;
        }

        private bool OnIOWrite(ulong addr, byte value)
        {
            switch (addr)
            {
                case 0xD012:
                    rasterCompare = (rasterCompare & 0x100) | value;
                    return true;
                case 0xD011:
                    {
                        rasterCompare = (rasterCompare & 0xFF) | ((value & 0x80) << 1);
                        byte oldHigh = (byte)(cpu.memory.memory[0xD011] & 0x80);
                        cpu.memory.memory[0xD011] = (byte)((value & 0x7F) | oldHigh);
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
                        // CIA ICR mask register write semantics:
                        // bit7=1 sets mask bits in 0..4, bit7=0 clears them.
                        lock (cia1Lock)
                        {
                            byte bits = (byte)(value & 0x1F);
                            if ((value & 0x80) != 0)
                                cia1IcrMask |= bits;
                            else
                                cia1IcrMask = (byte)(cia1IcrMask & ~bits);
                        }
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
                            // CRA bit4 forces a load from latch into counter.
                            if ((value & 0x10) != 0)
                            {
                                cia1TimerACounter = cia1TimerALatch;
                                cpu.memory.memory[0xDC04] = (byte)(cia1TimerACounter & 0xFF);
                                cpu.memory.memory[0xDC05] = (byte)(cia1TimerACounter >> 8);
                            }

                            // Preserve control bits; force-load is a write strobe
                            // and always reads back clear.
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
                            // Preserve control bits; force-load is a write strobe
                            // and always reads back clear.
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
            }
            return false;
        }

        private byte OnIORead(ulong addr, byte fallback)
        {
            switch (addr)
            {
                case 0xDC00:
                    return ReadCia1PortA();
                case 0xDC01:
                    return ReadCia1PortB();
                case 0xDC02:
                    return cia1Ddra;
                case 0xDC03:
                    return cia1Ddrb;
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
                    return fallback;
            }
        }

        private byte ReadCia1PortA()
        {
            byte external = 0xFF;
            external &= ReadKeyboardColumns(cia1PortB, cia1Ddrb);
            external &= joystick2;
            return MergeCiaPortRead(cia1PortA, cia1Ddra, external);
        }

        private byte ReadCia1PortB()
        {
            byte external = ReadKeyboardColumns(cia1PortA, cia1Ddra);
            return MergeCiaPortRead(cia1PortB, cia1Ddrb, external);
        }

        // CIA ports behave like a wired matrix on the C64 bus: an output
        // bit driven high can still be pulled low by external sources.
        // This allows joystick reads to work even when KERNAL leaves DDRA
        // bits configured as outputs.
        private static byte MergeCiaPortRead(byte latch, byte ddr, byte external)
        {
            byte outBits = (byte)((latch & external) & ddr);
            byte inBits = (byte)(external & (byte)~ddr);
            return (byte)(outBits | inBits);
        }

        private byte ReadKeyboardColumns(byte rowLatch, byte rowDdr)
        {
            byte activeRows = (byte)(~rowLatch & rowDdr);
            if (activeRows == 0)
                return 0xFF;

            byte columns = 0xFF;
            for (int row = 0; row < keyboardMatrix.Length; row++)
            {
                if ((activeRows & (1 << row)) == 0)
                    continue;
                columns &= keyboardMatrix[row];
            }
            return columns;
        }

        private static int CountUnderflows(ref ushort counter, ushort latch, uint ticks, bool oneShot, ref byte control)
        {
            if (ticks == 0 || (control & 0x01) == 0)
                return 0;

            int underflows = 0;
            uint remaining = ticks;
            while (remaining > 0 && (control & 0x01) != 0)
            {
                // 6526 underflow occurs when the down-counter decrements
                // from 0 to FFFF, so it takes (counter + 1) ticks to hit
                // an underflow from any current counter value.
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
                // CNT pulses available this slice. Without full external
                // CNT wiring emulation, treat CNT as continuously pulsing
                // while high so CNT-clock modes don't deadlock software.
                uint cntPulses = cia1CntHigh ? cycles : 0u;

                // Timer A input mode (CRA bit5): 0=PHI2, 1=CNT.
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

                // Serial output mode can source CNT from Timer A underflow.
                // Keep the stronger of the synthetic external pulse train
                // and the CIA-generated pulses for compatibility.
                if ((cia1Cra & 0x40) != 0 && underA > 0)
                    cntPulses = Math.Max(cntPulses, (uint)underA);

                // Timer B input mode from CRB bits 6..5:
                // 00 = PHI2
                // 01 = CNT pulses
                // 10 = Timer-A underflow
                // 11 = Timer-A underflow while CNT high
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

        private void OnIOPostRead(ulong addr)
        {
        }

        // ------------------------------------------------------------
        //  SDL2 entry point / main loop
        // ------------------------------------------------------------

        public void Run()
        {
            if (SDL_Init(SDL_INIT_VIDEO) != 0)
                throw new Exception($"SDL_Init failed: {SDL_GetError()}");

            const int initialScale = 3;
            window = SDL_CreateWindow(
                "C64",
                SDL_WINDOWPOS_CENTERED, SDL_WINDOWPOS_CENTERED,
                FrameW * initialScale, FrameH * initialScale,
                SDL_WindowFlags.SDL_WINDOW_SHOWN | SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
            if (window == IntPtr.Zero)
                throw new Exception($"SDL_CreateWindow failed: {SDL_GetError()}");

            // Enable SDL_DROPFILE so users can drag a .prg / .bas onto
            // the running window instead of using the (blocking) stdin
            // prompt.
            SDL_EventState(SDL_EventType.SDL_DROPFILE, SDL_ENABLE);

            renderer = SDL_CreateRenderer(window, -1,
                SDL_RendererFlags.SDL_RENDERER_ACCELERATED |
                SDL_RendererFlags.SDL_RENDERER_PRESENTVSYNC);
            if (renderer == IntPtr.Zero)
                throw new Exception($"SDL_CreateRenderer failed: {SDL_GetError()}");

            // Use nearest-neighbour scaling so pixels stay crisp.
            SDL_SetHint(SDL_HINT_RENDER_SCALE_QUALITY, "0");
            // Keep the full C64 frame (playfield + border) aspect while resizing.
            SDL_RenderSetLogicalSize(renderer, FrameW, FrameH);

            texture = SDL_CreateTexture(renderer,
                SDL_PIXELFORMAT_ARGB8888,
                (int)SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING,
                ScreenW, ScreenH);
            if (texture == IntPtr.Zero)
                throw new Exception($"SDL_CreateTexture failed: {SDL_GetError()}");

            // Start emulator threads.
            var token = cts.Token;

            cpuThread = new Thread(() =>
            {
                try { cpu.Run(); }
                catch (Exception ex) { Debug.WriteLine($"CPU thread crashed: {ex}"); }
            })
            {
                IsBackground = true,
                Name = "6502",
                Priority = ThreadPriority.AboveNormal
            };
            cpuThread.Start();

            rasterThread = new Thread(() => RasterLoop(token))
            {
                IsBackground = true,
                Name = "VIC-II raster",
                Priority = ThreadPriority.AboveNormal,
            };
            rasterThread.Start();

            irqThread = new Thread(() => IrqLoop(token))
            {
                IsBackground = true,
                Name = "CIA-1 IRQ"
            };
            irqThread.Start();

            // Main UI loop on the calling (main) thread. SDL requires
            // event pumping from the thread that created the window on
            // macOS, so we keep everything graphics-related here.
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
                            if (HandleKeyDown(ev.key)) quit = true;
                            break;
                        case SDL_EventType.SDL_KEYUP:
                            HandleKeyUp(ev.key);
                            break;
                        case SDL_EventType.SDL_DROPFILE:
                            {
                                // SDL allocated the string; ideally we'd
                                // call SDL_free on it, but the binding
                                // we use doesn't expose it. The leak is
                                // a handful of bytes per drop - fine.
                                IntPtr p = ev.drop.file;
                                string? droppedPath = Marshal.PtrToStringUTF8(p);
                                if (!string.IsNullOrWhiteSpace(droppedPath))
                                    QueueLoad(droppedPath);
                                break;
                            }
                    }
                }

                // Drain any pending file loads on the main thread.
                while (pendingLoads.TryDequeue(out string? path))
                    DoLoad(path);

                uint now = SDL_GetTicks();
                if ((int)(now - nextDraw) >= 0)
                {
                    RedrawScreen();
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
            if (texture != IntPtr.Zero) { SDL_DestroyTexture(texture); texture = IntPtr.Zero; }
            if (renderer != IntPtr.Zero) { SDL_DestroyRenderer(renderer); renderer = IntPtr.Zero; }
            if (window != IntPtr.Zero) { SDL_DestroyWindow(window); window = IntPtr.Zero; }
            SDL_Quit();
        }

        private void RasterLoop(CancellationToken token)
        {
            long ticksPerLine = Stopwatch.Frequency / RasterLinesPerSecond;
            long next = Stopwatch.GetTimestamp() + ticksPerLine;
            int line = 0;
            byte[] mem = cpu.memory.memory;
            while (!token.IsCancellationRequested)
            {
                if (resetInProgress)
                {
                    line = 0;
                    currentRasterLine = 0;
                    next = Stopwatch.GetTimestamp() + ticksPerLine;
                    Thread.SpinWait(32);
                    continue;
                }

                if (rasterResyncPending)
                {
                    line = 0;
                    currentRasterLine = 0;
                    rasterResyncPending = false;
                    next = Stopwatch.GetTimestamp() + ticksPerLine;
                }

                currentRasterLine = line;

                mem[0xD012] = (byte)(line & 0xFF);
                byte d011 = mem[0xD011];
                d011 = (byte)((d011 & 0x7F) | (((line >> 8) & 1) << 7));
                mem[0xD011] = d011;

                // Raster IRQ fires on the line-matching edge. The CPU
                // services it asynchronously - by the time we render the
                // next few scanlines, the game's handler will likely have
                // already updated VIC state for the new region of screen,
                // so our per-line snapshot picks up those changes.
                if (line == rasterCompare)
                {
                    bool rasterIrqEnabled = (mem[0xD01A] & 0x01) != 0;
                    if (rasterIrqEnabled)
                    {
                        mem[0xD019] = (byte)(mem[0xD019] | 0x81);
                        cpu.InitiateIRQ(0xFFFE);
                    }
                }

                // Render this scanline if it's inside the visible
                // playfield. Lines outside are border - we just tick
                // through them without writing pixels.
                if (line >= VisibleTop && line <= VisibleBottom)
                {
                    RenderScanline(line - VisibleTop);
                }

                line++;
                if (line >= PalRasterLines)
                {
                    line = 0;
                    // End-of-frame: swap the just-rendered buffer with the
                    // one the UI thread reads. Tiny lock window.
                    lock (swapLock)
                    {
                        (renderBuf, displayBuf) = (displayBuf, renderBuf);
                    }
                }

                while (Stopwatch.GetTimestamp() < next)
                    Thread.SpinWait(1);
                next += ticksPerLine;
            }
        }

        // Dispatches per-scanline rendering based on the VIC-II mode at
        // the moment this line is being drawn.
        private void RenderScanline(int y)
        {
            byte[] mem = cpu.memory.memory;
            byte d011 = mem[0xD011];
            byte d016 = mem[0xD016];
            byte d018 = mem[0xD018];
            byte bg0 = (byte)(mem[0xD021] & 0x0F);
            byte bg1 = (byte)(mem[0xD022] & 0x0F);
            byte bg2 = (byte)(mem[0xD023] & 0x0F);
            byte bg3 = (byte)(mem[0xD024] & 0x0F);

            int bank = VicBankBase();
            int screenAddr = bank + ((d018 >> 4) & 0x0F) * 0x400;
            int charAddr = bank + ((d018 >> 1) & 0x07) * 0x800;

            bool screenOn = (d011 & 0x10) != 0;
            bool bmm = (d011 & 0x20) != 0;
            bool ecm = (d011 & 0x40) != 0;
            bool mcm = (d016 & 0x10) != 0;

            // Clear per-line masks before rendering this scanline.
            Array.Clear(fgLine, 0, fgLine.Length);
            Array.Clear(spriteLine, 0, spriteLine.Length);

            if (!screenOn)
                FillLineSolid(y, bg0);
            else if (bmm && mcm)
                RenderLineMulticolorBitmap(y, screenAddr, bank, d018, bg0);
            else if (bmm)
                RenderLineHiresBitmap(y, screenAddr, bank, d018);
            else if (ecm)
                RenderLineExtendedBgText(y, screenAddr, charAddr, bg0, bg1, bg2, bg3);
            else if (mcm)
                RenderLineMulticolorText(y, screenAddr, charAddr, bg0, bg1, bg2);
            else
                RenderLineStandardText(y, screenAddr, charAddr, bg0);

            if (screenOn)
                RenderSpritesScanline(y, screenAddr, bank);
        }

        private void IrqLoop(CancellationToken token)
        {
            long ticksPerTick = Stopwatch.Frequency / CiaTickHz;
            long next = Stopwatch.GetTimestamp() + ticksPerTick;
            long remCyclesNumerator = 0;
            long lastStamp = Stopwatch.GetTimestamp();
            while (!token.IsCancellationRequested)
            {
                DrainKeyboardQueue();

                long now = Stopwatch.GetTimestamp();
                long elapsedTicks = now - lastStamp;
                if (elapsedTicks < 0) elapsedTicks = 0;
                lastStamp = now;

                if (resetInProgress)
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

        private int VicBankBase()
        {
            int sel = cpu.memory.memory[0xDD00] & 0x03;
            return (3 - sel) * 0x4000;
        }

        private void RedrawScreen()
        {
            // Per-scanline rendering is done by the raster thread into
            // displayBuf (after frame swap). The UI's only job is to
            // upload that buffer into the streaming texture and present.
            lock (swapLock)
            {
                unsafe
                {
                    fixed (byte* p = displayBuf)
                    {
                        SDL_UpdateTexture(texture, IntPtr.Zero, (IntPtr)p, ScreenW * 4);
                    }
                }
            }

            // Border color is VIC register $D020.
            int border = C64Palette[cpu.memory.memory[0xD020] & 0x0F];
            SDL_SetRenderDrawColor(renderer, (byte)(border >> 16), (byte)(border >> 8), (byte)border, 255);
            SDL_RenderClear(renderer);

            SDL_Rect dst = new SDL_Rect
            {
                x = FramePlayfieldX,
                y = FramePlayfieldY,
                w = ScreenW,
                h = ScreenH,
            };
            SDL_RenderCopy(renderer, texture, IntPtr.Zero, ref dst);
            SDL_RenderPresent(renderer);
        }

        // Resolves where the renderer should fetch character bitmaps from.
        // The VIC-II maps the character ROM into char address $1000/$9000
        // (upper-case set) and $1800/$9800 (lower-case set), but only in
        // VIC banks 0 and 2. In banks 1 and 3 those addresses are plain RAM.
        private void ResolveCharSource(int charAddr, int bank, out byte[] src, out int baseIdx)
        {
            int withinBank = charAddr - bank;
            bool isShadowBank = (bank == 0x0000 || bank == 0x8000);
            if (isShadowBank && withinBank == 0x1000)
            {
                src = charRom;
                baseIdx = 0x0000;
            }
            else if (isShadowBank && withinBank == 0x1800)
            {
                src = charRom;
                baseIdx = 0x0800;
            }
            else
            {
                src = cpu.memory.memory;
                baseIdx = charAddr;
            }
        }

        private void FillLineSolid(int y, byte colorIdx)
        {
            int c = C64Palette[colorIdx & 0x0F];
            int p = y * ScreenW * 4;
            int end = p + ScreenW * 4;
            while (p < end)
            {
                renderBuf[p] = (byte)c;
                renderBuf[p + 1] = (byte)(c >> 8);
                renderBuf[p + 2] = (byte)(c >> 16);
                renderBuf[p + 3] = 0xFF;
                p += 4;
            }
        }

        private void RenderLineStandardText(int y, int screenAddr, int charAddr, byte bg)
        {
            byte[] mem = cpu.memory.memory;
            ResolveCharSource(charAddr, VicBankBase(), out byte[] cs, out int cb);
            int bgC = C64Palette[bg];

            int row = y / 8;
            int dy = y % 8;
            int rowBase = row * 40;
            int lineStart = y * ScreenW * 4;
            int fgBase = 0;

            for (int col = 0; col < 40; col++)
            {
                byte code = mem[screenAddr + rowBase + col];
                int fgC = C64Palette[mem[0xD800 + rowBase + col] & 0x0F];
                byte bits = cs[cb + code * 8 + dy];
                int p = lineStart + col * 32;
                for (int dx = 0; dx < 8; dx++)
                {
                    bool on = (bits & (0x80 >> dx)) != 0;
                    int c = on ? fgC : bgC;
                    renderBuf[p] = (byte)c;
                    renderBuf[p + 1] = (byte)(c >> 8);
                    renderBuf[p + 2] = (byte)(c >> 16);
                    renderBuf[p + 3] = 0xFF;
                    fgLine[fgBase + dx] = on;
                    p += 4;
                }
                fgBase += 8;
            }
        }

        private void RenderLineMulticolorText(int y, int screenAddr, int charAddr, byte bg0, byte bg1, byte bg2)
        {
            byte[] mem = cpu.memory.memory;
            ResolveCharSource(charAddr, VicBankBase(), out byte[] cs, out int cb);
            int bgC = C64Palette[bg0];
            int[] mcc = { bgC, C64Palette[bg1], C64Palette[bg2], 0 };

            int row = y / 8;
            int dy = y % 8;
            int rowBase = row * 40;
            int lineStart = y * ScreenW * 4;
            int fgBase = 0;

            for (int col = 0; col < 40; col++)
            {
                byte code = mem[screenAddr + rowBase + col];
                byte colRam = (byte)(mem[0xD800 + rowBase + col] & 0x0F);
                bool cellMc = (colRam & 0x08) != 0;
                int fgC = C64Palette[colRam & (cellMc ? 0x07 : 0x0F)];
                byte bits = cs[cb + code * 8 + dy];
                int p = lineStart + col * 32;

                if (cellMc)
                {
                    mcc[3] = fgC;
                    for (int pair = 0; pair < 4; pair++)
                    {
                        int pix = (bits >> ((3 - pair) * 2)) & 0x03;
                        int c = mcc[pix];
                        bool fg = pix == 3;
                        renderBuf[p] = (byte)c;
                        renderBuf[p + 1] = (byte)(c >> 8);
                        renderBuf[p + 2] = (byte)(c >> 16);
                        renderBuf[p + 3] = 0xFF;
                        renderBuf[p + 4] = (byte)c;
                        renderBuf[p + 5] = (byte)(c >> 8);
                        renderBuf[p + 6] = (byte)(c >> 16);
                        renderBuf[p + 7] = 0xFF;
                        fgLine[fgBase + pair * 2] = fg;
                        fgLine[fgBase + pair * 2 + 1] = fg;
                        p += 8;
                    }
                }
                else
                {
                    for (int dx = 0; dx < 8; dx++)
                    {
                        bool on = (bits & (0x80 >> dx)) != 0;
                        int c = on ? fgC : bgC;
                        renderBuf[p] = (byte)c;
                        renderBuf[p + 1] = (byte)(c >> 8);
                        renderBuf[p + 2] = (byte)(c >> 16);
                        renderBuf[p + 3] = 0xFF;
                        fgLine[fgBase + dx] = on;
                        p += 4;
                    }
                }
                fgBase += 8;
            }
        }

        private void RenderLineExtendedBgText(int y, int screenAddr, int charAddr, byte bg0, byte bg1, byte bg2, byte bg3)
        {
            byte[] mem = cpu.memory.memory;
            ResolveCharSource(charAddr, VicBankBase(), out byte[] cs, out int cb);
            int[] bgC = { C64Palette[bg0], C64Palette[bg1], C64Palette[bg2], C64Palette[bg3] };

            int row = y / 8;
            int dy = y % 8;
            int rowBase = row * 40;
            int lineStart = y * ScreenW * 4;
            int fgBase = 0;

            for (int col = 0; col < 40; col++)
            {
                byte code = mem[screenAddr + rowBase + col];
                int fgC = C64Palette[mem[0xD800 + rowBase + col] & 0x0F];
                int b = bgC[(code >> 6) & 0x03];
                byte bits = cs[cb + (code & 0x3F) * 8 + dy];
                int p = lineStart + col * 32;

                for (int dx = 0; dx < 8; dx++)
                {
                    bool on = (bits & (0x80 >> dx)) != 0;
                    int c = on ? fgC : b;
                    renderBuf[p] = (byte)c;
                    renderBuf[p + 1] = (byte)(c >> 8);
                    renderBuf[p + 2] = (byte)(c >> 16);
                    renderBuf[p + 3] = 0xFF;
                    fgLine[fgBase + dx] = on;
                    p += 4;
                }
                fgBase += 8;
            }
        }

        private void RenderLineHiresBitmap(int y, int screenAddr, int bank, byte d018)
        {
            byte[] mem = cpu.memory.memory;
            int bitmapAddr = bank + (((d018 & 0x08) != 0) ? 0x2000 : 0x0000);

            int row = y / 8;
            int dy = y % 8;
            int rowBase = row * 40;
            int lineStart = y * ScreenW * 4;
            int fgBase = 0;

            for (int col = 0; col < 40; col++)
            {
                byte clr = mem[screenAddr + rowBase + col];
                int fgC = C64Palette[(clr >> 4) & 0x0F];
                int bgC = C64Palette[clr & 0x0F];
                byte bits = mem[bitmapAddr + (rowBase * 8) + col * 8 + dy];
                int p = lineStart + col * 32;

                for (int dx = 0; dx < 8; dx++)
                {
                    bool on = (bits & (0x80 >> dx)) != 0;
                    int c = on ? fgC : bgC;
                    renderBuf[p] = (byte)c;
                    renderBuf[p + 1] = (byte)(c >> 8);
                    renderBuf[p + 2] = (byte)(c >> 16);
                    renderBuf[p + 3] = 0xFF;
                    fgLine[fgBase + dx] = on;
                    p += 4;
                }
                fgBase += 8;
            }
        }

        private void RenderLineMulticolorBitmap(int y, int screenAddr, int bank, byte d018, byte bg0)
        {
            byte[] mem = cpu.memory.memory;
            int bitmapAddr = bank + (((d018 & 0x08) != 0) ? 0x2000 : 0x0000);
            int bgC = C64Palette[bg0];

            int row = y / 8;
            int dy = y % 8;
            int rowBase = row * 40;
            int lineStart = y * ScreenW * 4;
            int fgBase = 0;

            for (int col = 0; col < 40; col++)
            {
                byte clr = mem[screenAddr + rowBase + col];
                int cFg1 = C64Palette[(clr >> 4) & 0x0F];
                int cFg2 = C64Palette[clr & 0x0F];
                int cFg3 = C64Palette[mem[0xD800 + rowBase + col] & 0x0F];
                byte bits = mem[bitmapAddr + (rowBase * 8) + col * 8 + dy];
                int p = lineStart + col * 32;

                for (int pair = 0; pair < 4; pair++)
                {
                    int pix = (bits >> ((3 - pair) * 2)) & 0x03;
                    int c = pix switch { 0 => bgC, 1 => cFg1, 2 => cFg2, _ => cFg3 };
                    bool fg = pix != 0;
                    renderBuf[p] = (byte)c;
                    renderBuf[p + 1] = (byte)(c >> 8);
                    renderBuf[p + 2] = (byte)(c >> 16);
                    renderBuf[p + 3] = 0xFF;
                    renderBuf[p + 4] = (byte)c;
                    renderBuf[p + 5] = (byte)(c >> 8);
                    renderBuf[p + 6] = (byte)(c >> 16);
                    renderBuf[p + 7] = 0xFF;
                    fgLine[fgBase + pair * 2] = fg;
                    fgLine[fgBase + pair * 2 + 1] = fg;
                    p += 8;
                }
                fgBase += 8;
            }
        }

        // ------------------------------------------------------------
        //  Sprites
        // ------------------------------------------------------------

        private void RenderSpritesScanline(int y, int screenAddr, int bank)
        {
            byte[] mem = cpu.memory.memory;
            byte enable = mem[0xD015];
            if (enable == 0) return;

            byte xExpand = mem[0xD01D];
            byte yExpand = mem[0xD017];
            byte multicolor = mem[0xD01C];
            byte priority = mem[0xD01B];
            byte xHigh = mem[0xD010];
            int mc1 = C64Palette[mem[0xD025] & 0x0F];
            int mc2 = C64Palette[mem[0xD026] & 0x0F];
            int pointerBase = screenAddr + 0x03F8;

            // Lower-numbered sprites have priority, so render highest-
            // numbered first and let lower ones paint over.
            for (int s = 7; s >= 0; s--)
            {
                int mask = 1 << s;
                if ((enable & mask) == 0) continue;

                int sx = mem[0xD000 + s * 2] | (((xHigh & mask) != 0) ? 0x100 : 0);
                int sy = mem[0xD000 + s * 2 + 1];
                int fbX = sx - 24;
                int fbY = sy - 50;

                bool xExp = (xExpand & mask) != 0;
                bool yExp = (yExpand & mask) != 0;
                bool mc = (multicolor & mask) != 0;
                bool behindBg = (priority & mask) != 0;
                int color = C64Palette[mem[0xD027 + s] & 0x0F];

                int spriteHeight = yExp ? 42 : 21;
                int spriteRow = y - fbY;
                if (spriteRow < 0 || spriteRow >= spriteHeight) continue;

                int row = yExp ? (spriteRow >> 1) : spriteRow;
                int spritePtr = mem[pointerBase + s];
                int dataAddr = bank + spritePtr * 64;
                int rowAddr = dataAddr + row * 3;
                int rowBits = (mem[rowAddr] << 16) | (mem[rowAddr + 1] << 8) | mem[rowAddr + 2];

                if (mc)
                {
                    for (int p = 0; p < 12; p++)
                    {
                        int code = (rowBits >> ((11 - p) * 2)) & 0x03;
                        if (code == 0) continue;
                        int c = code switch { 1 => mc1, 2 => color, _ => mc2 };
                        int basePix = p * 2 * (xExp ? 2 : 1);
                        int width = 2 * (xExp ? 2 : 1);
                        for (int w = 0; w < width; w++)
                            PaintSpritePixelLine(fbX + basePix + w, y, c, behindBg, s);
                    }
                }
                else
                {
                    for (int p = 0; p < 24; p++)
                    {
                        if ((rowBits & (1 << (23 - p))) == 0) continue;
                        int basePix = p * (xExp ? 2 : 1);
                        int width = xExp ? 2 : 1;
                        for (int w = 0; w < width; w++)
                            PaintSpritePixelLine(fbX + basePix + w, y, color, behindBg, s);
                    }
                }
            }
        }

        private void PaintSpritePixelLine(int x, int y, int color, bool behindBg, int spriteIdx)
        {
            if ((uint)x >= ScreenW) return;
            byte myBit = (byte)(1 << spriteIdx);
            byte[] mem = cpu.memory.memory;

            byte priorSprites = spriteLine[x];
            if (priorSprites != 0)
            {
                mem[0xD01E] |= (byte)(priorSprites | myBit);
                if ((mem[0xD01A] & 0x04) != 0 && (mem[0xD019] & 0x04) == 0)
                {
                    mem[0xD019] |= 0x84;
                    cpu.InitiateIRQ(0xFFFE);
                }
            }

            if (fgLine[x])
            {
                mem[0xD01F] |= myBit;
                if ((mem[0xD01A] & 0x02) != 0 && (mem[0xD019] & 0x02) == 0)
                {
                    mem[0xD019] |= 0x82;
                    cpu.InitiateIRQ(0xFFFE);
                }
            }

            spriteLine[x] |= myBit;

            if (behindBg && fgLine[x]) return;

            int p = (y * ScreenW + x) * 4;
            renderBuf[p] = (byte)color;
            renderBuf[p + 1] = (byte)(color >> 8);
            renderBuf[p + 2] = (byte)(color >> 16);
            renderBuf[p + 3] = 0xFF;
        }

        // ------------------------------------------------------------
        //  File load / save / reset
        // ------------------------------------------------------------

        // Hard reset: requests the CPU thread to re-run InitHardware and
        // re-point PC at the KERNAL reset vector at the next safe slice
        // boundary.
        private void HardReset()
        {
            resetInProgress = true;
            rasterResyncPending = true;
            rasterCompare = 0;

            // Drop any host-held input state so reset always starts from
            // a clean matrix/joystick condition.
            joystick2 = 0xFF;
            for (int i = 0; i < keyboardMatrix.Length; i++)
                keyboardMatrix[i] = 0xFF;

            cpu.RequestReset();
        }

        // Queue a file for loading. Safe to call from any thread; the
        // main loop performs the actual load.
        public void QueueLoad(string path) => pendingLoads.Enqueue(path);

        // SDL has no native file dialog. Prompt for a path on stdin from
        // a background thread so the main loop keeps pumping SDL events
        // and the emulator stays interactive. The path is then queued
        // for the main loop to process.
        private void LoadProgram()
        {
            Task.Run(() =>
            {
                try
                {
                    Console.Write("Load file (.prg, .bas, .txt) - path: ");
                    string? path = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(path)) return;
                    path = path.Trim().Trim('"', '\'');
                    pendingLoads.Enqueue(path);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Load prompt failed: {ex.Message}");
                }
            });
        }

        // Actually performs the load. Always called on the main thread.
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
                    LoadText(path);
                else
                    LoadPrg(path);
                Console.WriteLine($"Loaded {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Load failed: {ex.Message}");
            }
        }

        // Binary PRG format: first two bytes are the little-endian load
        // address, the rest is loaded verbatim. If the load address is the
        // standard BASIC area ($0801) we also update the BASIC pointers so
        // the user can immediately LIST / RUN.
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

            Array.Copy(data, 2, mem, loadAddr, progLen);

            // If loaded into the BASIC program area, fix up the BASIC
            // workspace pointers so the program is immediately runnable.
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

        // Plain-text BASIC source: each text line becomes a sequence of
        // PETSCII bytes followed by RETURN ($0D), enqueued into the same
        // keyboard buffer the user normally types into.
        private void LoadText(string path)
        {
            foreach (var rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.TrimEnd('\r');
                foreach (char ch in line)
                {
                    byte pet = AsciiCharToPetscii(ch);
                    if (pet != 0) keyQueue.Enqueue(pet);
                }
                keyQueue.Enqueue(0x0D); // RETURN
            }
        }

        private static byte AsciiCharToPetscii(char ch)
        {
            // C64 boots in upper-case/graphics mode, so map lower-case
            // source to PETSCII upper-case ($41..$5A).
            if (ch >= 'a' && ch <= 'z') return (byte)('A' + (ch - 'a'));
            if (ch >= ' ' && ch <= '~') return (byte)ch;
            return 0;
        }

        // Save the current BASIC program as a PRG file.
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

        // ------------------------------------------------------------
        //  Keyboard
        // ------------------------------------------------------------

        private bool caseModeUpper = true;

        private static readonly byte[] CtrlColours =
        {
            0x90, 0x05, 0x1C, 0x9F, 0x9C, 0x1E, 0x1F, 0x9E,
        };
        private static readonly byte[] CommodoreColours =
        {
            0x81, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0x9B,
        };
        private static readonly byte[] FunctionKeys =
        {
            0x85, 0x89, 0x86, 0x8A, 0x87, 0x8B, 0x88, 0x8C,
        };

        private byte ToPetscii(SDL_Keycode sym, SDL_Keymod mod)
        {
            bool shift = (mod & SDL_Keymod.KMOD_SHIFT) != 0;
            bool ctrl = (mod & SDL_Keymod.KMOD_CTRL) != 0;
            // Use Alt as the Commodore key (matches the original mapping
            // and works on macOS where the Command key cannot be relied
            // upon to deliver via SDL2).
            bool cbm = (mod & SDL_Keymod.KMOD_ALT) != 0;

            // Shift+Commodore toggles upper-case / mixed-case mode.
            // We emit it directly on any qualifying key event - in the
            // original Forms version it fired on the modifier key itself,
            // but SDL delivers a real KEYDOWN for the modifier so we
            // simply react to seeing both bits set without any other key.
            if (shift && cbm && (sym == SDL_Keycode.SDLK_LSHIFT || sym == SDL_Keycode.SDLK_RSHIFT ||
                                 sym == SDL_Keycode.SDLK_LALT || sym == SDL_Keycode.SDLK_RALT))
            {
                caseModeUpper = !caseModeUpper;
                return caseModeUpper ? (byte)0x8E : (byte)0x0E;
            }

            // F1..F8
            if (sym >= SDL_Keycode.SDLK_F1 && sym <= SDL_Keycode.SDLK_F8)
                return FunctionKeys[(int)(sym - SDL_Keycode.SDLK_F1)];

            // Letters - C64 boots upper-case so map to $41..$5A.
            if (sym >= SDL_Keycode.SDLK_a && sym <= SDL_Keycode.SDLK_z)
                return (byte)(0x41 + (int)(sym - SDL_Keycode.SDLK_a));

            // Digit row 1..8 with Ctrl / Commodore -> colour codes.
            if (sym >= SDL_Keycode.SDLK_1 && sym <= SDL_Keycode.SDLK_8)
            {
                int idx = (int)(sym - SDL_Keycode.SDLK_1);
                if (ctrl) return CtrlColours[idx];
                if (cbm) return CommodoreColours[idx];
            }
            // Digits 0..9 with shift -> US punctuation.
            if (sym >= SDL_Keycode.SDLK_0 && sym <= SDL_Keycode.SDLK_9)
            {
                int d = (int)(sym - SDL_Keycode.SDLK_0);
                if (!shift) return (byte)(0x30 + d);
                return d switch
                {
                    1 => (byte)'!',
                    2 => (byte)'"',
                    3 => (byte)'#',
                    4 => (byte)'$',
                    5 => (byte)'%',
                    6 => (byte)'&',
                    7 => (byte)'\'',
                    8 => (byte)'(',
                    9 => (byte)')',
                    _ => 0
                };
            }

            // Numeric keypad digits.
            if (sym >= SDL_Keycode.SDLK_KP_0 && sym <= SDL_Keycode.SDLK_KP_9)
                return (byte)(0x30 + (int)(sym - SDL_Keycode.SDLK_KP_0));

            return sym switch
            {
                SDL_Keycode.SDLK_SPACE => (byte)0x20,
                SDL_Keycode.SDLK_RETURN => (byte)0x0D,
                SDL_Keycode.SDLK_KP_ENTER => (byte)0x0D,
                SDL_Keycode.SDLK_BACKSPACE => (byte)0x14,
                SDL_Keycode.SDLK_TAB => (byte)0x20,
                SDL_Keycode.SDLK_ESCAPE => (byte)0x03,
                SDL_Keycode.SDLK_HOME => (byte)0x13,
                SDL_Keycode.SDLK_INSERT => (byte)0x94,
                SDL_Keycode.SDLK_DELETE => (byte)0x14,
                SDL_Keycode.SDLK_LEFT => (byte)0x9D,
                SDL_Keycode.SDLK_RIGHT => (byte)0x1D,
                SDL_Keycode.SDLK_UP => (byte)0x91,
                SDL_Keycode.SDLK_DOWN => (byte)0x11,
                SDL_Keycode.SDLK_PERIOD => shift ? (byte)'>' : (byte)'.',
                SDL_Keycode.SDLK_COMMA => shift ? (byte)'<' : (byte)',',
                SDL_Keycode.SDLK_SLASH => shift ? (byte)'?' : (byte)'/',
                SDL_Keycode.SDLK_SEMICOLON => shift ? (byte)':' : (byte)';',
                SDL_Keycode.SDLK_QUOTE => shift ? (byte)'"' : (byte)'\'',
                SDL_Keycode.SDLK_MINUS => shift ? (byte)'_' : (byte)'-',
                SDL_Keycode.SDLK_EQUALS => shift ? (byte)'+' : (byte)'=',
                SDL_Keycode.SDLK_LEFTBRACKET => shift ? (byte)'{' : (byte)'[',
                SDL_Keycode.SDLK_RIGHTBRACKET => shift ? (byte)'}' : (byte)']',
                SDL_Keycode.SDLK_BACKSLASH => shift ? (byte)'|' : (byte)'\\',
                _ => 0
            };
        }

        // Returns true when the host requested quit (e.g. Cmd/Ctrl+Q).
        private bool HandleKeyDown(SDL_KeyboardEvent ke)
        {
            // SDL repeats KEYDOWN while a key is held. Ignore auto-repeat
            // events so we don't flood the keyboard queue or fire the
            // shortcut actions repeatedly.
            if (ke.repeat != 0) return false;

            SDL_Keycode sym = ke.keysym.sym;
            SDL_Keymod mod = ke.keysym.mod;
            bool ctrl = (mod & SDL_Keymod.KMOD_CTRL) != 0;
            bool gui = (mod & SDL_Keymod.KMOD_GUI) != 0; // Cmd on macOS, Win key on Windows
            bool shift = (mod & SDL_Keymod.KMOD_SHIFT) != 0;
            bool alt = (mod & SDL_Keymod.KMOD_ALT) != 0;

            // Dedicated hard reset key that doesn't depend on modifiers.
            if (sym == SDL_Keycode.SDLK_F12)
            {
                HardReset();
                return false;
            }

            // Host-side shortcuts come first. Accept Ctrl+ on Windows/
            // Linux and Cmd+ on macOS.
            if ((ctrl || gui) && !shift && !alt)
            {
                switch (sym)
                {
                    case SDL_Keycode.SDLK_o: LoadProgram(); return false;
                    case SDL_Keycode.SDLK_s: SaveProgram(); return false;
                    case SDL_Keycode.SDLK_r:
                    case SDL_Keycode.SDLK_F12:
                        HardReset();
                        return false;
                    case SDL_Keycode.SDLK_q: return true;
                    case SDL_Keycode.SDLK_w: return true;
                }
            }

            // Joystick port 2 ($DC00). Arrows + Right-Ctrl set the
            // matching active-low bit. We intentionally still also map
            // arrow keys to PETSCII so BASIC editing keeps working.
            byte jmask = JoystickMaskFromKey(sym);
            if (jmask != 0)
            {
                joystick2 = (byte)(joystick2 & ~jmask);
            }

            UpdateKeyboardState(sym, true);
            return false;
        }

        private void HandleKeyUp(SDL_KeyboardEvent ke)
        {
            byte jmask = JoystickMaskFromKey(ke.keysym.sym);
            if (jmask != 0)
            {
                joystick2 = (byte)(joystick2 | jmask);
            }

            UpdateKeyboardState(ke.keysym.sym, false);
        }

        private void UpdateKeyboardState(SDL_Keycode sym, bool pressed)
        {
            switch (sym)
            {
                case SDL_Keycode.SDLK_LSHIFT:
                case SDL_Keycode.SDLK_RSHIFT:
                    SetMatrixKey(1, 7, pressed);
                    SetMatrixKey(6, 4, pressed);
                    return;
                case SDL_Keycode.SDLK_LCTRL:
                    SetMatrixKey(7, 2, pressed); // C64 CTRL key
                    return; // joystick-only to avoid game keyboard side-effects
                case SDL_Keycode.SDLK_RCTRL:
                    return; // joystick-only to avoid game keyboard side-effects
                case SDL_Keycode.SDLK_RALT:
                    return; // joystick fire only — no keyboard matrix entry
                case SDL_Keycode.SDLK_RETURN:
                case SDL_Keycode.SDLK_KP_ENTER:
                    SetMatrixKey(0, 1, pressed);
                    return;
                case SDL_Keycode.SDLK_BACKSPACE:
                case SDL_Keycode.SDLK_DELETE:
                    SetMatrixKey(0, 0, pressed);
                    return;
                case SDL_Keycode.SDLK_SPACE:
                    SetMatrixKey(7, 4, pressed);
                    return;
                case SDL_Keycode.SDLK_F1:
                    SetMatrixKey(0, 4, pressed);
                    return;
                case SDL_Keycode.SDLK_F3:
                    SetMatrixKey(0, 5, pressed);
                    return;
                case SDL_Keycode.SDLK_F5:
                    SetMatrixKey(0, 6, pressed);
                    return;
                case SDL_Keycode.SDLK_F7:
                    SetMatrixKey(0, 3, pressed);
                    return;
                case SDL_Keycode.SDLK_LEFT:
                    SetMatrixKey(7, 1, pressed);
                    return;
                case SDL_Keycode.SDLK_RIGHT:
                    SetMatrixKey(0, 2, pressed);
                    return;
                case SDL_Keycode.SDLK_UP:
                    return; // joystick-only to avoid keyboard side-effects in games
                case SDL_Keycode.SDLK_DOWN:
                    return; // joystick-only to avoid keyboard side-effects in games
            }

            if (sym >= SDL_Keycode.SDLK_a && sym <= SDL_Keycode.SDLK_z)
            {
                switch (sym)
                {
                    case SDL_Keycode.SDLK_a: SetMatrixKey(1, 2, pressed); return;
                    case SDL_Keycode.SDLK_b: SetMatrixKey(3, 4, pressed); return;
                    case SDL_Keycode.SDLK_c: SetMatrixKey(2, 4, pressed); return;
                    case SDL_Keycode.SDLK_d: SetMatrixKey(2, 2, pressed); return;
                    case SDL_Keycode.SDLK_e: SetMatrixKey(1, 6, pressed); return;
                    case SDL_Keycode.SDLK_f: SetMatrixKey(2, 5, pressed); return;
                    case SDL_Keycode.SDLK_g: SetMatrixKey(3, 2, pressed); return;
                    case SDL_Keycode.SDLK_h: SetMatrixKey(3, 5, pressed); return;
                    case SDL_Keycode.SDLK_i: SetMatrixKey(4, 1, pressed); return;
                    case SDL_Keycode.SDLK_j: SetMatrixKey(4, 2, pressed); return;
                    case SDL_Keycode.SDLK_k: SetMatrixKey(4, 5, pressed); return;
                    case SDL_Keycode.SDLK_l: SetMatrixKey(5, 2, pressed); return;
                    case SDL_Keycode.SDLK_m: SetMatrixKey(4, 4, pressed); return;
                    case SDL_Keycode.SDLK_n: SetMatrixKey(4, 7, pressed); return;
                    case SDL_Keycode.SDLK_o: SetMatrixKey(4, 6, pressed); return;
                    case SDL_Keycode.SDLK_p: SetMatrixKey(5, 1, pressed); return;
                    case SDL_Keycode.SDLK_q: SetMatrixKey(7, 6, pressed); return;
                    case SDL_Keycode.SDLK_r: SetMatrixKey(2, 1, pressed); return;
                    case SDL_Keycode.SDLK_s: SetMatrixKey(1, 5, pressed); return;
                    case SDL_Keycode.SDLK_t: SetMatrixKey(2, 6, pressed); return;
                    case SDL_Keycode.SDLK_u: SetMatrixKey(3, 6, pressed); return;
                    case SDL_Keycode.SDLK_v: SetMatrixKey(3, 7, pressed); return;
                    case SDL_Keycode.SDLK_w: SetMatrixKey(1, 1, pressed); return;
                    case SDL_Keycode.SDLK_x: SetMatrixKey(2, 7, pressed); return;
                    case SDL_Keycode.SDLK_y: SetMatrixKey(3, 1, pressed); return;
                    case SDL_Keycode.SDLK_z: SetMatrixKey(1, 4, pressed); return;
                }
            }

            if (sym >= SDL_Keycode.SDLK_0 && sym <= SDL_Keycode.SDLK_9)
            {
                switch (sym)
                {
                    case SDL_Keycode.SDLK_1: SetMatrixKey(7, 0, pressed); return;
                    case SDL_Keycode.SDLK_2: SetMatrixKey(7, 3, pressed); return;
                    case SDL_Keycode.SDLK_3: SetMatrixKey(1, 0, pressed); return;
                    case SDL_Keycode.SDLK_4: SetMatrixKey(1, 3, pressed); return;
                    case SDL_Keycode.SDLK_5: SetMatrixKey(2, 0, pressed); return;
                    case SDL_Keycode.SDLK_6: SetMatrixKey(2, 3, pressed); return;
                    case SDL_Keycode.SDLK_7: SetMatrixKey(3, 0, pressed); return;
                    case SDL_Keycode.SDLK_8: SetMatrixKey(3, 3, pressed); return;
                    case SDL_Keycode.SDLK_9: SetMatrixKey(4, 0, pressed); return;
                    case SDL_Keycode.SDLK_0: SetMatrixKey(4, 3, pressed); return;
                }
            }

            switch (sym)
            {
                case SDL_Keycode.SDLK_MINUS:
                    SetMatrixKey(5, 3, pressed);
                    return;
                case SDL_Keycode.SDLK_EQUALS:
                    SetMatrixKey(6, 5, pressed);
                    return;
                case SDL_Keycode.SDLK_COMMA:
                    SetMatrixKey(5, 7, pressed);
                    return;
                case SDL_Keycode.SDLK_PERIOD:
                    SetMatrixKey(5, 4, pressed);
                    return;
                case SDL_Keycode.SDLK_SLASH:
                    SetMatrixKey(6, 7, pressed);
                    return;
                case SDL_Keycode.SDLK_SEMICOLON:
                    SetMatrixKey(6, 2, pressed);
                    return;
                case SDL_Keycode.SDLK_QUOTE:
                    SetMatrixKey(6, 1, pressed);
                    return;
                case SDL_Keycode.SDLK_LEFTBRACKET:
                    SetMatrixKey(6, 0, pressed);
                    return;
                case SDL_Keycode.SDLK_RIGHTBRACKET:
                    SetMatrixKey(6, 3, pressed);
                    return;
                case SDL_Keycode.SDLK_BACKSLASH:
                    SetMatrixKey(5, 6, pressed);
                    return;
            }
        }

        private void SetMatrixKey(int row, int column, bool pressed)
        {
            byte mask = (byte)(1 << column);
            if (pressed)
                keyboardMatrix[row] = (byte)(keyboardMatrix[row] & ~mask);
            else
                keyboardMatrix[row] = (byte)(keyboardMatrix[row] | mask);
        }

        private static byte JoystickMaskFromKey(SDL_Keycode k) => k switch
        {
            SDL_Keycode.SDLK_UP => 0x01,
            SDL_Keycode.SDLK_DOWN => 0x02,
            SDL_Keycode.SDLK_LEFT => 0x04,
            SDL_Keycode.SDLK_RIGHT => 0x08,
            SDL_Keycode.SDLK_RCTRL => 0x10, // fire
            SDL_Keycode.SDLK_LCTRL => 0x10, // fire (MacBook-friendly)
            SDL_Keycode.SDLK_RALT => 0x10,  // fire alternate
            SDL_Keycode.SDLK_LALT => 0x10,  // fire alternate
            _ => 0
        };

        private void DrainKeyboardQueue()
        {
            while (!keyQueue.IsEmpty)
            {
                byte count = cpu.memory.ReadByte(0x00C6);
                if (count >= 10) return;
                if (!keyQueue.TryDequeue(out byte pet)) return;
                cpu.memory.WriteByte((ulong)(0x0277 + count), pet);
                cpu.memory.WriteByte(0x00C6, (byte)(count + 1));
            }
        }
    }
}
