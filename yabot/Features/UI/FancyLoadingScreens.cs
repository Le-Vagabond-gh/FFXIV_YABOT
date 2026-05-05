using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using YABOT.FeaturesSetup;
using System;
using System.Linq;

namespace YABOT.Features.UI
{
    // Adapted from MapleHinata/Dalamud.LoadingImage by goat / Maple.
    // Uses the original plugin's byte-signature hook to capture the destination
    // territory ID. A polling-based replacement using GameMain.NextTerritoryTypeId
    // was attempted (see commit history) but produced wrong / stuck textures across
    // zone changes - the field's update timing relative to _LocationTitle.PreDraw
    // is inconsistent. The hook captures the destination as a function argument
    // which is racy-by-construction-impossible.
    public unsafe class FancyLoadingScreens : Feature
    {
        public override string Name => "Fancy Loading Screens";

        public override string Description =>
            "Replaces the loading-screen image with the destination zone's concept art. Adapted from Dalamud.LoadingImage by goat / Maple. Note: only renders correctly on 16:9 displays - the concept art textures don't have the resolution to fill ultrawide.";

        public override FeatureType FeatureType => FeatureType.UI;

        public override bool UseAutoConfig => false;

        public class Configs : FeatureConfig
        {
            // 16:9 reference layout (lifted verbatim from the standalone plugin).
            public int Width = 1920;
            public int Height = 1080;
            public float ScaleX = 0.595f;
            public float ScaleY = 0.595f;
            public float OffsetX = -60f;
            public float OffsetY = -220f;
        }

        public Configs Config { get; private set; } = null!;

        // Lumina sheet definition (not shipped in Lumina.Excel.Sheets).
        [Sheet("LoadingImage")]
        public readonly struct LoadingImageRow(ExcelPage page, uint offset, uint row) : IExcelRow<LoadingImageRow>
        {
            public uint RowId => row;
            public uint RowOffset { get; }
            public ExcelPage ExcelPage { get; }
            public ReadOnlySeString FileName => page.ReadString(offset, offset);

            public static LoadingImageRow Create(ExcelPage page, uint offset, uint row) => new(page, offset, row);
        }

        private delegate byte HandleTerriChangeDelegate(IntPtr a1, uint a2, byte a3, byte a4, IntPtr a5);
        private Hook<HandleTerriChangeDelegate>? handleTerriChangeHook;

        private ExcelSheet<TerritoryType>? terris;
        private ExcelSheet<LoadingImageRow>? loadings;
        private ExcelSheet<ContentFinderCondition>? cfcs;

        private int toLoadingTerri = -1;
        private bool hasLoading;

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();

            terris = Svc.Data.GetExcelSheet<TerritoryType>();
            loadings = Svc.Data.GetExcelSheet<LoadingImageRow>();
            cfcs = Svc.Data.GetExcelSheet<ContentFinderCondition>();

            handleTerriChangeHook = Svc.Hook.HookFromAddress<HandleTerriChangeDelegate>(
                Svc.SigScanner.ScanText("40 53 55 56 41 56 48 81 EC F8 00 00 00"),
                HandleTerriChangeDetour);
            handleTerriChangeHook.Enable();

