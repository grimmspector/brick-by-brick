using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Numerics;
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
        WorldInteraction[] placementInteractions;

        SkillItem[] toolModes;
        const int CapacityPerTier = 16;

        // -----------------
        // AUDIO FILES
        // -----------------

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

        private const string MortarUseAnimationCode = "trowelmortarspread";
        private const string BrickUseAnimationCode = "trowelbrickplace";

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);

            // Cache mode data once per API instance. Icons are client-only, so
            // server loads keep the same mode codes with null textures.
            var capi = api as ICoreClientAPI;
            toolModes = ObjectCacheUtil.GetOrCreate<SkillItem[]>(api, "trowelToolModes", () => [
                new SkillItem {
                    Code = new AssetLocation("build"),
                    Name = Lang.Get("brickbybrick:toolmode-trowel-build"),
                    Texture = capi?.Gui.LoadSvgWithPadding(new AssetLocation("brickbybrick:textures/icons/trowel.svg"), 64, 64, 5, ColorUtil.WhiteArgb)
                },
                new SkillItem()
                {
                    Code = new AssetLocation("slab"),
                    Name = Lang.Get("brickbybrick:toolmode-trowel-slab"),
                    Texture = capi?.Gui.LoadSvgWithPadding(new AssetLocation("brickbybrick:textures/icons/brick-slab.svg"), 64, 64, 5, ColorUtil.WhiteArgb)
                },
                new SkillItem()
                {
                    Code = new AssetLocation("stair"),
                    Name = Lang.Get("brickbybrick:toolmode-trowel-stair"),
                    Texture = capi?.Gui.LoadSvgWithPadding(new AssetLocation("brickbybrick:textures/icons/brick-stair.svg"), 64, 64, 5, ColorUtil.WhiteArgb)
                },
                new SkillItem()
                {
                    Code = new AssetLocation("block"),
                    Name = Lang.Get("brickbybrick:toolmode-trowel-block"),
                    Texture = capi?.Gui.LoadSvgWithPadding(new AssetLocation("brickbybrick:textures/icons/brick-block.svg"), 64, 64, 5, ColorUtil.WhiteArgb)
                }
            ]);
            if (capi == null) return;

            // Build interaction help from all blocks marked trowelable so the
            // client shows right-click help on every construction-stage block.
            interactions = ObjectCacheUtil.GetOrCreate(capi, "trowelInteractions", () =>
            {
                List<ItemStack> stacks = new List<ItemStack>();

                foreach (Block block in capi.World.Blocks)
                {
                    if (block.Code == null) continue;
                    if (block.Attributes == null) continue;

                    if (block.Attributes["trowelable"].AsBool(false))
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

            // Placement modes consume fired bricks directly, so their held
            // help should preview the compatible brick items instead.
            placementInteractions = ObjectCacheUtil.GetOrCreate(capi, "trowelPlacementInteractions", () =>
            {
                List<ItemStack> stacks = new List<ItemStack>();

                foreach (Item item in capi.World.Items)
                {
                    string path = item?.Code?.Path;
                    if (string.IsNullOrEmpty(path)) continue;

                    if (path.StartsWith("burnedbrick-", StringComparison.Ordinal))
                    {
                        stacks.Add(new ItemStack(item));
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

            if (!firstEvent) return;
            if (byEntity?.World == null) return;
            if (blockSel == null) return;

            IWorldAccessor world = byEntity.World;
            BlockPos pos = blockSel.Position;
            Block block = world.BlockAccessor.GetBlock(pos);

            // Each held interaction gets one completed action. Reset the latch
            // and midpoint sound flag before mode-specific logic starts.
            SetInteracted(slot.Itemstack, false);
            slot.Itemstack.Attributes.SetBool("soundPlayed", false);

            // Refill from mortar containers immediately before any build mode
            // handling so buckets and similar targets always win over placement.
            if (TryCollectFromContainer(world, pos, slot))
            {
                SetInteracted(slot.Itemstack, true);
                handling = EnumHandHandling.PreventDefault;
                return;
            }

            IPlayer byPlayer = (byEntity as EntityPlayer)?.Player;
            if (byPlayer == null) return;

            // Mode selection determines whether this click advances an
            // existing build or places a new staged block beside the target.
            int toolMode = GetToolMode(slot, byPlayer, blockSel);

            // Placement modes own the interaction even when the selected block
            // is not trowelable because they place into the adjacent position.
            if (toolMode == 1 || toolMode == 2 || toolMode == 3)
            {
                UpdateTrowelUseAnimation(slot, byEntity, byPlayer, blockSel);
                handling = EnumHandHandling.PreventDefault;
                return;
            }

            // Build mode only advances existing construction blocks.
            if (!IsTrowelable(block))
            {
                handling = EnumHandHandling.PreventDefault;
                return;
            }

            UpdateTrowelUseAnimation(slot, byEntity, byPlayer, blockSel);

            switch (toolMode)
            {
                case 0:
                    handling = EnumHandHandling.PreventDefault;
                    return;

                default:
                    return;
            }
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

            int toolMode = GetToolMode(slot, player, blockSel);

            // If NOT a trowelable block, only placement modes should continue.
            if (!IsTrowelable(block) && toolMode != 1 && toolMode != 2 && toolMode != 3)
            {
                return false;
            }

            // Dispatch the held-use tick to the active mode. Returning true
            // keeps the hold alive; returning false ends or cancels the action.
            switch (toolMode)
            {
                case 0:
                    return HandleTrowelMode0(secondsUsed, slot, byEntity, player, block, pos);

                case 1:
                    return HandleTrowelMode1(secondsUsed, slot, byEntity, player, blockSel);

                case 2:
                    return HandleTrowelMode2(secondsUsed, slot, byEntity, player, blockSel);

                case 3:
                    return HandleTrowelMode3(secondsUsed, slot, byEntity, player, blockSel);

                default:
                    return false;
            }
        }

        public override void OnHeldInteractStop(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel)
        {
            base.OnHeldInteractStop(secondsUsed, slot, byEntity, blockSel, entitySel);

            if (byEntity?.World == null) return;

            StopTrowelUseAnimations(byEntity);

            var attrs = slot.Itemstack.Attributes;

            // Clear the one-action latch so a later right-click can run.
            attrs.SetBool("didInteract", false);
        }

        public override bool OnHeldInteractCancel(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, EnumItemUseCancelReason cancelReason)
        {
            // Any cancellation should leave the player animation state clean.
            StopTrowelUseAnimations(byEntity);
            return base.OnHeldInteractCancel(secondsUsed, slot, byEntity, blockSel, entitySel, cancelReason);
        }

        // -------------------------
        // MODE 2 LOGIC
        // -------------------------

        /// <summary>
        /// Handles stair placement mode by placing the first staged stair block
        /// with vanilla-style horizontal and upside-down orientation.
        /// </summary>
        private bool HandleTrowelMode2(float secondsUsed, ItemSlot slot, EntityAgent byEntity, IPlayer player, BlockSelection blockSel)
        {
            if (blockSel == null) return false;

            // Prevent duplicate placement while the same right-click is held.
            if (HasInteracted(slot.Itemstack)) return false;

            bool soundPlayed = slot.Itemstack.Attributes.GetBool("soundPlayed", false);
            BlockPos soundPos = blockSel.Position;

            // Play feedback at the midpoint, then wait for the full hold time
            // before consuming materials or placing the staged stair.
            if (!soundPlayed && secondsUsed >= 1f && byEntity.World.Side == EnumAppSide.Client)
            {
                PlayRandomSound(byEntity.World, soundPos, player, BrickSounds, 20f);
                slot.Itemstack.Attributes.SetBool("soundPlayed", true);
            }

            if (secondsUsed < 2f) return true;

            if (!HasEnoughMortar(slot, byEntity)) return false;
            if (!TryGetMode3BrickInfo(player, byEntity, out ItemSlot offhandSlot, out _, out string color)) return false;

            // Target and orientation are resolved from the original click so
            // side clicks place beside the block, matching vanilla placement.
            BlockPos targetPos = ResolveMode3TargetPos(blockSel);
            ResolveStairOrientationCodes(blockSel, byEntity, out string vertical, out string horizontal);

            AssetLocation blockCode = new AssetLocation("brickbybrick", $"brickstairscourse-four-{color}-{vertical}-{horizontal}-1");
            Block placeBlock = byEntity.World.BlockAccessor.GetBlock(blockCode);
            if (placeBlock == null)
            {
                NotifyPlayerDebug(player, byEntity.World, Lang.Get("brickbybrick:notice-trowel-could-not-resolve-stair", blockCode));
                return false;
            }

            Block existingBlock = byEntity.World.BlockAccessor.GetBlock(targetPos);
            if (existingBlock == null || !existingBlock.IsReplacableBy(placeBlock))
            {
                byEntity.World.Api.Logger.Event($"[TROWEL] Stair placement blocked at {targetPos} by {existingBlock?.Code}");
                return false;
            }

            // Consume materials only after all placement checks have passed.
            offhandSlot.TakeOut(1);
            offhandSlot.MarkDirty();

            byEntity.World.BlockAccessor.SetBlock(placeBlock.Id, targetPos);
            placeBlock.OnBlockPlaced(byEntity.World, targetPos, slot.Itemstack);

            SetStoredAmount(slot, GetStoredAmount(slot.Itemstack) - 1);
            SetInteracted(slot.Itemstack, true);

            return false;
        }

        // -------------------------
        // MODE 1 LOGIC
        // -------------------------

        /// <summary>
        /// Handles slab placement mode by placing the first staged slab block
        /// before mode 0 advances it through the remaining construction stages.
        /// </summary>
        private bool HandleTrowelMode1(float secondsUsed, ItemSlot slot, EntityAgent byEntity, IPlayer player, BlockSelection blockSel)
        {
            if (blockSel == null) return false;

            // Prevent duplicate placement while the same right-click is held.
            if (HasInteracted(slot.Itemstack)) return false;

            bool soundPlayed = slot.Itemstack.Attributes.GetBool("soundPlayed", false);
            BlockPos soundPos = blockSel.Position;

            // Play feedback at the midpoint, then wait for the full hold time
            // before consuming materials or placing the staged slab.
            if (!soundPlayed && secondsUsed >= 1f && byEntity.World.Side == EnumAppSide.Client)
            {
                PlayRandomSound(byEntity.World, soundPos, player, BrickSounds, 20f);
                slot.Itemstack.Attributes.SetBool("soundPlayed", true);
            }

            if (secondsUsed < 2f) return true;

            if (!HasEnoughMortar(slot, byEntity)) return false;
            if (!TryGetMode3BrickInfo(player, byEntity, out ItemSlot offhandSlot, out _, out string color)) return false;

            // Slab rotation comes from the clicked face, while the target
            // position remains adjacent to the selected block.
            BlockPos targetPos = ResolveMode3TargetPos(blockSel);
            string rot = ResolveSlabRotationCode(blockSel);
            AssetLocation blockCode = new AssetLocation("brickbybrick", $"brickslabcourse-four-{color}-{rot}-1");
            Block placeBlock = byEntity.World.BlockAccessor.GetBlock(blockCode);
            if (placeBlock == null)
            {
                NotifyPlayerDebug(player, byEntity.World, Lang.Get("brickbybrick:notice-trowel-could-not-resolve-slab", blockCode));
                return false;
            }

            Block existingBlock = byEntity.World.BlockAccessor.GetBlock(targetPos);
            if (existingBlock == null || !existingBlock.IsReplacableBy(placeBlock))
            {
                byEntity.World.Api.Logger.Event($"[TROWEL] Slab placement blocked at {targetPos} by {existingBlock?.Code}");
                return false;
            }

            // Consume materials only after all placement checks have passed.
            offhandSlot.TakeOut(1);
            offhandSlot.MarkDirty();

            byEntity.World.BlockAccessor.SetBlock(placeBlock.Id, targetPos);
            placeBlock.OnBlockPlaced(byEntity.World, targetPos, slot.Itemstack);

            SetStoredAmount(slot, GetStoredAmount(slot.Itemstack) - 1);
            SetInteracted(slot.Itemstack, true);

            return false;
        }

        // -------------------------
        // MODE 0 LOGIC
        // -------------------------

        /// <summary>
        /// Handles staged construction progression for trowelable blocks,
        /// advancing brick, slab, or stair stages when materials are available.
        /// </summary>
        private bool HandleTrowelMode0(float secondsUsed, ItemSlot slot, EntityAgent byEntity, IPlayer player, Block block, BlockPos pos)
        {

            // Mode 0 advances an existing construction block in-place.
            if (!IsTrowelable(block)) return false;
            
            // Prevent multiple stage advances during the same held click.
            if (HasInteracted(slot.Itemstack)) return false;
            
            // Read current construction state and decide which material this
            // advance needs. Mortar is used for every successful advance.
            int currentStage = GetBlockStage(block);
            int nextStage = currentStage + 1;
            bool isBrickStage = (nextStage == 3 || nextStage == 5 || nextStage == 8);
            bool isMortarStage = nextStage == 2 || nextStage == 4 || nextStage == 6 || nextStage == 7;
            string color = GetBlockColor(block);

            bool soundPlayed = slot.Itemstack.Attributes.GetBool("soundPlayed", false);

            // -------------------------
            // PLAY SOUND AT HALF TIME (1s)
            // -------------------------

            if (!soundPlayed && secondsUsed >= 1f && byEntity.World.Side == EnumAppSide.Client)
            {
                PlayStageSound(byEntity.World, pos, player, isBrickStage, isMortarStage);

                slot.Itemstack.Attributes.SetBool("soundPlayed", true);
            }


            if (secondsUsed < 2f) return true;

            // Determine the next block before consuming any resources so finished
            // staged blocks simply stop progressing without wasting materials.
            AssetLocation newPath = ResolveNextBlock(block, nextStage, color);
            if (newPath == null)
            {
                return false;
            }

            // Validate only the resources needed by this stage.
            if (isBrickStage && !HasMatchingMaterial(player, color, byEntity)) return false;
            if (!HasEnoughMortar(slot, byEntity)) return false;

            // -------------------------
            // BRICK CONSUMPTION LOGIC
            // -------------------------

            // At brick stages, consume one matching brick from offhand.
            if (isBrickStage)
            {
                string requiredPath = $"burnedbrick-{color}";

                if (!TryGetOffhandStack(player, out ItemSlot offhandSlot, out ItemStack offhandStack))
                {
                    NotifyPlayerDebug(player, byEntity.World, Lang.Get("brickbybrick:notice-trowel-hold-matching-brick"));
                    return false;
                }

                if (offhandStack.Collectible?.Code?.Path != requiredPath)
                {
                    NotifyPlayerDebug(player, byEntity.World, Lang.Get("brickbybrick:notice-trowel-wrong-brick", requiredPath, offhandStack.Collectible?.Code?.Path));
                    return false;
                }

                // Consume the brick only after the next block has resolved.
                offhandSlot.TakeOut(1);
                offhandSlot.MarkDirty();
            }

            // -------------------------
            // BLOCK TRANSFORMATION
            // -------------------------

            Block newBlock = byEntity.World.BlockAccessor.GetBlock(newPath);
            if (newBlock == null)
            {
                byEntity.World.Api.Logger.Warning($"[TROWEL] Block not found: {newPath}");
                return false;
            }

            // ExchangeBlock preserves the position while swapping to the next
            // construction stage or final vanilla block.
            byEntity.World.BlockAccessor.ExchangeBlock(newBlock.Id, pos);

            // Full brick blocks occupy the entire space, so remove any water
            // that was sharing the construction course's fluid layer.
            if (IsCompletingFullBrickBlock(block, nextStage))
            {
                byEntity.World.BlockAccessor.SetBlock(0, pos, BlockLayersAccess.Fluid);
            }

            // -------------------------
            // MORTAR CONSUMPTION (ALWAYS)
            // -------------------------

            SetStoredAmount(slot, GetStoredAmount(slot.Itemstack) - 1);

            // Mark that we've interacted to prevent repeat triggers
            SetInteracted(slot.Itemstack, true);

            return false;
        }

        // -------------------------
        // MODE 3 LOGIC
        // -------------------------

        /// <summary>
        /// Handles block placement mode by placing a four-running brick course
        /// beside or above the selected block after a short hold.
        /// </summary>
        private bool HandleTrowelMode3(float secondsUsed, ItemSlot slot, EntityAgent byEntity, IPlayer player, BlockSelection blockSel)
        {
            if (blockSel == null) return false;

            // Prevent duplicate placement while the same right-click is held.
            if (HasInteracted(slot.Itemstack)) return false;

            bool soundPlayed = slot.Itemstack.Attributes.GetBool("soundPlayed", false);
            BlockPos soundPos = blockSel.Position;

            // -------------------------
            // PLAY SOUND AT HALF TIME (1s)
            // -------------------------

            if (!soundPlayed && secondsUsed >= 1f && byEntity.World.Side == EnumAppSide.Client)
            {
                PlayRandomSound(byEntity.World, soundPos, player, BrickSounds, 20f);
                slot.Itemstack.Attributes.SetBool("soundPlayed", true);
            }

            if (secondsUsed < 2f) return true;

            // Validate mortar and offhand brick before resolving placement.
            if (!HasEnoughMortar(slot, byEntity)) return false;

            if (!TryGetMode3BrickInfo(player, byEntity, out ItemSlot offhandSlot, out _, out string color)) return false;

            // Uncomment this guard if block mode should never place below the clicked block.
            //if (blockSel.Face == BlockFacing.DOWN)
            //{
            //    NotifyPlayerDebug(player, byEntity.World, "[TROWEL] Block mode does not allow bottom-face placement");
            //    return false;
            //}

            BlockPos targetPos = ResolveMode3TargetPos(blockSel);
            Block placeBlock = ResolveMode3PlacementBlock(byEntity.World, byEntity, color);
            if (placeBlock == null) return false;

            Block existingBlock = byEntity.World.BlockAccessor.GetBlock(targetPos);
            if (existingBlock == null || !existingBlock.IsReplacableBy(placeBlock))
            {
                byEntity.World.Api.Logger.Event($"[TROWEL] Placement blocked at {targetPos} by {existingBlock?.Code}");
                return false;
            }

            // Consume the validated offhand brick only after every placement
            // check has succeeded.
            offhandSlot.TakeOut(1);
            offhandSlot.MarkDirty();

            // Place the resolved block and fire the standard placement hook.
            byEntity.World.BlockAccessor.SetBlock(placeBlock.Id, targetPos);
            placeBlock.OnBlockPlaced(byEntity.World, targetPos, slot.Itemstack);

            // Consume one stored mortar portion from the trowel.
            SetStoredAmount(slot, GetStoredAmount(slot.Itemstack) - 1);

            // Mark that we've interacted to prevent repeat triggers.
            SetInteracted(slot.Itemstack, true);

            return false;
        }

        /// <summary>
        /// Attempts to find and optionally consume items from the player's offhand
        /// that match a given condition. This generic helper is kept for
        /// future placement recipes that may need materials beyond fired brick.
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

            ItemSlot candidateSlot = player?.InventoryManager?.OffhandHotbarSlot;
            ItemStack candidateStack = candidateSlot?.Itemstack;

            if (candidateStack == null) return false;

            if (!matcher(candidateStack)) return false;

            // Ensure enough quantity if this call is actually consuming.
            if (consume && candidateSlot.StackSize < quantity) return false;

            // Perform consumption only after the caller's matcher succeeds.
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
        /// Returns max number of "liquidmortarportion" items this tool can hold.
        /// </summary>
        public static int GetMaxCapacity(ItemStack stack)
        {
            int toolTier = stack.Collectible.ToolTier;
            return toolTier * CapacityPerTier;
        }

        /// <summary>
        /// Gets how many mortar portions are currently stored in the item.
        /// </summary>
        public static int GetStoredAmount(ItemStack stack)
        {
            return stack.Attributes.GetInt("mortarAmount", 0);
        }

        /// <summary>
        /// Sets the mortar amount, syncs durability, and updates UI.
        /// This is the ONLY method that should ever modify stored mortar.
        /// </summary>
        private void SetStoredAmount(ItemSlot slot, int newAmount)
        {
            ItemStack stack = slot?.Itemstack;
            if (stack?.Attributes == null) return;

            int max = GetMaxCapacity(stack);

            // Clamp safely to keep durability and stored mortar in range.
            newAmount = GameMath.Clamp(newAmount, 0, max);

            // Store the gameplay value first, then mirror it to durability for UI.
            stack.Attributes.SetInt("mortarAmount", newAmount);

            // --- SYNC DURABILITY ---
            int jsonMax = stack.Collectible.GetMaxDurability(stack);

            if (jsonMax <= 1)
            {
                stack.Attributes.SetInt("durability", 1);
            }
            else
            {
                float ratio = (float)newAmount / max;

                int durability = (int)(ratio * jsonMax);

                // Prevent broken/hidden states while still showing depletion.
                durability = GameMath.Clamp(durability, 1, jsonMax - 1);

                stack.Attributes.SetInt("durability", durability);
            }

            // Force the client inventory UI to refresh.
            slot.MarkDirty();
        }

        /// <summary>
        /// Maps vanilla durability display to mortar capacity instead of tool wear.
        /// </summary>
        public override int GetMaxDurability(ItemStack itemstack)
        {
            return GetMaxCapacity(itemstack);
        }

        // ------------------------
        // LIQUID HANDLING
        // ------------------------

        /// <summary>
        /// Adds mortar to the tool safely and returns amount actually added.
        /// </summary>
        private int AddLiquid(ItemSlot toolSlot, int amountToAdd)
        {
            ItemStack stack = toolSlot.Itemstack;

            int current = GetStoredAmount(stack);
            int max = GetMaxCapacity(stack);

            int space = max - current;
            int moved = GameMath.Clamp(amountToAdd, 0, space);

            if (moved <= 0) return 0;

            SetStoredAmount(toolSlot, current + moved);

            return moved;
        }

        /// <summary>
        /// Ensures trowels only accept stored "liquidmortarportion" items.
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
        /// Attempts to pull mortar portions from a container block at the
        /// selected position until either the trowel or container is empty.
        /// </summary>
        private bool TryCollectFromContainer(IWorldAccessor world, BlockPos pos, ItemSlot toolSlot)
        {
            BlockEntity be = world.BlockAccessor.GetBlockEntity(pos);
            if (be == null) return false;

            var invProvider = be as IBlockEntityContainer;
            if (invProvider == null) return false;

            var inv = invProvider.Inventory;

            bool movedAny = false;

            for (int i = 0; i < inv.Count; i++)
            {
                ItemSlot slot = inv[i];
                ItemStack content = slot.Itemstack;

                if (!IsValidLiquid(content)) continue;

                int moved = AddLiquid(toolSlot, content.StackSize);

                if (moved > 0)
                {
                    slot.TakeOut(moved);
                    slot.MarkDirty();

                    movedAny = true;

                    // Stop once no more mortar can be accepted by the trowel.
                    if (GetStoredAmount(toolSlot.Itemstack) >= GetMaxCapacity(toolSlot.Itemstack))
                    {
                        break;
                    }
                }
            }

            return movedAny;
        }

        // ------------------------
        // TOOLTIP
        // ------------------------

        /// <summary>
        /// Replaces the vanilla durability tooltip with stored mortar capacity.
        /// </summary>
        public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
        {
            base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);

            // Remove vanilla durability text so players see mortar capacity.
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

            dsc.AppendLine(Lang.Get("brickbybrick:tooltip-trowel-mortar", stored, max));

            // -------------------------
            // EMPTY TOOLTIP HINT
            // -------------------------

            if (stored == 0)
            {
                dsc.AppendLine();
                dsc.AppendLine(Lang.Get("brickbybrick:tooltip-trowel-refill"));
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
            if (world == null ||  sounds == null || sounds.Length == 0) return;
 
            var rand = world.Rand;

            int index = rand.Next(sounds.Length);

            var sound = new AssetLocation("brickbybrick", $"sounds/{sounds[index]}");
            //world.Api.Logger.Event($"Playing sound: {sound} at position {pos} with range {range}");

            // Subtle variation
            float pitch = 0.95f + (float)rand.NextDouble() * 0.1f;  // pitch 0.95 to 1.05
            float volume = 0.9f + (float)rand.NextDouble() * 0.2f;  // volume 0.9 to 1.1

            world.PlaySoundAt(
                sound,
                pos.X + 0.5,    // center of block
                pos.Y + 0.5,
                pos.Z + 0.5,
                player,
                true,
                range,
                volume
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
        /// Determines the placement position for block mode based on the face
        /// the player held against.
        /// </summary>
        private BlockPos ResolveMode3TargetPos(BlockSelection blockSel)
        {
            return blockSel.Position.AddCopy(blockSel.Face);
        }

        /// <summary>
        /// Resolves the four-running course block variant to place based on the
        /// held brick color and the player's facing direction.
        /// </summary>
        private Block ResolveMode3PlacementBlock(IWorldAccessor world, EntityAgent byEntity, string color)
        {
            // Use the entity's current yaw to choose between the two straight
            // running-bond variants. North/south uses "runningo" and east/west
            // uses "running" so the visible brick faces align with the player.
            BlockFacing facing = BlockFacing.HorizontalFromYaw(byEntity.Pos.Yaw);
            string type = facing.IsAxisWE ? "running" : "runningo";
            AssetLocation blockCode = new AssetLocation("brickbybrick", $"brickcourse-four-{type}-{color}-1");

            Block placeBlock = world.BlockAccessor.GetBlock(blockCode);
            if (placeBlock == null)
            {
                world.Api.Logger.Event($"[TROWEL] Could not resolve placement block {blockCode}");
            }

            return placeBlock;
        }

        /// <summary>
        /// Resolves the slab rotation code from the clicked face. Slabs occupy the
        /// side of the target block space opposite the face that was clicked.
        /// </summary>
        private string ResolveSlabRotationCode(BlockSelection blockSel)
        {
            BlockFacing clickedFace = blockSel?.Face ?? BlockFacing.UP;

            if (clickedFace == BlockFacing.UP) return BlockFacing.DOWN.Code;
            if (clickedFace == BlockFacing.DOWN) return BlockFacing.UP.Code;

            return clickedFace.Opposite.Code;
        }

        /// <summary>
        /// Resolves stair orientation from the clicked face and player yaw.
        /// Side-face clicks above the midpoint place upside-down stairs.
        /// </summary>
        private void ResolveStairOrientationCodes(BlockSelection blockSel, EntityAgent byEntity, out string vertical, out string horizontal)
        {
            BlockFacing clickedFace = blockSel?.Face ?? BlockFacing.UP;
            bool upperHalf = clickedFace.IsHorizontal && blockSel.HitPosition != null && blockSel.HitPosition.Y > 0.5;

            vertical = clickedFace == BlockFacing.DOWN || upperHalf ? "down" : "up";
            horizontal = BlockFacing.HorizontalFromYaw(byEntity.Pos.Yaw).Code;
        }

        /// <summary>
        /// Validates the player's offhand brick for placement modes and returns
        /// the exact slot, stack, and resolved color to use.
        /// </summary>
        private bool TryGetMode3BrickInfo(IPlayer player, EntityAgent byEntity, out ItemSlot slot, out ItemStack stack, out string color)
        {
            slot = null;
            stack = null;
            color = null;

            if (!TryGetOffhandStack(player, out slot, out stack))
            {
                NotifyPlayerDebug(player, byEntity.World, Lang.Get("brickbybrick:notice-trowel-hold-burned-brick"));
                return false;
            }

            string path = stack.Collectible?.Code?.Path;
            if (string.IsNullOrEmpty(path) || !path.StartsWith("burnedbrick-"))
            {
                NotifyPlayerDebug(player, byEntity.World, Lang.Get("brickbybrick:notice-trowel-hold-burned-brick-found", stack?.Collectible?.Code));
                return false;
            }

            color = GetItemColor(stack);
            if (string.IsNullOrEmpty(color))
            {
                NotifyPlayerDebug(player, byEntity.World, Lang.Get("brickbybrick:notice-trowel-could-not-read-brick-color", stack?.Collectible?.Code));
                return false;
            }

            return true;
        }

        /// <summary>
        /// Writes a debug message to the log and, when available, shows it to the
        /// local player as a chat notification.
        /// </summary>
        private void NotifyPlayerDebug(IPlayer player, IWorldAccessor world, string message)
        {
            world.Api.Logger.Event(message);

            if (world.Side == EnumAppSide.Client && player is IClientPlayer clientPlayer)
            {
                clientPlayer.ShowChatNotification(message);
            }
        }

        /// <summary>
        /// Starts the appropriate staged-use animation for the current trowel
        /// action, or stops any trowel animations when none should play.
        /// </summary>
        private void UpdateTrowelUseAnimation(ItemSlot slot, EntityAgent byEntity, IPlayer player, BlockSelection blockSel)
        {
            string animationCode = ResolveTrowelUseAnimationCode(slot, byEntity, player, blockSel);

            if (animationCode == null)
            {
                StopTrowelUseAnimations(byEntity);
                return;
            }

            StartTrowelUseAnimation(byEntity, animationCode);
        }

        /// <summary>
        /// Resolves which custom animation should play for the current
        /// interaction based on tool mode and construction stage.
        /// </summary>
        private string ResolveTrowelUseAnimationCode(ItemSlot slot, EntityAgent byEntity, IPlayer player, BlockSelection blockSel)
        {
            if (slot?.Itemstack == null || byEntity?.World == null || player == null || blockSel == null) return null;

            int toolMode = GetToolMode(slot, player, blockSel);
            Block selectedBlock = byEntity.World.BlockAccessor.GetBlock(blockSel.Position);

            switch (toolMode)
            {
                case 0:
                    if (!IsTrowelable(selectedBlock)) return null;

                    int nextStage = GetBlockStage(selectedBlock) + 1;

                    if (nextStage == 2 || nextStage == 4 || nextStage == 6 || nextStage == 7)
                    {
                        return MortarUseAnimationCode;
                    }

                    if (nextStage == 3 || nextStage == 5 || nextStage == 8)
                    {
                        return BrickUseAnimationCode;
                    }

                    return null;

                case 1:
                    return BrickUseAnimationCode;

                case 2:
                    return BrickUseAnimationCode;

                case 3:
                    return BrickUseAnimationCode;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Starts a custom third-person trowel animation on the interacting
        /// entity. The matching code and animation names are identical.
        /// </summary>
        private void StartTrowelUseAnimation(EntityAgent byEntity, string animationCode)
        {
            if (byEntity?.AnimManager == null || string.IsNullOrEmpty(animationCode)) return;

            byEntity.AnimManager.TryStartAnimation(new AnimationMetaData
            {
                Code = animationCode,
                Animation = animationCode,
                AnimationSpeed = 1f,
                EaseInSpeed = 6f,
                EaseOutSpeed = 6f,
                Weight = 8f,
                WeightCapFactor = 0.75f,
                BlendMode = EnumAnimationBlendMode.AddAverage
            });
        }

        /// <summary>
        /// Stops all custom trowel use animations that may be active.
        /// </summary>
        private void StopTrowelUseAnimations(EntityAgent byEntity)
        {
            if (byEntity?.AnimManager == null) return;

            byEntity.AnimManager.StopAnimation(MortarUseAnimationCode);
            byEntity.AnimManager.StopAnimation(BrickUseAnimationCode);
        }

        /// <summary>
        /// Returns true if the block can still be modified by the trowel.
        /// Completed staged slab and stair blocks are treated as final and return false.
        /// </summary>
        private bool IsTrowelable(Block block)
        {
            if (block?.Attributes?["trowelable"].AsBool(false) != true) return false;

            // Final staged slab and stair variants should behave like completed
            // blocks, so mode 0 no longer advances them.
            if (IsFinalStagedVariant(block)) return false;

            return true;
        }

        /// <summary>
        /// Returns held help for the active mode, using construction stages for
        /// build mode and fired bricks for placement modes.
        /// </summary>
        public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot)
        {
            base.GetHeldInteractionHelp(inSlot);
            int maxToolMode = toolModes == null ? 0 : Math.Max(0, toolModes.Length - 1);
            int toolMode = inSlot?.Itemstack == null ? 0 : Math.Min(maxToolMode, inSlot.Itemstack.Attributes.GetInt("toolMode"));
            WorldInteraction[] activeInteractions = toolMode == 0 ? interactions : placementInteractions;

            WorldInteraction modeInteraction = new WorldInteraction()
            {
                ActionLangCode = "Change tool mode",
                HotKeyCodes = new string[] { "toolmodeselect" },
                MouseButton = EnumMouseButton.None
            };

            return activeInteractions == null
                ? new WorldInteraction[] { modeInteraction }
                : activeInteractions.Append(modeInteraction);

        }
        /// <summary>
        /// Extracts the numeric construction stage from the block variant or code.
        /// </summary>
        private int GetBlockStage(Block block)
        {
            if (block == null) return 0;

            if (block.Variant != null && block.Variant.TryGetValue("stage", out string stageValue))
            {
                return int.TryParse(stageValue, out int parsedStage) ? parsedStage : 0;
            }

            int stage;
            return int.TryParse(block.LastCodePart(0), out stage) ? stage : 0;
        }

        /// <summary>
        /// Extracts the color variant from the block variant data or code.
        /// </summary>
        private string GetBlockColor(Block block)
        {
            if (block?.Variant != null && block.Variant.TryGetValue("color", out string color))
            {
                return color;
            }

            return block?.LastCodePart(1);
        }

        /// <summary>
        /// Reads a named block variant when the block code exposes it.
        /// </summary>
        private string GetBlockVariant(Block block, string variantCode)
        {
            if (block?.Variant != null && block.Variant.TryGetValue(variantCode, out string value))
            {
                return value;
            }

            return null;
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
            if (!TryGetOffhandStack(player, out _, out ItemStack stack))
            {
                NotifyPlayerDebug(player, byEntity.World, Lang.Get("brickbybrick:notice-trowel-hold-required-brick", requiredPath));
                return false;
            }

            string held = stack.Collectible?.Code?.Path;
            if (held != requiredPath)
            {
                NotifyPlayerDebug(player, byEntity.World, Lang.Get("brickbybrick:notice-trowel-wrong-brick", requiredPath, held ?? Lang.Get("brickbybrick:notice-trowel-nothing")));
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
                IPlayer player = (byEntity as EntityPlayer)?.Player;
                NotifyPlayerDebug(player, byEntity.World, Lang.Get("brickbybrick:notice-trowel-no-mortar"));
                return false;
            }

            return true;
        }

        /// <summary>
        /// Returns true when the current held interaction has already produced
        /// its one placement or stage advancement.
        /// </summary>
        private bool HasInteracted(ItemStack stack)
        {
            return stack.Attributes.GetBool("didInteract", false);
        }

        /// <summary>
        /// Sets the held-interaction latch used to prevent duplicate outputs
        /// during one right-click hold.
        /// </summary>
        private void SetInteracted(ItemStack stack, bool value)
        {
            stack.Attributes.SetBool("didInteract", value);
        }


        // -------------------------
        // BLOCK RESOLUTION
        // -------------------------

        /// <summary>
        /// Determines the next staged or final block state based on construction
        /// progression. Slabs and stairs finish as vanilla game blocks; regular
        /// brick courses finish as their completed full-block variant.
        /// </summary>
        private AssetLocation ResolveNextBlock(Block block, int nextStage, string color)
        {
            if (block == null) return null;

            if (IsStagedSlabBlock(block))
            {
                // Staged slabs have three mod stages; the fourth advance
                // resolves directly to the matching vanilla slab block.
                if (nextStage <= 3)
                {
                    return block.CodeWithParts(nextStage.ToString());
                }

                if (nextStage == 4)
                {
                    string slabColor = GetBlockColor(block);
                    string rot = GetBlockVariant(block, "rot") ?? BlockFacing.DOWN.Code;

                    return new AssetLocation("game", $"brickslabs-four-{slabColor}-{rot}-free");
                }

                return null;
            }

            if (IsStagedStairBlock(block))
            {
                // Staged stairs have five mod stages; the sixth advance
                // resolves directly to the matching vanilla stair block.
                if (nextStage <= 5)
                {
                    return block.CodeWithParts(nextStage.ToString());
                }

                if (nextStage == 6)
                {
                    string stairColor = GetBlockColor(block);
                    string vertical = GetBlockVariant(block, "verticalorientation") ?? "up";
                    string horizontal = GetBlockVariant(block, "horizontalorientation") ?? BlockFacing.NORTH.Code;

                    return new AssetLocation("game", $"brickstairs-four-{stairColor}-{vertical}-{horizontal}-free");
                }

                return null;
            }

            if (nextStage <= 7)
            {
                return block.CodeWithParts(nextStage.ToString());
            }

            if (nextStage == 8)
            {
                // Fire brick uses a custom finished block, while other colors
                // can drop the stage suffix from the course code.
                if (color == "fire")
                {
                    return new AssetLocation("brickbybrick:brickblock-good-fire");
                }

                return block.CodeWithoutParts(1);
            }

            return null;
        }

        /// <summary>
        /// Returns true when the given block is one of the staged slab variants.
        /// </summary>
        private bool IsStagedSlabBlock(Block block)
        {
            return block?.Code?.PathStartsWith("brickslabcourse") == true;
        }

        /// <summary>
        /// Returns true when the given block is one of the staged stair variants.
        /// </summary>
        private bool IsStagedStairBlock(Block block)
        {
            return block?.Code?.PathStartsWith("brickstairscourse") == true;
        }

        /// <summary>
        /// Returns true when a regular brick course is becoming a full block.
        /// Slab and stair completions keep any fluid-layer handling separate.
        /// </summary>
        private bool IsCompletingFullBrickBlock(Block block, int nextStage)
        {
            if (block?.Code?.PathStartsWith("brickcourse") != true) return false;

            return nextStage == 8;
        }

        /// <summary>
        /// Returns true when a staged construction block has reached its final
        /// build stage and should no longer advance through trowel mode 0.
        /// </summary>
        private bool IsFinalStagedVariant(Block block)
        {
            int stage = GetBlockStage(block);

            if (IsStagedSlabBlock(block) || IsStagedStairBlock(block))
            {
                return IsStagedSlabBlock(block) ? stage >= 4 : stage >= 6;
            }

            return false;
        }

        // -------------------------
        // AUDIO
        // -------------------------

        /// <summary>
        /// Plays the appropriate sound for the current stage.
        /// </summary>
        private void PlayStageSound(IWorldAccessor world, BlockPos pos, IPlayer player, bool isBrickStage, bool isMortarStage)
        {
            if (isBrickStage)
            {
                PlayRandomSound(world, pos, player, BrickSounds, 20f);
            }
            else if (isMortarStage)
            {
                PlayRandomSound(world, pos, player, TrowelSounds, 12f);
            }
        }
    }
}
