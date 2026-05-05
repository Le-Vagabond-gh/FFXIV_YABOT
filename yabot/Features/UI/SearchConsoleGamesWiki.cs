using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using YABOT.FeaturesSetup;
using System;

namespace YABOT.Features.UI
{
    public class SearchConsoleGamesWiki : Feature
    {
        public override string Name => "Search Console Games Wiki";

        public override string Description => "Adds a \"Search on Console Games Wiki\" option to item right-click menus.";

        public override FeatureType FeatureType => FeatureType.UI;

        private readonly SeString menuString = new SeString(new TextPayload("Search on Wiki"));

        public override void Enable()
        {
            Svc.ContextMenu.OnMenuOpened += OnMenuOpened;
            base.Enable();
        }

        private void OnMenuOpened(IMenuOpenedArgs args)
        {
            uint itemId = 0;

            if (args.MenuType == ContextMenuType.Inventory)
            {
                var targetItem = ((MenuTargetInventory)args.Target).TargetItem;
                if (targetItem == null) return;
                itemId = targetItem.Value.ItemId;
            }
            else if (args.MenuType == ContextMenuType.Default)
            {
                try
                {
                    unsafe
                    {
                        var agent = AgentItemDetail.Instance();
                        if (agent != null && agent->ItemId != 0)
                            itemId = agent->ItemId;
                    }
                }
                catch { return; }
            }

            if (itemId == 0) return;

            // Strip HQ flag
            if (itemId > 1000000) itemId -= 1000000;

            if (!Svc.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var item)) return;

            var itemName = item.Name.ToString();
            if (string.IsNullOrEmpty(itemName)) return;

            var menuItem = new MenuItem
            {
                Name = menuString,
                PrefixChar = 'Y',
                PrefixColor = 32,
            };
            menuItem.OnClicked += _ => OpenWikiSearch(itemName);
            args.AddMenuItem(menuItem);
        }

        private void OpenWikiSearch(string itemName)
        {
            var encoded = Uri.EscapeDataString(itemName);
            Dalamud.Utility.Util.OpenLink($"https://ffxiv.consolegameswiki.com/wiki/{encoded}");
        }

        public override void Disable()
        {
            Svc.ContextMenu.OnMenuOpened -= OnMenuOpened;
            base.Disable();
        }
    }
}
