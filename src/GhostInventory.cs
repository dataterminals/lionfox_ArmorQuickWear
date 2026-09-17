using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace lionfox_ArmorQuickWear
{
    // The client-only inventory behind the Quick Wear grid. It exists so the vanilla slot grid can
    // draw the mounted pieces; it is never registered with the player's inventory manager, so the
    // server never hears of it. Clicks move nothing and go to the tab, which asks the server.
    public class GhostInventory : InventoryBase
    {
        readonly ItemSlot[] slots;
        readonly Action<int, ItemStackMoveOperation> onClick;

        public GhostInventory(ICoreAPI api, int count, Action<int, ItemStackMoveOperation> onClick)
            : base("lionfoxarmorquickwear-cells", api)
        {
            this.onClick = onClick;
            slots = GenEmptySlots(count);
        }

        public override int Count => slots.Length;

        public override ItemSlot this[int slotId]
        {
            get => slots[slotId];
            set => slots[slotId] = value;
        }

        protected override ItemSlot NewSlot(int slotId)
        {
            return new GhostSlot(this);
        }

        // Returning null means the grid has no inventory packet to send.
        public override object? ActivateSlot(int slotId, ItemSlot sourceSlot, ref ItemStackMoveOperation op)
        {
            onClick(slotId, op);
            return null;
        }

        public override bool CanContain(ItemSlot sinkSlot, ItemSlot sourceSlot)
        {
            return false;
        }

        // Shows a copy, so nothing that renders or describes a ghost can touch the real item.
        public void Show(int slotId, ItemStack? stack, string? backgroundColor, bool unavailable)
        {
            var slot = slots[slotId];
            bool sameStack = stack == null
                ? slot.Itemstack == null
                : slot.Itemstack != null && slot.Itemstack.StackSize == stack.StackSize && slot.Itemstack.Equals(Api.World, stack);

            if (sameStack && slot.HexBackgroundColor == backgroundColor && slot.DrawUnavailable == unavailable) return;

            slot.Itemstack = stack?.Clone();
            slot.HexBackgroundColor = backgroundColor;
            slot.DrawUnavailable = unavailable;
            MarkSlotDirty(slotId);
        }

        public override void FromTreeAttributes(ITreeAttribute tree)
        {
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
        }
    }

    public class GhostSlot : ItemSlot
    {
        public GhostSlot(InventoryBase inventory)
            : base(inventory)
        {
        }

        public override bool CanHold(ItemSlot sourceSlot)
        {
            return false;
        }

        public override bool CanTake()
        {
            return false;
        }

        public override bool CanTakeFrom(ItemSlot sourceSlot, EnumMergePriority priority = EnumMergePriority.AutoMerge)
        {
            return false;
        }
    }
}
