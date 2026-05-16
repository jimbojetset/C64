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
        private const int FrameFirstRasterLine = VisibleTop - FramePlayfieldY;
        private const int FrameLastRasterLine = FrameFirstRasterLine + FrameH - 1;

        private const int PalRasterLines = 312;
        private const int CyclesPerRasterLine = 63;

        private const int VisibleTop = 51;
        private const int VisibleBottom = 250;
        private static readonly bool TraceSpriteCollisions =
            string.Equals(Environment.GetEnvironmentVariable("C64_TRACE_SPRCOL"), "1", StringComparison.Ordinal);

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

        private byte[] renderBuf = new byte[FrameW * FrameH * 4];
        private byte[] displayBuf = new byte[FrameW * FrameH * 4];
        private readonly object swapLock = new object();

        private readonly bool[] fgLine = new bool[ScreenW];
        private readonly byte[] spriteLine = new byte[ScreenW];

        private byte[] cachedScreenRow = new byte[40];
        private byte[][] cachedBitmapRows = new byte[8][];
        private int[] cachedBitmapRowNum = new int[8];  // Track which row number each cache came from

        private IntPtr window;
        private IntPtr renderer;
        private IntPtr texture;
        private const string BaseWindowTitle = "C64 Emulator";
        private string? loadedFileDisplayName;
        private bool windowTitleDirty;

        private volatile int rasterCompare;
        private volatile int currentRasterLine;
        private volatile bool isResetting;
        private volatile bool resyncPending;
        private int rasterCycleInLine;
        private readonly bool[] busStealMask = new bool[CyclesPerRasterLine];

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
            rasterCycleInLine = 0;
            rasterCompare = 0;
            resyncPending = true;
            Array.Clear(busStealMask, 0, busStealMask.Length);
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
                BaseWindowTitle,
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
                FrameW, FrameH);
            if (texture == IntPtr.Zero)
                throw new Exception($"SDL_CreateTexture failed: {SDL_GetError()}");

            charRom = File.ReadAllBytes(Path.Combine("ROMS", "characters.901225-01.bin"));
            windowTitleDirty = true;
            ApplyPendingWindowTitle();
        }

        public void Start(CancellationToken token) { }

        public void SetLoadedFileInTitle(string? filePath)
        {
            string? next = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFileName(filePath);
            if (string.Equals(loadedFileDisplayName, next, StringComparison.Ordinal))
                return;

            loadedFileDisplayName = next;
            windowTitleDirty = true;
        }

        private void ApplyPendingWindowTitle()
        {
            if (!windowTitleDirty || window == IntPtr.Zero)
                return;

            string title = string.IsNullOrWhiteSpace(loadedFileDisplayName)
                ? BaseWindowTitle
                : BaseWindowTitle + " - " + loadedFileDisplayName;
            SDL_SetWindowTitle(window, title);
            windowTitleDirty = false;
        }

        public void RedrawScreen()
        {
            ApplyPendingWindowTitle();

            lock (swapLock)
            {
                unsafe
                {
                    fixed (byte* p = displayBuf)
                    {
                        SDL_UpdateTexture(texture, IntPtr.Zero, (IntPtr)p, FrameW * 4);
                    }
                }
            }

            SDL_Rect dst = new SDL_Rect
            {
                x = 0,
                y = 0,
                w = FrameW,
                h = FrameH,
            };
            SDL_RenderCopy(renderer, texture, IntPtr.Zero, ref dst);
            SDL_RenderPresent(renderer);
        }

        public void Dispose()
        {
            if (texture != IntPtr.Zero) { SDL_DestroyTexture(texture); texture = IntPtr.Zero; }
            if (renderer != IntPtr.Zero) { SDL_DestroyRenderer(renderer); renderer = IntPtr.Zero; }
            if (window != IntPtr.Zero) { SDL_DestroyWindow(window); window = IntPtr.Zero; }
        }

        public uint StepCycles(uint cycles, bool accountBusSteal)
        {
            if (cycles == 0 || isResetting)
                return 0;

            if (resyncPending)
            {
                currentRasterLine = 0;
                rasterCycleInLine = 0;
                resyncPending = false;
                Array.Clear(busStealMask, 0, busStealMask.Length);
            }

            byte[] mem = cpu.memory.memory;
            uint stolen = 0;
            while (cycles > 0)
            {
                if (rasterCycleInLine == 0)
                {
                    ProcessRasterLine(currentRasterLine, mem);
                    if (accountBusSteal)
                        BuildLineBusStealMask(currentRasterLine, mem, busStealMask);
                    else
                        Array.Clear(busStealMask, 0, busStealMask.Length);
                }

                int toBoundary = CyclesPerRasterLine - rasterCycleInLine;
                int step = (int)Math.Min(cycles, (uint)toBoundary);

                if (accountBusSteal)
                {
                    int start = rasterCycleInLine;
                    int end = start + step;
                    for (int c = start; c < end; c++)
                    {
                        if (busStealMask[c])
                            stolen++;
                    }
                }

                rasterCycleInLine += step;
                cycles -= (uint)step;

                if (rasterCycleInLine >= CyclesPerRasterLine)
                {
                    rasterCycleInLine = 0;
                    int nextLine = currentRasterLine + 1;
                    if (nextLine >= PalRasterLines)
                    {
                        nextLine = 0;
                        lock (swapLock)
                        {
                            (renderBuf, displayBuf) = (displayBuf, renderBuf);
                        }
                    }

                    currentRasterLine = nextLine;
                }
            }

            return stolen;
        }

        private static void BuildLineBusStealMask(int line, byte[] mem, bool[] mask)
        {
            Array.Clear(mask, 0, mask.Length);

            bool den = (mem[0xD011] & 0x10) != 0;
            int yScroll = mem[0xD011] & 0x07;
            // SEVERITY 3 FIX: VIC Bus Stall Cycle Accuracy - Precise cycle-by-cycle model
            // Badline detection: DEN=1, line in 0x30-0xF7, and raster line & 0x07 == fine Y scroll
            // When badline condition is true, VIC steals cycles during character/sprite data fetch
            bool badline = den && line >= 0x30 && line <= 0xF7 && ((line & 0x07) == yScroll);
            if (badline)
            {
                // Character matrix fetch on badlines: VIC access $2400-$3FFF (or banked equivalent)
                // Steals cycles 15-54 (40 cycles) during 63-cycle PAL line for character data + color lookups
                // Precise cycle windows based on documented C64 behavior:
                // - Cycles 15-54: character/color RAM fetch and graphics data prefetch
                for (int c = 15; c <= 54 && c < mask.Length; c++)
                    mask[c] = true;
            }

            byte spriteEnable = mem[0xD015];
            byte spriteYExpand = mem[0xD017];
            for (int s = 0; s < 8; s++)
            {
                int spriteBit = 1 << s;
                if ((spriteEnable & spriteBit) == 0)
                    continue;

                int spriteY = mem[0xD001 + s * 2];
                int height = (spriteYExpand & spriteBit) != 0 ? 42 : 21;
                if (line >= spriteY && line < spriteY + height)
                {
                    // Sprite DMA: Each sprite can steal 2 cycles per line when active
                    // Precise model: sprite 0 DMA at cycles 0-1, sprite 1 at 2-3, ... sprite 7 at 14-15
                    // However, if badline is active, sprite DMA is delayed or interleaved with char fetch
                    int baseCycle = s * 2;  // Each sprite has 2-cycle slot starting from beginning of line
                    if (baseCycle < mask.Length)
                        mask[baseCycle] = true;
                    if (baseCycle + 1 < mask.Length)
                        mask[baseCycle + 1] = true;
                }
            }
        }

        private void ProcessRasterLine(int line, byte[] mem)
        {
            if (line == rasterCompare)
            {
                bool rasterIrqEnabled = (mem[0xD01A] & 0x01) != 0;
                if (rasterIrqEnabled)
                {
                    mem[0xD019] = (byte)(mem[0xD019] | 0x81);
                    cpu.InitiateIRQ(0xFFFE);
                }
            }

            int frameY = line - FrameFirstRasterLine;
            if (line >= FrameFirstRasterLine && line <= FrameLastRasterLine)
                FillFrameLineSolid(frameY, (byte)(mem[0xD020] & 0x0F));

            if (line < VisibleTop || line > VisibleBottom)
                return;

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
            // VisibleTop is calibrated around the normal C64 text baseline (yscroll=3).
            // Apply only the delta from that baseline so we don't wrap/crop the 25x8 matrix.
            int fineYOffset = fineY - 3;
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

            if (matrixVisible)
            {
                for (int col = 0; col < 40; col++)
                    cachedScreenRow[col] = cpu.memory.ReadVicByte((ulong)(screenAddr + wrappedRow * 40 + col));

                int bitmapAddr = bank + (((d018 & 0x08) != 0) ? 0x2000 : 0x0000);
                if (cachedBitmapRows[dy] == null || cachedBitmapRowNum[dy] != wrappedRow)
                {
                    cachedBitmapRows[dy] = new byte[40];
                    cachedBitmapRowNum[dy] = wrappedRow;
                }
                for (int col = 0; col < 40; col++)
                    cachedBitmapRows[dy][col] = cpu.memory.ReadVicByte((ulong)(bitmapAddr + (wrappedRow * 40 + col) * 8 + dy));
            }

            byte[] colorRow = new byte[40];
            if (matrixVisible)
            {
                for (int col = 0; col < 40; col++)
                    colorRow[col] = mem[0xD800 + wrappedRow * 40 + col];
            }

            RenderScanline(frameY, playY, d011, d016, d018, bg0, bg1, bg2, bg3, dd00, dd02, spriteEnable, spriteXExpand, spriteYExpand, spriteMulticolor, spritePriority, spriteXHigh, spriteMc1Color, spriteMc2Color, spriteColors, spriteXPos, spriteYPos, spritePtrs, colorRow, cachedScreenRow, cachedBitmapRows, dy, matrixVisible);
        }

        private void RenderScanline(int frameY, int playY, byte d011, byte d016, byte d018, byte bg0, byte bg1, byte bg2, byte bg3, byte dd00, byte dd02, byte spriteEnable, byte spriteXExpand, byte spriteYExpand, byte spriteMulticolor, byte spritePriority, byte spriteXHigh, byte spriteMc1Color, byte spriteMc2Color, byte[] spriteColors, byte[] spriteXPos, byte[] spriteYPos, byte[] spritePtrs, byte[] colorRow, byte[] cachedScreenRow, byte[][] cachedBitmapRows, int dy, bool matrixVisible)
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
                FillLineSolid(frameY, (byte)(cpu.memory.memory[0xD020] & 0x0F));
            }
            else if (bmm && mcm)
                RenderLineMulticolorBitmap(frameY, bg0, colorRow, cachedScreenRow, cachedBitmapRows, dy);
            else if (bmm)
                RenderLineHiresBitmap(frameY, colorRow, cachedScreenRow, cachedBitmapRows, dy);
            else if (ecm)
                RenderLineExtendedBgText(frameY, charAddr, bg0, bg1, bg2, bg3, bank, colorRow, cachedScreenRow, dy);
            else if (mcm)
                RenderLineMulticolorText(frameY, charAddr, bg0, bg1, bg2, bank, colorRow, cachedScreenRow, dy);
            else
                RenderLineStandardText(frameY, charAddr, bg0, bank, colorRow, cachedScreenRow, dy);

            if (screenOn && matrixVisible)
                RenderSpritesScanline(frameY, playY, bank, spriteEnable, spriteXExpand, spriteYExpand, spriteMulticolor, spritePriority, spriteXHigh, spriteMc1Color, spriteMc2Color, spriteColors, spriteXPos, spriteYPos, spritePtrs);

            ApplyInnerBorders(frameY, playY, d011, d016);
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
            int p = (y * FrameW + FramePlayfieldX) * 4;
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

        private void FillFrameLineSolid(int y, byte colorIdx)
        {
            int c = C64Palette[colorIdx & 0x0F];
            int p = y * FrameW * 4;
            int end = p + FrameW * 4;
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

            int p = (y * FrameW + FramePlayfieldX + xStart) * 4;
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

        private void ApplyInnerBorders(int frameY, int playY, byte d011, byte d016)
        {
            byte borderIdx = (byte)(cpu.memory.memory[0xD020] & 0x0F);
            int borderArgb = C64Palette[borderIdx];

            // RSEL=0 selects 24-row display: 4px inner border at top and bottom.
            bool row25 = (d011 & 0x08) != 0;
            int firstVisibleY = row25 ? 0 : 4;
            int lastVisibleYExclusive = row25 ? ScreenH : (ScreenH - 4);

            if (playY < firstVisibleY || playY >= lastVisibleYExclusive)
            {
                FillLineSolid(frameY, borderIdx);
                return;
            }

            // CSEL=0 selects 38-column display: 7px inner border on each side.
            bool col40 = (d016 & 0x08) != 0;
            if (!col40)
            {
                FillLineRange(frameY, 0, 6, borderArgb);
                FillLineRange(frameY, ScreenW - 7, ScreenW - 1, borderArgb);
            }
        }

        private void RenderLineStandardText(int y, int charAddr, byte bg, int bank, byte[] colorRow, byte[] cachedScreenRow, int dy)
        {
            byte[] mem = cpu.memory.memory;
            ResolveCharSource(charAddr, bank, out byte[] cs, out int cb);
            int bgC = C64Palette[bg];
            bool charFromVicRam = ReferenceEquals(cs, cpu.memory.memory) && cb >= 0xD000 && cb < 0xE000;

            int lineStart = (y * FrameW + FramePlayfieldX) * 4;
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

            int lineStart = (y * FrameW + FramePlayfieldX) * 4;
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

            int lineStart = (y * FrameW + FramePlayfieldX) * 4;
            int fgBase = 0;

            for (int col = 0; col < 40; col++)
            {
                byte code = cachedScreenRow[col];
                // SEVERITY 3 FIX: ECM color interpretation - color RAM provides actual foreground color per character
                // (not just a selector), and upper 2 bits of code select background from bg0-bg3
                int fgC = C64Palette[colorRow[col] & 0x0F];
                int bgIdx = (code >> 6) & 0x03;
                int bgColor = bgC[bgIdx];
                int charByteAddr = cb + (code & 0x3F) * 8 + dy;
                byte bits = charFromVicRam
                    ? cpu.memory.ReadVicByte((ulong)charByteAddr)
                    : cs[charByteAddr];
                int p = lineStart + col * 32;

                for (int dx = 0; dx < 8; dx++)
                {
                    bool on = (bits & (0x80 >> dx)) != 0;
                    int c = on ? fgC : bgColor;
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

            int lineStart = (y * FrameW + FramePlayfieldX) * 4;
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

            int lineStart = (y * FrameW + FramePlayfieldX) * 4;
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

        private void RenderSpritesScanline(int frameY, int playY, int bank, byte spriteEnable, byte spriteXExpand, byte spriteYExpand, byte spriteMulticolor, byte spritePriority, byte spriteXHigh, byte spriteMc1Color, byte spriteMc2Color, byte[] spriteColors, byte[] spriteXPos, byte[] spriteYPos, byte[] spritePtrs)
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
                int spriteRow = playY - fbY;
                if (spriteRow < 0 || spriteRow >= spriteHeight) continue;

                // SEVERITY 3 FIX: Sprite Y-Expansion - Exact pixel-by-pixel doubling
                // When Y-expanded, each sprite row becomes 2 scanlines; divide by 2 to get source row index
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
                            PaintSpritePixelLine(fbX + basePix + w, frameY, playY, c, behindBg, s);
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
                            PaintSpritePixelLine(fbX + basePix + w, frameY, playY, color, behindBg, s);
                    }
                }
            }
        }

        private void PaintSpritePixelLine(int x, int frameY, int playY, int color, bool behindBg, int spriteIdx)
        {
            // SEVERITY 3 FIX: Sprite 9-bit X Positioning Edge Cases
            // Clamp to visible screen area; real C64 doesn't wrap horizontally at 320px boundary
            if (x < 0 || x >= ScreenW) return;
            byte myBit = (byte)(1 << spriteIdx);
            byte[] mem = cpu.memory.memory;

            byte priorSprites = spriteLine[x];
            byte priorOtherSprites = (byte)(priorSprites & ~myBit);
            if (priorOtherSprites != 0)
            {
                mem[0xD01E] |= (byte)(priorOtherSprites | myBit);
                if ((mem[0xD01A] & 0x04) != 0 && (mem[0xD019] & 0x04) == 0)
                {
                    mem[0xD019] |= 0x84;
                    cpu.InitiateIRQ(0xFFFE);
                    if (TraceSpriteCollisions)
                        Console.Error.WriteLine($"[VIC-SPRCOL] x={x} y={playY} self={spriteIdx} priorMask=${priorOtherSprites:X2} d01e=${mem[0xD01E]:X2}");
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

            int p = (frameY * FrameW + FramePlayfieldX + x) * 4;
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
                    // Capture current display buffer and save as PNG
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                    string filename = $"c64_screenshot_{timestamp}.png";
                    WritePng(filename, displayBuf, FrameW, FrameH);
                    Console.Error.WriteLine($"[SCREENSHOT] Saved to {filename}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Screenshot failed: {ex.Message}");
            }
        }

        private static void WritePng(string path, byte[] argbData, int width, int height)
        {
            using (var fs = File.Create(path))
            {
                // PNG signature
                fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

                // IHDR
                byte[] ihdr = new byte[13];
                WriteUInt32BigEndian(ihdr, 0, (uint)width);
                WriteUInt32BigEndian(ihdr, 4, (uint)height);
                ihdr[8] = 8;  // Bit depth
                ihdr[9] = 6;  // Color type RGBA
                ihdr[10] = 0; // Compression method
                ihdr[11] = 0; // Filter method
                ihdr[12] = 0; // Interlace method
                WritePngChunk(fs, "IHDR", ihdr);

                // Prepare raw scanlines: one filter byte (0) + RGBA pixels per row.
                int stride = width * 4;
                byte[] raw = new byte[height * (stride + 1)];
                int dst = 0;
                for (int y = 0; y < height; y++)
                {
                    raw[dst++] = 0; // Filter: None
                    for (int x = 0; x < width; x++)
                    {
                        int idx = (y * width + x) * 4;
                        // Internal buffer is BGRA; PNG needs RGBA.
                        raw[dst++] = argbData[idx + 2];
                        raw[dst++] = argbData[idx + 1];
                        raw[dst++] = argbData[idx];
                        raw[dst++] = argbData[idx + 3];
                    }
                }

                byte[] compressed;
                using (var compressedMs = new MemoryStream())
                {
                    using (var zlib = new System.IO.Compression.ZLibStream(compressedMs, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
                    {
                        zlib.Write(raw, 0, raw.Length);
                    }

                    compressed = compressedMs.ToArray();
                }

                WritePngChunk(fs, "IDAT", compressed);
                WritePngChunk(fs, "IEND", Array.Empty<byte>());
            }
        }

        private static void WritePngChunk(Stream output, string type, byte[] data)
        {
            byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            if (typeBytes.Length != 4)
                throw new ArgumentException("PNG chunk type must be 4 bytes.", nameof(type));

            WriteUInt32BigEndian(output, (uint)data.Length);
            output.Write(typeBytes, 0, typeBytes.Length);
            output.Write(data, 0, data.Length);

            uint crc = Crc32(typeBytes, data);
            WriteUInt32BigEndian(output, crc);
        }

        private static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static void WriteUInt32BigEndian(Stream output, uint value)
        {
            output.WriteByte((byte)(value >> 24));
            output.WriteByte((byte)(value >> 16));
            output.WriteByte((byte)(value >> 8));
            output.WriteByte((byte)value);
        }

        private static uint Crc32(byte[] typeBytes, byte[] data)
        {
            uint crc = 0xFFFFFFFF;

            for (int i = 0; i < typeBytes.Length; i++)
                crc = Crc32Update(crc, typeBytes[i]);

            for (int i = 0; i < data.Length; i++)
                crc = Crc32Update(crc, data[i]);

            return ~crc;
        }

        private static uint Crc32Update(uint crc, byte value)
        {
            crc ^= value;
            for (int i = 0; i < 8; i++)
            {
                bool lsbSet = (crc & 1) != 0;
                crc >>= 1;
                if (lsbSet)
                    crc ^= 0xEDB88320;
            }

            return crc;
        }
    }
}
