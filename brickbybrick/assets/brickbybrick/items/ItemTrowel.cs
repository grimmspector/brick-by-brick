using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
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
        /* ------------------------
                    TO-DO
            + Rerwite with language file entries for all applicable strings using Lang.Get("key") and add said keys to en-us.json
            + Refactor main sections of code
            + Add sound effects
            + Add particle effects
            + Add proper client-side interaction help (currently just a placeholder)
            + Add comments to all methods and major code pieces
            + Cull unnecessary code sections, and optimize where possible
            + See if we can safely remove the #nullable disable at the top after refactor
           ------------------------ */
        //WorldInteraction[]? interactions;
        WorldInteraction[] interactions;
        //ProPickWorkSpace tws; //Fix later
        //SkillItem[]? toolModes;
        SkillItem[] toolModes;
        SkillItem[] modes;
        const int CapacityPerTier = 14;

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
                List<ItemStack> stacks = new List<ItemStack>();

                foreach (Block block in capi.World.Blocks)
                {
                    if (block.Code == null) continue;

                    if (block.Code.PathStartsWith("brickbybrick:brickblock") || block.Code.PathStartsWith("cobbleblock"))
                    {
                        stacks.Add(new ItemStack(block));
                    }
                }

                return new WorldInteraction[]
                {
                    new WorldInteraction()
                    {
                        ActionLangCode = "heldhelp-trowel",
                        MouseButton = EnumMouseButton.Right,
                        Itemstacks = stacks.ToArray()
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
            base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);

            if (!firstEvent) return;
            byEntity.World.Api.Logger.Event("Trowel used!");
            if (blockSel == null) return;
            IWorldAccessor world = byEntity.World;

            if (TryCollectFromContainer(world, blockSel.Position, slot.Itemstack)) { 
                handling = EnumHandHandling.PreventDefault;
                return;
            }

            

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
            byEntity.World.Api.Logger.Event("Block path:" + block.Code.Path);
            byEntity.World.Api.Logger.Event("Player: " + player?.PlayerName);
            byEntity.World.Api.Logger.Event("Item: " + slot.Itemstack.Collectible.Code);
            byEntity.World.Api.Logger.Event("Toolmode: " + toolMode);
            if (toolMode == 0)
            {
                //return;
                //}
                byEntity.World.Api.Logger.Event("Entity: " + byEntity.Code);
                handling = EnumHandHandling.PreventDefault;
            }
            return;
        }
        public override bool OnHeldInteractStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel)
        {
            base.OnHeldInteractStep(secondsUsed, slot, byEntity, blockSel, entitySel);
            byEntity.World.Api.Logger.Event("Trowel used continuously for " + secondsUsed + " seconds!");
            BlockPos pos = blockSel.Position;
            Block block = api.World.BlockAccessor.GetBlock(blockSel.Position);
            var player = (byEntity as EntityPlayer)?.Player;
            //string[] parts = block.Code.Path.Split('-');
            //int stage = Convert.ToInt32(parts[parts.Length - 1]);
            if (!isTrowelable(block)) return false;
            //pull off the last chunk of our variant/part/whatever
            //this assumes the end of the path is a number you can do math with, not a word. adjust accordingly
            int lastPart;
            int.TryParse(block.LastCodePart(0), out lastPart);
            //you can put whatever or do whatever math here you want
            int newPart = lastPart + 1;
            string color = block.LastCodePart(3);
            byEntity.World.Api.Logger.Event("Color: " + color);
            byEntity.World.Api.Logger.Event("lastPart, newPart: " + lastPart + ", " + newPart);
            if (newPart <= 6 && secondsUsed >= 1) { 
                if (CheckLeftHand(player)?.Collectible.LastCodePart() != color || GetToolMode(slot, (byEntity as EntityPlayer).Player, blockSel) != 0) {
                    byEntity.World.Api.Logger.Event("Color mismatch! Block, Hand: " + color + ", " + CheckLeftHand(player)?.Collectible.LastCodePart());
                    return false; }
                byEntity.World.Api.Logger.Event("Left hand item: " + CheckLeftHand(player)?.Collectible.Code);
            //this should take the block's existing path, cut off the last part, and stitch the new part on
                AssetLocation newPath = block.CodeWithParts(newPart.ToString());
                byEntity.World.Api.Logger.Event("newPath: " + newPath);
            //here we ask the game to find a block corresponding to the new path and retrieve its numeric ID
            int newBlockId = api.World.BlockAccessor.GetBlock(newPath).Id;
            //tell the game to swap our existing block with the new one
                api.World.BlockAccessor.ExchangeBlock(newBlockId, pos);
                return false;
            } 
            else { return true; }

            //byEntity.World.Api.Logger.Event("Block code without parts: " + prevBlock);
            //if (secondsUsed > 1)
            //{
            //    if (!block.Code.PathStartsWith("brickblock")) return;
            //    IPlayer? byPlayer = (byEntity as EntityPlayer)?.Player;
            //    Block? masonry = byEntity.World.GetBlock(new AssetLocation(""));

            //    byEntity.World.BlockAccessor.MarkBlockDirty(pos);
            //    byEntity.World.Api.Logger.Event("Trowel used for more than 1 second!");
            //    byEntity.World.Api.Logger.Event("Seconds used: " + secondsUsed);
            //    byEntity.World.Api.Logger.Event("Block selected: " + blockSel.Block.Code);
            //    byEntity.World.Api.Logger.Event("Block position: " + blockSel.Position);
            //    byEntity.World.Api.Logger.Event("Block face: " + blockSel.Face);
            //    byEntity.World.Api.Logger.Event("Block selection hit position: " + blockSel.HitPosition);
            //    byEntity.World.Api.Logger.Event("Entity: " + byEntity.Code);
            //    return false;

            return true;
        }
        public ItemStack CheckLeftHand(IPlayer player)
        {
            // Access the inventory manager
            IPlayerInventoryManager invManager = player?.InventoryManager;

            // Get the itemstack in the left hand (off-hand)
            //ItemStack leftHandStack = invManager?.GetHotbarItemstack(14);
            ItemStack leftHandStack = invManager.OffhandHotbarSlot?.Itemstack;
            //InventoryManager.ActiveHotbarSlot;

            // Check if the hand is not empty
            if (leftHandStack != null) return leftHandStack;
            return null;
        }

        // ------------------------
        // CAPACITY
        // ------------------------

        /// <summary>
        /// Returns max number of "liquidmortarportion" items this tool can hold
        /// </summary>
        public static int GetMaxCapacity(ItemStack stack)
        {
            int toolTier = stack.Collectible.ToolTier;
            return toolTier * CapacityPerTier;
        }

        /// <summary>
        /// Gets how many portions are currently stored in the item
        /// </summary>
        public static int GetStoredAmount(ItemStack stack)
        {
            return stack.Attributes.GetInt("mortarAmount", 0);
        }

        // Set stored amount of mortar, ensuring it doesn't exceed max capacity
        public static void SetStoredAmount(ItemStack stack, int value)
        {
            stack.Attributes.SetInt("mortarAmount", value);
            SyncDurability(stack);
        }

        // Return max "capacity"
        public override int GetMaxDurability(ItemStack itemstack)
        {
            return GetMaxCapacity(itemstack);
        }

        // ------------------------
        // DURABILITY SYNC (CRITICAL)
        // ------------------------

        public static void SyncDurability(ItemStack stack)
        {
            int current = GetStoredAmount(stack);
            int max = GetMaxCapacity(stack);

            // No capacity = no durability
            if (max <= 0)
            {
                stack.Attributes.SetInt("durability", 0);
                return;
            }

            // Engine-defined max durability (from JSON)
            int jsonMaxDurability = stack.Collectible.GetMaxDurability(stack);

            // --- SEGMENT LOGIC ---
            // Use near 1:1 unless it exceeds visual resolution
            int segments = max <= 60 ? max : 60;

            float fillRatio = (float)current / max;

            // Snap to segment step
            int steppedSegment = (int)(fillRatio * segments);
            float steppedRatio = (float)steppedSegment / segments;

            // Convert to durability scale
            int durability = (int)(steppedRatio * jsonMaxDurability);

            // --- SAFE CLAMP ---
            durability = GameMath.Clamp(durability, 0, jsonMaxDurability);

            // Apply to item
            stack.Attributes.SetInt("durability", durability);
        }

        // ------------------------
        // LIQUID HANDLING
        // ------------------------

        /// <summary>
        /// Attempts to add mortar portions into the item
        /// Returns how many were actually added
        /// </summary>
        public static int AddLiquid(ItemStack stack, int amountToAdd)
        {
            int current = GetStoredAmount(stack);
            int max = GetMaxCapacity(stack);

            int spaceLeft = max - current;
            int toAdd = Math.Min(spaceLeft, amountToAdd);

            stack.Attributes.SetInt("mortarAmount", current + toAdd);

            return toAdd;
        }

        /// <summary>
        /// Ensures we ONLY accept "liquidmortarportion"
        /// </summary>
        public bool IsValidLiquid(ItemStack stack)
        {
            if (stack == null) return false;

            return stack.Collectible.Code.Path == "liquidmortarportion";
        }


        // ------------------------
        // CONTAINER INTERACTION
        // ------------------------

        /// <summary>
        /// Attempts to pull mortar liquid from a block at the given position
        /// Only works with containers holding "liquidmortarportion"
        /// </summary>
        public bool TryCollectFromContainer(IWorldAccessor world, BlockPos pos, ItemStack toolStack)
        {
            BlockEntity be = world.BlockAccessor.GetBlockEntity(pos);

            if (be == null) return false;

            // Try to access inventory (works for barrels, buckets, etc.)
            var invProvider = be as IBlockEntityContainer;
            if (invProvider == null) return false;

            var inv = invProvider.Inventory;

            for (int i = 0; i < inv.Count; i++)
            {
                ItemSlot slot = inv[i];
                ItemStack content = slot.Itemstack;

                if (!IsValidLiquid(content)) continue;

                int available = content.StackSize;

                // Try to add into our tool
                int moved = AddLiquid(toolStack, available);

                if (moved > 0)
                {
                    slot.TakeOut(moved);
                    slot.MarkDirty();

                    // Stop once full
                    if (GetStoredAmount(toolStack) >= GetMaxCapacity(toolStack))
                    {
                        return true;
                    }
                }
            }
            return true;
        }

        // ------------------------
        // TOOLTIP
        // ------------------------

        // Add tooltip to show stored mortar
        public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
        {
            base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
            
            // Remove durability line
            string durabilityText = Lang.Get("Durability");

            var lines = dsc.ToString().Split('\n');
            dsc.Clear();

            foreach (var line in lines)
            {
                if (!line.Contains(durabilityText))
                {
                    dsc.AppendLine(line);
                }
            }

            int stored = GetStoredAmount(inSlot.Itemstack);
            int max = GetMaxCapacity(inSlot.Itemstack);

            dsc.AppendLine($"Mortar: {stored} / {max}");
        }
    }
}
