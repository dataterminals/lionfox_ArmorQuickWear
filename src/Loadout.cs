using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace lionfox_ArmorQuickWear
{
    public class LoadoutEntry
    {
        // A copy of the piece as it was when mounted. Only its kind and look matter; the real item
        // stays wherever the player keeps it.
        public ItemStack Template;

        // The carried slot the piece was last put on from, so taking it off can return it there.
        public string? ReturnInventoryId;
        public int ReturnSlotId = -1;

        public LoadoutEntry(ItemStack template)
        {
            Template = template;
        }
    }

    public class Loadout
    {
        public const int CellCount = 18;
        const int FormatVersion = 1;

        public readonly LoadoutEntry?[] Cells = new LoadoutEntry?[CellCount];

        public bool IsEmpty => Array.TrueForAll(Cells, entry => entry == null);

        public IEnumerable<int> OccupiedCells()
        {
            for (int i = 0; i < CellCount; i++)
            {
                if (Cells[i] != null) yield return i;
            }
        }

        public byte[] ToBytes()
        {
            var tree = new TreeAttribute();
            tree.SetInt("version", FormatVersion);
            var cells = tree.GetOrAddTreeAttribute("cells");
            for (int i = 0; i < CellCount; i++)
            {
                if (Cells[i] is not { } entry) continue;

                var cell = cells.GetOrAddTreeAttribute(i.ToString());
                cell.SetItemstack("stack", entry.Template);
                if (entry.ReturnInventoryId != null)
                {
                    cell.SetString("returnInv", entry.ReturnInventoryId);
                    cell.SetInt("returnSlot", entry.ReturnSlotId);
                }
            }
            return tree.ToBytes();
        }

        // Pieces whose item no longer exists (its mod was removed) are dropped instead of being
        // kept as ghosts nothing can ever match.
        public static Loadout FromBytes(byte[]? data, IWorldAccessor world, ILogger logger)
        {
            var loadout = new Loadout();
            if (data == null || data.Length == 0) return loadout;

            var cells = TreeAttribute.CreateFromBytes(data).GetTreeAttribute("cells");
            if (cells == null) return loadout;

            for (int i = 0; i < CellCount; i++)
            {
                var cell = cells.GetTreeAttribute(i.ToString());
                var stack = cell?.GetItemstack("stack");
                if (cell == null || stack == null) continue;

                if (!stack.ResolveBlockOrItem(world))
                {
                    logger.Warning("Dropping mounted piece in cell {0}: its item no longer exists.", i);
                    continue;
                }
                loadout.Cells[i] = new LoadoutEntry(stack)
                {
                    ReturnInventoryId = cell.GetString("returnInv"),
                    ReturnSlotId = cell.GetInt("returnSlot", -1)
                };
            }
            return loadout;
        }
    }
}
