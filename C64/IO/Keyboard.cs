

using C64.CPU;
using System.Collections.Concurrent;
using static SDL2.SDL;

namespace C64.IO
{

    internal sealed class Keyboard : IDisposable
    {
        private readonly CPU_6510 cpu;

        private readonly ConcurrentQueue<byte> keyQueue = new();

        private readonly byte[] keyboardMatrix = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

        private volatile byte keyboardJoystick = 0xFF;
        private volatile byte controllerJoystick = 0xFF;
        private volatile int activeJoystickPort;
        private IntPtr gameController;
        private int gameControllerInstanceId = -1;
        private byte controllerButtonMask;
        private byte controllerAxisMask;

        private bool shiftLockActive;
        private const short ControllerDeadZone = 12000;

        private readonly Dictionary<SDL_Scancode, TextInputBinding> textInputHeld = [];

        private enum ShiftPolicy
        {

            Passthrough,

            ForceShift,

            ForceUnshift,
        }

        private readonly struct TextInputBinding
        {
            public readonly int Row;
            public readonly int Column;
            public readonly ShiftPolicy Shift;
            public TextInputBinding(int row, int col, ShiftPolicy shift) { Row = row; Column = col; Shift = shift; }
        }

        private int syntheticShiftCount;

        private int syntheticUnshiftCount;

        private bool physicalLShift;

        private bool physicalRShift;

        private SDL_Scancode pendingTextInputScancode = SDL_Scancode.SDL_SCANCODE_UNKNOWN;

        public Action? OnHardReset { get; set; }

        public Action? OnNativeLoad { get; set; }

        public Action? OnSave { get; set; }

        public Action? OnRestoreNmi { get; set; }

        public Action? OnScreenshot { get; set; }

        public Action? OnViewportScreenshot { get; set; }

        public Action? OnToggleFullscreenViewport { get; set; }

        public Action? OnToggleMute { get; set; }

        public Action? OnTogglePause { get; set; }

        public Action? OnToggleJoystickPort { get; set; }

        public Action? OnToggleTurbo { get; set; }

        public Action? OnSelectAudioDevice { get; set; }

        /// <summary>Creates the C64 keyboard matrix and joystick input state connected to the 6510.</summary>
        /// <param name="cpu">The CPU instance connected to this component.</param>
        public Keyboard(CPU_6510 cpu)
        {
            this.cpu = cpu;
        }

        public int ActiveJoystickPort => activeJoystickPort;

        public byte Joystick1 => (byte)(KeyboardJoystickForPort(1) & controllerJoystick);

        public byte Joystick2 => (byte)(KeyboardJoystickForPort(2) & controllerJoystick);

        /// <summary>Gets the active-low keyboard joystick byte for the requested port.</summary>
        /// <param name="port">The C64 joystick port number to query.</param>
        private byte KeyboardJoystickForPort(int port) => activeJoystickPort == port ? keyboardJoystick : (byte)0xFF;

        /// <summary>Discovers the first SDL game controller and begins joystick event processing.</summary>
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
        /// Drains the PETSCII key queue into the C64 keyboard buffer ($0277-$0280 / $C6).
        /// Call once per CIA tick from the IRQ thread.
        ///
        /// Injects only a single byte per call, and only when the buffer is
        /// empty, so the BASIC screen editor processes each keystroke
        /// (including RETURN's line-tokenise / quote-mode bookkeeping) before
        /// the next one arrives. This matches the cadence of a real user
        /// typing and prevents characters being lost when long listings are
        /// pasted from the host clipboard.
        /// </summary>
        public void DrainQueue()
        {
            if (keyQueue.IsEmpty) return;

            byte count = cpu.memory.ReadByte(0x00C6);
            if (count != 0) return;

            if (!keyQueue.TryDequeue(out byte pet)) return;

            cpu.memory.WriteByte(0x0277, pet);
            cpu.memory.WriteByte(0x00C6, 1);
        }

