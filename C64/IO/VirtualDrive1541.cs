namespace C64
{

    /// <summary>
    /// Wraps an attached D64 image and exposes the file and sector operations used by the IEC bus and load traps.
    /// </summary>
    internal sealed class VirtualDrive1541
    {
        private D64Image? image;
        private string? lastLoadedName;

        /// <summary>Gets whether drive media is attached.</summary>
        public bool HasMedia => image is not null;

        /// <summary>Gets the path of the attached disk image.</summary>
        public string? AttachedPath => image?.SourcePath;

        /// <summary>Attaches a D64 disk image.</summary>
        /// <param name="path">The path of the file to use.</param>
        public void AttachD64(string path)
        {
            image = D64Image.Load(path);
            lastLoadedName = null;
        }

        /// <summary>Ejects the attached media.</summary>
        public void Eject()
        {
            image = null;
            lastLoadedName = null;
        }

        /// <summary>Lists PRG files on the attached media.</summary>
        /// <returns>The available file names.</returns>
        public IReadOnlyList<string> ListFiles()
        {
            return image?.ListPrgFiles() ?? Array.Empty<string>();
        }

        /// <summary>Attempts to load prg.</summary>
        /// <param name="requestedName">The C64 filename requested by the caller, or null to select a default.</param>
        /// <param name="prg">Receives the PRG bytes when the load succeeds.</param>
        /// <param name="resolvedName">Receives the resolved C64 filename when the operation succeeds.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
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

        /// <summary>Attempts to read sector.</summary>
        /// <param name="track">The disk track number.</param>
        /// <param name="sector">The disk sector number.</param>
        /// <param name="sectorBytes">Receives the sector bytes when the read succeeds.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        public bool TryReadSector(int track, int sector, out byte[] sectorBytes)
        {
            sectorBytes = Array.Empty<byte>();
            return image?.TryReadSector(track, sector, out sectorBytes) == true;
        }

        /// <summary>Determines whether a load name requests the disk directory.</summary>
        /// <param name="requestedName">The C64 filename requested by the caller, or null to select a default.</param>
        /// <returns>True when the operation succeeds; otherwise, false.</returns>
        private static bool IsDirectoryRequest(string? requestedName)
        {
            if (string.IsNullOrWhiteSpace(requestedName))
                return false;

            string normalized = requestedName.Trim().Trim('"', '\'').ToUpperInvariant();
            return normalized == "$" || normalized.StartsWith("$=", StringComparison.Ordinal);
        }
    }
}
