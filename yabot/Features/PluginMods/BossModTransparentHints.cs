using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;
using ECommons.Reflection;
using System;
using System.Linq;
using System.Reflection;
using YABOT.FeaturesSetup;

namespace YABOT.Features.PluginMods
{
    public class BossModTransparentHints : PluginModFeature
    {
        public override string Name => "BossMod: Transparent Text Hints Window Background";

        public override string Description =>
            "Removes the black background behind BossMod's separate 'text hints' window, like TrishaMode does for the radar. " +
            "Also hides the 'Cur: Inactive' line when no encounter is active. " +
            "Requires BossMod's 'Show text hints in separate window' option to be enabled. " +
            "Re-applies automatically if BossMod is reloaded.";

        public override string RequiredPluginName => PluginName;

        private const string PluginName = "BossMod";
        private const string ServiceTypeName = "BossMod.Service";
        private const string WindowSystemFieldName = "WindowSystem";
        private const string HintsWindowTypeName = "BossModuleHintsWindow";

        private WindowSystem? bossModWindowSystem;
        private Window? originalHintsWindow;
        private WrapperWindow? wrapperWindow;
        private bool isEnabled;

        public override void Setup()
        {
            // Register once at load time. Registering inside Enable() would mutate
            // ECommons' onPluginsChangedActions list while it's being enumerated by
            // SyncPluginModFeatures (Plugin.cs), throwing "Collection was modified".
            DalamudReflector.RegisterOnInstalledPluginsChangedEvents(OnPluginsChanged);
            base.Setup();
        }

        public override void Enable()
        {
            base.Enable();
            isEnabled = true;
            TrySetup();
        }

        public override void Disable()
        {
            isEnabled = false;
            TryTeardown();
            base.Disable();
        }

        private void OnPluginsChanged()
        {
            if (!isEnabled) return;

            if (!IsWrapperStillActive())
            {
                TryTeardown();
                TrySetup();
            }
        }

        private bool IsWrapperStillActive()
        {
            if (bossModWindowSystem == null || wrapperWindow == null) return false;

            try
            {
                var currentWindowSystem = GetCurrentWindowSystem();
                return currentWindowSystem != null
                    && ReferenceEquals(currentWindowSystem, bossModWindowSystem)
                    && bossModWindowSystem.Windows.Contains(wrapperWindow);
            }
            catch
            {
                return false;
            }
        }

        private static WindowSystem? GetCurrentWindowSystem()
        {
            if (!DalamudReflector.TryGetDalamudPlugin(PluginName, out var plugin, suppressErrors: true, ignoreCache: false))
                return null;

            var serviceType = plugin.GetType().Assembly.GetType(ServiceTypeName);
            var field = serviceType?.GetField(WindowSystemFieldName, BindingFlags.Public | BindingFlags.Static);
            return field?.GetValue(null) as WindowSystem;
        }

        private void TrySetup()
        {
            try
            {
                var currentWindowSystem = GetCurrentWindowSystem();
                if (currentWindowSystem == null)
                {
                    Svc.Log.Warning($"[BossModTransparentHints] {PluginName} not loaded or WindowSystem unavailable.");
                    return;
                }

                var original = currentWindowSystem.Windows.FirstOrDefault(w => w.GetType().Name == HintsWindowTypeName) as Window
                    ?? throw new InvalidOperationException($"Window of type '{HintsWindowTypeName}' not found in BossMod's WindowSystem.");

                bossModWindowSystem = currentWindowSystem;
                originalHintsWindow = original;
                wrapperWindow = new WrapperWindow(original);

                bossModWindowSystem.RemoveWindow(originalHintsWindow);
                bossModWindowSystem.AddWindow(wrapperWindow);
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[BossModTransparentHints] setup failed: {ex.Message}");
                TryTeardown();
            }
        }

