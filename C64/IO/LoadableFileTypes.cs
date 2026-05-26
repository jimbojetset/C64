// ============================================================================
// Project:     C64
// File:        LoadableFileTypes.cs
// Description: Shared host-file extension list for load dialogs and loaders.
// Author:      James Booth
// Created:     2026
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Commodore 64 and related ROMs are property of their respective
//              rights holders. This emulator is for educational purposes only.
// ============================================================================

namespace C64
{
    /// <summary>Defines the host file extensions supported by the emulator loader.</summary>
    internal static class LoadableFileTypes
    {
        /// <summary>Gets the loadable host-file extensions, including the leading dot.</summary>
        public static readonly string[] Extensions =
        {
            ".bas",
            ".crt",
            ".d64",
            ".prg",
            ".psid",
            ".rsid",
            ".sid",
            ".t64",
            ".tap",
            ".txt"
        };

        private static readonly HashSet<string> ExtensionSet = new(Extensions, StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets whether the supplied path has a loadable extension.</summary>
        /// <param name="path">The file path to test.</param>
        /// <returns>True when the loader supports the file extension.</returns>
        public static bool IsLoadable(string path) => ExtensionSet.Contains(Path.GetExtension(path));

        /// <summary>Gets a Windows file-dialog filter for loadable files.</summary>
        public static string WindowsDialogFilter
        {
            get
            {
                string patterns = string.Join(";", Extensions.Select(ext => "*" + ext));
                return $"C64 files ({patterns})|{patterns}|All files (*.*)|*.*";
            }
        }

        /// <summary>Gets a shell-style file pattern list for Linux dialog helpers.</summary>
        public static string LinuxPatternList => string.Join(" ", Extensions.Select(ext => "*" + ext));
    }
}