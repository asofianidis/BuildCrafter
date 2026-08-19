using HarmonyLib;
using UnityEngine;

namespace NevarisBuildCrafter;

[HarmonyPatch(typeof(PlayerController), "Update")]
internal static class RuntimeTickPatch
{
    private static bool _loggedFailure;

    private static void Postfix(PlayerController __instance)
    {
        try
        {
            if (!__instance.IsSpawned || !__instance.IsOwner)
                return;
            PlayerWorldGate.Tick(__instance);
        }
        catch (Exception ex)
        {
            if (_loggedFailure)
                return;
            _loggedFailure = true;
            Plugin.Error($"The Build Crafter world gate stopped after an update error: {ex}");
        }
    }
}

[HarmonyPatch(typeof(UIManager), nameof(UIManager.Escape))]
internal static class EscapePatch
{
    private static bool Prefix()
    {
        if (!BuildCrafterController.IsOpen)
            return true;
        BuildCrafterController.HandleEscape();
        return false;
    }
}

[HarmonyPatch(typeof(MoveComponent), nameof(MoveComponent.Move), new[] { typeof(Vector3) })]
internal static class DirectionalMovementSuppressionPatch
{
    private static bool Prefix(MoveComponent __instance) =>
        !BuildCrafterController.ShouldBlockMovement(__instance);
}

[HarmonyPatch(typeof(MoveComponent), nameof(MoveComponent.MoveTo),
    new[] { typeof(Vector3), typeof(Vector3), typeof(bool), typeof(bool) })]
internal static class ClickMovementSuppressionPatch
{
    private static bool Prefix(MoveComponent __instance) =>
        !BuildCrafterController.ShouldBlockMovement(__instance);
}

[HarmonyPatch(typeof(MoveComponent), nameof(MoveComponent.MoveToRange),
    new[] { typeof(Vector3), typeof(float), typeof(bool) })]
internal static class RangeMovementSuppressionPatch
{
    private static bool Prefix(MoveComponent __instance, ref bool __result)
    {
        if (!BuildCrafterController.ShouldBlockMovement(__instance))
            return true;
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(MoveComponent), nameof(MoveComponent.MoveToRange),
    new[] { typeof(BaseUnitController), typeof(float), typeof(bool) })]
internal static class TargetMovementSuppressionPatch
{
    private static bool Prefix(MoveComponent __instance, ref bool __result)
    {
        if (!BuildCrafterController.ShouldBlockMovement(__instance))
            return true;
        __result = false;
        return false;
    }
}

internal static class PlayerWorldGate
{
    private const float ReadyGraceSeconds = 0.5f;
    private static PlayerController _localPlayer;
    private static float _readySince = -1f;
    private static bool _enteredWorld;

    internal static void Tick(PlayerController player)
    {
        bool ready;
        try
        {
            ready = player.IsSpawned && player.IsCharacterLoaded && player.gameObject.activeInHierarchy;
        }
        catch { ready = false; }

        if (!ready)
        {
            if (SamePlayer(_localPlayer, player))
            {
                _readySince = -1f;
                LeaveWorld();
            }
            return;
        }
        if (!SamePlayer(_localPlayer, player))
        {
            LeaveWorld();
            _localPlayer = player;
            _readySince = -1f;
        }
        if (_readySince < 0f)
        {
            _readySince = Time.unscaledTime;
            return;
        }
        if (!_enteredWorld && Time.unscaledTime - _readySince >= ReadyGraceSeconds)
        {
            _enteredWorld = true;
            BuildCrafterController.EnterWorld(player);
        }
        if (_enteredWorld)
            BuildCrafterController.Tick();
    }

    internal static void Shutdown()
    {
        LeaveWorld();
        _localPlayer = null;
        _readySince = -1f;
    }

    private static void LeaveWorld()
    {
        if (!_enteredWorld)
            return;
        _enteredWorld = false;
        BuildCrafterController.LeaveWorld();
    }

    private static bool SamePlayer(PlayerController left, PlayerController right)
    {
        if (left is null || right is null)
            return false;
        if (left == right)
            return true;
        try { return left.ObjectId == right.ObjectId; }
        catch { return false; }
    }
}
