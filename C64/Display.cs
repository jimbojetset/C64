using System.Diagnostics;
using _6502CPU;
using static SDL2.SDL;

namespace C64
{
    internal sealed class Display : IDisposable
    {
        public const int ScreenW = 320;
        public const int ScreenH = 200;

        public const int FrameW = 384;
        public const int FrameH = 272;
        private const int FramePlayfieldX = (FrameW - ScreenW) / 2;
        private const int FramePlayfieldY = (FrameH - ScreenH) / 2;

        private const int PalRasterLines = 312;
        private const int RasterLinesPerSecond = PalRasterLines * 50;

        private const int VisibleTop = 51;
        private const int VisibleBottom = 250;

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

        private readonly _6502_CPU cpu;

        private byte[] charRom = Array.Empty<byte>();

        private byte[] renderBuf = new byte[ScreenW * ScreenH * 4];
        private byte[] displayBuf = new byte[ScreenW * ScreenH * 4];
        private readonly object swapLock = new object();

        private readonly bool[] fgLine = new bool[ScreenW];
        private readonly byte[] spriteLine = new byte[ScreenW];

        private byte[] cachedScreenRow = new byte[40];
        private byte[][] cachedBitmapRows = new byte[8][];
        private int[] cachedBitmapRowNum = new int[8];  // Track which row number each cache came from

        private IntPtr window;
        private IntPtr renderer;
        private IntPtr texture;

        private volatile int rasterCompare;
        private volatile int currentRasterLine;
        private volatile bool isResetting;
        private volatile bool resyncPending;

        private Thread? rasterThread;
        private CancellationToken cancellationToken;

        public Display(_6502_CPU cpu)
        {
            this.cpu = cpu;
        }

        public int RasterCompare
        {
            get => rasterCompare;
            set => rasterCompare = value;
        }

        public int CurrentRasterLine => currentRasterLine;

        public bool IsResetting => isResetting;

        public void BeginReset()
        {
            isResetting = true;
            resyncPending = true;
            rasterCompare = 0;
        }

        public void EndReset()
        {
            currentRasterLine = 0;
            rasterCompare = 0;
            resyncPending = true;
            ClearFramebuffers();
            // Invalidate bitmap row cache on reset
            for (int i = 0; i < cachedBitmapRowNum.Length; i++)
                cachedBitmapRowNum[i] = -1;
            isResetting = false;
        }

        public void ClearFramebuffers()
        {
            lock (swapLock)
            {
                Array.Clear(renderBuf, 0, renderBuf.Length);
                Array.Clear(displayBuf, 0, displayBuf.Length);
            }
        }

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

            charRom = File.ReadAllBytes(Path.Combine("ROMS", "characters.901225-01.bin"));
        }

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

        public void Dispose()
        {
            try { rasterThread?.Join(200); } catch { }

            if (texture != IntPtr.Zero) { SDL_DestroyTexture(texture); texture = IntPtr.Zero; }
            if (renderer != IntPtr.Zero) { SDL_DestroyRenderer(renderer); renderer = IntPtr.Zero; }
            if (window != IntPtr.Zero) { SDL_DestroyWindow(window); window = IntPtr.Zero; }
        }

