using Dalamud.Hooking;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;
using YABOT.FeaturesSetup;
using YABOT.Helpers;
using System;

namespace YABOT.Features.DeepDungeons
{
    // Every weapon model change goes through DrawDataContainer.LoadWeapon (slot 0 = main hand,
    // slot 1 = off hand, which also carries the hidden off-hand models bundled with a main hand,
    // e.g. MCH's aetherotransformer). Outside deep dungeons we remember the last value the game
    // loaded for the local player per slot; inside, the game's own load of the deep dungeon model
    // is swapped for the remembered one.
    //
    // Only game loads (skipGameObject == 0) are touched. Glamourer lets the game load through, then
    // re-loads its own weapon draw-object-only (skipGameObject == 1), so a Glamourer weapon override
    // still wins and Glamourer records the kept weapon as the "game" state - regardless of which
    // plugin's hook sits on top.
    public unsafe class KeepDeepDungeonWeapon : BaseFeature
    {
        public override string Name => "Keep Weapon in Deep Dungeons";

        public override string Description =>
            "Deep dungeons replace your weapon's appearance with a dungeon-specific model. This keeps the main hand and off hand you had when you entered instead, including glamour and dyes. Toggle it per deep dungeon. A Glamourer design that sets your weapon still takes priority.";

        public override FeatureType FeatureType => FeatureType.DeepDungeons;
        public override bool UseAutoConfig => true;

        public class Configs : FeatureConfig
        {
            [FeatureConfigOption("Palace of the Dead")]
            public bool PalaceOfTheDead = true;

            [FeatureConfigOption("Heaven-on-High")]
            public bool HeavenOnHigh = true;

            [FeatureConfigOption("Eureka Orthos")]
            public bool EurekaOrthos = true;

            [FeatureConfigOption("Pilgrim's Traverse")]
            public bool PilgrimsTraverse = true;
        }

        public Configs Config { get; private set; } = null!;

        // Deep dungeon territories grouped by PlaceName. Resolved from the territory rather than the
        // DD director, which may not exist yet while the character is spawned during zone-in.
        private const uint PalaceOfTheDeadPlace = 1793;
        private const uint HeavenOnHighPlace = 2775;
        private const uint EurekaOrthosPlace = 2529;
        private const uint PilgrimsTraversePlace = 5277;

        private Hook<DrawDataContainer.Delegates.LoadWeapon>? loadWeaponHook;

        // Last main/off hand the game loaded on the local player outside deep dungeons; 0 = unknown.
        private readonly ulong[] kept = new ulong[2];

        public override void Enable()
        {
            Config = LoadConfig<Configs>() ?? new Configs();

            loadWeaponHook ??= Svc.Hook.HookFromAddress<DrawDataContainer.Delegates.LoadWeapon>(
                DrawDataContainer.MemberFunctionPointers.LoadWeapon, LoadWeaponDetour);
            loadWeaponHook.Enable();

            Svc.ClientState.Logout += OnLogout;
            base.Enable();
        }

        public override void Disable()
        {
            SaveConfig(Config);
            loadWeaponHook?.Disable();
            Svc.ClientState.Logout -= OnLogout;
            base.Disable();
        }

        public override void Dispose()
        {
            loadWeaponHook?.Dispose();
            loadWeaponHook = null;
            base.Dispose();
        }

        // The remembered weapons belong to the character that loaded them.
        private void OnLogout(int type, int code) => Array.Clear(kept);

        private void LoadWeaponDetour(DrawDataContainer* drawData, DrawDataContainer.WeaponSlot slot, WeaponModelId weaponData,
            byte redrawOnEquality, byte a5, byte skipGameObject, byte a7, bool a8)
        {
            try
            {
                if (skipGameObject == 0 && (uint)slot < 2 && IsLocalPlayer(drawData))
                {
                    var territory = Svc.ClientState.TerritoryType;
                    if (!ZoneHelper.IsDeepDungeon(territory))
                    {
                        kept[(int)slot] = weaponData.Value;
                    }
                    else if (kept[(int)slot] != 0 && IsEnabledFor(territory) && weaponData.Value != kept[(int)slot])
                    {
                        Log($"slot {slot}: {weaponData.Value:X14} -> {kept[(int)slot]:X14} (flags {redrawOnEquality} {a5} {a7} {a8})");
                        weaponData.Value = kept[(int)slot];
                    }
                }
            }
            catch (Exception e)
            {
                Svc.Log.Error(e, $"[{Name}] LoadWeaponDetour");
            }

            loadWeaponHook!.Original(drawData, slot, weaponData, redrawOnEquality, a5, skipGameObject, a7, a8);
        }

        // Control's pointer rather than Dalamud's LocalPlayer, which can still be null while the
        // character is being spawned during zone-in - exactly when the deep dungeon weapon loads.
        private static bool IsLocalPlayer(DrawDataContainer* drawData)
        {
            var owner = drawData->OwnerObject;
            return owner != null && owner == (Character*)Control.GetLocalPlayer();
        }

        private bool IsEnabledFor(uint territory)
        {
            if (!Svc.Data.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var row)) return false;
            return row.PlaceName.RowId switch
            {
                PalaceOfTheDeadPlace => Config.PalaceOfTheDead,
                HeavenOnHighPlace => Config.HeavenOnHigh,
                EurekaOrthosPlace => Config.EurekaOrthos,
                PilgrimsTraversePlace => Config.PilgrimsTraverse,
                _ => false,
            };
        }
    }
}
