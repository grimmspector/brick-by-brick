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
using Vintagestory.API.Server;
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

        WorldInteraction[] interactions;
        //ProPickWorkSpace tws; //Fix later

        SkillItem[] toolModes;
        SkillItem[] modes;
        const int CapacityPerTier = 14;

        private static readonly string[] BrickSounds =
        {
            "brick-1",
            "brick-2",
            "brick-3",
            "brick-4"
        };

        private static readonly string[] TrowelSounds =
        {
            "trowel-1",
            "trowel-2",
            "trowel-3"
        };

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

        // -------------------------
        // INTERACTION ENTRY POINT
        // -------------------------

        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
            // -------------------------
            // EARLY EXIT CONDITIONS
            // -------------------------

            // Ensure this logic only runs once at the start of the interaction
            if (!firstEvent) return;
            if (firstEvent)
            {
                SetInteracted(slot.Itemstack, false);
            }
            byEntity.World.Api.Logger.Event("Trowel used!");
            byEntity.World.Api.Logger.Event("Item: " + slot.Itemstack.Collectible.Code);

            // Ensure we have a valid entity and world reference
            // (prevents null reference crashes when accessing world systems)
            if (byEntity?.World == null) return;

            // Ensure a block is actually being targeted
            if (blockSel == null) return;

            // Get the targeted block safely
            Block block = byEntity.World.BlockAccessor.GetBlock(blockSel.Position);
            byEntity.World.Api.Logger.Event("Block code: " + block.Code);
            byEntity.World.Api.Logger.Event("Block position: " + blockSel.Position);
            byEntity.World.Api.Logger.Event("Block face: " + blockSel.Face);
            byEntity.World.Api.Logger.Event("Block selection hit position: " + blockSel.HitPosition);
            byEntity.World.Api.Logger.Event("Block path:" + block.Code.Path);

            // Retrieve player (needed for tool mode)
            IPlayer player = (byEntity as EntityPlayer)?.Player;
            if (player == null) return;
            byEntity.World.Api.Logger.Event("Player: " + player?.PlayerName);

            int toolMode = GetToolMode(slot, player, blockSel);
            byEntity.World.Api.Logger.Event("Toolmode: " + toolMode);

            // Ensure the block is valid and can be interacted with by the trowel
            if (!IsTrowelable(block)) 
            {
                // Default behavior: collect mortar from containers
                TryCollectFromContainer(byEntity.World, blockSel.Position, slot.Itemstack);
                handling = EnumHandHandling.PreventDefault;
                return; 
            }

            // -------------------------
            // TOOL MODE SWITCH
            // -------------------------

            switch (toolMode)
            {
                case 0:
                    // Prevent default interaction behavior (e.g. placing block)
                    handling = EnumHandHandling.PreventDefault;
                    return;

                case 1:
                    // Placeholder for future tool mode behavior
                    return;

                case 2:
                    // Placeholder for future tool mode behavior
                    return;

                case 3:
                    // Placeholder for future tool mode behavior
                    return;

                default:
                    return;
            }

            if (!firstEvent) return;

            if (blockSel == null) return;
            IWorldAccessor world = byEntity.World;

            if (TryCollectFromContainer(world, blockSel.Position, slot.Itemstack))
            {
                handling = EnumHandHandling.PreventDefault;
                return;
            }

            return;
        }
        public override bool OnHeldInteractStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel)
        {
            base.OnHeldInteractStep(secondsUsed, slot, byEntity, blockSel, entitySel);
            if (byEntity?.World == null || blockSel == null) return false;

            IPlayer player = (byEntity as EntityPlayer)?.Player;
            if (player == null) return false;

            BlockPos pos = blockSel.Position;
            Block block = byEntity.World.BlockAccessor.GetBlock(pos);
            if (block == null) return false;

            // If NOT a trowelable block, do not handle this interaction
            if (!IsTrowelable(block))
            {
                return false; // let default behavior (pickup) happen
            }

            int toolMode = GetToolMode(slot, player, blockSel);

            byEntity.World.Api.Logger.Event($"Trowel used continuously for {secondsUsed} seconds!");

            byEntity.World.Api.Logger.Event($"Tool mode: {toolMode}");

            switch (toolMode)
            {
                case 0:
                    byEntity.World.Api.Logger.Event($"Handling case: {toolMode}");
                    {
                        return HandleTrowelMode0(secondsUsed, slot, byEntity, player, block, pos);
                    }
                case 1:
                    // Placeholder for future functionality
                    return false;

                case 2:
                    // Placeholder for future functionality
                    return false;

                case 3:
                    // Placeholder for future functionality
                    return false;

                default:
                    return false;
            }
        }

        // -------------------------
        // MODE 0 LOGIC
        // -------------------------

        /// <summary>
        /// Handles the primary trowel behavior:
        /// advancing block stages using mortar if conditions are met.
        /// </summary>
        private bool HandleTrowelMode0(float secondsUsed, ItemSlot slot, EntityAgent byEntity, IPlayer player, Block block, BlockPos pos)
        {

            // Ensure the block is valid and can be interacted with by the trowel
            if (!IsTrowelable(block)) return false;
            
            // Prevent multiple triggers during same hold
            if (HasInteracted(slot.Itemstack)) return false;
            
            // Extract stage and color from block code
            int currentStage = GetBlockStage(block);
            int nextStage = currentStage + 1;
            string color = GetBlockColor(block);

            // DEBUG: inspect offhand state
            byEntity.World.Api.Logger.Event("Block color: " + color);
            byEntity.World.Api.Logger.Event("Offhand slot exists: " + (player.InventoryManager?.OffhandHotbarSlot != null));
            byEntity.World.Api.Logger.Event("Offhand item: " + player.InventoryManager?.OffhandHotbarSlot?.Itemstack?.Collectible?.Code);
            byEntity.World.Api.Logger.Event("Offhand stack size: " + player.InventoryManager?.OffhandHotbarSlot?.StackSize);

            if (secondsUsed < 1f) return true;
            byEntity.World.Api.Logger.Event($"Color: {color}");
            byEntity.World.Api.Logger.Event($"Stage: {currentStage} -> {nextStage}");

            // Validate player conditions
            if (!HasMatchingMaterial(player, color, byEntity)) return false;
            if (!HasEnoughMortar(slot, byEntity)) return false;

            // -------------------------
            // BRICK CONSUMPTION LOGIC
            // -------------------------

            // At stages 3, 5, and 7, consume one matching brick from offhand, enforcing offhand usage
            if (nextStage == 3 || nextStage == 5 || nextStage == 7)
            {
                string requiredPath = $"burnedbrick-{color}";

                if (!TryGetOffhandStack(player, out ItemSlot offhandSlot, out ItemStack offhandStack))
                {
                    byEntity.World.Api.Logger.Event("No item in offhand!");
                    return false;
                }

                if (offhandStack.Collectible?.Code?.Path != requiredPath)
                {
                    byEntity.World.Api.Logger.Event($"Color mismatch! Required: {requiredPath}, Found: {offhandStack.Collectible?.Code?.Path}");
                    return false;
                }

                // Consume brick
                offhandSlot.TakeOut(1);
                offhandSlot.MarkDirty();

                //byEntity.World.Api.Logger.Event("Consumed 1 brick from offhand");
            }

            // -------------------------
            // BLOCK TRANSFORMATION
            // -------------------------

            // Determine next block
            AssetLocation newPath = ResolveNextBlock(block, nextStage, color);
            if (newPath == null)
            {
                //byEntity.World.Api.Logger.Warning("newPath is null, skipping");
                return false;
            }

            Block newBlock = byEntity.World.BlockAccessor.GetBlock(newPath);
            if (newBlock == null)
            {
                //byEntity.World.Api.Logger.Warning($"Block not found: {newPath}");
                return false;
            }

            // Apply transformation
            byEntity.World.BlockAccessor.ExchangeBlock(newBlock.Id, pos);

            // -------------------------
            // SOUND EFFECTS
            // -------------------------

            if (byEntity.World.Side == EnumAppSide.Server)
            {

                // Brick placement sounds (stages 2, 4, 6)
                if (nextStage == 2 || nextStage == 4 || nextStage == 6)
                {
                    PlayRandomSound(byEntity.World, pos, player, BrickSounds, 20f);
                }

                // Trowel action sounds (stages 3, 5, 7)
                if (nextStage == 3 || nextStage == 5 || nextStage == 7)
                {
                    PlayRandomSound(byEntity.World, pos, player, TrowelSounds, 12f);
                }
            }

            // -------------------------
            // SOUND SYNC (SERVER → CLIENTS)
            // -------------------------

            //if (byEntity.World.Side == EnumAppSide.Server)
            //{
            //    int soundType = -1;

            //    if (nextStage == 2 || nextStage == 4 || nextStage == 6)
            //    {
            //        soundType = 0; // brick
            //    }
            //    else if (nextStage == 3 || nextStage == 5 || nextStage == 7)
            //    {
            //        soundType = 1; // trowel
            //    }

            //    if (soundType != -1)
            //    {
            //        var packet = new TrowelSoundPacket()
            //        {
            //            X = pos.X,
            //            Y = pos.Y,
            //            Z = pos.Z,
            //            SoundType = soundType
            //        };

            //        // Send to all nearby players (efficient + correct)
            //        (byEntity.World.Api as ICoreServerAPI)?
            //            .Network
            //            .GetChannel("trowelsound")
            //            .BroadcastPacket(packet);
            //    }
            //}

            // -------------------------
            // MORTAR CONSUMPTION (ALWAYS)
            // -------------------------

            SetStoredAmount(slot.Itemstack, GetStoredAmount(slot.Itemstack) - 1);

            // Mark that we've interacted to prevent repeat triggers
            SetInteracted(slot.Itemstack, true);

            return true;
        }

        /// <summary>
        /// Attempts to find and optionally consume items from the player's offhand
        /// that match a given condition.
        /// </summary>
        /// <param name="player">The player</param>
        /// <param name="matcher">Function to match valid items</param>
        /// <param name="quantity">Amount to consume (default 1)</param>
        /// <param name="consume">Whether to actually remove the item</param>
        /// <param name="slot">Returned slot if successful</param>
        /// <param name="stack">Returned stack if successful</param>
        /// <returns>True if a matching item exists (and was consumed if requested)</returns>
        private bool TryConsumeFromOffhand(
            IPlayer player,
            System.Func<ItemStack, bool> matcher,
            int quantity,
            bool consume,
            out ItemSlot slot,
            out ItemStack stack)
        {
            slot = null;
            stack = null;

            var inv = player?.InventoryManager?.GetOwnInventory("offhand");

            if (inv == null || inv.Count == 0) return false;

            ItemSlot candidateSlot = inv[0];
            ItemStack candidateStack = candidateSlot?.Itemstack;

            if (candidateStack == null) return false;

            if (!matcher(candidateStack)) return false;

            // Ensure enough quantity if consuming
            if (consume && candidateSlot.StackSize < quantity) return false;

            // Perform consumption
            if (consume)
            {
                candidateSlot.TakeOut(quantity);
                candidateSlot.MarkDirty();
            }

            slot = candidateSlot;
            stack = candidateStack;

            return true;
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
            if (stack?.Attributes == null) return;

            int current = GetStoredAmount(stack);
            int max = GetMaxCapacity(stack);

            if (max <= 0)
            {
                stack.Attributes.SetInt("durability", 1);
                return;
            }

            int jsonMax = stack.Collectible.GetMaxDurability(stack);

            // Clamp current safely
            current = GameMath.Clamp(current, 0, max);

            // Ratio (0 → 1)
            float ratio = (float)current / max;

            // Convert directly (no segmentation for now—debug first)
            int durability = (int)(ratio * jsonMax);

            // --- CRITICAL FIXES ---

            // Prevent 0 (empty red/black bar bug)
            if (durability <= 0)
            {
                durability = 1;
            }

            // Prevent full (bar disappears)
            if (durability >= jsonMax)
            {
                durability = jsonMax - 1;
            }

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
        private static int AddLiquid(ItemStack stack, int amountToAdd)
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
        private bool IsValidLiquid(ItemStack stack)
        {
            if (stack == null) return false;

            return stack?.Collectible?.Code?.Path == "liquidmortarportion";
        }


        // ------------------------
        // CONTAINER INTERACTION
        // ------------------------

        /// <summary>
        /// Attempts to pull mortar liquid from a block at the given position
        /// Only works with containers holding "liquidmortarportion"
        /// </summary>
        private bool TryCollectFromContainer(IWorldAccessor world, BlockPos pos, ItemStack toolStack)
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

            // -------------------------
            // EMPTY TOOLTIP HINT
            // -------------------------

            if (stored == 0)
            {
                dsc.AppendLine();
                dsc.AppendLine("Right-click a bucket to fill");
            }
        }

        // -------------------------
        // VALIDATION HELPERS
        // -------------------------

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

        /// <summary>
        /// Plays a random sound from the given list at the entity's position, with a variation in pitch and volume for natural effect. Only runs on client side.
        /// </summary>
        private void PlayRandomSound(IWorldAccessor world, BlockPos pos, IPlayer player, string[] sounds, float range)
        {
            if (player == null || world == null ||  sounds == null || sounds.Length == 0) return;
 
            var rand = world.Rand;

            int index = rand.Next(sounds.Length);

            //var sound = new AssetLocation("brickbybrick", $"{sounds[index]}");
            var sound = new AssetLocation("game", "block/ceramicplace");
            world.Api.Logger.Event($"Playing sound: {sound} at position {pos} with range {range}");

            // Subtle variation
            float pitch = 0.95f + (float)rand.NextDouble() * 0.1f;  // pitch 0.95 to 1.05
            float volume = 0.9f + (float)rand.NextDouble() * 0.2f;  // volume 0.9 to 1.1

            volume = 1f;
            range = 32f;

            world.PlaySoundAt(
                sound,
                pos.X + 0.5,    // center of block
                pos.Y + 0.5,
                pos.Z + 0.5,
                player,
                volume,
                pitch,
                range
            );
        }

        /// <summary>
        /// Attempts to retrieve the player's offhand slot and stack safely.
        /// Uses OffhandHotbarSlot (correct API usage).
        /// </summary>
        private bool TryGetOffhandStack(IPlayer player, out ItemSlot slot, out ItemStack stack)
        {
            slot = player?.InventoryManager?.OffhandHotbarSlot;
            stack = slot?.Itemstack;

            return stack != null;
        }

        /// <summary>
        /// Returns true if the block can be modified by the trowel.
        /// </summary>
        private bool IsTrowelable(Block block)
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
        /// <summary>
        /// Extracts the numeric stage from the block code.
        /// </summary>
        private int GetBlockStage(Block block)
        {
            if (block == null) return 0;

            int stage;
            return int.TryParse(block.LastCodePart(0), out stage) ? stage : 0;
        }

        /// <summary>
        /// Extracts the color variant from the block code.
        /// </summary>
        private string GetBlockColor(Block block)
        {
            return block?.LastCodePart(1);
        }

        /// <summary>
        /// Attempts to extract a "color" identifier from an itemstack.
        /// Supports both path-based and variant-based items.
        /// </summary>
        private string GetItemColor(ItemStack stack)
        {
            if (stack?.Collectible?.Code == null) return null;

            // --- Variant-based (preferred) ---
            if (stack.Collectible.Variant != null && stack.Collectible.Variant.ContainsKey("color"))
            {
                return stack.Collectible.Variant["color"];
            }

            // --- Fallback: path-based ---
            string path = stack.Collectible.Code.Path;

            if (string.IsNullOrEmpty(path)) return null;

            string[] parts = path.Split('-');

            return parts.Length > 1 ? parts[parts.Length - 1] : null;
        }

        /// <summary>
        /// Ensures the player is holding the correct material in the off-hand.
        /// </summary>
        private bool HasMatchingMaterial(IPlayer player, string color, EntityAgent byEntity)
        {
            string requiredPath = $"burnedbrick-{color}";

            bool success = TryGetOffhandStack(player, out _, out ItemStack stack)
                           && stack.Collectible?.Code?.Path == requiredPath;

            if (!success)
            {
                string held = stack?.Collectible?.Code?.Path;
                byEntity.World.Api.Logger.Event($"Color mismatch! Required: {requiredPath}, Found: {held}");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Ensures the tool has enough stored mortar to perform the action.
        /// </summary>
        private bool HasEnoughMortar(ItemSlot slot, EntityAgent byEntity)
        {
            if (GetStoredAmount(slot.Itemstack) <= 0)
            {
                byEntity.World.Api.Logger.Event("Not enough mortar in trowel!");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Checks to see if the interaction is complete
        /// </summary>
        private bool HasInteracted(ItemStack stack)
        {
            return stack.Attributes.GetBool("didInteract", false);
        }

        /// <summary>
        /// Sets the interaction based on the caller, used to prevent multiple calls to the same interaction logic within one interaction session.
        /// </summary>
        private void SetInteracted(ItemStack stack, bool value)
        {
            stack.Attributes.SetBool("didInteract", value);
        }


        // -------------------------
        // BLOCK RESOLUTION
        // -------------------------

        /// <summary>
        /// Determines the next block state based on stage progression.
        /// </summary>
        private AssetLocation ResolveNextBlock(Block block, int nextStage, string color)
        {
            if (block == null) return null;

            if (nextStage <= 6)
            {
                return block.CodeWithParts(nextStage.ToString());
            }

            if (nextStage == 7)
            {
                if (color == "fire")
                {
                    return new AssetLocation("game:claybricks-good-fire");
                }

                return block.CodeWithoutParts(1);
            }

            return null;
        }
    }
}
