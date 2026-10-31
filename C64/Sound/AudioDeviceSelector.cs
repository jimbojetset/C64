

using ImGuiNET;
using System.Numerics;

namespace C64.Sound
{

    internal sealed class AudioDeviceSelector
    {
        private const string PopupId = "Select Audio Device";

        private readonly List<string> _devices;
        private bool _needsOpen;
        private bool _completed;
        private string? _selectedDeviceName;

        private int _currentIndex = 0;

        /// <summary>Creates an ImGui audio-device picker over the available SDL playback devices.</summary>
        /// <param name="devices">The audio device names to show in the picker.</param>
        public AudioDeviceSelector(List<string> devices)
        {
            _devices = devices;
            _needsOpen = _devices.Count > 0;
            _completed = _devices.Count == 0;
        }

        public bool IsCompleted => _completed;

        public string? SelectedDeviceName => _selectedDeviceName;

        /// <summary>Draws the modal device list and handles keyboard or pointer selection within an active ImGui frame.</summary>
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

                    if (i == 0)
                        ImGui.SetItemDefaultFocus();
                }

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

                if (ImGui.IsKeyPressed(ImGuiKey.UpArrow, false) && _currentIndex > 0)
                    _currentIndex--;
                if (ImGui.IsKeyPressed(ImGuiKey.DownArrow, false) && _currentIndex < _devices.Count - 1)
                    _currentIndex++;

                if (ImGui.IsKeyPressed(ImGuiKey.Enter, false))
                {
                    Select(_currentIndex);
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
        }

        /// <summary>Completes the picker with the SDL playback device at the selected index.</summary>
        /// <param name="index">The item index to select.</param>
        private void Select(int index)
        {
            _selectedDeviceName = _devices[index];
            _completed = true;
        }
    }
}
