// ============================================================================
// Project:     C64
// File:        Program.cs
// Description: Application entry point and main C64 emulator host wiring CPU,
//              memory, display, audio, input, storage, and KERNAL traps.
// Author:      James Booth
// Created:     2025
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Commodore 64 and related ROMs are property of their respective
//              rights holders. This emulator is for educational purposes only.
// ============================================================================

using C64.CPU;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static SDL2.SDL;

namespace C64
{
    /// <summary>
    /// Provides the process entry point and native-library resolution hooks used before the emulator starts.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            NativeLibrary.SetDllImportResolver(typeof(SDL2.SDL).Assembly, ResolveNativeLibrary);

            string? loadPath = null;

            foreach (string arg in args)
            {
                if (File.Exists(arg))
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

        /// <summary>
        /// Resolves the SDL2 native library from common platform-specific install locations before falling back to the system loader.
        /// </summary>
        /// <param name="libraryName">The native library name requested by the runtime.</param>
        /// <param name="assembly">The managed assembly requesting the native library.</param>
        /// <param name="searchPath">The optional runtime library search path flags.</param>
        /// <returns>The resolved native library handle, or IntPtr.Zero when this resolver does not handle the library.</returns>
        private static IntPtr ResolveNativeLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName != "SDL2") return IntPtr.Zero;

