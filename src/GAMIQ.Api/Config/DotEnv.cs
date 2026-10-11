namespace GAMIQ.Api.Config;

// Small .env reader so we don't need an extra package. Looks in the current folder and a few
// parents (dotnet run is usually started from src/GAMIQ.Api), and never overrides a variable
// that is already set in the shell.
public static class DotEnv
{
    public static void Load()
    {
        var dir = Directory.GetCurrentDirectory();

        for (var i = 0; i < 5 && dir is not null; i++)
        {
            var path = Path.Combine(dir, ".env");
            if (File.Exists(path))
            {
                ReadFile(path);
                return;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }
    }

    private static void ReadFile(string path)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"');

            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
