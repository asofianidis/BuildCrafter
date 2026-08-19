using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime.InteropTypes;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Il2CppRaycastList = Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>;
using Object = UnityEngine.Object;

namespace NevarisBuildCrafter;

internal static class BuildCrafterController
{
    private const float WindowWidth = 1600f;
    private const float WindowHeight = 920f;
    private static readonly string[] AttributeNames = { "STR", "VIT", "AGI", "DEX", "INT", "LUK" };
    private static readonly ConcurrentQueue<Action> MainThread = new();
    private static readonly Regex RichTextTag = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex RichTextBreak = new("<br\\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private enum Section { Craft, Local, Community }
    private enum CraftView { Loadout, Skills }
    private enum ItemContextView { Details, Compare, Replace }
    private enum ItemToolView { Items, Attachments, Substats }

    private readonly struct SkillBudget
    {
        internal SkillBudget(int total, int advanced)
        {
            Total = total;
            Advanced = advanced;
        }

        internal int Total { get; }
        internal int Advanced { get; }
    }

    private static PlayerController _localPlayer;
    private static bool _worldReady;
    private static bool _open;
    private static bool _dirty;
    private static Section _section = Section.Craft;
    private static CraftView _craftView = CraftView.Loadout;
    private static ItemContextView _itemContextView = ItemContextView.Details;
    private static ItemToolView _itemToolView = ItemToolView.Items;
    private static BuildDocument _current = new();
    private static IReadOnlyList<BuildDocument> _localBuilds = Array.Empty<BuildDocument>();
    private static IReadOnlyList<CommunityBuildSummary> _communityBuilds = Array.Empty<CommunityBuildSummary>();
    private static bool _communityLoading;
    private static bool _communitySearchPending;
    private static float _communitySearchAt;
    private static int _communityRequestVersion;
    private static string _localSearch = string.Empty;
    private static string _communitySearch = string.Empty;
    private static string _pickerSearch = string.Empty;
    private static string _selectedSlot = string.Empty;
    private static string _selectedSkill = string.Empty;
    private static string _comparisonSlot = string.Empty;
    private static PlannedItem _comparisonItem;
    private static BuildCatalogEntry _comparisonResolved;
    private static IReadOnlyList<string> _comparisonStatLines = Array.Empty<string>();
    private static int _selectedSubstatIndex = -1;
    private static CatalogKind _pickerKind = CatalogKind.Equipment;
    private static GameObject _canvasObject;
    private static RectTransform _windowRect;
    private static BuildCrafterUi _ui;
    private static Transform _navigationRoot;
    private static Transform _contentRoot;
    private static Transform _pickerResults;
    private static Transform _localRows;
    private static Transform _communityRows;
    private static TMP_InputField _activeSearch;
    private static TextMeshProUGUI _statusText;
    private static int _lastScreenWidth = -1;
    private static int _lastScreenHeight = -1;

    internal static bool IsOpen => _open;

    internal static void Initialize() { }

    internal static void EnterWorld(PlayerController player)
    {
        _localPlayer = player;
        _worldReady = true;
        GameCatalog.Rebuild();
        RefreshLocalBuilds();
        ResolveBuildNames(_current);
    }

    internal static void LeaveWorld()
    {
        _worldReady = false;
        _open = false;
        _localPlayer = null;
        ClearComparison();
        DestroyUi();
    }

    internal static void Shutdown()
    {
        _worldReady = false;
        _open = false;
        _localPlayer = null;
        ClearComparison();
        DestroyUi();
        while (MainThread.TryDequeue(out _)) { }
    }

    internal static void Tick()
    {
        while (MainThread.TryDequeue(out Action action))
        {
            try { action(); }
            catch (Exception ex) { Plugin.Error($"Queued UI action failed: {ex}"); }
        }

        if (!_worldReady)
            return;
        if (Plugin.Settings.ToggleHotkey.Value.IsDown())
        {
            if (_open)
                Close();
            else if (!IsBaseMenuOpen())
                Open();
        }
        if (!IsTextInputFocused() && Plugin.Settings.CompareHotkey.Value.IsDown())
        {
            CompareHoveredItem();
            return;
        }
        if (!_open)
            return;
        if (_lastScreenWidth != Screen.width || _lastScreenHeight != Screen.height)
            ApplyConfiguredScale();

        if (_communitySearchPending && _section == Section.Community
            && Time.unscaledTime >= _communitySearchAt)
        {
            _communitySearchPending = false;
            RefreshCommunity();
        }

        if (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.S))
        {
            SaveCurrent();
            return;
        }
        if (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.F))
        {
            _activeSearch?.ActivateInputField();
            return;
        }
    }

    internal static void Open()
    {
        if (!_worldReady)
            return;
        EnsureUi();
        if (_canvasObject is null)
            return;
        _open = true;
        _canvasObject.SetActive(true);
        Render();
        ApplyConfiguredScale();
    }

    internal static void Close()
    {
        _open = false;
        if (_canvasObject is not null)
            _canvasObject.SetActive(false);
        try
        {
            if (EventSystem.current?.currentSelectedGameObject is not null)
                EventSystem.current.SetSelectedGameObject(null);
        }
        catch { }
    }

    internal static void HandleEscape()
    {
        if (!string.IsNullOrWhiteSpace(_pickerSearch))
        {
            _pickerSearch = string.Empty;
            if (_section == Section.Craft)
                RenderCraft();
            return;
        }
        Close();
    }

    internal static void ApplyConfiguredScale()
    {
        if (_windowRect is null)
            return;
        float preferred = Mathf.Clamp(Plugin.Settings.UiScale.Value, 0.75f, 1.15f);
        float widthFit = Screen.width > 0 ? Mathf.Min(1f, Screen.width / (WindowWidth + 40f)) : 1f;
        float heightFit = Screen.height > 0 ? Mathf.Min(1f, Screen.height / (WindowHeight + 40f)) : 1f;
        float scale = preferred * Mathf.Min(widthFit, heightFit);
        _windowRect.localScale = new Vector3(scale, scale, 1f);
        _lastScreenWidth = Screen.width;
        _lastScreenHeight = Screen.height;
    }

    internal static bool ShouldBlockMovement(MoveComponent movement)
    {
        if (!_open || movement is null || _localPlayer is null)
            return false;
        try { return movement == _localPlayer.gameObject.GetComponent<MoveComponent>(); }
        catch { return false; }
    }

    internal static void RefreshCatalog()
    {
        GameCatalog.Rebuild();
        ResolveBuildNames(_current);
        ClearComparison();
        if (_open)
        {
            SetStatus("Game data refreshed.", BuildCrafterUi.Positive);
            Render();
        }
    }

    private static void EnsureUi()
    {
        if (_canvasObject is not null)
            return;
        _ui = new BuildCrafterUi();
        _canvasObject = new GameObject("NevarisBuildCrafter.Canvas");
        Object.DontDestroyOnLoad(_canvasObject);
        Canvas canvas = _canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32450;
        CanvasScaler scaler = _canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        _canvasObject.AddComponent<GraphicRaycaster>();

        GameObject backdrop = _ui.Rect("Backdrop", _canvasObject.transform, Vector2.zero, Vector2.one,
            Vector2.zero, Vector2.zero);
        _ui.Panel(backdrop, BuildCrafterUi.Backdrop);

        GameObject window = _ui.Rect("Window", backdrop.transform, new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f), new Vector2(-WindowWidth / 2f, -WindowHeight / 2f),
            new Vector2(WindowWidth / 2f, WindowHeight / 2f));
        _ui.Panel(window, BuildCrafterUi.Surface);
        _windowRect = window.GetComponent<RectTransform>();

        GameObject header = _ui.Rect("Header", window.transform, new Vector2(0f, 1f), Vector2.one,
            new Vector2(0f, -72f), Vector2.zero);
        _ui.Panel(header, BuildCrafterUi.SurfaceRaised);
        TextMeshProUGUI title = _ui.TextBlock("Title", header.transform, "BUILD CRAFTER", 22f,
            BuildCrafterUi.TextStrong, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        title.rectTransform.offsetMin = new Vector2(24f, 4f);
        title.rectTransform.offsetMax = new Vector2(-1100f, -4f);
        TextMeshProUGUI subtitle = _ui.TextBlock("Subtitle", header.transform,
            "Plan locally. Learn from the community.", 13f, BuildCrafterUi.TextMuted);
        subtitle.rectTransform.offsetMin = new Vector2(215f, 4f);
        subtitle.rectTransform.offsetMax = new Vector2(-830f, -4f);
        _statusText = _ui.TextBlock("Status", header.transform, "Ready", 13f,
            BuildCrafterUi.TextMuted, TextAlignmentOptions.MidlineRight);
        _statusText.rectTransform.offsetMin = new Vector2(730f, 4f);
        _statusText.rectTransform.offsetMax = new Vector2(-260f, -4f);
        Button save = _ui.Button("Save", header.transform, "SAVE LOCAL", SaveCurrent, primary: true, fontSize: 13f);
        save.GetComponent<RectTransform>().anchorMin = new Vector2(1f, 0.5f);
        save.GetComponent<RectTransform>().anchorMax = new Vector2(1f, 0.5f);
        save.GetComponent<RectTransform>().offsetMin = new Vector2(-240f, -20f);
        save.GetComponent<RectTransform>().offsetMax = new Vector2(-82f, 20f);
        Button close = _ui.Button("Close", header.transform, "×", Close, fontSize: 22f);
        close.GetComponent<RectTransform>().anchorMin = new Vector2(1f, 0.5f);
        close.GetComponent<RectTransform>().anchorMax = new Vector2(1f, 0.5f);
        close.GetComponent<RectTransform>().offsetMin = new Vector2(-66f, -20f);
        close.GetComponent<RectTransform>().offsetMax = new Vector2(-24f, 20f);

        GameObject navigation = _ui.Rect("Navigation", window.transform, Vector2.zero,
            new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(220f, -72f));
        _ui.Panel(navigation, BuildCrafterUi.SurfaceLow);
        _navigationRoot = navigation.transform;
        GameObject content = _ui.Rect("Content", window.transform, Vector2.zero, Vector2.one,
            new Vector2(220f, 0f), new Vector2(0f, -72f));
        _contentRoot = content.transform;
    }

    private static void Render()
    {
        RenderNavigation();
        BuildCrafterUi.Clear(_contentRoot);
        _activeSearch = null;
        switch (_section)
        {
            case Section.Local: RenderLocal(); break;
            case Section.Community: RenderCommunity(); break;
            default: RenderCraft(); break;
        }
    }

    private static void RenderNavigation()
    {
        BuildCrafterUi.Clear(_navigationRoot);
        TextMeshProUGUI label = _ui.TextBlock("NavLabel", _navigationRoot, "WORKSPACE", 11f,
            BuildCrafterUi.TextMuted, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        PlaceTop(label.rectTransform, 22f, -16f, -20f, 32f);
        AddNavButton("Craft", "CRAFT", Section.Craft, -104f);
        AddNavButton("Local", $"LOCAL  {_localBuilds.Count}", Section.Local, -160f);
        AddNavButton("Community", "COMMUNITY", Section.Community, -216f);

        GameObject summary = _ui.Rect("Summary", _navigationRoot, new Vector2(0f, 0f),
            new Vector2(1f, 0f), new Vector2(16f, 18f), new Vector2(-16f, 176f));
        _ui.Panel(summary, BuildCrafterUi.SurfaceRaised);
        int planned = _current.Equipment.Count(pair => !string.IsNullOrWhiteSpace(pair.Value?.ItemId))
            + _current.Artifacts.Count(pair => !string.IsNullOrWhiteSpace(pair.Value?.ItemId));
        TextMeshProUGUI current = _ui.TextBlock("Current", summary.transform,
            $"<size=80%><color=#91A0A3>CURRENT BUILD</color></size>\n<b>{Escape(_current.Name)}</b>\n" +
            $"{Escape(_current.Class)}  •  Level {_current.BaseLevel}" +
            (_dirty ? "  •  <color=#D7B66B>Unsaved</color>" : string.Empty) + "\n\n" +
            $"<color=#D7B66B>{planned}/14</color> loadout slots planned",
            14f, BuildCrafterUi.Text, TextAlignmentOptions.TopLeft, richText: true);
        current.rectTransform.offsetMin = new Vector2(14f, 12f);
        current.rectTransform.offsetMax = new Vector2(-14f, -12f);
    }

    private static void AddNavButton(string name, string label, Section section, float top)
    {
        Button button = _ui.Button(name, _navigationRoot, label, () =>
        {
            _section = section;
            Render();
            if (section == Section.Community)
                RefreshCommunity();
        }, selected: _section == section, fontSize: 14f);
        RectTransform rect = button.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.offsetMin = new Vector2(16f, top - 44f);
        rect.offsetMax = new Vector2(-16f, top);
    }

    private static void RenderCraft()
    {
        BuildCrafterUi.Clear(_contentRoot);
        _activeSearch = null;
        GameObject toolbar = _ui.Rect("CraftToolbar", _contentRoot, new Vector2(0f, 1f), Vector2.one,
            new Vector2(24f, -74f), new Vector2(-24f, -18f));
        TextMeshProUGUI nameLabel = _ui.TextBlock("NameLabel", toolbar.transform, "BUILD NAME", 11f,
            BuildCrafterUi.TextMuted, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        PlaceTop(nameLabel.rectTransform, 0f, 2f, -1070f, 22f);
        TMP_InputField name = _ui.Input("BuildName", toolbar.transform, "Name this build",
            value => { _current.Name = value; MarkDirty(); }, _current.Name, characterLimit: 60);
        RectTransform nameRect = name.GetComponent<RectTransform>();
        nameRect.anchorMin = new Vector2(0f, 0f);
        nameRect.anchorMax = new Vector2(0f, 0f);
        nameRect.offsetMin = new Vector2(0f, 0f);
        nameRect.offsetMax = new Vector2(500f, 36f);
        AddCraftTab(toolbar.transform, "LOADOUT", CraftView.Loadout, 160f);
        AddCraftTab(toolbar.transform, "SKILLS", CraftView.Skills, 0f);

        GameObject body = _ui.Rect("CraftBody", _contentRoot, Vector2.zero, Vector2.one,
            new Vector2(24f, 20f), new Vector2(-24f, -88f));
        RenderIdentity(body.transform);
        RenderCraftCenter(body.transform);
        RenderContext(body.transform);
    }

    private static void AddCraftTab(Transform parent, string label, CraftView view, float right)
    {
        Button button = _ui.Button(label, parent, label, () =>
        {
            _craftView = view;
            _selectedSkill = string.Empty;
            _pickerSearch = string.Empty;
            RenderCraft();
        }, selected: _craftView == view, fontSize: 13f);
        RectTransform rect = button.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.offsetMin = new Vector2(-right - 145f, 0f);
        rect.offsetMax = new Vector2(-right, 36f);
    }

    private static void RenderIdentity(Transform body)
    {
        GameObject panel = _ui.Rect("Identity", body, Vector2.zero, Vector2.one,
            Vector2.zero, new Vector2(-1012f, 0f));
        _ui.Panel(panel, BuildCrafterUi.SurfaceLow);
        AddSectionHeading(panel.transform, "CHARACTER", "Class and target levels", -18f);

        TextMeshProUGUI className = _ui.TextBlock("Class", panel.transform, _current.Class, 22f,
            BuildCrafterUi.TextStrong, TextAlignmentOptions.Center, FontStyles.Bold);
        PlaceTop(className.rectTransform, 78f, -68f, -78f, 50f);
        AddSmallButton(panel.transform, "PrevClass", "‹", 18f, -112f, 48f, 42f, () => ChangeClass(-1));
        AddRightSmallButton(panel.transform, "NextClass", "›", 18f, -112f, 48f, 42f,
            () => ChangeClass(1));

        AddNumericField(panel.transform, "BASE LEVEL", _current.BaseLevel, 1, 150, -158f,
            value => { _current.BaseLevel = value; MarkDirty(); });
        AddNumericField(panel.transform, "JOB LEVEL", _current.JobLevel, 1, 99, -210f,
            UpdateJobLevel);

        TextMeshProUGUI attrs = _ui.TextBlock("AttributesLabel", panel.transform, "ATTRIBUTES", 11f,
            BuildCrafterUi.Accent, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        PlaceTop(attrs.rectTransform, 18f, -244f, -18f, 28f);
        for (int i = 0; i < AttributeNames.Length; i++)
            AddAttributeRow(panel.transform, AttributeNames[i], -310f - i * 50f);

        TextMeshProUGUI notesLabel = _ui.TextBlock("NotesLabel", panel.transform, "BUILD NOTES", 11f,
            BuildCrafterUi.Accent, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        PlaceTop(notesLabel.rectTransform, 18f, -600f, -18f, 28f);
        TMP_InputField notes = _ui.Input("Notes", panel.transform, "Rotation, goals, farming notes...",
            value => { _current.Notes = value; MarkDirty(); }, _current.Notes, true, 1200);
        notes.GetComponent<RectTransform>().offsetMin = new Vector2(18f, 20f);
        notes.GetComponent<RectTransform>().offsetMax = new Vector2(-18f, -636f);
    }

    private static void RenderCraftCenter(Transform body)
    {
        GameObject panel = _ui.Rect("Center", body, Vector2.zero, Vector2.one,
            new Vector2(368f, 0f), new Vector2(-444f, 0f));
        _ui.Panel(panel, BuildCrafterUi.SurfaceLow);
        if (_craftView == CraftView.Skills)
            RenderSkills(panel.transform);
        else
            RenderLoadout(panel.transform);
    }

    private static void RenderLoadout(Transform panel)
    {
        AddSectionHeading(panel, "LOADOUT", "Select a slot, then choose an item", -18f);
        for (int i = 0; i < BuildSlots.Equipment.Length; i++)
        {
            string slot = BuildSlots.Equipment[i];
            int column = i % 2;
            int row = i / 2;
            float top = -74f - row * 92f;
            AddLoadoutSlot(panel, slot, false, column, top, 78f);
        }

        TextMeshProUGUI artifacts = _ui.TextBlock("ArtifactsLabel", panel, "ARTIFACT SET", 11f,
            BuildCrafterUi.Accent, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        PlaceTop(artifacts.rectTransform, 18f, -544f, -18f, 28f);
        for (int i = 0; i < BuildSlots.Artifacts.Length; i++)
        {
            string slot = BuildSlots.Artifacts[i];
            int column = i % 2;
            int row = i / 2;
            AddLoadoutSlot(panel, slot, true, column, -610f - row * 72f, 58f);
        }
    }

    private static void AddLoadoutSlot(Transform parent, string slot, bool artifact, int column,
        float top, float height)
    {
        PlannedItem planned = artifact
            ? (_current.Artifacts.TryGetValue(slot, out PlannedArtifact art) ? art : null)
            : (_current.Equipment.TryGetValue(slot, out PlannedItem equip) ? equip : null);
        BuildCatalogEntry resolved = planned is null ? null : GameCatalog.Resolve(planned.ItemId,
            artifact ? CatalogKind.Artifact : CatalogKind.Equipment)
            ?? GameCatalog.Resolve(planned.DisplayName,
                artifact ? CatalogKind.Artifact : CatalogKind.Equipment);
        string itemName = planned is null || string.IsNullOrWhiteSpace(planned.ItemId)
            ? "Choose item" : DisplayName(planned);
        string refine = planned is not null && planned.Refine > 0 ? $"  +{planned.Refine}" : string.Empty;
        string label = $"<size=78%><color=#91A0A3>{BuildSlots.Label(slot).ToUpperInvariant()}</color></size>\n" +
            $"{Escape(itemName)}<color=#D7B66B>{refine}</color>";
        Button button = _ui.Button("Slot." + slot, parent, label, () =>
        {
            _selectedSlot = slot;
            _selectedSkill = string.Empty;
            _pickerKind = artifact ? CatalogKind.Artifact : CatalogKind.Equipment;
            _pickerSearch = string.Empty;
            _itemToolView = ItemToolView.Items;
            _selectedSubstatIndex = -1;
            _itemContextView = planned is null ? ItemContextView.Replace : ItemContextView.Details;
            RenderCraft();
        }, selected: _selectedSlot == slot, fontSize: 14f);
        TextMeshProUGUI buttonText = button.GetComponentInChildren<TextMeshProUGUI>();
        buttonText.richText = true;
        buttonText.alignment = TextAlignmentOptions.MidlineLeft;
        if (planned is not null)
        {
            float iconSize = artifact ? 36f : 46f;
            AddCenteredCatalogIcon(button.transform, "ItemIcon", resolved?.Icon, 10f, iconSize);
            buttonText.rectTransform.offsetMin = new Vector2(artifact ? 54f : 64f, 3f);
        }
        RectTransform rect = button.GetComponent<RectTransform>();
        // Each slot owns half of the pane. Insets create an 18 px outer margin and a
        // 14 px center gutter without relying on a width that can overflow the pane.
        rect.anchorMin = new Vector2(column * 0.5f, 1f);
        rect.anchorMax = new Vector2((column + 1) * 0.5f, 1f);
        rect.offsetMin = new Vector2(column == 0 ? 18f : 7f, top - height);
        rect.offsetMax = new Vector2(column == 0 ? -7f : -18f, top);
    }

    private static void RenderSkills(Transform panel)
    {
        SkillBudget budget = CurrentSkillBudget();
        int totalSpent = SkillPointsSpent();
        int advancedSpent = AdvancedSkillPointsSpent();
        string allocation = GameCatalog.IsAdvancedClass(_current.Class)
            ? $"{totalSpent} / {budget.Total} total  •  {advancedSpent} / {budget.Advanced} advanced"
            : GameCatalog.IsFreeformClass(_current.Class)
                ? $"{totalSpent} / {budget.Total} points  •  all base trees"
                : $"{totalSpent} / {budget.Total} points";
        AddSectionHeading(panel, "SKILLS", allocation, -18f);

        ScrollRect scroll = _ui.Scroll("SkillList", panel, out Transform content, 5f);
        scroll.GetComponent<RectTransform>().offsetMin = new Vector2(18f, 18f);
        scroll.GetComponent<RectTransform>().offsetMax = new Vector2(-18f, -66f);
        IReadOnlyList<BuildCatalogEntry> skills = CurrentSkillEntries();
        if (skills.Count == 0)
        {
            AddEmptyRow(content, "No skill tree was found for this class in the installed game data.");
            return;
        }
        foreach (BuildCatalogEntry skill in skills)
        {
            int level = _current.Skills.TryGetValue(skill.Id, out int value) ? value : 0;
            GameObject row = _ui.Rect("Skill." + skill.Id, content, Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero);
            _ui.Panel(row, _selectedSkill == skill.Id ? BuildCrafterUi.AccentMuted : BuildCrafterUi.SurfaceRaised);
            BuildCrafterUi.AddLayout(row, 58f);
            Button select = row.AddComponent<Button>();
            select.targetGraphic = row.GetComponent<Image>();
            BuildCrafterUi.Wire(select, () => { _selectedSkill = skill.Id; RenderCraft(); });
            AddCenteredCatalogIcon(row.transform, "SkillIcon", skill.Icon, 8f, 42f);
            TextMeshProUGUI text = _ui.TextBlock("Name", row.transform,
                $"{Escape(skill.Name)}\n<size=78%><color=#91A0A3>{Escape(SkillSubtitle(skill))}</color></size>",
                14f, BuildCrafterUi.Text, TextAlignmentOptions.MidlineLeft, richText: true);
            text.rectTransform.offsetMin = new Vector2(60f, 4f);
            text.rectTransform.offsetMax = new Vector2(-138f, -4f);
            AddRowStepper(row.transform, level, 0, skill.MaxLevel, value => SetSkillLevel(skill, value));
        }
    }

    private static void RenderContext(Transform body)
    {
        GameObject panel = _ui.Rect("Context", body, Vector2.zero, Vector2.one,
            new Vector2(948f, 0f), Vector2.zero);
        _ui.Panel(panel, BuildCrafterUi.SurfaceLow);
        if (_craftView == CraftView.Skills)
            RenderSkillContext(panel.transform);
        else
            RenderItemContext(panel.transform);
    }

    private static void RenderItemContext(Transform panel)
    {
        if (string.IsNullOrWhiteSpace(_selectedSlot))
        {
            AddSectionHeading(panel, "ITEM WORKBENCH", "Nothing selected", -18f);
            AddEmptyState(panel, "Select a loadout slot to browse compatible items.\n\n" +
                "Build Crafter reads the current game data, so choices follow the installed SpiritVale version.");
            return;
        }

        bool artifactSlot = BuildSlots.Artifacts.Contains(_selectedSlot);
        PlannedItem planned = artifactSlot
            ? (_current.Artifacts.TryGetValue(_selectedSlot, out PlannedArtifact art) ? art : null)
            : (_current.Equipment.TryGetValue(_selectedSlot, out PlannedItem equip) ? equip : null);
        bool hasComparison = _comparisonItem is not null
            && _comparisonSlot.Equals(_selectedSlot, StringComparison.OrdinalIgnoreCase);
        if (planned is null && !hasComparison)
            _itemContextView = ItemContextView.Replace;
        else if (_itemContextView == ItemContextView.Compare && !hasComparison)
            _itemContextView = planned is null ? ItemContextView.Replace : ItemContextView.Details;
        AddSectionHeading(panel, BuildSlots.Label(_selectedSlot).ToUpperInvariant(),
            hasComparison
                ? (planned is null
                    ? $"Empty slot vs {DisplayName(_comparisonItem)}"
                    : $"{DisplayName(planned)} vs {DisplayName(_comparisonItem)}")
                : planned is null ? "Choose an item" : DisplayName(planned), -18f);

        AddItemContextTabs(panel, planned is not null, hasComparison);
        BuildCatalogEntry resolved = planned is null ? null : GameCatalog.Resolve(planned.ItemId,
            artifactSlot ? CatalogKind.Artifact : CatalogKind.Equipment)
            ?? GameCatalog.Resolve(planned.DisplayName,
                artifactSlot ? CatalogKind.Artifact : CatalogKind.Equipment);
        if (_itemContextView == ItemContextView.Compare && hasComparison)
            RenderItemComparison(panel, planned, resolved, artifactSlot);
        else if (_itemContextView == ItemContextView.Details && planned is not null)
            RenderItemDetails(panel, planned, resolved, artifactSlot);
        else
            RenderItemReplace(panel, planned, resolved, artifactSlot);
    }

    private static void AddItemContextTabs(Transform panel, bool hasItem, bool hasComparison)
    {
        Button details = _ui.Button("DetailsTab", panel, "DETAILS", () =>
        {
            _itemContextView = ItemContextView.Details;
            RenderCraft();
        }, selected: _itemContextView == ItemContextView.Details, fontSize: 12f);
        PlaceTopFraction(details.GetComponent<RectTransform>(), 0f,
            hasComparison ? 0.3f : 0.5f, -68f, 38f, 18f, 4f);
        details.interactable = hasItem;

        if (hasComparison)
        {
            Button compare = _ui.Button("CompareTab", panel, "COMPARE", () =>
            {
                _itemContextView = ItemContextView.Compare;
                RenderCraft();
            }, selected: _itemContextView == ItemContextView.Compare, fontSize: 12f);
            PlaceTopFraction(compare.GetComponent<RectTransform>(), 0.3f, 0.62f,
                -68f, 38f, 4f, 4f);
        }

        Button replace = _ui.Button("ReplaceTab", panel, "REPLACE & TOOLS", () =>
        {
            _itemContextView = ItemContextView.Replace;
            RenderCraft();
        }, selected: _itemContextView == ItemContextView.Replace, fontSize: 12f);
        PlaceTopFraction(replace.GetComponent<RectTransform>(), hasComparison ? 0.62f : 0.5f,
            1f, -68f, 38f, 4f, 18f);
    }

    private static void RenderItemComparison(Transform panel, PlannedItem planned,
        BuildCatalogEntry plannedResolved, bool artifactSlot)
    {
        AddComparisonSummary(panel, "BUILD", planned, plannedResolved, artifactSlot,
            0f, 0.5f, BuildCrafterUi.Accent);
        AddComparisonSummary(panel, "HOVERED", _comparisonItem, _comparisonResolved,
            artifactSlot, 0.5f, 1f, BuildCrafterUi.Positive);

        GameObject divider = _ui.DividerLine("ComparisonDivider", panel);
        RectTransform dividerRect = divider.GetComponent<RectTransform>();
        dividerRect.anchorMin = new Vector2(0.5f, 0f);
        dividerRect.anchorMax = new Vector2(0.5f, 1f);
        dividerRect.offsetMin = new Vector2(-0.5f, 18f);
        dividerRect.offsetMax = new Vector2(0.5f, -118f);

        IReadOnlyList<string> plannedLines = planned is null
            ? new[] { "No item is planned for this slot." }
            : ItemStatLines(plannedResolved, planned);
        IReadOnlyList<string> hoveredLines = _comparisonStatLines.Count > 0
            ? _comparisonStatLines
            : ItemStatLines(_comparisonResolved, _comparisonItem);
        var plannedSet = new HashSet<string>(plannedLines, StringComparer.OrdinalIgnoreCase);
        var hoveredSet = new HashSet<string>(hoveredLines, StringComparer.OrdinalIgnoreCase);

        AddComparisonStats(panel, plannedLines, hoveredSet, 0f, 0.5f,
            planned is null ? BuildCrafterUi.TextMuted : BuildCrafterUi.Warning);
        AddComparisonStats(panel, hoveredLines, plannedSet, 0.5f, 1f, BuildCrafterUi.Positive);
    }

    private static void AddComparisonSummary(Transform panel, string label, PlannedItem item,
        BuildCatalogEntry resolved, bool artifactSlot, float minX, float maxX, Color accent)
    {
        TextMeshProUGUI labelText = _ui.TextBlock(label + "Label", panel, label, 10.5f,
            accent, TextAlignmentOptions.Center, FontStyles.Bold);
        PlaceTopFraction(labelText.rectTransform, minX, maxX, -120f, 20f, 10f, 10f);

        AddFractionCatalogIcon(panel, label + "Icon", resolved?.Icon,
            (minX + maxX) * 0.5f, -146f, 52f);
        string itemName = item is null ? "Empty slot" : DisplayName(item);
        string summary = item is null
            ? "Nothing planned"
            : $"Refine +{item.Refine}  •  {AttachmentSummary(item, artifactSlot)}";
        TextMeshProUGUI detail = _ui.TextBlock(label + "Detail", panel,
            $"<b>{Escape(itemName)}</b>\n<size=78%><color=#91A0A3>{Escape(summary)}</color></size>",
            12.5f, BuildCrafterUi.Text, TextAlignmentOptions.Top,
            FontStyles.Normal, richText: true);
        PlaceTopFraction(detail.rectTransform, minX, maxX, -204f, 66f, 10f, 10f);
    }

    private static void AddComparisonStats(Transform panel, IReadOnlyList<string> lines,
        HashSet<string> otherLines, float minX, float maxX, Color differenceColor)
    {
        TextMeshProUGUI heading = _ui.TextBlock("ComparisonStats", panel, "STATS & EFFECTS",
            10.5f, BuildCrafterUi.TextMuted, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        PlaceTopFraction(heading.rectTransform, minX, maxX, -282f, 22f, 12f, 8f);

        ScrollRect scroll = _ui.Scroll("ComparisonScroll", panel, out Transform content, 4f);
        RectTransform rect = scroll.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(minX, 0f);
        rect.anchorMax = new Vector2(maxX, 1f);
        rect.offsetMin = new Vector2(10f, 18f);
        rect.offsetMax = new Vector2(-10f, -312f);
        foreach (string line in lines)
        {
            Color color = otherLines.Contains(line) ? BuildCrafterUi.TextMuted : differenceColor;
            AddComparisonStatRow(content, line, color);
        }
    }

    private static void AddComparisonStatRow(Transform parent, string line, Color color)
    {
        GameObject row = _ui.Rect("ComparisonStat", parent, Vector2.zero, Vector2.one,
            Vector2.zero, Vector2.zero);
        float height = line.Length > 48 ? 68f : line.Length > 28 ? 48f : 32f;
        BuildCrafterUi.AddLayout(row, height);
        TextMeshProUGUI text = _ui.TextBlock("Text", row.transform, "• " + Escape(line),
            11.5f, color, TextAlignmentOptions.TopLeft, richText: true);
        text.rectTransform.offsetMin = new Vector2(2f, 3f);
        text.rectTransform.offsetMax = new Vector2(-4f, -3f);
    }

    private static void RenderItemDetails(Transform panel, PlannedItem planned,
        BuildCatalogEntry resolved, bool artifactSlot)
    {
        AddTopCatalogIcon(panel, "SelectedItemIcon", resolved?.Icon, 18f, -122f, 88f);
        TextMeshProUGUI detail = _ui.TextBlock("SelectedDetail", panel,
            $"<b>{Escape(DisplayName(planned))}</b>\n" +
            $"<size=82%><color=#91A0A3>{Escape(resolved?.Subtitle ?? "Imported item")}</color></size>\n" +
            $"<size=82%>Refine +{planned.Refine}  •  {Escape(AttachmentSummary(planned, artifactSlot))}</size>",
            15f, BuildCrafterUi.Text, TextAlignmentOptions.TopLeft, richText: true);
        PlaceTop(detail.rectTransform, 118f, -120f, -18f, 108f);

        TextMeshProUGUI statsLabel = _ui.TextBlock("StatsLabel", panel, "STATS & EFFECTS", 11f,
            BuildCrafterUi.Accent, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        PlaceTop(statsLabel.rectTransform, 18f, -244f, -18f, 24f);
        ScrollRect statsScroll = _ui.Scroll("ItemStats", panel, out Transform statsContent, 4f);
        statsScroll.GetComponent<RectTransform>().offsetMin = new Vector2(18f, 18f);
        statsScroll.GetComponent<RectTransform>().offsetMax = new Vector2(-18f, -274f);
        foreach (string line in ItemStatLines(resolved, planned))
            AddItemStatRow(statsContent, line);
    }

    private static void RenderItemReplace(Transform panel, PlannedItem planned,
        BuildCatalogEntry resolved, bool artifactSlot)
    {
        float actionTop = -122f;
        if (planned is not null)
        {
            AddTopCatalogIcon(panel, "SelectedItemIcon", resolved?.Icon, 18f, -122f, 68f);
            TextMeshProUGUI detail = _ui.TextBlock("SelectedDetail", panel,
                $"<b>{Escape(DisplayName(planned))}</b>\n" +
                $"<size=82%><color=#91A0A3>{Escape(resolved?.Subtitle ?? "Imported item")}</color></size>",
                14f, BuildCrafterUi.Text, TextAlignmentOptions.TopLeft, richText: true);
            PlaceTop(detail.rectTransform, 98f, -120f, -18f, 72f);

            const float refineTop = -208f;
            Button refineMinus = AddSmallButton(panel, "RefineMinus", "−", 18f, refineTop, 40f, 34f,
                () => ChangeRefine(-1));
            PlaceTopFraction(refineMinus.GetComponent<RectTransform>(), 0f, 0.15f, refineTop, 34f, 18f, 4f);
            TextMeshProUGUI refine = _ui.TextBlock("Refine", panel, $"+{planned.Refine}", 15f,
                BuildCrafterUi.Accent, TextAlignmentOptions.Center, FontStyles.Bold);
            PlaceTopFraction(refine.rectTransform, 0.15f, 0.31f, refineTop, 34f, 4f, 4f);
            Button refinePlus = AddSmallButton(panel, "RefinePlus", "+", 118f, refineTop, 40f, 34f,
                () => ChangeRefine(1));
            PlaceTopFraction(refinePlus.GetComponent<RectTransform>(), 0.31f, 0.45f, refineTop, 34f, 4f, 4f);
            Button clear = AddActionButton(panel, "Clear", "CLEAR", 174f, refineTop, 82f, 34f,
                ClearSelectedItem);
            PlaceTopFraction(clear.GetComponent<RectTransform>(), 0.45f, 0.64f, refineTop, 34f, 4f, 4f);
            Button encyclopedia = AddActionButton(panel, "Encyclopedia",
                ItemEncyclopediaIntegration.IsAvailable ? "ENCYCLOPEDIA" : "MORE INFO",
                264f, refineTop, 154f, 34f, OpenSelectedInEncyclopedia, true);
            PlaceTopFraction(encyclopedia.GetComponent<RectTransform>(), 0.64f, 1f,
                refineTop, 34f, 4f, 18f);
            actionTop = -254f;
        }

        if (planned is null)
        {
            _itemToolView = ItemToolView.Items;
            _pickerKind = artifactSlot ? CatalogKind.Artifact : CatalogKind.Equipment;
            RenderCatalogPicker(panel, artifactSlot, actionTop, false);
            return;
        }

        RenderItemToolTabs(panel, artifactSlot, actionTop);
        float contentTop = actionTop - 48f;
        if (_itemToolView == ItemToolView.Substats)
            RenderSubstatEditor(panel, planned, contentTop);
        else
            RenderCatalogPicker(panel, artifactSlot, contentTop,
                _itemToolView == ItemToolView.Attachments);
    }

    private static void RenderItemToolTabs(Transform panel, bool artifactSlot, float top)
    {
        Button items = _ui.Button("ItemsTool", panel, artifactSlot ? "ARTIFACT" : "EQUIPMENT", () =>
        {
            _itemToolView = ItemToolView.Items;
            _pickerKind = artifactSlot ? CatalogKind.Artifact : CatalogKind.Equipment;
            _pickerSearch = string.Empty;
            RenderCraft();
        }, selected: _itemToolView == ItemToolView.Items, fontSize: 11f);
        PlaceTopFraction(items.GetComponent<RectTransform>(), 0f, 0.39f, top, 36f, 18f, 4f);

        Button attachments = _ui.Button("AttachmentsTool", panel, artifactSlot ? "GEM" : "CARDS", () =>
        {
            _itemToolView = ItemToolView.Attachments;
            _pickerKind = artifactSlot ? CatalogKind.Gem : CatalogKind.Card;
            _pickerSearch = string.Empty;
            RenderCraft();
        }, selected: _itemToolView == ItemToolView.Attachments, fontSize: 11f);
        PlaceTopFraction(attachments.GetComponent<RectTransform>(), 0.39f, 0.69f, top, 36f, 4f, 4f);

        Button substats = _ui.Button("SubstatsTool", panel, "SUBSTATS", () =>
        {
            _itemToolView = ItemToolView.Substats;
            _selectedSubstatIndex = Math.Clamp(_selectedSubstatIndex, -1,
                Math.Max(-1, (SelectedItem()?.Substats?.Count ?? 0) - 1));
            RenderCraft();
        }, selected: _itemToolView == ItemToolView.Substats, fontSize: 11f);
        PlaceTopFraction(substats.GetComponent<RectTransform>(), 0.69f, 1f, top, 36f, 4f, 18f);
    }

    private static void RenderCatalogPicker(Transform panel, bool artifactSlot, float searchTop,
        bool attachments)
    {
        string placeholder = attachments
            ? (artifactSlot ? "Search gems..." : "Search cards...")
            : (artifactSlot ? "Search artifacts..." : "Search compatible equipment...");
        TMP_InputField search = _ui.Input("PickerSearch", panel, placeholder, value =>
        {
            _pickerSearch = value;
            RenderPickerResults();
        }, _pickerSearch, characterLimit: 80);
        RectTransform searchRect = search.GetComponent<RectTransform>();
        float searchEnd = attachments ? 0.76f : 1f;
        PlaceTopFraction(searchRect, 0f, searchEnd, searchTop, 40f, 18f, attachments ? 4f : 18f);
        _activeSearch = search;

        if (attachments)
        {
            Button reset = AddActionButton(panel, "ClearAttachment", "RESET ALL", 0f, searchTop, 0f, 40f,
                () => ClearAttachments(artifactSlot));
            PlaceTopFraction(reset.GetComponent<RectTransform>(), 0.76f, 1f,
                searchTop, 40f, 4f, 18f);
        }

        ScrollRect results = _ui.Scroll("PickerResults", panel, out _pickerResults, 5f);
        results.GetComponent<RectTransform>().offsetMin = new Vector2(18f, 18f);
        results.GetComponent<RectTransform>().offsetMax = new Vector2(-18f, searchTop - 50f);
        RenderPickerResults();
    }

    private static void RenderSubstatEditor(Transform panel, PlannedItem planned, float top)
    {
        planned.Substats ??= new List<ImportedSubstat>();
        _selectedSubstatIndex = Math.Clamp(_selectedSubstatIndex, -1, planned.Substats.Count - 1);

        TextMeshProUGUI heading = _ui.TextBlock("SubstatHeading", panel,
            $"SUBSTATS  {planned.Substats.Count} / 6", 11f, BuildCrafterUi.Accent,
            TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        PlaceTopFraction(heading.rectTransform, 0f, 0.66f, top, 36f, 18f, 4f);
        Button add = AddActionButton(panel, "AddSubstat", "ADD SUBSTAT", 0f, top, 0f, 36f,
            AddSubstat, true);
        PlaceTopFraction(add.GetComponent<RectTransform>(), 0.66f, 1f, top, 36f, 4f, 18f);
        add.interactable = planned.Substats.Count < 6;

        float listTop = top - 46f;
        float listBottom = top - 246f;
        ScrollRect list = _ui.Scroll("SubstatList", panel, out Transform content, 5f);
        RectTransform listRect = list.GetComponent<RectTransform>();
        listRect.anchorMin = new Vector2(0f, 1f);
        listRect.anchorMax = new Vector2(1f, 1f);
        listRect.offsetMin = new Vector2(18f, listBottom);
        listRect.offsetMax = new Vector2(-18f, listTop);
        if (planned.Substats.Count == 0)
        {
            AddEmptyRow(content, "No planned substats. Add one to start editing.", 72f);
        }
        else
        {
            for (int i = 0; i < planned.Substats.Count; i++)
                AddSubstatRow(content, planned.Substats[i], i);
        }

        float editorHeadingTop = listBottom - 16f;
        if (_selectedSubstatIndex < 0 || _selectedSubstatIndex >= planned.Substats.Count)
        {
            TextMeshProUGUI hint = _ui.TextBlock("SubstatHint", panel,
                "Select a substat above to change its type or value.", 13f,
                BuildCrafterUi.TextMuted, TextAlignmentOptions.TopLeft);
            PlaceTop(hint.rectTransform, 18f, editorHeadingTop, -18f, 60f);
            return;
        }

        ImportedSubstat selected = planned.Substats[_selectedSubstatIndex];
        TextMeshProUGUI editorHeading = _ui.TextBlock("EditorHeading", panel, "EDIT SUBSTAT", 11f,
            BuildCrafterUi.Accent, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        PlaceTop(editorHeading.rectTransform, 18f, editorHeadingTop, -18f, 24f);

        float statTop = editorHeadingTop - 34f;
        Button previous = _ui.Button("PreviousStat", panel, "‹", () => CycleSubstatType(-1), fontSize: 17f);
        PlaceTopFraction(previous.GetComponent<RectTransform>(), 0f, 0.13f, statTop, 36f, 18f, 4f);
        TextMeshProUGUI statName = _ui.TextBlock("StatName", panel,
            ImportedSkillName(selected.Stat), 13f, BuildCrafterUi.Text,
            TextAlignmentOptions.Center, FontStyles.Bold);
        PlaceTopFraction(statName.rectTransform, 0.13f, 0.87f, statTop, 36f, 4f, 4f);
        Button next = _ui.Button("NextStat", panel, "›", () => CycleSubstatType(1), fontSize: 17f);
        PlaceTopFraction(next.GetComponent<RectTransform>(), 0.87f, 1f, statTop, 36f, 4f, 18f);

        float valueTop = statTop - 46f;
        Button minus = _ui.Button("SubstatMinus", panel, "−", () => AdjustSubstatValue(-1f), fontSize: 16f);
        PlaceTopFraction(minus.GetComponent<RectTransform>(), 0f, 0.15f, valueTop, 38f, 18f, 4f);
        TMP_InputField valueInput = _ui.Input("SubstatValue", panel, "Value",
            SetSubstatValue, selected.Base.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            characterLimit: 7);
        PlaceTopFraction(valueInput.GetComponent<RectTransform>(), 0.15f, 0.48f,
            valueTop, 38f, 4f, 4f);
        Button plus = _ui.Button("SubstatPlus", panel, "+", () => AdjustSubstatValue(1f), fontSize: 16f);
        PlaceTopFraction(plus.GetComponent<RectTransform>(), 0.48f, 0.63f, valueTop, 38f, 4f, 4f);
        Button remove = AddActionButton(panel, "RemoveSubstat", "REMOVE", 0f, valueTop, 0f, 38f,
            RemoveSelectedSubstat);
        PlaceTopFraction(remove.GetComponent<RectTransform>(), 0.68f, 1f,
            valueTop, 38f, 4f, 18f);
    }

    private static void AddSubstatRow(Transform parent, ImportedSubstat substat, int index)
    {
        GameObject row = _ui.Rect("Substat." + index, parent, Vector2.zero, Vector2.one,
            Vector2.zero, Vector2.zero);
        Image background = _ui.Panel(row,
            index == _selectedSubstatIndex ? BuildCrafterUi.AccentMuted : BuildCrafterUi.SurfaceRaised);
        BuildCrafterUi.AddLayout(row, 46f);
        Button select = row.AddComponent<Button>();
        select.targetGraphic = background;
        BuildCrafterUi.Wire(select, () =>
        {
            _selectedSubstatIndex = index;
            RenderCraft();
        });
        string sign = substat.Base > 0f ? "+" : string.Empty;
        TextMeshProUGUI text = _ui.TextBlock("Text", row.transform,
            $"{Escape(ImportedSkillName(substat.Stat))}<color=#D7B66B>  {sign}{substat.Base:0.##}</color>",
            13f, BuildCrafterUi.Text, TextAlignmentOptions.MidlineLeft,
            index == _selectedSubstatIndex ? FontStyles.Bold : FontStyles.Normal, richText: true);
        text.rectTransform.offsetMin = new Vector2(12f, 3f);
        text.rectTransform.offsetMax = new Vector2(-12f, -3f);
    }

    private static void RenderPickerResults()
    {
        if (_pickerResults is null)
            return;
        BuildCrafterUi.Clear(_pickerResults);
        string slot = _pickerKind is CatalogKind.Equipment or CatalogKind.Artifact ? _selectedSlot : null;
        IReadOnlyList<BuildCatalogEntry> entries = GameCatalog.Find(_pickerKind, slot, _pickerSearch);
        if (entries.Count == 0)
        {
            AddEmptyRow(_pickerResults, "No compatible items match this search.");
            return;
        }
        foreach (BuildCatalogEntry entry in entries)
        {
            GameObject row = _ui.Rect("Choice." + entry.Id, _pickerResults, Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero);
            Image background = _ui.Panel(row, BuildCrafterUi.SurfaceRaised);
            BuildCrafterUi.AddLayout(row, 62f);
            Button button = row.AddComponent<Button>();
            button.targetGraphic = background;
            BuildCrafterUi.Wire(button, () => ChooseCatalogEntry(entry));
            AddCenteredCatalogIcon(row.transform, "Icon", entry.Icon, 9f, 44f);
            TextMeshProUGUI text = _ui.TextBlock("Text", row.transform,
                $"{Escape(entry.Name)}\n<size=78%><color=#91A0A3>{Escape(entry.Subtitle)}</color></size>",
                14f, BuildCrafterUi.Text, TextAlignmentOptions.MidlineLeft, richText: true);
            text.rectTransform.offsetMin = new Vector2(64f, 4f);
            text.rectTransform.offsetMax = new Vector2(-10f, -4f);
        }
    }

    private static void RenderSkillContext(Transform panel)
    {
        BuildCatalogEntry skill = CurrentSkillEntries().FirstOrDefault(entry =>
            entry.Id.Equals(_selectedSkill, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
        {
            AddSectionHeading(panel, "SKILL NOTES", "Nothing selected", -18f);
            AddEmptyState(panel, "Select a skill to read its client description and adjust its planned level.");
            return;
        }
        int level = _current.Skills.TryGetValue(skill.Id, out int value) ? value : 0;
        AddSectionHeading(panel, skill.Name.ToUpperInvariant(), $"Level {level} of {skill.MaxLevel}", -18f);
        AddTopCatalogIcon(panel, "SkillIcon", skill.Icon, 20f, -76f, 60f);
        TextMeshProUGUI meta = _ui.TextBlock("SkillMeta", panel, Escape(SkillSubtitle(skill)), 12f,
            BuildCrafterUi.TextMuted, TextAlignmentOptions.TopLeft, richText: true);
        PlaceTop(meta.rectTransform, 94f, -78f, -20f, 58f);
        TextMeshProUGUI description = _ui.TextBlock("Description", panel,
            string.IsNullOrWhiteSpace(skill.Description)
                ? "No description is included in the current client data." : Escape(skill.Description),
            15f, BuildCrafterUi.Text, TextAlignmentOptions.TopLeft, richText: true);
        description.rectTransform.offsetMin = new Vector2(20f, 280f);
        description.rectTransform.offsetMax = new Vector2(-20f, -206f);
        AddActionButton(panel, "Minus", "−1", 20f, -148f, 86f, 38f,
            () => SetSkillLevel(skill, level - 1));
        AddActionButton(panel, "Plus", "+1", 114f, -148f, 86f, 38f,
            () => SetSkillLevel(skill, level + 1), true);
        AddActionButton(panel, "Max", "MAX", 208f, -148f, 86f, 38f,
            () => SetSkillLevel(skill, skill.MaxLevel));
        AddActionButton(panel, "Reset", "RESET", 302f, -148f, 116f, 38f,
            () => SetSkillLevel(skill, 0));
    }

    private static void RenderLocal()
    {
        BuildCrafterUi.Clear(_contentRoot);
        AddPageTitle("LOCAL BUILDS", "Saved on this machine", _contentRoot);
        TMP_InputField search = _ui.Input("LocalSearch", _contentRoot, "Search saved builds...", value =>
        {
            _localSearch = value;
            RenderLocalRows();
        }, _localSearch, characterLimit: 80);
        RectTransform searchRect = search.GetComponent<RectTransform>();
        searchRect.anchorMin = new Vector2(0f, 1f);
        searchRect.anchorMax = new Vector2(0f, 1f);
        searchRect.offsetMin = new Vector2(24f, -122f);
        searchRect.offsetMax = new Vector2(530f, -80f);
        _activeSearch = search;
        Button create = _ui.Button("NewBuild", _contentRoot, "NEW BUILD", NewBuild, primary: true, fontSize: 13f);
        RectTransform createRect = create.GetComponent<RectTransform>();
        createRect.anchorMin = new Vector2(1f, 1f);
        createRect.anchorMax = new Vector2(1f, 1f);
        createRect.offsetMin = new Vector2(-196f, -122f);
        createRect.offsetMax = new Vector2(-24f, -80f);

        ScrollRect scroll = _ui.Scroll("LocalList", _contentRoot, out _localRows, 7f);
        scroll.GetComponent<RectTransform>().offsetMin = new Vector2(24f, 24f);
        scroll.GetComponent<RectTransform>().offsetMax = new Vector2(-24f, -140f);
        RenderLocalRows();
    }

    private static void RenderLocalRows()
    {
        if (_localRows is null)
            return;
        BuildCrafterUi.Clear(_localRows);
        IEnumerable<BuildDocument> builds = _localBuilds;
        if (!string.IsNullOrWhiteSpace(_localSearch))
        {
            string term = _localSearch.Trim();
            builds = builds.Where(build => string.Join(" ", build.Name, build.Class, build.SourceAuthor,
                build.Notes).Contains(term, StringComparison.OrdinalIgnoreCase));
        }
        List<BuildDocument> visible = builds.ToList();
        if (visible.Count == 0)
        {
            AddEmptyRow(_localRows, _localBuilds.Count == 0
                ? "No local builds yet. Start in Craft, then press Save Local."
                : "No saved builds match this search.", 110f);
            return;
        }

        foreach (BuildDocument build in visible)
        {
            GameObject row = _ui.Rect("Local." + build.Id, _localRows, Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero);
            _ui.Panel(row, build.Id == _current.Id ? BuildCrafterUi.AccentMuted : BuildCrafterUi.SurfaceRaised);
            BuildCrafterUi.AddLayout(row, 82f);
            string source = string.IsNullOrWhiteSpace(build.SourceAuthor)
                ? "Local build" : $"Imported from {build.SourceAuthor}";
            TextMeshProUGUI text = _ui.TextBlock("Text", row.transform,
                $"<b>{Escape(build.Name)}</b>\n<size=82%><color=#91A0A3>{Escape(build.Class)}  •  Level {build.BaseLevel}  •  {Escape(source)}  •  {build.UpdatedUtc.ToLocalTime():MMM d, yyyy}</color></size>",
                15f, BuildCrafterUi.Text, TextAlignmentOptions.MidlineLeft, richText: true);
            text.rectTransform.offsetMin = new Vector2(16f, 5f);
            text.rectTransform.offsetMax = new Vector2(-278f, -5f);
            AddRowButton(row.transform, "Open", "OPEN", 82f, () => LoadBuild(build), true, 112f);
            AddRowButton(row.transform, "Copy", "DUPLICATE", 96f, () => DuplicateBuild(build));
        }
    }

    private static void RenderCommunity()
    {
        BuildCrafterUi.Clear(_contentRoot);
        AddPageTitle("COMMUNITY BUILDS", "Public builds from spiritvalers.com", _contentRoot);
        TMP_InputField search = _ui.Input("CommunitySearch", _contentRoot,
            "Search name, class, or author...", value =>
            {
                _communitySearch = value;
                _communitySearchPending = true;
                _communitySearchAt = Time.unscaledTime + 0.35f;
                SetStatus("Preparing a fresh SpiritValers search...", BuildCrafterUi.Accent);
            }, _communitySearch, characterLimit: 80);
        RectTransform searchRect = search.GetComponent<RectTransform>();
        searchRect.anchorMin = new Vector2(0f, 1f);
        searchRect.anchorMax = new Vector2(0f, 1f);
        searchRect.offsetMin = new Vector2(24f, -122f);
        searchRect.offsetMax = new Vector2(600f, -80f);
        _activeSearch = search;
        Button refresh = _ui.Button("Refresh", _contentRoot,
            _communityLoading ? "LOADING..." : "REFRESH", RefreshCommunity, primary: true, fontSize: 13f);
        refresh.interactable = !_communityLoading && Plugin.Settings.EnableCommunityBuilds.Value;
        RectTransform refreshRect = refresh.GetComponent<RectTransform>();
        refreshRect.anchorMin = new Vector2(1f, 1f);
        refreshRect.anchorMax = new Vector2(1f, 1f);
        refreshRect.offsetMin = new Vector2(-196f, -122f);
        refreshRect.offsetMax = new Vector2(-24f, -80f);

        TextMeshProUGUI privacy = _ui.TextBlock("Privacy", _contentRoot,
            "Read-only public gallery. No SpiritValers account or upload access is used.", 12f,
            BuildCrafterUi.TextMuted, TextAlignmentOptions.MidlineRight);
        privacy.rectTransform.anchorMin = new Vector2(1f, 1f);
        privacy.rectTransform.anchorMax = new Vector2(1f, 1f);
        privacy.rectTransform.offsetMin = new Vector2(-710f, -165f);
        privacy.rectTransform.offsetMax = new Vector2(-24f, -137f);

        ScrollRect scroll = _ui.Scroll("CommunityList", _contentRoot, out _communityRows, 7f);
        scroll.GetComponent<RectTransform>().offsetMin = new Vector2(24f, 24f);
        scroll.GetComponent<RectTransform>().offsetMax = new Vector2(-24f, -170f);
        RenderCommunityRows();
    }

    private static void RenderCommunityRows()
    {
        if (_communityRows is null)
            return;
        BuildCrafterUi.Clear(_communityRows);
        if (!Plugin.Settings.EnableCommunityBuilds.Value)
        {
            AddEmptyRow(_communityRows,
                "Community builds are disabled. Enable SpiritValers in Mod Configuration Menu to browse them.", 110f);
            return;
        }
        if (_communityLoading && _communityBuilds.Count == 0)
        {
            for (int i = 0; i < 5; i++)
                AddEmptyRow(_communityRows, "Loading public build details...", 66f);
            return;
        }
        List<CommunityBuildSummary> visible = _communityBuilds.ToList();
        if (visible.Count == 0)
        {
            AddEmptyRow(_communityRows, string.IsNullOrWhiteSpace(_communitySearch)
                ? "No community builds are available. Press Refresh to try again."
                : "SpiritValers returned no public builds for this search.", 110f);
            return;
        }
        foreach (CommunityBuildSummary build in visible)
        {
            GameObject row = _ui.Rect("Community." + build.Id, _communityRows, Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero);
            _ui.Panel(row, BuildCrafterUi.SurfaceRaised);
            BuildCrafterUi.AddLayout(row, 78f);
            string author = string.IsNullOrWhiteSpace(build.Author) ? "Unknown author" : build.Author;
            TextMeshProUGUI text = _ui.TextBlock("Text", row.transform,
                $"<b>{Escape(string.IsNullOrWhiteSpace(build.Name) ? "Unnamed build" : build.Name)}</b>\n" +
                $"<size=82%><color=#91A0A3>{Escape(build.Class)}  •  by {Escape(author)}  •  updated {build.UpdatedAt.ToLocalTime():MMM d}</color></size>",
                15f, BuildCrafterUi.Text, TextAlignmentOptions.MidlineLeft, richText: true);
            text.rectTransform.offsetMin = new Vector2(16f, 5f);
            text.rectTransform.offsetMax = new Vector2(-194f, -5f);
            AddRowButton(row.transform, "Import", "IMPORT COPY", 150f, () => ImportCommunityBuild(build), true);
        }
    }

    private static void RefreshCommunity()
    {
        if (!Plugin.Settings.EnableCommunityBuilds.Value)
        {
            SetStatus("Community access is disabled in settings.", BuildCrafterUi.Warning);
            return;
        }
        _communitySearchPending = false;
        int requestVersion = ++_communityRequestVersion;
        _communityLoading = true;
        _communityBuilds = Array.Empty<CommunityBuildSummary>();
        SetStatus(string.IsNullOrWhiteSpace(_communitySearch)
            ? "Loading fresh public builds from SpiritValers..."
            : $"Searching SpiritValers for '{_communitySearch.Trim()}'...", BuildCrafterUi.Accent);
        if (_section == Section.Community)
            RenderCommunity();
        CancellationToken token = SpiritValersClient.BeginRequest(out _);
        _ = RefreshCommunityAsync(_communitySearch, requestVersion, token);
    }

    private static async Task RefreshCommunityAsync(string search, int requestVersion,
        CancellationToken token)
    {
        try
        {
            IReadOnlyList<CommunityBuildSummary> builds = await SpiritValersClient.GetCommunityBuildsAsync(
                Plugin.Settings.CommunityLimit.Value, search, token).ConfigureAwait(false);
            MainThread.Enqueue(() =>
            {
                if (requestVersion != _communityRequestVersion)
                    return;
                _communityBuilds = builds;
                _communityLoading = false;
                SetStatus(string.IsNullOrWhiteSpace(search)
                    ? $"Loaded {builds.Count:N0} current public builds."
                    : $"SpiritValers returned {builds.Count:N0} matching builds.", BuildCrafterUi.Positive);
                if (_open && _section == Section.Community)
                    RenderCommunity();
            });
        }
        catch (OperationCanceledException)
        {
            MainThread.Enqueue(() =>
            {
                if (requestVersion != _communityRequestVersion)
                    return;
                _communityLoading = false;
                SetStatus("The SpiritValers request timed out.", BuildCrafterUi.Warning);
                if (_open && _section == Section.Community)
                    RenderCommunity();
            });
        }
        catch (Exception ex)
        {
            MainThread.Enqueue(() =>
            {
                if (requestVersion != _communityRequestVersion)
                    return;
                _communityLoading = false;
                Plugin.Warning($"Community gallery request failed: {ex.Message}");
                SetStatus("Could not reach SpiritValers. Your local builds are still available.",
                    BuildCrafterUi.Warning);
                if (_open && _section == Section.Community)
                    RenderCommunity();
            });
        }
    }

    private static void ImportCommunityBuild(CommunityBuildSummary summary)
    {
        SetStatus($"Importing {summary.Name}...", BuildCrafterUi.Accent);
        CancellationToken token = SpiritValersClient.BeginRequest(out _);
        _ = ImportCommunityBuildAsync(summary, token);
    }

    private static async Task ImportCommunityBuildAsync(CommunityBuildSummary summary,
        CancellationToken token)
    {
        try
        {
            BuildDocument build = await SpiritValersClient.GetBuildAsync(summary.Id, token)
                .ConfigureAwait(false);
            MainThread.Enqueue(() =>
            {
                ResolveBuildNames(build);
                _current = build;
                _dirty = true;
                _section = Section.Craft;
                _craftView = CraftView.Loadout;
                _selectedSlot = string.Empty;
                ClearComparison();
                SetStatus($"Community build imported with {build.Skills.Count:N0} allocated skills.",
                    BuildCrafterUi.Positive);
                if (_open) Render();
            });
        }
        catch (Exception ex)
        {
            MainThread.Enqueue(() =>
            {
                Plugin.Warning($"Community build import failed: {ex.Message}");
                SetStatus(ex is OperationCanceledException
                    ? "The build import timed out."
                    : "That community build could not be imported.", BuildCrafterUi.Warning);
            });
        }
    }

    private static void ChooseCatalogEntry(BuildCatalogEntry entry)
    {
        bool artifactSlot = BuildSlots.Artifacts.Contains(_selectedSlot);
        bool replacedItem = false;
        if (_pickerKind == CatalogKind.Equipment)
        {
            _current.Equipment[_selectedSlot] = new PlannedItem
            {
                ItemId = entry.Id,
                DisplayName = entry.Name,
                Cards = new List<string>()
            };
            replacedItem = true;
        }
        else if (_pickerKind == CatalogKind.Artifact)
        {
            _current.Artifacts[_selectedSlot] = new PlannedArtifact
            {
                ItemId = entry.Id,
                DisplayName = entry.Name
            };
            replacedItem = true;
        }
        else if (_pickerKind == CatalogKind.Card
            && _current.Equipment.TryGetValue(_selectedSlot, out PlannedItem item))
        {
            BuildCatalogEntry equipment = GameCatalog.Resolve(item.ItemId, CatalogKind.Equipment);
            int capacity = Math.Max(0, equipment?.SocketCount ?? 0);
            item.Cards ??= new List<string>();
            if (capacity > 0 && item.Cards.Count >= capacity)
            {
                SetStatus($"{DisplayName(item)} has {capacity} card slot{(capacity == 1 ? string.Empty : "s")}.",
                    BuildCrafterUi.Warning);
                return;
            }
            item.Cards.Add(entry.Id);
        }
        else if (_pickerKind == CatalogKind.Gem && artifactSlot
            && _current.Artifacts.TryGetValue(_selectedSlot, out PlannedArtifact artifact))
        {
            artifact.GemId = entry.Id;
            artifact.GemRefine = 0;
        }
        MarkDirty();
        _pickerSearch = string.Empty;
        if (replacedItem)
            _itemContextView = ItemContextView.Details;
        RenderCraft();
    }

    private static void ChangeRefine(int delta)
    {
        PlannedItem item = SelectedItem();
        if (item is null)
            return;
        item.Refine = Math.Clamp(item.Refine + delta, 0, 10);
        MarkDirty();
        RenderCraft();
    }

    private static void ClearSelectedItem()
    {
        if (BuildSlots.Artifacts.Contains(_selectedSlot))
            _current.Artifacts.Remove(_selectedSlot);
        else
            _current.Equipment.Remove(_selectedSlot);
        _itemContextView = ItemContextView.Replace;
        _pickerKind = BuildSlots.Artifacts.Contains(_selectedSlot)
            ? CatalogKind.Artifact : CatalogKind.Equipment;
        MarkDirty();
        RenderCraft();
    }

    private static void ClearAttachments(bool artifactSlot)
    {
        if (artifactSlot && _current.Artifacts.TryGetValue(_selectedSlot, out PlannedArtifact artifact))
        {
            artifact.GemId = string.Empty;
            artifact.GemRefine = 0;
        }
        else if (_current.Equipment.TryGetValue(_selectedSlot, out PlannedItem item))
            item.Cards?.Clear();
        MarkDirty();
        RenderCraft();
    }

    private static void AddSubstat()
    {
        PlannedItem item = SelectedItem();
        if (item is null)
            return;
        item.Substats ??= new List<ImportedSubstat>();
        if (item.Substats.Count >= 6)
        {
            SetStatus("An item can have up to six planned substats.", BuildCrafterUi.Warning);
            return;
        }
        IReadOnlyList<string> types = GameCatalog.AvailableSubstatTypes();
        string type = types.FirstOrDefault(candidate => !item.Substats.Any(substat =>
            substat.Stat.Equals(candidate, StringComparison.OrdinalIgnoreCase))) ?? types.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(type))
        {
            SetStatus("No substat types were found in the installed game data.", BuildCrafterUi.Warning);
            return;
        }
        item.Substats.Add(new ImportedSubstat { Stat = type, Base = 1f });
        _selectedSubstatIndex = item.Substats.Count - 1;
        MarkDirty();
        RenderCraft();
    }

    private static void CycleSubstatType(int direction)
    {
        PlannedItem item = SelectedItem();
        if (item?.Substats is null || _selectedSubstatIndex < 0
            || _selectedSubstatIndex >= item.Substats.Count)
            return;
        IReadOnlyList<string> types = GameCatalog.AvailableSubstatTypes();
        if (types.Count == 0)
            return;
        ImportedSubstat selected = item.Substats[_selectedSubstatIndex];
        int current = types.ToList().FindIndex(type => type.Equals(selected.Stat,
            StringComparison.OrdinalIgnoreCase));
        current = current < 0 ? 0 : current;
        for (int offset = 1; offset <= types.Count; offset++)
        {
            int index = (current + direction * offset % types.Count + types.Count) % types.Count;
            string candidate = types[index];
            bool used = item.Substats.Where((_, index) => index != _selectedSubstatIndex)
                .Any(substat => substat.Stat.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (used)
                continue;
            selected.Stat = candidate;
            MarkDirty();
            RenderCraft();
            return;
        }
        SetStatus("All available substat types are already used on this item.", BuildCrafterUi.Warning);
    }

    private static void AdjustSubstatValue(float delta)
    {
        PlannedItem item = SelectedItem();
        if (item?.Substats is null || _selectedSubstatIndex < 0
            || _selectedSubstatIndex >= item.Substats.Count)
            return;
        item.Substats[_selectedSubstatIndex].Base = Math.Clamp(
            item.Substats[_selectedSubstatIndex].Base + delta, -999f, 999f);
        MarkDirty();
        RenderCraft();
    }

    private static void SetSubstatValue(string text)
    {
        PlannedItem item = SelectedItem();
        if (item?.Substats is null || _selectedSubstatIndex < 0
            || _selectedSubstatIndex >= item.Substats.Count)
            return;
        bool parsed = float.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float value)
            || float.TryParse(text, out value);
        if (!parsed)
            return;
        item.Substats[_selectedSubstatIndex].Base = Math.Clamp(value, -999f, 999f);
        MarkDirty();
    }

    private static void RemoveSelectedSubstat()
    {
        PlannedItem item = SelectedItem();
        if (item?.Substats is null || _selectedSubstatIndex < 0
            || _selectedSubstatIndex >= item.Substats.Count)
            return;
        item.Substats.RemoveAt(_selectedSubstatIndex);
        _selectedSubstatIndex = item.Substats.Count == 0
            ? -1 : Math.Min(_selectedSubstatIndex, item.Substats.Count - 1);
        MarkDirty();
        RenderCraft();
    }

    private static void OpenSelectedInEncyclopedia()
    {
        PlannedItem item = SelectedItem();
        if (item is null)
            return;
        Close();
        if (ItemEncyclopediaIntegration.TryOpen(item.ItemId, item.DisplayName, out string message))
        {
            Plugin.Info(message);
            return;
        }
        Open();
        SetStatus(message, BuildCrafterUi.Warning);
    }

    private static PlannedItem SelectedItem() => BuildSlots.Artifacts.Contains(_selectedSlot)
        ? (_current.Artifacts.TryGetValue(_selectedSlot, out PlannedArtifact artifact) ? artifact : null)
        : (_current.Equipment.TryGetValue(_selectedSlot, out PlannedItem equipment) ? equipment : null);

    private static void SetSkillLevel(BuildCatalogEntry skill, int level)
    {
        int current = _current.Skills.TryGetValue(skill.Id, out int allocated) ? allocated : 0;
        int target = Math.Clamp(level, 0, skill.MaxLevel);
        string blockedReason = string.Empty;

        if (target > current)
        {
            while (current < target)
            {
                if (!CanIncreaseSkill(skill, out blockedReason))
                    break;
                current++;
                _current.Skills[skill.Id] = current;
            }
        }
        else if (target <= 0)
        {
            _current.Skills.Remove(skill.Id);
        }
        else
        {
            _current.Skills[skill.Id] = target;
        }

        PruneInvalidSkills();
        MarkDirty();
        RenderCraft();
        if (!string.IsNullOrWhiteSpace(blockedReason))
            SetStatus(blockedReason, BuildCrafterUi.Warning);
    }

    private static bool CanIncreaseSkill(BuildCatalogEntry skill, out string reason)
    {
        int current = _current.Skills.TryGetValue(skill.Id, out int allocated) ? allocated : 0;
        if (current >= skill.MaxLevel)
        {
            reason = $"{skill.Name} is already at its maximum level.";
            return false;
        }

        if (!GameCatalog.IsSkillAvailableForClass(_current.Class, _current.BaseClass, skill.Id))
        {
            reason = $"{skill.Name} was preserved from the imported build but is not in this class tree.";
            return false;
        }

        if (!GameCatalog.RequirementsDisabled(_current.Class, _current.BaseClass, skill.Id))
        {
            foreach ((string requirementId, int requiredLevel) in skill.Requirements)
            {
                int requirementLevel = _current.Skills.TryGetValue(requirementId, out int value) ? value : 0;
                if (requirementLevel >= requiredLevel)
                    continue;
                string name = GameCatalog.Resolve(requirementId, CatalogKind.Skill)?.Name ?? requirementId;
                reason = $"Requires {name} level {requiredLevel}.";
                return false;
            }
        }

        SkillBudget budget = CurrentSkillBudget();
        if (SkillPointsSpent() >= budget.Total)
        {
            reason = $"All {budget.Total} available skill points are allocated.";
            return false;
        }
        if (GameCatalog.IsAdvancedSkill(_current.Class, skill.Id)
            && AdvancedSkillPointsSpent() >= budget.Advanced)
        {
            reason = $"All {budget.Advanced} advanced skill points are allocated.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static SkillBudget CurrentSkillBudget()
    {
        int jobLevel = Math.Max(0, _current.JobLevel);
        int jobCap = GameCatalog.MaxJobLevel(_current.Class);
        if (GameCatalog.IsFreeformClass(_current.Class))
        {
            int total = 50 + Math.Clamp(jobLevel, 0, jobCap);
            return new SkillBudget(total, total);
        }
        if (GameCatalog.IsAdvancedClass(_current.Class))
        {
            int advanced = Math.Clamp(jobLevel, 0, jobCap);
            return new SkillBudget(50 + advanced, advanced);
        }
        return new SkillBudget(Math.Clamp(jobLevel, 0, jobCap), 0);
    }

    private static int SkillPointsSpent() => CurrentSkillEntries().Sum(skill =>
        _current.Skills.TryGetValue(skill.Id, out int value) ? Math.Max(0, value) : 0);

    private static int AdvancedSkillPointsSpent() => CurrentSkillEntries()
        .Where(skill => GameCatalog.IsAdvancedSkill(_current.Class, skill.Id))
        .Sum(skill => _current.Skills.TryGetValue(skill.Id, out int value) ? Math.Max(0, value) : 0);

    private static IReadOnlyList<BuildCatalogEntry> CurrentSkillEntries()
    {
        var entries = GameCatalog.Find(CatalogKind.Skill, null, string.Empty,
            _current.Class, _current.BaseClass).ToList();
        var included = new HashSet<string>(entries.Select(entry => entry.Id),
            StringComparer.OrdinalIgnoreCase);

        foreach ((string skillId, int level) in _current.Skills)
        {
            if (level <= 0)
                continue;
            BuildCatalogEntry resolved = GameCatalog.Resolve(skillId, CatalogKind.Skill);
            if (resolved is not null)
            {
                if (included.Add(resolved.Id))
                    entries.Add(resolved);
                continue;
            }
            if (!included.Add(skillId))
                continue;
            entries.Add(new BuildCatalogEntry
            {
                Id = skillId,
                Name = ImportedSkillName(skillId),
                Kind = CatalogKind.Skill,
                Subtitle = "Imported community allocation",
                Description = "This skill was preserved from the community build but was not found in the installed game catalog.",
                SearchText = skillId,
                MaxLevel = Math.Max(1, level)
            });
        }

        return entries.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string ImportedSkillName(string skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId))
            return "Unknown skill";
        string spaced = skillId.Replace('-', ' ').Replace('_', ' ').Trim();
        return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(spaced);
    }

    private static void PruneInvalidSkills()
    {
        bool changed;
        do
        {
            changed = false;
            foreach (BuildCatalogEntry skill in CurrentSkillEntries())
            {
                if (!_current.Skills.TryGetValue(skill.Id, out int level) || level <= 0
                    || GameCatalog.RequirementsDisabled(_current.Class, _current.BaseClass, skill.Id))
                    continue;
                bool valid = skill.Requirements.All(requirement =>
                    _current.Skills.TryGetValue(requirement.Key, out int value)
                    && value >= requirement.Value);
                if (valid)
                    continue;
                _current.Skills.Remove(skill.Id);
                changed = true;
            }
        } while (changed);
    }

    private static void UpdateJobLevel(int value)
    {
        _current.JobLevel = value;
        TrimSkillsToBudget();
        MarkDirty();
        if (_craftView == CraftView.Skills)
            RenderCraft();
    }

    private static void TrimSkillsToBudget()
    {
        SkillBudget budget = CurrentSkillBudget();
        List<BuildCatalogEntry> entries = CurrentSkillEntries().ToList();
        while (AdvancedSkillPointsSpent() > budget.Advanced)
        {
            BuildCatalogEntry skill = entries.LastOrDefault(entry =>
                GameCatalog.IsAdvancedSkill(_current.Class, entry.Id)
                && _current.Skills.TryGetValue(entry.Id, out int value) && value > 0);
            if (skill is null)
                break;
            DecrementSkill(skill.Id);
        }
        while (SkillPointsSpent() > budget.Total)
        {
            BuildCatalogEntry skill = entries.LastOrDefault(entry =>
                _current.Skills.TryGetValue(entry.Id, out int value) && value > 0);
            if (skill is null)
                break;
            DecrementSkill(skill.Id);
        }
        PruneInvalidSkills();
    }

    private static void DecrementSkill(string skillId)
    {
        if (!_current.Skills.TryGetValue(skillId, out int value))
            return;
        if (value <= 1)
            _current.Skills.Remove(skillId);
        else
            _current.Skills[skillId] = value - 1;
    }

    private static string SkillSubtitle(BuildCatalogEntry skill)
    {
        if (skill.Requirements.Count == 0
            || GameCatalog.RequirementsDisabled(_current.Class, _current.BaseClass, skill.Id))
            return skill.Subtitle;
        string requirements = string.Join(", ", skill.Requirements.Select(requirement =>
        {
            string name = GameCatalog.Resolve(requirement.Key, CatalogKind.Skill)?.Name ?? requirement.Key;
            return $"{name} Lv {requirement.Value}";
        }));
        return $"{skill.Subtitle}  •  Requires {requirements}";
    }

    private static void SaveCurrent()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_current.Name))
                _current.Name = "Unnamed build";
            BuildStorage.Save(_current);
            _dirty = false;
            RefreshLocalBuilds();
            SetStatus($"Saved {_current.Name} locally.", BuildCrafterUi.Positive);
            if (_open)
                RenderNavigation();
        }
        catch (Exception ex)
        {
            Plugin.Error($"Could not save build: {ex}");
            SetStatus("The build could not be saved. Check the BepInEx log.", BuildCrafterUi.Warning);
        }
    }

    private static void RefreshLocalBuilds() => _localBuilds = BuildStorage.LoadAll();

    private static void NewBuild()
    {
        _current = new BuildDocument();
        _dirty = true;
        _section = Section.Craft;
        _craftView = CraftView.Loadout;
        _itemContextView = ItemContextView.Details;
        _selectedSlot = string.Empty;
        _selectedSkill = string.Empty;
        ClearComparison();
        SetStatus("New unsaved build.", BuildCrafterUi.Accent);
        Render();
    }

    private static void LoadBuild(BuildDocument build)
    {
        _current = Clone(build);
        ResolveBuildNames(_current);
        _dirty = false;
        _section = Section.Craft;
        _itemContextView = ItemContextView.Details;
        _selectedSlot = string.Empty;
        ClearComparison();
        SetStatus($"Opened {_current.Name}.", BuildCrafterUi.Positive);
        Render();
    }

    private static void DuplicateBuild(BuildDocument build)
    {
        _current = build.EditableCopy();
        ResolveBuildNames(_current);
        _dirty = true;
        _section = Section.Craft;
        _itemContextView = ItemContextView.Details;
        _selectedSlot = string.Empty;
        ClearComparison();
        SetStatus("Duplicated as an unsaved build.", BuildCrafterUi.Accent);
        Render();
    }

    private static BuildDocument Clone(BuildDocument build)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(build, BuildStorage.JsonOptions);
        BuildDocument clone = System.Text.Json.JsonSerializer.Deserialize<BuildDocument>(json,
            BuildStorage.JsonOptions) ?? new BuildDocument();
        clone.Normalize();
        return clone;
    }

    private static void ChangeClass(int direction)
    {
        IReadOnlyList<string> classes = GameCatalog.PlayableClasses();
        int index = classes.ToList().FindIndex(value => value.Equals(_current.Class,
            StringComparison.OrdinalIgnoreCase));
        index = index < 0 ? 0 : (index + direction + classes.Count) % classes.Count;
        _current.Class = classes[index];
        _current.BaseClass = GameCatalog.IsFreeformClass(_current.Class)
            ? "Warrior" : GameCatalog.ParentClassOf(_current.Class);
        _current.Skills.Clear();
        _selectedSkill = string.Empty;
        MarkDirty();
        RenderCraft();
        RenderNavigation();
    }

    private static void AddAttributeRow(Transform parent, string attribute, float top)
    {
        TextMeshProUGUI label = _ui.TextBlock(attribute + "Label", parent, attribute, 13f,
            BuildCrafterUi.Text, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        PlaceTop(label.rectTransform, 18f, top, -274f, 38f);
        AddSmallButton(parent, attribute + "Minus", "−", 82f, top, 38f, 36f,
            () => SetAttribute(attribute, _current.Attributes[attribute] - 1));
        TMP_InputField input = _ui.Input(attribute + "Input", parent, "1", text =>
        {
            if (int.TryParse(text, out int parsed))
                SetAttribute(attribute, parsed, false);
        }, _current.Attributes[attribute].ToString(), characterLimit: 2);
        RectTransform rect = input.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.offsetMin = new Vector2(126f, top - 36f);
        rect.offsetMax = new Vector2(218f, top);
        AddSmallButton(parent, attribute + "Plus", "+", 224f, top, 38f, 36f,
            () => SetAttribute(attribute, _current.Attributes[attribute] + 1));
        TextMeshProUGUI hint = _ui.TextBlock(attribute + "Hint", parent, "/ 99", 12f,
            BuildCrafterUi.TextMuted, TextAlignmentOptions.MidlineLeft);
        PlaceTop(hint.rectTransform, 274f, top, -12f, 36f);
    }

    private static void SetAttribute(string attribute, int value, bool rerender = true)
    {
        _current.Attributes[attribute] = Math.Clamp(value, 1, 99);
        MarkDirty();
        if (rerender)
            RenderCraft();
    }

    private static void AddNumericField(Transform parent, string labelText, int current, int min,
        int max, float top, Action<int> setter)
    {
        TextMeshProUGUI label = _ui.TextBlock(labelText, parent, labelText, 11f,
            BuildCrafterUi.TextMuted, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        PlaceTop(label.rectTransform, 18f, top, -190f, 38f);
        TMP_InputField input = _ui.Input(labelText + "Input", parent, min.ToString(), text =>
        {
            if (int.TryParse(text, out int value))
                setter(Math.Clamp(value, min, max));
        }, current.ToString(), characterLimit: 3);
        RectTransform rect = input.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.offsetMin = new Vector2(158f, top - 38f);
        rect.offsetMax = new Vector2(258f, top);
        TextMeshProUGUI cap = _ui.TextBlock(labelText + "Cap", parent, $"/ {max}", 12f,
            BuildCrafterUi.TextMuted, TextAlignmentOptions.MidlineLeft);
        PlaceTop(cap.rectTransform, 268f, top, -10f, 38f);
    }

    private static void AddSectionHeading(Transform parent, string heading, string subheading, float top)
    {
        TextMeshProUGUI title = _ui.TextBlock("Heading." + heading, parent, heading, 14f,
            BuildCrafterUi.Accent, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        PlaceTop(title.rectTransform, 18f, top, -18f, 28f);
        TextMeshProUGUI subtitle = _ui.TextBlock("Subheading." + heading, parent, subheading, 11f,
            BuildCrafterUi.TextMuted, TextAlignmentOptions.MidlineRight);
        PlaceTop(subtitle.rectTransform, 130f, top, -18f, 28f);
        GameObject line = _ui.DividerLine("HeadingLine", parent);
        PlaceTop(line.GetComponent<RectTransform>(), 18f, top - 37f, -18f, 1f);
    }

    private static void AddPageTitle(string title, string subtitle, Transform parent)
    {
        TextMeshProUGUI heading = _ui.TextBlock("PageTitle", parent, title, 25f,
            BuildCrafterUi.TextStrong, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        PlaceTop(heading.rectTransform, 24f, -18f, -700f, 46f);
        TextMeshProUGUI sub = _ui.TextBlock("PageSubtitle", parent, subtitle, 13f,
            BuildCrafterUi.TextMuted, TextAlignmentOptions.MidlineRight);
        PlaceTop(sub.rectTransform, 600f, -18f, -24f, 46f);
    }

    private static void AddEmptyState(Transform parent, string text)
    {
        TextMeshProUGUI empty = _ui.TextBlock("Empty", parent, text, 15f,
            BuildCrafterUi.TextMuted, TextAlignmentOptions.Center);
        empty.rectTransform.offsetMin = new Vector2(38f, 160f);
        empty.rectTransform.offsetMax = new Vector2(-38f, -120f);
    }

    private static void AddEmptyRow(Transform parent, string text, float height = 80f)
    {
        GameObject row = _ui.Rect("Empty", parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        _ui.Panel(row, BuildCrafterUi.SurfaceRaised);
        BuildCrafterUi.AddLayout(row, height);
        TextMeshProUGUI label = _ui.TextBlock("Text", row.transform, text, 14f,
            BuildCrafterUi.TextMuted, TextAlignmentOptions.Center);
        label.rectTransform.offsetMin = new Vector2(18f, 8f);
        label.rectTransform.offsetMax = new Vector2(-18f, -8f);
    }

    private static Button AddSmallButton(Transform parent, string name, string label, float left,
        float top, float width, float height, Action action)
    {
        Button button = _ui.Button(name, parent, label, action, fontSize: 17f);
        RectTransform rect = button.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.offsetMin = new Vector2(left, top - height);
        rect.offsetMax = new Vector2(left + width, top);
        return button;
    }

    private static void AddRightSmallButton(Transform parent, string name, string label, float right,
        float top, float width, float height, Action action)
    {
        Button button = _ui.Button(name, parent, label, action, fontSize: 17f);
        RectTransform rect = button.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.offsetMin = new Vector2(-right - width, top - height);
        rect.offsetMax = new Vector2(-right, top);
    }

    private static Button AddActionButton(Transform parent, string name, string label, float left,
        float top, float width, float height, Action action, bool primary = false)
    {
        Button button = _ui.Button(name, parent, label, action, primary: primary, fontSize: 11f);
        RectTransform rect = button.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.offsetMin = new Vector2(left, top - height);
        rect.offsetMax = new Vector2(left + width, top);
        return button;
    }

    private static void AddRowButton(Transform row, string name, string label, float width,
        Action action, bool primary = false, float right = 14f)
    {
        Button button = _ui.Button(name, row, label, action, primary: primary, fontSize: 12f);
        RectTransform rect = button.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 0.5f);
        rect.anchorMax = new Vector2(1f, 0.5f);
        rect.offsetMin = new Vector2(-width - right, -19f);
        rect.offsetMax = new Vector2(-right, 19f);
    }

    private static void AddRowStepper(Transform row, int value, int min, int max, Action<int> setter)
    {
        Button minus = _ui.Button("Minus", row, "−", () => setter(Math.Max(min, value - 1)), fontSize: 16f);
        RectTransform minusRect = minus.GetComponent<RectTransform>();
        minusRect.anchorMin = new Vector2(1f, 0.5f);
        minusRect.anchorMax = new Vector2(1f, 0.5f);
        minusRect.offsetMin = new Vector2(-134f, -18f);
        minusRect.offsetMax = new Vector2(-98f, 18f);
        minus.interactable = value > min;
        TextMeshProUGUI level = _ui.TextBlock("Level", row, value.ToString(), 15f,
            value > 0 ? BuildCrafterUi.Accent : BuildCrafterUi.TextMuted,
            TextAlignmentOptions.Center, FontStyles.Bold);
        level.rectTransform.anchorMin = new Vector2(1f, 0.5f);
        level.rectTransform.anchorMax = new Vector2(1f, 0.5f);
        level.rectTransform.offsetMin = new Vector2(-92f, -18f);
        level.rectTransform.offsetMax = new Vector2(-54f, 18f);
        Button plus = _ui.Button("Plus", row, "+", () => setter(Math.Min(max, value + 1)), fontSize: 16f);
        RectTransform rect = plus.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 0.5f);
        rect.anchorMax = new Vector2(1f, 0.5f);
        rect.offsetMin = new Vector2(-50f, -18f);
        rect.offsetMax = new Vector2(-14f, 18f);
        plus.interactable = value < max;
    }

    private static void AddCenteredCatalogIcon(Transform parent, string name, Sprite sprite,
        float left, float size)
    {
        GameObject frame = _ui.Rect(name + "Frame", parent,
            new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
            new Vector2(left, -size * 0.5f), new Vector2(left + size, size * 0.5f));
        AddCatalogIconContents(frame, name, sprite);
    }

    private static void AddTopCatalogIcon(Transform parent, string name, Sprite sprite,
        float left, float top, float size)
    {
        GameObject frame = _ui.Rect(name + "Frame", parent,
            new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(left, top - size), new Vector2(left + size, top));
        AddCatalogIconContents(frame, name, sprite);
    }

    private static void AddFractionCatalogIcon(Transform parent, string name, Sprite sprite,
        float anchorX, float top, float size)
    {
        GameObject frame = _ui.Rect(name + "Frame", parent,
            new Vector2(anchorX, 1f), new Vector2(anchorX, 1f),
            new Vector2(-size * 0.5f, top - size), new Vector2(size * 0.5f, top));
        AddCatalogIconContents(frame, name, sprite);
    }

    private static void AddCatalogIconContents(GameObject frame, string name, Sprite sprite)
    {
        Image background = _ui.Panel(frame, BuildCrafterUi.SurfaceLow);
        background.raycastTarget = false;
        Image icon = _ui.Rect(name, frame.transform, Vector2.zero, Vector2.one,
            new Vector2(4f, 4f), new Vector2(-4f, -4f)).AddComponent<Image>();
        icon.sprite = sprite;
        icon.preserveAspect = true;
        icon.color = sprite is null ? new Color(1f, 1f, 1f, 0f) : Color.white;
        icon.raycastTarget = false;
        if (sprite is not null)
            return;
        TextMeshProUGUI placeholder = _ui.TextBlock("Placeholder", frame.transform, "?", 16f,
            BuildCrafterUi.TextMuted, TextAlignmentOptions.Center, FontStyles.Bold);
        placeholder.rectTransform.offsetMin = new Vector2(3f, 3f);
        placeholder.rectTransform.offsetMax = new Vector2(-3f, -3f);
    }

    private static IReadOnlyList<string> ItemStatLines(BuildCatalogEntry resolved, PlannedItem planned)
    {
        var lines = new List<string>();
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (resolved is not null)
        {
            foreach (string stat in resolved.StatLines)
                if (!string.IsNullOrWhiteSpace(stat) && included.Add(stat.Trim()))
                    lines.Add(stat.Trim());
            foreach (string effect in (resolved.Description ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = effect.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed) && included.Add(trimmed))
                    lines.Add(trimmed);
            }
        }
        if (planned?.Substats is not null)
        {
            foreach (ImportedSubstat substat in planned.Substats)
            {
                if (substat is null || string.IsNullOrWhiteSpace(substat.Stat))
                    continue;
                string sign = substat.Base > 0f ? "+" : string.Empty;
                string quality = string.IsNullOrWhiteSpace(substat.Quality)
                    ? string.Empty : $" ({ImportedSkillName(substat.Quality)})";
                string line = $"{ImportedSkillName(substat.Stat)} {sign}{substat.Base:0.##}{quality}";
                if (included.Add(line))
                    lines.Add(line);
            }
        }
        if (lines.Count == 0)
            lines.Add("No stat data is available in the installed game catalog for this item.");
        return lines;
    }

    private static void AddItemStatRow(Transform parent, string line)
    {
        GameObject row = _ui.Rect("Stat", parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        float height = line.Length > 82 ? 54f : line.Length > 46 ? 38f : 26f;
        BuildCrafterUi.AddLayout(row, height);
        TextMeshProUGUI text = _ui.TextBlock("Text", row.transform, "• " + Escape(line), 12.5f,
            BuildCrafterUi.Text, TextAlignmentOptions.TopLeft, richText: true);
        text.rectTransform.offsetMin = new Vector2(2f, 2f);
        text.rectTransform.offsetMax = new Vector2(-8f, -2f);
    }

    private static string AttachmentSummary(PlannedItem item, bool artifact)
    {
        if (artifact && item is PlannedArtifact art)
            return string.IsNullOrWhiteSpace(art.GemId) ? "No gem selected" : $"Gem: {DisplayAttachment(art.GemId, CatalogKind.Gem)}";
        if (item.Cards is null || item.Cards.Count == 0)
            return "No cards selected";
        return "Cards: " + string.Join(", ", item.Cards.Select(id => DisplayAttachment(id, CatalogKind.Card)));
    }

    private static string DisplayAttachment(string id, CatalogKind kind) =>
        GameCatalog.Resolve(id, kind)?.Name ?? id;

    private static string DisplayName(PlannedItem item) => !string.IsNullOrWhiteSpace(item.DisplayName)
        ? item.DisplayName : GameCatalog.Resolve(item.ItemId)?.Name ?? item.ItemId;

    private static void ResolveBuildNames(BuildDocument build)
    {
        build.Normalize();
        foreach ((string slot, PlannedItem item) in build.Equipment)
        {
            if (item is null) continue;
            BuildCatalogEntry resolved = GameCatalog.Resolve(item.ItemId, CatalogKind.Equipment)
                ?? GameCatalog.Resolve(item.DisplayName, CatalogKind.Equipment);
            if (resolved is not null)
            {
                item.ItemId = resolved.Id;
                item.DisplayName = resolved.Name;
            }
            item.Cards ??= new List<string>();
            item.Substats ??= new List<ImportedSubstat>();
        }
        foreach ((string slot, PlannedArtifact item) in build.Artifacts)
        {
            if (item is null) continue;
            string titleSlot = char.ToUpperInvariant(slot[0]) + slot[1..];
            BuildCatalogEntry resolved = GameCatalog.Resolve(item.ItemId + ":" + titleSlot,
                CatalogKind.Artifact) ?? GameCatalog.Resolve(item.ItemId, CatalogKind.Artifact)
                ?? GameCatalog.Resolve(item.DisplayName, CatalogKind.Artifact);
            if (resolved is not null)
            {
                item.ItemId = resolved.Id;
                item.DisplayName = resolved.Name;
            }
            item.Cards ??= new List<string>();
            item.Substats ??= new List<ImportedSubstat>();
        }

        var resolvedSkills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach ((string skillId, int level) in build.Skills)
        {
            if (level <= 0)
                continue;
            BuildCatalogEntry resolved = GameCatalog.Resolve(skillId, CatalogKind.Skill);
            string resolvedId = resolved?.Id ?? skillId;
            resolvedSkills[resolvedId] = Math.Max(
                resolvedSkills.TryGetValue(resolvedId, out int existing) ? existing : 0, level);
        }
        build.Skills = resolvedSkills;
    }

    private static void MarkDirty()
    {
        _dirty = true;
        SetStatus("Unsaved changes", BuildCrafterUi.Accent);
    }

    private static void SetStatus(string message, Color color)
    {
        if (_statusText is null)
            return;
        _statusText.text = message ?? string.Empty;
        _statusText.color = color;
    }

    private static string Escape(string value) => (value ?? string.Empty)
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static void PlaceTop(RectTransform rect, float left, float top, float right, float height)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.offsetMin = new Vector2(left, top - height);
        rect.offsetMax = new Vector2(right, top);
    }

    private static void PlaceTopFraction(RectTransform rect, float anchorMinX, float anchorMaxX,
        float top, float height, float leftInset, float rightInset)
    {
        rect.anchorMin = new Vector2(anchorMinX, 1f);
        rect.anchorMax = new Vector2(anchorMaxX, 1f);
        rect.offsetMin = new Vector2(leftInset, top - height);
        rect.offsetMax = new Vector2(-rightInset, top);
    }

    private static void CompareHoveredItem()
    {
        if (!TryGetHoveredComparison(out PlannedItem hovered, out BuildCatalogEntry resolved,
            out string slot, out IReadOnlyList<string> statLines))
        {
            if (!_open)
                Open();
            SetStatus("Hover equipment or an artifact, then press the compare hotkey.",
                BuildCrafterUi.Warning);
            return;
        }

        _comparisonItem = hovered;
        _comparisonResolved = resolved;
        _comparisonSlot = slot;
        _comparisonStatLines = statLines;
        _section = Section.Craft;
        _craftView = CraftView.Loadout;
        _selectedSlot = slot;
        _selectedSkill = string.Empty;
        _pickerSearch = string.Empty;
        _itemContextView = ItemContextView.Compare;
        if (!_open)
            Open();
        else
            Render();

        PlannedItem planned = BuildSlots.Artifacts.Contains(slot)
            ? (_current.Artifacts.TryGetValue(slot, out PlannedArtifact artifact) ? artifact : null)
            : (_current.Equipment.TryGetValue(slot, out PlannedItem equipment) ? equipment : null);
        string target = planned is null ? "an empty build slot" : DisplayName(planned);
        SetStatus($"Comparing {DisplayName(hovered)} with {target}.", BuildCrafterUi.Positive);
    }

    private static void ClearComparison()
    {
        _comparisonSlot = string.Empty;
        _comparisonItem = null;
        _comparisonResolved = null;
        _comparisonStatLines = Array.Empty<string>();
        if (_itemContextView == ItemContextView.Compare)
            _itemContextView = ItemContextView.Details;
    }

    private static bool TryGetHoveredComparison(out PlannedItem hovered,
        out BuildCatalogEntry resolved, out string slot, out IReadOnlyList<string> statLines)
    {
        hovered = null;
        resolved = null;
        slot = string.Empty;
        statLines = Array.Empty<string>();
        EventSystem eventSystem = EventSystem.current;
        if (eventSystem is null)
            return false;

        var pointer = new PointerEventData(eventSystem) { position = Input.mousePosition };
        var hits = new Il2CppRaycastList();
        eventSystem.RaycastAll(pointer, hits);
        foreach (RaycastResult hit in hits)
        {
            GameObject target = hit.gameObject;
            UIInventoryItem uiItem = target?.GetComponentInParent<UIInventoryItem>();
            IInfoDrawable source = uiItem?.Data;
            if (source is null)
                continue;

            try
            {
                if (source.TryCast<EquipData>() is { } equip
                    && TryCreateEquipmentComparison(target, uiItem, equip, out hovered,
                        out resolved, out slot, out statLines))
                    return true;
                if (source.TryCast<ArtifactData>() is { } artifact
                    && TryCreateArtifactComparison(target, uiItem, artifact, out hovered,
                        out resolved, out slot, out statLines))
                    return true;
            }
            catch (Exception ex)
            {
                Plugin.Warning($"Could not read hovered item data: {ex.Message}");
            }
        }
        return false;
    }

    private static bool TryCreateEquipmentComparison(GameObject target, UIInventoryItem uiItem,
        EquipData equip, out PlannedItem hovered, out BuildCatalogEntry resolved, out string slot,
        out IReadOnlyList<string> statLines)
    {
        hovered = null;
        resolved = GameCatalog.Resolve(equip.Id, CatalogKind.Equipment);
        slot = string.Empty;
        statLines = Array.Empty<string>();
        if (resolved is null)
            return false;

        UIEquipSlotItem equippedSlot = target?.GetComponentInParent<UIEquipSlotItem>();
        slot = equippedSlot is null ? PreferredComparisonSlot(resolved.Slot)
            : BuildSlot(equippedSlot.Slot);
        if (string.IsNullOrWhiteSpace(slot) || !BuildSlots.Equipment.Contains(slot))
            return false;

        var planned = new PlannedItem
        {
            ItemId = resolved.Id,
            DisplayName = resolved.Name,
            Refine = Math.Max(0, equip.Refine),
            Substats = CopySubstats(equip.Substats),
            Cards = new List<string>()
        };
        if (equip.Cards is not null)
        {
            foreach (string card in equip.Cards)
                if (!string.IsNullOrWhiteSpace(card))
                    planned.Cards.Add(card);
        }
        hovered = planned;
        statLines = NativeEquipmentStatLines(equip, uiItem);
        return true;
    }

    private static bool TryCreateArtifactComparison(GameObject target, UIInventoryItem uiItem,
        ArtifactData artifact, out PlannedItem hovered, out BuildCatalogEntry resolved,
        out string slot, out IReadOnlyList<string> statLines)
    {
        hovered = null;
        resolved = null;
        statLines = Array.Empty<string>();
        UIArtifactSlotItem equippedSlot = target?.GetComponentInParent<UIArtifactSlotItem>();
        ArtifactSlot artifactSlot = equippedSlot is null ? artifact.Slot : equippedSlot.Slot;
        slot = artifactSlot.ToString().ToLowerInvariant();
        if (!BuildSlots.Artifacts.Contains(slot))
            return false;

        string id = artifact.Id ?? string.Empty;
        resolved = GameCatalog.Resolve(id, CatalogKind.Artifact)
            ?? GameCatalog.Resolve($"{id}:{artifactSlot}", CatalogKind.Artifact)
            ?? GameCatalog.Find(CatalogKind.Artifact, slot, id)
                .FirstOrDefault(entry => entry.Id.StartsWith(id + ":",
                    StringComparison.OrdinalIgnoreCase));
        if (resolved is null)
            return false;

        var planned = new PlannedArtifact
        {
            ItemId = resolved.Id,
            DisplayName = resolved.Name,
            Refine = Math.Max(0, artifact.Refine),
            Substats = CopySubstats(artifact.Substats)
        };
        if (artifact.Gems is not null)
        {
            foreach (GemData gem in artifact.Gems)
            {
                if (gem is null || string.IsNullOrWhiteSpace(gem.Id))
                    continue;
                planned.GemId = gem.Id;
                planned.GemRefine = Math.Max(0, gem.Refine);
                break;
            }
        }
        hovered = planned;
        statLines = NativeArtifactStatLines(artifact, uiItem);
        return true;
    }

    private static IReadOnlyList<string> NativeEquipmentStatLines(EquipData equip,
        UIInventoryItem uiItem)
    {
        string description = string.Empty;
        try
        {
            EquipConfig config = App.ServerRuntime?.GetEquip(equip.Id);
            if (config is not null)
                description = Extensions.ToDescription(equip, config, false, false);
        }
        catch (Exception ex)
        {
            Plugin.Warning($"Native equipment stat formatting failed: {ex.Message}");
        }
        return NativeDescriptionLines(description, uiItem);
    }

    private static IReadOnlyList<string> NativeArtifactStatLines(ArtifactData artifact,
        UIInventoryItem uiItem)
    {
        string description = string.Empty;
        try
        {
            string setId = artifact.Id ?? string.Empty;
            int separator = setId.IndexOf(':');
            if (separator >= 0)
                setId = setId[..separator];
            ArtifactSetConfig config = App.ServerRuntime?.GetArtifactSet(setId);
            if (config is not null)
                description = Extensions.ToDescription(artifact, config, false, false);
        }
        catch (Exception ex)
        {
            Plugin.Warning($"Native artifact stat formatting failed: {ex.Message}");
        }
        return NativeDescriptionLines(description, uiItem);
    }

    private static IReadOnlyList<string> NativeDescriptionLines(string description,
        UIInventoryItem uiItem)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            try { description = uiItem?.Description?.text ?? string.Empty; }
            catch { description = string.Empty; }
        }
        if (string.IsNullOrWhiteSpace(description))
            return Array.Empty<string>();

        string plain = RichTextBreak.Replace(description, "\n");
        plain = RichTextTag.Replace(plain, string.Empty);
        plain = WebUtility.HtmlDecode(plain);
        return plain.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimStart('•').Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<ImportedSubstat> CopySubstats(
        Il2CppSystem.Collections.Generic.List<StatData> source)
    {
        var result = new List<ImportedSubstat>();
        if (source is null)
            return result;
        foreach (StatData stat in source)
        {
            if (stat is null || stat.Type == StatType.None)
                continue;
            result.Add(new ImportedSubstat
            {
                Stat = stat.Type.ToString(),
                Base = stat.Value
            });
        }
        return result;
    }

    private static string PreferredComparisonSlot(string catalogSlot) => catalogSlot switch
    {
        "weapon" => FirstPopulatedEquipmentSlot("mainhand", "offhand"),
        "accessory" => FirstPopulatedEquipmentSlot("acc1", "acc2"),
        "offhand" => "offhand",
        "head" => "head",
        "legs" => "legs",
        "feet" => "feet",
        "chest" => "chest",
        "eyewear" => "eyewear",
        "back" => "back",
        _ => string.Empty
    };

    private static string FirstPopulatedEquipmentSlot(string first, string second)
    {
        if (_current.Equipment.TryGetValue(first, out PlannedItem firstItem)
            && !string.IsNullOrWhiteSpace(firstItem?.ItemId))
            return first;
        if (_current.Equipment.TryGetValue(second, out PlannedItem secondItem)
            && !string.IsNullOrWhiteSpace(secondItem?.ItemId))
            return second;
        return first;
    }

    private static string BuildSlot(EquipSlot slot) => slot switch
    {
        EquipSlot.Mainhand => "mainhand",
        EquipSlot.Offhand => "offhand",
        EquipSlot.Head => "head",
        EquipSlot.Legs => "legs",
        EquipSlot.Feet => "feet",
        EquipSlot.Chest => "chest",
        EquipSlot.AccessoryLeft => "acc1",
        EquipSlot.AccessoryRight => "acc2",
        EquipSlot.Eyewear => "eyewear",
        EquipSlot.Back => "back",
        _ => string.Empty
    };

    private static bool IsTextInputFocused()
    {
        try
        {
            GameObject selected = EventSystem.current?.currentSelectedGameObject;
            TMP_InputField input = selected?.GetComponentInParent<TMP_InputField>();
            return input is not null && input.isFocused;
        }
        catch { return false; }
    }

    private static bool IsBaseMenuOpen()
    {
        try
        {
            UIManager ui = App.UI;
            if (ui is null)
                return true;
            if (!ui.IsInGame || !ui.IsShowingGameScreen || ui.IsLoading)
                return true;
        }
        catch { }
        return false;
    }

    private static void DestroyUi()
    {
        if (_canvasObject is not null)
            Object.Destroy(_canvasObject);
        _canvasObject = null;
        _windowRect = null;
        _ui = null;
        _navigationRoot = null;
        _contentRoot = null;
        _pickerResults = null;
        _localRows = null;
        _communityRows = null;
        _activeSearch = null;
        _statusText = null;
    }
}