            string[] candidates;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                candidates = new[]
                {
                    "/opt/homebrew/lib/libSDL2.dylib",      /// Apple Silicon Homebrew
                    "/opt/homebrew/opt/sdl2/lib/libSDL2.dylib",
                    "/usr/local/lib/libSDL2.dylib",         /// Intel Homebrew / manual install
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

    /// <summary>
    /// Coordinates the CPU, VIC display, SID audio, CIA state, keyboard, IEC drive, REU, and datasette devices.
    /// This class owns the main event loop and the glue logic that maps C64 I/O reads and writes onto emulated hardware.
    /// </summary>
    internal sealed class C64Emulator : IDisposable
    {
        /// --- Constants ---
        private const int Clock_PAL = 985248;

        private const int KeyboardDrainPeriodCycles = 5000;

        private bool lastDatasetteReadHigh;

        private readonly CancellationTokenSource cts;
        private readonly CPU_6510 cpu;
        private readonly Display display;
        private readonly Keyboard keyboard;
        private readonly Sound sound;
        private readonly VirtualDrive1541 drive;
        private readonly Drive1541Emulator? fullDrive;
        private readonly IecBus iecBus;
        private readonly DatasetteDevice datasette;
        private readonly REU reu;
        private readonly object cia1Lock = new();
        private readonly object cia2Lock = new();
        private readonly ConcurrentQueue<(string Path, bool AutoRun)> pendingLoads = new();
        private string? lastHostLoadedFile;
        private byte cia1PortA = 0xFF;
        private byte cia1PortB = 0xFF;
        private byte cia1Ddra = 0x00;
        private byte cia1Ddrb = 0x00;
        private ushort cia1TimerALatch = 0xFFFF;
        private ushort cia1TimerACounter = 0xFFFF;
        private ushort cia1TimerBLatch = 0xFFFF;
        private ushort cia1TimerBCounter = 0xFFFF;
        private byte cia1Cra;
        private byte cia1Crb;
        private byte cia1Sdr;
        private byte cia1IcrMask;
        private byte cia1IcrStatus;
        private byte cia1TodTenths;
        private byte cia1TodSeconds;
        private byte cia1TodMinutes;
        private byte cia1TodHours;
        private byte cia1AlarmTenths;
        private byte cia1AlarmSeconds;
        private byte cia1AlarmMinutes;
        private byte cia1AlarmHours;
        private byte cia1TodLatchTenths;
        private byte cia1TodLatchSeconds;
        private byte cia1TodLatchMinutes;
        private byte cia1TodLatchHours;
        private bool cia1TodLatched;
        private long cia1TodNumerator;
        private int cia1TodSubTicks;
        private bool cia1SpInHigh = true;
        private bool cia1CntInHigh = true;
        private bool cia1CntHighSeen = true;
        private bool cia1SpOutHigh = true;
        private uint cia1CntPulseBudget;
        private byte cia1SerialShiftReg;
        private int cia1SerialBitsRemaining;
        private bool cia1SerialOutputActive;
        private bool cia1SerialDataPending;
        private byte cia1SerialInShiftReg;
        private int cia1SerialInBits;
        private bool cia1Pa6PrescalerPrevState = true;
        private int keyboardDrainCycleBudget;
        private byte cia2PortA = 0x17;
        private byte cia2Ddra = 0x3F;
        private ushort cia2TimerALatch = 0xFFFF;
        private ushort cia2TimerACounter = 0xFFFF;
        private ushort cia2TimerBLatch = 0xFFFF;
        private ushort cia2TimerBCounter = 0xFFFF;
        private byte cia2Cra;
        private byte cia2Crb;
        private byte cia2Sdr;
        private byte cia2IcrMask;
        private byte cia2IcrStatus;
        private byte cia2TodTenths;
        private byte cia2TodSeconds;
        private byte cia2TodMinutes;
        private byte cia2TodHours;
        private byte cia2AlarmTenths;
        private byte cia2AlarmSeconds;
        private byte cia2AlarmMinutes;
        private byte cia2AlarmHours;
        private byte cia2TodLatchTenths;
        private byte cia2TodLatchSeconds;
        private byte cia2TodLatchMinutes;
        private byte cia2TodLatchHours;
        private bool cia2TodLatched;
        private long cia2TodNumerator;
        private int cia2TodSubTicks;
        private bool cia2SpInHigh = true;
        private bool cia2CntInHigh = true;
        private bool cia2CntHighSeen = true;
        private bool cia2SpOutHigh = true;
        private uint cia2CntPulseBudget;
        private byte cia2SerialShiftReg;
        private int cia2SerialBitsRemaining;
        private bool cia2SerialOutputActive;
        private bool cia2SerialDataPending;
        private byte cia2SerialInShiftReg;
        private int cia2SerialInBits;
        private static readonly bool Native1541LoadEnabled =
            string.Equals(Environment.GetEnvironmentVariable("C64_1541_NATIVE_LOAD"), "1", StringComparison.Ordinal);

        /// <summary>Initializes a new C64Emulator instance.</summary>
        public C64Emulator()
        {
            cts = new System.Threading.CancellationTokenSource();
            cpu = new CPU_6510(Clock_PAL);
            cpu.OnCyclesExecuted = OnCpuCyclesExecuted;
            cpu.memory.LoadBankedROM(Path.Combine("ROMS", "basic.bin"), Memory.BankSlot.Basic);
            cpu.memory.LoadBankedROM(Path.Combine("ROMS", "kernal.bin"), Memory.BankSlot.Kernal);
            cpu.memory.LoadBankedROM(Path.Combine("ROMS", "characters.bin"), Memory.BankSlot.Char);
            display = new Display(cpu);
            keyboard = new Keyboard(cpu);
            sound = new Sound();
            drive = new VirtualDrive1541();
            fullDrive = IsFull1541Enabled()
                ? Drive1541Emulator.TryCreate(
                    Path.Combine("ROMS", "1541.bin"),
                    Path.Combine("ROMS", "dos1541.bin"),
                    Path.Combine("ROMS", "1541-II.bin"))
                : null;
            if (fullDrive is not null)
                Console.WriteLine("1541 drive ROM found; full drive emulation path enabled.");
            iecBus = new IecBus(drive, fullDrive);
            iecBus.OnDriveActivity = display.PulseDriveActivity;
            datasette = new DatasetteDevice();
            reu = new REU(128);
            reu.OnIrqRequest = () => cpu.InitiateIRQ(0xFFFE);
            keyboard.OnHardReset = HardResetFromKeyboard;
            keyboard.OnLoad = LoadProgram;
            keyboard.OnSave = SaveProgram;
            keyboard.OnRestoreNmi = TriggerRestoreNmi;
            keyboard.OnScreenshot = () =>
            {
                display.TakeScreenshot();
            };
            keyboard.OnViewportScreenshot = () =>
            {
                display.TakeViewportScreenshot();
            };
            keyboard.OnSpriteDebugScreenshot = () =>
            {
                display.TakeSpriteDebugScreenshot();
            };
            keyboard.OnToggleMute = ToggleMute;
            keyboard.OnTogglePause = TogglePause;
            keyboard.OnToggleJoystickPort = ToggleJoystickPort;
            keyboard.OnSelectAudioDevice = SelectAudioDevice;
            display.JoystickPortOverlay = keyboard.ActiveJoystickPort;

            byte[] kernal = cpu.memory.GetBankedROM(Memory.BankSlot.Kernal)!;
            kernal[0xFCF5 - 0xE000] = 0xEA;
            kernal[0xFCF6 - 0xE000] = 0xEA;
            kernal[0xFCF7 - 0xE000] = 0xEA;

            InitHardware();

            cpu.OnReset = InitHardware;

            display.RasterCompare = 0;

            cpu.memory.OnIOWrite = OnIOWrite;
            cpu.memory.OnIORead = OnIORead;
        }

        /// <summary>Gets whether the full drive ROM path should be used when a 1541 ROM is available.</summary>
        private static bool IsFull1541Enabled()
        {
            string? setting = Environment.GetEnvironmentVariable("C64_1541_FULL");
            return !string.Equals(setting, "0", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(setting, "false", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resets RAM, ROM banking, device registers, CIA timers, display state, SID state, IEC lines, datasette state, and host-load metadata.
        /// </summary>
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
                cia1Sdr = 0x00;
                cia1IcrMask = 0x00;
                cia1IcrStatus = 0x00;
                cia1TodTenths = 0x00;
                cia1TodSeconds = 0x00;
                cia1TodMinutes = 0x00;
                cia1TodHours = 0x01;
                cia1AlarmTenths = 0x00;
                cia1AlarmSeconds = 0x00;
                cia1AlarmMinutes = 0x00;
                cia1AlarmHours = 0x01;
                cia1TodLatchTenths = 0x00;
                cia1TodLatchSeconds = 0x00;
                cia1TodLatchMinutes = 0x00;
                cia1TodLatchHours = 0x01;
                cia1TodLatched = false;
                cia1TodNumerator = 0;
                cia1TodSubTicks = 0;
                cia1SpInHigh = true;
                cia1CntInHigh = true;
                cia1CntHighSeen = true;
                cia1SpOutHigh = true;
                cia1CntPulseBudget = 0;
                cia1Pa6PrescalerPrevState = true;
                cia1SerialShiftReg = 0;
                cia1SerialBitsRemaining = 0;
                cia1SerialOutputActive = false;
                cia1SerialDataPending = false;
                cia1SerialInShiftReg = 0;
                cia1SerialInBits = 0;
            }
            keyboardDrainCycleBudget = 0;
            m[0xDC04] = 0xFF;
            m[0xDC05] = 0xFF;
            m[0xDC06] = 0xFF;
            m[0xDC07] = 0xFF;
            m[0xDC08] = cia1TodTenths;
            m[0xDC09] = cia1TodSeconds;
            m[0xDC0A] = cia1TodMinutes;
            m[0xDC0B] = cia1TodHours;
            m[0xDC0C] = cia1Sdr;
            m[0xDC0D] = 0x00;
            m[0xDC0E] = 0x00;
            m[0xDC0F] = 0x00;

            m[0x0281] = 0x00; m[0x0282] = 0x08; /// MEMSTR = $0800
            m[0x0283] = 0x00; m[0x0284] = 0xA0; /// MEMSIZ = $A000
            m[0x0288] = 0x04;                   /// screen page = $0400

            m[0xDC00] = 0xFF;
            m[0xDC01] = 0xFF;

            cia2PortA = 0x17;
            cia2Ddra = 0x3F;
            m[0xDD00] = 0x17;
            m[0xDD02] = 0x3F;
            iecBus.UpdateHostCia2PortA(m[0xDD00], m[0xDD02]);
            iecBus.ResetDrive();
            lock (cia2Lock)
            {
                cia2TimerALatch = 0xFFFF;
                cia2TimerACounter = 0xFFFF;
                cia2TimerBLatch = 0xFFFF;
                cia2TimerBCounter = 0xFFFF;
                cia2Cra = 0x00;
                cia2Crb = 0x00;
                cia2Sdr = 0x00;
                cia2IcrMask = 0x00;
                cia2IcrStatus = 0x00;
                cia2TodTenths = 0x00;
                cia2TodSeconds = 0x00;
                cia2TodMinutes = 0x00;
                cia2TodHours = 0x01;
                cia2AlarmTenths = 0x00;
                cia2AlarmSeconds = 0x00;
                cia2AlarmMinutes = 0x00;
                cia2AlarmHours = 0x01;
                cia2TodLatchTenths = 0x00;
                cia2TodLatchSeconds = 0x00;
                cia2TodLatchMinutes = 0x00;
                cia2TodLatchHours = 0x01;
                cia2TodLatched = false;
                cia2TodNumerator = 0;
                cia2TodSubTicks = 0;
                cia2SpInHigh = true;
                cia2CntInHigh = true;
                cia2CntHighSeen = true;
                cia2SpOutHigh = true;
                cia2CntPulseBudget = 0;
                cia2SerialShiftReg = 0;
                cia2SerialBitsRemaining = 0;
                cia2SerialOutputActive = false;
                cia2SerialDataPending = false;
                cia2SerialInShiftReg = 0;
                cia2SerialInBits = 0;
            }
            m[0xDD04] = 0xFF;
            m[0xDD05] = 0xFF;
            m[0xDD06] = 0xFF;
            m[0xDD07] = 0xFF;
            m[0xDD08] = cia2TodTenths;
            m[0xDD09] = cia2TodSeconds;
            m[0xDD0A] = cia2TodMinutes;
            m[0xDD0B] = cia2TodHours;
            m[0xDD0C] = cia2Sdr;
            m[0xDD0D] = 0x00;
            m[0xDD0E] = 0x00;
            m[0xDD0F] = 0x00;

            m[0xD011] = 0x1B; /// DEN, RSEL, YSCROLL=3
            m[0xD016] = 0xC8; /// (top bits), CSEL, XSCROLL=0
            m[0xD018] = 0x14; /// screen $0400, char ROM shadow $1000
            m[0xD020] = 0x0E; /// border  = light blue
            m[0xD021] = 0x06; /// bg 0    = blue
            m[0xD022] = 0x01; /// bg 1    = white
            m[0xD023] = 0x02; /// bg 2    = red
            m[0xD024] = 0x03; /// bg 3    = cyan

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
            lastDatasetteReadHigh = datasette.ReadHigh;

            display.EndReset();
        }

        /// <summary>
        /// Routes CPU writes in the I/O window to VIC, SID, CIA, REU, datasette, IEC, and color-RAM handlers.
        /// Returns true when the emulated device consumed the write.
        /// </summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <param name="value">The value supplied to the operation.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        private bool OnIOWrite(ulong addr, byte value)
        {
            switch (addr)
            {
                case 0xD012:
                    display.RasterCompare = (display.RasterCompare & 0x100) | value;
                    return true;

                case 0xD011:
                    {
                        byte oldD011 = cpu.memory.memory[0xD011];
                        display.RasterCompare = (display.RasterCompare & 0xFF) | ((value & 0x80) << 1);
                        byte oldHigh = (byte)(oldD011 & 0x80);
                        byte newVal = (byte)((value & 0x7F) | oldHigh);
                        cpu.memory.memory[0xD011] = newVal;
                        display.RecordRasterWrite(addr, oldD011, newVal);
                        display.RefreshCurrentRasterLine();
                        return true;
                    }
                case 0xD016:
                    {
                        WriteVicRenderRegister(addr, value);
                        return true;
                    }
                case 0xD018:
                    {
                        WriteVicRenderRegister(addr, value);
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

                            cia1Cra = NormalizeCiaControlWrite(value);
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
                case 0xDC08:
                case 0xDC09:
                case 0xDC0A:
                case 0xDC0B:
                    {
                        lock (cia1Lock)
                        {
                            bool writeAlarm = (cia1Crb & 0x80) != 0;
                            WriteCiaTodRegister((int)(addr - 0xDC08), value, writeAlarm,
                                ref cia1TodTenths, ref cia1TodSeconds, ref cia1TodMinutes, ref cia1TodHours,
                                ref cia1AlarmTenths, ref cia1AlarmSeconds, ref cia1AlarmMinutes, ref cia1AlarmHours,
                                ref cia1TodLatched);
                            UpdateCiaTodMirror(0xDC08, cia1TodTenths, cia1TodSeconds, cia1TodMinutes, cia1TodHours);
                        }
                        return true;
                    }
                case 0xDC0C:
                    {
                        lock (cia1Lock)
                        {
                            cia1Sdr = value;
                            cia1SerialDataPending = true;
                            cpu.memory.memory[0xDC0C] = cia1Sdr;
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

                            cia1Crb = NormalizeCiaControlWrite(value);
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
                    iecBus.UpdateHostCia2PortA(cia2PortA, cia2Ddra);
                    return true;

                case 0xDD02:
                    cia2Ddra = (byte)(value & 0x3F);
                    cpu.memory.memory[addr] = value;
                    iecBus.UpdateHostCia2PortA(cia2PortA, cia2Ddra);
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

                case 0xDD08:
                case 0xDD09:
                case 0xDD0A:
                case 0xDD0B:
                    {
                        lock (cia2Lock)
                        {
                            bool writeAlarm = (cia2Crb & 0x80) != 0;
                            WriteCiaTodRegister((int)(addr - 0xDD08), value, writeAlarm,
                                ref cia2TodTenths, ref cia2TodSeconds, ref cia2TodMinutes, ref cia2TodHours,
                                ref cia2AlarmTenths, ref cia2AlarmSeconds, ref cia2AlarmMinutes, ref cia2AlarmHours,
                                ref cia2TodLatched);
                            UpdateCiaTodMirror(0xDD08, cia2TodTenths, cia2TodSeconds, cia2TodMinutes, cia2TodHours);
                        }
                        return true;
                    }
                case 0xDD0C:
                    {
                        lock (cia2Lock)
                        {
                            cia2Sdr = value;
                            cia2SerialDataPending = true;
                            cpu.memory.memory[0xDD0C] = cia2Sdr;
                        }
                        return true;
                    }
                case 0xDD0D:
                    {
                        bool raiseNmi = false;
                        lock (cia2Lock)
                        {
                            byte bits = (byte)(value & 0x1F);
                            if ((value & 0x80) != 0)
                                cia2IcrMask |= bits;
                            else
                                cia2IcrMask = (byte)(cia2IcrMask & ~bits);

                            if ((cia2IcrStatus & cia2IcrMask & 0x1F) != 0)
                            {
                                bool wasSet = (cia2IcrStatus & 0x80) != 0;
                                cia2IcrStatus |= 0x80;
                                if (!wasSet) raiseNmi = true;
                            }
                            else
                            {
                                cia2IcrStatus = (byte)(cia2IcrStatus & 0x7F);
                            }

                            cpu.memory.memory[0xDD0D] = cia2IcrStatus;
                        }

                        if (raiseNmi)
                            cpu.InitiateNMI(0xFFFA);
                        return true;
                    }
                case 0xDD0E:
                    lock (cia2Lock)
                    {
                        if ((value & 0x10) != 0)
                        {
                            cia2TimerACounter = cia2TimerALatch;
                            cpu.memory.memory[0xDD04] = (byte)(cia2TimerACounter & 0xFF);
                            cpu.memory.memory[0xDD05] = (byte)(cia2TimerACounter >> 8);
                        }
                        cia2Cra = NormalizeCiaControlWrite(value);
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
                        cia2Crb = NormalizeCiaControlWrite(value);
                        cpu.memory.memory[0xDD0F] = cia2Crb;
                    }
                    return true;
            }

            if (IsVicRenderRegister(addr))
            {
                WriteVicRenderRegister(addr, value);
                return true;
            }

            if (addr >= 0xDF00 && addr <= 0xDFFF)
            {
                reu.Write((int)addr, value);
                cpu.memory.memory[addr] = value;
                return true;
            }

            /// SID registers are mirrored across $D400-$D7FF in 32-byte blocks.
            /// Accept mirrored writes so routines using alternate SID mirrors
            /// (common in some games/effects code) are not lost.
            if (addr >= 0xD400 && addr <= 0xD7FF)
            {
                int sidReg = (int)((addr - 0xD400) & 0x1F);
                sound.WriteRegister(sidReg, value);
                cpu.memory.memory[addr] = value;
                return true;
            }

            return false;
        }

        /// <summary>Writes vic render register.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <param name="value">The value supplied to the operation.</param>
        private void WriteVicRenderRegister(ulong addr, byte value)
        {
            byte oldValue = cpu.memory.memory[addr];
            cpu.memory.memory[addr] = value;
            display.RecordRasterWrite(addr, oldValue, value);
            if (!IsVicSpriteRenderRegister(addr))
                display.RefreshCurrentRasterLine();
        }

        /// <summary>Determines whether vic render register.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        private static bool IsVicRenderRegister(ulong addr)
        {
            return addr switch
            {
                >= 0xD000 and <= 0xD010 => true, /// sprite positions and X high bits
                0xD015 => true,                  /// sprite enable
                0xD017 => true,                  /// sprite Y expansion
                0xD01B => true,                  /// sprite/background priority
                0xD01C => true,                  /// sprite multicolor enable
                0xD01D => true,                  /// sprite X expansion
                >= 0xD020 and <= 0xD02E => true, /// border/background/sprite colors
                _ => false
            };
        }

        /// <summary>Determines whether vic sprite render register.</summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        private static bool IsVicSpriteRenderRegister(ulong addr)
        {
            return addr switch
            {
                >= 0xD000 and <= 0xD010 => true,
                0xD015 => true,
                0xD017 => true,
                0xD01B => true,
                0xD01C => true,
                0xD01D => true,
                >= 0xD025 and <= 0xD02E => true,
                _ => false
            };
        }

        /// <summary>
        /// Resolves CPU reads from the I/O window, including VIC collision latches, SID readback, CIA ports, IEC lines, and datasette signals.
        /// </summary>
        /// <param name="addr">The emulated address to access.</param>
        /// <param name="fallback">The fallback value to return when no device handles the read.</param>
        /// <returns>The byte value produced by the operation.</returns>
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
                        byte external = iecBus.BuildExternalCia2PortA(0xFF);
                        byte outputLatchBits = (byte)(cia2PortA & cia2Ddra);
                        byte inputPinBits = (byte)(external & (byte)~cia2Ddra);
                        byte v = (byte)(outputLatchBits | inputPinBits);
                        return v;
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

                case 0xDD08:
                case 0xDD09:
                case 0xDD0A:
                case 0xDD0B:
                    lock (cia2Lock)
                        return ReadCiaTodRegister((int)(addr - 0xDD08),
                            ref cia2TodLatched,
                            ref cia2TodLatchTenths, ref cia2TodLatchSeconds, ref cia2TodLatchMinutes, ref cia2TodLatchHours,
                            cia2TodTenths, cia2TodSeconds, cia2TodMinutes, cia2TodHours);

                case 0xDD0C:
                    lock (cia2Lock)
                        return cia2Sdr;

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
                case 0xDC08:
                case 0xDC09:
                case 0xDC0A:
                case 0xDC0B:
                    {
                        lock (cia1Lock)
                            return ReadCiaTodRegister((int)(addr - 0xDC08),
                                ref cia1TodLatched,
                                ref cia1TodLatchTenths, ref cia1TodLatchSeconds, ref cia1TodLatchMinutes, ref cia1TodLatchHours,
                                cia1TodTenths, cia1TodSeconds, cia1TodMinutes, cia1TodHours);
                    }
                case 0xDC0C:
                    {
                        lock (cia1Lock)
                            return cia1Sdr;
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
                        lock (cia2Lock)
                        {
                            byte value = cia2IcrStatus;
                            cia2IcrStatus = 0x00;
                            cpu.memory.memory[0xDD0D] = 0x00;
                            return value;
                        }
                    }
                default:
                    if (addr >= 0xDF00 && addr <= 0xDFFF)
                        return reu.Read((int)addr);

                    /// SID readback registers are mirrored across $D400-$D7FF.
                    /// We only provide meaningful values for $19-$1C (POT/POT/OSC3/ENV3).
                    if (addr >= 0xD400 && addr <= 0xD7FF)
                    {
                        int sidRegister = (int)((addr - 0xD400) & 0x1F);
                        return sound.ReadRegister(sidRegister);
                    }
                    return fallback;
            }
        }

        /// <summary>Normalizes cia control write.</summary>
        /// <param name="value">The value supplied to the operation.</param>
        /// <returns>The byte value produced by the operation.</returns>
        private static byte NormalizeCiaControlWrite(byte value)
        {
            /// Bit 4 force-loads the timer latch into the counter and then
            /// reads back clear; other control bits remain latched.
            return (byte)(value & 0xEF);
        }

        /// <summary>Reads cia1 port a.</summary>
        /// <returns>The byte value produced by the operation.</returns>
        private byte ReadCia1PortA()
        {
            byte external = 0xFF;
            external &= keyboard.ScanMatrix(cia1PortB, cia1Ddrb);
            external &= keyboard.Joystick2;
            return MergeCiaPortRead(cia1PortA, cia1Ddra, external);
        }

        /// <summary>Reads cia1 port b.</summary>
        /// <returns>The byte value produced by the operation.</returns>
        private byte ReadCia1PortB()
        {
            byte external = keyboard.ScanMatrix(cia1PortA, cia1Ddra);
            external &= keyboard.Joystick1;
            return MergeCiaPortRead(cia1PortB, cia1Ddrb, external);
        }

        /// <summary>Merges cia port read.</summary>
        /// <param name="latch">The timer latch value used when the counter reloads.</param>
        /// <param name="ddr">The CIA data direction register value.</param>
        /// <param name="external">The external input bits visible on the port.</param>
        /// <returns>The byte value produced by the operation.</returns>
        private static byte MergeCiaPortRead(byte latch, byte ddr, byte external)
        {
            byte outBits = (byte)((latch & external) & ddr);
            byte inBits = (byte)(external & (byte)~ddr);
            return (byte)(outBits | inBits);
        }

        /// External serial/user-port model entry points. CNT rising edges drive
        /// serial input mode and timer CNT-counting modes.

        /// <summary>Sets cia1 serial pins.</summary>
        /// <param name="spHigh">Whether the serial SP line is high.</param>
        /// <param name="cntHigh">Whether the serial CNT line is high.</param>
        public void SetCia1SerialPins(bool spHigh, bool cntHigh)
        {
            bool raiseIrq = false;
            lock (cia1Lock)
            {
                bool prevCnt = cia1CntInHigh;
                cia1SpInHigh = spHigh;
                cia1CntInHigh = cntHigh;
                if (cntHigh)
                    cia1CntHighSeen = true;
                if (!prevCnt && cntHigh)
                    OnCia1CntRisingEdge(ref raiseIrq);
            }

            if (raiseIrq)
                cpu.InitiateIRQ(0xFFFE);
        }

        /// <summary>Sets cia2 serial pins.</summary>
        /// <param name="spHigh">Whether the serial SP line is high.</param>
        /// <param name="cntHigh">Whether the serial CNT line is high.</param>
        public void SetCia2SerialPins(bool spHigh, bool cntHigh)
        {
            bool raiseNmi = false;
            lock (cia2Lock)
            {
                bool prevCnt = cia2CntInHigh;
                cia2SpInHigh = spHigh;
                cia2CntInHigh = cntHigh;
                if (cntHigh)
                    cia2CntHighSeen = true;
                if (!prevCnt && cntHigh)
                    OnCia2CntRisingEdge(ref raiseNmi);
            }

            if (raiseNmi)
                cpu.InitiateNMI(0xFFFA);
        }

        /// <summary>Handles cia1 cnt rising edge.</summary>
        /// <param name="raiseIrq">Set to true when the operation should raise a CIA1 IRQ.</param>
        private void OnCia1CntRisingEdge(ref bool raiseIrq)
        {
            cia1CntPulseBudget++;

            /// Serial input mode (CRA bit 6 clear): sample SP on CNT rising edges.
            if ((cia1Cra & 0x40) != 0)
                return;

            cia1SerialInShiftReg = (byte)((cia1SerialInShiftReg << 1) | (cia1SpInHigh ? 1 : 0));
            cia1SerialInBits++;
            if (cia1SerialInBits < 8)
                return;

            cia1SerialInBits = 0;
            cia1Sdr = cia1SerialInShiftReg;
            cpu.memory.memory[0xDC0C] = cia1Sdr;

            cia1IcrStatus |= 0x08;
            if ((cia1IcrMask & 0x08) != 0)
            {
                bool wasSet = (cia1IcrStatus & 0x80) != 0;
                cia1IcrStatus |= 0x80;
                if (!wasSet)
                    raiseIrq = true;
            }
        }

        /// <summary>Handles cia2 cnt rising edge.</summary>
        /// <param name="raiseNmi">Set to true when the operation should raise a CIA2 NMI.</param>
        private void OnCia2CntRisingEdge(ref bool raiseNmi)
        {
            cia2CntPulseBudget++;

            if ((cia2Cra & 0x40) != 0)
                return;

            cia2SerialInShiftReg = (byte)((cia2SerialInShiftReg << 1) | (cia2SpInHigh ? 1 : 0));
            cia2SerialInBits++;
            if (cia2SerialInBits < 8)
                return;

            cia2SerialInBits = 0;
            cia2Sdr = cia2SerialInShiftReg;
            cpu.memory.memory[0xDD0C] = cia2Sdr;

            cia2IcrStatus |= 0x08;
            if ((cia2IcrMask & 0x08) != 0)
            {
                bool wasSet = (cia2IcrStatus & 0x80) != 0;
                cia2IcrStatus |= 0x80;
                if (!wasSet)
                    raiseNmi = true;
            }
        }

        /// <summary>Advances cia1 serial output from timer a.</summary>
        /// <param name="underflows">The number of timer underflows to process.</param>
        /// <param name="raiseIrq">Set to true when the operation should raise a CIA1 IRQ.</param>
        private void StepCia1SerialOutputFromTimerA(int underflows, ref bool raiseIrq)
        {
            if (underflows <= 0 || (cia1Cra & 0x40) == 0)
                return;

            for (int i = 0; i < underflows; i++)
            {
                if (!cia1SerialOutputActive)
                {
                    if (!cia1SerialDataPending)
                        break;
                    cia1SerialShiftReg = cia1Sdr;
                    cia1SerialBitsRemaining = 8;
                    cia1SerialOutputActive = true;
                    cia1SerialDataPending = false;
                }

                cia1SpOutHigh = (cia1SerialShiftReg & 0x80) != 0;
                cia1SerialShiftReg <<= 1;
                cia1SerialBitsRemaining--;

                /// Output mode drives CNT pulses for each shifted bit.
                if (cia1SerialBitsRemaining == 0)
                {
                    cia1SerialOutputActive = false;
                    cia1IcrStatus |= 0x08;
                    if ((cia1IcrMask & 0x08) != 0)
                    {
                        bool wasSet = (cia1IcrStatus & 0x80) != 0;
                        cia1IcrStatus |= 0x80;
                        if (!wasSet)
                            raiseIrq = true;
                    }
                }
            }
        }

        /// <summary>Advances cia2 serial output from timer a.</summary>
        /// <param name="underflows">The number of timer underflows to process.</param>
        /// <param name="raiseNmi">Set to true when the operation should raise a CIA2 NMI.</param>
        private void StepCia2SerialOutputFromTimerA(int underflows, ref bool raiseNmi)
        {
            if (underflows <= 0 || (cia2Cra & 0x40) == 0)
                return;

            for (int i = 0; i < underflows; i++)
            {
                if (!cia2SerialOutputActive)
                {
                    if (!cia2SerialDataPending)
                        break;
                    cia2SerialShiftReg = cia2Sdr;
                    cia2SerialBitsRemaining = 8;
                    cia2SerialOutputActive = true;
                    cia2SerialDataPending = false;
                }

                cia2SpOutHigh = (cia2SerialShiftReg & 0x80) != 0;
                cia2SerialShiftReg <<= 1;
                cia2SerialBitsRemaining--;

                if (cia2SerialBitsRemaining == 0)
                {
                    cia2SerialOutputActive = false;
                    cia2IcrStatus |= 0x08;
                    if ((cia2IcrMask & 0x08) != 0)
                    {
                        bool wasSet = (cia2IcrStatus & 0x80) != 0;
                        cia2IcrStatus |= 0x80;
                        if (!wasSet)
                            raiseNmi = true;
                    }
                }
            }
        }

        /// <summary>Writes cia tod register.</summary>
        private static void WriteCiaTodRegister(
            int reg,
            byte value,
            bool writeAlarm,
            ref byte todTenths,
            ref byte todSeconds,
            ref byte todMinutes,
            ref byte todHours,
            ref byte alarmTenths,
            ref byte alarmSeconds,
            ref byte alarmMinutes,
            ref byte alarmHours,
            ref bool todLatched)
        {
            if (writeAlarm)
            {
                switch (reg)
                {
                    case 0: alarmTenths = (byte)(value & 0x0F); break;
                    case 1: alarmSeconds = (byte)(value & 0x7F); break;
                    case 2: alarmMinutes = (byte)(value & 0x7F); break;
                    case 3: alarmHours = (byte)(value & 0x9F); break;
                }
            }
            else
            {
                switch (reg)
                {
                    case 0: todTenths = (byte)(value & 0x0F); break;
                    case 1: todSeconds = (byte)(value & 0x7F); break;
                    case 2: todMinutes = (byte)(value & 0x7F); break;
                    case 3: todHours = (byte)(value & 0x9F); break;
                }
                todLatched = false;
            }
        }

        /// <summary>Reads cia tod register.</summary>
        private static byte ReadCiaTodRegister(
            int reg,
            ref bool latched,
            ref byte latchTenths,
            ref byte latchSeconds,
            ref byte latchMinutes,
            ref byte latchHours,
            byte todTenths,
            byte todSeconds,
            byte todMinutes,
            byte todHours)
        {
            /// CIA TOD reads latch on HOURS (reg 3) and release on TENTHS (reg 0).
            /// The latch captures all four registers atomically to prevent torn reads
            /// when TOD simultaneously advances during multi-byte read sequence.
            if (reg == 3 && !latched)
            {
                /// Atomically snapshot all TOD registers when HOURS is read
                latchTenths = todTenths;
                latchSeconds = todSeconds;
                latchMinutes = todMinutes;
                latchHours = todHours;
                latched = true;
            }

            byte value = reg switch
            {
                0 => latched ? latchTenths : todTenths,
                1 => latched ? latchSeconds : todSeconds,
                2 => latched ? latchMinutes : todMinutes,
                3 => latched ? latchHours : todHours,
                _ => 0
            };

            if (reg == 0)
                latched = false;

            return value;
        }

        /// <summary>Updates cia tod mirror.</summary>
        /// <param name="baseAddr">The base address of the CIA TOD register mirror.</param>
        /// <param name="tenths">The BCD tenths-of-a-second value.</param>
        /// <param name="seconds">The BCD seconds value.</param>
        /// <param name="minutes">The BCD minutes value.</param>
        /// <param name="hours">The BCD hours value.</param>
        private void UpdateCiaTodMirror(int baseAddr, byte tenths, byte seconds, byte minutes, byte hours)
        {
            cpu.memory.memory[baseAddr] = tenths;
            cpu.memory.memory[baseAddr + 1] = seconds;
            cpu.memory.memory[baseAddr + 2] = minutes;
            cpu.memory.memory[baseAddr + 3] = hours;
        }

        /// <summary>Converts a BCD byte to an integer.</summary>
        /// <param name="v">The SID voice state to update or inspect.</param>
        /// <returns>The numeric value produced by the operation.</returns>
        private static int BcdToInt(byte v)
        {
            return ((v >> 4) & 0x0F) * 10 + (v & 0x0F);
        }

        /// <summary>Converts an integer to a BCD byte.</summary>
        /// <param name="v">The SID voice state to update or inspect.</param>
        /// <returns>The byte value produced by the operation.</returns>
        private static byte IntToBcd(int v)
        {
            return (byte)(((v / 10) << 4) | (v % 10));
        }

        /// <summary>Increments tod.</summary>
        /// <param name="tenths">The BCD tenths-of-a-second value.</param>
        /// <param name="seconds">The BCD seconds value.</param>
        /// <param name="minutes">The BCD minutes value.</param>
        /// <param name="hours">The BCD hours value.</param>
        private static void IncrementTod(ref byte tenths, ref byte seconds, ref byte minutes, ref byte hours)
        {
            int t = (tenths & 0x0F) + 1;
            if (t < 10)
            {
                tenths = (byte)t;
                return;
            }

            tenths = 0x00;

            int s = BcdToInt((byte)(seconds & 0x7F)) + 1;
            if (s < 60)
            {
                seconds = IntToBcd(s);
                return;
            }

            seconds = 0x00;

            int m = BcdToInt((byte)(minutes & 0x7F)) + 1;
            if (m < 60)
            {
                minutes = IntToBcd(m);
                return;
            }

            minutes = 0x00;

            int h = BcdToInt((byte)(hours & 0x1F));
            if (h < 1 || h > 12) h = 12;
            bool pm = (hours & 0x80) != 0;

            if (h == 11)
            {
                h = 12;
                pm = !pm;
            }
            else if (h == 12)
            {
                h = 1;
            }
            else
            {
                h++;
            }

            hours = (byte)((pm ? 0x80 : 0x00) | (IntToBcd(h) & 0x1F));
        }

        /// <summary>Advances cia1 tod.</summary>
        /// <param name="cycles">The number of emulated CPU cycles to advance.</param>
        /// <param name="raiseIrq">Set to true when the operation should raise a CIA1 IRQ.</param>
        private void StepCia1Tod(uint cycles, ref bool raiseIrq)
        {
            int todHz = (cia1Cra & 0x80) != 0 ? 50 : 60;
            int subTicksPerTenth = todHz / 10;

            cia1TodNumerator += (long)cycles * todHz;
            while (cia1TodNumerator >= Clock_PAL)
            {
                cia1TodNumerator -= Clock_PAL;
                cia1TodSubTicks++;
                if (cia1TodSubTicks >= subTicksPerTenth)
                {
                    cia1TodSubTicks = 0;
                    IncrementTod(ref cia1TodTenths, ref cia1TodSeconds, ref cia1TodMinutes, ref cia1TodHours);

                    bool alarmMatch =
                        (cia1TodTenths & 0x0F) == (cia1AlarmTenths & 0x0F) &&
                        (cia1TodSeconds & 0x7F) == (cia1AlarmSeconds & 0x7F) &&
                        (cia1TodMinutes & 0x7F) == (cia1AlarmMinutes & 0x7F) &&
                        (cia1TodHours & 0x9F) == (cia1AlarmHours & 0x9F);

                    if (alarmMatch)
                    {
                        cia1IcrStatus |= 0x04;
                        if ((cia1IcrMask & 0x04) != 0)
                        {
                            bool wasSet = (cia1IcrStatus & 0x80) != 0;
                            cia1IcrStatus |= 0x80;
                            if (!wasSet)
                                raiseIrq = true;
                        }
                    }
                }
            }

            UpdateCiaTodMirror(0xDC08, cia1TodTenths, cia1TodSeconds, cia1TodMinutes, cia1TodHours);
        }

        /// <summary>Advances cia2 tod.</summary>
        /// <param name="cycles">The number of emulated CPU cycles to advance.</param>
        /// <param name="raiseNmi">Set to true when the operation should raise a CIA2 NMI.</param>
        private void StepCia2Tod(uint cycles, ref bool raiseNmi)
        {
            int todHz = (cia2Cra & 0x80) != 0 ? 50 : 60;
            int subTicksPerTenth = todHz / 10;

            cia2TodNumerator += (long)cycles * todHz;
            while (cia2TodNumerator >= Clock_PAL)
            {
                cia2TodNumerator -= Clock_PAL;
                cia2TodSubTicks++;
                if (cia2TodSubTicks >= subTicksPerTenth)
                {
                    cia2TodSubTicks = 0;
                    IncrementTod(ref cia2TodTenths, ref cia2TodSeconds, ref cia2TodMinutes, ref cia2TodHours);

                    bool alarmMatch =
                        (cia2TodTenths & 0x0F) == (cia2AlarmTenths & 0x0F) &&
                        (cia2TodSeconds & 0x7F) == (cia2AlarmSeconds & 0x7F) &&
                        (cia2TodMinutes & 0x7F) == (cia2AlarmMinutes & 0x7F) &&
                        (cia2TodHours & 0x9F) == (cia2AlarmHours & 0x9F);

                    if (alarmMatch)
                    {
                        cia2IcrStatus |= 0x04;
                        if ((cia2IcrMask & 0x04) != 0)
                        {
                            bool wasSet = (cia2IcrStatus & 0x80) != 0;
                            cia2IcrStatus |= 0x80;
                            if (!wasSet)
                                raiseNmi = true;
                        }
                    }
                }
            }

            UpdateCiaTodMirror(0xDD08, cia2TodTenths, cia2TodSeconds, cia2TodMinutes, cia2TodHours);
        }

        /// <summary>Counts underflows.</summary>
        /// <param name="counter">The timer counter to decrement.</param>
        /// <param name="latch">The timer latch value used when the counter reloads.</param>
        /// <param name="ticks">The number of timer ticks to apply.</param>
        /// <param name="oneShot">Whether the timer runs in one-shot mode.</param>
        /// <param name="control">The CIA timer control register to update.</param>
        /// <returns>The numeric value produced by the operation.</returns>
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

        /// <summary>
        /// Advances CIA1 timers, serial output, keyboard scanning, interrupt latches, and timer-driven underflow behavior.
        /// </summary>
        /// <param name="cycles">The number of emulated CPU cycles to advance.</param>
        private void StepCia1Timers(uint cycles)
        {
            if (cycles == 0) return;

            bool raiseIrq = false;
            lock (cia1Lock)
            {
                bool wasIrqPending = (cia1IcrStatus & 0x80) != 0;
                uint cntPulses = cia1CntPulseBudget;
                cia1CntPulseBudget = 0;
                bool cntHighObserved = cia1CntInHigh || cia1CntHighSeen;
                cia1CntHighSeen = false;

                /// When timer A is in external count mode (CRA bit 5 = 1), count external PA6 pulses
                /// instead of system cycles. The prescaler behavior must detect rising edges on PA6.
                /// Current port A bit 6 state affects timer A clock source selection.
                bool pa6Current = (cia1PortA & 0x40) != 0;
                if ((cia1Cra & 0x20) != 0 && pa6Current != cia1Pa6PrescalerPrevState)
                {
                    /// Rising edge on PA6 (external clock) advances timer A
                    if (pa6Current)
                        cntPulses++;
                }
                cia1Pa6PrescalerPrevState = pa6Current;

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
                }

                StepCia1SerialOutputFromTimerA(underA, ref raiseIrq);

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
                    ticksB = cntHighObserved ? (uint)underA : 0;

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
                    }
                }

                if ((cia1IcrStatus & cia1IcrMask & 0x1F) != 0)
                    cia1IcrStatus |= 0x80;
                else
                    cia1IcrStatus = (byte)(cia1IcrStatus & 0x7F);

                bool isIrqPending = (cia1IcrStatus & 0x80) != 0;
                if (isIrqPending && !wasIrqPending)
                    raiseIrq = true;

                StepCia1Tod(cycles, ref raiseIrq);

                cpu.memory.memory[0xDC04] = (byte)(cia1TimerACounter & 0xFF);
                cpu.memory.memory[0xDC05] = (byte)(cia1TimerACounter >> 8);
                cpu.memory.memory[0xDC06] = (byte)(cia1TimerBCounter & 0xFF);
                cpu.memory.memory[0xDC07] = (byte)(cia1TimerBCounter >> 8);
                cpu.memory.memory[0xDC0C] = cia1Sdr;
                cpu.memory.memory[0xDC0D] = cia1IcrStatus;
                cpu.memory.memory[0xDC0E] = cia1Cra;
                cpu.memory.memory[0xDC0F] = cia1Crb;
            }

            if (raiseIrq)
                cpu.InitiateIRQ(0xFFFE);
        }

        /// <summary>
        /// Advances CIA2 timers, serial output, NMI latches, and timer-driven underflow behavior.
        /// </summary>
        /// <param name="cycles">The number of emulated CPU cycles to advance.</param>
        private void StepCia2Timers(uint cycles)
        {
            if (cycles == 0) return;

            bool raiseNmi = false;
            lock (cia2Lock)
            {
                uint cntPulses = cia2CntPulseBudget;
                cia2CntPulseBudget = 0;
                bool cntHighObserved = cia2CntInHigh || cia2CntHighSeen;
                cia2CntHighSeen = false;

                int underA = CountUnderflows(
                    ref cia2TimerACounter,
                    cia2TimerALatch,
                    (cia2Cra & 0x20) == 0 ? cycles : cntPulses,
                    (cia2Cra & 0x08) != 0,
                    ref cia2Cra);

                StepCia2SerialOutputFromTimerA(underA, ref raiseNmi);

                if ((cia2Cra & 0x40) != 0 && underA > 0)
                    cntPulses = Math.Max(cntPulses, (uint)underA);

                uint ticksB = cycles;
                int tbMode = (cia2Crb >> 5) & 0x03;
                if (tbMode == 1)
                    ticksB = cntPulses;
                else if (tbMode == 2)
                    ticksB = (uint)Math.Max(underA, 0);
                else if (tbMode == 3)
                    ticksB = cntHighObserved ? (uint)Math.Max(underA, 0) : 0u;

                if (ticksB > 0)
                {
                    int underB = CountUnderflows(
                        ref cia2TimerBCounter,
                        cia2TimerBLatch,
                        ticksB,
                        (cia2Crb & 0x08) != 0,
                        ref cia2Crb);

                    if (underB > 0)
                    {
                        cia2IcrStatus |= 0x02;
                    }
                }

                if (underA > 0)
                    cia2IcrStatus |= 0x01;

                if ((cia2IcrStatus & cia2IcrMask & 0x1F) != 0)
                {
                    bool wasSet = (cia2IcrStatus & 0x80) != 0;
                    cia2IcrStatus |= 0x80;
                    if (!wasSet)
                        raiseNmi = true;
                }
                else
                {
                    cia2IcrStatus = (byte)(cia2IcrStatus & 0x7F);
                }

                StepCia2Tod(cycles, ref raiseNmi);

                cpu.memory.memory[0xDD04] = (byte)(cia2TimerACounter & 0xFF);
                cpu.memory.memory[0xDD05] = (byte)(cia2TimerACounter >> 8);
                cpu.memory.memory[0xDD06] = (byte)(cia2TimerBCounter & 0xFF);
                cpu.memory.memory[0xDD07] = (byte)(cia2TimerBCounter >> 8);
                cpu.memory.memory[0xDD0C] = cia2Sdr;
                cpu.memory.memory[0xDD0D] = cia2IcrStatus;
                cpu.memory.memory[0xDD0E] = cia2Cra;
                cpu.memory.memory[0xDD0F] = cia2Crb;
            }

            if (raiseNmi)
                cpu.InitiateNMI(0xFFFA);
        }

        /// Drive CIA state directly from executed CPU cycles so timer and IRQ/NMI
        /// behavior follows CPU progression instead of coarse host wall-clock ticks.

        /// <summary>
        /// Steps peripherals after CPU execution, including VIC raster timing, CIA timers/TOD, REU DMA, datasette pulses, and keyboard queue draining.
        /// </summary>
        /// <param name="cycles">The number of emulated CPU cycles to advance.</param>
        private void OnCpuCyclesExecuted(int cycles)
        {
            if (cycles <= 0 || display.IsResetting)
                return;

            TryHandleKernalIecTrap();
            TryHandleKernalLoadTrap();
            TryHandleKernalSaveTrap();

            byte p1 = cpu.memory.memory[0x0001];
            bool motorOn = (p1 & 0x20) == 0;
            datasette.SetMotor(motorOn);

            uint step = (uint)cycles;
            uint vicSteal = display.StepCycles(step, accountBusSteal: true);
            if (vicSteal > 0)
            {
                cpu.RequestExternalStallCycles((int)vicSteal);
                _ = display.StepCycles(vicSteal, accountBusSteal: false);
            }

            uint elapsed = step + vicSteal;
            bool tapeEdge = datasette.Step(elapsed);
            if (tapeEdge || datasette.ReadHigh != lastDatasetteReadHigh)
            {
                SetCia1SerialPins(datasette.ReadHigh, datasette.ReadHigh);
                lastDatasetteReadHigh = datasette.ReadHigh;
            }

            /// Keep a simple sense bit mirror on processor-port bit 4.
            if (datasette.SenseHigh)
                cpu.memory.memory[0x0001] |= 0x10;
            else
                cpu.memory.memory[0x0001] &= 0xEF;

            /// Reflect IEC data/clock line levels onto CIA2 SP/CNT pins so
            /// external clock/input timer modes observe real bus transitions.
            byte iecExternal = iecBus.BuildExternalCia2PortA(0xFF);
            bool iecDataHigh = (iecExternal & 0x80) != 0;
            bool iecClockHigh = (iecExternal & 0x40) != 0;
            SetCia2SerialPins(iecDataHigh, iecClockHigh);
            iecBus.StepDriveCycles((int)elapsed);
            display.DriveActivityLightOn = iecBus.DriveActivityLightOn;

            StepCia1Timers(elapsed);
            StepCia2Timers(elapsed);
            reu.StepDma((int)cycles, cpu.memory);

            keyboardDrainCycleBudget += (int)elapsed;
            if (keyboardDrainCycleBudget >= KeyboardDrainPeriodCycles)
            {
                keyboardDrainCycleBudget %= KeyboardDrainPeriodCycles;
                keyboard.DrainQueue();
            }
        }

        /// <summary>
        /// Intercepts selected KERNAL IEC routines and services them through the virtual IEC bus when possible.
        /// </summary>
        private void TryHandleKernalIecTrap()
        {
            ulong pc = cpu.registers.PC;
            switch (pc)
            {
                case 0xFFC0: /// OPEN
                    HandleKernalOpenTrap();
                    break;

                case 0xFFC3: /// CLOSE
                    if (iecBus.Close(cpu.registers.A))
                    {
                        cpu.registers.Flags.C = false;
                        ReturnFromKernelTrap();
                    }
                    break;

                case 0xFFC6: /// CHKIN
                    if (iecBus.Chkin(cpu.registers.X))
                    {
                        cpu.registers.Flags.C = false;
                        ReturnFromKernelTrap();
                    }
                    break;

                case 0xFFC9: /// CHKOUT
                    if (iecBus.Chkout(cpu.registers.X))
                    {
                        cpu.registers.Flags.C = false;
                        ReturnFromKernelTrap();
                    }
                    break;

                case 0xFFCC: /// CLRCHN
                    if (iecBus.HasActiveChannel)
                    {
                        iecBus.FlushOutput();
                        iecBus.Clrchn();
                        cpu.registers.Flags.C = false;
                        ReturnFromKernelTrap();
                    }
                    break;

                case 0xFFCF: /// CHRIN
                    if (iecBus.HasInputChannel)
                    {
                        cpu.registers.A = iecBus.Chrin();
                        cpu.registers.Flags.C = false;
                        ReturnFromKernelTrap();
                    }
                    break;

                case 0xFFD2: /// CHROUT
                    if (iecBus.Chrout(cpu.registers.A))
                    {
                        cpu.registers.Flags.C = false;
                        ReturnFromKernelTrap();
                    }
                    break;

                case 0xFFB1: /// LISTEN
                    iecBus.Listen(cpu.registers.A);
                    cpu.registers.Flags.C = false;
                    ReturnFromKernelTrap();
                    break;

                case 0xFFB4: /// TALK
                    iecBus.Talk(cpu.registers.A);
                    cpu.registers.Flags.C = false;
                    ReturnFromKernelTrap();
                    break;

                case 0xFF93: /// SECOND
                    iecBus.Second(cpu.registers.A);
                    cpu.registers.Flags.C = false;
                    ReturnFromKernelTrap();
                    break;

                case 0xFF96: /// TKSA
                    iecBus.Tksa(cpu.registers.A);
                    cpu.registers.Flags.C = false;
                    ReturnFromKernelTrap();
                    break;

                case 0xFFA8: /// CIOUT
                    iecBus.Ciout(cpu.registers.A);
                    cpu.registers.Flags.C = false;
                    ReturnFromKernelTrap();
                    break;

                case 0xFFA5: /// ACPTR
                    cpu.registers.A = iecBus.Acptr();
                    cpu.registers.Flags.C = false;
                    ReturnFromKernelTrap();
                    break;

                case 0xFFAE: /// UNLSN
                    iecBus.Unlisten();
                    cpu.registers.Flags.C = false;
                    ReturnFromKernelTrap();
                    break;

                case 0xFFAB: /// UNTLK
                    iecBus.Untalk();
                    cpu.registers.Flags.C = false;
                    ReturnFromKernelTrap();
                    break;
            }
        }

        /// <summary>Handles kernal open trap.</summary>
        private void HandleKernalOpenTrap()
        {
            byte[] mem = cpu.memory.memory;
            byte nameLen = mem[0x00B7];
            ushort namePtr = (ushort)(mem[0x00BB] | (mem[0x00BC] << 8));
            string? name = null;

            if (nameLen != 0)
            {
                var chars = new char[nameLen];
                for (int i = 0; i < nameLen; i++)
                {
                    byte b = cpu.memory.ReadByte((ulong)(namePtr + i));
                    chars[i] = b >= 0x20 && b <= 0x7E ? (char)b : '?';
                }
                name = new string(chars).Trim();
            }

            if (iecBus.Open(mem[0x00B8], mem[0x00BA], mem[0x00B9], name))
            {
                cpu.registers.Flags.C = false;
                ReturnFromKernelTrap();
            }
        }

        /// <summary>
        /// Handles intercepted KERNAL LOAD calls for host PRG/T64 files, attached D64 media, loose host programs, and load messages.
        /// </summary>
        private void TryHandleKernalLoadTrap()
        {
            /// KERNAL LOAD entry. Trap after JSR has transferred PC to $FFD5.
            if (cpu.registers.PC != 0xFFD5)
                return;

            byte[] mem = cpu.memory.memory;

            /// KERNAL parameter block used by SETLFS/SETNAM:
            ///   $B7 filename length
            ///   $BB/$BC filename pointer
            byte nameLen = mem[0x00B7];
            ushort namePtr = (ushort)(mem[0x00BB] | (mem[0x00BC] << 8));
            byte secondaryAddress = mem[0x00B9];
            ushort relocateAddress = (ushort)(cpu.registers.X | (cpu.registers.Y << 8));
            ushort? loadOverride = secondaryAddress == 0 ? relocateAddress : null;

            string? requestedName = null;
            if (nameLen != 0)
            {
                var chars = new char[nameLen];
                for (int i = 0; i < nameLen; i++)
                {
                    byte b = cpu.memory.ReadByte((ulong)(namePtr + i));
                    chars[i] = b >= 0x20 && b <= 0x7E ? (char)b : '?';
                }
                requestedName = new string(chars).Trim();
            }

            byte currentDevice = mem[0x00BA];
            if (currentDevice == 8 && drive.HasMedia && iecBus.HasFullDrive && Native1541LoadEnabled)
                return;

            if (currentDevice == 8 && drive.HasMedia)
            {
                byte[] prg;
                string resolvedName;
                PrintKernalLoadMessages(requestedName);
                bool ok = string.IsNullOrWhiteSpace(requestedName)
                    ? iecBus.TryLoadFromDrive(out prg, out resolvedName)
                    : iecBus.TryLoadFromDrive(requestedName, out prg, out resolvedName);

                if (!ok)
                {
                    cpu.registers.A = 0x04;
                    cpu.registers.Flags.C = true;
                    ReturnFromKernelTrap();
                    return;
                }

                try
                {
                    (ushort startAddr, ushort end) = LoadPrgFromBytes(prg, loadOverride);
                    cpu.registers.X = (byte)(end & 0xFF);
                    cpu.registers.Y = (byte)(end >> 8);
                    cpu.registers.A = 0x00;
                    cpu.registers.Flags.C = false;
                    /// Mirror end address into KERNAL/BASIC scratch ($AE/$AF) and
                    /// clear IEC STATUS so BASIC's READST after LOAD sees success.
                    cpu.memory.WriteByte(0x00AE, (byte)(end & 0xFF));
                    cpu.memory.WriteByte(0x00AF, (byte)(end >> 8));
                    cpu.memory.WriteByte(0x0090, 0x00);
                    SetLastHostLoadedFile(drive.AttachedPath);
                    ReleaseIecLinesAfterTrappedLoad();
                }
                catch
                {
                    cpu.registers.A = 0x1F;
                    cpu.registers.Flags.C = true;
                }

                ReturnFromKernelTrap();
                return;
            }

            string? resolved = ResolveKernelLoadPath(requestedName);
            if (resolved is null)
            {
                cpu.registers.A = 0x04; /// FILE NOT FOUND
                cpu.registers.Flags.C = true;
                ReturnFromKernelTrap();
                return;
            }

            try
            {
                (ushort start, ushort end) = Path.GetExtension(resolved).Equals(".t64", StringComparison.OrdinalIgnoreCase)
                    ? LoadT64FromLoadCommand(resolved, requestedName)
                    : LoadPrgFromLoadCommand(resolved, requestedName, loadOverride);

                /// LOAD returns end address in X/Y and C clear on success.
                cpu.registers.X = (byte)(end & 0xFF);
                cpu.registers.Y = (byte)(end >> 8);
                cpu.registers.A = 0x00;
                cpu.registers.Flags.C = false;
                cpu.memory.WriteByte(0x00AE, (byte)(end & 0xFF));
                cpu.memory.WriteByte(0x00AF, (byte)(end >> 8));
                cpu.memory.WriteByte(0x0090, 0x00);

                SetLastHostLoadedFile(resolved);
                ReleaseIecLinesAfterTrappedLoad();
            }
            catch
            {
                cpu.registers.A = 0x1F; /// generic LOAD error
                cpu.registers.Flags.C = true;
            }

            ReturnFromKernelTrap();
        }

        /// <summary>
        /// Leaves CIA2 IEC output lines idle after a host-side LOAD trap.
        /// The trap bypasses the real KERNAL serial cleanup, so preserve VIC bank bits and release DATA, CLOCK, and ATN before custom loaders take over.
        /// </summary>
        private void ReleaseIecLinesAfterTrappedLoad()
        {
            cia2PortA = (byte)(cia2PortA & ~0x38);
            cpu.memory.memory[0xDD00] = cia2PortA;
            iecBus.UpdateHostCia2PortA(cia2PortA, cia2Ddra);
        }

        /// <summary>
        /// Handles intercepted KERNAL SAVE calls by resolving the requested filename and writing the selected memory range as a PRG file.
        /// </summary>
        private void TryHandleKernalSaveTrap()
        {
            /// KERNAL SAVE entry. A points to a zero-page word containing the
            /// start address; X/Y contain the exclusive end address.
            if (cpu.registers.PC != 0xFFD8)
                return;

            byte[] mem = cpu.memory.memory;
            string? requestedName = ReadKernalFilename();
            if (string.IsNullOrWhiteSpace(requestedName))
            {
                cpu.registers.A = 0x08; /// missing file name
                cpu.registers.Flags.C = true;
                ReturnFromKernelTrap();
                return;
            }

            byte startPointer = cpu.registers.A;
            ushort start = (ushort)(mem[startPointer] | (mem[(byte)(startPointer + 1)] << 8));
            ushort end = (ushort)(cpu.registers.X | (cpu.registers.Y << 8));

            if (end <= start)
            {
                cpu.registers.A = 0x1F;
                cpu.registers.Flags.C = true;
                ReturnFromKernelTrap();
                return;
            }

            try
            {
                string softwareDir = SoftwareDirectory.Ensure();
                string filename = NormalizePrgFilename(requestedName);
                string path = Path.Combine(softwareDir, filename);

                PrintKernalSaveMessages(requestedName);
                SaveMemoryRangeAsPrg(path, mem, start, end);

                cpu.registers.A = 0x00;
                cpu.registers.Flags.C = false;
                cpu.memory.WriteByte(0x0090, 0x00);
                SetLastHostLoadedFile(path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"SAVE failed: {ex.Message}");
                cpu.registers.A = 0x1F;
                cpu.registers.Flags.C = true;
            }

            ReturnFromKernelTrap();
        }

        /// <summary>Loads a host PRG file requested by a trapped KERNAL LOAD call and emits the matching KERNAL status text.</summary>
        /// <param name="path">The path of the file to use.</param>
        /// <param name="requestedName">The C64 filename requested by the caller, or null to select a default.</param>
        /// <param name="loadOverride">An optional load address that overrides the PRG header address.</param>
        /// <returns>The load start and end addresses written in emulated memory.</returns>
        private (ushort Start, ushort End) LoadPrgFromLoadCommand(string path, string? requestedName, ushort? loadOverride)
        {
            PrintKernalLoadMessages(requestedName);
            return LoadPrgFromBytes(File.ReadAllBytes(path), loadOverride);
        }

        /// <summary>Loads the selected entry from a host T64 image requested by a trapped KERNAL LOAD call.</summary>
        /// <param name="path">The path of the file to use.</param>
        /// <param name="requestedName">The C64 filename requested by the caller, or null to select a default.</param>
        /// <returns>The load start and end addresses written in emulated memory.</returns>
        private (ushort Start, ushort End) LoadT64FromLoadCommand(string path, string? requestedName)
        {
            List<TapeEntry> entries = TapeLoader.ReadT64(File.ReadAllBytes(path));
            TapeEntry? entry = SelectTapeEntry(entries, requestedName);
            if (entry is null)
                throw new FileNotFoundException("No matching T64 entry found.");

            PrintTapeLoadMessages(entry.Name);
            return LoadTapeEntry(entry);
        }

        /// <summary>Selects the requested tape entry, or the first entry when no explicit name was supplied.</summary>
        /// <param name="entries">The decoded tape entries to search.</param>
        /// <param name="requestedName">The C64 filename requested by the caller, or null to select a default.</param>
        /// <returns>The selected tape entry, or null when no entry matches.</returns>
        private static TapeEntry? SelectTapeEntry(List<TapeEntry> entries, string? requestedName)
        {
            if (entries.Count == 0)
                return null;

            string wanted = NormalizeTapeName(requestedName);
            if (string.IsNullOrEmpty(wanted) || wanted == "*")
                return entries[0];

            if (wanted.EndsWith("*", StringComparison.Ordinal))
            {
                string prefix = wanted.Substring(0, wanted.Length - 1);
                return entries.FirstOrDefault(e => NormalizeTapeName(e.Name).StartsWith(prefix, StringComparison.Ordinal));
            }

            return entries.FirstOrDefault(e => NormalizeTapeName(e.Name) == wanted)
                ?? entries.FirstOrDefault(e => NormalizeTapeName(e.Name).StartsWith(wanted, StringComparison.Ordinal));
        }

        /// <summary>Normalizes tape name.</summary>
        /// <param name="name">The C64 filename or display name to use.</param>
        /// <returns>The string value produced by the operation.</returns>
        private static string NormalizeTapeName(string? name)
        {
            return string.IsNullOrWhiteSpace(name)
                ? string.Empty
                : name.Trim().Trim('"', '\'').ToUpperInvariant();
        }

        /// <summary>Prints tape load messages.</summary>
        /// <param name="name">The C64 filename or display name to use.</param>
        private void PrintTapeLoadMessages(string name)
        {
            EnsureScreenLineStart();
            WriteScreenText("SEARCHING");
            NewScreenLine();
            WriteScreenText($"FOUND {name.ToUpperInvariant()}");
            NewScreenLine();
            WriteScreenText("LOADING");
            NewScreenLine();
        }

        /// <summary>Prints kernal load messages.</summary>
        /// <param name="requestedName">The C64 filename requested by the caller, or null to select a default.</param>
        private void PrintKernalLoadMessages(string? requestedName)
        {
            string name = string.IsNullOrWhiteSpace(requestedName)
                ? "*"
                : requestedName.Trim().Trim('"', '\'');
            if (string.IsNullOrWhiteSpace(name))
                name = "*";

            EnsureScreenLineStart();
            WriteScreenText($"SEARCHING FOR {name.ToUpperInvariant()}");
            NewScreenLine();
            WriteScreenText("LOADING");
            NewScreenLine();
        }

        /// <summary>Prints kernal save messages.</summary>
        /// <param name="requestedName">The C64 filename requested by the caller, or null to select a default.</param>
        private void PrintKernalSaveMessages(string requestedName)
        {
            string name = requestedName.Trim().Trim('"', '\'');
            EnsureScreenLineStart();
            WriteScreenText($"SAVING {name.ToUpperInvariant()}");
            NewScreenLine();
        }

        /// <summary>Reads kernal filename.</summary>
        /// <returns>The selected or resolved string value, or null when no value is available.</returns>
        private string? ReadKernalFilename()
        {
            byte[] mem = cpu.memory.memory;
            byte nameLen = mem[0x00B7];
            ushort namePtr = (ushort)(mem[0x00BB] | (mem[0x00BC] << 8));

            if (nameLen == 0)
                return null;

            var chars = new char[nameLen];
            for (int i = 0; i < nameLen; i++)
            {
                byte b = cpu.memory.ReadByte((ulong)(namePtr + i));
                chars[i] = b >= 0x20 && b <= 0x7E ? (char)b : '?';
            }

            return new string(chars).Trim();
        }

        /// <summary>Resolves kernel load path.</summary>
        /// <param name="requestedName">The C64 filename requested by the caller, or null to select a default.</param>
        /// <returns>The selected or resolved string value, or null when no value is available.</returns>
        private string? ResolveKernelLoadPath(string? requestedName)
        {
            if (!string.IsNullOrWhiteSpace(requestedName))
            {
                string name = requestedName.Trim().Trim('"', '\'');
                if (!Path.HasExtension(name))
                    name += ".prg";

                if (Path.IsPathRooted(name) && File.Exists(name))
                    return name;

                string? softwareDir = SoftwareDirectory.Find();
                if (!string.IsNullOrWhiteSpace(softwareDir))
                {
                    string softwareCandidate = Path.Combine(softwareDir, name);
                    if (File.Exists(softwareCandidate))
                        return softwareCandidate;
                }

                string cwdCandidate = Path.Combine(Environment.CurrentDirectory, name);
                if (File.Exists(cwdCandidate))
                    return cwdCandidate;

                string projectCandidate = Path.Combine(Environment.CurrentDirectory, "C64", name);
                if (File.Exists(projectCandidate))
                    return projectCandidate;

                if (!string.IsNullOrWhiteSpace(lastHostLoadedFile))
                {
                    string? dir = Path.GetDirectoryName(lastHostLoadedFile);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        string nearLast = Path.Combine(dir, name);
                        if (File.Exists(nearLast))
                            return nearLast;
                    }
                }

                return null;
            }

            /// LOAD"",x reuses the most recent host-backed file when available.
            if (!string.IsNullOrWhiteSpace(lastHostLoadedFile) && File.Exists(lastHostLoadedFile))
                return lastHostLoadedFile;

            return null;
        }

