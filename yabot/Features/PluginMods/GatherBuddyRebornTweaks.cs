using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using ECommons.Reflection;
using YABOT.FeaturesSetup;

namespace YABOT.Features.PluginMods
{
    // GatherBuddy Reborn's Vulcan crafting browser lets you right-click a recipe and "Create New List",
    // but the "List name..." field starts empty. This prefills it with the recipe's name.
    //
    // GBR doesn't store which recipe was right-clicked anywhere we can read, and ImGui is immediate-mode
    // so the on-screen text isn't recoverable. The one reliable signal is ImGui's open-popup stack: GBR
    // opens the menu as BeginPopupContextItem($"RecipeContextMenu##{rowId}"). ImGui hashes that string
    // (seeded by the call-site id stack) into PopupId, and conveniently stores that exact seed as
    // OpenParentId. So we reproduce ImGui's ImHashStr over each visible recipe and match PopupId to find
    // the right-clicked rowId, then write its name into GBR's static field via reflection.
    public class GatherBuddyRebornTweaks : PluginModFeature
    {
        public override string Name => "GatherBuddy Reborn tweaks";

        public override string Description =>
            "Tweaks for GatherBuddy Reborn's crafting lists. When you 'Create New List' from a recipe's right-click "
            + "menu it can prefill the list name with the recipe's name and tick 'Ephemeral' for you, and newly "
            + "created lists can have 'Skip if Already Have Enough' ticked automatically.";

        public override string RequiredPluginName => PluginName;

        public override bool UseAutoConfig => true;

        public class Configs : FeatureConfig
        {
            [FeatureConfigOption("Create New List: prefill the name with the recipe name")]
            public bool PrefillListName = true;

            [FeatureConfigOption("Create New List: tick \"Ephemeral\" too (you can untick it afterwards)")]
            public bool CheckEphemeral = false;

            [FeatureConfigOption("New lists: tick \"Skip if Already Have Enough\" (you can untick it afterwards)")]
            public bool AutoSkipIfEnough = false;
        }

        public Configs Config { get; private set; } = null!;

        private const string PluginName       = "GatherBuddyReborn";
        private const string VulcanTypeName   = "GatherBuddy.Gui.VulcanWindow";
        private const int    MaxConsecutiveFailures = 5;

        // Reflection cache, rebuilt whenever GBR reloads into a fresh assembly.
        private object?       cachedPlugin;
        private FieldInfo?    newListNameField;     // static string  VulcanWindow._contextMenuNewListName
        private FieldInfo?    ephemeralField;       // static bool    VulcanWindow._contextMenuNewListEphemeral
        private FieldInfo?    filteredRecipesField; // static IList    VulcanWindow._filteredRecipes
        private FieldInfo?    erNameField;          // ExtendedRecipe.Name
        private FieldInfo?    erRecipeField;        // ExtendedRecipe.Recipe
        private PropertyInfo? recipeRowIdProp;      // Recipe.RowId
        private PropertyInfo? listManagerProp;      // static GatherBuddy.CraftingListManager
        private PropertyInfo? listsProp;            // CraftingListManager.Lists
        private MethodInfo?   saveListMethod;       // CraftingListManager.SaveList(CraftingListDefinition)
        private PropertyInfo? listIdProp;           // CraftingListDefinition.ID
        private PropertyInfo? skipIfEnoughProp;     // CraftingListDefinition.SkipIfEnough
        private bool          reflectionFailed;
        private int           consecutiveFailures;

        // One-shot-per-opening state.
        private uint prevPopupId;
        private uint filledPopupId;

        // New-list tracking. GBR list IDs are random and reused after deletion, so we compare each frame's
        // set against the previous frame's: a "new" list is an ID that wasn't there last frame. That
        // re-detects a recreated list even if it reuses a freed ID (the ID is gone for at least one frame).
        private readonly HashSet<int> prevListIds = new();
        private bool listsSeeded;

        private static readonly uint[] Crc32 = BuildCrc32();

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            Svc.PluginInterface.UiBuilder.Draw += OnDraw;
            base.Enable();
        }

        public override void Disable()
        {
            Svc.PluginInterface.UiBuilder.Draw -= OnDraw;
            prevPopupId = filledPopupId = 0;
            SaveConfig(Config);
            base.Disable();
        }

