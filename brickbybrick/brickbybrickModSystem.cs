using brickbybrick.Blocks;
using brickbybrick.items;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace brickbybrick
{
    public class brickbybrickModSystem : ModSystem
    {
        // Called on server and client
        // Useful for registering block/entity classes on both sides
        public override void Start(ICoreAPI api)
        {
            Mod.Logger.Notification("Hello from template mod: " + api.Side);
            api.RegisterBlockClass(Mod.Info.ModID + ".trampoline", typeof(BlockTrampoline));
            api.RegisterItemClass(Mod.Info.ModID + ".thornsblade", typeof(ItemThornsBlade));
            api.RegisterItemClass(Mod.Info.ModID + ".trowel", typeof(ItemTrowel));
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            Mod.Logger.Notification("Hello from template mod server side: " + Lang.Get("brick-by-brick:hello"));
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            Mod.Logger.Notification("Hello from template mod client side: " + Lang.Get("brick-by-brick:hello"));
        }
    }
}
