namespace C64
{
    internal static class SoftwareDirectory
    {
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
