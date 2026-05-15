using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Dalamud.Plugin;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using YABOT.FeaturesSetup;
using YABOT.Helpers;

namespace YABOT.Features;

public abstract class BaseFeature
{
    protected Configuration? config;
    protected TaskManager TaskManager = null!;
    public FeatureProvider Provider { get; private set; } = null!;

    public virtual bool Enabled { get; protected set; }

    public virtual bool FeatureDisabled { get; protected set; }

    public virtual string DisabledReason { get; set; } = "";

    public abstract string Name { get; }

    public virtual string Key => GetType().Name;

    public abstract string Description { get; }

    /// <summary>
    /// (Command, Aliases/args, Description) tuples advertised in the Commands tab table.
    /// Override on features that register chat commands manually so they show up in the documentation table.
    /// </summary>
    public virtual System.Collections.Generic.IEnumerable<(string Command, string Aliases, string Description)> CommandReferences =>
        System.Array.Empty<(string, string, string)>();

    public virtual void Draw() { }
    public virtual bool DrawConditions() { return false; }

    public virtual bool Ready { get; protected set; }

    public abstract FeatureType FeatureType { get; }

    public void InterfaceSetup(Plugin plugin, IDalamudPluginInterface pluginInterface, Configuration config, FeatureProvider fp)
    {
        this.config = config;
        this.Provider = fp;
        this.TaskManager = new(new() { TimeoutSilently = true });
    }

    public virtual void Setup()
    {
        Ready = true;
    }

    public virtual void Enable()
    {
        Svc.Log.Debug($"Enabling {Name} / {this.GetType().Name}");
        Enabled = true;
    }

    public virtual void Disable()
    {
        TaskManager!.Abort();
        Enabled = false;
    }

    public virtual void Dispose()
    {
        Ready = false;
    }

    protected T? LoadConfig<T>() where T : FeatureConfig? => LoadConfig<T>(Key);

    protected T? LoadConfig<T>(string key) where T : FeatureConfig?
    {
        try
        {
            var configDirectory = Svc.PluginInterface.GetPluginConfigDirectory();
            var configFile = Path.Combine(configDirectory, key + ".json");
            if (!File.Exists(configFile)) return default;
            var jsonString = File.ReadAllText(configFile);
            return JsonConvert.DeserializeObject<T>(jsonString);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Failed to load config for feature {Name}");
            return default;
        }
    }

    protected void SaveConfig<T>(T config) where T : FeatureConfig? => SaveConfig(config, this.Key);

    protected void SaveConfig<T>(T config, string key) where T : FeatureConfig?
    {
        try
        {
            var configDirectory = Svc.PluginInterface.GetPluginConfigDirectory();
            var configFile = Path.Combine(configDirectory, key + ".json");
            var jsonString = JsonConvert.SerializeObject(config, Formatting.Indented);

            File.WriteAllText(configFile, jsonString);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Feature failed to write config {this.Name}");
        }
    }

