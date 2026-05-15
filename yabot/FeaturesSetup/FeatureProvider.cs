using ECommons.DalamudServices;
using YABOT.Features;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace YABOT.FeaturesSetup
{
    public class FeatureProvider : IDisposable
    {
        public bool Disposed { get; protected set; } = false;

        public List<BaseFeature> Features { get; } = new();

        public Assembly Assembly { get; init; } = null!;

        public FeatureProvider(Assembly assembly)
        {
            Assembly = assembly;
        }

        public virtual void LoadFeatures()
        {
            foreach (var t in Assembly.GetTypes().Where(x => x.IsSubclassOf(typeof(BaseFeature)) && !x.IsAbstract))
            {
                try
                {
                    var feature = (BaseFeature)Activator.CreateInstance(t)!;
                    feature.InterfaceSetup(P, Svc.PluginInterface, Config, this);
                    feature.Setup();
                    Plugin.ApplyUserPreference(feature);
                    Features.Add(feature);
                }
                catch (Exception ex)
                {
                    Svc.Log.Error(ex, $"Feature not loaded: {t.Name}");
                }
            }
        }

        public void UnloadFeatures()
        {
            foreach (var t in Features)
            {
                if (t.Enabled || t.FeatureType == FeatureType.Commands)
                {
                    try
                    {
                        t.Disable();
                    }
                    catch (Exception ex)
                    {
                        Svc.Log.Error(ex, $"Cannot disable {t.Name}");
                    }
                }
            }
            Features.Clear();
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            UnloadFeatures();
            Disposed = true;
        }
    }
}
