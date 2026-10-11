using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.Reflection;
using System;
using System.Reflection;
using YABOT.FeaturesSetup;

namespace YABOT.Features.PluginMods
{
    public class AutoCloseLifestreamWorldSelect : PluginModFeature
    {
        public override string Name => "Lifestream: Auto-Close World Select Window";

        public override string Description =>
            "Automatically closes Lifestream's 'Select World' window (opened by /li w) once you click a destination.";

        public override string RequiredPluginName => PluginName;

        private const string PluginName = "Lifestream";
        private const string GuiTypeName = "Lifestream.Services.Service+Gui";

        private const int MaxConsecutiveFailures = 5;

        private bool prevBusy;
        private FieldInfo? selectWorldWindowField;
        private FieldInfo? taskManagerField;
        private PropertyInfo? isBusyProp;
        private bool reflectionFailed;
        private int consecutiveFailures;
        private object? cachedPlugin;

        public override void Enable()
        {
            Svc.Framework.Update += OnUpdate;
            base.Enable();
        }

        public override void Disable()
        {
            Svc.Framework.Update -= OnUpdate;
            prevBusy = false;
            base.Disable();
        }

        private void OnUpdate(IFramework framework)
        {
            if (reflectionFailed) return;

            try
            {
                if (!DalamudReflector.TryGetDalamudPlugin(PluginName, out var plugin, suppressErrors: true, ignoreCache: false))
                {
                    prevBusy = false;
                    return;
                }

                // Lifestream reloads (e.g. on update) into a fresh assembly with new Type objects.
                // Drop cached reflection so it re-resolves against the live instance.
                if (!ReferenceEquals(plugin, cachedPlugin))
                {
                    cachedPlugin = plugin;
                    selectWorldWindowField = null;
                    taskManagerField = null;
                    isBusyProp = null;
                    prevBusy = false;
                }

                if (!EnsureReflection(plugin)) return;

                // Lifestream's SelectWorldWindow shadows Window.IsOpen with an obsolete dummy property,
                // so go through Dalamud's Window type (shared across load contexts) instead of reflection.
                if (selectWorldWindowField!.GetValue(null) is not Window window || !window.IsOpen)
                {
                    prevBusy = false;
                    return;
                }

                var taskManager = taskManagerField!.GetValue(plugin);
                if (taskManager == null) return;

                var isBusy = (bool)(isBusyProp!.GetValue(taskManager) ?? false);

                if (isBusy && !prevBusy)
                {
                    window.IsOpen = false;
                }

                prevBusy = isBusy;
                consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                if (consecutiveFailures >= MaxConsecutiveFailures)
                {
                    reflectionFailed = true;
                    Svc.Log.Warning($"[AutoCloseLifestreamWorldSelect] disabling after {consecutiveFailures} consecutive failures. Last error: {ex.Message}");
                }
                else
                {
                    Svc.Log.Warning($"[AutoCloseLifestreamWorldSelect] update failed ({consecutiveFailures}/{MaxConsecutiveFailures}): {ex.Message}");
                }
            }
        }

        private bool EnsureReflection(object plugin)
        {
            if (selectWorldWindowField != null && taskManagerField != null && isBusyProp != null)
                return true;

            try
            {
                var asm = plugin.GetType().Assembly;

                if (selectWorldWindowField == null)
                {
                    var guiType = asm.GetType(GuiTypeName)
                        ?? throw new InvalidOperationException($"Type '{GuiTypeName}' not found in Lifestream assembly.");
                    selectWorldWindowField = guiType.GetField("SelectWorldWindow", BindingFlags.Public | BindingFlags.Static)
                        ?? throw new InvalidOperationException("Static field 'SelectWorldWindow' not found on Lifestream.Services.Service.Gui.");
                }

                taskManagerField ??= plugin.GetType().GetField("TaskManager", BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new InvalidOperationException("Field 'TaskManager' not found on Lifestream plugin.");

                var taskManager = taskManagerField.GetValue(plugin);
                if (taskManager == null) return false;

                isBusyProp ??= taskManager.GetType().GetProperty("IsBusy")
                    ?? throw new InvalidOperationException("Property 'IsBusy' not found on Lifestream TaskManager.");

                return true;
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[AutoCloseLifestreamWorldSelect] reflection setup failed, disabling: {ex.Message}");
                reflectionFailed = true;
                return false;
            }
        }
    }
}
