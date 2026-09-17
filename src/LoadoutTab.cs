using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace lionfox_ArmorQuickWear
{
    // The Quick Wear tab of the character dialog, added through GuiDialogCharacterBase's public
    // Tabs and RenderTabHandlers lists.
    public class LoadoutTab : IDisposable
    {
        const int Columns = 6;
        const int RefreshMs = 250;
        const string WornColor = "#4f7f4f";
        const string MissingColor = "#884444";
        const string StatusKey = "lionfoxarmorquickwear-status";

        readonly ICoreClientAPI capi;
        readonly LoadoutClient client;
        readonly GhostInventory cells;
        readonly int tabIndex;
        readonly long tickListenerId;

        // Set only while this tab is the one on screen.
        GuiComposer? composer;
        string statusText = "";
        bool clickedThisPress;

        public LoadoutTab(ICoreClientAPI capi, LoadoutClient client, GuiDialogCharacterBase dialog)
        {
            this.capi = capi;
            this.client = client;
            cells = new GhostInventory(capi, Loadout.CellCount, OnCellClicked);

            // A tab's DataInt is the index its render handler is looked up by, so take it from the
            // handler list rather than assume the two lists have always grown together.
            tabIndex = dialog.RenderTabHandlers.Count;
            dialog.RenderTabHandlers.Add(Compose);
            dialog.Tabs.Add(new GuiTab { Name = Lang.Get("lionfoxarmorquickwear:tab-name"), DataInt = tabIndex });

            dialog.TabClicked += index =>
            {
                if (index != tabIndex) composer = null;
            };
            dialog.OnClosed += () => composer = null;

            // Holding the button and dragging across the grid re-clicks every cell passed over. The
            // game raises these before any dialog sees the click, so they bracket one press.
            capi.Event.MouseDown += _ => clickedThisPress = false;
            capi.Event.MouseUp += _ => clickedThisPress = false;

            tickListenerId = capi.Event.RegisterGameTickListener(_ =>
            {
                if (composer != null) Refresh();
            }, RefreshMs);
        }

        void Compose(GuiComposer compo)
        {
            composer = compo;
            Refresh();

            ElementBounds statusBounds = ElementBounds.Fixed(0, 25, 310, 20);
            ElementBounds gridBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 50, Columns, Loadout.CellCount / Columns);
            ElementBounds putOnBounds = ElementBounds.Fixed(0, 0, 150, 25).FixedUnder(gridBounds, 10);
            ElementBounds takeOffBounds = ElementBounds.Fixed(0, 0, 150, 25).FixedUnder(gridBounds, 10).FixedRightOf(putOnBounds, 6);
            ElementBounds mountWornBounds = ElementBounds.Fixed(0, 0, 150, 25).FixedUnder(putOnBounds, 6);
            ElementBounds clearBounds = ElementBounds.Fixed(0, 0, 150, 25).FixedUnder(putOnBounds, 6).FixedRightOf(mountWornBounds, 6);
            ElementBounds hintBounds = ElementBounds.Fixed(0, 0, 310, 45).FixedUnder(mountWornBounds, 10);

            compo
                .AddDynamicText(statusText, CairoFont.WhiteSmallText(), statusBounds, StatusKey)
                .AddItemSlotGrid(cells, _ => { }, Columns, gridBounds, "lionfoxarmorquickwear-cells")
                .AddSmallButton(Lang.Get("lionfoxarmorquickwear:button-puton"), () => Press(QuickWearAction.PutOn), putOnBounds)
                .AddSmallButton(Lang.Get("lionfoxarmorquickwear:button-takeoff"), () => Press(QuickWearAction.TakeOff), takeOffBounds)
                .AddSmallButton(Lang.Get("lionfoxarmorquickwear:button-mountworn"), () => Press(QuickWearAction.MountWorn), mountWornBounds)
                .AddSmallButton(Lang.Get("lionfoxarmorquickwear:button-clear"), () => Press(QuickWearAction.ClearAll), clearBounds)
                .AddStaticText(Lang.Get("lionfoxarmorquickwear:hint"), CairoFont.WhiteDetailText(), hintBounds);
        }

        bool Press(QuickWearAction action)
        {
            client.Send(action);
            return true;
        }

        void OnCellClicked(int cell, ItemStackMoveOperation op)
        {
            if (op.MouseButton != EnumMouseButton.Left && op.MouseButton != EnumMouseButton.Right) return;
            if (clickedThisPress) return;
            clickedThisPress = true;

            ItemSlot cursor = capi.World.Player.InventoryManager.MouseItemSlot;
            if (!op.ShiftDown && !cursor.Empty)
            {
                client.RequestMount(cell, cursor);
            }
            else if (client.Loadout.Cells[cell] != null)
            {
                client.Send(QuickWearAction.Unmount, cell);
            }
        }

        // Green: worn. Plain: in the bags. Red and crossed out: nowhere on the player.
        public void Refresh()
        {
            if (capi.World is not { } world || world.Player is not { } player) return;

            var loadout = client.Loadout;
            var status = ArmorRules.Evaluate(world, player, loadout);
            int worn = 0, carried = 0, missing = 0;

            for (int i = 0; i < Loadout.CellCount; i++)
            {
                var entry = loadout.Cells[i];
                if (entry == null)
                {
                    cells.Show(i, null, null, false);
                }
                else if (status.Worn.TryGetValue(i, out var wornSlot))
                {
                    cells.Show(i, wornSlot.Itemstack, WornColor, false);
                    worn++;
                }
                else if (status.Carried.TryGetValue(i, out var carriedSlot))
                {
                    cells.Show(i, carriedSlot.Itemstack, null, false);
                    carried++;
                }
                else
                {
                    cells.Show(i, entry.Template, MissingColor, true);
                    missing++;
                }
            }

            string text = Lang.Get("lionfoxarmorquickwear:status", worn, carried, missing);
            if (text == statusText) return;
            statusText = text;
            composer?.GetDynamicText(StatusKey)?.SetNewText(statusText);
        }

        public void Dispose()
        {
            capi.Event.UnregisterGameTickListener(tickListenerId);
        }
    }
}
