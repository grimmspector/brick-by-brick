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
            //if (api is not ICoreClientAPI capi) return;
            //ICoreClientAPI capi = api as ICoreClientAPI;
            base.OnLoaded(api);

            var capi = api as ICoreClientAPI;
            toolModes = ObjectCacheUtil.GetOrCreate<SkillItem[]>(api, "trowelToolModes", () => [
                new SkillItem {
                    Code = new AssetLocation("build"),
                    Name = Lang.Get("Build mode for building up any stone/brick block"),
                    Texture = capi?.Gui.LoadSvgWithPadding(new AssetLocation("brickbybrick:textures/icons/trowel.svg"), 64, 64, 5, ColorUtil.WhiteArgb)
                },
                new SkillItem()
                {
                    Code = new AssetLocation("slab"),
                    Name = Lang.Get("Slab placement mode for any stone/brick material"),
                    Texture = capi?.Gui.LoadSvgWithPadding(new AssetLocation("brickbybrick:textures/icons/brick-block.svg"), 64, 64, 5, ColorUtil.WhiteArgb)
                },
                new SkillItem()
                {
                    Code = new AssetLocation("stair"),
                    Name = Lang.Get("Stair placement mode for any stone/brick material"),
                    Texture = capi?.Gui.LoadSvgWithPadding(new AssetLocation("brickbybrick:textures/icons/brick-stair.svg"), 64, 64, 5, ColorUtil.WhiteArgb)
                },
                new SkillItem()
                {
                    Code = new AssetLocation("block"),
                    Name = Lang.Get("Block placement mode for any stone/brick material"),
                    Texture = capi?.Gui.LoadSvgWithPadding(new AssetLocation("brickbybrick:textures/icons/brick-slab.svg"), 64, 64, 5, ColorUtil.WhiteArgb)
                }
            ]);
            if (capi == null) return;
            interactions = ObjectCacheUtil.GetOrCreate(capi, "trowelInteractions", () =>
            {
                //List<ItemStack> stacks = new List<ItemStack>();

                //foreach (Block block in capi.World.Blocks)
                //{
                //    if (block.Code == null) continue;

                //    if (block.Code.PathStartsWith("soil"))
                //    {
                //        stacks.Add(new ItemStack(block));
                //    }
                //}

                return new WorldInteraction[]
                {
                    new WorldInteraction()
                    {
                        ActionLangCode = "heldhelp-trowel",
                        MouseButton = EnumMouseButton.Right,
                        //Itemstacks = stacks.ToArray()
                    }
                };
            });
        }

        public override SkillItem[] GetToolModes(ItemSlot slot, IClientPlayer forPlayer, BlockSelection blockSel)
        {
            return toolModes;
        }
        public override int GetToolMode(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel)
        {
            return Math.Min(toolModes.Length - 1, slot.Itemstack.Attributes.GetInt("toolMode"));
        }

        public override void SetToolMode(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel, int toolMode)
        {
            slot.Itemstack.Attributes.SetInt("toolMode", toolMode);
        }
        private bool isTrowelable(Block block)
        {
            return block?.Attributes?["trowelable"].AsBool(false) == true;
        }
        public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot)
        {
            base.GetHeldInteractionHelp(inSlot);
            return new WorldInteraction[] {
                new WorldInteraction()
                {
                    ActionLangCode = "Change tool mode",
                    HotKeyCodes = new string[] { "toolmodeselect" },
                    MouseButton = EnumMouseButton.None
                }
            };

        }

        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent,ref handling);

            byEntity.World.Api.Logger.Event("Trowel used!");
            if (blockSel == null) return;
            var player = (byEntity as EntityPlayer)?.Player;
            var block = api.World.BlockAccessor.GetBlock(blockSel.Position);
            byEntity.World.Api.Logger.Event("Block code: " + block.Code);
            if (!isTrowelable(block)) return;
            //if (!byEntity.World.Claims.TryAccess(player, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak))
            //      {
            int toolMode = GetToolMode(slot, (byEntity as EntityPlayer).Player, blockSel);
            byEntity.World.Api.Logger.Event("Block position: " + blockSel.Position);
            byEntity.World.Api.Logger.Event("Block face: " + blockSel.Face);
            byEntity.World.Api.Logger.Event("Block selection hit position: " + blockSel.HitPosition);
            byEntity.World.Api.Logger.Event("Player: " + player?.PlayerName);
            byEntity.World.Api.Logger.Event("Item: " + slot.Itemstack.Collectible.Code);
            byEntity.World.Api.Logger.Event("Toolmode: " + toolMode);

            //return;
            //}
            byEntity.World.Api.Logger.Event("Entity: " + byEntity.Code);
            handling = EnumHandHandling.PreventDefault;
            return;
        }
        public override bool OnHeldInteractStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel)
        {
            base.OnHeldInteractStep(secondsUsed, slot, byEntity, blockSel, entitySel);
            byEntity.World.Api.Logger.Event("Trowel used continuously for " + secondsUsed + " seconds!");
            //if (secondsUsed > 1)
            //{
            //    byEntity.World.Api.Logger.Event("Trowel used for more than 1 second!");
            //    byEntity.World.Api.Logger.Event("Seconds used: " + secondsUsed);
            //    byEntity.World.Api.Logger.Event("Block selected: " + blockSel.Block.Code);
            //    byEntity.World.Api.Logger.Event("Block position: " + blockSel.Position);
            //    byEntity.World.Api.Logger.Event("Block face: " + blockSel.Face);
            //    byEntity.World.Api.Logger.Event("Block selection hit position: " + blockSel.HitPosition);
            //    byEntity.World.Api.Logger.Event("Entity: " + byEntity.Code);
            //    return false;
            //}
            return true;
        }
    }
}
