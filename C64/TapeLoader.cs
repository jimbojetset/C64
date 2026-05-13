using System.Text;

namespace C64
{
    /// <summary>A single loadable program block extracted from a T64 or TAP file.</summary>
    internal sealed record TapeEntry(
        string   Name,
        ushort   LoadAddress,
        byte[]   Data)
    {
        /// <summary>True when the program sits at the standard BASIC start address $0801.</summary>
        public bool IsBasic => LoadAddress == 0x0801;
    }

    /// <summary>
    /// Parses .t64 tape-archive files and .tap raw-pulse files into
    /// <see cref="TapeEntry"/> objects that the emulator can load directly.
    ///
    /// TAP support covers the standard C64 ROM tape loader only (pilot tone +
    /// sync byte + 192-byte header + data block).  Commercial games that use
    /// custom turbo loaders (Novaload, Bleepload, Cyberload, …) encode pulses
    /// with different timing and/or protocol and require full CIA datasette
    /// hardware emulation — those files will throw <see cref="TurboTapeException"/>.
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

        // ?? TAP ??????????????????????????????????????????????????????????????

        /// <summary>
        /// Decodes a TAP raw-pulse file using the standard C64 ROM tape protocol
        /// and returns all program blocks found.
        /// </summary>
        /// <exception cref="TurboTapeException">
        /// Thrown when the pulse stream does not match the standard ROM loader
        /// protocol, which indicates a turbo-loader tape that needs hardware
        /// datasette emulation.
        /// </exception>
        public static List<TapeEntry> ReadTap(byte[] raw)
        {
            // TAP header:
            //   [0-11]  Magic "C64-TAPE-RAW" or "C16-TAPE-RAW"
            //   [12]    Version (0 = no overflow, 1 = overflow blocks supported)
            //   [13]    Platform (0 = C64, 1 = VIC-20, 2 = C16/Plus4)
            //   [14]    Video standard (0 = PAL, 1 = NTSC)
            //   [15]    Reserved
            //   [16-19] Data length in bytes (little-endian)
            //   [20+]   Pulse data

            if (raw.Length < 20)
                throw new InvalidDataException("TAP file is too short to contain a valid header.");

            string magic = Encoding.ASCII.GetString(raw, 0, 12);
            if (!magic.StartsWith("C64-TAPE-RAW", StringComparison.Ordinal) &&
                !magic.StartsWith("C16-TAPE-RAW", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Not a valid TAP file (magic: '{magic.TrimEnd()}').");
            }

            byte version  = raw[12];
            int  dataSize = raw[16] | (raw[17] << 8) | (raw[18] << 16) | (raw[19] << 24);
            int  end      = Math.Min(20 + dataSize, raw.Length);

            // Build the pulse list.
            // Each non-zero byte = pulse length in units of 8 CPU cycles.
            // A zero byte in version 0 = long silence (skip).
            // A zero byte in version 1 = followed by a 3-byte little-endian length
            // in single CPU cycles, which we convert back to *8 units.
            var pulses = new List<int>(dataSize);
            int pos = 20;

            while (pos < end)
            {
                byte b = raw[pos++];
                if (b != 0)
                {
                    pulses.Add(b);
                }
                else if (version >= 1)
                {
                    if (pos + 3 > end) break;
                    int longVal = raw[pos] | (raw[pos + 1] << 8) | (raw[pos + 2] << 16);
                    pos += 3;
                    pulses.Add(longVal / 8); // convert cycles ? TAP units
                }
                // version 0 zero byte = overflow / long silence, ignore
            }

            return DecodePulseStream(pulses);
        }

        // ?? Pulse decoder ????????????????????????????????????????????????????

        // Standard C64 ROM loader pulse-width thresholds (PAL, TAP units = 8 CPU cycles).
        //
        //   Short  (S): ? 0x38  ? 183 µs  — the "0-bit" marker pulse
        //   Medium (M): ? 0x4C  ? 311 µs  — the "1-bit" marker pulse
        //   Long   (L):  > 0x4C            — new-data / end-of-block marker
        //
        // Each data bit is encoded as a consecutive pair:
        //   Bit 0  =  Short  + Medium
        //   Bit 1  =  Medium + Short
        // Bytes are transmitted LSB-first.

        private const int ShortMax  = 0x38;
        private const int MediumMax = 0x4C;

        private static List<TapeEntry> DecodePulseStream(List<int> pulses)
        {
            var results = new List<TapeEntry>();
            int idx = 0;
            int turboLikePairs = 0;

            while (idx < pulses.Count)
            {
                // ?? Pilot tone ??????????????????????????????????????????????
                // The standard ROM loader uses a run of short pulses as a preamble.
                // Header pilot: ? 27 000 short pulses; we accept ? 100 to be lenient.
                int pilot = 0;
                while (idx < pulses.Count && pulses[idx] <= ShortMax)
                {
                    pilot++;
                    idx++;
                }

                if (pilot < 100)
                {
                    // Skip non-pilot pulses (gaps, long pulses between blocks).
                    if (idx < pulses.Count) idx++;
                    continue;
                }

                // ?? Sync byte ???????????????????????????????????????????????
                // Standard ROM loader header sync = $89, data sync = $09.
                byte? sync = TryReadByte(pulses, ref idx, ref turboLikePairs);
                if (sync == null) continue;
                if (sync != 0x89 && sync != 0x09) continue;

                bool readingHeader = (sync == 0x89);

                // ?? Header block (192 bytes) ????????????????????????????????
                // Layout:
                //   [0]      Block type: $01 = relocatable, $03 = sequential data
                //   [1-2]    Start address (LE)
                //   [3-4]    End address   (LE, exclusive)
                //   [5-20]   Filename (16 bytes, PETSCII, space-padded)
                //   [21-191] Filler ($20)
                // [192]      Checksum byte (XOR of all 192 header bytes) — read but not strict

                if (!readingHeader)
                {
                    // A data sync without a preceding header sync — skip.
                    continue;
                }

                byte[] header = new byte[192];
                if (!TryReadBytes(pulses, ref idx, header, ref turboLikePairs)) continue;

                // Read (and discard) the checksum byte; we don't enforce it because
                // some duplicated tape copies have been poorly preserved.
                TryReadByte(pulses, ref idx, ref turboLikePairs);

                byte blockType = header[0];
                if (blockType != 0x01 && blockType != 0x03) continue; // not a program block

                ushort startAddr = (ushort)(header[1] | (header[2] << 8));
                ushort endAddr   = (ushort)(header[3] | (header[4] << 8));
                string name      = DecodePetsciiName(header, 5, 16);

                int dataLen = endAddr - startAddr;
                if (dataLen <= 0 || dataLen > 0xFFFF) continue;

                // ?? Data block ??????????????????????????????????????????????
                // After the header there is a data-pilot (? 20 short pulses)
                // followed by a sync byte ($09) and then the payload bytes.
                int dataPilot = 0;
                while (idx < pulses.Count && pulses[idx] <= ShortMax)
                {
                    dataPilot++;
                    idx++;
                }
                if (dataPilot < 20) continue;

                byte? dataSync = TryReadByte(pulses, ref idx, ref turboLikePairs);
                if (dataSync == null || (dataSync != 0x09 && dataSync != 0x89)) continue;

                byte[] data = new byte[dataLen];
                if (!TryReadBytes(pulses, ref idx, data, ref turboLikePairs)) continue;

                // Read the data checksum byte (optional — skip on failure).
                TryReadByte(pulses, ref idx, ref turboLikePairs);

                results.Add(new TapeEntry(name, startAddr, data));
            }

            // If we found many unrecognised pulse pairs but no standard blocks,
            // the file is almost certainly a turbo-loader tape.
            if (results.Count == 0 && turboLikePairs > 50)
            {
                throw new TurboTapeException(
                    "No standard-format blocks were found in this TAP file. " +
                    "The tape appears to use a custom turbo loader (Novaload, " +
                    "Bleepload, Cyberload, etc.) which requires full CIA datasette " +
                    "hardware emulation to decode. Use a .t64 version of this game instead.");
            }

            return results;
        }

        // Reads exactly `dest.Length` bytes from the pulse stream.
        private static bool TryReadBytes(List<int> pulses, ref int idx, byte[] dest, ref int turboPairs)
        {
            for (int i = 0; i < dest.Length; i++)
            {
                byte? b = TryReadByte(pulses, ref idx, ref turboPairs);
                if (b == null) return false;
                dest[i] = b.Value;
            }
            return true;
        }

        // Reads one byte (8 bit-pairs, LSB-first) from the pulse stream.
        // Returns null if the stream runs out of pulses.
        // Increments turboPairs for every pulse pair that doesn't fit the standard protocol.
        private static byte? TryReadByte(List<int> pulses, ref int idx, ref int turboPairs)
        {
            byte value = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                if (idx + 1 >= pulses.Count) return null;

                int p1 = pulses[idx];
                int p2 = pulses[idx + 1];
                idx += 2;

                bool p1Short  = p1 <= ShortMax;
                bool p1Medium = !p1Short && p1 <= MediumMax;
                bool p2Short  = p2 <= ShortMax;
                bool p2Medium = !p2Short && p2 <= MediumMax;

                int bitVal;
                if      (p1Short  && p2Medium) bitVal = 0; // S + M = bit 0
                else if (p1Medium && p2Short)  bitVal = 1; // M + S = bit 1
                else
                {
                    // Unrecognised pair — turbo tape or noise.
                    turboPairs++;
                    return null;
                }

                value |= (byte)(bitVal << bit);
            }
            return value;
        }

        // ?? Shared helpers ????????????????????????????????????????????????????

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

    /// <summary>
    /// Thrown when a TAP file uses a custom turbo-loader protocol that cannot
    /// be decoded without full CIA datasette hardware emulation.
    /// </summary>
    internal sealed class TurboTapeException(string message) : Exception(message) { }
}
