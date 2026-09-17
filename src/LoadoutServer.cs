using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace lionfox_ArmorQuickWear
{
    public class LoadoutServer
    {
        const string ModdataKey = "lionfoxarmorquickwear-loadout";
        // Keeps a double-tapped key from flipping the armor straight back before the first
        // result has even reached the client.
        const long MoveCooldownMs = 300;
        const float SoundRange = 16f;

        readonly ICoreServerAPI sapi;
        readonly IServerNetworkChannel channel;
        readonly ILogger logger;
        readonly Dictionary<string, Loadout> loadouts = new();
        readonly Dictionary<string, long> lastMoveMs = new();

        public LoadoutServer(ICoreServerAPI api, IServerNetworkChannel channel, ILogger logger)
        {
            sapi = api;
            this.channel = channel;
            this.logger = logger;

            channel.SetMessageHandler<ActionPacket>(OnAction);
            api.Event.PlayerNowPlaying += player => SendLoadout(player, GetLoadout(player));
            api.Event.PlayerDisconnect += player =>
            {
                loadouts.Remove(player.PlayerUID);
                lastMoveMs.Remove(player.PlayerUID);
            };
        }

        // ---------- persistence ----------

        Loadout GetLoadout(IServerPlayer player)
        {
            if (loadouts.TryGetValue(player.PlayerUID, out var loadout)) return loadout;

            try
            {
                loadout = Loadout.FromBytes(player.WorldData.GetModdata(ModdataKey), sapi.World, logger);
            }
            catch (Exception e)
            {
                logger.Error("Could not read the Quick Wear loadout of {0}, starting empty: {1}", player.PlayerName, e);
                loadout = new Loadout();
            }
            loadouts[player.PlayerUID] = loadout;
            return loadout;
        }

        void Save(IServerPlayer player, Loadout loadout)
        {
            player.WorldData.SetModdata(ModdataKey, loadout.ToBytes());
        }

        void Changed(IServerPlayer player, Loadout loadout)
        {
            Save(player, loadout);
            SendLoadout(player, loadout);
        }

        // ---------- requests ----------

        void OnAction(IServerPlayer player, ActionPacket packet)
        {
            if (player.Entity == null || !player.Entity.Alive) return;
            if (player.WorldData.CurrentGameMode == EnumGameMode.Spectator) return;

            var loadout = GetLoadout(player);
            switch (packet.Action)
            {
                case QuickWearAction.Toggle:
                case QuickWearAction.PutOn:
                case QuickWearAction.TakeOff:
                    if (!CoolingDown(player)) Move(player, loadout, packet.Action);
                    break;

                case QuickWearAction.MountFromCursor:
                    MountFromCursor(player, loadout, packet.Cell);
                    break;

                case QuickWearAction.Unmount:
                    if (IsValidCell(packet.Cell) && loadout.Cells[packet.Cell] != null)
                    {
                        loadout.Cells[packet.Cell] = null;
                        Changed(player, loadout);
                    }
                    break;

                case QuickWearAction.MountWorn:
                    MountWorn(player, loadout);
                    break;

                case QuickWearAction.ClearAll:
                    Array.Clear(loadout.Cells);
                    Changed(player, loadout);
                    break;
            }
        }

        void MountFromCursor(IServerPlayer player, Loadout loadout, int cell)
        {
            // The server's own copy of the cursor, not anything the client claims to be holding.
            ItemStack? stack = player.InventoryManager.MouseItemSlot?.Itemstack;
            if (!IsValidCell(cell) || stack == null) return;

            var result = new ResultPacket { Action = QuickWearAction.MountFromCursor };
            var character = player.InventoryManager.GetOwnInventory(GlobalConstants.characterInvClassName);
            if (!ArmorRules.IsArmorPiece(stack, character))
            {
                AddProblem(result, cell, "notarmor");
                Send(player, result);
                return;
            }

            // The same look in two cells would only ever fight itself for one armor slot.
            string[] ignored = ArmorRules.LookIgnoredAttributes();
            for (int i = 0; i < Loadout.CellCount; i++)
            {
                if (i == cell || loadout.Cells[i] is not { } other) continue;
                if (ArmorRules.SameKind(other.Template, stack) && other.Template.Equals(sapi.World, stack, ignored))
                {
                    AddProblem(result, cell, "duplicate");
                    Send(player, result);
                    return;
                }
            }

            loadout.Cells[cell] = new LoadoutEntry(TemplateOf(stack));
            Changed(player, loadout);
        }

        void MountWorn(IServerPlayer player, Loadout loadout)
        {
            var result = new ResultPacket { Action = QuickWearAction.MountWorn };
            var worn = ArmorRules.WornArmorSlots(player);
            var alreadyMounted = new HashSet<ItemSlot>(ArmorRules.Evaluate(sapi.World, player, loadout).Worn.Values);

            foreach (var slot in worn)
            {
                if (alreadyMounted.Contains(slot) || slot.Itemstack is not { } stack) continue;

                int free = Array.IndexOf(loadout.Cells, null);
                if (free < 0)
                {
                    AddProblem(result, -1, "full");
                    break;
                }
                loadout.Cells[free] = new LoadoutEntry(TemplateOf(stack));
                result.Moved++;
            }

            if (result.Moved > 0) Changed(player, loadout);
            else if (result.Problems == null) AddProblem(result, -1, worn.Count == 0 ? "nothingworn" : "allmounted");
            Send(player, result);
        }

        // ---------- moving armor ----------

        void Move(IServerPlayer player, Loadout loadout, QuickWearAction action)
        {
            var result = new ResultPacket { Action = action };
            if (loadout.IsEmpty)
            {
                AddProblem(result, -1, "empty");
                Send(player, result);
                return;
            }

            // Toggle puts on while anything mounted is still carried, and takes off otherwise.
            bool putOn = action == QuickWearAction.PutOn
                || (action == QuickWearAction.Toggle && ArmorRules.Evaluate(sapi.World, player, loadout).Carried.Count > 0);
            result.Action = putOn ? QuickWearAction.PutOn : QuickWearAction.TakeOff;

            ItemStack? firstMoved = putOn
                ? PutOn(player, loadout, result)
                : TakeOff(player, loadout, result, action == QuickWearAction.Toggle);

            if (result.Moved > 0)
            {
                if (putOn) Save(player, loadout);
                PlayArmorSound(player, firstMoved);
            }
            Send(player, result);
        }

        ItemStack? PutOn(IServerPlayer player, Loadout loadout, ResultPacket result)
        {
            var character = player.InventoryManager.GetOwnInventory(GlobalConstants.characterInvClassName);
            ItemStack? firstMoved = null;
            bool anythingOff = false;

            foreach (int cell in loadout.OccupiedCells().ToList())
            {
                // Re-read every time: moving a piece that doubles as a bag makes CO rebuild the
                // backpack's slots, and slot references from before the move would be stale.
                var status = ArmorRules.Evaluate(sapi.World, player, loadout);
                if (status.Worn.ContainsKey(cell)) continue;
                anythingOff = true;

                if (!status.Carried.TryGetValue(cell, out var source))
                {
                    AddProblem(result, cell, "missing");
                    continue;
                }
                if (ArmorRules.HoldsItems(source.Itemstack))
                {
                    AddProblem(result, cell, "holdsitems");
                    continue;
                }

                string sourceInventoryId = source.Inventory.InventoryID;
                int sourceSlotId = source.Inventory.GetSlotId(source);

                // The same move CO's own right-click equip makes: flip into the first armor slot
                // that accepts the piece. A piece already worn in the way makes CanHold say no.
                ItemSlot? target = FirstFreeArmorSlotFor(character, source);
                if (target == null || !target.TryFlipWith(source))
                {
                    AddProblem(result, cell, "blocked");
                    continue;
                }
                target.MarkDirty();
                source.MarkDirty();

                var entry = loadout.Cells[cell]!;
                entry.ReturnInventoryId = sourceInventoryId;
                entry.ReturnSlotId = sourceSlotId;
                result.Moved++;
                firstMoved ??= target.Itemstack;
            }

            if (!anythingOff) AddProblem(result, -1, "alreadyon");
            return firstMoved;
        }

        ItemStack? TakeOff(IServerPlayer player, Loadout loadout, ResultPacket result, bool fromToggle)
        {
            ItemStack? firstMoved = null;
            bool anythingOn = false;

            foreach (int cell in loadout.OccupiedCells().ToList())
            {
                var status = ArmorRules.Evaluate(sapi.World, player, loadout);
                if (!status.Worn.TryGetValue(cell, out var worn)) continue;
                anythingOn = true;

                if (ArmorRules.HoldsItems(worn.Itemstack))
                {
                    AddProblem(result, cell, "holdsitems");
                    continue;
                }
                if (!worn.CanTake())
                {
                    AddProblem(result, cell, "stuck");
                    continue;
                }

                ItemSlot? target = FindReturnSlot(player, loadout.Cells[cell]!, worn);
                if (target == null || !target.TryFlipWith(worn))
                {
                    AddProblem(result, cell, "noroom");
                    continue;
                }
                target.MarkDirty();
                worn.MarkDirty();

                result.Moved++;
                firstMoved ??= target.Itemstack;
            }

            if (!anythingOn)
            {
                // A toggle only lands here when nothing mounted is carried either, so every piece
                // is missing and naming them is more useful than "nothing to take off".
                if (fromToggle)
                {
                    foreach (int cell in loadout.OccupiedCells()) AddProblem(result, cell, "missing");
                }
                else
                {
                    AddProblem(result, -1, "nothingon");
                }
            }
            return firstMoved;
        }

        static ItemSlot? FirstFreeArmorSlotFor(IInventory? character, ItemSlot source)
        {
            return ArmorRules.SlotsOf(character).FirstOrDefault(slot => ArmorRules.IsArmorSlot(slot) && slot.Empty && slot.CanHold(source));
        }

        // Back where the piece came from if that spot is still free, otherwise anywhere in the
        // backpack. Never the ground: if nothing fits, the piece stays on.
        static ItemSlot? FindReturnSlot(IServerPlayer player, LoadoutEntry entry, ItemSlot worn)
        {
            var backpack = player.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName);
            var hotbar = player.InventoryManager.GetOwnInventory(GlobalConstants.hotBarInvClassName);

            IInventory? origin = null;
            if (entry.ReturnInventoryId != null)
            {
                if (entry.ReturnInventoryId == backpack?.InventoryID) origin = backpack;
                else if (entry.ReturnInventoryId == hotbar?.InventoryID) origin = hotbar;
            }
            if (origin != null && entry.ReturnSlotId >= 0 && entry.ReturnSlotId < origin.Count
                && origin[entry.ReturnSlotId] is { } returnSlot && Fits(returnSlot, worn))
            {
                return returnSlot;
            }

            return ArmorRules.SlotsOf(backpack).FirstOrDefault(slot => Fits(slot, worn));
        }

        static bool Fits(ItemSlot slot, ItemSlot worn)
        {
            return ArmorRules.IsCarrySlot(slot) && slot.Empty && slot.CanHold(worn);
        }

        // The piece's own footstep sound (plate, chain, leather...), heard by everyone nearby.
        void PlayArmorSound(IServerPlayer player, ItemStack? stack)
        {
            if (stack?.Collectible == null) return;

            try
            {
                var sounds = stack.Collectible.GetCollectibleInterface<IWearableStatsSupplier>()?.GetFootStepSounds(new DummySlot(stack));
                if (sounds == null || sounds.Length == 0) return;
                sapi.World.PlaySoundAt(sounds[sapi.World.Rand.Next(sounds.Length)], player.Entity, null, true, SoundRange);
            }
            catch (Exception e)
            {
                logger.VerboseDebug("No armor sound for {0}: {1}", stack.Collectible.Code, e.Message);
            }
        }

        // ---------- helpers ----------

        bool CoolingDown(IServerPlayer player)
        {
            long now = sapi.World.ElapsedMilliseconds;
            if (lastMoveMs.TryGetValue(player.PlayerUID, out long last) && now - last < MoveCooldownMs) return true;
            lastMoveMs[player.PlayerUID] = now;
            return false;
        }

        static bool IsValidCell(int cell)
        {
            return cell >= 0 && cell < Loadout.CellCount;
        }

        static ItemStack TemplateOf(ItemStack stack)
        {
            var template = stack.Clone();
            template.StackSize = 1;
            return template;
        }

        static void AddProblem(ResultPacket result, int cell, string code)
        {
            (result.Problems ??= new List<Problem>()).Add(new Problem { Cell = cell, Code = code });
        }

        void SendLoadout(IServerPlayer player, Loadout loadout)
        {
            channel.SendPacket(new LoadoutPacket { Data = loadout.ToBytes() }, player);
        }

        void Send(IServerPlayer player, ResultPacket result)
        {
            channel.SendPacket(result, player);
        }
    }
}
