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

        SkillItem[] toolModes;
        const int CapacityPerTier = 14;

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
                    Texture = capi?.Gui.LoadSvgWithPadding(new AssetLocation("brickbybrick:textures/icons/brick-slab.svg"), 64, 64, 5, ColorUtil.WhiteArgb)
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
                    Texture = capi?.Gui.LoadSvgWithPadding(new AssetLocation("brickbybrick:textures/icons/brick-block.svg"), 64, 64, 5, ColorUtil.WhiteArgb)
                }
            ]);
            if (capi == null) return;
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

            // -------------------------
            // RESET STATE
            // -------------------------

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

            // -------------------------
            // DETERMINE STAGE + MODE
            // -------------------------

            int currentStage = GetBlockStage(block);
            int nextStage = currentStage + 1;

            bool isBrickStage = (nextStage == 3 || nextStage == 5 || nextStage == 7);
            bool isMortarStage = (nextStage == 2 || nextStage == 4 || nextStage == 6);

            // DEBUG
            world.Api.Logger.Event($"[Start] Side={world.Side}");

            IPlayer byPlayer = (byEntity as EntityPlayer)?.Player;
            if (byPlayer == null) return;

            int toolMode = GetToolMode(slot, byPlayer, blockSel);

            if (toolMode == 1 || toolMode == 3)
            {
                UpdateTrowelUseAnimation(slot, byEntity, byPlayer, blockSel);
                handling = EnumHandHandling.PreventDefault;
                return;
            }

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
            if (!IsTrowelable(block) && toolMode != 1 && toolMode != 3)
            {
                return false; // let default behavior (pickup) happen
            }

            //byEntity.World.Api.Logger.Event($"Trowel used continuously for {secondsUsed} seconds!");

            //byEntity.World.Api.Logger.Event($"Tool mode: {toolMode}");

            switch (toolMode)
            {
                case 0:
                    //byEntity.World.Api.Logger.Event($"Handling case: {toolMode}");
                    {
                        return HandleTrowelMode0(secondsUsed, slot, byEntity, player, block, pos);
                    }
                case 1:
                    return HandleTrowelMode1(secondsUsed, slot, byEntity, player, blockSel);

                case 2:
                    // Placeholder for future functionality
                    return false;

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

            attrs.SetBool("didInteract", false);

            // DEBUG
            byEntity.World.Api.Logger.Event($"[TROWEL] Interaction stopped");
        }

        public override bool OnHeldInteractCancel(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, EnumItemUseCancelReason cancelReason)
        {
            StopTrowelUseAnimations(byEntity);
            return base.OnHeldInteractCancel(secondsUsed, slot, byEntity, blockSel, entitySel, cancelReason);
        }

        //public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
        //{
        //    base.OnBeforeRender(capi, itemstack, target, ref renderinfo);

        //    if (target != EnumItemRenderTarget.HandTp) return;

        //    var slot = capi.World.Player?.InventoryManager?.ActiveHotbarSlot;
        //    var stack = slot?.Itemstack;

        //    if (stack == null || stack.Collectible != this) return;
        //    if (itemstack != stack) return;

        //    var attrs = stack.Attributes;
        //    if (attrs == null) return;

        //    if (!attrs.GetBool(AttrIsUsing, false)) return;

        //    // Clone transform
        //    renderinfo.Transform = renderinfo.Transform.Clone();

        //    float startTime = attrs.GetFloat(AttrUseStart, 0f);
        //    float now = capi.World.ElapsedMilliseconds / 1000f;
        //    float secondsUsed = now - startTime;

        //    if (secondsUsed < 0f || secondsUsed > 2f) return;

        //    int mode = attrs.GetInt(AttrUseMode, 0);

        //    // Time
        //    float t = secondsUsed / 2f;

        //    // Base values
        //    float tx = renderinfo.Transform.Translation.X;
        //    float ty = renderinfo.Transform.Translation.Y;
        //    float tz = renderinfo.Transform.Translation.Z;

        //    float rx = renderinfo.Transform.Rotation.X;
        //    float ry = renderinfo.Transform.Rotation.Y;
        //    float rz = renderinfo.Transform.Rotation.Z;

        //    // -------------------------
        //    // BUILD LOCAL SIDE VECTOR
        //    // -------------------------

        //    // Sideways motion amount
        //    //float sweep = GameMath.Sin(t * GameMath.TWOPI * 2f) * 0.05f;
        //    float sweep = GameMath.Sin(t * GameMath.TWOPI * 2f) * 0.15f;

        //    // Compute sideways direction based on current rotation
        //    float cosY = GameMath.Cos(ry);
        //    float sinY = GameMath.Sin(ry);

        //    // Rotate X movement into local space
        //    float localX = sweep * cosY;
        //    float localZ = sweep * sinY;

        //    // Apply as sideways relative to hand
        //    tx += localX;
        //    tz -= localZ;

        //    // -------------------------
        //    // ADD VISIBLE ROTATION (helps perception)
        //    // -------------------------

        //    rz += sweep * 8f * GameMath.DEG2RAD;
        //    ry += sweep * 4f * GameMath.DEG2RAD;

        //    // -------------------------
        //    // BRICK MODE (camera feel)
        //    // -------------------------

        //    if (mode == 0)
        //    {
        //        float sway = GameMath.Sin(t * GameMath.TWOPI) * 0.015f;
        //        ty += sway;

        //        rz += sway * 1.5f;
        //    }

        //    // Apply final values
        //    renderinfo.Transform.Translation.X = tx;
        //    renderinfo.Transform.Translation.Y = ty;
        //    renderinfo.Transform.Translation.Z = tz;

        //    renderinfo.Transform.Rotation.X = rx;
        //    renderinfo.Transform.Rotation.Y = ry;
        //    renderinfo.Transform.Rotation.Z = rz;
        //}

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

            // Slab mode only places the initial staged slab. Existing staged slab
            // blocks should be advanced with mode 0.
            Block selectedBlock = byEntity.World.BlockAccessor.GetBlock(blockSel.Position);
            if (IsStagedSlabBlock(selectedBlock)) return false;

            if (HasInteracted(slot.Itemstack)) return false;

            bool soundPlayed = slot.Itemstack.Attributes.GetBool("soundPlayed", false);
            BlockPos soundPos = blockSel.Position;

            if (!soundPlayed && secondsUsed >= 1f && byEntity.World.Side == EnumAppSide.Client)
            {
                PlayRandomSound(byEntity.World, soundPos, player, BrickSounds, 20f);
                slot.Itemstack.Attributes.SetBool("soundPlayed", true);
            }

            if (secondsUsed < 2f) return true;

            if (!HasEnoughMortar(slot, byEntity)) return false;
            if (!TryGetMode3BrickInfo(player, byEntity, out ItemSlot offhandSlot, out _, out string color)) return false;

            BlockPos targetPos = ResolveMode3TargetPos(blockSel);
            string rot = ResolveSlabRotationCode(blockSel.Face);
            AssetLocation blockCode = new AssetLocation("brickbybrick", $"brickslabcourse-four-{color}-{rot}-1");
            Block placeBlock = byEntity.World.BlockAccessor.GetBlock(blockCode);
            if (placeBlock == null)
            {
                NotifyPlayerDebug(player, byEntity.World, $"[TROWEL] Could not resolve slab block {blockCode}");
                return false;
            }

            Block existingBlock = byEntity.World.BlockAccessor.GetBlock(targetPos);
            if (existingBlock == null || !existingBlock.IsReplacableBy(placeBlock))
            {
                byEntity.World.Api.Logger.Event($"[TROWEL] Slab placement blocked at {targetPos} by {existingBlock?.Code}");
                return false;
            }

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

            // Ensure the block is valid and can be interacted with by the trowel
            if (!IsTrowelable(block)) return false;
            
            // Prevent multiple triggers during same hold
            if (HasInteracted(slot.Itemstack)) return false;
            
            // Extract stage and color from block code
            int currentStage = GetBlockStage(block);
            int nextStage = currentStage + 1;
            bool isBrickStage = (nextStage == 3 || nextStage == 5 || nextStage == 7);
            bool isMortarStage = (nextStage == 2 || nextStage == 4 || nextStage == 6);
            string color = GetBlockColor(block);

            bool soundPlayed = slot.Itemstack.Attributes.GetBool("soundPlayed", false);

            // DEBUG: inspect offhand state
            //byEntity.World.Api.Logger.Event("Block color: " + color);
            //byEntity.World.Api.Logger.Event("Offhand slot exists: " + (player.InventoryManager?.OffhandHotbarSlot != null));
            //byEntity.World.Api.Logger.Event("Offhand item: " + player.InventoryManager?.OffhandHotbarSlot?.Itemstack?.Collectible?.Code);
            //byEntity.World.Api.Logger.Event("Offhand stack size: " + player.InventoryManager?.OffhandHotbarSlot?.StackSize);

            // -------------------------
            // PLAY SOUND AT HALF TIME (1s)
            // -------------------------

            if (!soundPlayed && secondsUsed >= 1f && byEntity.World.Side == EnumAppSide.Client)
            {
                PlayStageSound(byEntity.World, pos, player, isBrickStage, isMortarStage);

                slot.Itemstack.Attributes.SetBool("soundPlayed", true);
            }


            if (secondsUsed < 2f) return true;
            
            //byEntity.World.Api.Logger.Event($"Color: {color}");
            //byEntity.World.Api.Logger.Event($"Stage: {currentStage} -> {nextStage}");

            // Determine the next block before consuming any resources so finished
            // staged blocks simply stop progressing without wasting materials.
            AssetLocation newPath = ResolveNextBlock(block, nextStage, color);
            if (newPath == null)
            {
                return false;
            }

            // Validate player conditions
            if (!HasMatchingMaterial(player, color, byEntity)) return false;
            if (!HasEnoughMortar(slot, byEntity)) return false;

            // -------------------------
            // BRICK CONSUMPTION LOGIC
            // -------------------------

            // At stages 3, 5, and 7, consume one matching brick from offhand, enforcing offhand usage
            if (isBrickStage)
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

            // Brick placement sounds (stages 2, 4, 6)
            //if (nextStage == 2 || nextStage == 4 || nextStage == 6)
            //{
            //    PlayRandomSound(byEntity.World, pos, player, BrickSounds, 20f);
            //}

            // Trowel action sounds (stages 3, 5, 7)
            //if (nextStage == 3 || nextStage == 5 || nextStage == 7)
            //{
            //    PlayRandomSound(byEntity.World, pos, player, TrowelSounds, 12f);
            //}

            // -------------------------
            // MORTAR CONSUMPTION (ALWAYS)
            // -------------------------

            SetStoredAmount(slot, GetStoredAmount(slot.Itemstack) - 1);

            // Mark that we've interacted to prevent repeat triggers
            SetInteracted(slot.Itemstack, true);

            // Stop animation after interaction completes
            //StopTrowelUseAnimation(byEntity);
            // Stop animation state
            //slot.Itemstack.Attributes.SetBool(AttrIsUsing, false);

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

            // Prevent multiple triggers during same hold
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

            // Validate the mortar portion before trying placement logic.
            if (!HasEnoughMortar(slot, byEntity)) return false;

            // Resolve the target block code from the player's offhand item.
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

            ItemSlot candidateSlot = player?.InventoryManager?.OffhandHotbarSlot;
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

        /// <summary>
        /// Sets the mortar amount, syncs durability, and updates UI.
        /// This is the ONLY method that should ever modify stored mortar.
        /// </summary>
        private void SetStoredAmount(ItemSlot slot, int newAmount)
        {
            ItemStack stack = slot?.Itemstack;
            if (stack?.Attributes == null) return;

            int max = GetMaxCapacity(stack);

            // Clamp safely
            newAmount = GameMath.Clamp(newAmount, 0, max);

            // Store value
            stack.Attributes.SetInt("mortarAmount", newAmount);

            // --- Sync durability ---
            int jsonMax = stack.Collectible.GetMaxDurability(stack);

            if (jsonMax <= 1)
            {
                stack.Attributes.SetInt("durability", 1);
            }
            else
            {
                float ratio = (float)newAmount / max;

                int durability = (int)(ratio * jsonMax);

                // Prevent broken/hidden states
                durability = GameMath.Clamp(durability, 1, jsonMax - 1);

                stack.Attributes.SetInt("durability", durability);
            }

            // --- FORCE UI UPDATE ---
            slot.MarkDirty();
        }

        // Return max "capacity"
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

                int available = content.StackSize;

                int moved = AddLiquid(toolSlot, available);

                if (moved > 0)
                {
                    slot.TakeOut(moved);
                    slot.MarkDirty();

                    movedAny = true;

                    // Stop when full
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
        private string ResolveSlabRotationCode(BlockFacing clickedFace)
        {
            BlockFacing resolvedFace = clickedFace?.Opposite ?? BlockFacing.DOWN;
            return resolvedFace.Code;
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
                NotifyPlayerDebug(player, byEntity.World, "[TROWEL] Hold a burned brick in your offhand to place with the trowel.");
                return false;
            }

            string path = stack.Collectible?.Code?.Path;
            if (string.IsNullOrEmpty(path) || !path.StartsWith("burnedbrick-"))
            {
                NotifyPlayerDebug(player, byEntity.World, $"[TROWEL] Hold a burned brick in your offhand to place with the trowel. Found: {stack?.Collectible?.Code}");
                return false;
            }

            color = GetItemColor(stack);
            if (string.IsNullOrEmpty(color))
            {
                NotifyPlayerDebug(player, byEntity.World, $"[TROWEL] Could not determine brick color from offhand item: {stack?.Collectible?.Code}");
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

                    if (nextStage == 2 || nextStage == 4 || nextStage == 6)
                    {
                        return MortarUseAnimationCode;
                    }

                    if (nextStage == 3 || nextStage == 5 || nextStage == 7)
                    {
                        return BrickUseAnimationCode;
                    }

                    return null;

                case 1:
                    return IsStagedSlabBlock(selectedBlock) ? null : BrickUseAnimationCode;

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
        public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot)
        {
            base.GetHeldInteractionHelp(inSlot);
            WorldInteraction modeInteraction = new WorldInteraction()
            {
                ActionLangCode = "Change tool mode",
                HotKeyCodes = new string[] { "toolmodeselect" },
                MouseButton = EnumMouseButton.None
            };

            return interactions == null
                ? new WorldInteraction[] { modeInteraction }
                : interactions.Append(modeInteraction);

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
                NotifyPlayerDebug(player, byEntity.World, $"[TROWEL] Hold {requiredPath} in your offhand to continue this build stage.");
                return false;
            }

            string held = stack.Collectible?.Code?.Path;
            if (held != requiredPath)
            {
                NotifyPlayerDebug(player, byEntity.World, $"[TROWEL] Wrong brick color in offhand. Need {requiredPath}, found {held ?? "nothing"}.");
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
                NotifyPlayerDebug(player, byEntity.World, "[TROWEL] No mortar left in the trowel. Refill it before building.");
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
        /// Determines the next staged or final block state based on construction progression.
        /// </summary>
        private AssetLocation ResolveNextBlock(Block block, int nextStage, string color)
        {
            if (block == null) return null;

            if (IsStagedSlabBlock(block))
            {
                return nextStage <= 4 ? block.CodeWithParts(nextStage.ToString()) : null;
            }

            if (IsStagedStairBlock(block))
            {
                return nextStage <= 5 ? block.CodeWithParts(nextStage.ToString()) : null;
            }

            if (nextStage <= 6)
            {
                return block.CodeWithParts(nextStage.ToString());
            }

            if (nextStage == 7)
            {
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
        /// Returns true when a staged construction block has reached its final
        /// build stage and should no longer advance through trowel mode 0.
        /// </summary>
        private bool IsFinalStagedVariant(Block block)
        {
            int stage = GetBlockStage(block);

            if (IsStagedSlabBlock(block) || IsStagedStairBlock(block))
            {
                return IsStagedSlabBlock(block) ? stage >= 4 : stage >= 5;
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