        private void RasterLoop()
        {
            int line = 0;
            long lineNumerator = 0;
            long lastCpuCycles = cpu.TotalCycles;
            byte[] mem = cpu.memory.memory;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (isResetting)
                {
                    line = 0;
                    currentRasterLine = 0;
                    lineNumerator = 0;
                    lastCpuCycles = cpu.TotalCycles;
                    Thread.SpinWait(32);
                    continue;
                }

                if (resyncPending)
                {
                    line = 0;
                    currentRasterLine = 0;
                    resyncPending = false;
                    lineNumerator = 0;
                    lastCpuCycles = cpu.TotalCycles;
                }

                long nowCpuCycles = cpu.TotalCycles;
                long deltaCpuCycles = nowCpuCycles - lastCpuCycles;
                if (deltaCpuCycles < 0)
                {
                    // CPU reset rewinds TotalCycles to 0; resync baseline immediately
                    // so the raster thread does not stall on a negative delta.
                    lastCpuCycles = nowCpuCycles;
                    lineNumerator = 0;
                    continue;
                }
                if (deltaCpuCycles == 0)
                {
                    Thread.SpinWait(16);
                    continue;
                }
                lastCpuCycles = nowCpuCycles;

                lineNumerator += deltaCpuCycles * RasterLinesPerSecond;
                long linesToAdvance = lineNumerator / cpu.ClockFrequency;
                lineNumerator %= cpu.ClockFrequency;

                if (linesToAdvance <= 0)
                    continue;

                if (linesToAdvance > PalRasterLines * 4)
                    linesToAdvance = PalRasterLines * 4;

                while (linesToAdvance-- > 0)
                {
                    currentRasterLine = line;

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
                        byte d011 = mem[0xD011];
                        byte d016 = mem[0xD016];
                        byte d018 = mem[0xD018];
                        byte bg0 = (byte)(mem[0xD021] & 0x0F);
                        byte bg1 = (byte)(mem[0xD022] & 0x0F);
                        byte bg2 = (byte)(mem[0xD023] & 0x0F);
                        byte bg3 = (byte)(mem[0xD024] & 0x0F);
                        byte dd00 = mem[0xDD00];
                        byte dd02 = mem[0xDD02];
                        byte spriteEnable = mem[0xD015];
                        byte spriteXExpand = mem[0xD01D];
                        byte spriteYExpand = mem[0xD017];
                        byte spriteMulticolor = mem[0xD01C];
                        byte spritePriority = mem[0xD01B];
                        byte spriteXHigh = mem[0xD010];
                        byte spriteMc1Color = mem[0xD025];
                        byte spriteMc2Color = mem[0xD026];
                        byte[] spriteColors = new byte[8];
                        byte[] spriteXPos = new byte[8];
                        byte[] spriteYPos = new byte[8];
                        byte[] spritePtrs = new byte[8];
                        for (int i = 0; i < 8; i++)
                        {
                            spriteColors[i] = mem[0xD027 + i];
                            spriteXPos[i] = mem[0xD000 + i * 2];
                            spriteYPos[i] = mem[0xD001 + i * 2];
                        }

                        int playY = line - VisibleTop;
                        int fineY = d011 & 0x07;
                        int fineYOffset = (fineY + 1) >> 1;
                        int scrolledY = playY - fineYOffset;
                        int row = scrolledY >> 3;
                        int dy = scrolledY & 0x07;
                        int wrappedRow = ((row % 25) + 25) % 25;

                        bool matrixVisible = playY >= 0 && playY < ScreenH;
                        int bank = GetVicBankBase(dd00, dd02);
                        int screenAddr = bank + ((d018 >> 4) & 0x0F) * 0x400;
                        int spritePtrBase = screenAddr + 0x03F8;
                        for (int i = 0; i < 8; i++)
                            spritePtrs[i] = cpu.memory.ReadVicByte((ulong)(spritePtrBase + i));

                        // Snapshot row data every scanline so raster splits never reuse stale data.
                        if (matrixVisible)
                        {
                            for (int col = 0; col < 40; col++)
                            {
                                cachedScreenRow[col] = cpu.memory.ReadVicByte((ulong)(screenAddr + wrappedRow * 40 + col));
                            }

                            int bitmapAddr = bank + (((d018 & 0x08) != 0) ? 0x2000 : 0x0000);
                            if (cachedBitmapRows[dy] == null || cachedBitmapRowNum[dy] != wrappedRow)
                            {
                                cachedBitmapRows[dy] = new byte[40];  // One byte per column for this dy
                                cachedBitmapRowNum[dy] = wrappedRow;
                            }
                            for (int col = 0; col < 40; col++)
                            {
                                cachedBitmapRows[dy][col] = cpu.memory.ReadVicByte((ulong)(bitmapAddr + (wrappedRow * 40 + col) * 8 + dy));
                            }
                        }

                        byte[] colorRow = new byte[40];
                        if (matrixVisible)
                        {
                            for (int col = 0; col < 40; col++)
                            {
                                colorRow[col] = mem[0xD800 + wrappedRow * 40 + col];
                            }
                        }

                        RenderScanline(playY, d011, d016, d018, bg0, bg1, bg2, bg3, dd00, dd02, spriteEnable, spriteXExpand, spriteYExpand, spriteMulticolor, spritePriority, spriteXHigh, spriteMc1Color, spriteMc2Color, spriteColors, spriteXPos, spriteYPos, spritePtrs, colorRow, cachedScreenRow, cachedBitmapRows, dy, matrixVisible);
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
                }
            }
        }

        private void RenderScanline(int y, byte d011, byte d016, byte d018, byte bg0, byte bg1, byte bg2, byte bg3, byte dd00, byte dd02, byte spriteEnable, byte spriteXExpand, byte spriteYExpand, byte spriteMulticolor, byte spritePriority, byte spriteXHigh, byte spriteMc1Color, byte spriteMc2Color, byte[] spriteColors, byte[] spriteXPos, byte[] spriteYPos, byte[] spritePtrs, byte[] colorRow, byte[] cachedScreenRow, byte[][] cachedBitmapRows, int dy, bool matrixVisible)
        {
            int bank = GetVicBankBase(dd00, dd02);
            int screenAddr = bank + ((d018 >> 4) & 0x0F) * 0x400;
            int charAddr = bank + ((d018 >> 1) & 0x07) * 0x800;

            bool screenOn = (d011 & 0x10) != 0;
            bool bmm = (d011 & 0x20) != 0;
            bool ecm = (d011 & 0x40) != 0;
            bool mcm = (d016 & 0x10) != 0;

            Array.Clear(fgLine, 0, fgLine.Length);
            Array.Clear(spriteLine, 0, spriteLine.Length);

            if (!screenOn || !matrixVisible)
            {
                FillLineSolid(y, (byte)(cpu.memory.memory[0xD020] & 0x0F));
            }
            else if (bmm && mcm)
                RenderLineMulticolorBitmap(y, bg0, colorRow, cachedScreenRow, cachedBitmapRows, dy);
            else if (bmm)
                RenderLineHiresBitmap(y, colorRow, cachedScreenRow, cachedBitmapRows, dy);
            else if (ecm)
                RenderLineExtendedBgText(y, charAddr, bg0, bg1, bg2, bg3, bank, colorRow, cachedScreenRow, dy);
            else if (mcm)
                RenderLineMulticolorText(y, charAddr, bg0, bg1, bg2, bank, colorRow, cachedScreenRow, dy);
            else
                RenderLineStandardText(y, charAddr, bg0, bank, colorRow, cachedScreenRow, dy);

            if (screenOn && matrixVisible)
                RenderSpritesScanline(y, bank, spriteEnable, spriteXExpand, spriteYExpand, spriteMulticolor, spritePriority, spriteXHigh, spriteMc1Color, spriteMc2Color, spriteColors, spriteXPos, spriteYPos, spritePtrs);

            ApplyInnerBorders(y, d011, d016);
        }

        private static int GetVicBankBase(byte dd00, byte dd02)
        {
            // CIA2 port A controls VIC bank on PA0/PA1. Input bits read high.
            byte effectivePortA = (byte)((dd00 & dd02) | (~dd02 & 0xFF));
            int sel = effectivePortA & 0x03;
            return (3 - sel) * 0x4000;
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

        private void FillLineRange(int y, int xStart, int xEnd, int argb)
        {
            if (xStart < 0) xStart = 0;
            if (xEnd >= ScreenW) xEnd = ScreenW - 1;
            if (xStart > xEnd) return;

            int p = (y * ScreenW + xStart) * 4;
            int count = xEnd - xStart + 1;
            for (int i = 0; i < count; i++)
            {
                renderBuf[p] = (byte)argb;
                renderBuf[p + 1] = (byte)(argb >> 8);
                renderBuf[p + 2] = (byte)(argb >> 16);
                renderBuf[p + 3] = 0xFF;
                p += 4;
            }
        }

        private void ApplyInnerBorders(int y, byte d011, byte d016)
        {
            byte borderIdx = (byte)(cpu.memory.memory[0xD020] & 0x0F);
            int borderArgb = C64Palette[borderIdx];

            // RSEL=0 selects 24-row display: 4px inner border at top and bottom.
            bool row25 = (d011 & 0x08) != 0;
            int firstVisibleY = row25 ? 0 : 4;
            int lastVisibleYExclusive = row25 ? ScreenH : (ScreenH - 4);

            if (y < firstVisibleY || y >= lastVisibleYExclusive)
            {
                FillLineSolid(y, borderIdx);
                return;
            }

            // CSEL=0 selects 38-column display: 7px inner border on each side.
            bool col40 = (d016 & 0x08) != 0;
            if (!col40)
            {
                FillLineRange(y, 0, 6, borderArgb);
                FillLineRange(y, ScreenW - 7, ScreenW - 1, borderArgb);
            }
        }

        private void RenderLineStandardText(int y, int charAddr, byte bg, int bank, byte[] colorRow, byte[] cachedScreenRow, int dy)
        {
            byte[] mem = cpu.memory.memory;
            ResolveCharSource(charAddr, bank, out byte[] cs, out int cb);
            int bgC = C64Palette[bg];
            bool charFromVicRam = ReferenceEquals(cs, cpu.memory.memory) && cb >= 0xD000 && cb < 0xE000;

            int lineStart = y * ScreenW * 4;
            int fgBase = 0;

            for (int col = 0; col < 40; col++)
            {
                byte code = cachedScreenRow[col];
                int fgC = C64Palette[colorRow[col] & 0x0F];
                int charByteAddr = cb + code * 8 + dy;
                byte bits = charFromVicRam
                    ? cpu.memory.ReadVicByte((ulong)charByteAddr)
                    : cs[charByteAddr];
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

        private void RenderLineMulticolorText(int y, int charAddr, byte bg0, byte bg1, byte bg2, int bank, byte[] colorRow, byte[] cachedScreenRow, int dy)
        {
            byte[] mem = cpu.memory.memory;
            ResolveCharSource(charAddr, bank, out byte[] cs, out int cb);
            int bgC = C64Palette[bg0];
            int[] mcc = { bgC, C64Palette[bg1], C64Palette[bg2], 0 };
            bool charFromVicRam = ReferenceEquals(cs, cpu.memory.memory) && cb >= 0xD000 && cb < 0xE000;

            int lineStart = y * ScreenW * 4;
            int fgBase = 0;

            for (int col = 0; col < 40; col++)
            {
                byte code = cachedScreenRow[col];
                byte colRam = (byte)(colorRow[col] & 0x0F);
                bool cellMc = (colRam & 0x08) != 0;
                int fgC = C64Palette[colRam & (cellMc ? 0x07 : 0x0F)];
                int charByteAddr = cb + code * 8 + dy;
                byte bits = charFromVicRam
                    ? cpu.memory.ReadVicByte((ulong)charByteAddr)
                    : cs[charByteAddr];
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

        private void RenderLineExtendedBgText(int y, int charAddr, byte bg0, byte bg1, byte bg2, byte bg3, int bank, byte[] colorRow, byte[] cachedScreenRow, int dy)
        {
            byte[] mem = cpu.memory.memory;
            ResolveCharSource(charAddr, bank, out byte[] cs, out int cb);
            int[] bgC = { C64Palette[bg0], C64Palette[bg1], C64Palette[bg2], C64Palette[bg3] };
            bool charFromVicRam = ReferenceEquals(cs, cpu.memory.memory) && cb >= 0xD000 && cb < 0xE000;

            int lineStart = y * ScreenW * 4;
            int fgBase = 0;

            for (int col = 0; col < 40; col++)
            {
                byte code = cachedScreenRow[col];
                int fgC = C64Palette[colorRow[col] & 0x0F];
                int b = bgC[(code >> 6) & 0x03];
                int charByteAddr = cb + (code & 0x3F) * 8 + dy;
                byte bits = charFromVicRam
                    ? cpu.memory.ReadVicByte((ulong)charByteAddr)
                    : cs[charByteAddr];
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

        private void RenderLineHiresBitmap(int y, byte[] colorRow, byte[] cachedScreenRow, byte[][] cachedBitmapRows, int dy)
        {
            byte[] mem = cpu.memory.memory;

            int lineStart = y * ScreenW * 4;
            int fgBase = 0;

            for (int col = 0; col < 40; col++)
            {
                byte clr = cachedScreenRow[col];
                int fgC = C64Palette[(clr >> 4) & 0x0F];
                int bgC = C64Palette[clr & 0x0F];
                byte bits = cachedBitmapRows[dy][col];
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

        private void RenderLineMulticolorBitmap(int y, byte bg0, byte[] colorRow, byte[] cachedScreenRow, byte[][] cachedBitmapRows, int dy)
        {
            byte[] mem = cpu.memory.memory;
            int bgC = C64Palette[bg0];

            int lineStart = y * ScreenW * 4;
            int fgBase = 0;

            for (int col = 0; col < 40; col++)
            {
                byte clr = cachedScreenRow[col];
                int cFg1 = C64Palette[(clr >> 4) & 0x0F];
                int cFg2 = C64Palette[clr & 0x0F];
                int cFg3 = C64Palette[colorRow[col] & 0x0F];
                byte bits = cachedBitmapRows[dy][col];
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

        private void RenderSpritesScanline(int y, int bank, byte spriteEnable, byte spriteXExpand, byte spriteYExpand, byte spriteMulticolor, byte spritePriority, byte spriteXHigh, byte spriteMc1Color, byte spriteMc2Color, byte[] spriteColors, byte[] spriteXPos, byte[] spriteYPos, byte[] spritePtrs)
        {
            byte[] mem = cpu.memory.memory;
            if (spriteEnable == 0) return;

            int mc1 = C64Palette[spriteMc1Color & 0x0F];
            int mc2 = C64Palette[spriteMc2Color & 0x0F];

            for (int s = 7; s >= 0; s--)
            {
                int mask = 1 << s;
                if ((spriteEnable & mask) == 0) continue;

                int sx = spriteXPos[s] | (((spriteXHigh & mask) != 0) ? 0x100 : 0);
                int sy = spriteYPos[s];
                int fbX = sx - 24;
                int fbY = sy - 50;

                bool xExp = (spriteXExpand & mask) != 0;
                bool yExp = (spriteYExpand & mask) != 0;
                bool mc = (spriteMulticolor & mask) != 0;
                bool behindBg = (spritePriority & mask) != 0;
                int color = C64Palette[spriteColors[s] & 0x0F];

                int spriteHeight = yExp ? 42 : 21;
                int spriteRow = y - fbY;
                if (spriteRow < 0 || spriteRow >= spriteHeight) continue;

                int row = yExp ? (spriteRow >> 1) : spriteRow;
                int spritePtr = spritePtrs[s];
                int dataAddr = bank + spritePtr * 64;
                int rowAddr = dataAddr + row * 3;
                int rowBits =
                    (cpu.memory.ReadVicByte((ulong)rowAddr) << 16) |
                    (cpu.memory.ReadVicByte((ulong)(rowAddr + 1)) << 8) |
                    cpu.memory.ReadVicByte((ulong)(rowAddr + 2));

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

        public void TakeScreenshot()
        {
            try
            {
                lock (swapLock)
                {
                    // Capture current display buffer and save as BMP
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                    string filename = $"c64_screenshot_{timestamp}.bmp";
                    WriteBmp(filename, displayBuf, ScreenW, ScreenH);
                    Console.Error.WriteLine($"[SCREENSHOT] Saved to {filename}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Screenshot failed: {ex.Message}");
            }
        }

        private static void WriteBmp(string path, byte[] argbData, int width, int height)
        {
            // BMP file format: 14-byte file header + 40-byte info header + pixel data
            using (var fs = File.Create(path))
            using (var bw = new System.IO.BinaryWriter(fs))
            {
                int pixelDataSize = width * height * 4;
                int fileSize = 14 + 40 + pixelDataSize;

                // File header (14 bytes)
                bw.Write((ushort)0x4D42);              // "BM" signature
                bw.Write(fileSize);                    // File size
                bw.Write((uint)0);                     // Reserved
                bw.Write(14 + 40);                     // Offset to pixel data

                // Info header (40 bytes)
                bw.Write(40);                          // Header size
                bw.Write(width);                       // Width
                bw.Write(height);                      // Height (negative = top-down)
                bw.Write((ushort)1);                   // Planes
                bw.Write((ushort)32);                  // Bits per pixel
                bw.Write((uint)0);                     // Compression (none)
                bw.Write((uint)pixelDataSize);         // Image size
                bw.Write(2835);                        // X pixels per meter
                bw.Write(2835);                        // Y pixels per meter
                bw.Write((uint)0);                     // Colors used
                bw.Write((uint)0);                     // Important colors

                // Pixel data: convert ARGB to BGRA (BMP uses BGR)
                // Note: BMP stores bottom-up, but we'll write top-down by reversing rows
                for (int y = height - 1; y >= 0; y--)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int idx = (y * width + x) * 4;
                        byte a = argbData[idx + 3];
                        byte r = argbData[idx + 2];
                        byte g = argbData[idx + 1];
                        byte b = argbData[idx];
                        bw.Write(b);
                        bw.Write(g);
                        bw.Write(r);
                        bw.Write(a);
                    }
                }
            }
        }
    }
}
