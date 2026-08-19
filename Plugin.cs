using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Configuration;
using HarmonyLib;
using SpiritVale.ModConfig;
using UnityEngine;

namespace NevarisBuildCrafter;

[BepInPlugin(Guid, Name, Version)]
[BepInDependency(ModConfigPlugin.PluginGuid, BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(ItemEncyclopediaIntegration.PluginGuid, BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "alex.nevaris.buildcrafter";
    public const string Name = "Build Crafter";
    public const string Version = "1.1.1";

    internal static ManualLogSource Logger = null!;
    internal static BuildCrafterSettings Settings = null!;
    private Harmony _harmony;

    public override void Load()
    {
        Logger = Log;
        Settings = new BuildCrafterSettings(Config);
        BuildStorage.Initialize();
        McmIntegration.Register();

        _harmony = new Harmony(Guid);
        _harmony.PatchAll(Assembly.GetExecutingAssembly());
        BuildCrafterController.Initialize();
        Info($"{Name} {Version} loaded. Press {Settings.ToggleHotkey.Value} to plan a build.");
    }

    public override bool Unload()
    {
        _harmony?.UnpatchSelf();
        ModConfigMenu.Unregister(Guid);
        PlayerWorldGate.Shutdown();
        BuildCrafterController.Shutdown();
        SpiritValersClient.Shutdown();
        return base.Unload();
    }

    internal static void Info(string message) => Logger.LogInfo($"[BuildCrafter] {message}");
    internal static void Warning(string message) => Logger.LogWarning($"[BuildCrafter] {message}");
    internal static void Error(string message) => Logger.LogError($"[BuildCrafter] {message}");
}

internal sealed class BuildCrafterSettings
{
    internal readonly ConfigEntry<KeyboardShortcut> ToggleHotkey;
    internal readonly ConfigEntry<float> UiScale;
    internal readonly ConfigEntry<bool> EnableCommunityBuilds;
    internal readonly ConfigEntry<int> CommunityLimit;
    internal readonly ConfigEntry<float> RequestTimeout;

    internal BuildCrafterSettings(ConfigFile config)
    {
        ToggleHotkey = config.Bind("Hotkeys", "Toggle", new KeyboardShortcut(KeyCode.F8),
            "Open or close Build Crafter.");
        UiScale = config.Bind("Display", "Scale", 1f,
            "Scale the Build Crafter window.");
        EnableCommunityBuilds = config.Bind("Community", "EnableSpiritValers", true,
            "Allow read-only downloads of public builds from spiritvalers.com.");
        CommunityLimit = config.Bind("Community", "BuildLimit", 200,
            "Maximum number of recent public builds to show, from 25 to 500.");
        RequestTimeout = config.Bind("Community", "RequestTimeoutSeconds", 15f,
            "Seconds to wait for spiritvalers.com before reporting a timeout.");
    }
}