        /// <summary>Ensures screen line start.</summary>
        private void EnsureScreenLineStart()
        {
            if (cpu.memory.memory[0x00D3] != 0)
                NewScreenLine();
        }

        /// <summary>Starts a new screen line.</summary>
        private void NewScreenLine()
        {
            byte[] mem = cpu.memory.memory;
            int row = Math.Min(mem[0x00D6] + 1, 24);
            if (mem[0x00D6] >= 24)
                ScrollBasicScreenUp();

            mem[0x00D3] = 0;
            mem[0x00D6] = (byte)row;
        }

        /// <summary>Writes screen text.</summary>
        /// <param name="text">The text to write.</param>
        private void WriteScreenText(string text)
        {
            foreach (char ch in text)
            {
                if (ch == '\r' || ch == '\n')
                {
                    NewScreenLine();
                    continue;
                }

                WriteScreenChar(ch);
            }
        }

        /// <summary>Writes screen char.</summary>
        /// <param name="ch">The character to convert or write.</param>
        private void WriteScreenChar(char ch)
        {
            byte[] mem = cpu.memory.memory;
            int col = mem[0x00D3];
            if (col >= 40)
            {
                NewScreenLine();
                col = 0;
            }

            int row = Math.Min(mem[0x00D6], (byte)24);
            int screenBase = mem[0x0288] << 8;
            if (screenBase == 0)
                screenBase = 0x0400;

            int offset = row * 40 + col;
            cpu.memory.WriteRamByte((ulong)(screenBase + offset), AsciiCharToScreenCode(ch));
            cpu.memory.WriteByte((ulong)(0xD800 + offset), (byte)(mem[0x0286] & 0x0F));
            mem[0x00D3] = (byte)(col + 1);
        }

