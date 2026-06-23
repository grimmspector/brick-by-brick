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
        WorldInteraction[] interactions;
        WorldInteraction[] placementInteractions;

        SkillItem[] toolModes;
        const int CapacityPerTier = 16;

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

        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);

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

            int toolMode = GetToolMode(slot, byPlayer, blockSel);

            // Placement modes own the interaction even when the selected block
            // is not trowelable because they place into the adjacent position.
            if (toolMode == 1 || toolMode == 2 || toolMode == 3)
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

            if (!IsTrowelable(block) && toolMode != 1 && toolMode != 2 && toolMode != 3)
            {
                return false;
            }

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

            attrs.SetBool("didInteract", false);
        }

        public override bool OnHeldInteractCancel(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, EnumItemUseCancelReason cancelReason)
        {
            StopTrowelUseAnimations(byEntity);
            return base.OnHeldInteractCancel(secondsUsed, slot, byEntity, blockSel, entitySel, cancelReason);
        }

        // Places the first staged stair with vanilla-style orientation.
        private bool HandleTrowelMode2(float secondsUsed, ItemSlot slot, EntityAgent byEntity, IPlayer player, BlockSelection blockSel)
        {
            if (blockSel == null) return false;

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

            offhandSlot.TakeOut(1);
            offhandSlot.MarkDirty();

            byEntity.World.BlockAccessor.SetBlock(placeBlock.Id, targetPos);
            placeBlock.OnBlockPlaced(byEntity.World, targetPos, slot.Itemstack);

            SetStoredAmount(slot, GetStoredAmount(slot.Itemstack) - 1);
            SetInteracted(slot.Itemstack, true);

            return false;
        }

        // Places the first staged slab; build mode handles later stages.
        private bool HandleTrowelMode1(float secondsUsed, ItemSlot slot, EntityAgent byEntity, IPlayer player, BlockSelection blockSel)
        {
            if (blockSel == null) return false;

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

            offhandSlot.TakeOut(1);
            offhandSlot.MarkDirty();

            byEntity.World.BlockAccessor.SetBlock(placeBlock.Id, targetPos);
            placeBlock.OnBlockPlaced(byEntity.World, targetPos, slot.Itemstack);

            SetStoredAmount(slot, GetStoredAmount(slot.Itemstack) - 1);
            SetInteracted(slot.Itemstack, true);

            return false;
        }

        // Advances an existing staged block.
        private bool HandleTrowelMode0(float secondsUsed, ItemSlot slot, EntityAgent byEntity, IPlayer player, Block block, BlockPos pos)
        {
            if (!IsTrowelable(block)) return false;
            
            if (HasInteracted(slot.Itemstack)) return false;
            
            // Read current construction state and decide which material this
            // advance needs. Mortar is used for every successful advance.
            int currentStage = GetBlockStage(block);
            int nextStage = currentStage + 1;
            bool isBrickStage = (nextStage == 3 || nextStage == 5 || nextStage == 8);
            bool isMortarStage = nextStage == 2 || nextStage == 4 || nextStage == 6 || nextStage == 7;
            string color = GetBlockColor(block);

            bool soundPlayed = slot.Itemstack.Attributes.GetBool("soundPlayed", false);

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

            SetStoredAmount(slot, GetStoredAmount(slot.Itemstack) - 1);

            SetInteracted(slot.Itemstack, true);

            return false;
        }

        // Places the first staged full-block course.
        private bool HandleTrowelMode3(float secondsUsed, ItemSlot slot, EntityAgent byEntity, IPlayer player, BlockSelection blockSel)
        {
            if (blockSel == null) return false;

            if (HasInteracted(slot.Itemstack)) return false;

            bool soundPlayed = slot.Itemstack.Attributes.GetBool("soundPlayed", false);
            BlockPos soundPos = blockSel.Position;

            if (!soundPlayed && secondsUsed >= 1f && byEntity.World.Side == EnumAppSide.Client)
            {
                PlayRandomSound(byEntity.World, soundPos, player, BrickSounds, 20f);
                slot.Itemstack.Attributes.SetBool("soundPlayed", true);
            }

            if (secondsUsed < 2f) return true;

            // Validate mortar and offhand brick before resolving placement.
            if (!HasEnoughMortar(slot, byEntity)) return false;

            if (!TryGetMode3BrickInfo(player, byEntity, out ItemSlot offhandSlot, out _, out string color)) return false;

            BlockPos targetPos = ResolveMode3TargetPos(blockSel);
            Block placeBlock = ResolveMode3PlacementBlock(byEntity.World, byEntity, color);
            if (placeBlock == null) return false;

            Block existingBlock = byEntity.World.BlockAccessor.GetBlock(targetPos);
            if (existingBlock == null || !existingBlock.IsReplacableBy(placeBlock))
            {
                byEntity.World.Api.Logger.Event($"[TROWEL] Placement blocked at {targetPos} by {existingBlock?.Code}");
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

        // Finds an offhand stack and optionally consumes it.
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

            if (consume && candidateSlot.StackSize < quantity) return false;

            if (consume)
            {
                candidateSlot.TakeOut(quantity);
                candidateSlot.MarkDirty();
            }

            slot = candidateSlot;
            stack = candidateStack;

            return true;
        }

        // Mortar capacity follows the tool's material tier.
        public static int GetMaxCapacity(ItemStack stack)
        {
            int toolTier = stack.Collectible.ToolTier;
            return toolTier * CapacityPerTier;
        }

        public static int GetStoredAmount(ItemStack stack)
        {
            return stack.Attributes.GetInt("mortarAmount", 0);
        }

        // Keeps stored mortar and the durability display in sync.
        private void SetStoredAmount(ItemSlot slot, int newAmount)
        {
            ItemStack stack = slot?.Itemstack;
            if (stack?.Attributes == null) return;

            int max = GetMaxCapacity(stack);

            newAmount = GameMath.Clamp(newAmount, 0, max);

            stack.Attributes.SetInt("mortarAmount", newAmount);

            int jsonMax = stack.Collectible.GetMaxDurability(stack);

            if (jsonMax <= 1)
            {
                stack.Attributes.SetInt("durability", 1);
            }
            else
            {
                float ratio = (float)newAmount / max;

                int durability = (int)(ratio * jsonMax);

                durability = GameMath.Clamp(durability, 1, jsonMax - 1);

                stack.Attributes.SetInt("durability", durability);
            }

            slot.MarkDirty();
        }

        public override int GetMaxDurability(ItemStack itemstack)
        {
            return GetMaxCapacity(itemstack);
        }

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

        private bool IsValidLiquid(ItemStack stack)
        {
            if (stack == null) return false;

            return stack?.Collectible?.Code?.Path == "liquidmortarportion";
        }

        // Pulls mortar until the trowel is full or the container is empty.
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

        // Replaces durability text with the stored mortar amount.
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

            if (stored == 0)
            {
                dsc.AppendLine();
                dsc.AppendLine(Lang.Get("brickbybrick:tooltip-trowel-refill"));
            }
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

        // Adds slight pitch and volume variation to action sounds.
        private void PlayRandomSound(IWorldAccessor world, BlockPos pos, IPlayer player, string[] sounds, float range)
        {
            if (world == null ||  sounds == null || sounds.Length == 0) return;
 
            var rand = world.Rand;

            int index = rand.Next(sounds.Length);

            var sound = new AssetLocation("brickbybrick", $"sounds/{sounds[index]}");
            float pitch = 0.95f + (float)rand.NextDouble() * 0.1f;
            float volume = 0.9f + (float)rand.NextDouble() * 0.2f;

            world.PlaySoundAt(
                sound,
                pos.X + 0.5,
                pos.Y + 0.5,
                pos.Z + 0.5,
                player,
                true,
                range,
                volume
            );
        }

        private bool TryGetOffhandStack(IPlayer player, out ItemSlot slot, out ItemStack stack)
        {
            slot = player?.InventoryManager?.OffhandHotbarSlot;
            stack = slot?.Itemstack;

            return stack != null;
        }

        private BlockPos ResolveMode3TargetPos(BlockSelection blockSel)
        {
            return blockSel.Position.AddCopy(blockSel.Face);
        }

        // Running-bond orientation follows the player's horizontal facing.
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

        private string ResolveSlabRotationCode(BlockSelection blockSel)
        {
            BlockFacing clickedFace = blockSel?.Face ?? BlockFacing.UP;

            if (clickedFace == BlockFacing.UP) return BlockFacing.DOWN.Code;
            if (clickedFace == BlockFacing.DOWN) return BlockFacing.UP.Code;

            return clickedFace.Opposite.Code;
        }

        // Side clicks above the midpoint place an upside-down stair.
        private void ResolveStairOrientationCodes(BlockSelection blockSel, EntityAgent byEntity, out string vertical, out string horizontal)
        {
            BlockFacing clickedFace = blockSel?.Face ?? BlockFacing.UP;
            bool upperHalf = clickedFace.IsHorizontal && blockSel.HitPosition != null && blockSel.HitPosition.Y > 0.5;

            vertical = clickedFace == BlockFacing.DOWN || upperHalf ? "down" : "up";
            horizontal = BlockFacing.HorizontalFromYaw(byEntity.Pos.Yaw).Code;
        }

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

        private void NotifyPlayerDebug(IPlayer player, IWorldAccessor world, string message)
        {
            world.Api.Logger.Event(message);

            if (world.Side == EnumAppSide.Client && player is IClientPlayer clientPlayer)
            {
                clientPlayer.ShowChatNotification(message);
            }
        }

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

        private void StopTrowelUseAnimations(EntityAgent byEntity)
        {
            if (byEntity?.AnimManager == null) return;

            byEntity.AnimManager.StopAnimation(MortarUseAnimationCode);
            byEntity.AnimManager.StopAnimation(BrickUseAnimationCode);
        }

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

        private string GetBlockColor(Block block)
        {
            if (block?.Variant != null && block.Variant.TryGetValue("color", out string color))
            {
                return color;
            }

            return block?.LastCodePart(1);
        }

        private string GetBlockVariant(Block block, string variantCode)
        {
            if (block?.Variant != null && block.Variant.TryGetValue(variantCode, out string value))
            {
                return value;
            }

            return null;
        }

        // Prefer the item variant, then fall back to the final code part.
        private string GetItemColor(ItemStack stack)
        {
            if (stack?.Collectible?.Code == null) return null;

            if (stack.Collectible.Variant != null && stack.Collectible.Variant.ContainsKey("color"))
            {
                return stack.Collectible.Variant["color"];
            }

            string path = stack.Collectible.Code.Path;

            if (string.IsNullOrEmpty(path)) return null;

            string[] parts = path.Split('-');

            return parts.Length > 1 ? parts[parts.Length - 1] : null;
        }

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

        private bool HasInteracted(ItemStack stack)
        {
            return stack.Attributes.GetBool("didInteract", false);
        }

        private void SetInteracted(ItemStack stack, bool value)
        {
            stack.Attributes.SetBool("didInteract", value);
        }

        // Resolves staged slabs and stairs to their vanilla finished blocks.
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

        private bool IsStagedSlabBlock(Block block)
        {
            return block?.Code?.PathStartsWith("brickslabcourse") == true;
        }

        private bool IsStagedStairBlock(Block block)
        {
            return block?.Code?.PathStartsWith("brickstairscourse") == true;
        }

        private bool IsCompletingFullBrickBlock(Block block, int nextStage)
        {
            if (block?.Code?.PathStartsWith("brickcourse") != true) return false;

            return nextStage == 8;
        }

        private bool IsFinalStagedVariant(Block block)
        {
            int stage = GetBlockStage(block);

            if (IsStagedSlabBlock(block) || IsStagedStairBlock(block))
            {
                return IsStagedSlabBlock(block) ? stage >= 4 : stage >= 6;
            }

            return false;
        }

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
