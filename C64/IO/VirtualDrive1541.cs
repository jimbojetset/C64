

namespace C64.IO
{

    internal sealed class VirtualDrive1541
    {
        private D64Image? image;
        private string? lastLoadedName;

        public bool HasMedia => image is not null;

        public string? AttachedPath => image?.SourcePath;

        /// <summary>Loads a D64 image and makes it the media currently exposed by virtual drive 8.</summary>
        /// <param name="path">The path of the file to use.</param>
        public void AttachD64(string path)
        {
            image = D64Image.Load(path);
            lastLoadedName = null;
        }

        /// <summary>Removes the D64 image currently exposed by virtual drive 8.</summary>
        public void Eject()
        {
            image = null;
            lastLoadedName = null;
        }

        /// <summary>Lists PRG files on the attached media.</summary>
        public IReadOnlyList<string> ListFiles()
        {
            return image?.ListPrgFiles() ?? [];
        }

        /// <summary>Loads a named PRG or synthesized directory listing from the attached D64 image.</summary>
        /// <param name="requestedName">The C64 filename requested by the caller, or null to select a default.</param>
        /// <param name="prg">Receives the PRG bytes when the load succeeds.</param>
        /// <param name="resolvedName">Receives the resolved C64 filename when the operation succeeds.</param>
        public bool TryLoadPrg(string? requestedName, out byte[] prg, out string resolvedName)
        {
            prg = [];
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

        /// <summary>Reads one track and sector from the attached D64 image when media is present.</summary>
        /// <param name="track">The disk track number.</param>
        /// <param name="sector">The disk sector number.</param>
        /// <param name="sectorBytes">Receives the sector bytes when the read succeeds.</param>
        public bool TryReadSector(int track, int sector, out byte[] sectorBytes)
        {
            sectorBytes = [];
            return image?.TryReadSector(track, sector, out sectorBytes) == true;
        }

        /// <summary>Determines whether a load name requests the disk directory.</summary>
        /// <param name="requestedName">The C64 filename requested by the caller, or null to select a default.</param>
        private static bool IsDirectoryRequest(string? requestedName)
        {
            if (string.IsNullOrWhiteSpace(requestedName))
                return false;

            string normalized = requestedName.Trim().Trim('"', '\'').ToUpperInvariant();
            return normalized == "$" || normalized.StartsWith("$=", StringComparison.Ordinal);
        }
    }
}
