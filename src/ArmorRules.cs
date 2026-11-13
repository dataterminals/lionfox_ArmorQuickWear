using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;

namespace lionfox_ArmorQuickWear
{
    // What counts as armor, and which real piece stands in for each mounted one. Shared by both
    // sides: the server moves items with it, the client only colors the tab with it.
    //
    // Everything here goes through the vanilla slot API. Combat Overhaul overrides those same
    // methods (CanHold, CanTake, FlipWith) with its layer and zone rules, so asking a slot is asking
    // CO, without this mod referencing CO's assembly.
    public static class ArmorRules
    {
        public sealed class Status
        {
            // Cell -> the slot holding the piece that fills it.
            public readonly Dictionary<int, ItemSlot> Worn;
            public readonly Dictionary<int, ItemSlot> Carried;

            public Status(Dictionary<int, ItemSlot> worn, Dictionary<int, ItemSlot> carried)
            {
                Worn = worn;
                Carried = carried;
            }
        }

        // Attributes that change as a piece gets used. A dented helmet still looks like the one mounted.
        static readonly string[] WearAndTear = { "durability", "condition" };

        public static string[] LookIgnoredAttributes()
        {
            return GlobalConstants.IgnoredStackAttributes.Concat(WearAndTear).ToArray();
        }

        // The API marks inventory indexers nullable; a missing slot is simply skipped.
        public static IEnumerable<ItemSlot> SlotsOf(IInventory? inventory)
        {
            if (inventory == null) yield break;

            for (int i = 0; i < inventory.Count; i++)
            {
                if (inventory[i] is { } slot) yield return slot;
            }
        }

        // CO's armor slots are plain ItemSlots in the character inventory; vanilla's are
        // ItemSlotCharacter typed ArmorHead/Body/Legs. Clothes and CO gear slots are
        // ItemSlotCharacter with other types, so belts and backpacks never count.
        public static bool IsArmorSlot(ItemSlot slot)
        {
            if (slot is ItemSlotCharacter dressSlot)
            {
                return dressSlot.Type == EnumCharacterDressType.ArmorHead
                    || dressSlot.Type == EnumCharacterDressType.ArmorBody
                    || dressSlot.Type == EnumCharacterDressType.ArmorLegs;
            }
            return true;
        }

        // Asks the item first (vanilla's wearable interface, which CO's armor implements too). CO
        // pieces can also carry a vanilla clothing category that says otherwise, so fall back to
        // whether one of this character's armor slots would take it.
        public static bool IsArmorPiece(ItemStack stack, IInventory? character)
        {
            var probe = new DummySlot(stack);
            if (stack.Collectible?.GetCollectibleInterface<IWearableStatsSupplier>()?.IsArmorType(probe) == true)
            {
                return true;
            }
            return SlotsOf(character).Any(slot => IsArmorSlot(slot) && slot.CanHold(probe));
        }

        // Taking a bag-like piece out of a CO armor slot spills its contents on the ground, so a
        // piece holding anything is never moved.
        public static bool HoldsItems(ItemStack? stack)
        {
            if (stack?.Collectible?.GetCollectibleInterface<IHeldBag>() is not { } bag) return false;
            return !bag.IsEmpty(stack);
        }

        // A piece that changes item when it changes state is still the same piece: lionfox Cloaked's hood and hooded helm
        // are hood-up / -half / -down (Sylvia, 2026-11-07: a mounted hood went unfound once it was raised). Their codes
        // are compared with the "state" variant left out.
        public static bool SameKind(ItemStack a, ItemStack b)
        {
            if (a.Class != b.Class) return false;
            if (a.Id == b.Id) return true;
            CollectibleObject? ca = a.Collectible, cb = b.Collectible;
            return ca?.Variant?["state"] != null && cb?.Variant?["state"] != null
                && ca.CodeWithVariant("state", "*").Equals(cb.CodeWithVariant("state", "*"));
        }

        // Worn pieces that make way for a mounted piece their slot would refuse: lionfox Cloaked's standalone hood
        // (hood-up / -half / -down) takes the head slot from every helmet, so putting on a mounted helmet took the hood off
        // first instead of skipping the helmet (Sylvia, 2026-11-07). Matched by code: this mod references no other.
        static readonly AssetLocation[] GivesWay = { new("lionfoxcloaked", "hood-*") };

