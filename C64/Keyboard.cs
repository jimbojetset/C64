using System.Collections.Concurrent;
using _6502CPU;
using static SDL2.SDL;

namespace C64
{
    /// <summary>
    /// Standalone keyboard and joystick port 2 controller.
    /// Owns the C64 keyboard matrix, the PETSCII key queue, and the
    /// joystick port 2 byte.  The SDL main loop hands every key event
    /// to <see cref="HandleSdlEvent"/> and calls nothing else.
    /// </summary>
    internal sealed class Keyboard
    {
        private readonly _6502_CPU cpu;

        private readonly ConcurrentQueue<byte> keyQueue = new ConcurrentQueue<byte>();

        // C64 keyboard matrix: 8 rows, each column bit is active-low.
        private readonly byte[] keyboardMatrix = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

        private volatile byte joystick2 = 0xFF;

        private bool caseModeUpper = true;
        private bool shiftLockActive;

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

        public Keyboard(_6502_CPU cpu)
        {
            this.cpu = cpu;
        }

        // ?? Public API ????????????????????????????????????????????????????????

        /// <summary>CIA-1 port A ($DC00) joystick port 2 byte (active-low).</summary>
        public byte Joystick2 => joystick2;

        /// <summary>
        /// Scans the keyboard matrix against the supplied CIA-1 row latch and DDR
        /// values and returns the resulting active-low column byte, exactly as the
        /// real CIA-1 does.
        /// </summary>
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
            joystick2 = 0xFF;
            for (int i = 0; i < keyboardMatrix.Length; i++)
                keyboardMatrix[i] = 0xFF;
            while (keyQueue.TryDequeue(out _)) { }
            caseModeUpper = true;
            shiftLockActive = false;
        }

        /// <summary>Enqueues a raw PETSCII byte for typed-text injection (e.g. from file load).</summary>
        public void EnqueuePetscii(byte petscii) => keyQueue.Enqueue(petscii);

        /// <summary>
        /// Handles a single SDL event.
        /// Returns <c>true</c> if the emulator should quit.
        /// </summary>
        public bool HandleSdlEvent(SDL_Event ev)
        {
            switch (ev.type)
            {
                case SDL_EventType.SDL_KEYDOWN:
                    return HandleKeyDown(ev.key);
                case SDL_EventType.SDL_KEYUP:
                    HandleKeyUp(ev.key);
                    return false;
                default:
                    return false;
            }
        }

