using System.Collections.Concurrent;
using C64.CPU;
using static SDL2.SDL;

namespace C64
{

    /// <summary>
    /// Standalone keyboard and joystick port 2 controller.
    /// Owns the C64 keyboard matrix, the PETSCII key queue, and the
    /// joystick port 2 byte.  The SDL main loop hands every key event
    /// to <see cref="HandleSdlEvent"/> and calls nothing else.
    /// </summary>
    internal sealed class Keyboard : IDisposable
    {
        private readonly CPU_6510 cpu;

        private readonly ConcurrentQueue<byte> keyQueue = new ConcurrentQueue<byte>();

        // C64 keyboard matrix: 8 rows, each column bit is active-low.
        private readonly byte[] keyboardMatrix = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

        private volatile byte keyboardJoystick2 = 0xFF;
        private volatile byte controllerJoystick2 = 0xFF;
        private IntPtr gameController;
        private int gameControllerInstanceId = -1;
        private byte controllerButtonMask;
        private byte controllerAxisMask;

        private bool shiftLockActive;
        private const short ControllerDeadZone = 12000;

        // ?? Callbacks wired by C64Emulator after construction ?????????????????

        /// <summary>Invoked when F12 / Ctrl+R is pressed.</summary>
        public Action? OnHardReset { get; set; }

        /// <summary>Invoked when Ctrl+O is pressed.</summary>
        public Action? OnLoad { get; set; }

        /// <summary>Invoked when Ctrl+S is pressed.</summary>
        public Action? OnSave { get; set; }

        /// <summary>Invoked when RESTORE key equivalent is pressed.</summary>
        public Action? OnRestoreNmi { get; set; }

        /// <summary>Invoked when Shift+S is pressed (screenshot).</summary>
        public Action? OnScreenshot { get; set; }

        /// <summary>Invoked when Ctrl+Q is pressed.</summary>
        public Action? OnToggleMute { get; set; }

        /// <summary>Invoked when Ctrl+P is pressed.</summary>
        public Action? OnTogglePause { get; set; }

        /// <summary>Invoked when Ctrl+A is pressed.</summary>
        public Action? OnSelectAudioDevice { get; set; }

        /// <summary>Initializes a new Keyboard instance.</summary>
        /// <param name="cpu">The CPU instance connected to this component.</param>
        public Keyboard(CPU_6510 cpu)
        {
            this.cpu = cpu;
        }

        // ?? Public API ????????????????????????????????????????????????????????

        /// <summary>CIA-1 port A ($DC00) joystick port 2 byte (active-low).</summary>
        public byte Joystick2 => (byte)(keyboardJoystick2 & controllerJoystick2);

        /// <summary>Initializes SDL game controller support.</summary>
        public void InitGameControllers()
        {
            SDL_GameControllerEventState(SDL_ENABLE);
            OpenFirstAvailableController();
        }

        /// <summary>
        /// Scans the keyboard matrix against the supplied CIA-1 row latch and DDR
        /// values and returns the resulting active-low column byte, exactly as the
        /// real CIA-1 does.
        /// </summary>
        /// <param name="rowLatch">The CIA row latch value used for keyboard scanning.</param>
        /// <param name="rowDdr">The CIA row data direction register used for keyboard scanning.</param>
        /// <returns>The byte value produced by the operation.</returns>
        public byte ScanMatrix(byte rowLatch, byte rowDdr)
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

        /// <summary>
        /// Drains the PETSCII key queue into the C64 keyboard buffer ($0277�$0280 / $C6).
        /// Call once per CIA tick from the IRQ thread.
        /// </summary>
        public void DrainQueue()
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

        /// <summary>
        /// Resets the keyboard matrix, joystick port 2, and flushes the key queue.
        /// Call from <c>C64Emulator.HardReset</c> / <c>InitHardware</c>.
        /// </summary>
        public void Reset()
        {
            keyboardJoystick2 = 0xFF;
            controllerJoystick2 = 0xFF;
            controllerButtonMask = 0;
            controllerAxisMask = 0;
            for (int i = 0; i < keyboardMatrix.Length; i++)
                keyboardMatrix[i] = 0xFF;
            while (keyQueue.TryDequeue(out _)) { }
            shiftLockActive = false;
        }

        /// <summary>Enqueues a raw PETSCII byte for typed-text injection (e.g. from file load).</summary>
        public void EnqueuePetscii(byte petscii) => keyQueue.Enqueue(petscii);