            Svc.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "_LocationTitle", LocationTitleOnDraw);
            Svc.Framework.Update += FrameworkOnUpdate;
            base.Enable();
        }

        public override void Disable()
        {
            SaveConfig(Config);
            Svc.Framework.Update -= FrameworkOnUpdate;
            Svc.AddonLifecycle.UnregisterListener(LocationTitleOnDraw);
            handleTerriChangeHook?.Disable();
            handleTerriChangeHook?.Dispose();
            handleTerriChangeHook = null;
            base.Disable();
        }

        private byte HandleTerriChangeDetour(IntPtr a1, uint a2, byte a3, byte a4, IntPtr a5)
        {
            toLoadingTerri = (int)a2;
            Svc.Log.Verbose($"[FancyLoadingScreens] toLoadingTerri: {toLoadingTerri}");
            return handleTerriChangeHook!.Original(a1, a2, a3, a4, a5);
        }

        private void LocationTitleOnDraw(AddonEvent type, AddonArgs args)
        {
            if (terris == null || loadings == null || cfcs == null) return;

            try
            {
                var addon = (AtkUnitBase*)args.Addon.Address;
                if (addon == null) return;

                var regionImageNode = (AtkImageNode*)addon->GetNodeById(3);

                if (cfcs.Any(x => x.ContentLinkType == 1 && x.TerritoryType.RowId == toLoadingTerri))
                {
                    Svc.Log.Verbose("[FancyLoadingScreens] Is InstanceContent zone!");
                    hasLoading = false;
                    return;
                }

                if (!terris.TryGetRow((uint)toLoadingTerri, out var terriZone))
                {
                    Svc.Log.Verbose("[FancyLoadingScreens] terriZone null!");
                    hasLoading = false;
                    return;
                }

                if (!loadings.TryGetRow(terriZone.LoadingImage.RowId, out var loadingImage))
                {
                    Svc.Log.Verbose("[FancyLoadingScreens] LoadingImage null!");
                    hasLoading = false;
                    return;
                }

                if (regionImageNode == null)
                {
                    Svc.Log.Verbose("[FancyLoadingScreens] regionImageNode null!");
                    return;
                }

                var asset = regionImageNode->PartsList->Parts[regionImageNode->PartId].UldAsset;
                if (regionImageNode->Type == NodeType.Image && asset != null)
                {
                    var resource = asset->AtkTexture.Resource;
                    if (resource == null) return;

                    var name = resource->TexFileResourceHandle->ResourceHandle.FileName;
                    if (name.BufferPtr == null) return;

                    var texName = name.ToString();
                    if (!texName.Contains("loadingimage"))
                    {
                        regionImageNode->LoadTexture($"ui/loadingimage/{loadingImage.FileName}_hr1.tex");
                        Svc.Log.Verbose($"[FancyLoadingScreens] Replacing icon for territory {terriZone.RowId}");
                    }
                }

                hasLoading = true;
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "[FancyLoadingScreens] Could not replace loading image.");
            }
        }

        private void FrameworkOnUpdate(Dalamud.Plugin.Services.IFramework framework)
        {
            if (!hasLoading) return;

            try
            {
                var unitBase = (AtkUnitBase*)Svc.GameGui.GetAddonByName("_LocationTitle", 1).Address;
                var unitBaseShort = (AtkUnitBase*)Svc.GameGui.GetAddonByName("_LocationTitleShort", 1).Address;

                if (unitBase == null || unitBaseShort == null) return;

                var loadingImage = unitBase->UldManager.NodeList[4];
                if (loadingImage == null) return;
                var imgNode = (AtkImageNode*)loadingImage;

                var asset = imgNode->PartsList->Parts[imgNode->PartId].UldAsset;
                if (loadingImage->Type == NodeType.Image && asset != null)
                {
                    var resource = asset->AtkTexture.Resource;
                    if (resource == null) return;

                    var name = resource->TexFileResourceHandle->ResourceHandle.FileName;
                    if (name.BufferPtr == null) return;

                    var texName = name.ToString();
                    if (!texName.Contains("loadingimage"))
                    {
                        // Swap node order so the replaced texture renders on top.
                        var t = unitBase->UldManager.NodeList[4];
                        unitBase->UldManager.NodeList[4] = unitBase->UldManager.NodeList[5];
                        unitBase->UldManager.NodeList[5] = t;
                        t->DrawFlags |= 0x1;
                        loadingImage = unitBase->UldManager.NodeList[4];
                        Svc.Log.Verbose("[FancyLoadingScreens] Swapped!");
                    }
                }

                loadingImage->Width = (ushort)Config.Width;
                loadingImage->Height = (ushort)Config.Height;
                loadingImage->ScaleX = Config.ScaleX;
                loadingImage->ScaleY = Config.ScaleY;
                loadingImage->X = Config.OffsetX;
                loadingImage->Y = Config.OffsetY;
                loadingImage->Priority = 0;
                loadingImage->DrawFlags |= 0x1;

                hasLoading = false;
            }
            catch { }
        }

        protected override DrawConfigDelegate DrawConfigTree => (ref bool hasChanged) =>
        {
            ImGui.TextWrapped("Layout (defaults are 16:9-tuned; reset if you mess with these and want sane numbers back):");
            if (ImGui.InputInt("Width", ref Config.Width)) hasChanged = true;
            if (ImGui.InputInt("Height", ref Config.Height)) hasChanged = true;
            if (ImGui.InputFloat("Scale X", ref Config.ScaleX)) hasChanged = true;
            if (ImGui.InputFloat("Scale Y", ref Config.ScaleY)) hasChanged = true;
            if (ImGui.InputFloat("Offset X", ref Config.OffsetX)) hasChanged = true;
            if (ImGui.InputFloat("Offset Y", ref Config.OffsetY)) hasChanged = true;
            if (ImGui.Button("Reset to 16:9 defaults"))
            {
                Config = new Configs();
                hasChanged = true;
            }
        };
    }
}