        // ?? Private implementation ????????????????????????????????????????????

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
                    case SDL_Keycode.SDLK_o: OnLoad?.Invoke(); return false;
                    case SDL_Keycode.SDLK_s: OnSave?.Invoke(); return false;
                    case SDL_Keycode.SDLK_r:
                    case SDL_Keycode.SDLK_F12: OnHardReset?.Invoke(); return false;
                    case SDL_Keycode.SDLK_q: return true;
                    case SDL_Keycode.SDLK_w: return true;
                }
            }

            byte jmask = JoystickMaskFromKey(sym);
            if (jmask != 0)
                joystick2 = (byte)(joystick2 & ~jmask);

            UpdateKeyboardState(sym, true);
            return false;
        }

        private void HandleKeyUp(SDL_KeyboardEvent ke)
        {
            byte jmask = JoystickMaskFromKey(ke.keysym.sym);
            if (jmask != 0)
                joystick2 = (byte)(joystick2 | jmask);

            UpdateKeyboardState(ke.keysym.sym, false);
        }

        private byte ToPetscii(SDL_Keycode sym, SDL_Keymod mod)
        {
            bool shift = (mod & SDL_Keymod.KMOD_SHIFT) != 0;
            bool ctrl = (mod & SDL_Keymod.KMOD_CTRL) != 0;
            bool cbm = (mod & SDL_Keymod.KMOD_RALT) != 0;

            if (shift && cbm && (sym == SDL_Keycode.SDLK_LSHIFT || sym == SDL_Keycode.SDLK_RSHIFT ||
                                 sym == SDL_Keycode.SDLK_LALT || sym == SDL_Keycode.SDLK_RALT))
            {
                caseModeUpper = !caseModeUpper;
                return caseModeUpper ? (byte)0x8E : (byte)0x0E;
            }

            if (sym >= SDL_Keycode.SDLK_F1 && sym <= SDL_Keycode.SDLK_F8)
                return FunctionKeys[(int)(sym - SDL_Keycode.SDLK_F1)];

            if (sym >= SDL_Keycode.SDLK_a && sym <= SDL_Keycode.SDLK_z)
                return (byte)(0x41 + (int)(sym - SDL_Keycode.SDLK_a));

            if (sym >= SDL_Keycode.SDLK_1 && sym <= SDL_Keycode.SDLK_8)
            {
                int idx = (int)(sym - SDL_Keycode.SDLK_1);
                if (ctrl) return CtrlColours[idx];
                if (cbm) return CommodoreColours[idx];
            }

            if (sym >= SDL_Keycode.SDLK_0 && sym <= SDL_Keycode.SDLK_9)
            {
                int d = (int)(sym - SDL_Keycode.SDLK_0);
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
                    8 => (byte)'*',
                    9 => (byte)'(',
                    0 => (byte)')',
                    _ => 0
                };
            }

            if (sym >= SDL_Keycode.SDLK_KP_0 && sym <= SDL_Keycode.SDLK_KP_9)
                return (byte)(0x30 + (int)(sym - SDL_Keycode.SDLK_KP_0));

            return sym switch
            {
                SDL_Keycode.SDLK_SPACE => (byte)0x20,
                SDL_Keycode.SDLK_RETURN => (byte)0x0D,
                SDL_Keycode.SDLK_KP_ENTER => (byte)0x0D,
                SDL_Keycode.SDLK_BACKSPACE => (byte)0x14,
                SDL_Keycode.SDLK_TAB => (byte)0x20,
                SDL_Keycode.SDLK_ESCAPE => (byte)0x03,
                SDL_Keycode.SDLK_HOME => (byte)0x13,
                SDL_Keycode.SDLK_INSERT => (byte)0x94,
                SDL_Keycode.SDLK_DELETE => (byte)0x14,
                SDL_Keycode.SDLK_LEFT => (byte)0x9D,
                SDL_Keycode.SDLK_RIGHT => (byte)0x1D,
                SDL_Keycode.SDLK_UP => (byte)0x91,
                SDL_Keycode.SDLK_DOWN => (byte)0x11,
                SDL_Keycode.SDLK_PERIOD => shift ? (byte)'>' : (byte)'.',
                SDL_Keycode.SDLK_COMMA => shift ? (byte)'<' : (byte)',',
                SDL_Keycode.SDLK_SLASH => shift ? (byte)'?' : (byte)'/',
                SDL_Keycode.SDLK_SEMICOLON => shift ? (byte)':' : (byte)';',
                SDL_Keycode.SDLK_QUOTE => shift ? (byte)'@' : (byte)'\'',
                SDL_Keycode.SDLK_MINUS => shift ? (byte)'_' : (byte)'-',
                SDL_Keycode.SDLK_EQUALS => shift ? (byte)'+' : (byte)'=',
                SDL_Keycode.SDLK_LEFTBRACKET => shift ? (byte)'{' : (byte)'[',
                SDL_Keycode.SDLK_RIGHTBRACKET => shift ? (byte)'}' : (byte)']',
                SDL_Keycode.SDLK_BACKSLASH => shift ? (byte)'|' : (byte)'\\',
                _ => 0
            };
        }

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

        private void SetMatrixKey(int row, int column, bool pressed)
        {
            byte mask = (byte)(1 << column);
            if (pressed)
                keyboardMatrix[row] = (byte)(keyboardMatrix[row] & ~mask);
            else
                keyboardMatrix[row] = (byte)(keyboardMatrix[row] | mask);
        }

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
