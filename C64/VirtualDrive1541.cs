namespace C64
{
    internal sealed class VirtualDrive1541
    {
        private D64Image? image;
        private string? lastLoadedName;

        public bool HasMedia => image is not null;
        public string? AttachedPath => image?.SourcePath;

        public void AttachD64(string path)
        {
            image = D64Image.Load(path);
            lastLoadedName = null;
        }

        public void Eject()
        {
            image = null;
            lastLoadedName = null;
        }

        public IReadOnlyList<string> ListFiles()
        {
            return image?.ListPrgFiles() ?? Array.Empty<string>();
        }

        public bool TryLoadPrg(string? requestedName, out byte[] prg, out string resolvedName)
        {
            prg = Array.Empty<byte>();
            resolvedName = string.Empty;

            if (image is null)
                return false;

            string? wanted = requestedName;
            if (string.IsNullOrWhiteSpace(wanted))
                wanted = lastLoadedName;

            if (IsDirectoryRequest(wanted))
            {
                if (!image.TryLoadDirectory(out prg))
                    return false;

                resolvedName = "$";
                lastLoadedName = resolvedName;
                return true;
            }

            if (!image.TryLoadPrg(wanted, out prg, out resolvedName))
                return false;

            lastLoadedName = resolvedName;
            return true;
        }

        public bool TryReadSector(int track, int sector, out byte[] sectorBytes)
        {
            sectorBytes = Array.Empty<byte>();
            return image?.TryReadSector(track, sector, out sectorBytes) == true;
        }

        private static bool IsDirectoryRequest(string? requestedName)
        {
            if (string.IsNullOrWhiteSpace(requestedName))
                return false;

            string normalized = requestedName.Trim().Trim('"', '\'').ToUpperInvariant();
            return normalized == "$" || normalized.StartsWith("$=", StringComparison.Ordinal);
        }
    }
}
