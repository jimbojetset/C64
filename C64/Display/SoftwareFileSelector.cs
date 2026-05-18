using System.Numerics;
using ImGuiNET;

namespace C64
{

    /// <summary>
    /// Modal ImGui popup that lets the user choose a loadable file from the
    /// bundled Software directory.
    /// </summary>
    internal sealed class SoftwareFileSelector
    {
        private const string PopupId = "Load Software";

        private readonly IReadOnlyList<SoftwareFileEntry> _files;
        private bool _needsOpen = true;
        private bool _completed;
        private bool _scrollToCurrent = true;
        private int _currentIndex;
        private string? _selectedPath;

        /// <summary>Initializes a new SoftwareFileSelector instance.</summary>
        public SoftwareFileSelector(IReadOnlyList<SoftwareFileEntry> files)
        {
            _files = files;
        }

        /// <summary>Gets whether the selector has completed.</summary>
        public bool IsCompleted => _completed;

        /// <summary>Gets the selected file path.</summary>
        public string? SelectedPath => _selectedPath;

        /// <summary>Draws this selector window.</summary>
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
                ImGui.Text("Select software to load:");
                ImGui.Separator();

                if (_files.Count == 0)
                {
                    ImGui.TextDisabled("No files found in the Software directory.");
                }
                else
                {
                    DrawFileList();
                    HandleKeyboard();
                }

                ImGui.EndPopup();
            }
        }

        /// <summary>Draws the scrollable bundled-software file list.</summary>
        private void DrawFileList()
        {
            Vector2 listSize = new Vector2(500, 280);
            if (ImGui.BeginChild("SoftwareFileList", listSize, ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar))
            {
                for (int i = 0; i < _files.Count; i++)
                {
                    SoftwareFileEntry file = _files[i];
                    bool isSelected = i == _currentIndex;
                    string label = $"{file.DisplayName}  [{file.Extension}]";

                    if (ImGui.Selectable(label, isSelected))
                    {
                        Select(i);
                        ImGui.CloseCurrentPopup();
                        ImGui.EndChild();
                        return;
                    }

                    if (isSelected)
                    {
                        ImGui.SetItemDefaultFocus();
                        if (_scrollToCurrent)
                            ImGui.SetScrollHereY(0.5f);
                    }
                }

                _scrollToCurrent = false;
            }

            ImGui.EndChild();
        }

        /// <summary>Handles keyboard navigation and activation for the selector.</summary>
        private void HandleKeyboard()
        {
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow, false) && _currentIndex > 0)
            {
                _currentIndex--;
                _scrollToCurrent = true;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow, false) && _currentIndex < _files.Count - 1)
            {
                _currentIndex++;
                _scrollToCurrent = true;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.PageUp, false))
            {
                _currentIndex = Math.Max(0, _currentIndex - 10);
                _scrollToCurrent = true;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.PageDown, false))
            {
                _currentIndex = Math.Min(_files.Count - 1, _currentIndex + 10);
                _scrollToCurrent = true;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.Home, false))
            {
                _currentIndex = 0;
                _scrollToCurrent = true;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.End, false))
            {
                _currentIndex = _files.Count - 1;
                _scrollToCurrent = true;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.Enter, false))
            {
                Select(_currentIndex);
                ImGui.CloseCurrentPopup();
            }
        }

        /// <summary>Accepts the item at the selected index.</summary>
        private void Select(int index)
        {
            _selectedPath = _files[index].Path;
            _completed = true;
        }
    }

    /// <summary>Represents one bundled software file shown in the picker.</summary>
    internal sealed record SoftwareFileEntry(string Path, string DisplayName, string Extension);
}