        /// <summary>
        /// Handles a single SDL event.
        /// Returns <c>true</c> if the emulator should quit.
        /// </summary>
        /// <param name="ev">The SDL event to process.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        public bool HandleSdlEvent(SDL_Event ev)
        {
            switch (ev.type)
            {
                case SDL_EventType.SDL_KEYDOWN:
                    return HandleKeyDown(ev.key);
                case SDL_EventType.SDL_KEYUP:
                    HandleKeyUp(ev.key);
                    return false;
                case SDL_EventType.SDL_CONTROLLERDEVICEADDED:
                    HandleControllerAdded(ev.cdevice);
                    return false;
                case SDL_EventType.SDL_CONTROLLERDEVICEREMOVED:
                    HandleControllerRemoved(ev.cdevice);
                    return false;
                case SDL_EventType.SDL_CONTROLLERBUTTONDOWN:
                case SDL_EventType.SDL_CONTROLLERBUTTONUP:
                    HandleControllerButton(ev.cbutton);
                    return false;
                case SDL_EventType.SDL_CONTROLLERAXISMOTION:
                    HandleControllerAxis(ev.caxis);
                    return false;
                default:
                    return false;
            }
        }

        /// <summary>Releases resources owned by this instance.</summary>
        public void Dispose()
        {
            CloseController();
        }

        // ?? Private implementation ????????????????????????????????????????????

        /// <summary>Handles key down.</summary>
        /// <param name="ke">The SDL keyboard event to process.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        private bool HandleKeyDown(SDL_KeyboardEvent ke)
        {
            if (ke.repeat != 0) return false;

            SDL_Keycode sym = ke.keysym.sym;
            SDL_Keymod mod = ke.keysym.mod;
            bool ctrl = (mod & SDL_Keymod.KMOD_CTRL) != 0;
            bool shift = (mod & SDL_Keymod.KMOD_SHIFT) != 0;
            bool alt = (mod & SDL_Keymod.KMOD_ALT) != 0;

            if (sym == SDL_Keycode.SDLK_F12)
            {
                OnHardReset?.Invoke();
                return false;
            }

            // C64 SHIFT LOCK on a modern keyboard.
            if (sym == SDL_Keycode.SDLK_CAPSLOCK)
            {
                shiftLockActive = !shiftLockActive;
                SetMatrixKey(1, 7, shiftLockActive);
                SetMatrixKey(6, 4, shiftLockActive);
                return false;
            }

            if (sym == SDL_Keycode.SDLK_PAGEUP || sym == SDL_Keycode.SDLK_PAUSE)
            {
                OnRestoreNmi?.Invoke();
                return false;
            }

            // Let common text-entry punctuation land in the BASIC input buffer
            // as the character the user typed on the host keyboard.
            if (!ctrl && !alt && sym == SDL_Keycode.SDLK_8 && shift)
            {
                keyQueue.Enqueue((byte)'*');
                return false;
            }

            if (!ctrl && !alt && sym == SDL_Keycode.SDLK_KP_MULTIPLY)
            {
                keyQueue.Enqueue((byte)'*');
                return false;
            }

            if (sym == SDL_Keycode.SDLK_q && (shift || alt) && !ctrl)
            {
                return true;
            }

            if (sym == SDL_Keycode.SDLK_s && shift && !ctrl && !alt)
            {
                OnScreenshot?.Invoke();
                return false;
            }

            if (ctrl && !shift && !alt)
            {
                switch (sym)
                {
                    case SDL_Keycode.SDLK_a: OnSelectAudioDevice?.Invoke(); return false;
                    case SDL_Keycode.SDLK_o: OnLoad?.Invoke(); return false;
                    case SDL_Keycode.SDLK_p: OnTogglePause?.Invoke(); return false;
                    case SDL_Keycode.SDLK_s: OnSave?.Invoke(); return false;
                    case SDL_Keycode.SDLK_r:
                    case SDL_Keycode.SDLK_F12: OnHardReset?.Invoke(); return false;
                    case SDL_Keycode.SDLK_q: OnToggleMute?.Invoke(); return false;
                    case SDL_Keycode.SDLK_w: return true;
                }
            }

            byte jmask = JoystickMaskFromKey(sym);
            if (jmask != 0)
                keyboardJoystick2 = (byte)(keyboardJoystick2 & ~jmask);

            UpdateKeyboardState(sym, true);
            return false;
        }

