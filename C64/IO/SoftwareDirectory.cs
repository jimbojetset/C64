namespace C64
{

    /// <summary>
    /// Locates the bundled software directory from the current working directory or nearby repository paths.
    /// </summary>
    internal static class SoftwareDirectory
    {

        /// <summary>Finds the bundled software directory.</summary>
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
