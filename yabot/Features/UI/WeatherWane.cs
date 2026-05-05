using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using YABOT.FeaturesSetup;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace YABOT.Features.UI
{
    public class WeatherWane : Feature
    {
        public override string Name => "Weather Forecast";

        public override string Description =>
            "FFXIV weather forecast panel: pick zones to track and see upcoming weather at a glance. Adds a cloud icon to the server info bar; click to toggle the forecast window, shift-click for settings.";

        public override FeatureType FeatureType => FeatureType.UI;

        public override bool UseAutoConfig => false;

        public class Configs : FeatureConfig
        {
            public HashSet<uint> SelectedZones = new();
            public int ForecastCount = 4;
            public bool LockWindow = false;
            public bool AutoResize = true;
            public bool ShowBackground = true;
        }

        public Configs Config { get; private set; } = null!;

        private WeatherForecastService? forecast;
        private MainWindow? mainWindow;
        private ConfigSubWindow? configSubWindow;
        private IDtrBarEntry? dtrEntry;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();

            forecast = new WeatherForecastService(Svc.Data);
            mainWindow = new MainWindow(Config, forecast);
            configSubWindow = new ConfigSubWindow(Config, forecast, mainWindow, this);

            P.Ws.AddWindow(mainWindow);
            P.Ws.AddWindow(configSubWindow);

            dtrEntry = Service.DtrBar.Get("YABOT.WeatherWane");
            dtrEntry.Text = new SeString(new TextPayload(" ☁ "));
            dtrEntry.Tooltip = new SeString(new TextPayload("Click: toggle weather window\nShift-click: settings"));
            dtrEntry.OnClick = _ =>
            {
                if (ImGui.GetIO().KeyShift)
                    configSubWindow.IsOpen = !configSubWindow.IsOpen;
                else
                    mainWindow.IsOpen = !mainWindow.IsOpen;
            };

            Svc.Framework.Update += OnUpdate;
            base.Enable();
        }

        public override void Disable()
        {
            SaveConfig(Config);
            Svc.Framework.Update -= OnUpdate;

            dtrEntry?.Remove();
            dtrEntry = null;

            if (mainWindow != null) P.Ws.RemoveWindow(mainWindow);
            if (configSubWindow != null) P.Ws.RemoveWindow(configSubWindow);

            mainWindow = null;
            configSubWindow = null;
            forecast = null;
            base.Disable();
        }

        private void OnUpdate(IFramework framework)
        {
            if (mainWindow != null && mainWindow.IsOpen)
                mainWindow.CheckForWeatherChange();
        }

        protected override DrawConfigDelegate DrawConfigTree => (ref bool hasChanged) =>
        {
            if (ImGui.Button("Open Weather Window") && mainWindow != null)
                mainWindow.IsOpen = true;
            ImGui.SameLine();
            if (ImGui.Button("Open Zone Settings") && configSubWindow != null)
                configSubWindow.IsOpen = true;

            ImGui.TextDisabled($"{Config.SelectedZones.Count} zone(s) selected");
            ImGui.TextDisabled("☁ in the server info bar - click to toggle, shift-click for settings.");
        };

        // ----- WeatherForecastService -----

        public record WeatherInfo(uint Id, string DisplayName, int Icon);

        public record WeatherListing(WeatherInfo Weather, DateTimeOffset Time, DateTimeOffset End);

        public class ZoneInfo
        {
            public uint TerritoryId { get; init; }
            public string Name { get; init; } = string.Empty;
            public (WeatherInfo Weather, byte CumulativeRate)[] Rates { get; init; } = Array.Empty<(WeatherInfo, byte)>();
        }

        public class WeatherForecastService
        {
            private const int MillisecondsPerEorzeaHour = 175_000;
            private const int MillisecondsPerEorzeaWeather = 8 * MillisecondsPerEorzeaHour;
            private const int SecondsPerEorzeaHour = MillisecondsPerEorzeaHour / 1000;
            private const int SecondsPerEorzeaDay = 24 * SecondsPerEorzeaHour;

            private readonly Dictionary<uint, ZoneInfo> zones = new();
            public IReadOnlyList<ZoneInfo> AllZones { get; }

            public WeatherForecastService(IDataManager dataManager)
            {
                var weathers = new Dictionary<uint, WeatherInfo>();
                foreach (var w in dataManager.GetExcelSheet<Weather>())
                    weathers[w.RowId] = new WeatherInfo(w.RowId, w.Name.ExtractText(), w.Icon);

                var weatherRates = new Dictionary<byte, (WeatherInfo Weather, byte CumulativeRate)[]>();
                foreach (var wr in dataManager.GetExcelSheet<WeatherRate>())
                {
                    var rates = new List<(WeatherInfo, byte)>();
                    byte cumulative = 0;
                    for (var i = 0; i < 8; i++)
                    {
                        var rate = wr.Rate[i];
                        if (rate <= 0) continue;
                        var weatherId = wr.Weather[i].RowId;
                        if (!weathers.TryGetValue(weatherId, out var weatherInfo)) continue;
                        cumulative += (byte)rate;
                        rates.Add((weatherInfo, cumulative));
                    }
                    if (rates.Count > 0)
                        weatherRates[(byte)wr.RowId] = rates.ToArray();
                }

                var allZones = new List<ZoneInfo>();
                var seenNames = new HashSet<string>();

                foreach (var tt in dataManager.GetExcelSheet<TerritoryType>())
                {
                    if (!tt.PCSearch || tt.WeatherRate.RowId == 0) continue;

                    var rateId = (byte)tt.WeatherRate.RowId;
                    if (!weatherRates.TryGetValue(rateId, out var rates) || rates.Length <= 1) continue;

                    var placeName = tt.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
                    if (string.IsNullOrEmpty(placeName) || !seenNames.Add(placeName)) continue;

                    var zone = new ZoneInfo { TerritoryId = tt.RowId, Name = placeName, Rates = rates };
                    zones[tt.RowId] = zone;
                    allZones.Add(zone);
                }

                allZones.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
                AllZones = allZones;
            }

            public WeatherListing[] GetForecast(uint territoryId, int count)
            {
                if (!zones.TryGetValue(territoryId, out var zone) || count <= 0)
                    return Array.Empty<WeatherListing>();

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var sync = now - (now % MillisecondsPerEorzeaWeather);

                var result = new WeatherListing[count];
                for (var i = 0; i < count; i++)
                {
                    var timestamp = sync + (long)i * MillisecondsPerEorzeaWeather;
                    var target = CalculateTarget(timestamp);
                    var weather = GetWeather(target, zone.Rates);
                    var time = DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
                    var end = DateTimeOffset.FromUnixTimeMilliseconds(timestamp + MillisecondsPerEorzeaWeather);
                    result[i] = new WeatherListing(weather, time, end);
                }
                return result;
            }

            public long GetCurrentWeatherPeriod()
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return now - (now % MillisecondsPerEorzeaWeather);
            }

            private static byte CalculateTarget(long unixMs)
            {
                var seconds = unixMs / 1000;
                var hour = seconds / SecondsPerEorzeaHour;
                var shiftedHour = (uint)(hour + 8 - hour % 8) % 24;
                var day = seconds / SecondsPerEorzeaDay;
                var ret = (uint)day * 100 + shiftedHour;
                ret = (ret << 11) ^ ret;
                ret = (ret >> 8) ^ ret;
                ret %= 100;
                return (byte)ret;
            }

            private static WeatherInfo GetWeather(byte target, (WeatherInfo Weather, byte CumulativeRate)[] rates)
            {
                foreach (var (weather, cumulativeRate) in rates)
                    if (cumulativeRate > target) return weather;
                return rates[^1].Weather;
            }
        }

        // ----- MainWindow (forecast table) -----

        public class MainWindow : Window
        {
            private readonly Configs config;
            private readonly WeatherForecastService forecast;
            private long lastWeatherPeriod;
            private readonly List<(string Zone, WeatherListing[] Weathers)> cachedData = new();
            private string[] timeHeaders = Array.Empty<string>();
            private bool dirty = true;
            private int autoFitFrames;

            private static readonly Vector2 IconSize = new(24, 24);
            private const ImGuiWindowFlags BaseFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
            private const ImGuiWindowFlags LockFlags = ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;

            public MainWindow(Configs config, WeatherForecastService forecast) : base("WeatherWane###YABOT_WeatherWaneMain", BaseFlags)
            {
                this.config = config;
                this.forecast = forecast;
                SizeConstraints = new WindowSizeConstraints
                {
                    MinimumSize = new Vector2(100, 50),
                    MaximumSize = new Vector2(2000, 1000),
                };
                UpdateFlags();
            }

            public void UpdateFlags()
            {
                var flags = config.LockWindow ? BaseFlags | LockFlags : BaseFlags;
                if (config.AutoResize || autoFitFrames > 0)
                    flags |= ImGuiWindowFlags.AlwaysAutoResize;
                if (!config.ShowBackground)
                    flags |= ImGuiWindowFlags.NoBackground;
                Flags = flags;
            }

            public void CheckForWeatherChange()
            {
                var period = forecast.GetCurrentWeatherPeriod();
                if (period != lastWeatherPeriod)
                {
                    lastWeatherPeriod = period;
                    dirty = true;
                }
            }

            public void SetDirty() => dirty = true;
            public void RequestAutoFit() => autoFitFrames = 3;

            private void RefreshCache()
            {
                if (!dirty) return;
                dirty = false;

                var count = config.ForecastCount;
                cachedData.Clear();

                foreach (var zone in forecast.AllZones)
                {
                    if (!config.SelectedZones.Contains(zone.TerritoryId)) continue;
                    var listings = forecast.GetForecast(zone.TerritoryId, count + 1);
                    cachedData.Add((zone.Name, listings));
                }

                timeHeaders = new string[count + 1];
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var sync = now - (now % 1_400_000);
                for (var i = 0; i < timeHeaders.Length; i++)
                {
                    var time = DateTimeOffset.FromUnixTimeMilliseconds(sync + (long)i * 1_400_000).LocalDateTime;
                    timeHeaders[i] = time.ToString("HH:mm");
                }
            }

            public override void PreDraw()
            {
                if (autoFitFrames > 0)
                {
                    autoFitFrames--;
                    UpdateFlags();
                }
            }

            public override void Draw()
            {
                RefreshCache();

                if (config.SelectedZones.Count == 0)
                {
                    ImGui.TextWrapped("No zones selected. Right-click the ☁ in the server info bar to open settings.");
                    return;
                }

                if (cachedData.Count == 0)
                {
                    ImGui.TextWrapped("No matching zones found.");
                    return;
                }

                var columnCount = config.ForecastCount + 2;

                if (!ImGui.BeginTable("YABOT_WeatherTable", columnCount,
                        ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable))
                    return;

                ImGui.TableSetupScrollFreeze(1, 1);
                ImGui.TableSetupColumn("Zone", ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.WidthFixed, 100 * ImGuiHelpers.GlobalScale);
                for (var i = 0; i < timeHeaders.Length; i++)
                    ImGui.TableSetupColumn(i == 0 ? "Now" : timeHeaders[i]);
                ImGui.TableHeadersRow();

                foreach (var (zone, weathers) in cachedData)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    var textOffset = (IconSize.Y * ImGuiHelpers.GlobalScale - ImGui.GetTextLineHeight()) / 2;
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + textOffset);
                    ImGui.TextUnformatted(zone);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(zone);

                    for (var i = 0; i < weathers.Length && i < timeHeaders.Length; i++)
                    {
                        ImGui.TableNextColumn();
                        if (i == 0)
                            ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.GetColorU32(new Vector4(0.2f, 0.4f, 0.2f, 0.3f)));

                        var listing = weathers[i];
                        var scaledIconSize = IconSize * ImGuiHelpers.GlobalScale;
                        var cellWidth = ImGui.GetContentRegionAvail().X;
                        var iconOffset = (cellWidth - scaledIconSize.X) / 2;
                        if (iconOffset > 0)
                            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + iconOffset);

                        var icon = Service.TextureProvider.GetFromGameIcon((uint)listing.Weather.Icon);
                        if (icon.TryGetWrap(out var wrap, out _))
                            ImGui.Image(wrap.Handle, scaledIconSize);
                        else
                            ImGui.Dummy(scaledIconSize);

                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(listing.Weather.DisplayName);
                    }
                }

                ImGui.EndTable();
            }
        }

        // ----- ConfigSubWindow (zone picker) -----

        public class ConfigSubWindow : Window
        {
            private readonly Configs config;
            private readonly WeatherForecastService forecast;
            private readonly MainWindow mainWindow;
            private readonly WeatherWane parent;
            private string filterText = string.Empty;

            public ConfigSubWindow(Configs config, WeatherForecastService forecast, MainWindow mainWindow, WeatherWane parent)
                : base("WeatherWane Settings###YABOT_WeatherWaneConfig", ImGuiWindowFlags.None)
            {
                this.config = config;
                this.forecast = forecast;
                this.mainWindow = mainWindow;
                this.parent = parent;
                SizeConstraints = new WindowSizeConstraints
                {
                    MinimumSize = new Vector2(350, 400),
                    MaximumSize = new Vector2(600, 800),
                };
            }

            public override void Draw()
            {
                var changed = false;

                if (ImGui.Button("Open Weather Window")) mainWindow.IsOpen = true;
                ImGui.Separator();

                if (ImGui.Checkbox("Show background", ref config.ShowBackground)) { mainWindow.UpdateFlags(); changed = true; }
                if (ImGui.Checkbox("Auto-resize window", ref config.AutoResize)) { mainWindow.UpdateFlags(); changed = true; }
                if (ImGui.SliderInt("Forecast periods", ref config.ForecastCount, 3, 16)) changed = true;
                if (ImGui.Checkbox("Lock window position", ref config.LockWindow)) { mainWindow.UpdateFlags(); changed = true; }

                ImGui.Separator();
                ImGui.Text($"Zones ({config.SelectedZones.Count} selected):");

                if (ImGui.Button("Select All"))
                {
                    foreach (var zone in forecast.AllZones)
                        config.SelectedZones.Add(zone.TerritoryId);
                    changed = true;
                }
                ImGui.SameLine();
                if (ImGui.Button("Deselect All"))
                {
                    config.SelectedZones.Clear();
                    changed = true;
                }

                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##filter", "Filter zones...", ref filterText, 256);
                ImGui.Separator();

                if (ImGui.BeginChild("YABOT_ZoneList", new Vector2(-1, -1), false))
                {
                    foreach (var zone in forecast.AllZones)
                    {
                        if (!string.IsNullOrEmpty(filterText)
                            && !zone.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var selected = config.SelectedZones.Contains(zone.TerritoryId);
                        if (ImGui.Checkbox(zone.Name, ref selected))
                        {
                            if (selected) config.SelectedZones.Add(zone.TerritoryId);
                            else config.SelectedZones.Remove(zone.TerritoryId);
                            changed = true;
                        }
                    }
                }
                ImGui.EndChild();

                if (changed)
                {
                    parent.SaveSubConfig();
                    mainWindow.SetDirty();
                }
            }
        }

        // Used by ConfigSubWindow; SaveConfig is protected on BaseFeature.
        internal void SaveSubConfig() => SaveConfig(Config);
    }
}