        /// <summary>Implements the scroll basic screen up helper.</summary>
        private void ScrollBasicScreenUp()
        {
            byte[] mem = cpu.memory.memory;
            int screenBase = mem[0x0288] << 8;
            if (screenBase == 0)
                screenBase = 0x0400;

            for (int i = 0; i < 24 * 40; i++)
            {
                cpu.memory.WriteRamByte((ulong)(screenBase + i), mem[screenBase + i + 40]);
                cpu.memory.WriteByte((ulong)(0xD800 + i), cpu.memory.ReadByte((ulong)(0xD800 + i + 40)));
            }

            byte colour = (byte)(mem[0x0286] & 0x0F);
            for (int i = 24 * 40; i < 25 * 40; i++)
            {
                cpu.memory.WriteRamByte((ulong)(screenBase + i), 0x20);
                cpu.memory.WriteByte((ulong)(0xD800 + i), colour);
            }
        }

        /// <summary>Implements the ascii char to screen code helper.</summary>
        /// <param name="ch">The character to convert or write.</param>
        /// <returns>The byte value produced by the operation.</returns>
        private static byte AsciiCharToScreenCode(char ch)
        {
            if (ch >= 'a' && ch <= 'z')
                ch = (char)('A' + (ch - 'a'));
            if (ch >= 'A' && ch <= 'Z')
                return (byte)(ch - 'A' + 1);
            if (ch >= '0' && ch <= '9')
                return (byte)ch;

            return ch switch
            {
                ' ' => 0x20,
                '*' => 0x2A,
                '$' => 0x24,
                '.' => 0x2E,
                ',' => 0x2C,
                '-' => 0x2D,
                '_' => 0x64,
                ':' => 0x3A,
                '/' => 0x2F,
                '?' => 0x3F,
                _ => 0x20
            };
        }

