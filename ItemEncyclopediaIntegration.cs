using System.Collections;
using System.Reflection;

namespace NevarisBuildCrafter;

internal static class ItemEncyclopediaIntegration
{
    internal const string PluginGuid = "alex.nevaris.itemencyclopedia";

    internal static bool IsAvailable => FindAssembly() is not null;

    internal static bool TryOpen(string itemId, string displayName, out string message)
    {
        Assembly assembly = FindAssembly();
        if (assembly is null)
        {
            message = "Install Item Encyclopedia to open full item pages.";
            return false;
        }

        try
        {
            if (TryPublicBridge(assembly, itemId, displayName))
            {
                message = "Opened in Item Encyclopedia.";
                return true;
            }

            Type controller = assembly.GetType("NevarisItemEncyclopedia.EncyclopediaController", false);
            if (controller is null)
                throw new MissingMemberException("Encyclopedia controller was not found.");

            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            controller.GetMethod("Open", flags)?.Invoke(null, null);
            FieldInfo catalogField = controller.GetField("Catalog", flags);
            MethodInfo selectMethod = controller.GetMethod("SelectItem", flags);
            if (catalogField?.GetValue(null) is not IEnumerable catalog || selectMethod is null)
                throw new MissingMemberException("This Item Encyclopedia version does not expose item selection.");

            foreach (object item in catalog)
            {
                if (item is null)
                    continue;
                Type itemType = item.GetType();
                string id = itemType.GetField("Id", flags)?.GetValue(item) as string;
                string name = itemType.GetField("Name", flags)?.GetValue(item) as string;
                if (!Matches(id, name, itemId, displayName))
                    continue;
                selectMethod.Invoke(null, new[] { item });
                message = "Opened in Item Encyclopedia.";
                return true;
            }

            message = "Item Encyclopedia is installed, but this item is not in its current index.";
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Warning($"Item Encyclopedia handoff failed: {ex.GetBaseException().Message}");
            message = "Item Encyclopedia could not open this item. Check for a mod update.";
            return false;
        }
    }

    private static bool TryPublicBridge(Assembly assembly, string itemId, string displayName)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public;
        foreach (Type type in assembly.GetTypes())
        {
            foreach (string methodName in new[] { "OpenItem", "OpenToItem", "ShowItem" })
            {
                MethodInfo method = type.GetMethod(methodName, flags, null, new[] { typeof(string) }, null);
                if (method is null)
                    continue;
                object result = method.Invoke(null, new object[] { itemId });
                if (result is not bool succeeded || succeeded)
                    return true;
                if (!string.IsNullOrWhiteSpace(displayName) && !displayName.Equals(itemId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    result = method.Invoke(null, new object[] { displayName });
                    return result is not bool second || second;
                }
            }
        }
        return false;
    }

    private static Assembly FindAssembly() => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
        string.Equals(assembly.GetName().Name, "NevarisItemEncyclopedia", StringComparison.OrdinalIgnoreCase));

    private static bool Matches(string indexedId, string indexedName, string itemId, string displayName) =>
        (!string.IsNullOrWhiteSpace(itemId) && (itemId.Equals(indexedId, StringComparison.OrdinalIgnoreCase)
            || itemId.Equals(indexedName, StringComparison.OrdinalIgnoreCase)))
        || (!string.IsNullOrWhiteSpace(displayName) && (displayName.Equals(indexedId,
            StringComparison.OrdinalIgnoreCase) || displayName.Equals(indexedName,
            StringComparison.OrdinalIgnoreCase)));
}
