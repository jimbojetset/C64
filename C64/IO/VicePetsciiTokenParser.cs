// ============================================================================
// Project:     C64
// File:        VicePetsciiTokenParser.cs
// Description: Converts VICE-style PETSCII escape tokens (e.g. {home},
//              {right*39}, {5 space}, {shift-a}, {$93}, {147}) into their
//              corresponding PETSCII byte sequences, suitable for injection
//              into the C64 keyboard buffer.
// Author:      James Booth
// Created:     2025
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// ============================================================================

using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace C64.IO
{
    /// <summary>
    /// Parses VICE / CBM-prg-studio style PETSCII escape tokens embedded in
    /// plain text and emits the equivalent PETSCII byte stream.
    ///
    /// Supported token forms (case-insensitive, whitespace tolerant):
    ///   {name}              named control code, e.g. {home}, {clr}, {wht}
    ///   {name*N}            repeat a named token N times,  e.g. {right*39}
    ///   {N name}            VICE repeat-prefix form,       e.g. {5 space}
    ///   {shift-X}           shifted character X
    ///   {cbm-X}             commodore-key character X
    ///   {ctrl-X}            control character (PETSCII 1..31 for A..Z)
    ///   {$HH}               raw PETSCII byte in hex
    ///   {NNN}               raw PETSCII byte in decimal (0..255)
    ///
    /// Unknown tokens are emitted verbatim (including the surrounding braces)
    /// so the caller's existing ASCII-to-PETSCII path can decide what to do
    /// with the literal characters.
    /// </summary>
    internal static class VicePetsciiTokenParser
    {
        /// <summary>
        /// Scans <paramref name="text"/> for VICE-style tokens and produces a
        /// list of PETSCII bytes interleaved with literal characters. Literal
        /// characters are returned as ASCII so the caller can run them through
        /// its own host-ASCII-to-PETSCII translation. Recognized tokens are
        /// returned as already-resolved PETSCII bytes via the <c>IsPetscii</c>
        /// flag on each output element.
        /// </summary>
        /// <param name="text">The text segment to parse (no line breaks).</param>
        /// <returns>An ordered list of <see cref="TokenOutput"/> entries.</returns>
        public static List<TokenOutput> Parse(string text)
        {
            var result = new List<TokenOutput>(text.Length);
            int p = 0;
            while (p < text.Length)
            {
                char ch = text[p];

                if (ch == '{')
                {
                    int close = text.IndexOf('}', p + 1);
                    if (close > p)
                    {
                        string token = text.Substring(p + 1, close - p - 1);
                        if (TryExpand(token, out byte petByte, out int repeat))
                        {
                            for (int r = 0; r < repeat; r++)
                                result.Add(TokenOutput.Petscii(petByte));
                            p = close + 1;
                            continue;
                        }

                        /// Unknown token: pass through literally, braces and all.
                        for (int j = p; j <= close; j++)
                            result.Add(TokenOutput.Literal(text[j]));
                        p = close + 1;
                        continue;
                    }
                }

                result.Add(TokenOutput.Literal(ch));
                p++;
            }
            return result;
        }

        /// <summary>
        /// Attempts to expand the inside of a <c>{...}</c> token to a single
        /// PETSCII byte plus repeat count. Returns false for unknown tokens.
        /// </summary>
        private static bool TryExpand(string token, out byte petByte, out int repeat)
        {
            petByte = 0;
            repeat = 1;
            if (string.IsNullOrWhiteSpace(token)) return false;

            string body = token.Trim();

            /// {name*N} repeat-suffix form.
            int starIdx = body.IndexOf('*');
            if (starIdx >= 0)
            {
                string countPart = body.Substring(starIdx + 1).Trim();
                body = body.Substring(0, starIdx).Trim();
                if (!int.TryParse(countPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out repeat) || repeat <= 0)
                    return false;
            }
            else
            {
                /// {N name} repeat-prefix form (VICE).
                int sp = body.IndexOf(' ');
                if (sp > 0)
                {
                    string head = body.Substring(0, sp).Trim();
                    string tail = body.Substring(sp + 1).Trim();
                    if (int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 && tail.Length > 0)
                    {
                        repeat = n;
                        body = tail;
                    }
                }
            }

            /// {shift-X}, {cbm-X}, {ctrl-X}
            if (TryExpandModified(body, out petByte))
                return true;

            string key = body.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");

            /// Numeric forms: {$93} or {147}.
            if (key.Length > 0 && key[0] == '$')
            {
                if (byte.TryParse(key.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte hb))
                {
                    petByte = hb;
                    return true;
                }
                return false;
            }
            if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int decVal) && decVal >= 0 && decVal <= 255)
            {
                petByte = (byte)decVal;
                return true;
            }

            if (NamedTokens.TryGetValue(key, out byte named))
            {
                petByte = named;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Handles <c>{shift-X}</c>, <c>{cbm-X}</c>, and <c>{ctrl-X}</c>
        /// tokens, where X is a single ASCII letter or digit.
        /// </summary>
        private static bool TryExpandModified(string body, out byte petByte)
        {
            petByte = 0;
            int dash = body.IndexOf('-');
            if (dash <= 0 || dash >= body.Length - 1) return false;

            string modifier = body.Substring(0, dash).Trim().ToLowerInvariant();
            string targetRaw = body.Substring(dash + 1).Trim();
            if (targetRaw.Length != 1) return false;
            char t = targetRaw[0];

            switch (modifier)
            {
                case "ctrl":
                case "control":
                    /// CTRL-A..CTRL-Z => PETSCII 1..26
                    if (t >= 'a' && t <= 'z') { petByte = (byte)(1 + (t - 'a')); return true; }
                    if (t >= 'A' && t <= 'Z') { petByte = (byte)(1 + (t - 'A')); return true; }
                    return false;

                case "shift":
                case "shifted":
                    /// SHIFT-A..SHIFT-Z => PETSCII $C1..$DA (shifted letters in uppercase/graphics set)
                    if (t >= 'a' && t <= 'z') { petByte = (byte)(0xC1 + (t - 'a')); return true; }
                    if (t >= 'A' && t <= 'Z') { petByte = (byte)(0xC1 + (t - 'A')); return true; }
                    /// SHIFT-0..SHIFT-9 => host-side shifted symbols (best-effort mapping)
                    switch (t)
                    {
                        case '0': petByte = (byte)')'; return true;
                        case '1': petByte = (byte)'!'; return true;
                        case '2': petByte = (byte)'"'; return true;
                        case '3': petByte = (byte)'#'; return true;
                        case '4': petByte = (byte)'$'; return true;
                        case '5': petByte = (byte)'%'; return true;
                        case '6': petByte = (byte)'&'; return true;
                        case '7': petByte = (byte)'\''; return true;
                        case '8': petByte = (byte)'('; return true;
                        case '9': petByte = (byte)'*'; return true;
                        case '*': petByte = 0xC0; return true;     /// shift-* -> graphic
                        case '+': petByte = 0xDB; return true;     /// shift-+
                        case '-': petByte = 0xDD; return true;     /// shift--
                        case '@': petByte = 0xBA; return true;     /// shift-@
                        case ':': petByte = 0x5B; return true;     /// shift-: -> '['
                        case ';': petByte = 0x5D; return true;     /// shift-; -> ']'
                        case ',': petByte = (byte)'<'; return true;
                        case '.': petByte = (byte)'>'; return true;
                        case '/': petByte = (byte)'?'; return true;
                        case ' ': petByte = 0xA0; return true;     /// shifted space
                    }
                    return false;

                case "cbm":
                case "commodore":
                    /// CBM-A..CBM-Z => PETSCII $A1..$BA
                    if (t >= 'a' && t <= 'z') { petByte = (byte)(0xA1 + (t - 'a')); return true; }
                    if (t >= 'A' && t <= 'Z') { petByte = (byte)(0xA1 + (t - 'A')); return true; }
                    /// CBM-0..CBM-9 => PETSCII $30+ alt set (best-effort, rarely used)
                    switch (t)
                    {
                        case '0': petByte = 0x30; return true;
                        case '1': petByte = 0x81; return true; /// orange (color), legacy alias
                        case '2': petByte = 0x95; return true; /// brown
                        case '3': petByte = 0x96; return true; /// light red
                        case '4': petByte = 0x97; return true; /// dark gray
                        case '5': petByte = 0x98; return true; /// medium gray
                        case '6': petByte = 0x99; return true; /// light green
                        case '7': petByte = 0x9A; return true; /// light blue
                        case '8': petByte = 0x9B; return true; /// light gray
                        case '9': petByte = 0x29; return true;
                        case '+': petByte = 0xA6; return true;
                        case '-': petByte = 0xDC; return true;
                        case '@': petByte = 0xA4; return true;
                        case '*': petByte = 0xDF; return true;
                        case ':': petByte = 0xDA; return true;
                        case ';': petByte = 0xDB; return true;
                        case ',': petByte = 0xBC; return true;
                        case '.': petByte = 0xBE; return true;
                        case '/': petByte = 0xBF; return true;
                        case ' ': petByte = 0xA0; return true;
                    }
                    return false;
            }

            return false;
        }

        /// <summary>
        /// Master table of named VICE / community PETSCII tokens, normalized to
        /// lowercase with separators stripped (e.g. "shiftspace" matches
        /// "shift space" / "shift-space").
        /// </summary>
        private static readonly Dictionary<string, byte> NamedTokens = BuildNamedTokens();

        private static Dictionary<string, byte> BuildNamedTokens()
        {
            var d = new Dictionary<string, byte>(System.StringComparer.Ordinal);

            void Add(byte b, params string[] names)
            {
                foreach (var n in names)
                {
                    string k = n.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
                    if (!d.ContainsKey(k)) d[k] = b;
                }
            }

            /// Cursor / editor control codes
            Add(0x03, "stop", "runstop");
            Add(0x05, "wht", "white");
            Add(0x07, "bell", "beep");
            Add(0x08, "dish", "disablecase", "lockcase");        /// $08 disables Shift+CBM (case change)
            Add(0x09, "ensh", "enablecase", "unlockcase");       /// $09 enables Shift+CBM
            Add(0x0A, "lf", "linefeed");
            Add(0x0D, "return", "cr", "enter");
            Add(0x0E, "swlc", "lowercase", "textmode");          /// switch to lower/upper case set
            Add(0x11, "down", "cdown", "cursordown");
            Add(0x12, "rvson", "rvon", "rvs", "reverseon");
            Add(0x13, "home", "clrhome");
            Add(0x14, "del", "inst", "instdel", "delete");
            Add(0x1C, "red");
            Add(0x1D, "right", "cright", "cursorright");
            Add(0x1E, "grn", "green");
            Add(0x1F, "blu", "blue");
            Add(0x20, "space", "sp");
            Add(0x5C, "pound", "ukpound", "sterling");
            Add(0x5E, "uparrow", "arrowup");
            Add(0x5F, "leftarrow", "arrowleft");
            Add(0x81, "orng", "orange");
            Add(0x85, "f1");
            Add(0x86, "f3");
            Add(0x87, "f5");
            Add(0x88, "f7");
            Add(0x89, "f2");
            Add(0x8A, "f4");
            Add(0x8B, "f6");
            Add(0x8C, "f8");
            Add(0x8D, "shiftreturn", "shenter");
            Add(0x8E, "swuc", "uppercase", "graphicsmode");      /// switch to upper case / graphics set
            Add(0x90, "blk", "black");
            Add(0x91, "up", "cup", "cursorup");
            Add(0x92, "rvsoff", "rvoff", "reverseoff");
            Add(0x93, "clr", "clear", "cls");
            Add(0x94, "instdelshift", "insert", "shiftinst");
            Add(0x95, "brn", "brown");
            Add(0x96, "lred", "lightred", "pink");
            Add(0x97, "gry1", "darkgray", "darkgrey", "grey1", "gray1");
            Add(0x98, "gry2", "gray", "grey", "mediumgray", "mediumgrey", "grey2", "gray2");
            Add(0x99, "lgrn", "lightgreen");
            Add(0x9A, "lblu", "lightblue");
            Add(0x9B, "gry3", "lightgray", "lightgrey", "grey3", "gray3");
            Add(0x9C, "pur", "purple", "magenta");
            Add(0x9D, "left", "cleft", "cursorleft");
            Add(0x9E, "yel", "yellow");
            Add(0x9F, "cyn", "cyan");
            Add(0xA0, "shiftspace", "sspace", "nbsp");
            Add(0xFF, "pi", "greekpi");

            return d;
        }

        /// <summary>
        /// One element of the parser output stream: either a literal host
        /// character (still needing ASCII-to-PETSCII translation) or an
        /// already-resolved PETSCII byte.
        /// </summary>
        internal readonly struct TokenOutput
        {
            public bool IsPetscii { get; }
            public byte Byte { get; }
            public char Char { get; }

            private TokenOutput(bool isPet, byte b, char c)
            {
                IsPetscii = isPet;
                Byte = b;
                Char = c;
            }

            public static TokenOutput Petscii(byte b) => new TokenOutput(true, b, '\0');
            public static TokenOutput Literal(char c) => new TokenOutput(false, 0, c);
        }
    }
}
