// ============================================================================
// Project:     C64
// File:        D64Image.cs
// Description: D64 disk image parser with directory decoding, PRG loading,
//              disk-name handling, and raw sector access.
// Author:      James Booth
// Created:     2025
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Commodore 64 and related ROMs are property of their respective
//              rights holders. This emulator is for educational purposes only.
// ============================================================================

using System.Text;

namespace C64
{
    /// <summary>
    /// Parses a D64 disk image and exposes directory, PRG loading, disk-name, and raw sector access helpers.
    /// </summary>
    internal sealed class D64Image
    {
        private static readonly int[] SectorsPerTrack =
        {
            0,
            21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,21,
            19,19,19,19,19,19,19,
            18,18,18,18,18,18,
            17,17,17,17,17,
            17,17,17,17,17,17,17
        };

        private readonly byte[] raw;

        /// <summary>Initializes a new D64Image instance.</summary>
        /// <param name="raw">The raw D64 image bytes.</param>
        private D64Image(byte[] raw)
        {
            this.raw = raw;
        }

        /// <summary>Gets or sets the source image path.</summary>
        public string SourcePath { get; private set; } = string.Empty;

        /// <summary>Loads a D64 disk image from disk.</summary>
        /// <param name="path">The path of the file to use.</param>
        /// <returns>The opened disk image.</returns>
        public static D64Image Load(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            if (raw.Length < 174_848)
                throw new InvalidDataException("D64 image is too small.");

            var img = new D64Image(raw) { SourcePath = path };
            return img;
        }

        /// <summary>Lists PRG directory entries in the image.</summary>
        /// <returns>The available file names.</returns>
        public IReadOnlyList<string> ListPrgFiles()
        {
            var files = new List<string>();
            foreach (var e in ReadDirectoryEntries())
            {
                if (!IsLoadableFileType(e.FileType)) /// Accept PRG-like files
                    continue;
                files.Add(e.Name);
            }
            return files;
        }

        /// <summary>Determines whether a directory file type is loadable.</summary>
        /// <param name="fileType">The Commodore directory file type byte.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        private static bool IsLoadableFileType(byte fileType)
        {
            return (fileType & 0x07) == 0x02;
        }

        /// <summary>Attempts to load prg.</summary>
        /// <param name="requestedName">The C64 filename requested by the caller, or null to select a default.</param>
        /// <param name="prgBytes">Receives or contains the PRG bytes for the operation.</param>
        /// <param name="resolvedName">Receives the resolved C64 filename when the operation succeeds.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        public bool TryLoadPrg(string? requestedName, out byte[] prgBytes, out string resolvedName)
        {
            prgBytes = Array.Empty<byte>();
            resolvedName = string.Empty;

            var entries = ReadDirectoryEntries();
            if (entries.Count == 0)
                return false;

            DirectoryEntry? selected = null;
            string wanted = NormalizeName(requestedName ?? string.Empty);
            if (string.IsNullOrEmpty(wanted) || wanted == "*")
            {
                /// CBM DOS: empty name or "*" means the first file on the disk.
                selected = entries.FirstOrDefault(e => IsLoadableFileType(e.FileType));
            }
            else if (wanted.EndsWith("*", StringComparison.Ordinal))
            {
                /// Prefix wildcard, e.g. LOAD"ELI*",8
                string prefix = wanted.Substring(0, wanted.Length - 1);
                selected = entries.FirstOrDefault(e => IsLoadableFileType(e.FileType) && NormalizeName(e.Name).StartsWith(prefix, StringComparison.Ordinal));
            }
            else
            {
                selected = entries.FirstOrDefault(e => IsLoadableFileType(e.FileType) && NormalizeName(e.Name) == wanted);
                selected ??= entries.FirstOrDefault(e => IsLoadableFileType(e.FileType) && NormalizeName(e.Name).StartsWith(wanted, StringComparison.Ordinal));
            }

            if (selected is null)
                return false;

            var data = new List<byte>(8192);
            byte track = selected.StartTrack;
            byte sector = selected.StartSector;

            while (track != 0)
            {
                int off = Offset(track, sector);
                if (off < 0 || off + 256 > raw.Length)
                    break;

                byte nextTrack = raw[off];
                byte nextSector = raw[off + 1];

                if (nextTrack == 0)
                {
                    int used = nextSector - 1;
                    if (used <= 0 || used > 254) used = 254;
                    for (int i = 0; i < used; i++)
                        data.Add(raw[off + 2 + i]);
                    break;
                }

                for (int i = 0; i < 254; i++)
                    data.Add(raw[off + 2 + i]);

                track = nextTrack;
                sector = nextSector;
            }

            prgBytes = data.ToArray();
            resolvedName = selected.Name;
            return prgBytes.Length >= 3;
        }

