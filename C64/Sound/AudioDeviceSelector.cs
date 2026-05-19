// ============================================================================
// Project:     C64
// File:        AudioDeviceSelector.cs
// Description: ImGui modal selector for choosing an SDL audio output device
//              from the available device list.
// Author:      James Booth
// Created:     2025
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Commodore 64 and related ROMs are property of their respective
//              rights holders. This emulator is for educational purposes only.
// ============================================================================

using ImGuiNET;
using System.Numerics;

namespace C64
{
    /// <summary>
    /// Modal ImGui popup that prompts the user to choose an audio output device.
    /// Selection is made by clicking an entry, using arrow keys plus Enter, or
    /// pressing the matching number key (0-9 for the first ten devices).
    /// </summary>
    internal sealed class AudioDeviceSelector
    {
        private const string PopupId = "Select Audio Device";

        private readonly List<string> _devices;
        private bool _needsOpen;
        private bool _completed;
        private string? _selectedDeviceName;

        private int _currentIndex = 0;  // Default to index 0

        /// <summary>Initializes a new AudioDeviceSelector instance.</summary>
        /// <param name="devices">The audio device names to show in the picker.</param>
        public AudioDeviceSelector(List<string> devices)
        {
            _devices = devices;
            _needsOpen = _devices.Count > 0;
            _completed = _devices.Count == 0;
        }

        /// <summary>True once the user has made a selection (or no prompt was needed).</summary>
        public bool IsCompleted => _completed;

        /// <summary>The selected device name, or null to use the system default.</summary>
        public string? SelectedDeviceName => _selectedDeviceName;

        /// <summary>Render the modal. Must be called inside an active ImGui frame.</summary>
        public void Draw()
        {
            if (_completed)
                return;

            if (_needsOpen)
            {
                ImGui.OpenPopup(PopupId);
                _needsOpen = false;
            }

            Vector2 center = ImGui.GetMainViewport().GetCenter();
            ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

            if (ImGui.BeginPopupModal(PopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove))
            {
                ImGui.Text("Select audio output device:");
                ImGui.Separator();

                for (int i = 0; i < _devices.Count; i++)
                {
                    string label = $"[{i}] {_devices[i]}";
                    bool isSelected = (i == _currentIndex);
                    if (ImGui.Selectable(label, isSelected))
                    {
                        Select(i);
                        ImGui.CloseCurrentPopup();
                        ImGui.EndPopup();
                        return;
                    }

                    // Set keyboard focus to the first (default) item on first frame
                    if (i == 0)
                        ImGui.SetItemDefaultFocus();
                }

                // Number-key selection (matches the previous command-line behaviour).
                int maxKey = Math.Min(10, _devices.Count);
                for (int i = 0; i < maxKey; i++)
                {
                    if (ImGui.IsKeyPressed(ImGuiKey._0 + i, false) ||
                        ImGui.IsKeyPressed(ImGuiKey.Keypad0 + i, false))
                    {
                        Select(i);
                        ImGui.CloseCurrentPopup();
                        break;
                    }
                }
                // Up/Down arrow keys to navigate selection
                if (ImGui.IsKeyPressed(ImGuiKey.UpArrow, false) && _currentIndex > 0)
                    _currentIndex--;
                if (ImGui.IsKeyPressed(ImGuiKey.DownArrow, false) && _currentIndex < _devices.Count - 1)
                    _currentIndex++;

                // Enter to confirm the current selection
                if (ImGui.IsKeyPressed(ImGuiKey.Enter, false))
                {
                    Select(_currentIndex);
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
        }

        /// <summary>Accepts the item at the selected index.</summary>
        /// <param name="index">The item index to select.</param>
        private void Select(int index)
        {
            _selectedDeviceName = _devices[index];
            _completed = true;
        }
    }
}