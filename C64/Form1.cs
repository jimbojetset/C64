using _6502CPU;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;


namespace C64
{
    public partial class Form1 : Form
    {
        private _6502_CPU cpu;
        public bool V_Sync = false;
        private const int Clock_PAL = 985_248;   // 6510 @ PAL
        private const int Clock_NTSC = 1_022_727; // 6510 @ NTSC

        // VIC-II visible area is 320 x 200 pixels.
        private const int ScreenW = 320;
        private const int ScreenH = 200;

        // PAL VIC-II: 312 raster lines per frame, 50 frames per second.
        private const int PalRasterLines = 312;
        private const int RasterLinesPerSecond = PalRasterLines * 50;
       
        // KERNAL IRQ (CIA-1 timer A on a real C64) at PAL frame rate.
        private const int IrqHz = 50;

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

        private PictureBox? screenBox;
        private Bitmap? screenBitmap;
        private readonly byte[] pixelBuf = new byte[ScreenW * ScreenH * 4];

        private readonly bool[] fgMask = new bool[ScreenW * ScreenH];

        private readonly byte[] spriteMask = new byte[ScreenW * ScreenH];

        private readonly byte[] scrollBuf = new byte[ScreenW * ScreenH * 4];

        private int rasterCompare;
        private int currentRasterLine;
        private bool rasterIrqPendingThisFrame;

        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private Thread? cpuThread;
        private Thread? rasterThread;
        private Thread? irqThread;
        private System.Windows.Forms.Timer? uiTimer;

        private readonly ConcurrentQueue<byte> keyQueue = new ConcurrentQueue<byte>();

        // CIA-1 port A ($DC00) is read by games as joystick port 2. Each
        // bit is active-low: 0 = pressed. We never use the keyboard matrix
        // (we inject PETSCII directly into the buffer at $0277) so we own
        // this register for joystick purposes.
        private byte joystick2 = 0xFF;

        public Form1()
        {
            InitializeComponent();

            cpu = new _6502_CPU(Clock_PAL);
            cpu.memory.Load(@"ROMS\basic.901226-01.bin", 0xA000, 0x2000, true);
            cpu.memory.Load(@"ROMS\kernal.901227-03.bin", 0xE000, 0x2000, true);
            cpu.memory.Load(@"ROMS\characters.901225-01.bin", 0xD000, 0x1000, false);
            charRom = File.ReadAllBytes(@"ROMS\characters.901225-01.bin");

            // Fast-boot: skip RAMTAS. Patches the KERNAL ROM image once;
            // survives a soft reset because we never reload the ROM.
            cpu.memory.memory[0xFCF5] = 0xEA;
            cpu.memory.memory[0xFCF6] = 0xEA;
            cpu.memory.memory[0xFCF7] = 0xEA;

            InitHardware();

            // Subsequent (Ctrl+R) resets re-run InitHardware on the CPU
            // thread so it doesn't race with instruction execution.
            cpu.OnReset = InitHardware;

            rasterCompare = 0;

            cpu.memory.OnIOWrite = OnIOWrite;
            cpu.memory.OnIOPostRead = OnIOPostRead;

            Load += Form1_Load;
            FormClosing += Form1_FormClosing;

            KeyPreview = true;
            KeyDown += Form1_KeyDown;
            KeyUp   += Form1_KeyUp;
        }

        // Per-reset RAM / VIC / colour-RAM / screen-RAM setup. Replicates
        // what the KERNAL would leave behind after RAMTAS + IOINIT + CINT,
        // so the renderer always sees sane state from the very first frame
        // and a Ctrl+R hard reset returns to a clean boot.
        private void InitHardware()
        {
            byte[] m = cpu.memory.memory;

            // Zero RAM ($0000-$9FFF). ROM ($A000-$BFFF BASIC, $E000-$FFFF
            // KERNAL) and the I/O region ($D000-$DFFF, which we re-init
            // below) are left untouched.
            Array.Clear(m, 0x0000, 0xA000);

            // RAMTAS leaves behind these workspace pointers; mirror them.
            m[0x0281] = 0x00; m[0x0282] = 0x08; // MEMSTR = $0800
            m[0x0283] = 0x00; m[0x0284] = 0xA0; // MEMSIZ = $A000
            m[0x0288] = 0x04;                   // screen page = $0400

            // CIA-1 keyboard matrix: report "no keys pressed".
            m[0xDC00] = 0xFF;
            m[0xDC01] = 0xFF;

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

            // Drain any queued keystrokes from the previous session.
            while (keyQueue.TryDequeue(out _)) { }
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
            }
            return false;
        }