        /// <summary>Returns from kernel trap.</summary>
        private void ReturnFromKernelTrap()
        {
            byte s = cpu.registers.S;
            byte lo = cpu.memory.ReadByte((ulong)(0x100 + (byte)(s + 1)));
            byte hi = cpu.memory.ReadByte((ulong)(0x100 + (byte)(s + 2)));
            cpu.registers.S = (byte)(s + 2);
            cpu.registers.PC = (ushort)(((hi << 8) | lo) + 1);
        }

        /// <summary>Runs the main emulator loop.</summary>
        public void Run()
        {
            string? audioDevice = Sound.GetDefaultDeviceName();
            display.Init();
            keyboard.InitGameControllers();
            sound.Init(audioDevice);

            var token = cts.Token;

            var cpuThread = new Thread(() =>
            {
                try { cpu.Run(); }
                catch (Exception) { }
            })
            {
                IsBackground = true,
                Name = "6502",
                Priority = ThreadPriority.AboveNormal
            };
            cpuThread.Start();

            display.Start(token);
            sound.Start(token);

            /// Run the exact same reset path used by Ctrl+R after all worker
            /// threads are alive. This avoids startup-only races where the
            /// display can remain in reset on first launch.
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
            const uint drawIntervalMs = 16; /// ~60 Hz upper bound; vsync paces actual present

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
                        case SDL_EventType.SDL_CONTROLLERDEVICEADDED:
                        case SDL_EventType.SDL_CONTROLLERDEVICEREMOVED:
                        case SDL_EventType.SDL_CONTROLLERBUTTONDOWN:
                        case SDL_EventType.SDL_CONTROLLERBUTTONUP:
                        case SDL_EventType.SDL_CONTROLLERAXISMOTION:
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

        /// <summary>Releases resources owned by this instance.</summary>
        public void Dispose()
        {
            try { cts?.Cancel(); } catch { }
            keyboard.Dispose();
            sound.Dispose();
            display.Dispose();
            reu.Dispose();
            SDL_Quit();
        }

        /// <summary>Performs a full emulator hardware reset.</summary>
        private void HardReset()
        {
            display.BeginReset();

            keyboard.Reset();
            display.JoystickPortOverlay = keyboard.ActiveJoystickPort;
            reu.Reset();
            iecBus.SetHostLooseProgramPresent(!string.IsNullOrWhiteSpace(lastHostLoadedFile));
            iecBus.ResetDrive();
            display.DriveActivityLightOn = false;

            cpu.RequestReset();
        }

        /// <summary>Handles a keyboard-triggered hard reset.</summary>
        private void HardResetFromKeyboard()
        {
            ClearLastHostLoadedFile();
            HardReset();
        }

        /// <summary>Raises the RESTORE-key NMI.</summary>
        private void TriggerRestoreNmi()
        {
            cpu.InitiateNMI(0xFFFA);
        }

        /// <summary>Toggles mute.</summary>
        private void ToggleMute()
        {
            bool muted = !sound.Muted;
            sound.Muted = muted;
            display.MuteOverlayVisible = muted;
        }

        /// <summary>Gets whether the emulator is currently paused.</summary>
        private bool IsPaused => cpu.Paused;

        /// <summary>Toggles pause.</summary>
        private void TogglePause()
        {
            SetPaused(!IsPaused);
        }

        /// <summary>Toggles keyboard and controller joystick input between C64 joystick ports and keyboard-only mode.</summary>
        private void ToggleJoystickPort()
        {
            int port = keyboard.ToggleJoystickPort();
            display.JoystickPortOverlay = port;
        }

        /// <summary>Selects audio device.</summary>
        private void SelectAudioDevice()
        {
            bool wasPaused = IsPaused;
            SetPaused(true);
            display.RedrawScreen();

            try
            {
                string? audioDevice = Sound.PromptForDevice();
                if (audioDevice is not null)
                    sound.SwitchDevice(audioDevice, wasPaused);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Audio device change failed: {ex.Message}");
            }
            finally
            {
                SetPaused(wasPaused);
            }
        }

        /// <summary>Sets whether execution is paused.</summary>
        /// <param name="paused">Whether emulation or audio output should be paused.</param>
        private void SetPaused(bool paused)
        {
            cpu.SetPaused(paused);
            sound.SetPaused(paused);
            display.PausedOverlayVisible = paused;
        }

        /// <summary>
        /// Drag-and-drop flow: reset the emulator (Ctrl+R equivalent), wait
        /// for the KERNAL to reach the READY prompt, load the file, then
        /// type RUN + RETURN.  Runs on a background task so we don't block
        /// the SDL event pump while waiting for the boot sequence.
        /// </summary>
        /// <param name="path">The path of the file to use.</param>
        /// <returns>A task that completes when the asynchronous operation finishes.</returns>
        private async Task ResetLoadRun(string path)
        {
            HardReset();

            /// Wait until reset is complete and BASIC reaches READY.
            /// Fixed wall-clock delays are brittle across host speeds/builds.
            await WaitForReadyPromptAsync(timeoutMs: 6000).ConfigureAwait(false);

            DoLoad(path);

            /// Small settle so directly loaded files have fully landed in memory
            /// before BASIC tokenises RUN.
            await Task.Delay(50).ConfigureAwait(false);

            /// Disk images attach media first, then use the C64 LOAD command.
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".d64")
            {
                await TypePetsciiLikeHumanAsync(
                    new byte[] { (byte)'L', (byte)'O', (byte)'A', (byte)'D', (byte)' ', (byte)'"', (byte)'*', (byte)'"', (byte)',', (byte)'8', (byte)',', (byte)'1', 0x0D, 0x0D },
                    minInterKeyMs: 110,
                    maxInterKeyMs: 220,
                    enterExtraMs: 120).ConfigureAwait(false);

                if (await WaitForReadyPromptAsync(timeoutMs: 8000).ConfigureAwait(false))
                {
                    await TypePetsciiLikeHumanAsync(
                        new byte[] { (byte)'R', (byte)'U', (byte)'N', 0x0D },
                        minInterKeyMs: 110,
                        maxInterKeyMs: 220,
                        enterExtraMs: 120).ConfigureAwait(false);
                }
                return;
            }
            else if (ext == ".t64")
            {
                await TypePetsciiLikeHumanAsync(
                    new byte[] { (byte)'L', (byte)'O', (byte)'A', (byte)'D', 0x0D },
                    minInterKeyMs: 110,
                    maxInterKeyMs: 220,
                    enterExtraMs: 120).ConfigureAwait(false);

                if (await WaitForReadyPromptAsync(timeoutMs: 8000).ConfigureAwait(false))
                {
                    await TypePetsciiLikeHumanAsync(
                        new byte[] { (byte)'R', (byte)'U', (byte)'N', 0x0D },
                        minInterKeyMs: 110,
                        maxInterKeyMs: 220,
                        enterExtraMs: 120).ConfigureAwait(false);
                }
                return;
            }
            else if (ext == ".prg")
            {
                await TypePetsciiLikeHumanAsync(
                    new byte[] { (byte)'R', (byte)'U', (byte)'N', 0x0D },
                    minInterKeyMs: 110,
                    maxInterKeyMs: 220,
                    enterExtraMs: 120).ConfigureAwait(false);
                return;
            }

            //await TypePetsciiLikeHumanAsync(
            ///    new byte[] { (byte)'R', (byte)'U', (byte)'N', 0x0D },
            ///    minInterKeyMs: 110,
            ///    maxInterKeyMs: 220,
            ///    enterExtraMs: 120).ConfigureAwait(false);
        }

