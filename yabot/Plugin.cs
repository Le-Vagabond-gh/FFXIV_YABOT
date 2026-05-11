using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using YABOT.Features;
using YABOT.FeaturesSetup;
using YABOT.UI;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace YABOT;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "YABOT";
    private const string CommandName = "/yabot";

    internal WindowSystem Ws = null!;
    internal MainWindow MainWindow = null!;

    public static Plugin P { get; private set; } = null!;
    public static Configuration Config { get; private set; } = null!;

    public List<FeatureProvider> FeatureProviders = new();
    private FeatureProvider provider = null!;

    public IEnumerable<BaseFeature> Features =>
        FeatureProviders.Where(x => !x.Disposed).SelectMany(x => x.Features).OrderBy(x => x.Name);

    public Plugin(IDalamudPluginInterface pluginInterface, IFramework framework)
    {
        P = this;
        pluginInterface.Create<Service>(this);
        _ = framework.RunOnFrameworkThread(() =>
        {
            ECommonsMain.Init(pluginInterface, this, ECommons.Module.All);
            Initialize();
        });
    }

    private void Initialize()
    {
        Ws = new WindowSystem("YABOT");
        MainWindow = new MainWindow();
        Ws.AddWindow(MainWindow);

        Config = Svc.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Initialize(Svc.PluginInterface);

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the YABOT configuration window, or run a subcommand: /yabot dontforget [option] [on|off|toggle]",
            ShowInHelp = true,
        });

        Svc.PluginInterface.UiBuilder.Draw += Ws.Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi += ToggleMain;
        Svc.PluginInterface.UiBuilder.OpenMainUi += ToggleMain;

        Events.Init();

        provider = new FeatureProvider(Assembly.GetExecutingAssembly());
        provider.LoadFeatures();
        FeatureProviders.Add(provider);

        Svc.Log.Info("YABOT initialized - separately we are weak, but together we form a mighty YABOT!");
    }

    public void Dispose()
    {
        Svc.Commands.RemoveHandler(CommandName);

        foreach (var f in Features.Where(x => x is not null && x.Enabled))
        {
            try { f.Disable(); f.Dispose(); }
            catch (System.Exception ex) { Svc.Log.Error(ex, $"Error disposing {f.Name}"); }
        }

        provider?.UnloadFeatures();

        if (Svc.PluginInterface != null)
        {
            Svc.PluginInterface.UiBuilder.Draw -= Ws.Draw;
            Svc.PluginInterface.UiBuilder.OpenConfigUi -= ToggleMain;
            Svc.PluginInterface.UiBuilder.OpenMainUi -= ToggleMain;
        }

        Ws?.RemoveAllWindows();
        Events.Disable();
        FeatureProviders.Clear();
        ECommonsMain.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        args = (args ?? string.Empty).Trim();
        if (args.Length == 0)
        {
            ToggleMain();
            return;
        }

        var split = args.Split(' ', 2, System.StringSplitOptions.RemoveEmptyEntries);
        var sub = split[0].ToLowerInvariant();
        var rest = split.Length > 1 ? split[1] : string.Empty;

        switch (sub)
        {
            case "dontforget":
                var df = Features.OfType<YABOT.Features.Actions.DontForget>().FirstOrDefault();
                if (df == null)
                {
                    Svc.Chat.PrintError("[YABOT] Don't Forget feature not loaded.");
                    return;
                }
                df.HandleSubCommand(rest);
                break;
            default:
                Svc.Chat.PrintError($"[YABOT] Unknown subcommand '{sub}'. Try: /yabot dontforget [option] [on|off|toggle]");
                break;
        }
    }

    public void ToggleMain()
    {
        if (MainWindow != null) MainWindow.IsOpen = !MainWindow.IsOpen;
    }
}
