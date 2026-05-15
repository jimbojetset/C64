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
            17,17,17,17,17
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
                if (e.FileType != 0x82) // PRG
                    continue;
                files.Add(e.Name);
            }
            return files;
        }

        public bool TryLoadPrg(string? requestedName, out byte[] prgBytes, out string resolvedName)
        {
            prgBytes = Array.Empty<byte>();
            resolvedName = string.Empty;

            var entries = ReadDirectoryEntries();
            if (entries.Count == 0)
                return false;

            DirectoryEntry? selected = null;
            if (string.IsNullOrWhiteSpace(requestedName))
            {
                selected = entries.FirstOrDefault(e => e.FileType == 0x82);
            }
            else
            {
                string wanted = NormalizeName(requestedName);
                selected = entries.FirstOrDefault(e => e.FileType == 0x82 && NormalizeName(e.Name) == wanted);
                selected ??= entries.FirstOrDefault(e => e.FileType == 0x82 && NormalizeName(e.Name).StartsWith(wanted, StringComparison.Ordinal));
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
                    int used = nextSector;
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
                    int eoff = off + 2 + i * 32;
                    byte fileType = (byte)(raw[eoff + 2] & 0x87);
                    if (fileType == 0)
                        continue;

                    byte st = raw[eoff + 3];
                    byte ss = raw[eoff + 4];
                    string name = DecodePetsciiName(raw, eoff + 5, 16);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    list.Add(new DirectoryEntry(name, fileType, st, ss));
                }

                track = nextTrack;
                sector = nextSector;
            }

            return list;
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

        private sealed record DirectoryEntry(string Name, byte FileType, byte StartTrack, byte StartSector);
    }
}
