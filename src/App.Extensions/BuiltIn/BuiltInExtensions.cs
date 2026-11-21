namespace App.Extensions.BuiltIn;

/// <summary>Nettoie les anciennes extensions de démonstration locales si présentes.</summary>
public static class LegacyDemoCleaner
{
    private static readonly string[] DemoIds =
        ["keyce.hello", "keyce.git-helper", "keyce.workspace-info"];

    public static void Clean(string catalogRoot, string extensionsRoot)
    {
        foreach (string id in DemoIds)
        {
            TryDeleteDirectory(Path.Combine(catalogRoot, id));
            TryDeleteDirectory(Path.Combine(extensionsRoot, id));
        }

        string marker = Path.Combine(catalogRoot, ".seeded");
        if (File.Exists(marker))
        {
            try { File.Delete(marker); }
            catch (IOException) { }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
    }
}
