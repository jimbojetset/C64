// ============================================================================
// Project:     C64
// File:        SidFile.cs
// Description: PSID/RSID music file parser for direct SID tune loading.
// Author:      James Booth
// Created:     2026
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Commodore 64 and related ROMs are property of their respective
//              rights holders. This emulator is for educational purposes only.
// ============================================================================

using System.Text;

namespace C64
{
    /// <summary>A parsed PSID/RSID tune ready to copy into C64 RAM.</summary>
    internal sealed record SidFile(
        bool IsRsid,
        ushort Version,
        ushort LoadAddress,
        ushort InitAddress,
        ushort PlayAddress,
        ushort Songs,
        ushort StartSong,
        uint Speed,
        string Name,
        string Author,
        string Released,
        byte[] Data)
    {
        /// <summary>Gets the zero-based song index expected by most SID init routines.</summary>
        public byte StartSongIndex => (byte)Math.Max(0, Math.Min(255, StartSong - 1));

        /// <summary>Gets whether the selected song requests CIA-timer playback according to the PSID speed word.</summary>
        public bool SelectedSongUsesCiaSpeed
        {
            get
            {
                int bit = Math.Clamp(StartSong - 1, 0, 31);
                return ((Speed >> bit) & 1) != 0;
            }
        }

        /// <summary>Parses a PSID or RSID byte stream.</summary>
        /// <param name="raw">The raw SID file bytes.</param>
        /// <returns>The parsed SID file.</returns>
        public static SidFile Parse(byte[] raw)
        {
            if (raw.Length < 0x76)
                throw new InvalidDataException("SID file is too small to contain a valid PSID/RSID header.");

            string magic = Encoding.ASCII.GetString(raw, 0, 4);
            bool isPsid = magic == "PSID";
            bool isRsid = magic == "RSID";
            if (!isPsid && !isRsid)
                throw new InvalidDataException("Not a valid SID file (expected PSID or RSID magic).");

            ushort version = ReadBe16(raw, 0x04);
            ushort dataOffset = ReadBe16(raw, 0x06);
            ushort loadAddress = ReadBe16(raw, 0x08);
            ushort initAddress = ReadBe16(raw, 0x0A);
            ushort playAddress = ReadBe16(raw, 0x0C);
            ushort songs = ReadBe16(raw, 0x0E);
            ushort startSong = ReadBe16(raw, 0x10);
            uint speed = ReadBe32(raw, 0x12);

            if (version == 0)
                throw new InvalidDataException("SID file has an invalid version.");
            if (dataOffset < 0x76 || dataOffset >= raw.Length)
                throw new InvalidDataException("SID file has an invalid data offset.");
            if (songs == 0)
                songs = 1;
            if (startSong == 0 || startSong > songs)
                startSong = 1;

            int payloadOffset = dataOffset;
            if (loadAddress == 0)
            {
                if (payloadOffset + 2 > raw.Length)
                    throw new InvalidDataException("SID file is missing the embedded little-endian load address.");

                loadAddress = (ushort)(raw[payloadOffset] | (raw[payloadOffset + 1] << 8));
                payloadOffset += 2;
            }

            if (loadAddress == 0)
                throw new InvalidDataException("SID file has no usable load address.");
            if (initAddress == 0)
                initAddress = loadAddress;

            int dataLength = raw.Length - payloadOffset;
            if (dataLength <= 0)
                throw new InvalidDataException("SID file contains no C64 payload data.");

            byte[] data = new byte[dataLength];
            Array.Copy(raw, payloadOffset, data, 0, dataLength);

            return new SidFile(
                isRsid,
                version,
                loadAddress,
                initAddress,
                playAddress,
                songs,
                startSong,
                speed,
                DecodeText(raw, 0x16, 32),
                DecodeText(raw, 0x36, 32),
                DecodeText(raw, 0x56, 32),
                data);
        }

        private static ushort ReadBe16(byte[] raw, int offset)
        {
            if (offset + 2 > raw.Length)
                throw new InvalidDataException("SID file header is truncated.");
            return (ushort)((raw[offset] << 8) | raw[offset + 1]);
        }

        private static uint ReadBe32(byte[] raw, int offset)
        {
            if (offset + 4 > raw.Length)
                throw new InvalidDataException("SID file header is truncated.");
            return (uint)((raw[offset] << 24) | (raw[offset + 1] << 16) | (raw[offset + 2] << 8) | raw[offset + 3]);
        }

        private static string DecodeText(byte[] raw, int offset, int length)
        {
            int available = Math.Max(0, Math.Min(length, raw.Length - offset));
            if (available == 0)
                return string.Empty;

            string text = Encoding.ASCII.GetString(raw, offset, available);
            int nul = text.IndexOf('\0');
            if (nul >= 0)
                text = text[..nul];

            return text.Trim();
        }
    }
}
