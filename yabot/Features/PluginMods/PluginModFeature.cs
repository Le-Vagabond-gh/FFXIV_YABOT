using ECommons.Reflection;
using YABOT.FeaturesSetup;

namespace YABOT.Features.PluginMods
{
    public abstract class PluginModFeature : Feature
    {
        public abstract string RequiredPluginName { get; }

        public override FeatureType FeatureType => FeatureType.PluginMods;

        public override bool FeatureDisabled
        {
            get => !DalamudReflector.TryGetDalamudPlugin(RequiredPluginName, out _, suppressErrors: true, ignoreCache: false);
            protected set { }
        }

        public override string DisabledReason
        {
            get => $"{RequiredPluginName} is not installed or not loaded. Reload YABOT after installing it.";
            set { }
        }
    }
}