        private void OnIOPostRead(ulong addr)
        {
            switch (addr)
            {
                case 0xD01E: // sprite-sprite collision latch
                case 0xD01F: // sprite-data   collision latch
                    cpu.memory.memory[addr] = 0;
                    break;
            }
        }

        private void Form1_Load(object? sender, EventArgs e)
        {
            InitRenderer();
            var token = cts.Token;

            cpuThread = new Thread(() =>
            {
                try
                {
                    cpu.Run();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"CPU thread crashed: {ex}");
                }
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
                Name = "VIC-II raster"
            };
            rasterThread.Start();

            irqThread = new Thread(() => IrqLoop(token))
            {
                IsBackground = true,
                Name = "CIA-1 IRQ"
            };
            irqThread.Start();

            uiTimer = new System.Windows.Forms.Timer { Interval = 33 };
            uiTimer.Tick += (_, _) => RedrawScreen();
            uiTimer.Start();
        }

        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            uiTimer?.Stop();
            cts.Cancel();
        }

        private void RasterLoop(CancellationToken token)
        {
            long ticksPerLine = Stopwatch.Frequency / RasterLinesPerSecond;
            long next = Stopwatch.GetTimestamp() + ticksPerLine;
            int line = 0;
            byte[] mem = cpu.memory.memory;
            while (!token.IsCancellationRequested)
            {
                currentRasterLine = line;

                mem[0xD012] = (byte)(line & 0xFF);
                byte d011 = mem[0xD011];
                d011 = (byte)((d011 & 0x7F) | (((line >> 8) & 1) << 7));
                mem[0xD011] = d011;

                if (line == rasterCompare && !rasterIrqPendingThisFrame)
                {
                    rasterIrqPendingThisFrame = true;
                    bool rasterIrqEnabled = (mem[0xD01A] & 0x01) != 0;
                    if (rasterIrqEnabled)
                    {
                        mem[0xD019] = (byte)(mem[0xD019] | 0x81);
                        cpu.InitiateIRQ(0xFFFE);
                    }
                }

                line++;
                if (line >= PalRasterLines)
                {
                    line = 0;
                    rasterIrqPendingThisFrame = false;
                }

                while (Stopwatch.GetTimestamp() < next)
                    Thread.SpinWait(1);
                next += ticksPerLine;
            }
        }

        private void IrqLoop(CancellationToken token)
        {
            long ticksPerTick = Stopwatch.Frequency / IrqHz;
            long next = Stopwatch.GetTimestamp() + ticksPerTick;
            while (!token.IsCancellationRequested)
            {
                DrainKeyboardQueue();
                cpu.InitiateIRQ(0xFFFE);

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

        private void InitRenderer()
        {
            textBox1.Visible = false;
            screenBitmap = new Bitmap(ScreenW, ScreenH, PixelFormat.Format32bppArgb);
            screenBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
            };
            screenBox.Paint += (s, e) =>
            {
                if (screenBitmap is null) return;
                e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;

                var bounds = screenBox.ClientRectangle;
                const float aspect = (float)ScreenW / ScreenH;
                int w = bounds.Width;
                int h = (int)(w / aspect);
                if (h > bounds.Height) { h = bounds.Height; w = (int)(h * aspect); }
                int x = (bounds.Width - w) / 2;
                int y = (bounds.Height - h) / 2;
                e.Graphics.DrawImage(screenBitmap, x, y, w, h);
            };
            panel1.Controls.Add(screenBox);
            screenBox.BringToFront();
        }

        private int VicBankBase()
        {
            int sel = cpu.memory.memory[0xDD00] & 0x03;
            return (3 - sel) * 0x4000;
        }

        private void RedrawScreen()
        {
            if (!IsHandleCreated || screenBox is null || screenBitmap is null) return;

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

            int xScroll = d016 & 0x07;
            int yScroll = (d011 & 0x07) - 3;

            Array.Clear(fgMask, 0, fgMask.Length);
            // Sprite occupancy mask is also per-frame; collision detection
            // only considers sprites visible in the current frame.
            Array.Clear(spriteMask, 0, spriteMask.Length);

            if (!screenOn)
                FillSolid(bg0);
            else if (bmm && mcm)
                RenderMulticolorBitmap(screenAddr, charAddr, bank, d018, bg0);
            else if (bmm)
                RenderHiresBitmap(screenAddr, bank, d018);
            else if (ecm)
                RenderExtendedBgText(screenAddr, charAddr, bg0, bg1, bg2, bg3);
            else if (mcm)
                RenderMulticolorText(screenAddr, charAddr, bg0, bg1, bg2);
            else
                RenderStandardText(screenAddr, charAddr, bg0);

            // Sprite overlay (always rendered when display is on).
            if (screenOn)
                RenderSprites(screenAddr, bank);

            // Apply smooth-scroll shift, if any. We do this after sprites so
            // the entire composition scrolls together, matching how a real
            // VIC moves its visible window relative to the display area.
            if (xScroll != 0 || yScroll != 0)
                ApplyScroll(xScroll, yScroll, bg0);

            // Blit pixelBuf -> Bitmap once per frame.
            var rect = new Rectangle(0, 0, ScreenW, ScreenH);
            BitmapData data = screenBitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(pixelBuf, 0, data.Scan0, pixelBuf.Length);
            screenBitmap.UnlockBits(data);
            screenBox.Invalidate();
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

        private void FillSolid(byte colorIdx)
        {
            int c = C64Palette[colorIdx & 0x0F];
            for (int i = 0; i < pixelBuf.Length; i += 4)
            {
                pixelBuf[i] = (byte)c;
                pixelBuf[i + 1] = (byte)(c >> 8);
                pixelBuf[i + 2] = (byte)(c >> 16);
                pixelBuf[i + 3] = 0xFF;
            }
        }

        private void RenderStandardText(int screenAddr, int charAddr, byte bg)
        {
            byte[] mem = cpu.memory.memory;
            ResolveCharSource(charAddr, VicBankBase(), out byte[] cs, out int cb);
            int bgC = C64Palette[bg];

            for (int row = 0; row < 25; row++)
            {
                int rowBase = row * 40;
                for (int col = 0; col < 40; col++)
                {
                    byte code = mem[screenAddr + rowBase + col];
                    int fgC = C64Palette[mem[0xD800 + rowBase + col] & 0x0F];
                    int charPtr = cb + code * 8;

                    for (int dy = 0; dy < 8; dy++)
                    {
                        byte bits = cs[charPtr + dy];
                        int py = row * 8 + dy;
                        int p = (py * ScreenW + col * 8) * 4;
                        int fgIdx = py * ScreenW + col * 8;
                        for (int dx = 0; dx < 8; dx++)
                        {
                            bool on = (bits & (0x80 >> dx)) != 0;
                            int c = on ? fgC : bgC;
                            pixelBuf[p] = (byte)c;
                            pixelBuf[p + 1] = (byte)(c >> 8);
                            pixelBuf[p + 2] = (byte)(c >> 16);
                            pixelBuf[p + 3] = 0xFF;
                            fgMask[fgIdx] = on;
                            p += 4;
                            fgIdx++;
                        }
                    }
                }
            }
        }

        private void RenderMulticolorText(int screenAddr, int charAddr, byte bg0, byte bg1, byte bg2)
        {
            byte[] mem = cpu.memory.memory;
            ResolveCharSource(charAddr, VicBankBase(), out byte[] cs, out int cb);
            int bgC = C64Palette[bg0];
            int[] mcc = { bgC, C64Palette[bg1], C64Palette[bg2], 0 };

            for (int row = 0; row < 25; row++)
            {
                int rowBase = row * 40;
                for (int col = 0; col < 40; col++)
                {
                    byte code = mem[screenAddr + rowBase + col];
                    byte colRam = (byte)(mem[0xD800 + rowBase + col] & 0x0F);
                    bool cellMc = (colRam & 0x08) != 0;
                    int fgC = C64Palette[colRam & (cellMc ? 0x07 : 0x0F)];
                    int charPtr = cb + code * 8;

                    if (cellMc)
                    {
                        mcc[3] = fgC;
                        for (int dy = 0; dy < 8; dy++)
                        {
                            byte bits = cs[charPtr + dy];
                            int py = row * 8 + dy;
                            int p = (py * ScreenW + col * 8) * 4;
                            int fgIdx = py * ScreenW + col * 8;
                            for (int pair = 0; pair < 4; pair++)
                            {
                                int pix = (bits >> ((3 - pair) * 2)) & 0x03;
                                int c = mcc[pix];
                                bool fg = pix == 3; // priority: only mc colour 11 counts as fg
                                pixelBuf[p] = (byte)c;
                                pixelBuf[p + 1] = (byte)(c >> 8);
                                pixelBuf[p + 2] = (byte)(c >> 16);
                                pixelBuf[p + 3] = 0xFF;
                                pixelBuf[p + 4] = (byte)c;
                                pixelBuf[p + 5] = (byte)(c >> 8);
                                pixelBuf[p + 6] = (byte)(c >> 16);
                                pixelBuf[p + 7] = 0xFF;
                                fgMask[fgIdx] = fg;
                                fgMask[fgIdx + 1] = fg;
                                p += 8;
                                fgIdx += 2;
                            }
                        }
                    }
                    else
                    {
                        for (int dy = 0; dy < 8; dy++)
                        {
                            byte bits = cs[charPtr + dy];
                            int py = row * 8 + dy;
                            int p = (py * ScreenW + col * 8) * 4;
                            int fgIdx = py * ScreenW + col * 8;
                            for (int dx = 0; dx < 8; dx++)
                            {
                                bool on = (bits & (0x80 >> dx)) != 0;
                                int c = on ? fgC : bgC;
                                pixelBuf[p] = (byte)c;
                                pixelBuf[p + 1] = (byte)(c >> 8);
                                pixelBuf[p + 2] = (byte)(c >> 16);
                                pixelBuf[p + 3] = 0xFF;
                                fgMask[fgIdx] = on;
                                p += 4;
                                fgIdx++;
                            }
                        }
                    }
                }
            }
        }

        private void RenderExtendedBgText(int screenAddr, int charAddr, byte bg0, byte bg1, byte bg2, byte bg3)
        {
            byte[] mem = cpu.memory.memory;
            ResolveCharSource(charAddr, VicBankBase(), out byte[] cs, out int cb);
            int[] bgC = { C64Palette[bg0], C64Palette[bg1], C64Palette[bg2], C64Palette[bg3] };

            for (int row = 0; row < 25; row++)
            {
                int rowBase = row * 40;
                for (int col = 0; col < 40; col++)
                {
                    byte code = mem[screenAddr + rowBase + col];
                    int fgC = C64Palette[mem[0xD800 + rowBase + col] & 0x0F];
                    int b = bgC[(code >> 6) & 0x03];
                    int charPtr = cb + (code & 0x3F) * 8;

                    for (int dy = 0; dy < 8; dy++)
                    {
                        byte bits = cs[charPtr + dy];
                        int py = row * 8 + dy;
                        int p = (py * ScreenW + col * 8) * 4;
                        int fgIdx = py * ScreenW + col * 8;
                        for (int dx = 0; dx < 8; dx++)
                        {
                            bool on = (bits & (0x80 >> dx)) != 0;
                            int c = on ? fgC : b;
                            pixelBuf[p] = (byte)c;
                            pixelBuf[p + 1] = (byte)(c >> 8);
                            pixelBuf[p + 2] = (byte)(c >> 16);
                            pixelBuf[p + 3] = 0xFF;
                            fgMask[fgIdx] = on;
                            p += 4;
                            fgIdx++;
                        }
                    }
                }
            }
        }

        private void RenderHiresBitmap(int screenAddr, int bank, byte d018)
        {
            byte[] mem = cpu.memory.memory;
            int bitmapAddr = bank + (((d018 & 0x08) != 0) ? 0x2000 : 0x0000);

            for (int row = 0; row < 25; row++)
            {
                int rowBase = row * 40;
                for (int col = 0; col < 40; col++)
                {
                    byte clr = mem[screenAddr + rowBase + col];
                    int fgC = C64Palette[(clr >> 4) & 0x0F];
                    int bgC = C64Palette[clr & 0x0F];
                    int cellBase = bitmapAddr + (rowBase * 8) + col * 8;

                    for (int dy = 0; dy < 8; dy++)
                    {
                        byte bits = mem[cellBase + dy];
                        int py = row * 8 + dy;
                        int p = (py * ScreenW + col * 8) * 4;
                        int fgIdx = py * ScreenW + col * 8;
                        for (int dx = 0; dx < 8; dx++)
                        {
                            bool on = (bits & (0x80 >> dx)) != 0;
                            int c = on ? fgC : bgC;
                            pixelBuf[p] = (byte)c;
                            pixelBuf[p + 1] = (byte)(c >> 8);
                            pixelBuf[p + 2] = (byte)(c >> 16);
                            pixelBuf[p + 3] = 0xFF;
                            fgMask[fgIdx] = on;
                            p += 4;
                            fgIdx++;
                        }
                    }
                }
            }
        }

        private void RenderMulticolorBitmap(int screenAddr, int charAddr, int bank, byte d018, byte bg0)
        {
            byte[] mem = cpu.memory.memory;
            int bitmapAddr = bank + (((d018 & 0x08) != 0) ? 0x2000 : 0x0000);
            int bgC = C64Palette[bg0];

            for (int row = 0; row < 25; row++)
            {
                int rowBase = row * 40;
                for (int col = 0; col < 40; col++)
                {
                    byte clr = mem[screenAddr + rowBase + col];
                    int cFg1 = C64Palette[(clr >> 4) & 0x0F];
                    int cFg2 = C64Palette[clr & 0x0F];
                    int cFg3 = C64Palette[mem[0xD800 + rowBase + col] & 0x0F];
                    int cellBase = bitmapAddr + (rowBase * 8) + col * 8;

                    for (int dy = 0; dy < 8; dy++)
                    {
                        byte bits = mem[cellBase + dy];
                        int py = row * 8 + dy;
                        int p = (py * ScreenW + col * 8) * 4;
                        int fgIdx = py * ScreenW + col * 8;
                        for (int pair = 0; pair < 4; pair++)
                        {
                            int pix = (bits >> ((3 - pair) * 2)) & 0x03;
                            int c = pix switch { 0 => bgC, 1 => cFg1, 2 => cFg2, _ => cFg3 };
                            bool fg = pix != 0;
                            pixelBuf[p] = (byte)c;
                            pixelBuf[p + 1] = (byte)(c >> 8);
                            pixelBuf[p + 2] = (byte)(c >> 16);
                            pixelBuf[p + 3] = 0xFF;
                            pixelBuf[p + 4] = (byte)c;
                            pixelBuf[p + 5] = (byte)(c >> 8);
                            pixelBuf[p + 6] = (byte)(c >> 16);
                            pixelBuf[p + 7] = 0xFF;
                            fgMask[fgIdx] = fg;
                            fgMask[fgIdx + 1] = fg;
                            p += 8;
                            fgIdx += 2;
                        }
                    }
                }
            }
        }

        // ------------------------------------------------------------
        //  Sprites
        // ------------------------------------------------------------

        // Sprites are 24x21 (single size) or 48x42 (expanded). Position
        // is given in display coords - to land on framebuffer (0,0) the
        // sprite (X,Y) must be (24,50). Bytes for each sprite live in the
        // current VIC bank at (pointer * 64).
        private void RenderSprites(int screenAddr, int bank)
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

            // Sprite pointers live at the top of the screen-RAM block.
            int pointerBase = screenAddr + 0x03F8;

            // Render highest-numbered sprite first; lower numbers have
            // priority so they paint last and overlay the others.
            for (int s = 7; s >= 0; s--)
            {
                int mask = 1 << s;
                if ((enable & mask) == 0) continue;

                int sx = mem[0xD000 + s * 2] | (((xHigh & mask) != 0) ? 0x100 : 0);
                int sy = mem[0xD000 + s * 2 + 1];

                // Display coordinates -> framebuffer coordinates.
                int fbX = sx - 24;
                int fbY = sy - 50;

                int spritePtr = mem[pointerBase + s];
                int dataAddr = bank + spritePtr * 64;

                bool xExp = (xExpand & mask) != 0;
                bool yExp = (yExpand & mask) != 0;
                bool mc = (multicolor & mask) != 0;
                bool behindBg = (priority & mask) != 0;
                int color = C64Palette[mem[0xD027 + s] & 0x0F];

                for (int row = 0; row < 21; row++)
                {
                    int rowAddr = dataAddr + row * 3;
                    // 24 bits = 3 bytes per row.
                    int rowBits = (mem[rowAddr] << 16) | (mem[rowAddr + 1] << 8) | mem[rowAddr + 2];

                    int yRepeats = yExp ? 2 : 1;
                    for (int yr = 0; yr < yRepeats; yr++)
                    {
                        int destY = fbY + row * yRepeats + yr;
                        if ((uint)destY >= ScreenH) continue;

                        if (mc)
                        {
                            // 12 logical pixels, each 2 bits, doubled width.
                            for (int p = 0; p < 12; p++)
                            {
                                int code = (rowBits >> ((11 - p) * 2)) & 0x03;
                                if (code == 0) continue; // transparent
                                int c = code switch { 1 => mc1, 2 => color, _ => mc2 };
                                int basePix = p * 2 * (xExp ? 2 : 1);
                                int width = 2 * (xExp ? 2 : 1);
                                for (int w = 0; w < width; w++)
                                    PaintSpritePixel(fbX + basePix + w, destY, c, behindBg, s);
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
                                    PaintSpritePixel(fbX + basePix + w, destY, color, behindBg, s);
                            }
                        }
                    }
                }
            }
        }

        private void PaintSpritePixel(int x, int y, int color, bool behindBg, int spriteIdx)
        {
            if ((uint)x >= ScreenW || (uint)y >= ScreenH) return;
            int idx = y * ScreenW + x;
            byte myBit = (byte)(1 << spriteIdx);
            byte[] mem = cpu.memory.memory;

            // Sprite-sprite collision: this pixel already has another
            // sprite's bit set. Latch both bits in $D01E and (if enabled)
            // raise the IRQ. The latch is cleared by the CPU reading the
            // register; until then no new IRQ fires (edge-triggered).
            byte priorSprites = spriteMask[idx];
            if (priorSprites != 0)
            {
                mem[0xD01E] |= (byte)(priorSprites | myBit);
                if ((mem[0xD01A] & 0x04) != 0 && (mem[0xD019] & 0x04) == 0)
                {
                    mem[0xD019] |= 0x84; // bit 2 (source) + bit 7 (IRQ active)
                    cpu.InitiateIRQ(0xFFFE);
                }
            }

            // Sprite-data collision: sprite pixel overlaps a foreground
            // background pixel (i.e. fgMask is set).
            if (fgMask[idx])
            {
                mem[0xD01F] |= myBit;
                if ((mem[0xD01A] & 0x02) != 0 && (mem[0xD019] & 0x02) == 0)
                {
                    mem[0xD019] |= 0x82; // bit 1 (source) + bit 7 (IRQ active)
                    cpu.InitiateIRQ(0xFFFE);
                }
            }

            spriteMask[idx] |= myBit;

            // Priority decision is independent of collision detection: even
            // a hidden sprite pixel counts for collision purposes.
            if (behindBg && fgMask[idx]) return;

            int p = idx * 4;
            pixelBuf[p]     = (byte)color;
            pixelBuf[p + 1] = (byte)(color >> 8);
            pixelBuf[p + 2] = (byte)(color >> 16);
            pixelBuf[p + 3] = 0xFF;
        }

        // ------------------------------------------------------------
        //  Smooth scroll
        // ------------------------------------------------------------

        // Shifts the framebuffer by (xs, ys) pixels, filling exposed
        // edges with the background colour. Done after sprites so the
        // entire composition scrolls together.
        private void ApplyScroll(int xs, int ys, byte bg)
        {
            int bgC = C64Palette[bg & 0x0F];
            // Fill scratch with bg.
            for (int i = 0; i < scrollBuf.Length; i += 4)
            {
                scrollBuf[i] = (byte)bgC;
                scrollBuf[i + 1] = (byte)(bgC >> 8);
                scrollBuf[i + 2] = (byte)(bgC >> 16);
                scrollBuf[i + 3] = 0xFF;
            }

            int copyRows = ScreenH - Math.Abs(ys);
            int copyCols = ScreenW - Math.Abs(xs);
            if (copyRows <= 0 || copyCols <= 0)
            {
                Array.Copy(scrollBuf, pixelBuf, pixelBuf.Length);
                return;
            }

            int srcY = ys > 0 ? 0 : -ys;
            int dstY = ys > 0 ? ys : 0;
            int srcX = xs > 0 ? 0 : -xs;
            int dstX = xs > 0 ? xs : 0;

            for (int y = 0; y < copyRows; y++)
            {
                int srcOff = ((srcY + y) * ScreenW + srcX) * 4;
                int dstOff = ((dstY + y) * ScreenW + dstX) * 4;
                Buffer.BlockCopy(pixelBuf, srcOff, scrollBuf, dstOff, copyCols * 4);
            }
            Array.Copy(scrollBuf, pixelBuf, pixelBuf.Length);
        }

        // ------------------------------------------------------------
        //  File load / save / reset (driven from Ctrl+O / Ctrl+S / Ctrl+R)
        // ------------------------------------------------------------

        // Hard reset: requests the CPU thread to re-run InitHardware and
        // re-point PC at the KERNAL reset vector at the next safe slice
        // boundary. Doing this through cpu.RequestReset rather than
        // touching CPU/memory state from the UI thread eliminates the
        // race that occasionally left the system mid-BRK chain.
        private void HardReset()
        {
            rasterCompare = 0;
            rasterIrqPendingThisFrame = false;
            cpu.RequestReset();
        }

        private void LoadProgram()
        {
            using var dlg = new OpenFileDialog
            {
                Title  = "Load C64 program",
                Filter = "C64 program (*.prg)|*.prg|BASIC source (*.bas;*.txt)|*.bas;*.txt|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            try
            {
                // Treat .bas/.txt as plain text regardless of filter index,
                // and anything else (including filter index 1 = *.prg) as
                // a binary PRG.
                if (ext == ".bas" || ext == ".txt")
                    LoadText(dlg.FileName);
                else
                    LoadPrg(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Load failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            // $2D/$2E (VARTAB)  = end of program + 1.
            // $2F/$30, $31/$32 follow VARTAB to mark start of arrays /
            // strings; setting them equal to VARTAB models the post-NEW
            // state with no variables defined yet.
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
        // keyboard buffer the user normally types into. BASIC processes
        // each line as if it had been typed at the READY prompt.
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
            return 0; // strip anything we can't represent (e.g. tab, NUL)
        }

        // Save the current BASIC program as a PRG file. The end-of-program
        // pointer at $2D/$2E is the authoritative "how much" value BASIC
        // itself uses; bytes from $0801 up to (but not including) that
        // address form the program.
        private void SaveProgram()
        {
            byte[] mem = cpu.memory.memory;
            int endAddr = mem[0x2D] | (mem[0x2E] << 8);
            int progLen = endAddr - 0x0801;
            if (progLen <= 2)
            {
                MessageBox.Show(this, "No BASIC program in memory.", "Save",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dlg = new SaveFileDialog
            {
                Title  = "Save C64 program",
                Filter = "C64 program (*.prg)|*.prg|All files (*.*)|*.*",
                DefaultExt = "prg",
                AddExtension = true,
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                using var fs = File.Create(dlg.FileName);
                fs.WriteByte(0x01); // load address lo
                fs.WriteByte(0x08); // load address hi -> $0801
                fs.Write(mem, 0x0801, progLen);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Save failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

        private byte ToPetscii(KeyEventArgs e)
        {
            bool shift = e.Shift;
            bool ctrl = e.Control;
            bool cbm = e.Alt;

            if (shift && cbm && (e.KeyCode == Keys.Menu || e.KeyCode == Keys.ShiftKey ||
                                 e.KeyCode == Keys.LMenu || e.KeyCode == Keys.RMenu ||
                                 e.KeyCode == Keys.LShiftKey || e.KeyCode == Keys.RShiftKey))
            {
                caseModeUpper = !caseModeUpper;
                return caseModeUpper ? (byte)0x8E : (byte)0x0E;
            }

            if (e.KeyCode >= Keys.F1 && e.KeyCode <= Keys.F8)
                return FunctionKeys[e.KeyCode - Keys.F1];

            if (e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z)
                return (byte)(0x41 + (e.KeyCode - Keys.A));

            if (e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D8)
            {
                int idx = e.KeyCode - Keys.D1;
                if (ctrl) return CtrlColours[idx];
                if (cbm) return CommodoreColours[idx];
            }
            if (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9)
            {
                int d = e.KeyCode - Keys.D0;
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

            if (e.KeyCode >= Keys.NumPad0 && e.KeyCode <= Keys.NumPad9)
                return (byte)(0x30 + (e.KeyCode - Keys.NumPad0));

            return e.KeyCode switch
            {
                Keys.Space => 0x20,
                Keys.Enter => 0x0D,
                Keys.Back => 0x14,
                Keys.Tab => 0x20,
                Keys.Escape => 0x03,
                Keys.Home => 0x13,
                Keys.Insert => 0x94,
                Keys.Delete => 0x14,
                Keys.Left => 0x9D,
                Keys.Right => 0x1D,
                Keys.Up => 0x91,
                Keys.Down => 0x11,
                Keys.OemPeriod => shift ? (byte)'>' : (byte)'.',
                Keys.Oemcomma => shift ? (byte)'<' : (byte)',',
                Keys.OemQuestion => shift ? (byte)'?' : (byte)'/',
                Keys.OemSemicolon => shift ? (byte)':' : (byte)';',
                Keys.OemQuotes => shift ? (byte)'"' : (byte)'\'',
                Keys.OemMinus => shift ? (byte)'_' : (byte)'-',
                Keys.Oemplus => shift ? (byte)'+' : (byte)'=',
                Keys.OemOpenBrackets => shift ? (byte)'{' : (byte)'[',
                Keys.OemCloseBrackets => shift ? (byte)'}' : (byte)']',
                Keys.OemPipe => shift ? (byte)'|' : (byte)'\\',
                _ => 0
            };
        }

        private void Form1_KeyDown(object? sender, KeyEventArgs e)
        {
            // Host-side shortcuts come first so they can't be intercepted
            // by the PETSCII letter-to-buffer path. We only trigger on
            // bare Ctrl+letter (no Shift / no Alt) so Ctrl+1..8 colour
            // codes and Shift+C= case toggle remain unaffected.
            if (e.Control && !e.Shift && !e.Alt)
            {
                switch (e.KeyCode)
                {
                    case Keys.O: LoadProgram(); e.Handled = e.SuppressKeyPress = true; return;
                    case Keys.S: SaveProgram(); e.Handled = e.SuppressKeyPress = true; return;
                    case Keys.R: HardReset();   e.Handled = e.SuppressKeyPress = true; return;
                }
            }

            // Joystick port 2 ($DC00). Arrows + Right-Ctrl set the
            // matching active-low bit. We intentionally don't mark the
            // event handled, so arrow keys still get PETSCII'd through
            // to the keyboard buffer for BASIC editing.
            byte jmask = JoystickMaskFromKey(e.KeyCode);
            if (jmask != 0)
            {
                joystick2 = (byte)(joystick2 & ~jmask);
                cpu.memory.memory[0xDC00] = joystick2;
            }

            byte pet = ToPetscii(e);
            if (pet != 0)
            {
                keyQueue.Enqueue(pet);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void Form1_KeyUp(object? sender, KeyEventArgs e)
        {
            byte jmask = JoystickMaskFromKey(e.KeyCode);
            if (jmask != 0)
            {
                joystick2 = (byte)(joystick2 | jmask);
                cpu.memory.memory[0xDC00] = joystick2;
            }
        }

        private static byte JoystickMaskFromKey(Keys k) => k switch
        {
            Keys.Up          => 0x01,
            Keys.Down        => 0x02,
            Keys.Left        => 0x04,
            Keys.Right       => 0x08,
            Keys.RControlKey => 0x10, // fire
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
