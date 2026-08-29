using System;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using YABOT.FeaturesSetup;
using YABOT.Helpers;

namespace YABOT.Features.UI
{
    public unsafe class HideParameterBarInCutscene : BaseFeature
    {
        public override string Name => "Hide Parameter Bar During Cutscenes";

        public override string Description =>
            "The game hides the HUD for cutscenes but leaves the parameter bar (HP/MP/GP/CP) on screen, " +
            "where it serves no purpose. This hides it for the duration of the cutscene and puts it back " +
            "afterwards. A parameter bar that was already hidden (HUD toggled off, own HUD layout) is left alone.";

        public override FeatureType FeatureType => FeatureType.UI;

        private const string AddonName = "_ParameterWidget";

        private bool hiddenByUs;

        public override void Enable()
        {
            Svc.Framework.Update += OnFrameworkUpdate;
            base.Enable();
        }

        public override void Disable()
        {
            Svc.Framework.Update -= OnFrameworkUpdate;
            Restore();
            base.Disable();
        }

        private void OnFrameworkUpdate(IFramework _)
        {
            try
            {
                if (ConditionHelper.IsInCutscene())
                    Hide();
                else
                    Restore();
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, Name);
            }
        }

        private static AtkUnitBase* GetAddon() => (AtkUnitBase*)Svc.GameGui.GetAddonByName(AddonName).Address;

        // Only ever flips a visible bar to hidden, so restoring it to visible can't reveal a bar the
        // player had hidden themselves. Re-checked every tick: the game re-shows the widget on its own
        // in the middle of some scenes (e.g. a duty cutscene ending a wipe).
        private void Hide()
        {
            var addon = GetAddon();
            if (addon == null || !addon->IsVisible) return;
            addon->IsVisible = false;
            hiddenByUs = true;
        }

        private void Restore()
        {
            if (!hiddenByUs) return;
            var addon = GetAddon();
            if (addon != null) addon->IsVisible = true;
            hiddenByUs = false;
        }
    }
}
