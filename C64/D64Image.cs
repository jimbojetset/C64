using System.Text;

namespace C64
{
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

        private D64Image(byte[] raw)
        {
            this.raw = raw;
        }

        public string SourcePath { get; private set; } = string.Empty;

        public static D64Image Load(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            if (raw.Length < 174_848)
                throw new InvalidDataException("D64 image is too small.");

            var img = new D64Image(raw) { SourcePath = path };
            return img;
        }

        public IReadOnlyList<string> ListPrgFiles()
        {
            var files = new List<string>();
            foreach (var e in ReadDirectoryEntries())
            {
                if (!IsLoadableFileType(e.FileType)) // Accept PRG-like files
                    continue;
                files.Add(e.Name);
            }
            return files;
        }

        private static bool IsLoadableFileType(byte fileType)
        {
            // Low 3 bits of the type byte are the CBM file type: 2 = PRG.
            // The 0x80 bit indicates the file was properly closed; many cracked
            // disks leave it clear, so accept both 0x82 and 0x02.
            return (fileType & 0x07) == 0x02;
        }

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
                // CBM DOS: empty name or "*" means the first file on the disk.
                selected = entries.FirstOrDefault(e => IsLoadableFileType(e.FileType));
            }
            else if (wanted.EndsWith("*", StringComparison.Ordinal))
            {
                // Prefix wildcard, e.g. LOAD"ELI*",8
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
                    // Each directory entry is 32 bytes long starting at sector offset i*32.
                    // The first two bytes of entry 0 hold the chain link (already read above);
                    // the remaining entries' first two bytes are unused. File type lives at
                    // entry offset 2 regardless.
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

        private string GetDiskName()
        {
            int bam = Offset(18, 0);
            if (bam < 0 || bam + 0xA0 > raw.Length)
                return "DISK";

            string name = DecodePetsciiName(raw, bam + 0x90, 16);
            return string.IsNullOrWhiteSpace(name) ? "DISK" : name;
        }

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

        private static byte CharToPetscii(char ch)
        {
            if (ch >= 'a' && ch <= 'z')
                ch = (char)(ch - 0x20);
            return ch >= ' ' && ch <= '~' ? (byte)ch : (byte)'?';
        }

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

        private static string NormalizeName(string s)
        {
            return s.Trim().Trim('"', '\'').ToUpperInvariant();
        }

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

        private sealed record DirectoryEntry(string Name, byte FileType, byte StartTrack, byte StartSector, ushort Blocks);
    }
}
