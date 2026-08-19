using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP.Configuration;
using SpiritVale.ModConfig;

namespace NevarisBuildCrafter;

internal static class McmIntegration
{
    internal static void Register()
    {
        ModPage page = ModConfigMenu.Register(Plugin.Guid, Plugin.Name,
            "Plan character builds, keep local copies, and browse the SpiritValers community gallery.",
            Plugin.Version);

        page.AddHeader("BUILDER", "Build choices are rebuilt from the installed game data on world entry.");
        AddHotkeyControls(page);

        page.AddHeader("DISPLAY");
        page.BindFloatSlider(Plugin.Settings.UiScale, "Window scale", 0.75f, 1.15f, 0.05f,
            "0.00x", "Scale the builder while keeping it inside the screen.",
            _ => BuildCrafterController.ApplyConfiguredScale());

        page.AddHeader("COMMUNITY", "Public builds are downloaded read-only from spiritvalers.com.");
        page.BindToggle(Plugin.Settings.EnableCommunityBuilds, "Enable SpiritValers community builds",
            "Allow Build Crafter to download public build listings and imports.");
        page.BindIntegerSlider(Plugin.Settings.CommunityLimit, "Community list size", 25, 500, 25,
            "Maximum number of recent public builds to load.");
        page.BindFloatSlider(Plugin.Settings.RequestTimeout, "Request timeout", 5f, 60f, 5f,
            "0 sec", "How long to wait for community requests.");

        page.AddHeader("ACTIONS");
        page.AddButton("Open", "Open Build Crafter", BuildCrafterController.Open,
            "Open the build workspace now.");
        page.AddButton("Refresh", "Refresh game data", BuildCrafterController.RefreshCatalog,
            "Reload equipment, artifacts, cards, gems, and skills from the client.");
    }

    private static void AddHotkeyControls(ModPage page)
    {
        MethodInfo bindHotkey = typeof(ModPage).GetMethod("BindHotkey",
            BindingFlags.Instance | BindingFlags.Public, null,
            new[] { typeof(ConfigEntry<KeyboardShortcut>), typeof(string), typeof(string),
                typeof(Action<KeyboardShortcut>) }, null);
        if (bindHotkey is null)
        {
            page.AddHeader("HOTKEYS", $"Press {Plugin.Settings.ToggleHotkey.Value} to open or close. " +
                $"Hover equipment and press {Plugin.Settings.CompareHotkey.Value} to compare it.");
            return;
        }
        page.AddHeader("HOTKEYS", "Hold modifiers first, press the main key last, then release.");
        bindHotkey.Invoke(page, new object[]
        {
            Plugin.Settings.ToggleHotkey,
            "Open Build Crafter",
            "Open or close the build workspace.",
            null
        });
        bindHotkey.Invoke(page, new object[]
        {
            Plugin.Settings.CompareHotkey,
            "Compare hovered equipment",
            "Compare the item under the cursor with its matching slot in the loaded build.",
            null
        });
    }
}