        /// <summary>
        /// Resets the keyboard matrix, joystick state, joystick mapping mode,
        /// and queued key input. Call from <c>C64Emulator.HardReset</c> /
        /// <c>InitHardware</c>.
        /// </summary>
        public void Reset()
        {
            keyboardJoystick = 0xFF;
            controllerJoystick = 0xFF;
            activeJoystickPort = 0;
            controllerButtonMask = 0;
            controllerAxisMask = 0;
            for (int i = 0; i < keyboardMatrix.Length; i++)
                keyboardMatrix[i] = 0xFF;
            while (keyQueue.TryDequeue(out _)) { }
            shiftLockActive = false;
            textInputHeld.Clear();
            syntheticShiftCount = 0;
            syntheticUnshiftCount = 0;
            physicalLShift = false;
            physicalRShift = false;
            pendingTextInputScancode = SDL_Scancode.SDL_SCANCODE_UNKNOWN;
        }

        /// <summary>Enqueues a raw PETSCII byte for typed-text injection (e.g. from file load).</summary>
        public void EnqueuePetscii(byte petscii) => keyQueue.Enqueue(petscii);

        /// <summary>
        /// Reads text from the host clipboard and enqueues it as PETSCII so the
        /// running C64 program (typically the BASIC READY prompt) receives it
        /// exactly as if the user had typed it. Each line is terminated with
        /// RETURN ($0D) so multi-line BASIC listings auto-enter.
        /// </summary>
        public bool PasteClipboardText()
        {
            if (SDL_HasClipboardText() == SDL_bool.SDL_FALSE)
                return false;

            string? text = SDL_GetClipboardText();
            if (string.IsNullOrEmpty(text))
                return false;

            text = text.Replace("\r\n", "\n").Replace('\r', '\n');

            bool abbreviationMode = DetectAbbreviationMode(text);

            bool any = false;
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                foreach (var tok in VicePetsciiTokenParser.Parse(line))
                {
                    if (tok.IsPetscii)
                    {
                        keyQueue.Enqueue(tok.Byte);
                        any = true;
                    }
                    else
                    {
                        byte pet = AsciiCharToPetscii(tok.Char, abbreviationMode);
                        if (pet != 0)
                        {
                            keyQueue.Enqueue(pet);
                            any = true;
                        }
                    }
                }

                if (i < lines.Length - 1 || line.Length > 0)
                {
                    keyQueue.Enqueue(0x0D);
                    any = true;
                }
            }

            return any;
        }

        /// <summary>
        /// Returns true when the clipboard text contains BOTH lowercase and
        /// uppercase ASCII letters outside of <c>{...}</c> token spans, which
        /// indicates a C64 BASIC listing using the lowercase=unshifted /
        /// UPPERCASE=SHIFTED abbreviation convention.
        /// </summary>
        private static bool DetectAbbreviationMode(string text)
        {
            bool hasLower = false;
            bool hasUpper = false;
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '{') { depth++; continue; }
                if (ch == '}') { if (depth > 0) depth--; continue; }
                if (depth > 0) continue;