        public static bool IsHood(ItemStack? stack)
        {
            AssetLocation? code = stack?.Collectible?.Code;
            return code != null && GivesWay.Any(w => WildcardUtil.Match(w, code));
        }

        // Both ways since 1.2.0 (Sylvia, 2026-11-12): a worn hood makes way for a mounted helmet, and a worn helmet for a
        // mounted hood. Whatever made way goes back on at the next take-off (LoadoutServer's set-aside). MakeWay still
        // puts a piece straight back if moving it didn't free the slot, so a piece that wasn't in the way stays on.
        public static bool GivesWayTo(ItemStack? worn, ItemStack? incoming)
        {
            return IsHood(worn) || (IsHood(incoming) && IsHeadPiece(worn));
        }

        // A helmet, by its code (armor-head-*, platehead-*, el-...-head-...): this mod references neither CO nor Cloaked,
        // and only head pieces can stand in a hood's way. Trying the others would flip them into the bags and back for
        // nothing (MakeWay's undo), nudging bag items aside on the way.
        static bool IsHeadPiece(ItemStack? stack)
        {
            string? path = stack?.Collectible?.Code?.Path;
            return path != null && path.Contains("head", System.StringComparison.Ordinal);
        }

        // A bag slot holds a worn bag, not carried gear, so it is never a source or a destination.
        public static bool IsCarrySlot(ItemSlot slot)
        {
            return slot is not ItemSlotBackpack;
        }

        public static List<ItemSlot> WornArmorSlots(IPlayer player)
        {
            var character = player.InventoryManager.GetOwnInventory(GlobalConstants.characterInvClassName);
            return SlotsOf(character).Where(slot => IsArmorSlot(slot) && !slot.Empty).ToList();
        }

        // Backpack first, then the hotbar, so a tie goes to the bags.
        public static List<ItemSlot> CarriedSlots(IPlayer player)
        {
            var slots = new List<ItemSlot>();
            foreach (string className in new[] { GlobalConstants.backpackInvClassName, GlobalConstants.hotBarInvClassName })
            {
                var inventory = player.InventoryManager.GetOwnInventory(className);
                slots.AddRange(SlotsOf(inventory).Where(slot => IsCarrySlot(slot) && !slot.Empty));
            }
            return slots;
        }

        // Worn pieces are matched first, so a spare in the bags is never mistaken for the one already on.
        public static Status Evaluate(IWorldAccessor world, IPlayer player, Loadout loadout)
        {
            string[] ignored = LookIgnoredAttributes();
            var cells = loadout.OccupiedCells().ToList();
            var worn = Match(world, loadout, cells, WornArmorSlots(player), ignored);
            var rest = cells.Where(cell => !worn.ContainsKey(cell)).ToList();
            var carried = Match(world, loadout, rest, CarriedSlots(player), ignored);
            return new Status(worn, carried);
        }

        // Every cell gets its exact look before any cell settles for another piece of the same kind,
        // so a spare can't take the place of the piece that was actually mounted. Among equals the
        // one with the most durability left wins.
        static Dictionary<int, ItemSlot> Match(IWorldAccessor world, Loadout loadout, List<int> cells, List<ItemSlot> slots, string[] ignored)
        {
            var matched = new Dictionary<int, ItemSlot>();
            var claimed = new HashSet<ItemSlot>();

            foreach (bool exactLook in new[] { true, false })
            {
                foreach (int cell in cells)
                {
                    if (matched.ContainsKey(cell)) continue;

                    ItemStack template = loadout.Cells[cell]!.Template;
                    ItemSlot? best = null;
                    int bestDurability = int.MinValue;

                    foreach (var slot in slots)
                    {
                        ItemStack? stack = slot.Itemstack;
                        if (stack?.Collectible == null || claimed.Contains(slot) || !SameKind(stack, template)) continue;
                        if (exactLook && !stack.Equals(world, template, ignored)) continue;

                        int durability = stack.Collectible.GetRemainingDurability(stack);
                        if (durability > bestDurability)
                        {
                            best = slot;
                            bestDurability = durability;
                        }
                    }

                    if (best != null)
                    {
                        matched[cell] = best;
                        claimed.Add(best);
                    }
                }
            }
            return matched;
        }
    }
}
