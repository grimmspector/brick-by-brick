using brickbybrick.Blocks;
using brickbybrick.items;
using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using static brickbybrick.items.ItemTrowel;
using ProtoBuf;

namespace brickbybrick
{

    //[ProtoContract]
    //public class TrowelSoundPacket
    //{
    //    [ProtoMember(1)] public int X;
    //    [ProtoMember(2)] public int Y;
    //    [ProtoMember(3)] public int Z;
    //    [ProtoMember(4)] public int SoundType;
    //}

    public class brickbybrickModSystem : ModSystem
    {
        //private ICoreClientAPI capi;   // ✔ store client API
        //private IServerNetworkChannel serverChannel;
        //private IClientNetworkChannel clientChannel;

        // Called on server and client
        // Useful for registering block/entity classes on both sides
        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            Mod.Logger.Notification("Hello from template mod: " + api.Side);
            api.RegisterBlockClass(Mod.Info.ModID + ".trampoline", typeof(BlockTrampoline));
            api.RegisterItemClass(Mod.Info.ModID + ".thornsblade", typeof(ItemThornsBlade));
            api.RegisterItemClass(Mod.Info.ModID + ".trowel", typeof(ItemTrowel));
            api.RegisterBlockClass(Mod.Info.ModID + ".cobbleblock", typeof(BlockStone));
            api.RegisterBlockClass(Mod.Info.ModID + ".brickblock", typeof(BlockBrick));

            // Register network channels
            //var channel = api.Network.RegisterChannel("trowelsound")
            //    .RegisterMessageType<TrowelSoundPacket>();

            //if (api.Side == EnumAppSide.Server)
            //{
            //    serverChannel = channel as IServerNetworkChannel;
            //}

            //if (api.Side == EnumAppSide.Client)
            //{
            //    capi = api as ICoreClientAPI;   // ✔ FIX

            //    clientChannel = channel as IClientNetworkChannel;
            //    clientChannel.SetMessageHandler<TrowelSoundPacket>(OnSoundPacket);
            //}
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            Mod.Logger.Notification("Hello from template mod server side: " + Lang.Get("brick-by-brick:hello"));
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            Mod.Logger.Notification("Hello from template mod client side: " + Lang.Get("brick-by-brick:hello"));
        }

        /// <summary>
        /// Client-side: plays the sound when packet is received
        /// </summary>
        //private void OnSoundPacket(TrowelSoundPacket packet)
        //{
        //    var world = capi.World;   // ✔ FIX

        //    BlockPos pos = new BlockPos(packet.X, packet.Y, packet.Z);

        //    string[] sounds = packet.SoundType == 0
        //        ? new[] { "brick-1", "brick-2", "brick-3", "brick-4" }
        //        : new[] { "trowel-1", "trowel-2", "trowel-3" };

        //    var rand = world.Rand;
        //    string chosen = sounds[rand.Next(sounds.Length)];

        //    var sound = new AssetLocation("brickbybrick", $"{chosen}");
        //    world.Api.Logger.Event($"Playing sound: {sound} at position {pos}");

        //    float pitch = 0.95f + (float)rand.NextDouble() * 0.1f;
        //    float volume = 0.9f + (float)rand.NextDouble() * 0.2f;

        //    world.PlaySoundAt(
        //        sound,
        //        pos.X + 0.5,
        //        pos.Y + 0.5,
        //        pos.Z + 0.5,
        //        null,
        //        volume,
        //        pitch,
        //        packet.SoundType == 0 ? 20f : 12f
        //    );
        //}
    }
}