        /// <summary>Attempts to load directory.</summary>
        /// <param name="prgBytes">Receives or contains the PRG bytes for the operation.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        public bool TryLoadDirectory(out byte[] prgBytes)
        {
            prgBytes = Array.Empty<byte>();

            var body = new List<byte>(2048);
            ushort nextLineAddress = 0x0801;
            AppendDirectoryLine(body, ref nextLineAddress, 0, $"\"{GetDiskName()}\" 00 2A");

            foreach (DirectoryEntry entry in ReadDirectoryEntries())
            {
                string type = FileTypeName(entry.FileType);
                AppendDirectoryLine(body, ref nextLineAddress, entry.Blocks, $"\"{entry.Name}\" {type}");
            }

            AppendDirectoryLine(body, ref nextLineAddress, 0, "BLOCKS FREE.");
            body.Add(0x00);
            body.Add(0x00);

            prgBytes = new byte[body.Count + 2];
            prgBytes[0] = 0x01;
            prgBytes[1] = 0x08;
            body.CopyTo(prgBytes, 2);
            return true;
        }

        /// <summary>Attempts to read sector.</summary>
        /// <param name="track">The disk track number.</param>
        /// <param name="sector">The disk sector number.</param>
        /// <param name="sectorBytes">Receives the sector bytes when the read succeeds.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        public bool TryReadSector(int track, int sector, out byte[] sectorBytes)
        {
            sectorBytes = Array.Empty<byte>();
            int off = Offset(track, sector);
            if (off < 0 || off + 256 > raw.Length)
                return false;

            sectorBytes = new byte[256];
            Array.Copy(raw, off, sectorBytes, 0, sectorBytes.Length);
            return true;
        }

        /// <summary>Reads directory entries.</summary>
        /// <returns>The directory entries decoded from the D64 image.</returns>
        private List<DirectoryEntry> ReadDirectoryEntries()
        {
            var list = new List<DirectoryEntry>();

            byte track = 18;
            byte sector = 1;

            while (track != 0)
            {
                int off = Offset(track, sector);
                if (off < 0 || off + 256 > raw.Length)
                    break;

                byte nextTrack = raw[off];
                byte nextSector = raw[off + 1];

                for (int i = 0; i < 8; i++)
                {
                    int eoff = off + i * 32;
                    byte fileType = (byte)(raw[eoff + 2] & 0x87);
                    if (fileType == 0)
                        continue;

                    byte st = raw[eoff + 3];
                    byte ss = raw[eoff + 4];
                    string name = DecodePetsciiName(raw, eoff + 5, 16);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    ushort blocks = (ushort)(raw[eoff + 30] | (raw[eoff + 31] << 8));
                    list.Add(new DirectoryEntry(name, fileType, st, ss, blocks));
                }

                track = nextTrack;
                sector = nextSector;
            }

            return list;
        }

        /// <summary>Reads the disk name from the directory sector.</summary>
        /// <returns>The string value produced by the operation.</returns>
        private string GetDiskName()
        {
            int bam = Offset(18, 0);
            if (bam < 0 || bam + 0xA0 > raw.Length)
                return "DISK";

            string name = DecodePetsciiName(raw, bam + 0x90, 16);
            return string.IsNullOrWhiteSpace(name) ? "DISK" : name;
        }