        /// <summary>Types petscii like human async.</summary>
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

        /// <summary>Waits for for ready prompt async.</summary>
        /// <param name="timeoutMs">The maximum time to wait, in milliseconds.</param>
        /// <returns>A task that returns true when the operation succeeds; otherwise, false.</returns>
        private async Task<bool> WaitForReadyPromptAsync(int timeoutMs)
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
                    return true;

                await Task.Delay(pollMs).ConfigureAwait(false);
                waitedMs += pollMs;
            }

            return false;
        }

        /// <summary>Gets whether ready prompt on screen.</summary>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        private bool HasReadyPromptOnScreen()
        {
            /// C64 screen RAM stores screen codes, not ASCII.
            /// READY. in upper-case screen code sequence.
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

        /// <summary>Queues a host file to load and run.</summary>
        public void QueueLoadAndRun(string path) => pendingLoads.Enqueue((path, true));

        /// <summary>Loads program.</summary>
        private void LoadProgram()
        {
            bool wasPaused = IsPaused;
            SetPaused(true);
            display.RedrawScreen();

            try
            {
                string? path = SoftwareFileWindow.Prompt();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    SetPaused(false);
                    pendingLoads.Enqueue((path, true));
                }
                else
                {
                    SetPaused(wasPaused);
                }
            }
            catch (Exception ex)
            {
                SetPaused(wasPaused);
                Console.Error.WriteLine($"Load picker failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads text/PRG/T64 files into memory or attaches TAP/D64 media, updating host-file metadata and user-facing status messages.
        /// </summary>
        /// <param name="path">The path of the file to use.</param>
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
                    EjectDriveMedia();
                    LoadText(path);
                    SetLastHostLoadedFile(path);
                    Console.WriteLine($"Loaded {Path.GetFileName(path)}");
                }
                else if (ext == ".t64")
                {
                    EjectDriveMedia();
                    TapeLoader.ReadT64(File.ReadAllBytes(path));
                    SetLastHostLoadedFile(path);
                    Console.WriteLine($"Attached T64 {Path.GetFileName(path)}");
                }
                else if (ext == ".tap")
                {
                    EjectDriveMedia();
                    datasette.AttachTap(File.ReadAllBytes(path));
                    SetLastHostLoadedFile(path);
                    Console.WriteLine($"Attached datasette TAP {Path.GetFileName(path)}");
                }
                else if (ext == ".d64")
                {
                    drive.AttachD64(path);
                    iecBus.AttachD64(path);
                    SetLastHostLoadedFile(path);
                    IReadOnlyList<string> files = drive.ListFiles();
                    Console.WriteLine($"Attached D64 {Path.GetFileName(path)} ({files.Count} PRG entries)");
                }
                else
                {
                    EjectDriveMedia();
                    LoadPrg(path);
                    Console.WriteLine($"Loaded {Path.GetFileName(path)}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Load failed: {ex.Message}");
            }
        }

        /// <summary>Loads prg.</summary>
        /// <param name="path">The path of the file to use.</param>
        private void LoadPrg(string path)
        {
            LoadPrgFromBytes(File.ReadAllBytes(path));
            SetLastHostLoadedFile(path);
        }

        /// <summary>Ejects disk media when switching to a host-loaded file type.</summary>
        private void EjectDriveMedia()
        {
            iecBus.EjectD64();
            display.DriveActivityLightOn = false;
        }

        /// <summary>Sets last host loaded file.</summary>
        /// <param name="path">The path of the file to use.</param>
        private void SetLastHostLoadedFile(string? path)
        {
            lastHostLoadedFile = path;
            display.SetLoadedFileInTitle(path);
            iecBus.SetHostLooseProgramPresent(!string.IsNullOrWhiteSpace(path));
        }

        /// <summary>Clears last host loaded file.</summary>
        private void ClearLastHostLoadedFile()
        {
            lastHostLoadedFile = null;
            display.SetLoadedFileInTitle(null);
            iecBus.SetHostLooseProgramPresent(false);
        }

        /// <summary>Copies a PRG payload into emulated RAM and updates BASIC pointers when it loads at the BASIC start address.</summary>
        /// <param name="data">The byte data to process.</param>
        /// <param name="loadAddressOverride">An optional load address that overrides the PRG header address.</param>
        /// <returns>The load start and end addresses written in emulated memory.</returns>
        private (ushort LoadAddress, ushort EndAddress) LoadPrgFromBytes(byte[] data, ushort? loadAddressOverride = null)
        {
            if (data.Length < 3)
                throw new InvalidDataException("PRG file is too small (need 2-byte header + body).");

            ushort loadAddr = loadAddressOverride ?? (ushort)(data[0] | (data[1] << 8));
            int progLen = data.Length - 2;
            byte[] mem = cpu.memory.memory;

            if (loadAddr + progLen > mem.Length)
                throw new InvalidDataException("PRG file would load past end of memory.");

            for (int i = 0; i < progLen; i++)
                cpu.memory.WriteRamByte((ulong)(loadAddr + i), data[2 + i]);

            int endAddr = loadAddr + progLen;
            if (loadAddr == 0x0801)
            {
                cpu.memory.WriteByte(0x002D, (byte)(endAddr & 0xFF));
                cpu.memory.WriteByte(0x002E, (byte)(endAddr >> 8));
                cpu.memory.WriteByte(0x002F, (byte)(endAddr & 0xFF));
                cpu.memory.WriteByte(0x0030, (byte)(endAddr >> 8));
                cpu.memory.WriteByte(0x0031, (byte)(endAddr & 0xFF));
                cpu.memory.WriteByte(0x0032, (byte)(endAddr >> 8));
            }

            return (loadAddr, (ushort)endAddr);
        }

        /// <summary>Copies a decoded tape entry into emulated RAM and updates BASIC pointers for BASIC tape programs.</summary>
        /// <param name="entry">The decoded tape entry to load.</param>
        /// <returns>The load start and end addresses written in emulated memory.</returns>
        private (ushort LoadAddress, ushort EndAddress) LoadTapeEntry(TapeEntry entry)
        {
            byte[] mem = cpu.memory.memory;
            int dataLen = entry.Data.Length;

            if (entry.LoadAddress + dataLen > mem.Length)
                throw new InvalidDataException("would load past end of memory.");

            for (int i = 0; i < dataLen; i++)
                cpu.memory.WriteRamByte((ulong)(entry.LoadAddress + i), entry.Data[i]);

            int endAddr = entry.LoadAddress + dataLen;
            if (entry.IsBasic)
            {
                cpu.memory.WriteByte(0x002D, (byte)(endAddr & 0xFF));
                cpu.memory.WriteByte(0x002E, (byte)(endAddr >> 8));
                cpu.memory.WriteByte(0x002F, (byte)(endAddr & 0xFF));
                cpu.memory.WriteByte(0x0030, (byte)(endAddr >> 8));
                cpu.memory.WriteByte(0x0031, (byte)(endAddr & 0xFF));
                cpu.memory.WriteByte(0x0032, (byte)(endAddr >> 8));
            }

            return (entry.LoadAddress, (ushort)endAddr);
        }

        /// <summary>Loads text.</summary>
        /// <param name="path">The path of the file to use.</param>
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
                keyboard.EnqueuePetscii(0x0D); /// RETURN
            }
        }

        /// <summary>Implements the ascii char to petscii helper.</summary>
        /// <param name="ch">The character to convert or write.</param>
        /// <returns>The byte value produced by the operation.</returns>
        private static byte AsciiCharToPetscii(char ch)
        {
            if (ch >= 'a' && ch <= 'z') return (byte)('A' + (ch - 'a'));
            if (ch >= ' ' && ch <= '~') return (byte)ch;
            return 0;
        }

        /// <summary>Saves program.</summary>
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

            bool wasPaused = IsPaused;
            SetPaused(true);
            display.RedrawScreen();

            try
            {
                string? filename = SaveFileWindow.Prompt(DefaultSaveFilename());
                if (string.IsNullOrWhiteSpace(filename))
                    return;

                string softwareDir = SoftwareDirectory.Ensure();
                string path = Path.Combine(softwareDir, NormalizePrgFilename(filename));
                SaveMemoryRangeAsPrg(path, mem, 0x0801, (ushort)(0x0801 + progLen));
                Console.WriteLine($"Saved {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Save failed: {ex.Message}");
            }
            finally
            {
                SetPaused(wasPaused);
            }
        }

        /// <summary>Saves memory range as prg.</summary>
        /// <param name="path">The path of the file to use.</param>
        /// <param name="mem">The emulated memory buffer to inspect or update.</param>
        /// <param name="start">The first address in the memory range.</param>
        /// <param name="end">The address just after the memory range.</param>
        private static void SaveMemoryRangeAsPrg(string path, byte[] mem, ushort start, ushort end)
        {
            int length = end - start;
            if (length <= 0)
                throw new InvalidDataException("empty save range.");

            using var fs = File.Create(path);
            fs.WriteByte((byte)(start & 0xFF));
            fs.WriteByte((byte)(start >> 8));
            fs.Write(mem, start, length);
        }

        /// <summary>Normalizes prg filename.</summary>
        /// <param name="raw">The raw bytes to decode.</param>
        /// <returns>The string value produced by the operation.</returns>
        private static string NormalizePrgFilename(string raw)
        {
            string name = raw.Trim().Trim('"', '\'');

            if (name.StartsWith("@", StringComparison.Ordinal))
                name = name.Substring(1);

            int commaIndex = name.IndexOf(',');
            if (commaIndex >= 0)
                name = name.Substring(0, commaIndex);

            int colonIndex = name.LastIndexOf(':');
            if (colonIndex >= 0 && colonIndex < name.Length - 1)
                name = name.Substring(colonIndex + 1);

            name = Path.GetFileName(name);

            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');

            if (string.IsNullOrWhiteSpace(name))
                name = "program";

            if (!name.EndsWith(".prg", StringComparison.OrdinalIgnoreCase))
                name += ".prg";

            return name;
        }

        /// <summary>Builds the default PRG save filename.</summary>
        /// <returns>The string value produced by the operation.</returns>
        private string DefaultSaveFilename()
        {
            if (!string.IsNullOrWhiteSpace(lastHostLoadedFile))
            {
                string? name = Path.GetFileNameWithoutExtension(lastHostLoadedFile);
                if (!string.IsNullOrWhiteSpace(name))
                    return name + ".prg";
            }

            return "program.prg";
        }
    }
}
