using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.Reflection;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using YABOT.FeaturesSetup;

namespace YABOT.Features.PluginMods
{
    public class OfDungeonsDeepTransparentTarget : PluginModFeature
    {
        public override string Name => "OfDungeonsDeep: Target Window Background";

        public override string Description =>
            "Removes or dims the background behind OfDungeonsDeep's target details window, " +
            "so the portrait and stats float over the game instead of sitting in an opaque box. " +
            "Re-applies automatically if OfDungeonsDeep is reloaded, and restores the original look when disabled.";

        public override string RequiredPluginName => PluginName;

        // It's a plugin mod, but it only matters inside deep dungeons, so surface it under both categories.
        public override IEnumerable<FeatureType> AdditionalCategories => new[] { FeatureType.DeepDungeons };

        public override bool UseAutoConfig => true;

        private const string PluginName = "OfDungeonsDeep";

        public enum BackgroundMode
        {
            [Description("Remove background (fully transparent, no border)")]
            Remove,

            [Description("Set transparency (dim the background to a chosen opacity)")]
            SetTransparency,
        }

        public class Configs : FeatureConfig
        {
            [FeatureConfigOption("Background", "RadioEnum")]
            public BackgroundMode Mode = BackgroundMode.Remove;

            [FeatureConfigOption("Background opacity", FloatMin = 0f, FloatMax = 1f, EditorSize = 200, Format = "%.2f", ConditionalDisplay = true)]
            public float Transparency = 0.3f;

            // Drives ConditionalDisplay: the opacity slider only matters in SetTransparency mode.
            public bool ShouldShowTransparency() => Mode == BackgroundMode.SetTransparency;
        }

        public Configs Config { get; private set; } = null!;

        // OfDungeonsDeep's target window only ever toggles NoMove/NoResize in its own PreDraw, so the
        // NoBackground flag / BgAlpha we set persist frame to frame. We cache the live Window instance
        // (resolved by reflection) and re-apply the current config each frame - cheap once cached, and
        // it picks up live changes to the mode/opacity slider. The instance dies when OfDungeonsDeep
        // reloads, so the cache is dropped on plugin changes and re-resolved (this also covers the
        // plugin's async startup, during which the window doesn't exist yet).
        private Window? targetWindow;
        private float? originalBgAlpha;
        private bool isEnabled;

        public override void Setup()
        {
            // Register once at load time. Registering inside Enable() would mutate ECommons'
            // onPluginsChangedActions list while SyncPluginModFeatures (Plugin.cs) enumerates it.
            DalamudReflector.RegisterOnInstalledPluginsChangedEvents(OnPluginsChanged);
            base.Setup();
        }

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();
            base.Enable();
            isEnabled = true;
            Svc.Framework.Update += OnUpdate;
        }

        public override void Disable()
        {
            isEnabled = false;
            Svc.Framework.Update -= OnUpdate;
            RestoreWindow();
            targetWindow = null;
            originalBgAlpha = null;
            SaveConfig(Config);
            base.Disable();
        }

        private void OnPluginsChanged()
        {
            if (!isEnabled) return;

            // The cached Window dies when OfDungeonsDeep reloads; drop it so OnUpdate re-resolves.
            targetWindow = null;
            originalBgAlpha = null;
        }

        private void OnUpdate(IFramework framework)
        {
            if (!isEnabled) return;

            try
            {
                if (targetWindow == null)
                {
                    targetWindow = ResolveTargetWindow();
                    if (targetWindow == null) return; // not loaded / still initializing - retry next frame
                    originalBgAlpha = targetWindow.BgAlpha; // capture before we touch it, to restore later
                }

                if (Config.Mode == BackgroundMode.Remove)
                {
                    targetWindow.Flags |= ImGuiWindowFlags.NoBackground;
                    targetWindow.BgAlpha = originalBgAlpha;
                }
                else
                {
                    targetWindow.Flags &= ~ImGuiWindowFlags.NoBackground;
                    targetWindow.BgAlpha = Config.Transparency;
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[{Name}] apply failed: {ex.Message}");
                targetWindow = null; // reflection may be stale; re-resolve next frame
            }
        }

        private void RestoreWindow()
        {
            try
            {
                if (targetWindow == null) return;
                targetWindow.Flags &= ~ImGuiWindowFlags.NoBackground;
                targetWindow.BgAlpha = originalBgAlpha;
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[{Name}] restore failed: {ex.Message}");
            }
        }

        // OfDungeonsDeep doesn't expose its WindowSystem statically (BossMod does), but the target
        // window is reachable through public fields: Plugin.Controller (static) -> WindowController ->
        // TargetDataWindow, which derives from Dalamud's Window. Controller is assigned asynchronously
        // after the bestiary data loads, so this returns null until that completes.
        private static Window? ResolveTargetWindow()
        {
            if (!DalamudReflector.TryGetDalamudPlugin(PluginName, out var plugin, suppressErrors: true, ignoreCache: false))
                return null;

            var controller = plugin.GetType()
                .GetField("Controller", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (controller == null) return null;

            var windowController = controller.GetType()
                .GetField("WindowController", BindingFlags.Public | BindingFlags.Instance)?.GetValue(controller);
            if (windowController == null) return null;

            var targetWindow = windowController.GetType()
                .GetField("TargetDataWindow", BindingFlags.Public | BindingFlags.Instance)?.GetValue(windowController);

            return targetWindow as Window;
        }
    }
}