        private void TryTeardown()
        {
            try
            {
                if (bossModWindowSystem != null && wrapperWindow != null)
                {
                    if (bossModWindowSystem.Windows.Contains(wrapperWindow))
                        bossModWindowSystem.RemoveWindow(wrapperWindow);
                    if (originalHintsWindow != null && !bossModWindowSystem.Windows.Contains(originalHintsWindow))
                        bossModWindowSystem.AddWindow(originalHintsWindow);
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[BossModTransparentHints] teardown failed: {ex.Message}");
            }
            finally
            {
                bossModWindowSystem = null;
                originalHintsWindow = null;
                wrapperWindow = null;
            }
        }

        private sealed class WrapperWindow : Window
        {
            private readonly Window _original;
            private readonly InactiveStateProbe _probe;

            public WrapperWindow(Window original) : base(original.WindowName)
            {
                _original = original;
                _probe = new InactiveStateProbe(original);
                RespectCloseHotkey = original.RespectCloseHotkey;
                AllowClickthrough = original.AllowClickthrough;
                AllowPinning = original.AllowPinning;
                Size = original.Size;
                SizeCondition = original.SizeCondition;
                SizeConstraints = original.SizeConstraints;
                BgAlpha = original.BgAlpha;
                ShowCloseButton = original.ShowCloseButton;
            }

            public override void PreOpenCheck()
            {
                _original.PreOpenCheck();
                IsOpen = _original.IsOpen;
                Flags = _original.Flags | ImGuiWindowFlags.NoBackground;
                ForceMainWindow = true;
            }

            public override void Draw()
            {
                var token = _probe.TrySuppressMechanicTimers();
                try
                {
                    _original.Draw();
                }
                finally
                {
                    _probe.Restore(token);
                }
            }
        }

        // Walks BossModuleHintsWindow._mgr -> .Config / .ActiveModule.StateMachine.ActiveState to decide
        // whether the StateMachine.Draw call inside BossModule.Draw will emit "Cur: Inactive" this frame.
        // If it will, temporarily flips Config.ShowMechanicTimers to false so BossModule.Draw skips that call.
        // All reflection lookups are cached after the first successful resolution.
        private sealed class InactiveStateProbe
        {
            private readonly object _hintsWindow;
            private bool _failed;

            private FieldInfo? _mgrField;
            private FieldInfo? _configField;
            private PropertyInfo? _activeModuleProp;
            private FieldInfo? _stateMachineField;
            private PropertyInfo? _activeStateProp;
            private FieldInfo? _showMechField;

            public InactiveStateProbe(object hintsWindow) => _hintsWindow = hintsWindow;

            // Returns the config object whose ShowMechanicTimers was just flipped to false, or null if nothing was changed.
            public object? TrySuppressMechanicTimers()
            {
                if (_failed) return null;
                try
                {
                    var mgr = GetField(ref _mgrField, _hintsWindow.GetType(), "_mgr", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_hintsWindow);
                    if (mgr == null) return null;

                    var config = GetField(ref _configField, mgr.GetType(), "Config", BindingFlags.Public | BindingFlags.Instance).GetValue(mgr);
                    if (config == null) return null;

                    var showMechField = GetField(ref _showMechField, config.GetType(), "ShowMechanicTimers", BindingFlags.Public | BindingFlags.Instance);
                    if ((bool)(showMechField.GetValue(config) ?? false) == false) return null;

                    var activeModule = GetProperty(ref _activeModuleProp, mgr.GetType(), "ActiveModule").GetValue(mgr);
                    if (activeModule == null) return null;

                    var sm = GetField(ref _stateMachineField, activeModule.GetType(), "StateMachine", BindingFlags.Public | BindingFlags.Instance).GetValue(activeModule);
                    if (sm == null) return null;

                    var activeState = GetProperty(ref _activeStateProp, sm.GetType(), "ActiveState").GetValue(sm);
                    if (activeState != null) return null;

                    showMechField.SetValue(config, false);
                    return config;
                }
                catch (Exception ex)
                {
                    Svc.Log.Warning($"[BossModTransparentHints] inactive-state probe failed (disabling hide logic): {ex.Message}");
                    _failed = true;
                    return null;
                }
            }

            public void Restore(object? token)
            {
                if (token == null || _showMechField == null) return;
                try
                {
                    _showMechField.SetValue(token, true);
                }
                catch (Exception ex)
                {
                    Svc.Log.Warning($"[BossModTransparentHints] failed to restore ShowMechanicTimers: {ex.Message}");
                    _failed = true;
                }
            }

            private static FieldInfo GetField(ref FieldInfo? cache, Type type, string name, BindingFlags flags)
            {
                if (cache != null) return cache;
                cache = type.GetField(name, flags) ?? throw new InvalidOperationException($"Field '{name}' not found on '{type.FullName}'.");
                return cache;
            }

            private static PropertyInfo GetProperty(ref PropertyInfo? cache, Type type, string name)
            {
                if (cache != null) return cache;
                cache = type.GetProperty(name) ?? throw new InvalidOperationException($"Property '{name}' not found on '{type.FullName}'.");
                return cache;
            }
        }
    }
}
