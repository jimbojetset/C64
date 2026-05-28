// ============================================================================
// Project:     C64
// File:        Keyboard.cs
// Description: SDL keyboard and controller input handler for the C64 keyboard
//              matrix, joystick port, hotkeys, and queued key injection.
// Author:      James Booth
// Created:     2025
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Commodore 64 and related ROMs are property of their respective
//              rights holders. This emulator is for educational purposes only.
// ============================================================================

using C64.CPU;
using C64.IO;
using System.Collections.Concurrent;
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

        /// C64 keyboard matrix: 8 rows, each column bit is active-low.
        private readonly byte[] keyboardMatrix = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

        private volatile byte keyboardJoystick = 0xFF;
        private volatile byte controllerJoystick = 0xFF;
        private volatile int activeJoystickPort;
        private IntPtr gameController;
        private int gameControllerInstanceId = -1;
        private byte controllerButtonMask;
        private byte controllerAxisMask;

        private bool shiftLockActive;
        private const short ControllerDeadZone = 12000;

        /// <summary>
        /// Tracks matrix cells (and synthetic SHIFT obligation) latched by an
        /// <c>SDL_TEXTINPUT</c> event keyed by the SDL scancode of the physical
        /// key that produced the character, so the matching <c>SDL_KEYUP</c> can
        /// release exactly what was pressed even if the host modifier state has
        /// since changed.
        /// </summary>
        private readonly Dictionary<SDL_Scancode, TextInputBinding> textInputHeld = new();

        /// <summary>SHIFT policy applied by a symbolic key mapping.</summary>
        private enum ShiftPolicy
        {
            /// <summary>Pass the physical host SHIFT through unchanged.</summary>
            Passthrough,
            /// <summary>Force the C64 SHIFT bits to be asserted while this key is held.</summary>
            ForceShift,
            /// <summary>Force the C64 SHIFT bits to be released while this key is held.</summary>
            ForceUnshift,
        }

        /// <summary>Latched binding from an <c>SDL_TEXTINPUT</c> event.</summary>
        private readonly struct TextInputBinding
        {
            public readonly int Row;
            public readonly int Column;
            public readonly ShiftPolicy Shift;
            public TextInputBinding(int row, int col, ShiftPolicy shift) { Row = row; Column = col; Shift = shift; }
        }

        /// <summary>Tracks how many text-input pressed keys want SHIFT asserted.</summary>
        private int syntheticShiftCount;

        /// <summary>Tracks how many text-input pressed keys want SHIFT released.</summary>
        private int syntheticUnshiftCount;

        /// <summary>Tracks the host LSHIFT physical key state.</summary>
        private bool physicalLShift;

        /// <summary>Tracks the host RSHIFT physical key state.</summary>
        private bool physicalRShift;

        /// <summary>
        /// Scancode of the most recent non-repeat KEYDOWN that fell through to
        /// the symbolic / matrix routing. SDL fires <c>SDL_TEXTINPUT</c> for the
        /// same physical key immediately after its KEYDOWN, so the text-input
        /// handler can use this value to key its binding by the originating
        /// physical key (not by the produced character, whose scancode would
        /// belong to a different key on the host layout).
        /// </summary>
        private SDL_Scancode pendingTextInputScancode = SDL_Scancode.SDL_SCANCODE_UNKNOWN;

        /// ?? Callbacks wired by C64Emulator after construction ?????????????????

        /// <summary>Invoked when F12 / Ctrl+R is pressed.</summary>
        public Action? OnHardReset { get; set; }

        /// <summary>Invoked when Ctrl+L is pressed.</summary>
        public Action? OnNativeLoad { get; set; }

        /// <summary>Invoked when Ctrl+S is pressed.</summary>
        public Action? OnSave { get; set; }

        /// <summary>Invoked when RESTORE key equivalent is pressed.</summary>
        public Action? OnRestoreNmi { get; set; }

        /// <summary>Invoked when Shift+S is pressed (screenshot).</summary>
        public Action? OnScreenshot { get; set; }

        /// <summary>Invoked when Ctrl+Shift+S is pressed (raw viewport screenshot).</summary>
        public Action? OnViewportScreenshot { get; set; }

        /// <summary>Invoked when Ctrl+F is pressed (fullscreen viewport toggle).</summary>
        public Action? OnToggleFullscreenViewport { get; set; }

        /// <summary>Invoked when Ctrl+Q is pressed.</summary>
        public Action? OnToggleMute { get; set; }

        /// <summary>Invoked when Ctrl+P is pressed.</summary>
        public Action? OnTogglePause { get; set; }

        /// <summary>Invoked when Ctrl+J is pressed.</summary>
        public Action? OnToggleJoystickPort { get; set; }

        /// <summary>Invoked when Ctrl+T is pressed.</summary>
        public Action? OnToggleTurbo { get; set; }

        /// <summary>Invoked when Ctrl+A is pressed.</summary>
        public Action? OnSelectAudioDevice { get; set; }

        /// <summary>Initializes a new Keyboard instance.</summary>
        /// <param name="cpu">The CPU instance connected to this component.</param>
        public Keyboard(CPU_6510 cpu)
        {
            this.cpu = cpu;
        }

        /// ?? Public API ????????????????????????????????????????????????????????

        /// <summary>Gets the selected joystick port number used by keyboard joystick mapping, or 0 when keyboard mapping is disabled.</summary>
        public int ActiveJoystickPort => activeJoystickPort;

        /// <summary>CIA-1 port B ($DC01) joystick port 1 byte (active-low).</summary>
        public byte Joystick1 => (byte)(KeyboardJoystickForPort(1) & controllerJoystick);

        /// <summary>CIA-1 port A ($DC00) joystick port 2 byte (active-low).</summary>
        public byte Joystick2 => (byte)(KeyboardJoystickForPort(2) & controllerJoystick);

        /// <summary>Gets the active-low keyboard joystick byte for the requested port.</summary>
        /// <param name="port">The C64 joystick port number to query.</param>
        /// <returns>The keyboard joystick byte for that port, or neutral when keyboard mapping is disabled or routed elsewhere.</returns>
        private byte KeyboardJoystickForPort(int port) => activeJoystickPort == port ? keyboardJoystick : (byte)0xFF;

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
        /// <returns>True if any characters were enqueued; otherwise false.</returns>
        public bool PasteClipboardText()
        {
            if (SDL_HasClipboardText() == SDL_bool.SDL_FALSE)
                return false;

            string? text = SDL_GetClipboardText();
            if (string.IsNullOrEmpty(text))
                return false;

            /// Normalize line endings so CRLF / CR / LF all become single RETURNs.
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');

            /// Heuristic: if the clipboard mixes lowercase and uppercase letters,
            /// assume the listing follows the C64 abbreviation convention where
            /// UPPERCASE means a SHIFTED keystroke (PETSCII $C1-$DA, used for
            /// keyword abbreviations like tA -> TAB(, rN -> RND, gO -> GOTO).
            /// Otherwise fall back to the case-insensitive mapping used for
            /// ordinary listings (everything -> $41-$5A).
            bool abbreviationMode = DetectAbbreviationMode(text);

            bool any = false;
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                /// Expand VICE-style {tokens} (e.g. {home}, {right*39}, {5 space},
                /// {shift-a}, {$93}, {147}) before falling back to the literal
                /// ASCII-to-PETSCII path used for ordinary characters.
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

                /// Terminate every line except a trailing empty one with RETURN.
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
        /// <returns>The newly selected keyboard joystick port number, or 0 when keyboard mapping is disabled.</returns>
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

        /// <summary>Releases resources owned by this instance.</summary>
        public void Dispose()
        {
            CloseController();
        }

        /// ?? Private implementation ????????????????????????????????????????????

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

            /// C64 SHIFT LOCK on a modern keyboard.
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

            /// Symbol/punctuation/shifted-digit keys are routed via SDL_TEXTINPUT
            /// (see HandleTextInput) so the C64 matrix entry matches the printable
            /// character the host layout actually produced.

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

            /// Shift+Insert is a common host-side "paste" shortcut.
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

        /// <summary>Handles key up.</summary>
        /// <param name="ke">The SDL keyboard event to process.</param>
        private void HandleKeyUp(SDL_KeyboardEvent ke)
        {
            byte jmask = JoystickMaskFromKey(ke.keysym.sym);
            if (jmask != 0 && activeJoystickPort != 0)
                keyboardJoystick = (byte)(keyboardJoystick | jmask);

            ReleaseTextInputBinding(ke.keysym.scancode);
            UpdateKeyboardState(ke.keysym.sym, false, (ke.keysym.mod & SDL_Keymod.KMOD_SHIFT) != 0);
        }

        /// <summary>Updates keyboard state.</summary>
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
                    SetMatrixKey(7, 2, pressed); /// C64 CTRL key
                    return;

                case SDL_Keycode.SDLK_RCTRL:
                    return; /// joystick-only to avoid game keyboard side-effects
                case SDL_Keycode.SDLK_RALT:
                    SetMatrixKey(7, 5, pressed); /// COMMODORE (C=)
                    return;

                case SDL_Keycode.SDLK_RETURN:
                case SDL_Keycode.SDLK_KP_ENTER:
                    SetMatrixKey(0, 1, pressed);
                    return;

                case SDL_Keycode.SDLK_ESCAPE:
                    SetMatrixKey(7, 7, pressed); /// RUN/STOP
                    return;

                case SDL_Keycode.SDLK_BACKSPACE:
                case SDL_Keycode.SDLK_DELETE:
                    SetMatrixKey(0, 0, pressed);
                    return;

                case SDL_Keycode.SDLK_INSERT:
                    SetMatrixKey(0, 0, pressed); /// INST/DEL shares key
                    return;

                case SDL_Keycode.SDLK_HOME:
                    SetMatrixKey(6, 6, pressed); /// CLR/HOME
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
                /// Punctuation, symbol and shifted-digit keys are routed via
                /// SDL_TEXTINPUT (see HandleTextInput) so the C64 matrix entry
                /// matches the printable character the host layout produced.
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

            /// Letters and plain digits are handled by the position-based path
            /// (UpdateKeyboardState) so the host SHIFT state controls C64 case.
            if (ch >= 'a' && ch <= 'z') return;
            if (ch >= 'A' && ch <= 'Z') return;
            if (ch >= '0' && ch <= '9') return;
            if (ch == ' ') return;

            if (!TryMapCharacterToMatrix(ch, out int row, out int col, out ShiftPolicy shift))
                return;

            SDL_Scancode scancode = pendingTextInputScancode;

            /// If we don't have a recent KEYDOWN to attribute the text input to
            /// (e.g. composed input, IME) fall back to keying the binding by the
            /// character itself in the high range so it can't collide with real
            /// SDL_Scancode values.
            if (scancode == SDL_Scancode.SDL_SCANCODE_UNKNOWN)
                scancode = (SDL_Scancode)(0x10000 + ch);

            /// Replace any binding currently held for the same physical key.
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
                case '\u00A3': row = 6; col = 0; shift = ShiftPolicy.ForceUnshift; return true; /// '£' (UK pound)
                case '^': row = 6; col = 6; shift = ShiftPolicy.ForceUnshift; return true; /// C64 up-arrow
                case '_': row = 7; col = 1; shift = ShiftPolicy.ForceUnshift; return true; /// C64 left-arrow
                default: return false;
            }
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
            SDL_Keycode.SDLK_SPACE => 0x10, /// fire
            SDL_Keycode.SDLK_RCTRL => 0x10, /// fire
            _ => 0
        };
    }
}