                if (ch >= 'a' && ch <= 'z') hasLower = true;
                else if (ch >= 'A' && ch <= 'Z') hasUpper = true;
                if (hasLower && hasUpper) return true;
            }
            return false;
        }

        /// <summary>
        /// Converts a host ASCII character to a PETSCII byte suitable for the
        /// keyboard buffer.
        /// </summary>
        /// <param name="ch">The character to convert.</param>
        /// <param name="abbreviationMode">
        /// When true, UPPERCASE letters are emitted as SHIFTED PETSCII bytes
        /// ($C1-$DA) so BASIC keyword abbreviations such as <c>tA</c> -> TAB(
        /// tokenize correctly; lowercase letters become unshifted PETSCII
        /// ($41-$5A). When false (default), letters map case-insensitively to
        /// $41-$5A, matching the behavior used for plain listings.
        /// </param>
        private static byte AsciiCharToPetscii(char ch, bool abbreviationMode = false)
        {
            if (abbreviationMode)
            {
                if (ch >= 'a' && ch <= 'z') return (byte)('A' + (ch - 'a'));
                if (ch >= 'A' && ch <= 'Z') return (byte)(0xC1 + (ch - 'A'));
            }
            else
            {
                if (ch >= 'a' && ch <= 'z') return (byte)('A' + (ch - 'a'));
            }
            if (ch >= ' ' && ch <= '~') return (byte)ch;
            return 0;
        }

        /// <summary>Toggles keyboard joystick input between C64 port 1, port 2, and keyboard-only mode.</summary>
        public int ToggleJoystickPort()
        {
            int previousPort = activeJoystickPort;
            activeJoystickPort = activeJoystickPort switch
            {
                2 => 1,
                1 => 0,
                _ => 2
            };
            keyboardJoystick = 0xFF;
            if (previousPort == 0 && activeJoystickPort != 0)
            {
                SetMatrixKey(7, 2, false);
                SetMatrixKey(0, 2, false);
                SetMatrixKey(0, 7, false);
                physicalLShift = false;
                physicalRShift = false;
                syntheticShiftCount = 0;
                syntheticUnshiftCount = 0;
                shiftLockActive = false;
                RecomputeShiftCells();
            }
            return activeJoystickPort;
        }

        /// <summary>
        /// Handles a single SDL event.
        /// Returns <c>true</c> if the emulator should quit.
        /// </summary>
        /// <param name="ev">The SDL event to process.</param>
        public bool HandleSdlEvent(SDL_Event ev)
        {
            switch (ev.type)
            {
                case SDL_EventType.SDL_KEYDOWN:
                    return HandleKeyDown(ev.key);

                case SDL_EventType.SDL_KEYUP:
                    HandleKeyUp(ev.key);
                    return false;

                case SDL_EventType.SDL_TEXTINPUT:
                    HandleTextInput(ev.text);
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

        /// <summary>Stops SDL text input and closes the active game controller handle.</summary>
        public void Dispose()
        {
            CloseController();
        }

        /// <summary>Maps an SDL key press to the C64 matrix, text-input latch, or emulator shortcut.</summary>
        /// <param name="ke">The SDL keyboard event to process.</param>
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

            if (sym == SDL_Keycode.SDLK_CAPSLOCK)
            {
                shiftLockActive = !shiftLockActive;
                RecomputeShiftCells();
                return false;
            }

            if (sym == SDL_Keycode.SDLK_PAGEUP || sym == SDL_Keycode.SDLK_PAUSE)
            {
                OnRestoreNmi?.Invoke();
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

            if (sym == SDL_Keycode.SDLK_s && ctrl && shift && !alt)
            {
                OnViewportScreenshot?.Invoke();
                return false;
            }

            if (ctrl && !shift && !alt)
            {
                switch (sym)
                {
                    case SDL_Keycode.SDLK_a: OnSelectAudioDevice?.Invoke(); return false;
                    case SDL_Keycode.SDLK_f: OnToggleFullscreenViewport?.Invoke(); return false;
                    case SDL_Keycode.SDLK_j: OnToggleJoystickPort?.Invoke(); return false;
                    case SDL_Keycode.SDLK_l: OnNativeLoad?.Invoke(); return false;
                    case SDL_Keycode.SDLK_p: OnTogglePause?.Invoke(); return false;
                    case SDL_Keycode.SDLK_s: OnSave?.Invoke(); return false;
                    case SDL_Keycode.SDLK_t: OnToggleTurbo?.Invoke(); return false;
                    case SDL_Keycode.SDLK_v: PasteClipboardText(); return false;
                    case SDL_Keycode.SDLK_r:
                    case SDL_Keycode.SDLK_F12: OnHardReset?.Invoke(); return false;
                    case SDL_Keycode.SDLK_q: OnToggleMute?.Invoke(); return false;
                    case SDL_Keycode.SDLK_w: return true;
                }
            }

            if (shift && !ctrl && !alt && sym == SDL_Keycode.SDLK_INSERT)
            {
                PasteClipboardText();
                return false;
            }

            byte jmask = JoystickMaskFromKey(sym);
            if (jmask != 0 && activeJoystickPort != 0)
                keyboardJoystick = (byte)(keyboardJoystick & ~jmask);

            pendingTextInputScancode = ke.keysym.scancode;
            UpdateKeyboardState(sym, true, shift);
            return false;
        }

        /// <summary>Releases the C64 matrix and text-input bindings associated with an SDL key.</summary>
        /// <param name="ke">The SDL keyboard event to process.</param>
        private void HandleKeyUp(SDL_KeyboardEvent ke)
        {
            byte jmask = JoystickMaskFromKey(ke.keysym.sym);
            if (jmask != 0 && activeJoystickPort != 0)
                keyboardJoystick = (byte)(keyboardJoystick | jmask);

            ReleaseTextInputBinding(ke.keysym.scancode);
            UpdateKeyboardState(ke.keysym.sym, false, (ke.keysym.mod & SDL_Keymod.KMOD_SHIFT) != 0);
        }

        /// <summary>Applies a host key transition to its C64 matrix position with synthetic shift handling.</summary>
        /// <param name="sym">The SDL key code to update.</param>
        /// <param name="pressed">Whether the key or button is currently pressed.</param>
        /// <param name="shiftHeld">Whether either host SHIFT key is currently held.</param>
        private void UpdateKeyboardState(SDL_Keycode sym, bool pressed, bool shiftHeld)
        {
            switch (sym)
            {
                case SDL_Keycode.SDLK_LSHIFT:
                    physicalLShift = pressed;
                    RecomputeShiftCells();
                    return;
                case SDL_Keycode.SDLK_RSHIFT:
                    physicalRShift = pressed;
                    RecomputeShiftCells();
                    return;

                case SDL_Keycode.SDLK_LCTRL:
                    SetMatrixKey(7, 2, pressed);
                    return;

                case SDL_Keycode.SDLK_RCTRL:
                    return;
                case SDL_Keycode.SDLK_RALT:
                    SetMatrixKey(7, 5, pressed);
                    return;

                case SDL_Keycode.SDLK_RETURN:
                case SDL_Keycode.SDLK_KP_ENTER:
                    SetMatrixKey(0, 1, pressed);
                    return;

                case SDL_Keycode.SDLK_ESCAPE:
                    SetMatrixKey(7, 7, pressed);
                    return;

                case SDL_Keycode.SDLK_BACKSPACE:
                case SDL_Keycode.SDLK_DELETE:
                    SetMatrixKey(0, 0, pressed);
                    return;

                case SDL_Keycode.SDLK_INSERT:
                    SetMatrixKey(0, 0, pressed);
                    return;

                case SDL_Keycode.SDLK_HOME:
                    SetMatrixKey(6, 6, pressed);
                    return;

                case SDL_Keycode.SDLK_SPACE:
                    if (activeJoystickPort == 0)
                        SetMatrixKey(7, 4, pressed);
                    return;

                case SDL_Keycode.SDLK_F1:
                    SetMatrixKey(0, 4, pressed);
                    return;

                case SDL_Keycode.SDLK_F2:
                    AdjustSyntheticShift(pressed, force: true);
                    SetMatrixKey(0, 4, pressed);
                    return;

                case SDL_Keycode.SDLK_F3:
                    SetMatrixKey(0, 5, pressed);
                    return;

                case SDL_Keycode.SDLK_F4:
                    AdjustSyntheticShift(pressed, force: true);
                    SetMatrixKey(0, 5, pressed);
                    return;

                case SDL_Keycode.SDLK_F5:
                    SetMatrixKey(0, 6, pressed);
                    return;

                case SDL_Keycode.SDLK_F6:
                    AdjustSyntheticShift(pressed, force: true);
                    SetMatrixKey(0, 6, pressed);
                    return;

                case SDL_Keycode.SDLK_F7:
                    SetMatrixKey(0, 3, pressed);
                    return;

                case SDL_Keycode.SDLK_F8:
                    AdjustSyntheticShift(pressed, force: true);
                    SetMatrixKey(0, 3, pressed);
                    return;

                case SDL_Keycode.SDLK_LEFT:
                    if (activeJoystickPort == 0)
                    {
                        AdjustSyntheticShift(pressed, force: true);
                        SetMatrixKey(0, 2, pressed);
                    }
                    return;

                case SDL_Keycode.SDLK_RIGHT:
                    if (activeJoystickPort == 0)
                        SetMatrixKey(0, 2, pressed);
                    return;

                case SDL_Keycode.SDLK_UP:
                    if (activeJoystickPort == 0)
                    {
                        AdjustSyntheticShift(pressed, force: true);
                        SetMatrixKey(0, 7, pressed);
                    }
                    return;

                case SDL_Keycode.SDLK_DOWN:
                    if (activeJoystickPort == 0)
                        SetMatrixKey(0, 7, pressed);
                    return;
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

            if (sym >= SDL_Keycode.SDLK_0 && sym <= SDL_Keycode.SDLK_9 && !shiftHeld)
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
                case SDL_Keycode.SDLK_EQUALS:
                case SDL_Keycode.SDLK_COMMA:
                case SDL_Keycode.SDLK_PERIOD:
                case SDL_Keycode.SDLK_SLASH:
                case SDL_Keycode.SDLK_SEMICOLON:
                case SDL_Keycode.SDLK_QUOTE:
                case SDL_Keycode.SDLK_LEFTBRACKET:
                case SDL_Keycode.SDLK_RIGHTBRACKET:
                case SDL_Keycode.SDLK_BACKSLASH:
                case SDL_Keycode.SDLK_BACKQUOTE:
                case SDL_Keycode.SDLK_KP_MULTIPLY:
                case SDL_Keycode.SDLK_KP_PLUS:
                case SDL_Keycode.SDLK_KP_MINUS:
                case SDL_Keycode.SDLK_KP_DIVIDE:
                case SDL_Keycode.SDLK_KP_PERIOD:
                    return;
            }
        }

        /// <summary>Sets or clears one active-low C64 keyboard-matrix row and column position.</summary>
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

        /// <summary>
        /// Recomputes the two C64 SHIFT cells (LSHIFT at row 1 col 7 and RSHIFT
        /// at row 6 col 4) from the combined physical shift, shift-lock and
        /// synthetic shift obligations driven by text-input / cursor / function
        /// keys.
        /// </summary>
        private void RecomputeShiftCells()
        {
            bool shift = physicalLShift || physicalRShift || shiftLockActive || syntheticShiftCount > 0;
            if (shift && syntheticUnshiftCount > 0 && syntheticShiftCount == 0 && !shiftLockActive)
                shift = false;
            SetMatrixKey(1, 7, shift);
            SetMatrixKey(6, 4, shift);
        }

        /// <summary>Increments or decrements a synthetic shift / unshift obligation counter.</summary>
        /// <param name="pressed">True to add the obligation, false to remove it.</param>
        /// <param name="force">True to force SHIFT asserted, false to force SHIFT released.</param>
        private void AdjustSyntheticShift(bool pressed, bool force)
        {
            ref int counter = ref (force ? ref syntheticShiftCount : ref syntheticUnshiftCount);
            if (pressed) counter++;
            else if (counter > 0) counter--;
            RecomputeShiftCells();
        }

        /// <summary>
        /// Handles SDL_TEXTINPUT events. Maps the printable character produced
        /// by the host keyboard layout to a C64 matrix cell (plus a synthetic
        /// SHIFT obligation if needed) so the C64 sees the same symbol the user
        /// typed, regardless of the host layout. Letters, unshifted digits and
        /// cursor / function keys are intentionally not handled here; they are
        /// driven from the position-based path in <see cref="UpdateKeyboardState"/>.
        /// </summary>
        private void HandleTextInput(SDL_TextInputEvent ev)
        {
            string text = ReadTextInputString(ev);
            if (string.IsNullOrEmpty(text))
                return;

            char ch = text[0];

            if (ch >= 'a' && ch <= 'z') return;
            if (ch >= 'A' && ch <= 'Z') return;
            if (ch >= '0' && ch <= '9') return;
            if (ch == ' ') return;

            if (!TryMapCharacterToMatrix(ch, out int row, out int col, out ShiftPolicy shift))
                return;

            SDL_Scancode scancode = pendingTextInputScancode;

            if (scancode == SDL_Scancode.SDL_SCANCODE_UNKNOWN)
                scancode = (SDL_Scancode)(0x10000 + ch);

            if (textInputHeld.TryGetValue(scancode, out TextInputBinding prev))
                ReleaseBinding(prev);

            SetMatrixKey(row, col, true);
            if (shift == ShiftPolicy.ForceShift)
                AdjustSyntheticShift(true, force: true);
            else if (shift == ShiftPolicy.ForceUnshift)
                AdjustSyntheticShift(true, force: false);

            textInputHeld[scancode] = new TextInputBinding(row, col, shift);
            pendingTextInputScancode = SDL_Scancode.SDL_SCANCODE_UNKNOWN;
        }

        /// <summary>Releases any text-input matrix binding currently latched for the given scancode.</summary>
        private void ReleaseTextInputBinding(SDL_Scancode scancode)
        {
            if (!textInputHeld.TryGetValue(scancode, out TextInputBinding binding))
                return;
            textInputHeld.Remove(scancode);
            ReleaseBinding(binding);
        }

        /// <summary>Releases a single latched text-input binding.</summary>
        private void ReleaseBinding(TextInputBinding binding)
        {
            SetMatrixKey(binding.Row, binding.Column, false);
            if (binding.Shift == ShiftPolicy.ForceShift)
                AdjustSyntheticShift(false, force: true);
            else if (binding.Shift == ShiftPolicy.ForceUnshift)
                AdjustSyntheticShift(false, force: false);
        }

        /// <summary>Reads the UTF-8 text payload from an SDL_TextInputEvent.</summary>
        private static unsafe string ReadTextInputString(SDL_TextInputEvent ev)
        {
            byte* p = ev.text;
            int len = 0;
            while (len < 32 && p[len] != 0) len++;
            if (len == 0) return string.Empty;
            return System.Text.Encoding.UTF8.GetString(p, len);
        }

        /// <summary>
        /// Maps a printable character produced by the host keyboard layout to a
        /// C64 keyboard-matrix cell and a SHIFT policy that overrides the host
        /// SHIFT state when needed. Mirrors VICE's symbolic mapping for the
        /// subset of ASCII / Latin-1 characters that exist on a real C64.
        /// </summary>
        private static bool TryMapCharacterToMatrix(char ch, out int row, out int col, out ShiftPolicy shift)
        {
            row = col = 0;
            shift = ShiftPolicy.Passthrough;
            switch (ch)
            {
                case '!': row = 7; col = 0; shift = ShiftPolicy.ForceShift; return true;
                case '"': row = 7; col = 3; shift = ShiftPolicy.ForceShift; return true;
                case '#': row = 1; col = 0; shift = ShiftPolicy.ForceShift; return true;
                case '$': row = 1; col = 3; shift = ShiftPolicy.ForceShift; return true;
                case '%': row = 2; col = 0; shift = ShiftPolicy.ForceShift; return true;
                case '&': row = 2; col = 3; shift = ShiftPolicy.ForceShift; return true;
                case '\'': row = 3; col = 0; shift = ShiftPolicy.ForceShift; return true;
                case '(': row = 3; col = 3; shift = ShiftPolicy.ForceShift; return true;
                case ')': row = 4; col = 0; shift = ShiftPolicy.ForceShift; return true;
                case '*': row = 6; col = 1; shift = ShiftPolicy.ForceUnshift; return true;
                case '+': row = 5; col = 0; shift = ShiftPolicy.ForceUnshift; return true;
                case ',': row = 5; col = 7; shift = ShiftPolicy.ForceUnshift; return true;
                case '-': row = 5; col = 3; shift = ShiftPolicy.ForceUnshift; return true;
                case '.': row = 5; col = 4; shift = ShiftPolicy.ForceUnshift; return true;
                case '/': row = 6; col = 7; shift = ShiftPolicy.ForceUnshift; return true;
                case ':': row = 5; col = 5; shift = ShiftPolicy.ForceUnshift; return true;
                case ';': row = 6; col = 2; shift = ShiftPolicy.ForceUnshift; return true;
                case '<': row = 5; col = 7; shift = ShiftPolicy.ForceShift; return true;
                case '=': row = 6; col = 5; shift = ShiftPolicy.ForceUnshift; return true;
                case '>': row = 5; col = 4; shift = ShiftPolicy.ForceShift; return true;
                case '?': row = 6; col = 7; shift = ShiftPolicy.ForceShift; return true;
                case '@': row = 5; col = 6; shift = ShiftPolicy.ForceUnshift; return true;
                case '[': row = 5; col = 5; shift = ShiftPolicy.ForceShift; return true;
                case ']': row = 6; col = 2; shift = ShiftPolicy.ForceShift; return true;
                case '\u00A3': row = 6; col = 0; shift = ShiftPolicy.ForceUnshift; return true;
                case '^': row = 6; col = 6; shift = ShiftPolicy.ForceUnshift; return true;
                case '_': row = 7; col = 1; shift = ShiftPolicy.ForceUnshift; return true;
                default: return false;
            }
        }

        /// <summary>Finds and opens the first SDL device recognized as a game controller.</summary>
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

        /// <summary>Opens an SDL game controller and records its joystick identity.</summary>
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

        /// <summary>Closes the active SDL game controller and clears its joystick state.</summary>
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

        /// <summary>Opens a newly connected SDL controller when no controller is active.</summary>
        /// <param name="ev">The SDL event to process.</param>
        private void HandleControllerAdded(SDL_ControllerDeviceEvent ev)
        {
            if (gameController == IntPtr.Zero && SDL_IsGameController(ev.which) != SDL_bool.SDL_FALSE)
                OpenController(ev.which);
        }

        /// <summary>Closes the active SDL controller when its joystick instance is disconnected.</summary>
        /// <param name="ev">The SDL event to process.</param>
        private void HandleControllerRemoved(SDL_ControllerDeviceEvent ev)
        {
            if (ev.which != gameControllerInstanceId)
                return;

            CloseController();
            OpenFirstAvailableController();
        }

        /// <summary>Maps an SDL controller button transition to C64 joystick fire or direction state.</summary>
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

        /// <summary>Converts an SDL analog-axis event into thresholded C64 joystick directions.</summary>
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

        /// <summary>Combines controller axes and buttons into the active-low C64 joystick byte.</summary>
        private void UpdateControllerJoystick()
        {
            controllerJoystick = (byte)~(controllerButtonMask | controllerAxisMask);
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
            SDL_Keycode.SDLK_SPACE => 0x10,
            SDL_Keycode.SDLK_RCTRL => 0x10,
            _ => 0
        };
    }
}