        private void OnDraw()
        {
            if (reflectionFailed) return;

            try
            {
                if (!DalamudReflector.TryGetDalamudPlugin(PluginName, out var plugin, suppressErrors: true, ignoreCache: false))
                {
                    prevPopupId = filledPopupId = 0;
                    return;
                }

                // GBR reloads (updates) into a new assembly with new Type objects; drop stale reflection.
                if (!ReferenceEquals(plugin, cachedPlugin))
                {
                    cachedPlugin         = plugin;
                    newListNameField     = null;
                    ephemeralField       = null;
                    filteredRecipesField = null;
                    erNameField          = null;
                    erRecipeField        = null;
                    recipeRowIdProp      = null;
                    listManagerProp      = null;
                    listsProp            = null;
                    saveListMethod       = null;
                    listIdProp           = null;
                    skipIfEnoughProp     = null;
                    prevPopupId          = filledPopupId = 0;
                    prevListIds.Clear();
                    listsSeeded          = false;
                }

                if (!EnsureReflection(plugin)) return;

                AutoTickNewLists();

                if (!TryGetTopPopup(out var popupId, out var seed))
                {
                    prevPopupId = filledPopupId = 0; // nothing open -> allow a fresh fill next time
                    return;
                }

                var newlyOpened = popupId != prevPopupId;
                prevPopupId = popupId;

                // Defer one frame: on the popup's first frame GBR resets the field via IsWindowAppearing,
                // and our Draw may run before or after theirs. Waiting a frame makes us order-independent.
                if (newlyOpened || popupId == filledPopupId) return;

                filledPopupId = popupId; // handle this opening exactly once (matched or not)

                if (!Config.PrefillListName && !Config.CheckEphemeral) return;

                // Only act on an actual recipe context menu; ignore GBR's other popups (e.g. the sort menu).
                if (!TryMatchRecipeName(popupId, seed, out var recipeName)) return;

                // Never fight the user: only prefill an empty name field.
                if (Config.PrefillListName && !string.IsNullOrEmpty(recipeName))
                {
                    var current = newListNameField!.GetValue(null) as string ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(current))
                        newListNameField.SetValue(null, recipeName);
                }

                // Optionally tick "Ephemeral" once; the user can untick it afterwards (we won't re-tick).
                if (Config.CheckEphemeral)
                    ephemeralField!.SetValue(null, true);

                consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                if (++consecutiveFailures >= MaxConsecutiveFailures)
                {
                    reflectionFailed = true;
                    Svc.Log.Warning($"[{Name}] disabling after {consecutiveFailures} consecutive failures. Last error: {ex.Message}");
                }
            }
        }

        // Topmost open popup's id and the id-stack seed ImGui used to hash it.
        private static unsafe bool TryGetTopPopup(out uint popupId, out uint seed)
        {
            popupId = 0;
            seed    = 0;

            var ctx = ImGui.GetCurrentContext();
            if (ctx.IsNull) return false;

            var stack = ctx.OpenPopupStack;
            if (stack.Size <= 0) return false;

            var top = new ImGuiPopupDataPtr(&stack.Data[stack.Size - 1]);
            popupId = top.PopupId;
            seed    = top.OpenParentId;
            return popupId != 0;
        }

        private bool TryMatchRecipeName(uint popupId, uint seed, out string recipeName)
        {
            recipeName = string.Empty;

            if (filteredRecipesField!.GetValue(null) is not IEnumerable recipes) return false;

            foreach (var er in recipes)
            {
                if (er == null) continue;
                var boxedRecipe = erRecipeField!.GetValue(er);
                if (boxedRecipe == null) continue;
                if (recipeRowIdProp!.GetValue(boxedRecipe) is not uint rowId) continue;

                if (ImHashStr($"RecipeContextMenu##{rowId}", seed) == popupId)
                {
                    recipeName = erNameField!.GetValue(er) as string ?? string.Empty;
                    return true;
                }
            }

            return false;
        }

        // Ticks "Skip if Already Have Enough" on lists that appeared since the last frame. The first pass
        // just records the current lists (so pre-existing ones are never touched); after that, each newly
        // appeared list is handled once - the user is free to untick it afterwards.
        private void AutoTickNewLists()
        {
            var manager = listManagerProp!.GetValue(null);
            if (manager == null) return;
            if (listsProp!.GetValue(manager) is not IEnumerable lists) return;

            var current = new HashSet<int>();
            foreach (var list in lists)
            {
                if (listIdProp!.GetValue(list) is not int id) continue;
                current.Add(id);

                if (listsSeeded && !prevListIds.Contains(id) && Config.AutoSkipIfEnough)
                {
                    skipIfEnoughProp!.SetValue(list, true);
                    saveListMethod!.Invoke(manager, new[] { list });
                }
            }

            prevListIds.Clear();
            prevListIds.UnionWith(current);
            listsSeeded = true;
        }

        private bool EnsureReflection(object plugin)
        {
            if (newListNameField != null && ephemeralField != null && filteredRecipesField != null
                && erNameField != null && erRecipeField != null && recipeRowIdProp != null
                && listManagerProp != null && listsProp != null && saveListMethod != null
                && listIdProp != null && skipIfEnoughProp != null)
                return true;

            try
            {
                var asm        = plugin.GetType().Assembly;
                var vulcanType = asm.GetType(VulcanTypeName)
                    ?? throw new InvalidOperationException($"Type '{VulcanTypeName}' not found in GatherBuddy Reborn assembly.");

                const BindingFlags staticNonPublic = BindingFlags.NonPublic | BindingFlags.Static;

                newListNameField ??= vulcanType.GetField("_contextMenuNewListName", staticNonPublic)
                    ?? throw new InvalidOperationException("Static field '_contextMenuNewListName' not found on VulcanWindow.");

                ephemeralField ??= vulcanType.GetField("_contextMenuNewListEphemeral", staticNonPublic)
                    ?? throw new InvalidOperationException("Static field '_contextMenuNewListEphemeral' not found on VulcanWindow.");

                filteredRecipesField ??= vulcanType.GetField("_filteredRecipes", staticNonPublic)
                    ?? throw new InvalidOperationException("Static field '_filteredRecipes' not found on VulcanWindow.");

                var erType = vulcanType.GetNestedType("ExtendedRecipe", BindingFlags.Public)
                    ?? throw new InvalidOperationException("Nested type 'ExtendedRecipe' not found on VulcanWindow.");

                erNameField ??= erType.GetField("Name", BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new InvalidOperationException("Field 'Name' not found on ExtendedRecipe.");

                erRecipeField ??= erType.GetField("Recipe", BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new InvalidOperationException("Field 'Recipe' not found on ExtendedRecipe.");

                recipeRowIdProp ??= erRecipeField.FieldType.GetProperty("RowId")
                    ?? throw new InvalidOperationException("Property 'RowId' not found on Recipe.");

                // The plugin instance is GatherBuddy.GatherBuddy; CraftingListManager is a static property on it.
                listManagerProp ??= plugin.GetType().GetProperty("CraftingListManager", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException("Static property 'CraftingListManager' not found on GatherBuddy.");

                var managerType = listManagerProp.PropertyType;
                listsProp ??= managerType.GetProperty("Lists")
                    ?? throw new InvalidOperationException("Property 'Lists' not found on CraftingListManager.");
                saveListMethod ??= managerType.GetMethod("SaveList")
                    ?? throw new InvalidOperationException("Method 'SaveList' not found on CraftingListManager.");

                var defType = asm.GetType("GatherBuddy.Crafting.CraftingListDefinition")
                    ?? throw new InvalidOperationException("Type 'CraftingListDefinition' not found in GatherBuddy Reborn assembly.");
                listIdProp ??= defType.GetProperty("ID")
                    ?? throw new InvalidOperationException("Property 'ID' not found on CraftingListDefinition.");
                skipIfEnoughProp ??= defType.GetProperty("SkipIfEnough")
                    ?? throw new InvalidOperationException("Property 'SkipIfEnough' not found on CraftingListDefinition.");

                return true;
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[{Name}] reflection setup failed, disabling: {ex.Message}");
                reflectionFailed = true;
                return false;
            }
        }

        // Faithful reproduction of ImGui's ImHashStr (null-terminated form): CRC32 with the standard
        // reflected polynomial, '~seed' init, and the "###" restart rule (a no-op here since recipe
        // ids only ever contain a single "##").
        private static uint ImHashStr(string s, uint seed)
        {
            var data = Encoding.UTF8.GetBytes(s);
            seed = ~seed;
            var crc = seed;

            for (var i = 0; i < data.Length; i++)
            {
                var c = data[i];
                if (c == (byte)'#' && i + 2 < data.Length && data[i + 1] == (byte)'#' && data[i + 2] == (byte)'#')
                    crc = seed;
                crc = (crc >> 8) ^ Crc32[(crc & 0xFF) ^ c];
            }

            return ~crc;
        }

        private static uint[] BuildCrc32()
        {
            const uint poly = 0xEDB88320;
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? poly ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }
    }
}
