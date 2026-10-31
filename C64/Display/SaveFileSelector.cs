

using ImGuiNET;
using System.Numerics;

namespace C64.Display
{

    internal sealed class SaveFileSelector
    {
        private const string PopupId = "Save Program";

        private string _filename;
        private bool _needsOpen = true;
        private bool _completed;
        private bool _focusName = true;
        private string? _selectedFilename;
        private string? _error;

        /// <summary>Creates a modal PRG filename selector seeded with the suggested C64 filename.</summary>
        /// <param name="defaultFilename">The filename initially offered to the user.</param>
        public SaveFileSelector(string defaultFilename)
        {
            _filename = defaultFilename;
        }

        public bool IsCompleted => _completed;

        public string? SelectedFilename => _selectedFilename;

        /// <summary>Draws the save-name modal and handles accept or cancel input for the current frame.</summary>
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
                ImGui.Text("Save BASIC program as:");
                ImGui.Separator();

                ImGui.SetNextItemWidth(420);
                if (_focusName)
                {
                    ImGui.SetKeyboardFocusHere();
                    _focusName = false;
                }

                if (ImGui.InputText("##Filename", ref _filename, 256, ImGuiInputTextFlags.EnterReturnsTrue))
                    TryAccept();

                if (!string.IsNullOrWhiteSpace(_error))
                    ImGui.TextDisabled(_error);

                ImGui.Spacing();

                if (ImGui.Button("Save", new Vector2(100, 0)))
                    TryAccept();

                ImGui.SameLine();

                if (ImGui.Button("Cancel", new Vector2(100, 0)))
                {
                    _completed = true;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }
        }

        /// <summary>Normalizes the entered PRG name and completes the selection when it is valid.</summary>
        private void TryAccept()
        {
            string filename = NormalizeFilename(_filename);
            if (string.IsNullOrWhiteSpace(filename))
            {
                _error = "Enter a file name.";
                return;
            }

            _selectedFilename = filename;
            _completed = true;
            ImGui.CloseCurrentPopup();
        }

        /// <summary>Normalizes a user-entered filename for PRG saving.</summary>
        /// <param name="raw">The raw bytes to decode.</param>
        private static string NormalizeFilename(string raw)
        {
            string name = raw.Trim().Trim('"', '\'');
            name = Path.GetFileName(name);

            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');

            if (string.IsNullOrWhiteSpace(name))
                return "";

            if (!name.EndsWith(".prg", StringComparison.OrdinalIgnoreCase))
                name += ".prg";

            return name;
        }
    }
}
