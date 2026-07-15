using AttributeRenderingLibrary;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
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
        private TrowelPlacementPreviewRenderer placementPreviewRenderer;
        private const int BuildMode = 0;
        private const int SlabMode = 1;
        private const int StairMode = 2;
        private const int BlockMode = 3;
        private const string MasonryCourseCode = "brickbybrick:masonrycourse";
        private const string FamilyAttribute = "masonryFamily";

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

        private enum ConstructionAction
        {
            None,
            Mortar,
            Masonry
        }

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
                        ActionLangCode = "brickbybrick:heldhelp-trowel-build",
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
                        ActionLangCode = "brickbybrick:heldhelp-trowel-block",
                        MouseButton = EnumMouseButton.Right,
                        Itemstacks = stacks.ToArray()
                    }
                };
            });

            placementPreviewRenderer = new TrowelPlacementPreviewRenderer(capi, this);
            capi.Event.RegisterRenderer(placementPreviewRenderer, EnumRenderStage.AfterOIT, "brickbybrick-trowel-placement-preview");
        }

        public override void OnUnloaded(ICoreAPI api)
        {
            if (api is ICoreClientAPI capi && placementPreviewRenderer != null)
            {
                capi.Event.UnregisterRenderer(placementPreviewRenderer, EnumRenderStage.AfterOIT);
                placementPreviewRenderer.Dispose();
                placementPreviewRenderer = null;
            }

            base.OnUnloaded(api);
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
            slot.Itemstack.Attributes.SetFloat("lastParticleTime", -1f);

            // Refill from mortar containers immediately before any build mode
            // handling so buckets and similar targets always win over placement.
            if (brickbybrickModSystem.Config.Trowels.AllowContainerRefill
                && TryCollectFromContainer(world, pos, slot))
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
            if (IsPlacementMode(toolMode))
            {
                BlockPos targetPos = ResolvePlacementTarget(blockSel);
                if (!ValidateStructuralSupport(world, targetPos, byPlayer))
                {
                    handling = EnumHandHandling.PreventDefault;
                    return;
                }

                if (!HasEnoughMortar(slot, byEntity))
                {
                    handling = EnumHandHandling.PreventDefault;
                    return;
                }

                if (!TryGetPlacementMaterial(byPlayer, byEntity, out _, out _, out _, out _))
                {
                    handling = EnumHandHandling.PreventDefault;
                    return;
                }

                UpdateTrowelUseAnimation(slot, byEntity, byPlayer, blockSel);
                handling = EnumHandHandling.PreventDefault;
                return;
            }

            if (!IsTrowelable(block))
            {
                handling = EnumHandHandling.PreventDefault;
                return;
            }

            if (!ValidateStructuralSupport(world, pos, byPlayer))
            {
                handling = EnumHandHandling.PreventDefault;
                return;
            }

            if (!HasEnoughMortar(slot, byEntity))
            {
                handling = EnumHandHandling.PreventDefault;
                return;
            }

            if (!CanStartBuildStage(block, world, pos, byPlayer, byEntity))
            {
                handling = EnumHandHandling.PreventDefault;
                return;
            }

            UpdateTrowelUseAnimation(slot, byEntity, byPlayer, blockSel);

            switch (toolMode)
            {
                case BuildMode:
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
                case BuildMode:
                    return HandleTrowelMode0(secondsUsed, slot, byEntity, player, block, pos);

                case SlabMode:
                case StairMode:
                case BlockMode:
                    return HandlePlacementMode(secondsUsed, slot, byEntity, player, blockSel, toolMode);

                default:
                    return false;
            }
        }

        public override void OnHeldInteractStop(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel)
        {
            base.OnHeldInteractStop(secondsUsed, slot, byEntity, blockSel, entitySel);

            if (byEntity?.World == null) return;

            StopTrowelUseAnimations(byEntity);

            SetInteracted(slot?.Itemstack, false);
        }

        public override bool OnHeldInteractCancel(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, EnumItemUseCancelReason cancelReason)
        {
            StopTrowelUseAnimations(byEntity);
            return base.OnHeldInteractCancel(secondsUsed, slot, byEntity, blockSel, entitySel, cancelReason);
        }

        // Places the first construction stage for slabs, stairs, and blocks.
        private bool HandlePlacementMode(float secondsUsed, ItemSlot slot, EntityAgent byEntity, IPlayer player, BlockSelection blockSel, int toolMode)
        {
            if (blockSel == null || HasInteracted(slot?.Itemstack)) return false;
            if (!HasEnoughMortar(slot, byEntity)) return false;

            PlayActionSoundAtMidpoint(secondsUsed, slot, byEntity, player, blockSel.Position, BrickSounds, 20f);
            SpawnPlacementParticlesDuringAction(secondsUsed, slot, byEntity, blockSel, toolMode);
            if (secondsUsed < GetInteractionSeconds(slot)) return true;
            if (byEntity.World.Side != EnumAppSide.Server) return false;

            if (!HasEnoughMortar(slot, byEntity)) return false;
            if (!TryGetPlacementMaterial(player, byEntity, out ItemSlot offhandSlot, out ItemStack materialStack, out string family, out string color)) return false;

            if (!TryCreatePlacementCourse(
                byEntity.World,
                byEntity,
                blockSel,
                toolMode,
                materialStack,
                family,
                color,
                out BlockPos targetPos,
                out Block placeBlock,
                out Variants placementVariants))
            {
                return false;
            }

            // Support is checked after the timed action so cave-ins or other
            // neighbour changes cannot leave a newly placed course floating.
            if (!ValidateStructuralSupport(byEntity.World, targetPos, player)) return false;

            Block existingBlock = byEntity.World.BlockAccessor.GetBlock(targetPos);
            if (existingBlock == null || !existingBlock.IsReplacableBy(placeBlock))
            {
                byEntity.World.Api.Logger.Event($"[TROWEL] Placement blocked at {targetPos} by {existingBlock?.Code}");
                return false;
            }

            ItemStack placementStack = CreateCourseStack(byEntity.World, placementVariants);

            byEntity.World.BlockAccessor.SetBlock(placeBlock.Id, targetPos, placementStack);
            ApplyCourseState(byEntity.World, targetPos, placementVariants);
            SpawnConstructionParticles(byEntity.World, targetPos, placeBlock, ConstructionAction.Masonry, color, false, 0.25, true, materialStack);

            ConsumeOffhand(offhandSlot, brickbybrickModSystem.Config.Trowels.MasonryCostPerAction);
            if (player.WorldData.CurrentGameMode != EnumGameMode.Creative)
            {
                ConsumeConfiguredMortar(slot, brickbybrickModSystem.Config.Trowels.MortarCostPerAction);
            }
            SetInteracted(slot.Itemstack, true);

            return false;
        }

        // Advances an existing staged block.
        private bool HandleTrowelMode0(float secondsUsed, ItemSlot slot, EntityAgent byEntity, IPlayer player, Block block, BlockPos pos)
        {
            if (!IsTrowelable(block)) return false;
            
            if (HasInteracted(slot.Itemstack)) return false;
            if (!HasEnoughMortar(slot, byEntity)) return false;
            
            // Read current construction state and decide which material this
            // advance needs. Mortar is used for every successful advance.
            int currentStage = GetBlockStage(block, byEntity.World, pos);
            int nextStage = currentStage + 1;
            ConstructionAction action = GetConstructionAction(nextStage);
            string color = GetBlockColor(block, byEntity.World, pos);

            PlayStageSoundAtMidpoint(secondsUsed, slot, byEntity, pos, player, action);
            SpawnStageParticlesDuringAction(secondsUsed, slot, byEntity, pos, block, action, color);
            if (secondsUsed < GetInteractionSeconds(slot)) return true;
            if (byEntity.World.Side != EnumAppSide.Server) return false;

            // Re-read the target after the timed action. Another player or a
            // block update may have changed it while this interaction ran.
            Block currentBlock = byEntity.World.BlockAccessor.GetBlock(pos);
            if (!IsTrowelable(currentBlock)) return false;
            if (GetBlockStage(currentBlock, byEntity.World, pos) != currentStage) return false;
            block = currentBlock;

            // Unsupported construction never advances. This is deliberately
            // checked again after the timed action because its support may move.
            if (!ValidateStructuralSupport(byEntity.World, pos, player)) return false;

            // Determine the next block before consuming any resources so finished
            // staged blocks simply stop progressing without wasting materials.
            AssetLocation newPath = ResolveNextBlock(block, nextStage, color, byEntity.World, pos);
            if (newPath == null)
            {
                return false;
            }

            // Validate only the resources needed by this stage.
            if (action == ConstructionAction.Masonry && !HasMatchingMaterial(player, color, byEntity)) return false;
            if (!HasEnoughMortar(slot, byEntity)) return false;

            ItemSlot materialSlot = null;
            if (action == ConstructionAction.Masonry)
            {
                if (!TryGetRequiredBrick(player, color, byEntity, out materialSlot)) return false;
            }

            Block newBlock = byEntity.World.BlockAccessor.GetBlock(newPath);
            if (newBlock == null)
            {
                byEntity.World.Api.Logger.Warning($"[TROWEL] Block not found: {newPath}");
                return false;
            }

            Variants courseVariants = GetCourseVariants(byEntity.World, pos, block);

            bool remainsConstructionCourse = newPath.ToString() == MasonryCourseCode;
            if (remainsConstructionCourse)
            {
                // ExchangeBlock keeps the existing ARL block entity while the
                // construction remains the same attribute-backed block.
                byEntity.World.BlockAccessor.ExchangeBlock(newBlock.Id, pos);
                courseVariants.Set("stage", nextStage.ToString());
                ApplyCourseState(byEntity.World, pos, courseVariants);
                SpawnConstructionParticles(byEntity.World, pos, newBlock, action, color, false, null, false, GetCourseMaterialStack(byEntity.World, pos, block));
            }
            else
            {
                // Completed masonry must discard the ARL block entity. A full
                // replacement avoids retaining construction data on slabs and
                // stairs whose final blocks use different block classes.
                byEntity.World.BlockAccessor.SetBlock(newBlock.Id, pos);
                newBlock.OnBlockPlaced(byEntity.World, pos, slot.Itemstack);
                SpawnConstructionParticles(byEntity.World, pos, newBlock, action, color, true, null, false, GetCourseMaterialStack(byEntity.World, pos, block));
            }

            // Full brick blocks occupy the entire space, so remove any water
            // that was sharing the construction course's fluid layer.
            if (IsCompletingFullBrickBlock(block, nextStage))
            {
                byEntity.World.BlockAccessor.SetBlock(0, pos, BlockLayersAccess.Fluid);
            }

            if (materialSlot != null)
            {
                ConsumeOffhand(materialSlot, brickbybrickModSystem.Config.Trowels.MasonryCostPerAction);
            }

            if (player.WorldData.CurrentGameMode != EnumGameMode.Creative)
            {
                ConsumeConfiguredMortar(slot, brickbybrickModSystem.Config.Trowels.MortarCostPerAction);
            }
            SetInteracted(slot.Itemstack, true);

            return false;
        }

        // Mortar capacity follows the tool's material tier.
        public static int GetMaxCapacity(ItemStack stack)
        {
            int toolTier = stack.Collectible.ToolTier;
            return toolTier * brickbybrickModSystem.Config.Trowels.CapacityPerTier;
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
            if (!brickbybrickModSystem.Config.Effects.EnableConstructionSounds) return;
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

        private static bool IsPlacementMode(int toolMode)
        {
            return toolMode == SlabMode || toolMode == StairMode || toolMode == BlockMode;
        }

        private AssetLocation ResolveFinishedPlacementBlockCode(int toolMode, Variants variants, string color)
        {
            if (variants == null || string.IsNullOrEmpty(color)) return null;

            if (toolMode == SlabMode)
            {
                string rot = variants.Get("rotation") ?? BlockFacing.DOWN.Code;
                return new AssetLocation("game", $"brickslabs-four-{color}-{rot}-free");
            }

            if (toolMode == StairMode)
            {
                string vertical = variants.Get("vertical") ?? "up";
                string horizontal = variants.Get("horizontal") ?? BlockFacing.NORTH.Code;
                return new AssetLocation("game", $"brickstairs-four-{color}-{vertical}-{horizontal}-free");
            }

            if (color == "fire")
            {
                return new AssetLocation("brickbybrick:brickblock-good-fire");
            }

            string state = variants.Get("state") ?? "four";
            string bond = variants.Get("bond") ?? "running";
            return new AssetLocation("game", $"brickcourse-{state}-{bond}-{color}");
        }

        private static ConstructionAction GetConstructionAction(int nextStage)
        {
            return nextStage switch
            {
                2 or 4 or 6 or 7 => ConstructionAction.Mortar,
                3 or 5 or 8 => ConstructionAction.Masonry,
                _ => ConstructionAction.None
            };
        }

        private void PlayActionSoundAtMidpoint(
            float secondsUsed,
            ItemSlot slot,
            EntityAgent byEntity,
            IPlayer player,
            BlockPos pos,
            string[] sounds,
            float range)
        {
            if (slot?.Itemstack == null || byEntity?.World?.Side != EnumAppSide.Client) return;
            if (secondsUsed < GetInteractionSeconds(slot) / 2f) return;
            if (slot.Itemstack.Attributes.GetBool("soundPlayed", false)) return;

            PlayRandomSound(byEntity.World, pos, player, sounds, brickbybrickModSystem.Config.Effects.ConstructionSoundRange);
            slot.Itemstack.Attributes.SetBool("soundPlayed", true);
        }

        // Scales the configured duration uniformly from copper through steel.
        // Wood and copper retain the baseline while steel completes in half the time.
        private float GetInteractionSeconds(ItemSlot slot)
        {
            const int baselineTier = 2;
            const int steelTier = 5;
            const float steelDurationMultiplier = 0.5f;

            int toolTier = GameMath.Clamp(slot?.Itemstack?.Collectible?.ToolTier ?? baselineTier, baselineTier, steelTier);
            float tierProgress = (toolTier - baselineTier) / (float)(steelTier - baselineTier);
            float durationMultiplier = GameMath.Lerp(1f, steelDurationMultiplier, tierProgress);

            return brickbybrickModSystem.Config.GetConstructionActionSeconds() * durationMultiplier;
        }

        private void PlayStageSoundAtMidpoint(
            float secondsUsed,
            ItemSlot slot,
            EntityAgent byEntity,
            BlockPos pos,
            IPlayer player,
            ConstructionAction action)
        {
            if (action == ConstructionAction.Masonry)
            {
                PlayActionSoundAtMidpoint(secondsUsed, slot, byEntity, player, pos, BrickSounds, 20f);
            }
            else if (action == ConstructionAction.Mortar)
            {
                PlayActionSoundAtMidpoint(secondsUsed, slot, byEntity, player, pos, TrowelSounds, 12f);
            }
        }

        private void SpawnConstructionParticles(
            IWorldAccessor world,
            BlockPos pos,
            Block block,
            ConstructionAction action,
            string color,
            bool completed,
            double? surfaceHeight = null,
            bool includeMortar = false,
            ItemStack materialStack = null)
        {
            if (!brickbybrickModSystem.Config.Effects.EnableConstructionParticles) return;
            if (world?.Side != EnumAppSide.Server || pos == null) return;

            EmitConstructionParticles(world, pos, block, action, color, completed, 1f, surfaceHeight, includeMortar, materialStack);
        }

        private void SpawnStageParticlesDuringAction(
            float secondsUsed,
            ItemSlot slot,
            EntityAgent byEntity,
            BlockPos pos,
            Block block,
            ConstructionAction action,
            string color)
        {
            if (!ShouldSpawnActionParticles(secondsUsed, slot, byEntity, action)) return;

            EmitConstructionParticles(byEntity.World, pos, block, action, color, false, 0.35f, null, false, GetCourseMaterialStack(byEntity.World, pos, block));
        }

        private void SpawnPlacementParticlesDuringAction(
            float secondsUsed,
            ItemSlot slot,
            EntityAgent byEntity,
            BlockSelection blockSel,
            int toolMode)
        {
            if (!ShouldSpawnActionParticles(secondsUsed, slot, byEntity, ConstructionAction.Masonry)) return;
            IPlayer player = (byEntity as EntityPlayer)?.Player;
            if (!TryGetPlacementMaterial(player, byEntity, out _, out ItemStack materialStack, out _, out string color)) return;

            BlockPos targetPos = ResolvePlacementTarget(blockSel);
            Block placeBlock = ResolvePlacementBlock(byEntity.World, byEntity, blockSel, toolMode, null);
            EmitConstructionParticles(byEntity.World, targetPos, placeBlock, ConstructionAction.Masonry, color, false, 0.35f, 0.25, true, materialStack);
        }

        private bool ShouldSpawnActionParticles(
            float secondsUsed,
            ItemSlot slot,
            EntityAgent byEntity,
            ConstructionAction action)
        {
            if (!brickbybrickModSystem.Config.Effects.EnableConstructionParticles) return false;
            if (action == ConstructionAction.None) return false;
            if (slot?.Itemstack == null || byEntity?.World?.Side != EnumAppSide.Client) return false;

            float lastParticleTime = slot.Itemstack.Attributes.GetFloat("lastParticleTime", -1f);
            if (lastParticleTime >= 0 && secondsUsed - lastParticleTime < 0.25f) return false;

            slot.Itemstack.Attributes.SetFloat("lastParticleTime", secondsUsed);
            return true;
        }

        private void EmitConstructionParticles(
            IWorldAccessor world,
            BlockPos pos,
            Block block,
            ConstructionAction action,
            string color,
            bool completed,
            float quantityMultiplier,
            double? surfaceHeight = null,
            bool includeMortar = false,
            ItemStack materialStack = null)
        {
            const double ParticleLift = 1d / 64d;

            Cuboidf bounds = GetParticleBounds(world, pos, block);
            double particleY = pos.Y + (surfaceHeight ?? bounds.Y2) + ParticleLift;
            Vec3d minPos = new Vec3d(pos.X, particleY - 0.02, pos.Z);
            Vec3d maxPos = new Vec3d(pos.X + 1, particleY + 0.04, pos.Z + 1);
            Vec3d centerPos = new Vec3d(
                (minPos.X + maxPos.X) / 2,
                (minPos.Y + maxPos.Y) / 2,
                (minPos.Z + maxPos.Z) / 2
            );

            if (completed)
            {
                SpawnDustPuff(world, pos, centerPos, ScaleQuantity(42, quantityMultiplier), 0.5f);
                return;
            }

            if (action == ConstructionAction.Masonry)
            {
                SpawnBrickChipScatter(world, centerPos, ScaleQuantity(18, quantityMultiplier), materialStack, GetFallbackBrickParticleColor(color), 0.5f);

                if (includeMortar)
                {
                    SpawnMortarScatter(world, centerPos, ScaleQuantity(15, quantityMultiplier), 0.5f);
                }
            }
            else if (action == ConstructionAction.Mortar)
            {
                SpawnMortarScatter(world, centerPos, ScaleQuantity(30, quantityMultiplier), 0.5f);
            }
        }

        private int ScaleQuantity(int quantity, float multiplier)
        {
            return Math.Max(1, (int)Math.Ceiling(quantity * multiplier));
        }

        private Cuboidf GetParticleBounds(IWorldAccessor world, BlockPos pos, Block block)
        {
            // Use the rendered block's selection bounds so stage particles stay
            // near the visible course instead of spawning from empty space.
            Cuboidf[] boxes = block?.GetSelectionBoxes(world.BlockAccessor, pos);
            Cuboidf bounds = boxes != null && boxes.Length > 0 ? boxes[0] : new Cuboidf(0, 0, 0, 1, 1, 1);

            for (int i = 1; i < boxes?.Length; i++)
            {
                bounds = bounds.Clone();
                bounds.X1 = Math.Min(bounds.X1, boxes[i].X1);
                bounds.Y1 = Math.Min(bounds.Y1, boxes[i].Y1);
                bounds.Z1 = Math.Min(bounds.Z1, boxes[i].Z1);
                bounds.X2 = Math.Max(bounds.X2, boxes[i].X2);
                bounds.Y2 = Math.Max(bounds.Y2, boxes[i].Y2);
                bounds.Z2 = Math.Max(bounds.Z2, boxes[i].Z2);
            }

            return bounds;
        }

        private void SpawnBrickChipScatter(
            IWorldAccessor world,
            Vec3d centerPos,
            int quantity,
            ItemStack materialStack,
            int fallbackColor,
            float scale)
        {
            Random rand = world.Rand;

            for (int i = 0; i < quantity; i++)
            {
                Vec3d origin = OffsetFromCenter(centerPos, rand, 0.45);
                Vec3f velocity = DirectionalVelocity(centerPos, origin, rand, 0.12f, 0.22f, 0.025f, 0.075f);
                Vec3d minPos = new Vec3d(origin.X - 0.02, origin.Y - 0.01, origin.Z - 0.02);
                Vec3d maxPos = new Vec3d(origin.X + 0.02, origin.Y + 0.03, origin.Z + 0.02);

                if (materialStack != null)
                {
                    world.SpawnCubeParticles(origin, materialStack, 0.18f, 1, scale, null, velocity);
                }
                else
                {
                    world.SpawnParticles(1, fallbackColor, minPos, maxPos, velocity, velocity, 0.55f, 0.45f, scale, EnumParticleModel.Cube, null);
                }
            }
        }

        private void SpawnDustPuff(
            IWorldAccessor world,
            BlockPos blockPos,
            Vec3d centerPos,
            int quantity,
            float scale)
        {
            Random rand = world.Rand;

            for (int i = 0; i < quantity; i++)
            {
                Vec3d origin = OffsetFromCenter(centerPos, rand, 0.35);
                Vec3f velocity = DirectionalVelocity(centerPos, origin, rand, 0.12f, 0.22f, 0.035f, 0.11f);

                world.SpawnCubeParticles(blockPos, origin, 0.08f, 1, scale, null, velocity);
            }
        }

        private void SpawnMortarScatter(IWorldAccessor world, Vec3d centerPos, int quantity, float scale)
        {
            Random rand = world.Rand;
            Vec3d splashCenter = OffsetFromCenter(centerPos, rand, 0.25);

            for (int i = 0; i < quantity; i++)
            {
                Vec3d origin = OffsetFromCenter(splashCenter, rand, 0.4);
                Vec3f velocity = DirectionalVelocity(splashCenter, origin, rand, 0.05f, 0.12f, -0.075f, -0.02f);
                Vec3d minPos = new Vec3d(origin.X - 0.02, origin.Y + 0.04, origin.Z - 0.02);
                Vec3d maxPos = new Vec3d(origin.X + 0.02, origin.Y + 0.12, origin.Z + 0.02);

                world.SpawnParticles(
                    1,
                    GetMortarParticleColor(),
                    minPos,
                    maxPos,
                    velocity,
                    velocity,
                    0.55f,
                    0.45f,
                    scale,
                    EnumParticleModel.Cube,
                    null
                );
            }
        }

        private Vec3d OffsetFromCenter(Vec3d centerPos, Random rand, double radius)
        {
            double angle = rand.NextDouble() * GameMath.TWOPI;
            double distance = Math.Sqrt(rand.NextDouble()) * radius;

            return new Vec3d(
                centerPos.X + Math.Cos(angle) * distance,
                centerPos.Y,
                centerPos.Z + Math.Sin(angle) * distance
            );
        }

        private Vec3f DirectionalVelocity(
            Vec3d centerPos,
            Vec3d origin,
            Random rand,
            float minHorizontal,
            float maxHorizontal,
            float minVertical,
            float maxVertical)
        {
            double x = origin.X - centerPos.X;
            double z = origin.Z - centerPos.Z;
            double length = Math.Sqrt(x * x + z * z);

            if (length < 0.001)
            {
                double angle = rand.NextDouble() * GameMath.TWOPI;
                x = Math.Cos(angle);
                z = Math.Sin(angle);
                length = 1;
            }

            float horizontal = minHorizontal + (float)rand.NextDouble() * (maxHorizontal - minHorizontal);

            return new Vec3f(
                (float)(x / length) * horizontal,
                minVertical + (float)rand.NextDouble() * (maxVertical - minVertical),
                (float)(z / length) * horizontal
            );
        }

        private int GetMortarParticleColor()
        {
            return ColorUtil.ToRgba(255, 198, 190, 168);
        }

        private int GetFallbackBrickParticleColor(string color)
        {
            return color switch
            {
                "brown" => ColorUtil.ToRgba(255, 113, 68, 43),
                "darkbrown" => ColorUtil.ToRgba(255, 75, 48, 34),
                "fire" => ColorUtil.ToRgba(255, 127, 60, 41),
                "gray" => ColorUtil.ToRgba(255, 112, 105, 96),
                "orange" => ColorUtil.ToRgba(255, 173, 82, 39),
                "red" => ColorUtil.ToRgba(255, 139, 54, 39),
                "tan" => ColorUtil.ToRgba(255, 181, 143, 95),
                _ => ColorUtil.ToRgba(255, 142, 92, 64)
            };
        }

        private static void ConsumeOffhand(ItemSlot slot, int quantity)
        {
            if (slot == null || quantity <= 0) return;

            slot.TakeOut(quantity);
            slot.MarkDirty();
        }

        private void ConsumeMortar(ItemSlot slot, int quantity)
        {
            if (slot?.Itemstack == null || quantity <= 0) return;

            SetStoredAmount(slot, GetStoredAmount(slot.Itemstack) - quantity);
        }

        // Builder mode carries fractional cost forward on the trowel so one
        // mortar portion pays for exactly two normal one-portion actions.
        private void ConsumeConfiguredMortar(ItemSlot slot, int baseQuantity)
        {
            if (slot?.Itemstack == null || baseQuantity <= 0) return;

            const string CostRemainderAttribute = "mortarCostRemainder";
            float multiplier = brickbybrickModSystem.Config.GetMortarCostMultiplier();
            float totalCost = slot.Itemstack.Attributes.GetFloat(CostRemainderAttribute, 0.0f)
                + baseQuantity * multiplier;
            int wholeCost = (int)Math.Floor(totalCost);
            float remainder = totalCost - wholeCost;

            slot.Itemstack.Attributes.SetFloat(CostRemainderAttribute, remainder);
            ConsumeMortar(slot, wholeCost);
        }

        private BlockPos ResolvePlacementTarget(BlockSelection blockSel)
        {
            return blockSel.Position.AddCopy(blockSel.Face);
        }

        // Resolves the first construction stage for the selected placement mode.
        private Block ResolvePlacementBlock(
            IWorldAccessor world,
            EntityAgent byEntity,
            BlockSelection blockSel,
            int toolMode,
            string color)
        {
            if (!IsPlacementMode(toolMode)) return null;

            AssetLocation blockCode = new AssetLocation(MasonryCourseCode);
            Block placeBlock = world.BlockAccessor.GetBlock(blockCode);
            if (placeBlock == null)
            {
                world.Api.Logger.Event($"[TROWEL] Could not resolve placement block {blockCode}");
            }

            return placeBlock;
        }

        private bool TryCreatePlacementCourse(
            IWorldAccessor world,
            EntityAgent byEntity,
            BlockSelection blockSel,
            int toolMode,
            ItemStack materialStack,
            string family,
            string color,
            out BlockPos targetPos,
            out Block placeBlock,
            out Variants variants)
        {
            targetPos = null;
            placeBlock = null;
            variants = null;

            if (world == null || blockSel == null || byEntity == null) return false;
            if (!IsPlacementMode(toolMode)) return false;

            targetPos = ResolvePlacementTarget(blockSel);
            placeBlock = ResolvePlacementBlock(world, byEntity, blockSel, toolMode, color);
            if (placeBlock == null) return false;

            variants = CreatePlacementVariants(toolMode, blockSel, byEntity, family, color, materialStack);
            return variants != null;
        }

        private ItemStack CreateCourseStack(IWorldAccessor world, Variants variants)
        {
            ItemStack stack = new ItemStack(world.BlockAccessor.GetBlock(new AssetLocation(MasonryCourseCode)));
            variants.ToStack(stack);

            return stack;
        }

        private Variants CreatePlacementVariants(
            int toolMode,
            BlockSelection blockSel,
            EntityAgent byEntity,
            string family,
            string color,
            ItemStack materialStack)
        {
            Variants variants = new();
            variants.Set("family", family);
            variants.Set("state", "four");
            variants.Set("color", color);
            variants.Set("stage", "1");
            if (materialStack?.Collectible?.Code != null)
            {
                variants.Set("materialDomain", materialStack.Collectible.Code.Domain);
                variants.Set("materialPath", materialStack.Collectible.Code.Path);
            }

            if (toolMode == SlabMode)
            {
                variants.Set("shape", "slab");
                variants.Set("rotation", ResolveSlabRotationCode(blockSel, byEntity));
            }
            else if (toolMode == StairMode)
            {
                ResolveStairOrientationCodes(blockSel, byEntity, out string vertical, out string horizontal);
                variants.Set("shape", "stair");
                variants.Set("vertical", vertical);
                variants.Set("horizontal", horizontal);
            }
            else
            {
                BlockFacing facing = BlockFacing.HorizontalFromYaw(byEntity.Pos.Yaw);
                variants.Set("shape", "block");
                variants.Set("bond", facing.IsAxisWE ? "running" : "runningo");
            }

            return variants;
        }

        // Divides the selected face into the five regions shown by the slab
        // placement template, then resolves player-relative regions to world faces.
        private string ResolveSlabRotationCode(BlockSelection blockSel, EntityAgent byEntity)
        {
            Vec3d hitPosition = blockSel?.HitPosition;
            BlockFacing facing = BlockFacing.HorizontalFromYaw(byEntity.Pos.Yaw);
            if (hitPosition == null) return facing.Code;

            bool horizontalFace = blockSel.Face == BlockFacing.UP || blockSel.Face == BlockFacing.DOWN;
            if (!horizontalFace)
            {
                // Side-face template regions stay aligned with the selected
                // face even when the player's yaw crosses a cardinal boundary.
                facing = blockSel.Face.Opposite;
            }

            double horizontal = GetPlayerRelativeHitX(hitPosition, facing) - 0.5;
            double vertical = horizontalFace
                ? GetPlayerRelativeHitDepth(hitPosition, facing)
                : hitPosition.Y - 0.5;

            // The center square occupies the middle half of each axis. Outside
            // it, diagonal corner boundaries assign the aim to the nearest edge.
            if (Math.Abs(horizontal) <= 0.25 && Math.Abs(vertical) <= 0.25)
            {
                if (blockSel.Face == BlockFacing.UP) return BlockFacing.DOWN.Code;
                if (blockSel.Face == BlockFacing.DOWN) return BlockFacing.UP.Code;

                return blockSel.Face.Opposite.Code;
            }

            if (Math.Abs(vertical) >= Math.Abs(horizontal))
            {
                if (horizontalFace)
                {
                    return vertical > 0 ? facing.Code : facing.Opposite.Code;
                }

                return vertical > 0 ? BlockFacing.UP.Code : BlockFacing.DOWN.Code;
            }

            return horizontal > 0 ? facing.GetCW().Code : facing.GetCCW().Code;
        }

        // Converts world-local hit coordinates into left-to-right screen space
        // for the player's nearest cardinal viewing direction.
        private double GetPlayerRelativeHitX(Vec3d hitPosition, BlockFacing facing)
        {
            if (facing == BlockFacing.NORTH) return hitPosition.X;
            if (facing == BlockFacing.SOUTH) return 1 - hitPosition.X;
            if (facing == BlockFacing.EAST) return hitPosition.Z;

            return 1 - hitPosition.Z;
        }

        // Converts top and bottom face hits into near-to-far screen space so
        // each template edge selects the matching vertical slab orientation.
        private double GetPlayerRelativeHitDepth(Vec3d hitPosition, BlockFacing facing)
        {
            if (facing == BlockFacing.NORTH) return 0.5 - hitPosition.Z;
            if (facing == BlockFacing.SOUTH) return hitPosition.Z - 0.5;
            if (facing == BlockFacing.EAST) return hitPosition.X - 0.5;

            return 0.5 - hitPosition.X;
        }

        // Side clicks above the midpoint place an upside-down stair.
        private void ResolveStairOrientationCodes(BlockSelection blockSel, EntityAgent byEntity, out string vertical, out string horizontal)
        {
            BlockFacing clickedFace = blockSel?.Face ?? BlockFacing.UP;
            bool upperHalf = clickedFace.IsHorizontal && blockSel.HitPosition != null && blockSel.HitPosition.Y > 0.5;

            vertical = clickedFace == BlockFacing.DOWN || upperHalf ? "down" : "up";
            horizontal = BlockFacing.HorizontalFromYaw(byEntity.Pos.Yaw).Code;
        }

        private bool TryGetPlacementMaterial(
            IPlayer player,
            EntityAgent byEntity,
            out ItemSlot slot,
            out ItemStack stack,
            out string family,
            out string color)
        {
            slot = null;
            stack = null;
            family = null;
            color = null;

            if (!TryGetOffhandStack(player, out slot, out stack))
            {
                NotifyPlayerDebug(player, byEntity.World, Lang.Get("brickbybrick:notice-trowel-hold-burned-brick"));
                return false;
            }

            family = GetMasonryFamily(stack);
            if (string.IsNullOrEmpty(family))
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

        // Item attributes are the extension point for other mods. The path
        // fallbacks cover vanilla and common vanilla-style material variants.
        private string GetMasonryFamily(ItemStack stack)
        {
            string configuredFamily = stack?.Collectible?.Attributes?[FamilyAttribute].AsString();
            if (!string.IsNullOrEmpty(configuredFamily)) return configuredFamily;

            string path = stack?.Collectible?.Code?.Path;
            if (string.IsNullOrEmpty(path)) return null;
            if (path.StartsWith("burnedbrick-", StringComparison.Ordinal)) return "brick";
            if (path.StartsWith("refractorybrick-", StringComparison.Ordinal)) return "refractory";
            if (path.StartsWith("stonebrick-", StringComparison.Ordinal)) return "ashlar";
            if (path.StartsWith("stone-", StringComparison.Ordinal)) return "cobble";

            return null;
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
                case BuildMode:
                    if (!IsTrowelable(selectedBlock)) return null;

                    int nextStage = GetBlockStage(selectedBlock, byEntity.World, blockSel.Position) + 1;
                    return GetConstructionAction(nextStage) switch
                    {
                        ConstructionAction.Mortar => MortarUseAnimationCode,
                        ConstructionAction.Masonry => BrickUseAnimationCode,
                        _ => null
                    };

                case SlabMode:
                case StairMode:
                case BlockMode:
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
            return block?.Code?.Path == "masonrycourse"
                && block.Attributes?["trowelable"].AsBool(false) == true;
        }

        public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot)
        {
            base.GetHeldInteractionHelp(inSlot);
            int maxToolMode = toolModes == null ? 0 : Math.Max(0, toolModes.Length - 1);
            int toolMode = inSlot?.Itemstack == null ? 0 : Math.Min(maxToolMode, inSlot.Itemstack.Attributes.GetInt("toolMode"));
            WorldInteraction[] activeInteractions = toolMode == 0 ? interactions : placementInteractions;

            WorldInteraction actionInteraction = new WorldInteraction()
            {
                ActionLangCode = GetHeldHelpLangCode(toolMode),
                MouseButton = EnumMouseButton.Right,
                Itemstacks = activeInteractions?[0].Itemstacks
            };

            WorldInteraction modeInteraction = new WorldInteraction()
            {
                ActionLangCode = "brickbybrick:heldhelp-trowel-change-mode",
                HotKeyCodes = new string[] { "toolmodeselect" },
                MouseButton = EnumMouseButton.None
            };

            return activeInteractions == null || activeInteractions.Length == 0
                ? new WorldInteraction[] { modeInteraction }
                : new WorldInteraction[] { actionInteraction, modeInteraction };

        }

        private string GetHeldHelpLangCode(int toolMode)
        {
            return toolMode switch
            {
                SlabMode => "brickbybrick:heldhelp-trowel-slab",
                StairMode => "brickbybrick:heldhelp-trowel-stair",
                BlockMode => "brickbybrick:heldhelp-trowel-block",
                _ => "brickbybrick:heldhelp-trowel-build"
            };
        }

        private Variants GetCourseVariants(IWorldAccessor world, BlockPos pos, Block block)
        {
            BlockEntity blockEntity = world?.BlockAccessor?.GetBlockEntity(pos);
            BlockEntityBehaviorShapeTexturesFromAttributes behavior =
                blockEntity?.GetBehavior<BlockEntityBehaviorShapeTexturesFromAttributes>();

            if (behavior?.Variants?.Any == true)
            {
                Variants copiedVariants = new();
                copiedVariants.Set(behavior.Variants.GetElements());
                return copiedVariants;
            }

            return new Variants();
        }

        private void ApplyCourseState(IWorldAccessor world, BlockPos pos, Variants variants)
        {
            BlockEntity blockEntity = world?.BlockAccessor?.GetBlockEntity(pos);
            BlockEntityBehaviorShapeTexturesFromAttributes behavior =
                blockEntity?.GetBehavior<BlockEntityBehaviorShapeTexturesFromAttributes>();
            if (behavior == null) return;

            ItemStack courseStack = CreateCourseStack(world, variants);
            behavior.OnBlockPlaced(courseStack);
            blockEntity.MarkDirty(true);
        }

        private int GetBlockStage(Block block, IWorldAccessor world = null, BlockPos pos = null)
        {
            if (world != null && pos != null)
            {
                string stage = GetCourseVariants(world, pos, block).Get("stage");
                if (int.TryParse(stage, out int attributeStage))
                {
                    return attributeStage;
                }
            }

            return 0;
        }

        private string GetBlockColor(Block block, IWorldAccessor world = null, BlockPos pos = null)
        {
            if (world != null && pos != null)
            {
                string attributeColor = GetCourseVariants(world, pos, block).Get("color");
                if (!string.IsNullOrEmpty(attributeColor))
                {
                    return attributeColor;
                }
            }

            return null;
        }

        private ItemStack GetCourseMaterialStack(IWorldAccessor world, BlockPos pos, Block block)
        {
            if (world == null || pos == null) return null;

            Variants variants = GetCourseVariants(world, pos, block);
            string materialDomain = variants.Get("materialDomain");
            string materialPath = variants.Get("materialPath");

            if (string.IsNullOrEmpty(materialDomain) || string.IsNullOrEmpty(materialPath)) return null;

            Item item = world.GetItem(new AssetLocation(materialDomain, materialPath));
            return item == null ? null : new ItemStack(item);
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

        // Build mode alternates between mortar and brick-setting stages. Brick
        // stages need their exact material before any animation begins.
        private bool CanStartBuildStage(Block block, IWorldAccessor world, BlockPos pos, IPlayer player, EntityAgent byEntity)
        {
            int nextStage = GetBlockStage(block, world, pos) + 1;
            ConstructionAction action = GetConstructionAction(nextStage);

            if (action != ConstructionAction.Masonry) return true;

            return TryGetRequiredBrick(player, GetBlockColor(block, world, pos), byEntity, out _);
        }

        private bool HasMatchingMaterial(IPlayer player, string color, EntityAgent byEntity)
        {
            return TryGetRequiredBrick(player, color, byEntity, out _);
        }

        private bool TryGetRequiredBrick(IPlayer player, string color, EntityAgent byEntity, out ItemSlot slot)
        {
            slot = null;

            string requiredPath = $"burnedbrick-{color}";
            string requiredName = FormatRequiredBrickName(color);

            if (!TryGetOffhandStack(player, out slot, out ItemStack stack))
            {
                NotifyPlayerDebug(player, byEntity.World, Lang.Get("brickbybrick:notice-trowel-hold-required-brick", requiredName));
                return false;
            }

            string held = stack.Collectible?.Code?.Path;
            if (held != requiredPath)
            {
                NotifyPlayerDebug(player, byEntity.World, Lang.Get("brickbybrick:notice-trowel-wrong-brick", requiredName, FormatHeldItemName(stack)));
                return false;
            }

            return true;
        }

        private string FormatRequiredBrickName(string color)
        {
            return string.IsNullOrEmpty(color) ? Lang.Get("brickbybrick:notice-trowel-matching-brick") : $"{color} fired brick";
        }

        private string FormatHeldItemName(ItemStack stack)
        {
            return stack?.GetName() ?? Lang.Get("brickbybrick:notice-trowel-nothing");
        }

        private bool HasEnoughMortar(ItemSlot slot, EntityAgent byEntity)
        {
            IPlayer player = (byEntity as EntityPlayer)?.Player;
            if (player?.WorldData?.CurrentGameMode == EnumGameMode.Creative) return true;

            if (GetStoredAmount(slot.Itemstack) <= 0)
            {
                NotifyPlayerDebug(player, byEntity.World, Lang.Get("brickbybrick:notice-trowel-no-mortar"));
                return false;
            }

            return true;
        }

        private bool HasInteracted(ItemStack stack)
        {
            return stack?.Attributes?.GetBool("didInteract", false) == true;
        }

        private void SetInteracted(ItemStack stack, bool value)
        {
            stack?.Attributes?.SetBool("didInteract", value);
        }

        // Resolves staged slabs and stairs to their vanilla finished blocks.
        private AssetLocation ResolveNextBlock(Block block, int nextStage, string color, IWorldAccessor world = null, BlockPos pos = null)
        {
            if (block == null) return null;

            Variants variants = world != null && pos != null ? GetCourseVariants(world, pos, block) : null;
            string shape = variants?.Get("shape");

            if (shape == "slab")
            {
                // Staged slabs have three mod stages; the fourth advance
                // resolves directly to the matching vanilla slab block.
                if (nextStage <= 3)
                {
                    return new AssetLocation(MasonryCourseCode);
                }

                if (nextStage == 4)
                {
                    string slabColor = variants?.Get("color") ?? GetBlockColor(block);
                    string rot = variants?.Get("rotation") ?? BlockFacing.DOWN.Code;

                    return new AssetLocation("game", $"brickslabs-four-{slabColor}-{rot}-free");
                }

                return null;
            }

            if (shape == "stair")
            {
                // Staged stairs have five mod stages; the sixth advance
                // resolves directly to the matching vanilla stair block.
                if (nextStage <= 5)
                {
                    return new AssetLocation(MasonryCourseCode);
                }

                if (nextStage == 6)
                {
                    string stairColor = variants?.Get("color") ?? GetBlockColor(block);
                    string vertical = variants?.Get("vertical") ?? "up";
                    string horizontal = variants?.Get("horizontal") ?? BlockFacing.NORTH.Code;

                    return new AssetLocation("game", $"brickstairs-four-{stairColor}-{vertical}-{horizontal}-free");
                }

                return null;
            }

            if (nextStage <= 7)
            {
                return new AssetLocation(MasonryCourseCode);
            }

            if (nextStage == 8)
            {
                // Fire brick uses a custom finished block, while other colors
                // can drop the stage suffix from the course code.
                if (color == "fire")
                {
                    return new AssetLocation("brickbybrick:brickblock-good-fire");
                }

                string state = variants?.Get("state") ?? "four";
                string bond = variants?.Get("bond") ?? "running";
                return new AssetLocation("game", $"brickcourse-{state}-{bond}-{color}");
            }

            return null;
        }

        private bool IsCompletingFullBrickBlock(Block block, int nextStage)
        {
            return nextStage == 8
                && block?.Code?.Path == "masonrycourse";
        }

        // A full upward face is the normal support rule. Attributes provide a
        // stable hook for wet finished masonry and compatibility scaffolding.
        internal static bool HasStructuralSupport(IWorldAccessor world, BlockPos pos, IPlayer player)
        {
            if (!brickbybrickModSystem.Config.Construction.RequireStructuralSupport) return true;
            if (player?.WorldData?.CurrentGameMode == EnumGameMode.Creative) return true;
            if (world?.BlockAccessor == null || pos == null) return false;

            BlockPos supportPos = pos.DownCopy();
            Block supportBlock = world.BlockAccessor.GetBlock(supportPos);
            if (supportBlock?.Code == null || supportBlock.IsLiquid()) return false;

            if (supportBlock.Attributes?["structuralSupport"].AsBool(false) == true) return true;
            if (supportBlock.Attributes?["completeWetMasonry"].AsBool(false) == true) return true;

            // Scaffolding is optional. Domain recognition avoids assembly or
            // API dependencies and supports every block variant from the mod.
            if (supportBlock.Code.Domain == "scaffolding") return true;

            return world.BlockAccessor.IsSideSolid(
                supportPos.X,
                supportPos.Y,
                supportPos.Z,
                BlockFacing.UP);
        }

        // Reports the same failed support check used by placement and build
        // actions while keeping preview validation quiet.
        private bool ValidateStructuralSupport(IWorldAccessor world, BlockPos pos, IPlayer player)
        {
            if (HasStructuralSupport(world, pos, player)) return true;

            NotifyPlayerDebug(player, world, Lang.Get("brickbybrick:notice-trowel-unsupported"));
            return false;
        }

        private sealed class TrowelPlacementPreviewRenderer : IRenderer, IDisposable
        {
            private const float ContactFaceOffset = 0.015625f;
            private const string DefaultPreviewColor = "red";
            private const string NoMortarPreviewVariant = "nomortar";
            private const string NoMortarPreviewMeshKey = "placement-preview-nomortar";

            private readonly ICoreClientAPI capi;
            private readonly ItemTrowel trowel;
            private readonly Dictionary<string, MeshRef> meshRefs = new();

            public double RenderOrder => 0.55;
            public int RenderRange => 24;

            public TrowelPlacementPreviewRenderer(ICoreClientAPI capi, ItemTrowel trowel)
            {
                this.capi = capi;
                this.trowel = trowel;
            }

            public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
            {
                if (!brickbybrickModSystem.Config.Trowels.EnablePlacementPreview) return;
                if (!TryResolvePreview(out BlockPos targetPos, out Block courseBlock, out Variants previewVariants, out BlockFacing selectedFace, out bool hasSupport)) return;

                MeshRef meshRef = GetOrCreateMeshRef(courseBlock, previewVariants);
                if (meshRef == null) return;

                IRenderAPI rpi = capi.Render;
                float previewAlpha = brickbybrickModSystem.Config.Trowels.PlacementPreviewOpacity;
                Vec4f ghostTint = hasSupport
                    ? new Vec4f(1f, 1f, 1f, previewAlpha)
                    : new Vec4f(1f, 0.15f, 0.08f, previewAlpha);
                Vec3d cameraPos = capi.World.Player.Entity.CameraPos;
                IStandardShaderProgram shader = rpi.PreparedStandardShader(targetPos.X, targetPos.Y, targetPos.Z, ghostTint);
                Vec3f faceOffset = GetFaceOffset(selectedFace);

                shader.Tex2D = capi.BlockTextureAtlas.AtlasTextures[0].TextureId;
                shader.RgbaTint = ghostTint;
                shader.AlphaTest = 0.01f;
                shader.ExtraGlow = 96;
                shader.SsaoAttn = 0f;
                shader.OverlayOpacity = 0f;
                shader.ModelMatrix = Matrixf.Create()
                    .Translate(
                        (float)(targetPos.X - cameraPos.X) + faceOffset.X,
                        (float)(targetPos.Y - cameraPos.Y) + faceOffset.Y,
                        (float)(targetPos.Z - cameraPos.Z) + faceOffset.Z)
                    .Values;

                rpi.GlToggleBlend(true);
                rpi.GLDepthMask(false);
                rpi.GlEnableCullFace();
                rpi.RenderMesh(meshRef);
                rpi.GLDepthMask(true);
                rpi.GlToggleBlend(false);
                shader.Stop();
            }

            public void Dispose()
            {
                foreach (MeshRef meshRef in meshRefs.Values)
                {
                    capi.Render.DeleteMesh(meshRef);
                }

                meshRefs.Clear();
            }

            private MeshRef GetOrCreateMeshRef(Block block, Variants variants)
            {
                string key = $"{block.Code}-{variants}-{NoMortarPreviewMeshKey}";
                if (meshRefs.TryGetValue(key, out MeshRef meshRef)) return meshRef;

                BlockBehaviorShapeTexturesFromAttributes behavior = block.GetBehavior<BlockBehaviorShapeTexturesFromAttributes>();
                MeshData meshData;

                if (behavior != null)
                {
                    meshData = behavior.GetOrCreateMesh(
                        variants: variants,
                        overrideShape: null,
                        atBlockPos: null,
                        extraCacheKey: NoMortarPreviewMeshKey);
                }
                else
                {
                    capi.Tesselator.TesselateBlock(block, out meshData);
                }

                meshRef = capi.Render.UploadMesh(meshData);
                meshRefs[key] = meshRef;

                return meshRef;
            }

            private bool TryResolvePreview(out BlockPos targetPos, out Block courseBlock, out Variants previewVariants, out BlockFacing selectedFace, out bool hasSupport)
            {
                targetPos = null;
                courseBlock = null;
                previewVariants = null;
                selectedFace = BlockFacing.UP;
                hasSupport = false;

                IClientPlayer player = capi.World.Player;
                ItemSlot activeSlot = player?.InventoryManager?.ActiveHotbarSlot;
                BlockSelection blockSel = player?.CurrentBlockSelection;
                if (activeSlot?.Itemstack?.Collectible != trowel || blockSel == null) return false;

                int toolMode = trowel.GetToolMode(activeSlot, player, blockSel);
                if (!IsPlacementMode(toolMode)) return false;
                if (!TryResolveMaterial(player, out ItemStack materialStack, out string family, out string color)) return false;
                if (!trowel.TryCreatePlacementCourse(
                    capi.World,
                    player.Entity,
                    blockSel,
                    toolMode,
                    materialStack,
                    family,
                    color,
                    out targetPos,
                    out courseBlock,
                    out previewVariants))
                {
                    return false;
                }

                hasSupport = HasStructuralSupport(capi.World, targetPos, player);
                selectedFace = blockSel.Face ?? BlockFacing.UP;
                Block existingBlock = capi.World.BlockAccessor.GetBlock(targetPos);
                if (existingBlock == null || !existingBlock.IsReplacableBy(courseBlock)) return false;

                previewVariants.Set("preview", NoMortarPreviewVariant);
                return true;
            }

            // Preview checks stay silent so invalid offhand contents do not
            // spam normal placement notices while the player looks around.
            private bool TryResolveMaterial(IPlayer player, out ItemStack materialStack, out string family, out string color)
            {
                materialStack = null;
                family = null;
                color = null;

                if (!trowel.TryGetOffhandStack(player, out _, out materialStack))
                {
                    family = "brick";
                    color = DefaultPreviewColor;
                    return true;
                }

                family = trowel.GetMasonryFamily(materialStack);
                color = trowel.GetItemColor(materialStack);

                if (!string.IsNullOrEmpty(family) && !string.IsNullOrEmpty(color)) return true;

                family = "brick";
                color = DefaultPreviewColor;
                return true;
            }

            private Vec3f GetFaceOffset(BlockFacing face)
            {
                if (face == BlockFacing.DOWN) return new Vec3f(0, -ContactFaceOffset, 0);
                if (face == BlockFacing.NORTH) return new Vec3f(0, 0, -ContactFaceOffset);
                if (face == BlockFacing.SOUTH) return new Vec3f(0, 0, ContactFaceOffset);
                if (face == BlockFacing.WEST) return new Vec3f(-ContactFaceOffset, 0, 0);
                if (face == BlockFacing.EAST) return new Vec3f(ContactFaceOffset, 0, 0);

                return new Vec3f(0, ContactFaceOffset, 0);
            }
        }

    }
}
