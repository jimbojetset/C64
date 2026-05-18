using System.Text;

namespace C64
{

    /// <summary>A single loadable program block extracted from a T64 file.</summary>
    internal sealed record TapeEntry(
        string   Name,
        ushort   LoadAddress,
        byte[]   Data)
    {

        /// <summary>True when the program sits at the standard BASIC start address $0801.</summary>
        public bool IsBasic => LoadAddress == 0x0801;
    }

    /// <summary>
    /// Parses .t64 tape-archive files into <see cref="TapeEntry"/> objects
    /// that the emulator can load directly.
    /// </summary>
    internal static class TapeLoader
    {
        // ?? T64 ??????????????????????????????????????????????????????????????

        /// <summary>Parses a T64 tape-archive and returns all usable program entries.</summary>
        public static List<TapeEntry> ReadT64(byte[] raw)
        {
            // T64 layout:
            //   [0-31]  Container ID string (starts with "C64")
            //   [32-33] Version (0x0100 or 0x0200)
            //   [34-35] Max directory entries
            //   [36-37] Used directory entries
            //   [38-39] Unused
            //   [40-63] Tape name (24 bytes, PETSCII, space-padded)
            //   [64+]   32-byte directory entries

            if (raw.Length < 64)
                throw new InvalidDataException("T64 file is too short to contain a valid header.");

            if (raw[0] != (byte)'C' || raw[1] != (byte)'6' || raw[2] != (byte)'4')
                throw new InvalidDataException("Not a valid T64 file (bad magic).");

            int usedEntries = raw[36] | (raw[37] << 8);

            // Some poorly-mastered T64 files leave usedEntries = 0 even though
            // entries are present.  Fall back to the max-entries field.
            if (usedEntries == 0)
                usedEntries = raw[34] | (raw[35] << 8);

            var results = new List<TapeEntry>();

            for (int i = 0; i < usedEntries; i++)
            {
                int d = 64 + i * 32;
                if (d + 32 > raw.Length) break;

                byte entryType = raw[d];
                if (entryType == 0) continue; // free slot

                ushort loadAddr = (ushort)(raw[d + 2] | (raw[d + 3] << 8));
                ushort endAddr  = (ushort)(raw[d + 4] | (raw[d + 5] << 8));
                int    offset   = raw[d + 8] | (raw[d + 9] << 8) | (raw[d + 10] << 16) | (raw[d + 11] << 24);

                // Guard against bad end-address (some tools write 0xC3C6 as a placeholder).
                int dataLen = endAddr - loadAddr;
                if (dataLen <= 0)
                {
                    // Use however many bytes are available from the offset to EOF
                    dataLen = raw.Length - offset;
                }

                if (offset < 0 || offset >= raw.Length) continue;
                dataLen = Math.Min(dataLen, raw.Length - offset);
                if (dataLen <= 0) continue;

                string name = DecodePetsciiName(raw, d + 16, 16);

                byte[] data = new byte[dataLen];
                Array.Copy(raw, offset, data, 0, dataLen);

                results.Add(new TapeEntry(name, loadAddr, data));
            }

            if (results.Count == 0)
                throw new InvalidDataException("No usable program entries found in T64 file.");

            return results;
        }

        /// <summary>Decodes petscii name.</summary>
        private static string DecodePetsciiName(byte[] src, int offset, int length)
        {
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                if (offset + i >= src.Length) break;
                byte b = src[offset + i];

                if (b == 0x00 || b == 0xA0) break; // null or PETSCII non-breaking space = end

                // PETSCII $41–$5A = uppercase A–Z (maps to the same ASCII range)
                // PETSCII $61–$7A = lowercase a–z in PETSCII lowercase mode
                if (b is >= 0x41 and <= 0x5A) sb.Append((char)b);
                else if (b is >= 0x61 and <= 0x7A) sb.Append((char)(b - 0x20));
                else if (b is >= 0x20 and < 0x80)  sb.Append((char)b);
                // else: graphics/control character — skip
            }
            return sb.ToString().TrimEnd();
        }
    }
}
