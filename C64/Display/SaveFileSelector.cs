using System.Numerics;
using ImGuiNET;

namespace C64
{
    /// <summary>
    /// Modal ImGui popup that prompts for a PRG filename inside Software.
    /// </summary>
    internal sealed class SaveFileSelector
    {
        private const string PopupId = "Save Program";

        private string _filename;
        private bool _needsOpen = true;
        private bool _completed;
        private bool _focusName = true;
        private string? _selectedFilename;
        private string? _error;

        public SaveFileSelector(string defaultFilename)
        {
            _filename = defaultFilename;
        }

        public bool IsCompleted => _completed;

        public string? SelectedFilename => _selectedFilename;

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