    private void DrawAutoConfig()
    {
        var configChanged = false;
        try
        {
            var configObj = this.GetType().GetProperties().FirstOrDefault(p => p.PropertyType.IsSubclassOf(typeof(FeatureConfig)))?.GetValue(this);

            var fields = configObj?.GetType().GetFields()
                .Where(f => f.GetCustomAttribute(typeof(FeatureConfigOptionAttribute)) != null)
                .Select(f => (f, f.GetCustomAttribute(typeof(FeatureConfigOptionAttribute)) as FeatureConfigOptionAttribute))
                .OrderBy(a => a.Item2?.Priority).ThenBy(a => a.Item2?.Name);

            if (fields is null) return;

            var configOptionIndex = 0;
            foreach (var (f, attr) in fields)
            {
                if (attr is null) continue;
                if (attr.Disabled)
                    ImGui.BeginDisabled();

                if (attr.ConditionalDisplay)
                {
                    var conditionalMethod = configObj?.GetType().GetMethod($"ShouldShow{f.Name}", BindingFlags.Public | BindingFlags.Instance);
                    if (conditionalMethod != null)
                    {
                        var shouldShow = (bool)(conditionalMethod.Invoke(configObj, Array.Empty<object>()) ?? true);
                        if (!shouldShow) continue;
                    }
                }

                if (attr.SameLine) ImGui.SameLine();

                if (attr.Editor != null)
                {
                    var v = f.GetValue(configObj);
                    var arr = new[] { $"{attr.Name}##{f.Name}_{this.GetType().Name}_{configOptionIndex++}", v };
                    var o = (bool)attr.Editor.Invoke(null, arr)!;
                    if (o)
                    {
                        configChanged = true;
                        f.SetValue(configObj, arr[1]);
                    }
                }
                else if (f.FieldType == typeof(bool))
                {
                    var v = (bool)f.GetValue(configObj)!;
                    if (ImGui.Checkbox($"{attr.Name}##{f.Name}_{this.GetType().Name}_{configOptionIndex++}", ref v))
                    {
                        configChanged = true;
                        f.SetValue(configObj, v);
                    }
                }
                else if (f.FieldType == typeof(int))
                {
                    var v = (int)f.GetValue(configObj)!;
                    ImGui.SetNextItemWidth(attr.EditorSize == -1 ? -1 : attr.EditorSize * ImGui.GetIO().FontGlobalScale);
                    var e = attr.IntType switch
                    {
                        FeatureConfigOptionAttribute.NumberEditType.Slider => ImGui.SliderInt($"{attr.Name}##{f.Name}_{this.GetType().Name}_{configOptionIndex++}", ref v, attr.IntMin, attr.IntMax),
                        FeatureConfigOptionAttribute.NumberEditType.Drag => ImGui.DragInt($"{attr.Name}##{f.Name}_{this.GetType().Name}_{configOptionIndex++}", ref v, 1f, attr.IntMin, attr.IntMax),
                        _ => false
                    };

                    if (v % attr.IntIncrements != 0)
                    {
                        v = v.RoundOff(attr.IntIncrements);
                        if (v < attr.IntMin) v = attr.IntMin;
                        if (v > attr.IntMax) v = attr.IntMax;
                    }

                    if (attr.EnforcedLimit && v < attr.IntMin)
                    {
                        v = attr.IntMin;
                        e = true;
                    }

                    if (attr.EnforcedLimit && v > attr.IntMax)
                    {
                        v = attr.IntMax;
                        e = true;
                    }

                    if (e)
                    {
                        f.SetValue(configObj, v);
                        configChanged = true;
                    }
                }
                else if (f.FieldType == typeof(float))
                {
                    var v = (float)f.GetValue(configObj)!;
                    ImGui.SetNextItemWidth(attr.EditorSize == -1 ? -1 : attr.EditorSize * ImGui.GetIO().FontGlobalScale);
                    var e = attr.IntType switch
                    {
                        FeatureConfigOptionAttribute.NumberEditType.Slider => ImGui.SliderFloat($"{attr.Name}##{f.Name}_{this.GetType().Name}_{configOptionIndex++}", ref v, attr.FloatMin, attr.FloatMax, attr.Format),
                        FeatureConfigOptionAttribute.NumberEditType.Drag => ImGui.DragFloat($"{attr.Name}##{f.Name}_{this.GetType().Name}_{configOptionIndex++}", ref v, 1f, attr.FloatMin, attr.FloatMax, attr.Format),
                        _ => false
                    };

                    if (v % attr.FloatIncrements != 0)
                    {
                        v = v.RoundOff(attr.FloatIncrements);
                        if (v < attr.FloatMin) v = attr.FloatMin;
                        if (v > attr.FloatMax) v = attr.FloatMax;
                    }

                    if (attr.EnforcedLimit && v < attr.FloatMin)
                    {
                        v = attr.FloatMin;
                        e = true;
                    }

                    if (attr.EnforcedLimit && v > attr.FloatMax)
                    {
                        v = attr.FloatMax;
                        e = true;
                    }

                    if (e)
                    {
                        f.SetValue(configObj, v);
                        configChanged = true;
                    }
                }
                else
                {
                    ImGui.Text($"Invalid Auto Field Type: {f.Name}");
                }

                if (attr.Disabled)
                {
                    ImGui.EndDisabled();
                    ImGuiComponents.HelpMarker("Currently Disabled");
                }

            }

            if (configChanged)
            {
                SaveConfig((FeatureConfig)configObj!);
            }

        }
        catch (Exception ex)
        {
            ImGui.Text($"Error with AutoConfig: {ex.Message}");
            ImGui.TextWrapped($"{ex.StackTrace}");
        }
    }

    public virtual bool UseAutoConfig => false;

    public string LocalizedName => this.Name;

    public bool DrawConfig(ref bool hasChanged)
    {
        var configTreeOpen = false;
        if ((UseAutoConfig || DrawConfigTree != null) && Enabled)
        {
            var x = ImGui.GetCursorPosX();
            if (ImGui.TreeNode($"{this.Name}##treeConfig_{GetType().Name}"))
            {
                configTreeOpen = true;
                ImGui.SetCursorPosX(x);
                ImGui.BeginGroup();
                if (UseAutoConfig)
                    DrawAutoConfig();
                else
                    DrawConfigTree!(ref hasChanged);
                ImGui.EndGroup();
                ImGui.TreePop();
            }
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, 0x0u);
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, 0x0u);
            ImGui.TreeNodeEx(LocalizedName, ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen);
            ImGui.PopStyleColor();
            ImGui.PopStyleColor();
        }

        if (hasChanged && Enabled) ConfigChanged();
        return configTreeOpen;
    }

    protected delegate void DrawConfigDelegate(ref bool hasChanged);
    protected virtual DrawConfigDelegate? DrawConfigTree => null;

    protected virtual void ConfigChanged()
    {
        if (this is null) return;

        var config = this.GetType().GetProperties().FirstOrDefault(p => p.PropertyType.IsSubclassOf(typeof(FeatureConfig)));

        if (config != null)
        {
            var configObj = config.GetValue(this);
            if (configObj != null)
                SaveConfig((FeatureConfig)configObj);
        }
    }

    protected void Log(string msg) => Svc.Log.Debug($"[{Name}] {msg}");

    public unsafe bool ZoneHasFlight()
    {
        if (Svc.Objects.LocalPlayer is null) return false;
        var territory = Svc.Data.Excel.GetSheet<TerritoryType>()?.GetRow(Svc.ClientState.TerritoryType);
        return territory?.TerritoryIntendedUse.RowId is 1 or 47 or 49;
    }
}
