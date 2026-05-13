using System.Diagnostics;
using System.Runtime.InteropServices;
using _6502CPU;
using static SDL2.SDL;

namespace C64
{
    // Standalone VIC-II + SDL display engine. Owns:
    //   - the SDL window / renderer / streaming texture
    //   - the double-buffered framebuffer
    //   - the per-scanline rendering pipeline
    //   - the raster thread (ticks $D012, fires raster IRQs, drives the
    //     scanline renderer, swaps buffers at vsync)
    //   - reset coordination flags so the CPU thread can pause raster
    //     activity while it re-initialises hardware
    //
    // Consumed by C64Emulator. Only depends on the _6502_CPU it's given
    // in the constructor - reads memory directly via cpu.memory.memory[]
    // and raises interrupts via cpu.InitiateIRQ().
    internal sealed class Display : IDisposable
    {
        // ----------------------------------------------------------------
        // Geometry / palette / framebuffer
        // ----------------------------------------------------------------

        // VIC-II visible area is 320 x 200 pixels.
        public const int ScreenW = 320;
        public const int ScreenH = 200;

        // Surround the active 320x200 playfield with a PAL-style border.
        public const int FrameW = 384;
        public const int FrameH = 272;
        private const int FramePlayfieldX = (FrameW - ScreenW) / 2;
        private const int FramePlayfieldY = (FrameH - ScreenH) / 2;

        // PAL VIC-II: 312 raster lines per frame, 50 frames per second.
        private const int PalRasterLines = 312;
        private const int RasterLinesPerSecond = PalRasterLines * 50;

        // PAL playfield: raster lines 51..250 inclusive are the visible
        // 200-line playfield. Lines outside this range are top/bottom
        // border; we just skip rendering them.
        private const int VisibleTop = 51;
        private const int VisibleBottom = 250;

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

        // ----------------------------------------------------------------
        // Fields
        // ----------------------------------------------------------------

        private readonly _6502_CPU cpu;

        // Char ROM bytes - loaded once at Init(). The renderer needs its
        // own copy because the CPU view of $D000-$DFFF is the I/O area
        // (VIC / SID / CIA registers) rather than the character set.
        private byte[] charRom = Array.Empty<byte>();

        // renderBuf  - the raster thread writes here, one scanline at a
        //              time, as that line is reached in the emulated frame.
        // displayBuf - the UI thread blits from here. Swapped at vsync so
        //              the user always sees a complete frame, no tearing.
        private byte[] renderBuf = new byte[ScreenW * ScreenH * 4];
        private byte[] displayBuf = new byte[ScreenW * ScreenH * 4];
        private readonly object swapLock = new object();

        // Per-scanline foreground/sprite masks (320 entries each).
        private readonly bool[] fgLine = new bool[ScreenW];
        private readonly byte[] spriteLine = new byte[ScreenW];

        // SDL handles
        private IntPtr window;
        private IntPtr renderer;
        private IntPtr texture;

        // Raster state shared with the host (raster IRQ compare value,
        // diagnostic line counter, and the reset coordination flags).
        // Marked volatile because they're read/written from multiple
        // threads (CPU thread, raster thread, main UI thread).
        private volatile int rasterCompare;
        private volatile int currentRasterLine;
        private volatile bool isResetting;
        private volatile bool resyncPending;

        private Thread? rasterThread;
        private CancellationToken cancellationToken;

        // ----------------------------------------------------------------
        // Construction / lifecycle
        // ----------------------------------------------------------------

        public Display(_6502_CPU cpu)
        {
            this.cpu = cpu;
        }

        // Public accessors for state the host (C64Emulator) needs to
        // poke or read - $D012/$D011 writes, diagnostic dumps and the
        // IRQ thread's reset-pause check.

        // Raster compare value (0..311). Game code writes the low byte
        // through $D012 and the high bit through $D011 bit 7. The host
        // forwards both writes via this property.
        public int RasterCompare
        {
            get => rasterCompare;
            set => rasterCompare = value;
        }

        // Current emulated raster line (0..311). Read-only from outside.
        // Used for the F11 debug dump.
        public int CurrentRasterLine => currentRasterLine;

        // True while a hard reset is in progress. The CIA IRQ thread
        // checks this to avoid stepping timers and delivering interrupts
        // while the CPU is mid-reset.
        public bool IsResetting => isResetting;

        // Begin a hard reset: pause the raster thread, mark resync, zero
        // the compare value. The CPU thread then re-runs InitHardware
        // and finally calls EndReset() to release things.
        public void BeginReset()
        {
            isResetting = true;
            resyncPending = true;
            rasterCompare = 0;
        }

        // Complete a hard reset: clear stale framebuffer pixels, restart
        // raster timing from line 0, release the IRQ thread.
        public void EndReset()
        {
            currentRasterLine = 0;
            rasterCompare = 0;
            resyncPending = true;
            ClearFramebuffers();
            isResetting = false;
        }

        // Zero both render and display buffers. Called from the CPU
        // thread during hard-reset hardware init so the first post-reset
        // frame can't briefly show stale pixels.
        public void ClearFramebuffers()
        {
            lock (swapLock)
            {
                Array.Clear(renderBuf, 0, renderBuf.Length);
                Array.Clear(displayBuf, 0, displayBuf.Length);
            }
        }

