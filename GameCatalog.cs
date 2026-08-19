using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NevarisBuildCrafter;

internal static class GameCatalog
{
    private static readonly List<BuildCatalogEntry> Entries = new();
    private static readonly Dictionary<string, BuildCatalogEntry> ByAnyId =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> SkillsByClass =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, bool> SkillRequirementsDisabledByClass =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SubstatTypeNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ParentClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Wizard"] = "Mage",
        ["Shinobi"] = "Rogue",
        ["Priest"] = "Acolyte",
        ["Paladin"] = "Knight",
        ["Gunslinger"] = "Scout",
        ["Necromancer"] = "Summoner",
        ["Berserker"] = "Warrior"
    };
    private static readonly HashSet<string> BaseClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Acolyte", "Knight", "Mage", "Rogue", "Scout", "Summoner", "Warrior"
    };

    internal static IReadOnlyList<BuildCatalogEntry> All => Entries;

    internal static void Rebuild()
    {
        Entries.Clear();
        ByAnyId.Clear();
        SkillsByClass.Clear();
        SkillRequirementsDisabledByClass.Clear();
        SubstatTypeNames.Clear();
        GameServerConfig config = App.ServerConfig;
        if (config is null)
            return;

        AddEquipment(config);
        AddArtifacts(config);
        AddCards(config);
        AddGems(config);
        AddSubstatTypes(config);
        AddSkills(config);

        Entries.Sort((left, right) =>
        {
            int kind = left.Kind.CompareTo(right.Kind);
            return kind != 0 ? kind : StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
        });

        foreach (BuildCatalogEntry entry in Entries)
        {
            AddLookup(entry.Id, entry);
            AddLookup(entry.Name, entry);
            AddLookup(Slug(entry.Id), entry);
            AddLookup(Slug(entry.Name), entry);
        }
        Plugin.Info($"Indexed {Entries.Count:N0} build choices from the installed game data.");
    }

    internal static BuildCatalogEntry Resolve(string idOrName, CatalogKind? kind = null)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
            return null;
        if (ByAnyId.TryGetValue(idOrName, out BuildCatalogEntry direct)
            && (!kind.HasValue || direct.Kind == kind.Value))
            return direct;
        string slug = Slug(idOrName);
        return ByAnyId.TryGetValue(slug, out BuildCatalogEntry normalized)
            && (!kind.HasValue || normalized.Kind == kind.Value) ? normalized : null;
    }

    internal static IReadOnlyList<BuildCatalogEntry> Find(CatalogKind kind, string slot,
        string query, string className = null, string baseClass = null)
    {
        IEnumerable<BuildCatalogEntry> selection = Entries.Where(entry => entry.Kind == kind);
        if (kind == CatalogKind.Equipment)
            selection = selection.Where(entry => FitsEquipmentSlot(entry.Slot, slot));
        else if (kind == CatalogKind.Artifact && !string.IsNullOrWhiteSpace(slot))
            selection = selection.Where(entry => entry.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase));
        else if (kind == CatalogKind.Skill && !string.IsNullOrWhiteSpace(className))
        {
            HashSet<string> ids = SkillIdsFor(className, baseClass);
            if (ids.Count > 0)
                selection = selection.Where(entry => ids.Contains(entry.Id));
            else
                selection = Enumerable.Empty<BuildCatalogEntry>();
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            string term = query.Trim();
            selection = selection.Where(entry => entry.SearchText.Contains(term,
                StringComparison.OrdinalIgnoreCase));
        }
        return selection.Take(250).ToList();
    }

    internal static string ParentClassOf(string className)
    {
        if (!string.IsNullOrWhiteSpace(className)
            && Enum.TryParse(className, true, out Archetype archetype)
            && archetype != Archetype.Weaver
            && GameRuntimeExtensions.IsAdvancedClass(archetype))
        {
            Archetype required = Formula.GetRequiredClass(archetype);
            if (required != Archetype.Novice)
                return required.ToString();
        }
        return ParentClasses.TryGetValue(className ?? string.Empty, out string parent)
            ? parent : string.Empty;
    }

    internal static bool IsBaseClass(string className) =>
        BaseClasses.Contains(className ?? string.Empty);

    internal static bool IsAdvancedClass(string className)
    {
        if (string.IsNullOrWhiteSpace(className) || IsFreeformClass(className))
            return false;
        return Enum.TryParse(className, true, out Archetype archetype)
            ? GameRuntimeExtensions.IsAdvancedClass(archetype)
            : ParentClasses.ContainsKey(className);
    }

    internal static bool IsFreeformClass(string className) =>
        string.Equals(className, "Weaver", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<string> BaseClassNames() =>
        BaseClasses.OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).ToList();

    internal static int MaxJobLevel(string className)
    {
        if (App.ServerConfig?.Archetypes is not null)
        {
            foreach (ArchetypeConfig config in App.ServerConfig.Archetypes)
            {
                if (config is not null && config.Id.Equals(className, StringComparison.OrdinalIgnoreCase)
                    && config.MaxJobLevel > 0)
                    return config.MaxJobLevel;
            }
        }
        return IsAdvancedClass(className) || IsFreeformClass(className) ? 70 : 50;
    }

    internal static IReadOnlyList<string> AvailableSubstatTypes()
    {
        if (SubstatTypeNames.Count > 0)
            return SubstatTypeNames.OrderBy(Readable, StringComparer.CurrentCultureIgnoreCase).ToList();
        return Enum.GetValues(typeof(StatType)).Cast<StatType>()
            .Where(type => type != StatType.None)
            .Select(type => type.ToString())
            .OrderBy(Readable, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    internal static string FriendlyStatText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        string spaced = Regex.Replace(value, "(?<=[a-z])(?=Mult\\b)", " ",
            RegexOptions.IgnoreCase);
        return Regex.Replace(spaced, "\\bMult\\.?(?=\\s|:|$)", "Multiplier",
            RegexOptions.IgnoreCase);
    }

    internal static string EffectiveBaseClass(string className, string requestedBaseClass)
    {
        string parent = ParentClassOf(className);
        if (!string.IsNullOrWhiteSpace(parent))
            return parent;
        if (IsBaseClass(className))
            return className;
        if (IsFreeformClass(className) && IsBaseClass(requestedBaseClass))
            return requestedBaseClass;
        return string.Empty;
    }

    internal static bool IsAdvancedSkill(string className, string skillId)
    {
        if (!IsAdvancedClass(className))
            return false;
        return SkillsByClass.TryGetValue(className, out HashSet<string> ids) && ids.Contains(skillId);
    }

    internal static bool IsSkillAvailableForClass(string className, string baseClass, string skillId) =>
        SkillIdsFor(className, baseClass).Contains(skillId ?? string.Empty);

    internal static bool RequirementsDisabled(string className, string baseClass, string skillId)
    {
        foreach (string owner in SkillOwners(className, baseClass))
        {
            if (SkillsByClass.TryGetValue(owner, out HashSet<string> ids) && ids.Contains(skillId)
                && SkillRequirementsDisabledByClass.TryGetValue(owner, out bool disabled))
                return disabled;
        }
        return false;
    }

    private static HashSet<string> SkillIdsFor(string className, string baseClass)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string owner in SkillOwners(className, baseClass))
            if (SkillsByClass.TryGetValue(owner, out HashSet<string> ids))
                result.UnionWith(ids);
        return result;
    }

    private static IEnumerable<string> SkillOwners(string className, string baseClass)
    {
        if (IsFreeformClass(className))
        {
            foreach (string baseTree in BaseClassNames())
                yield return baseTree;
            yield return className;
            yield break;
        }

        string effectiveBase = EffectiveBaseClass(className, baseClass);
        if (!string.IsNullOrWhiteSpace(effectiveBase))
            yield return effectiveBase;
        if (IsAdvancedClass(className)
            && !string.Equals(className, effectiveBase, StringComparison.OrdinalIgnoreCase))
            yield return className;
    }

    internal static IReadOnlyList<string> PlayableClasses()
    {
        var result = new List<string>();
        if (App.ServerConfig?.Archetypes is not null)
        {
            foreach (ArchetypeConfig config in App.ServerConfig.Archetypes)
            {
                if (config is null || !config.IsUnlocked
                    || !Enum.TryParse(config.Id, true, out Archetype archetype))
                    continue;
                int numeric = (int)archetype;
                if (numeric >= 0 && numeric <= 31 && !result.Contains(archetype.ToString()))
                    result.Add(archetype.ToString());
            }
            if (result.Count > 0)
                return result;
        }
        foreach (Archetype value in Enum.GetValues(typeof(Archetype)))
        {
            int numeric = (int)value;
            if (numeric < 0 || numeric > 31)
                continue;
            result.Add(value.ToString());
        }
        return result;
    }

    private static void AddEquipment(GameServerConfig config)
    {
        if (config.Equips is null)
            return;
        foreach (EquipConfig item in config.Equips)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id))
                continue;
            string name = NameOf(item, item.Id);
            string type = SafeEquipType(item.Type);
            var restrictions = new List<string>();
            if (item.Archetypes is not null)
                foreach (Archetype archetype in item.Archetypes)
                    restrictions.Add(archetype.ToString());
            string restriction = restrictions.Count > 0 ? string.Join(", ", restrictions) : "All classes";
            var statLines = new List<string>();
            if (item.PrimaryStats is not null)
                foreach (StatValue stat in item.PrimaryStats)
                    AddStatLine(statLines, stat);
            if (item.SecondaryStats is not null)
                foreach (StatValue stat in item.SecondaryStats)
                    AddStatLine(statLines, stat);
            Entries.Add(new BuildCatalogEntry
            {
                Id = item.Id,
                Name = name,
                Kind = CatalogKind.Equipment,
                Slot = SlotFor(item.Type),
                Subtitle = $"{type}  •  {restriction}",
                Description = DescriptionOf(item),
                SearchText = $"{name} {item.Id} {type} {restriction}",
                Icon = item.Sprite,
                StatLines = statLines,
                SocketCount = Math.Max(0, item.Slots)
            });
        }
    }

    private static void AddArtifacts(GameServerConfig config)
    {
        if (config.Artifacts is null)
            return;
        foreach (ArtifactSetConfig set in config.Artifacts)
        {
            if (set is null || string.IsNullOrWhiteSpace(set.Id))
                continue;
            foreach (ArtifactSlot slot in Enum.GetValues(typeof(ArtifactSlot)))
            {
                int index = (int)slot;
                string slotName = slot.ToString();
                string name = $"{NameOf(set, set.Id)} {slotName}";
                var statLines = new List<string>();
                if (set.Individual is not null)
                    foreach (StatValue stat in set.Individual)
                        AddStatLine(statLines, stat, "Piece");
                if (set.PerPiece is not null)
                    foreach (StatValue stat in set.PerPiece)
                        AddStatLine(statLines, stat, "Per piece");
                if (set.PerRefine is not null)
                    foreach (StatValue stat in set.PerRefine)
                        AddStatLine(statLines, stat, "Per refine");
                if (set.FullSet is not null)
                    foreach (StatValue stat in set.FullSet)
                        AddStatLine(statLines, stat, "Full set");
                Entries.Add(new BuildCatalogEntry
                {
                    Id = $"{set.Id}:{slot}",
                    Name = name,
                    Kind = CatalogKind.Artifact,
                    Slot = slotName.ToLowerInvariant(),
                    Subtitle = $"{NameOf(set, set.Id)} set  •  {slotName}",
                    Description = ArtifactDescription(set, index),
                    SearchText = $"{name} {set.Id} {slotName}",
                    Icon = ArtifactIcon(set, index),
                    StatLines = statLines
                });
            }
        }
    }

    private static void AddCards(GameServerConfig config)
    {
        if (config.Cards is null)
            return;
        foreach (CardConfig card in config.Cards)
        {
            if (card is null || string.IsNullOrWhiteSpace(card.Id))
                continue;
            string name = NameOf(card, card.Id);
            Entries.Add(new BuildCatalogEntry
            {
                Id = card.Id,
                Name = name,
                Kind = CatalogKind.Card,
                Slot = card.EquipClass.ToString(),
                Subtitle = $"{card.EquipClass} card" + (card.Unique ? "  •  Unique" : string.Empty),
                Description = DescriptionOf(card),
                SearchText = $"{name} {card.Id} {card.EquipClass} {card.Affix}",
                Icon = card.Sprite
            });
        }
    }

    private static void AddGems(GameServerConfig config)
    {
        if (config.Gems is null)
            return;
        foreach (GemConfig gem in config.Gems)
        {
            if (gem is null || string.IsNullOrWhiteSpace(gem.Id))
                continue;
            string name = NameOf(gem, gem.Id);
            Entries.Add(new BuildCatalogEntry
            {
                Id = gem.Id,
                Name = name,
                Kind = CatalogKind.Gem,
                Subtitle = gem.IsBoss ? "Boss gem" : "Artifact gem",
                Description = DescriptionOf(gem),
                SearchText = $"{name} {gem.Id} {gem.Affix}",
                Icon = gem.Sprite
            });
        }
    }

    private static void AddSubstatTypes(GameServerConfig config)
    {
        if (config.EquipSubstats is null)
            return;
        foreach (EquipSubstatConfig substatConfig in config.EquipSubstats)
        {
            if (substatConfig?.Stats is null)
                continue;
            foreach (EquipSubstatList list in substatConfig.Stats)
            {
                if (list?.Stats is null)
                    continue;
                foreach (StatValue stat in list.Stats)
                    if (stat is not null && stat.Type != StatType.None)
                        SubstatTypeNames.Add(stat.Type.ToString());
            }
        }
    }

    private static void AddSkills(GameServerConfig config)
    {
        var byId = new Dictionary<string, SkillConfig>(StringComparer.OrdinalIgnoreCase);
        if (config.Skills is not null)
        {
            foreach (SkillConfig skill in config.Skills)
                if (skill is not null && !string.IsNullOrWhiteSpace(skill.Id))
                    byId[skill.Id] = skill;
        }
        if (config.Passives is not null)
        {
            foreach (SkillPassiveConfig skill in config.Passives)
                if (skill is not null && !string.IsNullOrWhiteSpace(skill.Id))
                    AddSkillEntry(skill);
        }
        if (config.Masteries is not null)
        {
            foreach (SkillPassiveConfig skill in config.Masteries)
                if (skill is not null && !string.IsNullOrWhiteSpace(skill.Id))
                    AddSkillEntry(skill);
        }
        foreach (SkillConfig skill in byId.Values)
            AddSkillEntry(skill);

        if (config.SkillTrees is null)
            return;
        foreach (SkillTree tree in config.SkillTrees)
        {
            if (tree is null || tree.Items is null)
                continue;
            string className = tree.Type.ToString();
            SkillRequirementsDisabledByClass[className] = tree.DisableRequirements;
            if (!SkillsByClass.TryGetValue(className, out HashSet<string> ids))
            {
                ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                SkillsByClass[className] = ids;
            }
            foreach (SkillTreeItem item in tree.Items)
                if (item is not null && !string.IsNullOrWhiteSpace(item.Id))
                    ids.Add(item.Id);
        }
    }

    private static void AddSkillEntry(BaseSkillConfig skill)
    {
        string id = skill.Id;
        if (Entries.Any(entry => entry.Kind == CatalogKind.Skill
            && entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            return;
        var requirements = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (skill.Requirements is not null)
        {
            foreach (SkillRequirement requirement in skill.Requirements)
            {
                if (!string.IsNullOrWhiteSpace(requirement.Id) && requirement.Level > 0)
                    requirements[requirement.Id] = requirement.Level;
            }
        }
        string name = NameOf(skill, skill.Id);
        string description = DescriptionOf(skill);
        int maxLevel = skill.MaxLv;
        Entries.Add(new BuildCatalogEntry
        {
            Id = id,
            Name = name,
            Kind = CatalogKind.Skill,
            Subtitle = $"Skill  •  Max {Math.Max(1, maxLevel)}",
            Description = description,
            SearchText = $"{name} {id} {description}",
            Icon = skill.Sprite,
            MaxLevel = Math.Max(1, maxLevel),
            Requirements = requirements
        });
    }

    private static bool FitsEquipmentSlot(string itemSlot, string requested) => requested switch
    {
        "mainhand" => itemSlot == "weapon",
        "offhand" => itemSlot is "weapon" or "offhand",
        "acc1" or "acc2" => itemSlot == "accessory",
        _ => itemSlot.Equals(requested ?? string.Empty, StringComparison.OrdinalIgnoreCase)
    };

    private static string SlotFor(EquipType type) => type switch
    {
        EquipType.Shield => "offhand",
        EquipType.Head => "head",
        EquipType.Legs => "legs",
        EquipType.Feet => "feet",
        EquipType.Chest => "chest",
        EquipType.Accessory => "accessory",
        EquipType.Eyewear => "eyewear",
        EquipType.Back => "back",
        EquipType.Grimoire => "grimoire",
        _ => "weapon"
    };

    private static void AddLookup(string key, BuildCatalogEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(key) && !ByAnyId.ContainsKey(key))
            ByAnyId[key] = entry;
    }

    private static string NameOf(BaseConfig config, string fallback)
    {
        if (config is null)
            return Readable(fallback);
        string localized = Localize(config.DisplayName);
        return string.IsNullOrWhiteSpace(localized) ? Readable(fallback ?? config.Id) : localized;
    }

    private static string DescriptionOf(BaseConfig config)
    {
        if (config is null)
            return string.Empty;
        return string.Join("\n", new[] { Localize(config.Description), Localize(config.Description2) }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static void AddStatLine(List<string> lines, StatValue stat, string prefix = null)
    {
        if (stat is null)
            return;
        // Do not call StatUtil.ToDescription here. Its nullable ValueTuple out parameter crosses
        // the IL2CPP boundary incorrectly and can terminate the process with an access violation.
        // These fields are already present on the config object, so format them entirely in managed code.
        string label = Readable(stat.Type.ToString());
        ScaledValue scaled = stat.Value;
        string description = label;
        if (scaled is not null)
        {
            bool percentage = IsPercentageStat(stat.Type);
            var values = new List<string>();
            if (Math.Abs(scaled.Value) > 0.0001f)
                values.Add(FormatStatNumber(scaled.Value, percentage));
            if (Math.Abs(scaled.ValueLv) > 0.0001f)
                values.Add($"{FormatStatNumber(scaled.ValueLv, percentage)} per level");
            if (!string.IsNullOrWhiteSpace(scaled.ValueStr))
                values.Add(Readable(scaled.ValueStr));
            if (!string.IsNullOrWhiteSpace(scaled.ValueStr2))
                values.Add(Readable(scaled.ValueStr2));
            if (values.Count > 0)
                description += ": " + string.Join(" · ", values);
        }
        if (string.IsNullOrWhiteSpace(description))
            return;
        string line = string.IsNullOrWhiteSpace(prefix) ? description.Trim() : $"{prefix}: {description.Trim()}";
        if (!lines.Contains(line, StringComparer.OrdinalIgnoreCase))
            lines.Add(line);
    }

    private static string FormatStatNumber(float value, bool percentage)
    {
        float display = percentage && Math.Abs(value) <= 1f ? value * 100f : value;
        string sign = display > 0f ? "+" : string.Empty;
        return $"{sign}{display:0.##}{(percentage ? "%" : string.Empty)}";
    }

    private static bool IsPercentageStat(StatType type)
    {
        string name = type.ToString();
        if (type is StatType.ElementWeapon or StatType.ElementArmor)
            return false;
        return name.Contains("Mult", StringComparison.Ordinal)
            || name.Contains("Damage", StringComparison.Ordinal)
            || name.Contains("Resist", StringComparison.Ordinal)
            || name.Contains("Chance", StringComparison.Ordinal)
            || name.Contains("Rate", StringComparison.Ordinal)
            || name.Contains("Reduction", StringComparison.Ordinal)
            || name.Contains("Pierce", StringComparison.Ordinal)
            || name.Contains("Healing", StringComparison.Ordinal)
            || name.Contains("Reflect", StringComparison.Ordinal)
            || name.Contains("Leech", StringComparison.Ordinal)
            || name.EndsWith("Spd", StringComparison.Ordinal)
            || type is StatType.AfterCastDelay or StatType.Block or StatType.BlockShield
                or StatType.CritDamage or StatType.CritDef or StatType.PerfectDodge
                or StatType.PerfectCloak or StatType.MpCost or StatType.CooldownRecovery
                or StatType.CastTimeReduction;
    }

    private static string ArtifactDescription(ArtifactSetConfig set, int index)
    {
        try
        {
            if (set.Descriptions is not null && index >= 0 && index < set.Descriptions.Count)
                return Localize(set.Descriptions[index]);
        }
        catch { }
        return DescriptionOf(set);
    }

    private static UnityEngine.Sprite ArtifactIcon(ArtifactSetConfig set, int index)
    {
        try
        {
            if (set.Sprites is not null && index >= 0 && index < set.Sprites.Count)
                return set.Sprites[index];
        }
        catch { }
        return set.Sprite;
    }

    private static string Localize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        try { return Loc.Get(value, value); }
        catch { return value; }
    }

    private static string SafeEquipType(EquipType type)
    {
        try { return EquipUtil.GetDisplayName(type); }
        catch { return Readable(type.ToString()); }
    }

    private static string Slug(string value) => Regex.Replace((value ?? string.Empty).ToLowerInvariant(),
        "[^a-z0-9]+", "-").Trim('-');

    private static string Readable(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        string spaced = Regex.Replace(value.Replace('_', ' '), "(?<=[a-z0-9])(?=[A-Z])", " ");
        return FriendlyStatText(
            CultureInfo.CurrentCulture.TextInfo.ToTitleCase(spaced.ToLowerInvariant()));
    }
}
