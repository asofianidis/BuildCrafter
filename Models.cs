using System.Text.Json.Serialization;
using UnityEngine;

namespace NevarisBuildCrafter;

internal static class BuildSlots
{
    internal static readonly string[] Equipment =
    {
        "head", "back", "mainhand", "legs", "acc1",
        "eyewear", "chest", "offhand", "feet", "acc2"
    };

    internal static readonly string[] Artifacts = { "rune", "jewel", "scroll", "relic" };

    internal static string Label(string slot) => slot switch
    {
        "head" => "Head",
        "back" => "Back",
        "mainhand" => "Main Hand",
        "legs" => "Legs",
        "acc1" => "Accessory I",
        "eyewear" => "Eyewear",
        "chest" => "Chest",
        "offhand" => "Off Hand",
        "feet" => "Feet",
        "acc2" => "Accessory II",
        "rune" => "Rune",
        "jewel" => "Jewel",
        "scroll" => "Scroll",
        "relic" => "Relic",
        _ => slot ?? string.Empty
    };
}

internal sealed class BuildDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New build";
    public string Class { get; set; } = "Warrior";
    public string BaseClass { get; set; } = string.Empty;
    public int BaseLevel { get; set; } = 100;
    public int JobLevel { get; set; } = 50;
    public Dictionary<string, int> Attributes { get; set; } = NewAttributes();
    public Dictionary<string, PlannedItem> Equipment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PlannedArtifact> Artifacts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Skills { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Notes { get; set; } = string.Empty;
    public string SourceCommunityId { get; set; } = string.Empty;
    public string SourceAuthor { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    internal static Dictionary<string, int> NewAttributes() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["STR"] = 1,
        ["VIT"] = 1,
        ["AGI"] = 1,
        ["DEX"] = 1,
        ["INT"] = 1,
        ["LUK"] = 1
    };

    internal void Normalize()
    {
        SchemaVersion = Math.Max(1, SchemaVersion);
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id;
        Name = string.IsNullOrWhiteSpace(Name) ? "Unnamed build" : Name.Trim();
        Class = string.IsNullOrWhiteSpace(Class) ? "Warrior" : Class.Trim();
        BaseClass = BaseClass?.Trim() ?? string.Empty;
        BaseLevel = Math.Clamp(BaseLevel, 1, 150);
        JobLevel = Math.Clamp(JobLevel, 1, 99);
        Attributes ??= NewAttributes();
        foreach (string key in NewAttributes().Keys)
            Attributes[key] = Math.Clamp(Attributes.TryGetValue(key, out int value) ? value : 1, 1, 99);
        Equipment = Equipment is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, PlannedItem>(Equipment, StringComparer.OrdinalIgnoreCase);
        Artifacts = Artifacts is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, PlannedArtifact>(Artifacts, StringComparer.OrdinalIgnoreCase);
        Skills = Skills is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(Skills, StringComparer.OrdinalIgnoreCase);
        Notes ??= string.Empty;
        SourceCommunityId ??= string.Empty;
        SourceAuthor ??= string.Empty;
    }

    internal BuildDocument EditableCopy()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(this, BuildStorage.JsonOptions);
        BuildDocument copy = System.Text.Json.JsonSerializer.Deserialize<BuildDocument>(json, BuildStorage.JsonOptions)
            ?? new BuildDocument();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = Name.EndsWith(" copy", StringComparison.OrdinalIgnoreCase) ? Name : Name + " copy";
        copy.UpdatedUtc = DateTime.UtcNow;
        copy.Normalize();
        return copy;
    }
}

internal class PlannedItem
{
    public string ItemId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Refine { get; set; }
    public List<string> Cards { get; set; } = new();
    public List<ImportedSubstat> Substats { get; set; } = new();
}

internal sealed class PlannedArtifact : PlannedItem
{
    public string GemId { get; set; } = string.Empty;
    public int GemRefine { get; set; }
}

internal sealed class ImportedSubstat
{
    public string Stat { get; set; } = string.Empty;
    public float Base { get; set; }
    public string Quality { get; set; } = string.Empty;
}

internal enum CatalogKind
{
    Equipment,
    Artifact,
    Card,
    Gem,
    Skill
}

internal sealed class BuildCatalogEntry
{
    internal string Id = string.Empty;
    internal string Name = string.Empty;
    internal string Subtitle = string.Empty;
    internal string Description = string.Empty;
    internal string SearchText = string.Empty;
    internal CatalogKind Kind;
    internal string Slot = string.Empty;
    internal int MaxLevel;
    internal int SocketCount;
    internal Sprite Icon;
    internal List<string> StatLines = new();
    internal Dictionary<string, int> Requirements = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class CommunityBuildSummary
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("class")]
    public string Class { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