        // Create the SDL window, accelerated renderer and streaming
        // texture; load the character ROM. Must run on the thread that
        // will later pump SDL events (macOS requirement).
        public void Init()
        {
            if (SDL_Init(SDL_INIT_VIDEO) != 0)
                throw new Exception($"SDL_Init failed: {SDL_GetError()}");

            const int initialScale = 3;
            int initialW = FrameW * initialScale * 3 / 4;
            int initialH = FrameH * initialScale * 3 / 4;
            window = SDL_CreateWindow(
                "C64 Emulator",
                SDL_WINDOWPOS_CENTERED, SDL_WINDOWPOS_CENTERED,
                initialW, initialH,
                SDL_WindowFlags.SDL_WINDOW_SHOWN | SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
            if (window == IntPtr.Zero)
                throw new Exception($"SDL_CreateWindow failed: {SDL_GetError()}");

            SDL_EventState(SDL_EventType.SDL_DROPFILE, SDL_ENABLE);

            renderer = SDL_CreateRenderer(window, -1,
                SDL_RendererFlags.SDL_RENDERER_ACCELERATED |
                SDL_RendererFlags.SDL_RENDERER_PRESENTVSYNC);
            if (renderer == IntPtr.Zero)
                throw new Exception($"SDL_CreateRenderer failed: {SDL_GetError()}");

            SDL_SetHint(SDL_HINT_RENDER_SCALE_QUALITY, "0");
            SDL_RenderSetLogicalSize(renderer, FrameW, FrameH);

            texture = SDL_CreateTexture(renderer,
                SDL_PIXELFORMAT_ARGB8888,
                (int)SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING,
                ScreenW, ScreenH);
            if (texture == IntPtr.Zero)
                throw new Exception($"SDL_CreateTexture failed: {SDL_GetError()}");

            // The renderer needs its own copy of the char ROM (the CPU
            // view of $D000-$DFFF is the I/O area instead).
            charRom = File.ReadAllBytes(Path.Combine("ROMS", "characters.901225-01.bin"));
        }

        // Start the raster thread. Must be called after Init().
        public void Start(CancellationToken token)
        {
            cancellationToken = token;
            rasterThread = new Thread(RasterLoop)
            {
                IsBackground = true,
                Name = "VIC-II raster",
                Priority = ThreadPriority.AboveNormal,
            };
            rasterThread.Start();
        }

        // Upload the latest fully-rendered frame to the texture and
        // present it. Called on the main thread - SDL_RenderPresent
        // must run on the thread that created the window.
        public void RedrawScreen()
        {
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

        // Stop the raster thread and destroy SDL resources. Safe to call
        // multiple times.
        public void Dispose()
        {
            // The raster thread observes the same CancellationToken the
            // host passed to Start(). If the host has already cancelled
            // it, the thread will exit; if not we still want to join,
            // but the host should cancel before Dispose-ing.
            try { rasterThread?.Join(200); } catch { }

            if (texture != IntPtr.Zero) { SDL_DestroyTexture(texture); texture = IntPtr.Zero; }
            if (renderer != IntPtr.Zero) { SDL_DestroyRenderer(renderer); renderer = IntPtr.Zero; }
            if (window != IntPtr.Zero) { SDL_DestroyWindow(window); window = IntPtr.Zero; }
        }

        // ----------------------------------------------------------------
        // Raster thread: ticks $D012, fires raster IRQs, drives the per-
        // scanline renderer, and swaps buffers at vsync.
        // ----------------------------------------------------------------

        private void RasterLoop()
        {
            long ticksPerLine = Stopwatch.Frequency / RasterLinesPerSecond;
            long next = Stopwatch.GetTimestamp() + ticksPerLine;
            int line = 0;
            byte[] mem = cpu.memory.memory;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (isResetting)
                {
                    line = 0;
                    currentRasterLine = 0;
                    next = Stopwatch.GetTimestamp() + ticksPerLine;
                    Thread.SpinWait(32);
                    continue;
                }

                if (resyncPending)
                {
                    line = 0;
                    currentRasterLine = 0;
                    resyncPending = false;
                    next = Stopwatch.GetTimestamp() + ticksPerLine;
                }

                currentRasterLine = line;

                mem[0xD012] = (byte)(line & 0xFF);
                byte d011 = mem[0xD011];
                d011 = (byte)((d011 & 0x7F) | (((line >> 8) & 1) << 7));
                mem[0xD011] = d011;

                if (line == rasterCompare)
                {
                    bool rasterIrqEnabled = (mem[0xD01A] & 0x01) != 0;
                    if (rasterIrqEnabled)
                    {
                        mem[0xD019] = (byte)(mem[0xD019] | 0x81);
                        cpu.InitiateIRQ(0xFFFE);
                    }
                }

                if (line >= VisibleTop && line <= VisibleBottom)
                {
                    RenderScanline(line - VisibleTop);
                }

                line++;
                if (line >= PalRasterLines)
                {
                    line = 0;
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

        // ----------------------------------------------------------------
        // Per-scanline rendering (preserved verbatim from the previous
        // partial-class implementation).
        // ----------------------------------------------------------------

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

        private int VicBankBase()
        {
            int sel = cpu.memory.memory[0xDD00] & 0x03;
            return (3 - sel) * 0x4000;
        }

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
    }
}
