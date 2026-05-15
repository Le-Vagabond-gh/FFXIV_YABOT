using Dalamud.Game.Gui.NamePlate;
using Dalamud.IoC;
using Dalamud.Plugin.Services;

namespace YABOT
{
    internal class Service
    {
        [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
        [PluginService] public static ITextureProvider TextureProvider { get; private set; } = null!;
        [PluginService] public static IDtrBar DtrBar { get; private set; } = null!;
        [PluginService] public static INamePlateGui NamePlateGui { get; private set; } = null!;
    }
}
