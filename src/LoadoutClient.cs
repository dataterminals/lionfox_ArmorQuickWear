using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace lionfox_ArmorQuickWear
{
    public class LoadoutClient : IDisposable
    {
        const string ErrorCode = "lionfoxarmorquickwear";

        readonly ICoreClientAPI capi;
        readonly IClientNetworkChannel channel;
        readonly ILogger logger;
        readonly LoadoutTab? tab;

        // What was on the cursor when a mount was requested, to name it if the server says no.
        string? pendingMountName;

        public Loadout Loadout { get; private set; } = new Loadout();

        public LoadoutClient(ICoreClientAPI api, IClientNetworkChannel channel, ILogger logger)
        {
            capi = api;
            this.channel = channel;
            this.logger = logger;

            channel
                .SetMessageHandler<LoadoutPacket>(OnLoadout)
                .SetMessageHandler<ResultPacket>(OnResult);

            RegisterHotkey("lionfoxarmorquickwear-toggle", "hotkey-toggle", QuickWearAction.Toggle);
            RegisterHotkey("lionfoxarmorquickwear-puton", "hotkey-puton", QuickWearAction.PutOn);
            RegisterHotkey("lionfoxarmorquickwear-takeoff", "hotkey-takeoff", QuickWearAction.TakeOff);

            var characterDialog = api.Gui.LoadedGuis.OfType<GuiDialogCharacterBase>().FirstOrDefault();
            if (characterDialog == null)
            {
                logger.Warning("No character dialog found, so there is no Quick Wear tab. The hotkeys still work.");
            }
            else
            {
                tab = new LoadoutTab(api, this, characterDialog);
            }
        }

        void RegisterHotkey(string code, string langKey, QuickWearAction action)
        {
            // Unbound by default: with this many mods installed, any default key is likely taken.
            capi.Input.RegisterHotKey(code, Lang.Get("lionfoxarmorquickwear:" + langKey), GlKeys.Unknown, HotkeyType.GUIOrOtherControls);
            capi.Input.SetHotKeyHandler(code, _ =>
            {
                Send(action);
                return true;
            });
        }

        public void Send(QuickWearAction action, int cell = 0)
        {
            channel.SendPacket(new ActionPacket { Action = action, Cell = cell });
        }

        public void RequestMount(int cell, ItemSlot cursor)
        {
            pendingMountName = cursor.GetStackName();
            Send(QuickWearAction.MountFromCursor, cell);
        }

        void OnLoadout(LoadoutPacket packet)
        {
            try
            {
                Loadout = Loadout.FromBytes(packet.Data, capi.World, logger);
            }
            catch (Exception e)
            {
                logger.Error("Could not read the loadout sent by the server: {0}", e);
                return;
            }
            tab?.Refresh();
        }

        // A successful move needs no message: the armor visibly goes on or off and makes its sound.
        void OnResult(ResultPacket packet)
        {
            if (packet.Problems == null || packet.Problems.Count == 0) return;

            var messages = packet.Problems.Select(problem => Describe(packet.Action, problem)).ToList();
            capi.TriggerIngameError(this, ErrorCode, messages.Count == 1
                ? messages[0]
                : Lang.Get("lionfoxarmorquickwear:more-in-chat", messages[0], messages.Count - 1));

            if (messages.Count > 1)
            {
                foreach (string message in messages) capi.ShowChatMessage(message);
            }
        }

        string Describe(QuickWearAction action, Problem problem)
        {
            string? name = action == QuickWearAction.MountFromCursor
                ? pendingMountName
                : problem.Cell >= 0 && problem.Cell < Loadout.CellCount ? Loadout.Cells[problem.Cell]?.Template.GetName() : null;

            return Lang.Get("lionfoxarmorquickwear:problem-" + problem.Code, name ?? "?");
        }

        public void Dispose()
        {
            tab?.Dispose();
        }
    }
}
