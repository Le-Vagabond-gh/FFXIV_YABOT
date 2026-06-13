using ECommons.DalamudServices;
using ECommons.Reflection;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace YABOT.Features.PluginMods
{
    // Shared reflection plumbing for poking WrathCombo's config from YABOT features.
    // Keeps the brittle type/member names in one place so the tank tweaks don't each carry a copy.
    internal static class WrathComboInterop
    {
        public const string PluginName = "WrathCombo";

        private const string ConfigType = "WrathCombo.Core.Configuration";
        private const string ServiceType = "WrathCombo.Services.Service";
        private const string PresetStorageType = "WrathCombo.Core.PresetStorage";
        private const string CustomIntMember = "CustomIntValues";
        private const string ConfigMember = "Configuration";
        private const string EnabledActionsMember = "EnabledActions";

        public static bool TryGetWrath(out object plugin) =>
            DalamudReflector.TryGetDalamudPlugin(PluginName, out plugin, suppressErrors: true, ignoreCache: false);

        public static Dictionary<string, int>? GetCustomIntValues()
        {
            if (!TryGetWrath(out var pl)) return null;
            try
            {
                return pl.GetStaticFoP<Dictionary<string, int>>(ConfigType, CustomIntMember);
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[WrathComboInterop] CustomIntValues read failed: {ex.Message}");
                return null;
            }
        }

        // Enable or disable a preset by its int id via WrathCombo's PresetStorage helpers,
        // which handle parent combos, conflicting combos, and saving for us.
        public static void SetPreset(int preset, bool enabled)
        {
            if (!TryGetWrath(out var pl)) return;
            try
            {
                var type = pl.GetType().Assembly.GetType(PresetStorageType);
                var name = enabled ? "EnablePreset" : "DisablePreset";
                var method = type?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == name
                        && m.GetParameters() is { Length: 2 } p
                        && p[0].ParameterType == typeof(int));
                method?.Invoke(null, new object?[] { preset, null });
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[WrathComboInterop] {(enabled ? "enable" : "disable")} preset {preset} failed: {ex.Message}");
            }
        }

        // Whether a preset id is in WrathCombo's EnabledActions set.
        public static bool IsPresetEnabled(int preset)
        {
            if (!TryGetWrath(out var pl)) return false;
            try
            {
                var set = pl.GetStaticFoP<object>(ServiceType, ConfigMember)?.GetFoP<IEnumerable>(EnabledActionsMember);
                if (set == null) return false;
                foreach (var p in set)
                    if (Convert.ToInt32(p) == preset) return true;
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[WrathComboInterop] EnabledActions read failed: {ex.Message}");
            }
            return false;
        }

        public static void SaveConfig()
        {
            try
            {
                if (TryGetWrath(out var pl))
                    pl.GetStaticFoP<object>(ServiceType, ConfigMember)?.Call("Save", null, Array.Empty<object>());
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[WrathComboInterop] Save failed: {ex.Message}");
            }
        }
    }
}
