using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace lionfox_ArmorQuickWear
{
    public class LoadoutServer
    {
        const string ModdataKey = "lionfoxarmorquickwear-loadout";
        // What a put-on moved aside to make way (MakeWay), so the next take-off puts it back on (1.2.0). In the player's
        // moddata like the loadout, so it survives a restart between the two.
        const string SetAsideKey = "lionfoxarmorquickwear-setaside";
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

            // A piece that can't go on yet gets a second turn once the others are on (1.2.1; Sylvia, 2026-11-20: "I wear a
            // hood in my non-armor outfit and I have to quickwear twice to get it to cycle to the full armor set"). Every
            // piece that leaves the bags frees a slot, and a piece that waited may need one: the worn hood moves aside to
            // the end of the backpack. So the panel's order no longer decides whether one press does it all. A piece's
            // problem is only reported if its last turn failed too.
            var pending = loadout.OccupiedCells().ToList();
            var problems = new SortedDictionary<int, string>();
            for (int pass = 0; pass < 2 && pending.Count > 0; pass++)
            {
                var again = new List<int>();
                foreach (int cell in pending)
                {
                    // Re-read every time: moving a piece that doubles as a bag makes CO rebuild the
                    // backpack's slots, and slot references from before the move would be stale.
                    var status = ArmorRules.Evaluate(sapi.World, player, loadout);
                    if (status.Worn.ContainsKey(cell))
                    {
                        problems.Remove(cell);
                        continue;
                    }
                    anythingOff = true;

                    if (!status.Carried.TryGetValue(cell, out var source))
                    {
                        problems[cell] = "missing";
                        continue;
                    }
                    if (ArmorRules.HoldsItems(source.Itemstack))
                    {
                        problems[cell] = "holdsitems";
                        continue;
                    }

                    string sourceInventoryId = source.Inventory.InventoryID;
                    int sourceSlotId = source.Inventory.GetSlotId(source);

                    // The same move CO's own right-click equip makes: flip into the first armor slot
                    // that accepts the piece. A piece already worn in the way makes CanHold say no.
                    ItemSlot? target = FirstFreeArmorSlotFor(character, source);
                    ItemStack? setAside = null;
                    if (target == null) target = MakeWay(player, character, source, status, out setAside);
                    if (target == null || !target.TryFlipWith(source))
                    {
                        // the piece that made way goes straight back on: a put-on never leaves it off for nothing
                        if (setAside != null) RestoreOne(player, character, setAside);
                        problems[cell] = "blocked";
                        again.Add(cell);
                        continue;
                    }
                    target.MarkDirty();
                    source.MarkDirty();
                    problems.Remove(cell);

                    var entry = loadout.Cells[cell]!;
                    entry.ReturnInventoryId = sourceInventoryId;
                    entry.ReturnSlotId = sourceSlotId;
                    result.Moved++;
                    firstMoved ??= target.Itemstack;
                }
                pending = again;
            }
            foreach (var problem in problems) AddProblem(result, problem.Key, problem.Value);

            if (!anythingOff) AddProblem(result, -1, "alreadyon");
            return firstMoved;
        }

        ItemStack? TakeOff(IServerPlayer player, Loadout loadout, ResultPacket result, bool fromToggle)
        {
            var character = player.InventoryManager.GetOwnInventory(GlobalConstants.characterInvClassName);
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

                ItemSlot? target = FindReturnSlot(player, worn, character);
                if (target == null || !target.TryFlipWith(worn))
                {
                    AddProblem(result, cell, "noroom");
                    continue;
                }
                target.MarkDirty();
                worn.MarkDirty();

                result.Moved++;
                firstMoved ??= target.Itemstack;

                // what made way goes back on as soon as its slot is free, before the next piece wants bag room: it leaves
                // a bag slot as it goes (1.2.1; 1.2.0 put it back only after every piece was off)
                ItemStack? back = RestoreFitting(player, character, result, final: false);
                firstMoved ??= back;
            }

            // whatever made way and still waits goes back on now, or is reported (1.2.0)
            int before = result.Moved;
            ItemStack? restored = RestoreFitting(player, character, result, final: true);
            firstMoved ??= restored;

            if (!anythingOn && result.Moved == before)
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

        // When nothing takes the piece, a worn piece that gives way (ArmorRules.GivesWayTo: Cloaked's standalone hood, or
        // a helmet when the piece is that hood) and isn't itself mounted goes to the end of the backpack the way taking
        // off does, and the piece gets its slot. The piece that made way is remembered (setAside) so the next take-off
        // puts it back on. If the piece still doesn't fit, it goes back on where it was, so a put-on never leaves it off
        // for nothing.
        //
        // The end of the backpack is claimed around the panel's own pieces (1.2.1; Sylvia, 2026-11-20: "I have to
        // quickwear twice to get it to cycle to the full armor set"). Taking off leaves the set at the backpack's end,
        // and 1.2.0 asked "is this armour?" of what sat there: a hooded helm says no while a hood is worn (its
        // IsArmorType is false, and the hood blocks every head slot), so the helm was nudged aside and the hood parked
        // in the helm's own slot. The put-on then found the hood where the helm had been and put the hood back on,
        // quietly: press 1 left the hood on and the helm in the bags, press 2 (the end slot now free) did it.
        ItemSlot? MakeWay(IServerPlayer player, IInventory? character, ItemSlot source, ArmorRules.Status status, out ItemStack? setAside)
        {
            setAside = null;
            ItemStack? incoming = source.Itemstack;
            var mounted = new HashSet<ItemSlot>(status.Worn.Values);
            var keep = new HashSet<ItemSlot>(status.Carried.Values) { source };
            foreach (var worn in ArmorRules.SlotsOf(character).Where(slot => ArmorRules.IsArmorSlot(slot) && !slot.Empty).ToList())
            {
                if (mounted.Contains(worn) || !ArmorRules.GivesWayTo(worn.Itemstack, source.Itemstack) || !worn.CanTake() || ArmorRules.HoldsItems(worn.Itemstack)) continue;
                ItemStack moving = TemplateOf(worn.Itemstack!);
                ItemSlot? bag = FindReturnSlot(player, worn, character, keep);
                if (bag == null || !bag.TryFlipWith(worn)) continue;
                bag.MarkDirty();
                worn.MarkDirty();
                ItemSlot? target = source.Itemstack == incoming ? FirstFreeArmorSlotFor(character, source) : null;
                if (target != null)
                {
                    Remember(player, moving);
                    setAside = moving;
                    return target;
                }
                if (worn.TryFlipWith(bag))
                {
                    worn.MarkDirty();
                    bag.MarkDirty();
                }
            }
            return null;
        }

        // ---------- what made way, back on (1.2.0) ----------

        void Remember(IServerPlayer player, ItemStack moving)
        {
            var list = GetSetAside(player);
            string[] ignored = ArmorRules.LookIgnoredAttributes();
            if (list.Any(t => ArmorRules.SameKind(t, moving) && t.Equals(sapi.World, moving, ignored))) return;
            list.Add(moving);
            SaveSetAside(player, list);
        }

        // The one piece a failed put-on just moved aside, straight back on, and its record gone if it went.
        void RestoreOne(IServerPlayer player, IInventory? character, ItemStack template)
        {
            ItemSlot? source = FindSetAside(player, template, sapi.World);
            ItemSlot? target = source == null ? null : FirstFreeArmorSlotFor(character, source);
            if (source == null || target == null || !target.TryFlipWith(source)) return;
            target.MarkDirty();
            source.MarkDirty();
            var list = GetSetAside(player);
            string[] ignored = ArmorRules.LookIgnoredAttributes();
            int i = list.FindIndex(t => ArmorRules.SameKind(t, template) && t.Equals(sapi.World, template, ignored));
            if (i >= 0)
            {
                list.RemoveAt(i);
                SaveSetAside(player, list);
            }
        }

        static ItemSlot? FindSetAside(IServerPlayer player, ItemStack template, IWorldAccessor world)
        {
            string[] ignored = ArmorRules.LookIgnoredAttributes();
            return ArmorRules.CarriedSlots(player)
                .Where(slot => slot.Itemstack is { } s && ArmorRules.SameKind(s, template))
                .OrderByDescending(slot => slot.Itemstack!.Equals(world, template, ignored))
                .FirstOrDefault();
        }

        // Sylvia, 2026-11-12: "when you put the hood on standalone and then do the quickwear, when you take it back off it
        // doesnt put the hood back on". Each piece a put-on moved aside goes back on from the bags (its exact look first,
        // then any of its kind), into the first armor slot that takes it, and its record goes. A piece no longer in the
        // bags is forgotten. One whose slot is still taken waits for the next try; on the final one (final: true, the end
        // of a take-off) it stays in the bags with a message, and the record is cleared: it is one put-on's undo, not a
        // standing rule.
        ItemStack? RestoreFitting(IServerPlayer player, IInventory? character, ResultPacket result, bool final)
        {
            var list = GetSetAside(player);
            if (list.Count == 0) return null;
            var waiting = new List<ItemStack>();
            ItemStack? first = null;
            foreach (ItemStack template in list)
            {
                ItemSlot? source = FindSetAside(player, template, sapi.World);
                if (source == null || ArmorRules.HoldsItems(source.Itemstack)) continue;
                ItemSlot? target = FirstFreeArmorSlotFor(character, source);
                if (target == null || !target.TryFlipWith(source))
                {
                    if (final) AddProblem(result, -1, "restoreblocked");
                    else waiting.Add(template);
                    continue;
                }
                target.MarkDirty();
                source.MarkDirty();
                result.Moved++;
                first ??= target.Itemstack;
            }
            SaveSetAside(player, waiting);
            return first;
        }

        List<ItemStack> GetSetAside(IServerPlayer player)
        {
            var list = new List<ItemStack>();
            byte[]? data = player.WorldData.GetModdata(SetAsideKey);
            if (data == null || data.Length == 0) return list;
            try
            {
                var tree = TreeAttribute.CreateFromBytes(data);
                int n = tree.GetInt("n");
                for (int i = 0; i < n; i++)
                {
                    ItemStack? stack = tree.GetItemstack("s" + i);
                    if (stack != null && stack.ResolveBlockOrItem(sapi.World)) list.Add(stack);
                }
            }
            catch (Exception e)
            {
                logger.Warning("Could not read what Quick Wear set aside for {0}, forgetting it: {1}", player.PlayerName, e.Message);
            }
            return list;
        }

        void SaveSetAside(IServerPlayer player, List<ItemStack> list)
        {
            if (list.Count == 0)
            {
                player.WorldData.RemoveModdata(SetAsideKey);
                return;
            }
            var tree = new TreeAttribute();
            tree.SetInt("n", list.Count);
            for (int i = 0; i < list.Count; i++) tree.SetItemstack("s" + i, list[i]);
            player.WorldData.SetModdata(SetAsideKey, tree.ToBytes());
        }

        // Always the end of the backpack. Never the ground: if the backpack is completely full,
        // the piece stays on.
        //
        // 1.1.0 first tried the slot the piece was put on from. That spot is always free again by
        // the time the piece comes off (the piece left it), so armor picked up into the front
        // slots went straight back to the front every time and never reached the end (Gwen,
        // 2026-10-01). The remembered slot is still saved with the loadout, just not used.
        static ItemSlot? FindReturnSlot(IServerPlayer player, ItemSlot worn, IInventory? character, ISet<ItemSlot>? keep = null)
        {
            var backpack = player.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName);
            var carried = ArmorRules.SlotsOf(backpack).Where(ArmorRules.IsCarrySlot).ToList();
            return ClaimEndSlot(carried, worn, character, keep);
        }

        // Taken-off armor collects at the end of the backpack. Walk back from the last slot to the
        // first one that could hold the piece and isn't already holding armor; if something else
        // sits there, nudge it aside. The check is stateless (what's in the slot, not what this
        // take-off placed), so it stays right even if CO rebuilds the backpack's slots mid-way.
        static ItemSlot? ClaimEndSlot(List<ItemSlot> slots, ItemSlot worn, IInventory? character, ISet<ItemSlot>? keep = null)
        {
            for (int i = slots.Count - 1; i >= 0; i--)
            {
                var slot = slots[i];
                if (keep != null && keep.Contains(slot)) continue;   // a panel piece's slot: never claimed or nudged (1.2.1)
                if (!slot.CanHold(worn)) continue;
                if (slot.Empty) return slot;
                if (slot.Itemstack is { } occupant && ArmorRules.IsArmorPiece(occupant, character)) continue;
                if (TryNudge(slots, i)) return slot;
            }
            return null;
        }

        // Moves whatever is in slots[index] to the nearest free slot that takes it, preferring the
        // front on a tie. Fails only when no free slot anywhere can hold it.
        static bool TryNudge(List<ItemSlot> slots, int index)
        {
            var occupant = slots[index];
            if (!occupant.CanTake()) return false;

            for (int distance = 1; distance < slots.Count; distance++)
            {
                foreach (int j in new[] { index - distance, index + distance })
                {
                    if (j < 0 || j >= slots.Count || !slots[j].Empty || !slots[j].CanHold(occupant)) continue;
                    if (!slots[j].TryFlipWith(occupant)) continue;

                    slots[j].MarkDirty();
                    occupant.MarkDirty();
                    return true;
                }
            }
            return false;
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
