

using System.Text;

namespace C64.IO
{

    internal sealed record TapeEntry(
        string Name,
        ushort LoadAddress,
        byte[] Data)
    {

        public bool IsBasic => LoadAddress == 0x0801;
    }

    internal static class TapeLoader
    {

        /// <summary>Parses a T64 tape-archive and returns all usable program entries.</summary>
        /// <param name="raw">The raw bytes to decode.</param>
        public static List<TapeEntry> ReadT64(byte[] raw)
        {

            if (raw.Length < 64)
                throw new InvalidDataException("T64 file is too short to contain a valid header.");

            if (raw[0] != (byte)'C' || raw[1] != (byte)'6' || raw[2] != (byte)'4')
                throw new InvalidDataException("Not a valid T64 file (bad magic).");

            int usedEntries = raw[36] | (raw[37] << 8);

            if (usedEntries == 0)
                usedEntries = raw[34] | (raw[35] << 8);

            var results = new List<TapeEntry>();

            for (int i = 0; i < usedEntries; i++)
            {
                int d = 64 + i * 32;
                if (d + 32 > raw.Length) break;

                byte entryType = raw[d];
                if (entryType == 0) continue;

                ushort loadAddr = (ushort)(raw[d + 2] | (raw[d + 3] << 8));
                ushort endAddr = (ushort)(raw[d + 4] | (raw[d + 5] << 8));
                int offset = raw[d + 8] | (raw[d + 9] << 8) | (raw[d + 10] << 16) | (raw[d + 11] << 24);

                int dataLen = endAddr - loadAddr;
                if (dataLen <= 0)
                {

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

        /// <summary>Decodes a padded PETSCII tape-entry name into trimmed host text.</summary>
        /// <param name="src">The source byte buffer to read from.</param>
        /// <param name="offset">The starting offset within the buffer.</param>
        /// <param name="length">The number of bytes to load.</param>
        private static string DecodePetsciiName(byte[] src, int offset, int length)
        {
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                if (offset + i >= src.Length) break;
                byte b = src[offset + i];

                if (b == 0x00 || b == 0xA0) break;

                if (b is >= 0x41 and <= 0x5A) sb.Append((char)b);
                else if (b is >= 0x61 and <= 0x7A) sb.Append((char)(b - 0x20));
                else if (b is >= 0x20 and < 0x80) sb.Append((char)b);

            }
            return sb.ToString().TrimEnd();
        }
    }
}