// ============================================================================
// Project:     C64
// File:        SoftwareDirectory.cs
// Description: Helper for locating or creating the bundled Software directory
//              from runtime and repository paths.
// Author:      James Booth
// Created:     2025
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Commodore 64 and related ROMs are property of their respective
//              rights holders. This emulator is for educational purposes only.
// ============================================================================

namespace C64
{

    /// <summary>
    /// Locates the bundled software directory from the current working directory or nearby repository paths.
    /// </summary>
    internal static class SoftwareDirectory
    {

        /// <summary>Finds the bundled software directory.</summary>
        /// <returns>The selected or resolved string value, or null when no value is available.</returns>
        public static string? Find()
        {
            string[] candidates =
            {
                Path.Combine(Environment.CurrentDirectory, "Software"),
                Path.Combine(AppContext.BaseDirectory, "Software"),
                Path.Combine(Environment.CurrentDirectory, "C64", "Software"),
            };

            return candidates.FirstOrDefault(Directory.Exists);
        }

        /// <summary>Finds the software directory or throws if missing.</summary>
        /// <returns>The string value produced by the operation.</returns>
        public static string Ensure()
        {
            string? existing = Find();
            if (existing is not null)
                return existing;

            string created = Path.Combine(Environment.CurrentDirectory, "Software");
            Directory.CreateDirectory(created);
            return created;
        }
    }
}