        /// <summary>Handles key up.</summary>
        /// <param name="ke">The SDL keyboard event to process.</param>
        private void HandleKeyUp(SDL_KeyboardEvent ke)
        {
            byte jmask = JoystickMaskFromKey(ke.keysym.sym);
            if (jmask != 0)
                keyboardJoystick2 = (byte)(keyboardJoystick2 | jmask);

            UpdateKeyboardState(ke.keysym.sym, false);
        }

        /// <summary>Updates keyboard state.</summary>
        /// <param name="sym">The SDL key code to update.</param>
        /// <param name="pressed">Whether the key or button is currently pressed.</param>
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
                    SetMatrixKey(7, 5, pressed); // COMMODORE (C=)
                    return;
                case SDL_Keycode.SDLK_RETURN:
                case SDL_Keycode.SDLK_KP_ENTER:
                    SetMatrixKey(0, 1, pressed);
                    return;
                case SDL_Keycode.SDLK_ESCAPE:
                    SetMatrixKey(7, 7, pressed); // RUN/STOP
                    return;
                case SDL_Keycode.SDLK_BACKSPACE:
                case SDL_Keycode.SDLK_DELETE:
                    SetMatrixKey(0, 0, pressed);
                    return;
                case SDL_Keycode.SDLK_INSERT:
                    SetMatrixKey(0, 0, pressed); // INST/DEL shares key
                    return;
                case SDL_Keycode.SDLK_HOME:
                    SetMatrixKey(6, 6, pressed); // CLR/HOME
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
                case SDL_Keycode.SDLK_MINUS: SetMatrixKey(5, 3, pressed); return;
                case SDL_Keycode.SDLK_EQUALS: SetMatrixKey(6, 5, pressed); return;
                case SDL_Keycode.SDLK_COMMA: SetMatrixKey(5, 7, pressed); return;
                case SDL_Keycode.SDLK_PERIOD: SetMatrixKey(5, 4, pressed); return;
                case SDL_Keycode.SDLK_SLASH: SetMatrixKey(6, 7, pressed); return;
                // UK punctuation mode: host ';:' key targets C64 ':' key.
                case SDL_Keycode.SDLK_SEMICOLON: SetMatrixKey(5, 5, pressed); return;
                // UK punctuation mode: host ''@' key targets C64 ';' key.
                case SDL_Keycode.SDLK_QUOTE: SetMatrixKey(6, 2, pressed); return;
                case SDL_Keycode.SDLK_LEFTBRACKET: SetMatrixKey(6, 0, pressed); return;
                case SDL_Keycode.SDLK_RIGHTBRACKET: SetMatrixKey(6, 3, pressed); return;
                case SDL_Keycode.SDLK_BACKSLASH: SetMatrixKey(5, 6, pressed); return;
            }
        }

        /// <summary>Sets matrix key.</summary>
        /// <param name="row">The matrix row or image row to process.</param>
        /// <param name="column">The matrix column to update.</param>
        /// <param name="pressed">Whether the key or button is currently pressed.</param>
        private void SetMatrixKey(int row, int column, bool pressed)
        {
            byte mask = (byte)(1 << column);
            if (pressed)
                keyboardMatrix[row] = (byte)(keyboardMatrix[row] & ~mask);
            else
                keyboardMatrix[row] = (byte)(keyboardMatrix[row] | mask);
        }

        /// <summary>Opens first available controller.</summary>
        private void OpenFirstAvailableController()
        {
            if (gameController != IntPtr.Zero)
                return;

            int count = SDL_NumJoysticks();
            for (int i = 0; i < count; i++)
            {
                if (SDL_IsGameController(i) == SDL_bool.SDL_FALSE)
                    continue;

                OpenController(i);
                if (gameController != IntPtr.Zero)
                    return;
            }
        }

        /// <summary>Opens controller.</summary>
        /// <param name="deviceIndex">The SDL controller device index.</param>
        private void OpenController(int deviceIndex)
        {
            CloseController();

            gameController = SDL_GameControllerOpen(deviceIndex);
            if (gameController == IntPtr.Zero)
            {
                gameControllerInstanceId = -1;
                return;
            }

            IntPtr joystick = SDL_GameControllerGetJoystick(gameController);
            gameControllerInstanceId = joystick == IntPtr.Zero ? -1 : SDL_JoystickInstanceID(joystick);
            controllerButtonMask = 0;
            controllerAxisMask = 0;
            UpdateControllerJoystick();
        }

        /// <summary>Closes controller.</summary>
        private void CloseController()
        {
            if (gameController != IntPtr.Zero)
            {
                SDL_GameControllerClose(gameController);
                gameController = IntPtr.Zero;
            }

            gameControllerInstanceId = -1;
            controllerButtonMask = 0;
            controllerAxisMask = 0;
            UpdateControllerJoystick();
        }

        /// <summary>Handles controller added.</summary>
        /// <param name="ev">The SDL event to process.</param>
        private void HandleControllerAdded(SDL_ControllerDeviceEvent ev)
        {
            if (gameController == IntPtr.Zero && SDL_IsGameController(ev.which) != SDL_bool.SDL_FALSE)
                OpenController(ev.which);
        }

        /// <summary>Handles controller removed.</summary>
        /// <param name="ev">The SDL event to process.</param>
        private void HandleControllerRemoved(SDL_ControllerDeviceEvent ev)
        {
            if (ev.which != gameControllerInstanceId)
                return;

            CloseController();
            OpenFirstAvailableController();
        }

        /// <summary>Handles controller button.</summary>
        /// <param name="ev">The SDL event to process.</param>
        private void HandleControllerButton(SDL_ControllerButtonEvent ev)
        {
            if (ev.which != gameControllerInstanceId)
                return;

            byte mask = ControllerButtonMask((SDL_GameControllerButton)ev.button);
            if (mask == 0)
                return;

            if (ev.state == SDL_PRESSED)
                controllerButtonMask = (byte)(controllerButtonMask | mask);
            else
                controllerButtonMask = (byte)(controllerButtonMask & ~mask);

            UpdateControllerJoystick();
        }

        /// <summary>Handles controller axis.</summary>
        /// <param name="ev">The SDL event to process.</param>
        private void HandleControllerAxis(SDL_ControllerAxisEvent ev)
        {
            if (ev.which != gameControllerInstanceId)
                return;

            SDL_GameControllerAxis axis = (SDL_GameControllerAxis)ev.axis;
            byte clearMask = axis switch
            {
                SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTX => 0x0C,
                SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTY => 0x03,
                _ => 0
            };

            if (clearMask == 0)
                return;

            byte nextMask = 0;
            if (axis == SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTX)
            {
                if (ev.axisValue <= -ControllerDeadZone)
                    nextMask = 0x04;
                else if (ev.axisValue >= ControllerDeadZone)
                    nextMask = 0x08;
            }
            else if (axis == SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTY)
            {
                if (ev.axisValue <= -ControllerDeadZone)
                    nextMask = 0x01;
                else if (ev.axisValue >= ControllerDeadZone)
                    nextMask = 0x02;
            }

            controllerAxisMask = (byte)((controllerAxisMask & ~clearMask) | nextMask);
            UpdateControllerJoystick();
        }

        /// <summary>Updates controller joystick.</summary>
        private void UpdateControllerJoystick()
        {
            controllerJoystick2 = (byte)~(controllerButtonMask | controllerAxisMask);
        }

        /// <summary>Maps a controller button to a joystick bit mask.</summary>
        private static byte ControllerButtonMask(SDL_GameControllerButton button) => button switch
        {
            SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_UP => 0x01,
            SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_DOWN => 0x02,
            SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_LEFT => 0x04,
            SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_RIGHT => 0x08,
            SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_A => 0x10,
            SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_B => 0x10,
            SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_X => 0x10,
            SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_Y => 0x10,
            SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_LEFTSHOULDER => 0x10,
            SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_RIGHTSHOULDER => 0x10,
            _ => 0
        };

        /// <summary>Maps a keyboard key to a joystick bit mask.</summary>
        private static byte JoystickMaskFromKey(SDL_Keycode k) => k switch
        {
            SDL_Keycode.SDLK_UP => 0x01,
            SDL_Keycode.SDLK_DOWN => 0x02,
            SDL_Keycode.SDLK_LEFT => 0x04,
            SDL_Keycode.SDLK_RIGHT => 0x08,
            SDL_Keycode.SDLK_RCTRL => 0x10, // fire
            SDL_Keycode.SDLK_LCTRL => 0x10, // fire
            _ => 0
        };
    }
}
