using C64.CPU;
using Silk.NET.OpenGL;
using System.Diagnostics;
using System.IO.Compression;
using System.Threading;
using static SDL2.SDL;

namespace C64
{

    /// <summary>
    /// Emulates enough VIC-II display behavior to render C64 frames, track raster timing, handle sprite/text/bitmap modes, and present through SDL/OpenGL.
    /// </summary>
    internal sealed class Display : IDisposable
    {
        public const int ScreenW = 320;
        public const int ScreenH = 200;

        public const int FrameW = 384;
        public const int FrameH = 272;
        private const int MonitorImageW = 688;
        private const int MonitorImageH = 701;
        private const int MonitorScreenX = 90;
        private const int MonitorScreenY = 83;
        private const int MonitorScreenW = 508;
        private const int MonitorScreenH = 423;
        private const double MonitorScreenOverscan = 1.08;
        private const int FramePlayfieldX = (FrameW - ScreenW) / 2;
        private const int FramePlayfieldY = (FrameH - ScreenH) / 2;
        private const int FrameFirstRasterLine = VisibleTop - FramePlayfieldY;
        private const int FrameLastRasterLine = FrameFirstRasterLine + FrameH - 1;

        private const int PalRasterLines = 312;
        private const int CyclesPerRasterLine = 63;

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

        private readonly CPU_6510 cpu;

        private byte[] charRom = Array.Empty<byte>();

        private byte[] renderBuf = new byte[FrameW * FrameH * 4];
        private byte[] displayBuf = new byte[FrameW * FrameH * 4];
        private byte[] presentationBuf = new byte[FrameW * FrameH * 4];
        private readonly object swapLock = new object();

        private readonly bool[] fgLine = new bool[ScreenW];
        private readonly byte[] spriteLine = new byte[FrameW];

        private byte[] cachedScreenRow = new byte[40];
        private byte[][] cachedBitmapRows = new byte[8][];
        private int[] cachedBitmapRowNum = new int[8];  // Track which row number each cache came from

        private IntPtr window;
        private IntPtr glContext;
        private GL? gl;
        private uint frameTexture;
        private uint monitorTexture;
        private uint presentVao;
        private uint presentVbo;
        private uint presentShader;
        private int presentTextureUniform;
        private int presentOutputSizeUniform;
        private int presentEffectModeUniform;
        private const string BaseWindowTitle = "C64 Emulator";
        private string? loadedFileDisplayName;
        private bool windowTitleDirty;

        private volatile int rasterCompare;
        private volatile int currentRasterLine;
        private volatile bool isResetting;
        private volatile bool resyncPending;
        private volatile bool muteOverlayVisible;
        private volatile bool pausedOverlayVisible;
        private long driveActivityTicks;
        private int rasterCycleInLine;
        private readonly bool[] busStealMask = new bool[CyclesPerRasterLine];
        private readonly List<RasterWriteEvent>[] rasterWriteEvents = new List<RasterWriteEvent>[PalRasterLines];
        private readonly object rasterWriteEventLock = new object();

        /// <summary>
        /// Captures a VIC register write and the raster cycle where it occurred so late writes can affect only the remaining pixels on the line.
        /// </summary>
        private readonly struct RasterWriteEvent
        {

            /// <summary>Initializes a captured raster write event.</summary>
            public RasterWriteEvent(int cycle, ushort address, byte oldValue, byte newValue)
            {
                Cycle = cycle;
                Address = address;
                OldValue = oldValue;
                NewValue = newValue;
            }

        /// <summary>Gets the raster cycle where the write occurred.</summary>
        public int Cycle { get; }

        /// <summary>Gets the VIC register address that was written.</summary>
        public ushort Address { get; }

        /// <summary>Gets the register value before the raster write.</summary>
        public byte OldValue { get; }

        /// <summary>Gets the register value after the raster write.</summary>
        public byte NewValue { get; }
        }

        /// <summary>Initializes a captured raster write event.</summary>
        public Display(CPU_6510 cpu)
        {
            this.cpu = cpu;
            for (int i = 0; i < rasterWriteEvents.Length; i++)
                rasterWriteEvents[i] = new List<RasterWriteEvent>(8);
        }

        /// <summary>Gets or sets the VIC raster compare line.</summary>
        public int RasterCompare
        {
            get => rasterCompare;
            set => rasterCompare = value;
        }

        /// <summary>Gets the current VIC raster line.</summary>
        public int CurrentRasterLine => currentRasterLine;

        /// <summary>Gets whether the display is currently resetting.</summary>
        public bool IsResetting => isResetting;

        /// <summary>Gets or sets whether the mute overlay is visible.</summary>
        public bool MuteOverlayVisible
        {
            get => muteOverlayVisible;
            set => muteOverlayVisible = value;
        }

        /// <summary>Gets or sets whether the pause overlay is visible.</summary>
        public bool PausedOverlayVisible
        {
            get => pausedOverlayVisible;
            set => pausedOverlayVisible = value;
        }

        /// <summary>Pulses drive activity.</summary>
        public void PulseDriveActivity()
        {
            Interlocked.Exchange(ref driveActivityTicks, Stopwatch.GetTimestamp());
        }

        /// <summary>Records raster write.</summary>
        public void RecordRasterWrite(ulong address, byte oldValue, byte newValue)
        {
            if (isResetting)
                return;

            int line = currentRasterLine;
            if (line < 0 || line >= rasterWriteEvents.Length)
                return;

            ushort vicAddress = (ushort)(address & 0xFFFF);
            int cycle = rasterCycleInLine;
            if (cycle < 0)
                cycle = 0;
            else if (cycle >= CyclesPerRasterLine)
                cycle = CyclesPerRasterLine - 1;

            lock (rasterWriteEventLock)
                rasterWriteEvents[line].Add(new RasterWriteEvent(cycle, vicAddress, oldValue, newValue));
        }

        /// <summary>Begins reset.</summary>
        public void BeginReset()
        {
            isResetting = true;
            resyncPending = true;
            rasterCompare = 0;
            ClearRasterWriteEvents();
        }

        /// <summary>Ends reset.</summary>
        public void EndReset()
        {
            currentRasterLine = 0;
            rasterCycleInLine = 0;
            rasterCompare = 0;
            resyncPending = true;
            Array.Clear(busStealMask, 0, busStealMask.Length);
            ClearRasterWriteEvents();
            ClearFramebuffers();
            // Invalidate bitmap row cache on reset
            for (int i = 0; i < cachedBitmapRowNum.Length; i++)
                cachedBitmapRowNum[i] = -1;
            isResetting = false;
        }

