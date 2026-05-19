// ============================================================================
// Project:     C64
// File:        SoundDeviceWindow.cs
// Description: Temporary SDL/OpenGL host window for the ImGui audio device
//              selection prompt.
// Author:      James Booth
// Created:     2025
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Commodore 64 and related ROMs are property of their respective
//              rights holders. This emulator is for educational purposes only.
// ============================================================================

using Silk.NET.OpenGL;
using System.Diagnostics;
using static SDL2.SDL;

namespace C64
{
    /// <summary>
    /// Hosts an SDL2 + OpenGL window long enough to run an ImGui
    /// <see cref="AudioDeviceSelector"/> modal, then disposes everything and
    /// returns the user's choice.
    /// </summary>
    internal static class SoundDeviceWindow
    {
        /// <summary>Shows the picker window and returns the selected value.</summary>
        /// <param name="devices">The audio device names to show in the picker.</param>
        /// <returns>The selected or resolved string value, or null when no value is available.</returns>
        public static string? Prompt(List<string> devices)
        {
            if (devices.Count == 0) return null;

            if (SDL_InitSubSystem(SDL_INIT_VIDEO) != 0)
            {
                Console.Error.WriteLine($"SDL video init failed: {SDL_GetError()}");
                return null;
            }

            // Request a core-profile OpenGL 3.3 context (matches ImGuiController).
            SDL_GL_SetAttribute(SDL_GLattr.SDL_GL_CONTEXT_MAJOR_VERSION, 3);
            SDL_GL_SetAttribute(SDL_GLattr.SDL_GL_CONTEXT_MINOR_VERSION, 3);
            SDL_GL_SetAttribute(SDL_GLattr.SDL_GL_CONTEXT_PROFILE_MASK,
                (int)SDL_GLprofile.SDL_GL_CONTEXT_PROFILE_CORE);
            SDL_GL_SetAttribute(SDL_GLattr.SDL_GL_DOUBLEBUFFER, 1);
            SDL_GL_SetAttribute(SDL_GLattr.SDL_GL_DEPTH_SIZE, 24);

            const int Width = 520;
            const int Height = 360;

            IntPtr win = SDL_CreateWindow(
                "C64 Emulator - select audio device",
                SDL_WINDOWPOS_CENTERED, SDL_WINDOWPOS_CENTERED,
                Width, Height,
                SDL_WindowFlags.SDL_WINDOW_OPENGL |
                SDL_WindowFlags.SDL_WINDOW_SHOWN);
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
            SDL_GL_SetSwapInterval(1); // vsync

            GL gl = GL.GetApi(name => SDL_GL_GetProcAddress(name));

            string? chosen = null;
            ImGuiController? controller = null;
            try
            {
                controller = new ImGuiController(gl, Width, Height);
                var selector = new AudioDeviceSelector(devices);

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
                                {
                                    quit = true;
                                }
                                else if (ev.window.windowEvent ==
                                         SDL_WindowEventID.SDL_WINDOWEVENT_SIZE_CHANGED)
                                {
                                    controller.WindowResized(ev.window.data1, ev.window.data2);
                                }
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
                    chosen = selector.SelectedDeviceName;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Audio device picker failed: {ex.Message}");
            }
            finally
            {
                controller?.Dispose();
                SDL_GL_DeleteContext(glCtx);
                SDL_DestroyWindow(win);

                // Drain residual events so they don't leak into the emulator loop.
                while (SDL_PollEvent(out _) != 0) { }
            }

            return chosen;
        }
    }
}