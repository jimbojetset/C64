using System.Diagnostics;
using Silk.NET.OpenGL;
using static SDL2.SDL;

namespace C64
{
    /// <summary>
    /// Hosts an SDL2 + OpenGL window long enough to run an ImGui software
    /// selection modal, then returns the selected file path.
    /// </summary>
    internal static class SoftwareFileWindow
    {
        public static string? Prompt()
        {
            IReadOnlyList<SoftwareFileEntry> files = DiscoverSoftwareFiles();

            if (SDL_InitSubSystem(SDL_INIT_VIDEO) != 0)
            {
                Console.Error.WriteLine($"SDL video init failed: {SDL_GetError()}");
                return null;
            }

            SDL_GL_SetAttribute(SDL_GLattr.SDL_GL_CONTEXT_MAJOR_VERSION, 3);
            SDL_GL_SetAttribute(SDL_GLattr.SDL_GL_CONTEXT_MINOR_VERSION, 3);
            SDL_GL_SetAttribute(SDL_GLattr.SDL_GL_CONTEXT_PROFILE_MASK,
                (int)SDL_GLprofile.SDL_GL_CONTEXT_PROFILE_CORE);
            SDL_GL_SetAttribute(SDL_GLattr.SDL_GL_DOUBLEBUFFER, 1);
            SDL_GL_SetAttribute(SDL_GLattr.SDL_GL_DEPTH_SIZE, 24);

            const int Width = 600;
            const int Height = 420;

            IntPtr win = SDL_CreateWindow(
                "C64 Emulator - load software",
                SDL_WINDOWPOS_CENTERED, SDL_WINDOWPOS_CENTERED,
                Width, Height,
                SDL_WindowFlags.SDL_WINDOW_OPENGL |
                SDL_WindowFlags.SDL_WINDOW_SHOWN |
                SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
            if (win == IntPtr.Zero)
            {
                Console.Error.WriteLine($"SDL_CreateWindow failed: {SDL_GetError()}");
                return null;
            }

            uint windowId = SDL_GetWindowID(win);

            IntPtr glCtx = SDL_GL_CreateContext(win);
            if (glCtx == IntPtr.Zero)
            {
                Console.Error.WriteLine($"SDL_GL_CreateContext failed: {SDL_GetError()}");
                SDL_DestroyWindow(win);
                return null;
            }

            SDL_GL_MakeCurrent(win, glCtx);
            SDL_GL_SetSwapInterval(1);

            GL gl = GL.GetApi(name => SDL_GL_GetProcAddress(name));

            string? selected = null;
            ImGuiController? controller = null;
            try
            {
                controller = new ImGuiController(gl, win, Width, Height);
                var selector = new SoftwareFileSelector(files);

                var sw = Stopwatch.StartNew();
                double last = sw.Elapsed.TotalSeconds;
                bool quit = false;

                while (!quit && !selector.IsCompleted)
                {
                    while (SDL_PollEvent(out SDL_Event ev) != 0)
                    {
                        controller.ProcessEvent(ev);

                        switch (ev.type)
                        {
                            case SDL_EventType.SDL_QUIT:
                                quit = true;
                                break;

                            case SDL_EventType.SDL_KEYDOWN:
                                if (ev.key.keysym.sym == SDL_Keycode.SDLK_ESCAPE)
                                    quit = true;
                                break;

                            case SDL_EventType.SDL_WINDOWEVENT:
                                if (ev.window.windowID != windowId)
                                    break;

                                if (ev.window.windowEvent == SDL_WindowEventID.SDL_WINDOWEVENT_CLOSE)
                                    quit = true;
                                else if (ev.window.windowEvent == SDL_WindowEventID.SDL_WINDOWEVENT_SIZE_CHANGED)
                                    controller.WindowResized(ev.window.data1, ev.window.data2);
                                break;
                        }
                    }

                    double now = sw.Elapsed.TotalSeconds;
                    float delta = (float)(now - last);
                    last = now;

                    controller.NewFrame(delta);
                    selector.Draw();

                    SDL_GetWindowSize(win, out int w, out int h);
                    gl.Viewport(0, 0, (uint)w, (uint)h);
                    gl.ClearColor(0.07f, 0.07f, 0.10f, 1f);
                    gl.Clear((uint)ClearBufferMask.ColorBufferBit);

                    controller.Render();
                    SDL_GL_SwapWindow(win);
                }

                if (!quit)
                    selected = selector.SelectedPath;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Software picker failed: {ex.Message}");
            }
            finally
            {
                controller?.Dispose();
                SDL_GL_DeleteContext(glCtx);
                SDL_DestroyWindow(win);

                while (SDL_PollEvent(out _) != 0) { }
            }

            return selected;
        }

        private static IReadOnlyList<SoftwareFileEntry> DiscoverSoftwareFiles()
        {
            string? softwareDir = FindSoftwareDirectory();
            if (softwareDir is null)
                return Array.Empty<SoftwareFileEntry>();

            return Directory.EnumerateFiles(softwareDir, "*", SearchOption.AllDirectories)
                .OrderBy(path => Path.GetRelativePath(softwareDir, path), StringComparer.OrdinalIgnoreCase)
                .Select(path =>
                {
                    string relative = Path.GetRelativePath(softwareDir, path);
                    string display = Path.ChangeExtension(relative, null) ?? relative;
                    string extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(extension))
                        extension = "FILE";
                    return new SoftwareFileEntry(path, display, extension);
                })
                .ToArray();
        }

        private static string? FindSoftwareDirectory()
        {
            string[] candidates =
            {
                Path.Combine(Environment.CurrentDirectory, "Software"),
                Path.Combine(AppContext.BaseDirectory, "Software"),
                Path.Combine(Environment.CurrentDirectory, "C64", "Software"),
            };

            return candidates.FirstOrDefault(Directory.Exists);
        }
    }
}
