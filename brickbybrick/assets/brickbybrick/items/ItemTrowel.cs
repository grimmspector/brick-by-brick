using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

#nullable disable

namespace brickbybrick.items
{
    internal class ItemTrowel : Item
    {
        //WorldInteraction[]? interactions;
        WorldInteraction[] interactions;
        //ProPickWorkSpace tws; //Fix later
        //SkillItem[]? toolModes;
        SkillItem[] toolModes;
        SkillItem[] modes;

        public override void OnLoaded(ICoreAPI api)
        {
            if (api is not ICoreClientAPI capi) return;
            base.OnLoaded(api);

            toolModes = ObjectCacheUtil.GetOrCreate(api, "trowelToolModes", () =>
            {
                modes = new SkillItem[4];
                modes[0] = new SkillItem() { Code = new AssetLocation("build"), Name = Lang.Get("Build mode for building up any stone/brick block") };
                modes[1] = new SkillItem() { Code = new AssetLocation("slab"), Name = Lang.Get("Slab placement mode for any stone/brick material") };
                modes[2] = new SkillItem() { Code = new AssetLocation("stair"), Name = Lang.Get("Stair placement mode for any stone/brick material") };
                modes[3] = new SkillItem() { Code = new AssetLocation("block"), Name = Lang.Get("Block placement mode for any stone/brick material") };

                if (capi != null)
                {
                    modes[0].WithIcon(capi, capi.Gui.LoadSvgWithPadding(new AssetLocation("textures/icons/heatmap.svg"), 48, 48, 5, ColorUtil.WhiteArgb));
                    modes[0].TexturePremultipliedAlpha = false;
                }
                if (modes.Length > 1)
                {
                    modes[1].WithIcon(capi, capi.Gui.LoadSvgWithPadding(new AssetLocation("textures/icons/brick.svg"), 48, 48, 5, ColorUtil.WhiteArgb));
                    modes[1].TexturePremultipliedAlpha = false;
                }
                if (modes.Length > 2)
                {
                    modes[2].WithIcon(capi, capi.Gui.LoadSvgWithPadding(new AssetLocation("textures/icons/brick-stair.svg"), 48, 48, 5, ColorUtil.WhiteArgb));
                    modes[2].TexturePremultipliedAlpha = false;
                }
                if (modes.Length > 3)
                {
                    modes[3].WithIcon(capi, capi.Gui.LoadSvgWithPadding(new AssetLocation("textures/icons/brick-block.svg"), 48, 48, 5, ColorUtil.WhiteArgb));
                    modes[3].TexturePremultipliedAlpha = false;
                }
                return modes;
            });

            interactions = ObjectCacheUtil.GetOrCreate(capi, "trowelInteractions", () =>
            {
                List<ItemStack> stacks = new List<ItemStack>();

                foreach (Block block in capi.World.Blocks)
                {
                    if (block.Code == null) continue;

                    if (block.Code.PathStartsWith("soil"))
                    {
                        stacks.Add(new ItemStack(block));
                    }
                }

                return new WorldInteraction[]
                {
                    new WorldInteraction()
                    {
                        ActionLangCode = "heldhelp-till",
                        MouseButton = EnumMouseButton.Right,
                        Itemstacks = stacks.ToArray()
                    }
                };
            });
        }

        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent,ref handling);

            byEntity.World.Api.Logger.Event("Trowel used!");
            if (blockSel == null) return;
            var player = (byEntity as EntityPlayer)?.Player;

     if (!byEntity.World.Claims.TryAccess(player, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak))
           {
                var block = api.World.BlockAccessor.GetBlock(blockSel.Position);
                byEntity.World.Api.Logger.Event("Block position: " + blockSel.Position);
                byEntity.World.Api.Logger.Event("Block code: " + block.Code);
                byEntity.World.Api.Logger.Event("Block face: " + blockSel.Face);
                byEntity.World.Api.Logger.Event("Block selection hit position: " + blockSel.HitPosition);
                return;
           }
            byEntity.World.Api.Logger.Event("Entity: " + byEntity.Code);
            handling = EnumHandHandling.PreventDefault;
            return;
        }
        public override bool OnHeldInteractStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel)
        {
            base.OnHeldInteractStep(secondsUsed, slot, byEntity, blockSel, entitySel);
            byEntity.World.Api.Logger.Event("Trowel used continuously for " + secondsUsed + " seconds!");
            if (secondsUsed > 1)
            {
                byEntity.World.Api.Logger.Event("Trowel used for more than 1 second!");
                byEntity.World.Api.Logger.Event("Seconds used: " + secondsUsed);
                byEntity.World.Api.Logger.Event("Block selected: " + blockSel.Block.Code);
                byEntity.World.Api.Logger.Event("Block position: " + blockSel.Position);
                byEntity.World.Api.Logger.Event("Block face: " + blockSel.Face);
                byEntity.World.Api.Logger.Event("Block selection hit position: " + blockSel.HitPosition);
                byEntity.World.Api.Logger.Event("Entity: " + byEntity.Code);
                return false;
            }
            return true;
        }
    }
}
