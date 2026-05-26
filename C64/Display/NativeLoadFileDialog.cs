// ============================================================================
// Project:     C64
// File:        NativeLoadFileDialog.cs
// Description: Platform native host-file picker for loading C64 software,
//              cartridge, disk, tape, and SID files from arbitrary locations.
// Author:      James Booth
// Created:     2026
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Commodore 64 and related ROMs are property of their respective
//              rights holders. This emulator is for educational purposes only.
// ============================================================================

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace C64
{
    /// <summary>Shows a platform-native file picker for selecting loadable C64 files.</summary>
    internal static class NativeLoadFileDialog
    {
        /// <summary>Shows the native file picker and returns the selected loadable file path.</summary>
        /// <returns>The selected file path, or null when the user cancels or no supported picker is available.</returns>
        public static string? Prompt()
        {
            string? path = null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                path = PromptMac();
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                path = PromptWindows();
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                path = PromptLinux();

            if (string.IsNullOrWhiteSpace(path))
                return null;

            path = path.Trim();
            if (!File.Exists(path))
                return null;

            if (!LoadableFileTypes.IsLoadable(path))
            {
                Console.Error.WriteLine($"Unsupported file type: {Path.GetExtension(path)}");
                return null;
            }

            return path;
        }

        private static string? PromptMac()
        {
            var psi = new ProcessStartInfo("osascript")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("set c64File to choose file with prompt \"Choose C64 file to load\"");
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("POSIX path of c64File");

            return RunPickerProcess(psi);
        }

        private static string? PromptWindows()
        {
            string command =
                "Add-Type -AssemblyName System.Windows.Forms; " +
                "$dialog = New-Object System.Windows.Forms.OpenFileDialog; " +
                "$dialog.Title = 'Choose C64 file to load'; " +
                "$dialog.Filter = '" + LoadableFileTypes.WindowsDialogFilter + "'; " +
                "if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { [Console]::WriteLine($dialog.FileName) }";

            var psi = new ProcessStartInfo("powershell")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-STA");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);

            return RunPickerProcess(psi);
        }

        private static string? PromptLinux()
        {
            string? path = PromptZenity();
            return string.IsNullOrWhiteSpace(path) ? PromptKDialog() : path;
        }

        private static string? PromptZenity()
        {
            var psi = new ProcessStartInfo("zenity")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            psi.ArgumentList.Add("--file-selection");
            psi.ArgumentList.Add("--title=Choose C64 file to load");
            psi.ArgumentList.Add("--file-filter=C64 files | " + LoadableFileTypes.LinuxPatternList);
            psi.ArgumentList.Add("--file-filter=All files | *");

            return RunPickerProcess(psi);
        }

        private static string? PromptKDialog()
        {
            var psi = new ProcessStartInfo("kdialog")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            psi.ArgumentList.Add("--getopenfilename");
            psi.ArgumentList.Add(".");
            psi.ArgumentList.Add("C64 files (" + LoadableFileTypes.LinuxPatternList + ")");

            return RunPickerProcess(psi);
        }

        private static string? RunPickerProcess(ProcessStartInfo psi)
        {
            try
            {
                using var process = Process.Start(psi);
                if (process is null)
                    return null;

                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                    return null;

                return output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return null;
            }
        }
    }
}