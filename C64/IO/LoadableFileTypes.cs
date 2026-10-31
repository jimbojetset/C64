

namespace C64.IO
{

    internal static class LoadableFileTypes
    {

        public static readonly string[] Extensions =
        [
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
        ];

        private static readonly HashSet<string> ExtensionSet = new(Extensions, StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets whether the supplied path has a loadable extension.</summary>
        /// <param name="path">The file path to test.</param>
        public static bool IsLoadable(string path) => ExtensionSet.Contains(Path.GetExtension(path));

        public static string WindowsDialogFilter
        {
            get
            {
                string patterns = string.Join(";", Extensions.Select(ext => "*" + ext));
                return $"C64 files ({patterns})|{patterns}|All files (*.*)|*.*";
            }
        }

        public static string LinuxPatternList => string.Join(" ", Extensions.Select(ext => "*" + ext));
    }
}