        /// <summary>Clears framebuffers.</summary>
        public void ClearFramebuffers()
        {
            lock (swapLock)
            {
                Array.Clear(renderBuf, 0, renderBuf.Length);
                Array.Clear(displayBuf, 0, displayBuf.Length);
            }
        }

        /// <summary>Initializes this component.</summary>
        public void Init()
        {
            if (SDL_Init(SDL_INIT_VIDEO | SDL_INIT_GAMECONTROLLER) != 0)
                throw new Exception($"SDL_Init failed: {SDL_GetError()}");

            SDL_GL_SetAttribute(SDL_GLattr.SDL_GL_CONTEXT_MAJOR_VERSION, 3);
            SDL_GL_SetAttribute(SDL_GLattr.SDL_GL_CONTEXT_MINOR_VERSION, 3);
            SDL_GL_SetAttribute(SDL_GLattr.SDL_GL_CONTEXT_PROFILE_MASK,
                (int)SDL_GLprofile.SDL_GL_CONTEXT_PROFILE_CORE);
            SDL_GL_SetAttribute(SDL_GLattr.SDL_GL_DOUBLEBUFFER, 1);

            int initialW = MonitorImageW;
            int initialH = MonitorImageH;
            window = SDL_CreateWindow(
                BaseWindowTitle,
                SDL_WINDOWPOS_CENTERED, SDL_WINDOWPOS_CENTERED,
                initialW, initialH,
                SDL_WindowFlags.SDL_WINDOW_OPENGL |
                SDL_WindowFlags.SDL_WINDOW_SHOWN |
                SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
            if (window == IntPtr.Zero)
                throw new Exception($"SDL_CreateWindow failed: {SDL_GetError()}");

            SDL_EventState(SDL_EventType.SDL_DROPFILE, SDL_ENABLE);

            glContext = SDL_GL_CreateContext(window);
            if (glContext == IntPtr.Zero)
                throw new Exception($"SDL_GL_CreateContext failed: {SDL_GetError()}");

            SDL_GL_MakeCurrent(window, glContext);
            SDL_GL_SetSwapInterval(1);

            gl = GL.GetApi(name => SDL_GL_GetProcAddress(name));
            CreatePresentationObjects();

            charRom = File.ReadAllBytes(Path.Combine("ROMS", "characters.901225-01.bin"));
            windowTitleDirty = true;
            ApplyPendingWindowTitle();
        }

        /// <summary>Starts this component.</summary>
        public void Start(CancellationToken token) { }

        /// <summary>Sets loaded file in title.</summary>
        public void SetLoadedFileInTitle(string? filePath)
        {
            string? next = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFileName(filePath);
            if (string.Equals(loadedFileDisplayName, next, StringComparison.Ordinal))
                return;

            loadedFileDisplayName = next;
            windowTitleDirty = true;
        }

        /// <summary>Applies pending window title.</summary>
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

        /// <summary>Redraws the current emulator frame.</summary>
        public void RedrawScreen()
        {
            ApplyPendingWindowTitle();

            lock (swapLock)
            {
                System.Buffer.BlockCopy(displayBuf, 0, presentationBuf, 0, displayBuf.Length);
            }

            if (pausedOverlayVisible)
                DrawPausedOverlay();
            if (muteOverlayVisible)
                DrawMuteOverlay();
            DrawDriveActivityOverlay();
            PresentFrame();
        }

        /// <summary>Creates presentation objects.</summary>
        private unsafe void CreatePresentationObjects()
        {
            GL glApi = gl ?? throw new InvalidOperationException("OpenGL API is not initialized.");

            string vertexShaderSource = @"
                #version 330 core
                layout (location = 0) in vec2 Position;
                layout (location = 1) in vec2 UV;
                out vec2 Frag_UV;
                void main()
                {
                    Frag_UV = UV;
                    gl_Position = vec4(Position, 0.0, 1.0);
                }
            ";

            string fragmentShaderSource = @"
                #version 330 core
                in vec2 Frag_UV;
                uniform sampler2D Texture;
                uniform vec2 OutputSize;
                uniform int EffectMode;
                layout (location = 0) out vec4 Out_Color;

                void main()
                {
                    if (EffectMode == 0)
                    {
                        Out_Color = texture(Texture, Frag_UV);
                        return;
                    }

                    vec2 centered = Frag_UV * 2.0 - 1.0;
                    float r2 = dot(centered, centered);
                    vec2 sampleUv = centered * (1.0 + 0.0825 * r2);
                    sampleUv.x = centered.x * (1.0 + 0.04125 * r2);

                    if (abs(sampleUv.x) > 1.0 || abs(sampleUv.y) > 1.0)
                    {
                        Out_Color = vec4(0.0, 0.0, 0.0, 0.0);
                        return;
                    }

                    sampleUv = sampleUv * 0.5 + 0.5;
                    vec4 color = texture(Texture, sampleUv);

                    float vignette = 1.0 - clamp(r2 * 0.12, 0.0, 0.22);
                    float scanline = 1.0 - 0.045 * step(0.5, fract(gl_FragCoord.y * 0.5));
                    color.rgb *= vignette * scanline;

                    Out_Color = vec4(color.rgb, 1.0);
                }
            ";

            uint vertexShader = CompilePresentationShader(ShaderType.VertexShader, vertexShaderSource);
            uint fragmentShader = CompilePresentationShader(ShaderType.FragmentShader, fragmentShaderSource);

            presentShader = glApi.CreateProgram();
            glApi.AttachShader(presentShader, vertexShader);
            glApi.AttachShader(presentShader, fragmentShader);
            glApi.LinkProgram(presentShader);
            glApi.GetProgram(presentShader, ProgramPropertyARB.LinkStatus, out int linked);
            if (linked == 0)
                throw new Exception($"Presentation shader link failed: {glApi.GetProgramInfoLog(presentShader)}");

            glApi.DeleteShader(vertexShader);
            glApi.DeleteShader(fragmentShader);

            presentTextureUniform = glApi.GetUniformLocation(presentShader, "Texture");
            presentOutputSizeUniform = glApi.GetUniformLocation(presentShader, "OutputSize");
            presentEffectModeUniform = glApi.GetUniformLocation(presentShader, "EffectMode");

            frameTexture = glApi.GenTexture();
            glApi.BindTexture(TextureTarget.Texture2D, frameTexture);
            glApi.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba, FrameW, FrameH, 0, PixelFormat.Bgra, PixelType.UnsignedByte, null);
            glApi.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            glApi.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            glApi.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            glApi.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            monitorTexture = CreateMonitorTexture();

            float[] vertices =
            {
                -1f, -1f, 0f, 1f,
                 1f, -1f, 1f, 1f,
                -1f,  1f, 0f, 0f,
                 1f,  1f, 1f, 0f,
            };

            presentVao = glApi.GenVertexArray();
            presentVbo = glApi.GenBuffer();
            glApi.BindVertexArray(presentVao);
            glApi.BindBuffer(BufferTargetARB.ArrayBuffer, presentVbo);
            fixed (float* p = vertices)
            {
                glApi.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
            }

            const uint stride = 4 * sizeof(float);
            glApi.EnableVertexAttribArray(0);
            glApi.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
            glApi.EnableVertexAttribArray(1);
            glApi.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float)));

            glApi.BindVertexArray(0);
        }

        /// <summary>Compiles presentation shader.</summary>
        private uint CompilePresentationShader(ShaderType type, string source)
        {
            GL glApi = gl ?? throw new InvalidOperationException("OpenGL API is not initialized.");
            uint shader = glApi.CreateShader(type);
            glApi.ShaderSource(shader, source);
            glApi.CompileShader(shader);
            glApi.GetShader(shader, ShaderParameterName.CompileStatus, out int compiled);
            if (compiled == 0)
                throw new Exception($"Presentation shader compilation failed: {glApi.GetShaderInfoLog(shader)}");

            return shader;
        }

        /// <summary>Creates monitor texture.</summary>
        private unsafe uint CreateMonitorTexture()
        {
            GL glApi = gl ?? throw new InvalidOperationException("OpenGL API is not initialized.");
            byte[] pixels = LoadPngBgra(FindDisplayAsset("monitor.png"), out int width, out int height);

            uint texture = glApi.GenTexture();
            glApi.BindTexture(TextureTarget.Texture2D, texture);
            fixed (byte* p = pixels)
            {
                glApi.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba, (uint)width, (uint)height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, p);
            }

            glApi.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            glApi.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            glApi.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            glApi.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            return texture;
        }

        /// <summary>Finds display asset.</summary>
        private static string FindDisplayAsset(string fileName)
        {
            string[] candidates =
            {
                Path.Combine(Environment.CurrentDirectory, "Display", fileName),
                Path.Combine(AppContext.BaseDirectory, "Display", fileName),
                Path.Combine(Environment.CurrentDirectory, "C64", "Display", fileName),
            };

            return candidates.FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException($"Display asset not found: {fileName}");
        }

        /// <summary>Loads png bgra.</summary>
        private static byte[] LoadPngBgra(string path, out int width, out int height)
        {
            byte[] file = File.ReadAllBytes(path);
            ReadOnlySpan<byte> signature = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (file.Length < signature.Length || !file.AsSpan(0, signature.Length).SequenceEqual(signature))
                throw new InvalidDataException("Monitor background must be a PNG file.");

            width = 0;
            height = 0;
            int bitDepth = 0;
            int colorType = 0;
            using var idat = new MemoryStream();

            int offset = 8;
            while (offset + 12 <= file.Length)
            {
                int length = (int)ReadUInt32BigEndian(file, offset);
                offset += 4;
                string type = System.Text.Encoding.ASCII.GetString(file, offset, 4);
                offset += 4;

                if (offset + length + 4 > file.Length)
                    throw new InvalidDataException("PNG chunk extends past end of file.");

                if (type == "IHDR")
                {
                    width = (int)ReadUInt32BigEndian(file, offset);
                    height = (int)ReadUInt32BigEndian(file, offset + 4);
                    bitDepth = file[offset + 8];
                    colorType = file[offset + 9];
                    if (file[offset + 10] != 0 || file[offset + 11] != 0 || file[offset + 12] != 0)
                        throw new InvalidDataException("Unsupported PNG compression, filter, or interlace method.");
                }
                else if (type == "IDAT")
                {
                    idat.Write(file, offset, length);
                }
                else if (type == "IEND")
                {
                    break;
                }

                offset += length + 4; // data + CRC
            }

            if (width <= 0 || height <= 0 || bitDepth != 8 || colorType != 6)
                throw new InvalidDataException("Monitor background PNG must be 8-bit RGBA.");

            idat.Position = 0;
            byte[] inflated;
            using (var raw = new MemoryStream())
            {
                using (var zlib = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true))
                    zlib.CopyTo(raw);
                inflated = raw.ToArray();
            }

            int stride = width * 4;
            int rowWithFilter = stride + 1;
            if (inflated.Length < height * rowWithFilter)
                throw new InvalidDataException("PNG image data is shorter than expected.");

            byte[] rgba = new byte[height * stride];
            byte[] previous = new byte[stride];
            byte[] current = new byte[stride];
            int source = 0;

            for (int y = 0; y < height; y++)
            {
                int filter = inflated[source++];
                Array.Copy(inflated, source, current, 0, stride);
                source += stride;
                UnfilterPngRow(current, previous, filter, bytesPerPixel: 4);
                Array.Copy(current, 0, rgba, y * stride, stride);
                (previous, current) = (current, previous);
            }

            byte[] bgra = new byte[rgba.Length];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                bgra[i] = rgba[i + 2];
                bgra[i + 1] = rgba[i + 1];
                bgra[i + 2] = rgba[i];
                bgra[i + 3] = rgba[i + 3];
            }

            return bgra;
        }

        /// <summary>Applies PNG row unfiltering for one decoded image row.</summary>
        private static void UnfilterPngRow(byte[] row, byte[] previous, int filter, int bytesPerPixel)
        {
            for (int i = 0; i < row.Length; i++)
            {
                int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                int up = previous[i];
                int upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;

                int add = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => PaethPredictor(left, up, upLeft),
                    _ => throw new InvalidDataException($"Unsupported PNG filter type {filter}."),
                };

                row[i] = (byte)(row[i] + add);
            }
        }

        /// <summary>Computes the PNG Paeth predictor.</summary>
        private static int PaethPredictor(int left, int up, int upLeft)
        {
            int p = left + up - upLeft;
            int pa = Math.Abs(p - left);
            int pb = Math.Abs(p - up);
            int pc = Math.Abs(p - upLeft);

            if (pa <= pb && pa <= pc) return left;
            return pb <= pc ? up : upLeft;
        }

        /// <summary>Presents frame.</summary>
        private unsafe void PresentFrame()
        {
            GL glApi = gl ?? throw new InvalidOperationException("OpenGL API is not initialized.");

            SDL_GL_MakeCurrent(window, glContext);
            SDL_GetWindowSize(window, out int windowW, out int windowH);

            fixed (byte* p = presentationBuf)
            {
                glApi.BindTexture(TextureTarget.Texture2D, frameTexture);
                glApi.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, FrameW, FrameH, PixelFormat.Bgra, PixelType.UnsignedByte, p);
            }

            glApi.Viewport(0, 0, (uint)Math.Max(1, windowW), (uint)Math.Max(1, windowH));
            glApi.Disable(EnableCap.Blend);
            glApi.ClearColor(0f, 0f, 0f, 1f);
            glApi.Clear((uint)ClearBufferMask.ColorBufferBit);

            (int mx, int my, int mw, int mh) = CalculateMonitorViewport(windowW, windowH);

            double scaleX = mw / (double)MonitorImageW;
            double scaleY = mh / (double)MonitorImageH;
            double screenW = MonitorScreenW * scaleX * MonitorScreenOverscan;
            double screenH = MonitorScreenH * scaleY * MonitorScreenOverscan;
            double screenCenterX = mx + (MonitorScreenX + MonitorScreenW * 0.5) * scaleX;
            double screenCenterY = my + (MonitorImageH - MonitorScreenY - MonitorScreenH * 0.5) * scaleY;
            int sx = (int)Math.Round(screenCenterX - screenW * 0.5);
            int sy = (int)Math.Round(screenCenterY - screenH * 0.5);
            int sw = (int)Math.Round(screenW);
            int sh = (int)Math.Round(screenH);
            DrawPresentationQuad(frameTexture, sx, sy, sw, sh, effectMode: 1);
            DrawPresentationQuad(monitorTexture, mx, my, mw, mh, effectMode: 0);

            SDL_GL_SwapWindow(window);
        }

        /// <summary>Draws presentation quad.</summary>
        private void DrawPresentationQuad(uint texture, int x, int y, int w, int h, int effectMode)
        {
            GL glApi = gl ?? throw new InvalidOperationException("OpenGL API is not initialized.");

            glApi.Viewport(x, y, (uint)Math.Max(1, w), (uint)Math.Max(1, h));
            if (effectMode == 1 || effectMode == 0)
            {
                glApi.Enable(EnableCap.Blend);
                glApi.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            }
            else
            {
                glApi.Disable(EnableCap.Blend);
            }

            glApi.UseProgram(presentShader);
            glApi.Uniform1(presentTextureUniform, 0);
            glApi.Uniform2(presentOutputSizeUniform, (float)w, (float)h);
            glApi.Uniform1(presentEffectModeUniform, effectMode);
            glApi.ActiveTexture(TextureUnit.Texture0);
            glApi.BindTexture(TextureTarget.Texture2D, texture);
            glApi.BindVertexArray(presentVao);
            glApi.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            glApi.BindVertexArray(0);
        }

        /// <summary>Draws the pause overlay text into the presentation buffer.</summary>
        private static (int X, int Y, int W, int H) CalculateMonitorViewport(int windowW, int windowH)
        {
            if (windowW <= 0 || windowH <= 0)
                return (0, 0, 1, 1);

            double frameAspect = MonitorImageW / (double)MonitorImageH;
            int w = windowW;
            int h = (int)Math.Round(w / frameAspect);
            if (h > windowH)
            {
                h = windowH;
                w = (int)Math.Round(h * frameAspect);
            }

            return ((windowW - w) / 2, (windowH - h) / 2, Math.Max(1, w), Math.Max(1, h));
        }

        /// <summary>Draws paused overlay.</summary>
        private void DrawPausedOverlay()
        {
            BlendRect(0, 0, FrameW, FrameH, 0, 0, 0, 90);

            DrawBlockTextCentered("PAUSED", 1);
        }

        /// <summary>Draws block text centered.</summary>
        private void DrawBlockTextCentered(string text, int scale)
        {
            const int glyphW = 5;
            const int glyphH = 7;
            const int spacing = 1;

            int textW = text.Length * glyphW + Math.Max(0, text.Length - 1) * spacing;
            int originX = (FrameW - textW * scale) / 2;
            int originY = (FrameH - glyphH * scale) / 2;

            for (int i = 0; i < text.Length; i++)
            {
                byte[] glyph = GetBlockGlyph(text[i]);
                int glyphX = originX + i * (glyphW + spacing) * scale;

                for (int y = 0; y < glyphH; y++)
                {
                    byte row = glyph[y];
                    for (int x = 0; x < glyphW; x++)
                    {
                        if ((row & (1 << (glyphW - 1 - x))) == 0)
                            continue;

                        BlendRect(glyphX + x * scale, originY + y * scale, scale, scale, 235, 235, 235, 175);
                    }
                }
            }
        }

        /// <summary>Gets block glyph.</summary>
        private static byte[] GetBlockGlyph(char ch)
        {
            return ch switch
            {
                'A' => new byte[] { 0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11 },
                'D' => new byte[] { 0x1E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1E },
                'E' => new byte[] { 0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F },
                'P' => new byte[] { 0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10 },
                'S' => new byte[] { 0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E },
                'U' => new byte[] { 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E },
                _ => new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
            };
        }

        /// <summary>Draws mute overlay.</summary>
        private void DrawMuteOverlay()
        {
            const int x = 5;
            const int y = FrameH - 12;

            BlendRect(x, y + 4, 2, 3, 230, 230, 230, 150);

            BlendLine(x + 2, y + 4, x + 5, y + 1, 230, 230, 230, 150);
            BlendLine(x + 2, y + 6, x + 5, y + 9, 230, 230, 230, 150);
            BlendLine(x + 5, y + 1, x + 5, y + 9, 230, 230, 230, 150);

            BlendLine(x + 1, y + 2, x + 7, y + 8, 255, 80, 80, 165);
            BlendLine(x + 7, y + 2, x + 1, y + 8, 255, 80, 80, 165);
        }

        /// <summary>Draws drive activity overlay.</summary>
        private void DrawDriveActivityOverlay()
        {
            long ticks = Interlocked.Read(ref driveActivityTicks);
            if (ticks == 0)
                return;

            double elapsedMs = (Stopwatch.GetTimestamp() - ticks) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs > 180.0)
                return;

            byte alpha = (byte)Math.Max(45, 145 - (int)(elapsedMs * 100.0 / 180.0));
            const int x = 16;
            const int y = FrameH - 8;

            BlendRect(x, y, 4, 4, 95, 255, 125, alpha);
            BlendPixel(x - 1, y + 1, 95, 255, 125, alpha);
            BlendPixel(x + 4, y + 1, 95, 255, 125, alpha);
            BlendPixel(x - 1, y + 2, 95, 255, 125, alpha);
            BlendPixel(x + 4, y + 2, 95, 255, 125, alpha);
        }

        /// <summary>Blends rect.</summary>
        private void BlendRect(int x, int y, int w, int h, byte r, byte g, byte b, byte a)
        {
            int x0 = Math.Clamp(x, 0, FrameW);
            int y0 = Math.Clamp(y, 0, FrameH);
            int x1 = Math.Clamp(x + w, 0, FrameW);
            int y1 = Math.Clamp(y + h, 0, FrameH);

            for (int py = y0; py < y1; py++)
                for (int px = x0; px < x1; px++)
                    BlendPixel(px, py, r, g, b, a);
        }

        /// <summary>Blends line.</summary>
        private void BlendLine(int x0, int y0, int x1, int y1, byte r, byte g, byte b, byte a)
        {
            int dx = Math.Abs(x1 - x0);
            int sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0);
            int sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;

            while (true)
            {
                BlendPixel(x0, y0, r, g, b, a);
                if (x0 == x1 && y0 == y1)
                    break;

                int e2 = 2 * err;
                if (e2 >= dy)
                {
                    err += dy;
                    x0 += sx;
                }
                if (e2 <= dx)
                {
                    err += dx;
                    y0 += sy;
                }
            }
        }

        /// <summary>Blends pixel.</summary>
        private void BlendPixel(int x, int y, byte r, byte g, byte b, byte a)
        {
            if (x < 0 || x >= FrameW || y < 0 || y >= FrameH)
                return;

            int p = (y * FrameW + x) * 4;
            int invA = 255 - a;
            presentationBuf[p] = (byte)((b * a + presentationBuf[p] * invA) / 255);
            presentationBuf[p + 1] = (byte)((g * a + presentationBuf[p + 1] * invA) / 255);
            presentationBuf[p + 2] = (byte)((r * a + presentationBuf[p + 2] * invA) / 255);
            presentationBuf[p + 3] = 0xFF;
        }

        /// <summary>Releases resources owned by this instance.</summary>
        public void Dispose()
        {
            if (gl is not null)
            {
                if (presentVao != 0) { gl.DeleteVertexArray(presentVao); presentVao = 0; }
                if (presentVbo != 0) { gl.DeleteBuffer(presentVbo); presentVbo = 0; }
                if (frameTexture != 0) { gl.DeleteTexture(frameTexture); frameTexture = 0; }
                if (monitorTexture != 0) { gl.DeleteTexture(monitorTexture); monitorTexture = 0; }
                if (presentShader != 0) { gl.DeleteProgram(presentShader); presentShader = 0; }
            }

            if (glContext != IntPtr.Zero) { SDL_GL_DeleteContext(glContext); glContext = IntPtr.Zero; }
            if (window != IntPtr.Zero) { SDL_DestroyWindow(window); window = IntPtr.Zero; }
        }

        /// <summary>
        /// Advances VIC raster timing by CPU cycles and optionally returns bus-steal stall cycles for the CPU.
        /// </summary>
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
                    ClearRasterWriteEventsForLine(currentRasterLine);
                    RaiseRasterIrqForLine(currentRasterLine, mem);
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

        /// <summary>Builds line bus steal mask.</summary>
        private static void BuildLineBusStealMask(int line, byte[] mem, bool[] mask)
        {
            Array.Clear(mask, 0, mask.Length);

            bool den = (mem[0xD011] & 0x10) != 0;
            int yScroll = mem[0xD011] & 0x07;
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
                int spriteRow = line - spriteY - 1;
                if (spriteRow >= 0 && spriteRow < height)
                {
                    // Approximate VIC-II sprite pointer/data DMA slots. This
                    // is still not a full BA/AEC sequencer, but it places the
                    // steals in the late-line sprite fetch window instead of
                    // at cycle 0, which is closer for raster-sensitive code.
                    int baseCycle = 55 + s;
                    if (baseCycle < mask.Length)
                        mask[baseCycle] = true;
                }
            }
        }

        /// <summary>Refreshes the current raster line from memory.</summary>
        public void RefreshCurrentRasterLine()
        {
            if (isResetting)
                return;

            ProcessRasterLine(currentRasterLine, cpu.memory.memory);
        }

        /// <summary>Clears raster write events.</summary>
        private void ClearRasterWriteEvents()
        {
            lock (rasterWriteEventLock)
            {
                for (int i = 0; i < rasterWriteEvents.Length; i++)
                    rasterWriteEvents[i].Clear();
            }
        }

        /// <summary>Clears raster write events for line.</summary>
        private void ClearRasterWriteEventsForLine(int line)
        {
            if (line < 0 || line >= rasterWriteEvents.Length)
                return;

            lock (rasterWriteEventLock)
                rasterWriteEvents[line].Clear();
        }

        /// <summary>Gets raster line start value.</summary>
        private byte GetRasterLineStartValue(int line, int address, byte fallback)
        {
            if (line < 0 || line >= rasterWriteEvents.Length)
                return fallback;

            ushort vicAddress = (ushort)address;
            lock (rasterWriteEventLock)
            {
                List<RasterWriteEvent> events = rasterWriteEvents[line];
                for (int i = 0; i < events.Count; i++)
                {
                    RasterWriteEvent evt = events[i];
                    if (evt.Address != vicAddress)
                        continue;

                    if (IsSpriteRasterRegister(vicAddress))
                        return evt.OldValue;

                    // If a raster split writes before the visible playfield starts,
                    // use the new value for this line. Late writes still keep the
                    // old value so they don't repaint pixels that were already drawn.
                    return RasterCycleToFrameX(evt.Cycle) <= FramePlayfieldX
                        ? evt.NewValue
                        : evt.OldValue;
                }
            }

            return fallback;
        }

        /// <summary>Determines whether sprite raster register.</summary>
        private static bool IsSpriteRasterRegister(ushort address)
        {
            return address switch
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

        /// <summary>Raises raster irq for line.</summary>
        private void RaiseRasterIrqForLine(int line, byte[] mem)
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
        }

        /// <summary>
        /// Builds one raster line by applying pending register writes, border state, visible-mode rendering, sprites, and raster IRQ checks.
        /// </summary>
        private void ProcessRasterLine(int line, byte[] mem)
        {
            int frameY = line - FrameFirstRasterLine;
            if (line >= FrameFirstRasterLine && line <= FrameLastRasterLine)
                FillFrameLineWithBorderEvents(frameY, line, 0, FrameW - 1, (byte)(mem[0xD020] & 0x0F));

            if (line < FrameFirstRasterLine || line > FrameLastRasterLine)
                return;

            byte d011 = GetRasterLineStartValue(line, 0xD011, mem[0xD011]);
            byte d016 = GetRasterLineStartValue(line, 0xD016, mem[0xD016]);
            byte d018 = GetRasterLineStartValue(line, 0xD018, mem[0xD018]);
            byte bg0 = (byte)(GetRasterLineStartValue(line, 0xD021, mem[0xD021]) & 0x0F);
            byte bg1 = (byte)(GetRasterLineStartValue(line, 0xD022, mem[0xD022]) & 0x0F);
            byte bg2 = (byte)(GetRasterLineStartValue(line, 0xD023, mem[0xD023]) & 0x0F);
            byte bg3 = (byte)(GetRasterLineStartValue(line, 0xD024, mem[0xD024]) & 0x0F);
            byte dd00 = mem[0xDD00];
            byte dd02 = mem[0xDD02];
            byte spriteEnable = GetRasterLineStartValue(line, 0xD015, mem[0xD015]);
            byte spriteXExpand = GetRasterLineStartValue(line, 0xD01D, mem[0xD01D]);
            byte spriteYExpand = GetRasterLineStartValue(line, 0xD017, mem[0xD017]);
            byte spriteMulticolor = GetRasterLineStartValue(line, 0xD01C, mem[0xD01C]);
            byte spritePriority = GetRasterLineStartValue(line, 0xD01B, mem[0xD01B]);
            byte spriteXHigh = GetRasterLineStartValue(line, 0xD010, mem[0xD010]);
            byte spriteMc1Color = GetRasterLineStartValue(line, 0xD025, mem[0xD025]);
            byte spriteMc2Color = GetRasterLineStartValue(line, 0xD026, mem[0xD026]);
            byte[] spriteColors = new byte[8];
            byte[] spriteXPos = new byte[8];
            byte[] spriteYPos = new byte[8];
            byte[] spritePtrs = new byte[8];
            for (int i = 0; i < 8; i++)
            {
                spriteColors[i] = GetRasterLineStartValue(line, 0xD027 + i, mem[0xD027 + i]);
                spriteXPos[i] = GetRasterLineStartValue(line, 0xD000 + i * 2, mem[0xD000 + i * 2]);
                spriteYPos[i] = GetRasterLineStartValue(line, 0xD001 + i * 2, mem[0xD001 + i * 2]);
            }

            int playY = line - VisibleTop;
            int fineY = d011 & 0x07;
            // VisibleTop is calibrated around the normal C64 text baseline (yscroll=3).
            // Apply only the delta from that baseline so we don't wrap/crop the 25x8 matrix.
            int fineYOffset = fineY - 3;
            int scrolledY = playY - fineYOffset;
            int row = scrolledY >> 3;
            int dy = scrolledY & 0x07;

            bool playfieldLine = line >= VisibleTop && line <= VisibleBottom;
            bool matrixVisible = playfieldLine && scrolledY >= 0 && scrolledY < ScreenH;
            int bank = GetVicBankBase(dd00, dd02);
            int screenAddr = bank + ((d018 >> 4) & 0x0F) * 0x400;
            int spritePtrBase = screenAddr + 0x03F8;
            for (int i = 0; i < 8; i++)
                spritePtrs[i] = cpu.memory.ReadVicByte((ulong)(spritePtrBase + i));

            if (matrixVisible)
            {
                for (int col = 0; col < 40; col++)
                    cachedScreenRow[col] = cpu.memory.ReadVicByte((ulong)(screenAddr + row * 40 + col));

                int bitmapAddr = bank + (((d018 & 0x08) != 0) ? 0x2000 : 0x0000);
                cachedBitmapRows[dy] ??= new byte[40];
                cachedBitmapRowNum[dy] = row;
                for (int col = 0; col < 40; col++)
                    cachedBitmapRows[dy][col] = cpu.memory.ReadVicByte((ulong)(bitmapAddr + (row * 40 + col) * 8 + dy));
            }

            byte[] colorRow = new byte[40];
            if (matrixVisible)
            {
                for (int col = 0; col < 40; col++)
                    colorRow[col] = mem[0xD800 + row * 40 + col];
            }

            RenderScanline(frameY, playY, d011, d016, d018, bg0, bg1, bg2, bg3, dd00, dd02, spriteEnable, spriteXExpand, spriteYExpand, spriteMulticolor, spritePriority, spriteXHigh, spriteMc1Color, spriteMc2Color, spriteColors, spriteXPos, spriteYPos, spritePtrs, colorRow, cachedScreenRow, cachedBitmapRows, dy, matrixVisible);
        }

        /// <summary>
        /// Renders one visible VIC playfield scanline using the active text or bitmap mode and sprite state.
        /// </summary>
        private void RenderScanline(int frameY, int playY, byte d011, byte d016, byte d018, byte bg0, byte bg1, byte bg2, byte bg3, byte dd00, byte dd02, byte spriteEnable, byte spriteXExpand, byte spriteYExpand, byte spriteMulticolor, byte spritePriority, byte spriteXHigh, byte spriteMc1Color, byte spriteMc2Color, byte[] spriteColors, byte[] spriteXPos, byte[] spriteYPos, byte[] spritePtrs, byte[] colorRow, byte[] cachedScreenRow, byte[][] cachedBitmapRows, int dy, bool matrixVisible)
        {
            int bank = GetVicBankBase(dd00, dd02);
            int screenAddr = bank + ((d018 >> 4) & 0x0F) * 0x400;
            int charAddr = bank + ((d018 >> 1) & 0x07) * 0x800;

            bool screenOn = (d011 & 0x10) != 0;
            bool bmm = (d011 & 0x20) != 0;
            bool ecm = (d011 & 0x40) != 0;
            bool mcm = (d016 & 0x10) != 0;
            bool invalidBitmapMode = bmm && ecm;

            Array.Clear(fgLine, 0, fgLine.Length);
            Array.Clear(spriteLine, 0, spriteLine.Length);

            if (!screenOn || !matrixVisible)
            {
                bool inPlayfieldY = playY >= 0 && playY < ScreenH;
                if (screenOn && inPlayfieldY)
                    FillLineSolid(frameY, bg0);
                else
                    FillLineRangeWithBorderEvents(frameY, 0, ScreenW - 1, (byte)(cpu.memory.memory[0xD020] & 0x0F));
            }
            else if (invalidBitmapMode)
                FillLineSolid(frameY, 0);
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

            if (!invalidBitmapMode)
                RenderSpritesScanline(frameY, bank, spriteEnable, spriteXExpand, spriteYExpand, spriteMulticolor, spritePriority, spriteXHigh, spriteMc1Color, spriteMc2Color, spriteColors, spriteXPos, spriteYPos, spritePtrs);

            ApplyOuterBorders(frameY);
            ApplyInnerBorders(frameY, playY, d011, d016);
        }

        /// <summary>Gets vic bank base.</summary>
        private static int GetVicBankBase(byte dd00, byte dd02)
        {
            // CIA2 port A controls VIC bank on PA0/PA1. Input bits read high.
            byte effectivePortA = (byte)((dd00 & dd02) | (~dd02 & 0xFF));
            int sel = effectivePortA & 0x03;
            return (3 - sel) * 0x4000;
        }

        /// <summary>Resolves char source.</summary>
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

        /// <summary>Fills line solid.</summary>
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

        /// <summary>Fills frame line with border events.</summary>
        private void FillFrameLineWithBorderEvents(int y, int rasterLine, int xStart, int xEnd, byte fallbackBorderIdx)
        {
            if (xStart < 0) xStart = 0;
            if (xEnd >= FrameW) xEnd = FrameW - 1;
            if (xStart > xEnd) return;

            if (rasterLine < 0 || rasterLine >= rasterWriteEvents.Length)
            {
                FillFrameLineRange(y, xStart, xEnd, C64Palette[fallbackBorderIdx & 0x0F]);
                return;
            }

            lock (rasterWriteEventLock)
            {
                List<RasterWriteEvent> events = rasterWriteEvents[rasterLine];
                bool foundD020 = false;
                byte borderIdx = fallbackBorderIdx;
                int cursor = xStart;

                for (int i = 0; i < events.Count; i++)
                {
                    RasterWriteEvent evt = events[i];
                    if (evt.Address != 0xD020)
                        continue;

                    int eventX = RasterCycleToFrameX(evt.Cycle);
                    if (!foundD020)
                    {
                        borderIdx = (byte)(evt.OldValue & 0x0F);
                        foundD020 = true;
                    }

                    if (eventX > xStart)
                    {
                        int segmentEnd = Math.Min(eventX - 1, xEnd);
                        if (cursor <= segmentEnd)
                            FillFrameLineRange(y, cursor, segmentEnd, C64Palette[borderIdx & 0x0F]);
                        cursor = Math.Max(cursor, eventX);
                    }

                    borderIdx = (byte)(evt.NewValue & 0x0F);
                    if (cursor > xEnd)
                        return;
                }

                FillFrameLineRange(y, cursor, xEnd, C64Palette[borderIdx & 0x0F]);
            }
        }

        /// <summary>Fills frame line range.</summary>
        private void FillFrameLineRange(int y, int xStart, int xEnd, int argb)
        {
            if (xStart < 0) xStart = 0;
            if (xEnd >= FrameW) xEnd = FrameW - 1;
            if (xStart > xEnd) return;

            int p = (y * FrameW + xStart) * 4;
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

        /// <summary>Maps a raster cycle to a frame X coordinate.</summary>
        private static int RasterCycleToFrameX(int cycle)
        {
            if (cycle <= 0)
                return 0;
            if (cycle >= CyclesPerRasterLine)
                return FrameW;

            return cycle * FrameW / CyclesPerRasterLine;
        }

        /// <summary>Fills line range with border events.</summary>
        private void FillLineRangeWithBorderEvents(int y, int xStart, int xEnd, byte fallbackBorderIdx)
        {
            if (xStart < 0) xStart = 0;
            if (xEnd >= ScreenW) xEnd = ScreenW - 1;
            if (xStart > xEnd) return;

            int rasterLine = y + FrameFirstRasterLine;
            FillFrameLineWithBorderEvents(
                y,
                rasterLine,
                FramePlayfieldX + xStart,
                FramePlayfieldX + xEnd,
                fallbackBorderIdx);
        }

        /// <summary>Applies inner borders.</summary>
        private void ApplyInnerBorders(int frameY, int playY, byte d011, byte d016)
        {
            byte borderIdx = (byte)(cpu.memory.memory[0xD020] & 0x0F);

            // RSEL=0 selects 24-row display: 4px inner border at top and bottom.
            bool row25 = (d011 & 0x08) != 0;
            int firstVisibleY = row25 ? 0 : 4;
            int lastVisibleYExclusive = row25 ? ScreenH : (ScreenH - 4);

            if (playY < firstVisibleY || playY >= lastVisibleYExclusive)
            {
                FillLineRangeWithBorderEvents(frameY, 0, ScreenW - 1, borderIdx);
                Array.Clear(fgLine, 0, fgLine.Length);
                return;
            }

            // CSEL=0 selects 38-column display: 7px inner border on each side.
            bool col40 = (d016 & 0x08) != 0;
            if (!col40)
            {
                FillLineRangeWithBorderEvents(frameY, 0, 6, borderIdx);
                FillLineRangeWithBorderEvents(frameY, ScreenW - 7, ScreenW - 1, borderIdx);
                ClearFgLineRange(0, 6);
                ClearFgLineRange(ScreenW - 7, ScreenW - 1);
            }
        }

        /// <summary>Applies outer borders.</summary>
        private void ApplyOuterBorders(int frameY)
        {
            byte borderIdx = (byte)(cpu.memory.memory[0xD020] & 0x0F);
            int rasterLine = frameY + FrameFirstRasterLine;

            FillFrameLineWithBorderEvents(frameY, rasterLine, 0, FramePlayfieldX - 1, borderIdx);
            FillFrameLineWithBorderEvents(frameY, rasterLine, FramePlayfieldX + ScreenW, FrameW - 1, borderIdx);
        }

        /// <summary>Clears fg line range.</summary>
        private void ClearFgLineRange(int xStart, int xEnd)
        {
            if (xStart < 0) xStart = 0;
            if (xEnd >= ScreenW) xEnd = ScreenW - 1;
            if (xStart > xEnd) return;
            Array.Clear(fgLine, xStart, xEnd - xStart + 1);
        }

        /// <summary>Renders line standard text.</summary>
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

        /// <summary>Renders line multicolor text.</summary>
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

        /// <summary>Renders line extended bg text.</summary>
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
                // Color RAM provides the foreground color; upper code bits select bg0-bg3.
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

        /// <summary>Renders line hires bitmap.</summary>
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

        /// <summary>Renders line multicolor bitmap.</summary>
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
                fgBase += 8;
            }
        }

        /// <summary>
        /// Renders all enabled sprites that intersect the current scanline, including expansion, multicolor, priority, and collision state.
        /// </summary>
        private void RenderSpritesScanline(int frameY, int bank, byte spriteEnable, byte spriteXExpand, byte spriteYExpand, byte spriteMulticolor, byte spritePriority, byte spriteXHigh, byte spriteMc1Color, byte spriteMc2Color, byte[] spriteColors, byte[] spriteXPos, byte[] spriteYPos, byte[] spritePtrs)
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
                int frameSpriteX = sx + FramePlayfieldX - 24;
                int frameSpriteY = sy + FramePlayfieldY - 50;

                bool xExp = (spriteXExpand & mask) != 0;
                bool yExp = (spriteYExpand & mask) != 0;
                bool mc = (spriteMulticolor & mask) != 0;
                bool behindBg = (spritePriority & mask) != 0;
                int color = C64Palette[spriteColors[s] & 0x0F];

                int spriteHeight = yExp ? 42 : 21;
                int spriteRow = frameY - frameSpriteY;
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
                            PaintSpritePixelLine(frameSpriteX + basePix + w, frameY, c, behindBg, s);
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
                            PaintSpritePixelLine(frameSpriteX + basePix + w, frameY, color, behindBg, s);
                    }
                }
            }
        }

        /// <summary>Paints sprite pixel line.</summary>
        private void PaintSpritePixelLine(int frameX, int frameY, int color, bool behindBg, int spriteIdx)
        {
            if (frameX < 0 || frameX >= FrameW || frameY < 0 || frameY >= FrameH) return;
            byte myBit = (byte)(1 << spriteIdx);
            byte[] mem = cpu.memory.memory;

            int playfieldX = frameX - FramePlayfieldX;
            bool inPlayfield = playfieldX >= 0 && playfieldX < ScreenW;

            byte priorSprites = spriteLine[frameX];
            byte priorOtherSprites = (byte)(priorSprites & ~myBit);
            if (priorOtherSprites != 0)
            {
                mem[0xD01E] |= (byte)(priorOtherSprites | myBit);
                if ((mem[0xD01A] & 0x04) != 0 && (mem[0xD019] & 0x04) == 0)
                {
                    mem[0xD019] |= 0x84;
                    cpu.InitiateIRQ(0xFFFE);
                }
            }

            bool foreground = inPlayfield && fgLine[playfieldX];
            if (foreground)
            {
                mem[0xD01F] |= myBit;
                if ((mem[0xD01A] & 0x02) != 0 && (mem[0xD019] & 0x02) == 0)
                {
                    mem[0xD019] |= 0x82;
                    cpu.InitiateIRQ(0xFFFE);
                }
            }

            spriteLine[frameX] |= myBit;

            if (behindBg && foreground) return;

            int p = (frameY * FrameW + frameX) * 4;
            renderBuf[p] = (byte)color;
            renderBuf[p + 1] = (byte)(color >> 8);
            renderBuf[p + 2] = (byte)(color >> 16);
            renderBuf[p + 3] = 0xFF;
        }

        /// <summary>Takes screenshot.</summary>
        public void TakeScreenshot()
        {
            try
            {
                RedrawScreen();

                GL glApi = gl ?? throw new InvalidOperationException("OpenGL API is not initialized.");
                SDL_GL_MakeCurrent(window, glContext);
                SDL_GetWindowSize(window, out int width, out int height);

                byte[] rgbaBottomUp = new byte[width * height * 4];
                unsafe
                {
                    fixed (byte* p = rgbaBottomUp)
                    {
                        glApi.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
                    }
                }

                byte[] bgraTopDown = new byte[rgbaBottomUp.Length];
                int stride = width * 4;
                for (int y = 0; y < height; y++)
                {
                    int src = (height - 1 - y) * stride;
                    int dst = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        bgraTopDown[dst++] = rgbaBottomUp[src + 2];
                        bgraTopDown[dst++] = rgbaBottomUp[src + 1];
                        bgraTopDown[dst++] = rgbaBottomUp[src];
                        bgraTopDown[dst++] = rgbaBottomUp[src + 3];
                        src += 4;
                    }
                }

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                string filename = $"c64_screenshot_{timestamp}.png";
                WritePng(filename, bgraTopDown, width, height);
                Console.Error.WriteLine($"[SCREENSHOT] Saved to {filename}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Screenshot failed: {ex.Message}");
            }
        }

        /// <summary>Writes png.</summary>
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

        /// <summary>Writes png chunk.</summary>
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

        /// <summary>Writes Uint32 big endian.</summary>
        private static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        /// <summary>Reads u int32 big endian.</summary>
        private static uint ReadUInt32BigEndian(byte[] buffer, int offset)
        {
            return ((uint)buffer[offset] << 24) |
                   ((uint)buffer[offset + 1] << 16) |
                   ((uint)buffer[offset + 2] << 8) |
                   buffer[offset + 3];
        }

        /// <summary>Writes u int32 big endian.</summary>
        private static void WriteUInt32BigEndian(Stream output, uint value)
        {
            output.WriteByte((byte)(value >> 24));
            output.WriteByte((byte)(value >> 16));
            output.WriteByte((byte)(value >> 8));
            output.WriteByte((byte)value);
        }

        /// <summary>Computes a PNG CRC-32 value.</summary>
        private static uint Crc32(byte[] typeBytes, byte[] data)
        {
            uint crc = 0xFFFFFFFF;

            for (int i = 0; i < typeBytes.Length; i++)
                crc = Crc32Update(crc, typeBytes[i]);

            for (int i = 0; i < data.Length; i++)
                crc = Crc32Update(crc, data[i]);

            return ~crc;
        }

        /// <summary>Updates an in-progress CRC-32 value.</summary>
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
