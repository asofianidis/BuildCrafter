using System.Text.Json;
using BepInEx;

namespace NevarisBuildCrafter;

internal static class BuildStorage
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static string _directory = string.Empty;

    internal static string DirectoryPath => _directory;

    internal static void Initialize()
    {
        _directory = Path.GetFullPath(Path.Combine(Paths.ConfigPath, "BuildCrafter", "builds"));
        Directory.CreateDirectory(_directory);
        Plugin.Info($"Local builds are stored in {_directory}");
    }

    internal static void Save(BuildDocument build)
    {
        ArgumentNullException.ThrowIfNull(build);
        build.Normalize();
        build.UpdatedUtc = DateTime.UtcNow;
        Directory.CreateDirectory(_directory);

        string safeId = new string(build.Id.Where(char.IsLetterOrDigit).ToArray());
        if (safeId.Length < 8)
        {
            safeId = Guid.NewGuid().ToString("N");
            build.Id = safeId;
        }

        string path = Path.GetFullPath(Path.Combine(_directory, safeId + ".json"));
        EnsureInsideStorage(path);
        string temporary = path + ".tmp";
        string json = JsonSerializer.Serialize(build, JsonOptions);
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, true);
    }

    internal static IReadOnlyList<BuildDocument> LoadAll()
    {
        Directory.CreateDirectory(_directory);
        var builds = new List<BuildDocument>();
        foreach (string path in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                BuildDocument build = JsonSerializer.Deserialize<BuildDocument>(File.ReadAllText(path), JsonOptions);
                if (build is null)
                    continue;
                build.Normalize();
                builds.Add(build);
            }
            catch (Exception ex)
            {
                Plugin.Warning($"Skipped unreadable build file '{Path.GetFileName(path)}': {ex.Message}");
            }
        }

        return builds.OrderByDescending(build => build.UpdatedUtc).ToList();
    }

    private static void EnsureInsideStorage(string path)
    {
        string root = _directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Build path resolved outside the local build directory.");
    }
}
