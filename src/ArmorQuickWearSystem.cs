using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace lionfox_ArmorQuickWear
{
    // Universal mod. The server owns every player's loadout and performs every item move; the
    // client draws the Quick Wear tab, forwards clicks and hotkeys, and reports what went wrong.
    public class ArmorQuickWearSystem : ModSystem
    {
        public const string ChannelName = "lionfoxarmorquickwear";

        LoadoutServer? server;
        LoadoutClient? client;

        public override void Start(ICoreAPI api)
        {
            api.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<ActionPacket>()
                .RegisterMessageType<LoadoutPacket>()
                .RegisterMessageType<ResultPacket>();
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            server = new LoadoutServer(api, api.Network.GetChannel(ChannelName), Mod.Logger);
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            client = new LoadoutClient(api, api.Network.GetChannel(ChannelName), Mod.Logger);
        }

        public override void Dispose()
        {
            client?.Dispose();
            client = null;
            server = null;
        }
    }
}