        /// <summary>Appends one BASIC directory listing line.</summary>
        /// <param name="body">The BASIC directory body being built.</param>
        /// <param name="lineAddress">The next BASIC line address to write and advance.</param>
        /// <param name="lineNumber">The BASIC line number to emit.</param>
        /// <param name="text">The text to write.</param>
        private static void AppendDirectoryLine(List<byte> body, ref ushort lineAddress, ushort lineNumber, string text)
        {
            int lineStart = body.Count;
            body.Add(0x00);
            body.Add(0x00);
            body.Add((byte)(lineNumber & 0xFF));
            body.Add((byte)(lineNumber >> 8));

            foreach (char ch in text)
                body.Add(CharToPetscii(ch));

            body.Add(0x00);

            ushort next = (ushort)(lineAddress + (body.Count - lineStart));
            body[lineStart] = (byte)(next & 0xFF);
            body[lineStart + 1] = (byte)(next >> 8);
            lineAddress = next;
        }

        /// <summary>Converts an ASCII character to PETSCII.</summary>
        /// <param name="ch">The character to convert or write.</param>
        /// <returns>The byte value produced by the operation.</returns>
        private static byte CharToPetscii(char ch)
        {
            if (ch >= 'a' && ch <= 'z')
                ch = (char)(ch - 0x20);
            return ch >= ' ' && ch <= '~' ? (byte)ch : (byte)'?';
        }

        /// <summary>Formats a D64 file type byte.</summary>
        /// <param name="fileType">The Commodore directory file type byte.</param>
        /// <returns>The string value produced by the operation.</returns>
        private static string FileTypeName(byte fileType)
        {
            return (fileType & 0x07) switch
            {
                0 => "DEL",
                1 => "SEQ",
                2 => "PRG",
                3 => "USR",
                4 => "REL",
                _ => "???",
            };
        }

        /// <summary>Decodes petscii name.</summary>
        /// <param name="src">The source byte buffer to read from.</param>
        /// <param name="offset">The starting offset within the buffer.</param>
        /// <param name="len">The number of bytes to decode.</param>
        /// <returns>The string value produced by the operation.</returns>
        private static string DecodePetsciiName(byte[] src, int offset, int len)
        {
            var sb = new StringBuilder(len);
            for (int i = 0; i < len; i++)
            {
                byte b = src[offset + i];
                if (b == 0xA0 || b == 0x00)
                    break;
                if (b >= 0x41 && b <= 0x5A)
                    sb.Append((char)b);
                else if (b >= 0x61 && b <= 0x7A)
                    sb.Append((char)(b - 0x20));
                else if (b >= 0x20 && b <= 0x7E)
                    sb.Append((char)b);
            }
            return sb.ToString().Trim();
        }

        /// <summary>Normalizes name.</summary>
        /// <param name="s">The string to normalize.</param>
        /// <returns>The string value produced by the operation.</returns>
        private static string NormalizeName(string s)
        {
            return s.Trim().Trim('"', '\'').ToUpperInvariant();
        }

        /// <summary>Calculates the byte offset of a D64 sector.</summary>
        /// <param name="track">The disk track number.</param>
        /// <param name="sector">The disk sector number.</param>
        /// <returns>The numeric value produced by the operation.</returns>
        private static int Offset(int track, int sector)
        {
            if (track <= 0 || track >= SectorsPerTrack.Length)
                return -1;
            if (sector < 0 || sector >= SectorsPerTrack[track])
                return -1;

            int sectorsBefore = 0;
            for (int t = 1; t < track; t++)
                sectorsBefore += SectorsPerTrack[t];

            return (sectorsBefore + sector) * 256;
        }

        /// <summary>Represents Directory Entry.</summary>
        private sealed record DirectoryEntry(string Name, byte FileType, byte StartTrack, byte StartSector, ushort Blocks);
    }
}