using AttributeRenderingLibrary;
using brickbybrick.RealisticConstruction;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public const string RealisticOrientationAttribute = "brickbybrick:realisticOrientation";
        public const string RealisticVariantAttribute = "brickbybrick:realisticVariant";
        public const string SecondaryPlacementModifierHotKeyCode = "ctrl";
        public const int RealisticHalfBrickVariantCount = 9;
        private const string RealisticPlacementLogName = "brickbybrick-placement.log";
        private static readonly object RealisticPlacementLogLock = new();
        private static bool hasClientRealisticPlacementState;
        private static int clientRealisticOrientation;
        private static int clientRealisticVariant;
        private static string lastRealisticPreviewTraceKey = string.Empty;

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

            if (brickbybrickModSystem.Config.IsRealisticConstructionEnabled())
            {
                bool mortarAction = byEntity.Controls.Sneak || !TryGetRealisticMaterial(byPlayer, slot, out _, out _);
                if (mortarAction)
                {
                    if (HasEnoughMortar(slot, byEntity)) UpdateTrowelUseAnimation(slot, byEntity, byPlayer, blockSel);
                }
                else if (byEntity.World.Side == EnumAppSide.Server)
                {
                    // The wheel state is sent over the mod channel, while the
                    // held interaction arrives through the game channel.
                    // Delay one short callback so the server commits the pose
                    // that produced the client-side placement preview.
                    byEntity.World.Api.Event.RegisterCallback(_ =>
                    {
                        TryPlaceRealisticUnit(slot, byEntity, byPlayer, blockSel);
                    }, 25);
                }
                else
                {
                    brickbybrickModSystem system = byEntity.World.Api.ModLoader.GetModSystem<brickbybrickModSystem>();
                    system?.SendRealisticPlacementState(byPlayer);
                }

                handling = EnumHandHandling.PreventDefault;
                return;
            }

            // Placement modes own the interaction even when the selected block
            // is not trowelable because they place into the adjacent position.
            if (IsPlacementMode(toolMode))
            {
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

            if (brickbybrickModSystem.Config.IsRealisticConstructionEnabled())
            {
                return HandleRealisticMortar(secondsUsed, slot, byEntity, player, blockSel);
            }

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
            if (secondsUsed < brickbybrickModSystem.Config.GetConstructionActionSeconds()) return true;
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
            ConsumeConfiguredMortar(slot, brickbybrickModSystem.Config.Trowels.MortarCostPerAction);
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
            if (secondsUsed < brickbybrickModSystem.Config.GetConstructionActionSeconds()) return true;
            if (byEntity.World.Side != EnumAppSide.Server) return false;

            // Re-read the target after the timed action. Another player or a
            // block update may have changed it while this interaction ran.
            Block currentBlock = byEntity.World.BlockAccessor.GetBlock(pos);
            if (!IsTrowelable(currentBlock)) return false;
            if (GetBlockStage(currentBlock, byEntity.World, pos) != currentStage) return false;
            block = currentBlock;

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

            ConsumeConfiguredMortar(slot, brickbybrickModSystem.Config.Trowels.MortarCostPerAction);
            SetInteracted(slot.Itemstack, true);

            return false;
        }

        private void TryPlaceRealisticUnit(ItemSlot trowelSlot, EntityAgent byEntity, IPlayer player, BlockSelection blockSel)
        {
            if (!TryGetRealisticMaterial(player, trowelSlot, out ItemSlot materialSlot, out ItemStack materialStack)) return;

            string path = materialStack.Collectible?.Code?.Path ?? string.Empty;
            MasonryUnitKind kind = path == "testrammedearth"
                ? ResolveRealisticRammedEarthVariant(player) == 1 ? MasonryUnitKind.SmallRammedEarth : MasonryUnitKind.RammedEarth
                : path.StartsWith("halfbrick-", StringComparison.Ordinal)
                    ? MasonryUnitKind.HalfBrick
                    : path.StartsWith("burnedbrick-", StringComparison.Ordinal) && path != "burnedbrick-fire"
                        ? MasonryUnitKind.WholeBrick
                        : (MasonryUnitKind)(-1);
            if ((int)kind < 0) return;

            BlockPos targetPos = byEntity.World.BlockAccessor.GetBlock(blockSel.Position).Code?.Path == "realisticmasonry"
                ? blockSel.Position.Copy()
                : ResolvePlacementTarget(blockSel);
            bool targetsExistingCell = targetPos.Equals(blockSel.Position);
            ResolveRealisticGridOrigin(blockSel, ref targetPos, targetsExistingCell, out int gridX, out int gridZ);
            Block targetBlock = byEntity.World.BlockAccessor.GetBlock(targetPos);
            bool createdTargetBlock = false;
            BlockEntityRealisticMasonry initialEntity = targetBlock.Code?.Path == "realisticmasonry"
                ? byEntity.World.BlockAccessor.GetBlockEntity(targetPos) as BlockEntityRealisticMasonry
                : null;
            int layer = ResolveRealisticPlacementLayer(blockSel, targetsExistingCell, gridX, gridZ, initialEntity);
            MasonryUnitPlacement unit = new()
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = kind,
                VisualShape = ResolveRealisticVisualShape(kind, player),
                MaterialCode = path,
                Orientation = ResolveRealisticUnitOrientation(kind, player),
                Origin = new MasonryGridPosition(gridX, layer, gridZ)
            };
            StringBuilder placementTrace = new();
            AppendRealisticPlacementTraceHeader(placementTrace, "PLACE", byEntity.World.Side, blockSel, path, unit, targetPos, targetsExistingCell, gridX, gridZ, layer);
            TrySnapRealisticPlacement(byEntity.World.BlockAccessor, targetPos, blockSel, unit, placementTrace);
            CanonicalizePlacementOwner(ref targetPos, unit);
            placementTrace.AppendLine($"finalTarget={FormatBlockPos(targetPos)} finalUnit={FormatRealisticUnit(unit)}");
            targetBlock = byEntity.World.BlockAccessor.GetBlock(targetPos);

            if (targetBlock.Code?.Path != "realisticmasonry")
            {
                Block initialConstructionBlock = byEntity.World.GetBlock(new AssetLocation("brickbybrick:realisticmasonry"));
                if (initialConstructionBlock == null || !targetBlock.IsReplacableBy(initialConstructionBlock))
                {
                    placementTrace.AppendLine($"result=blocked targetBlock={targetBlock?.Code}");
                    LogRealisticPlacementTrace(byEntity.World.Api, placementTrace);
                    return;
                }

                byEntity.World.BlockAccessor.SetBlock(initialConstructionBlock.Id, targetPos);
                createdTargetBlock = true;
            }

            if (byEntity.World.BlockAccessor.GetBlockEntity(targetPos) is not BlockEntityRealisticMasonry entity)
            {
                placementTrace.AppendLine("result=blocked reason=missing-block-entity");
                LogRealisticPlacementTrace(byEntity.World.Api, placementTrace);
                CleanupCreatedEmptyTarget();
                return;
            }

            unit.OwnerBlockX = targetPos.X;
            unit.OwnerBlockY = targetPos.Y;
            unit.OwnerBlockZ = targetPos.Z;

            MasonryPlacementFailure placementFailure = GetProjectedPlacementFailure(byEntity.World.BlockAccessor, targetPos, entity, unit);
            if (placementFailure != MasonryPlacementFailure.None)
            {
                placementTrace.AppendLine($"result=blocked reason={placementFailure}");
                if (placementFailure == MasonryPlacementFailure.Frozen)
                {
                    NotifyPlayerDebug(player, byEntity.World, Lang.Get("brickbybrick:notice-realistic-frozen"));
                }
                else if (placementFailure == MasonryPlacementFailure.Unsupported)
                {
                    NotifyPlayerDebug(player, byEntity.World, Lang.Get("brickbybrick:notice-realistic-unsupported"));
                }

                CleanupCreatedEmptyTarget();
                LogRealisticPlacementTrace(byEntity.World.Api, placementTrace);
                return;
            }

            Dictionary<(int X, int Z), List<MasonryGridPosition>> neighborReservations = BuildNeighborReservations(unit);

            Block constructionBlock = byEntity.World.GetBlock(new AssetLocation("brickbybrick:realisticmasonry"));
            foreach ((int X, int Z) neighborOffset in GetNeighborOffsets(unit, neighborReservations))
            {
                BlockPos neighborPos = targetPos.AddCopy(neighborOffset.X, 0, neighborOffset.Z);
                Block neighborBlock = byEntity.World.BlockAccessor.GetBlock(neighborPos);
                if (neighborBlock.Code?.Path == "realisticmasonry")
                {
                    MasonryUnitPlacement neighborProjection = ProjectUnitIntoNeighbor(unit, neighborOffset);
                    if (byEntity.World.BlockAccessor.GetBlockEntity(neighborPos) is not BlockEntityRealisticMasonry neighborEntity
                        || !neighborEntity.CanReserve(neighborProjection))
                    {
                        placementTrace.AppendLine($"result=blocked reason=neighbor-reserve-failed neighbor={FormatBlockPos(neighborPos)} projection={FormatRealisticUnit(neighborProjection)}");
                        LogRealisticPlacementTrace(byEntity.World.Api, placementTrace);
                        CleanupCreatedEmptyTarget();
                        return;
                    }
                }
                else if (constructionBlock == null || !neighborBlock.IsReplacableBy(constructionBlock))
                {
                    placementTrace.AppendLine($"result=blocked reason=neighbor-not-replacable neighbor={FormatBlockPos(neighborPos)} block={neighborBlock?.Code}");
                    LogRealisticPlacementTrace(byEntity.World.Api, placementTrace);
                    CleanupCreatedEmptyTarget();
                    return;
                }
            }

            foreach ((int X, int Z) neighborOffset in GetNeighborOffsets(unit, neighborReservations))
            {
                BlockPos neighborPos = targetPos.AddCopy(neighborOffset.X, 0, neighborOffset.Z);
                if (byEntity.World.BlockAccessor.GetBlock(neighborPos).Code?.Path != "realisticmasonry")
                {
                    byEntity.World.BlockAccessor.SetBlock(constructionBlock.Id, neighborPos);
                }

                if (byEntity.World.BlockAccessor.GetBlockEntity(neighborPos) is BlockEntityRealisticMasonry neighborEntity)
                {
                    neighborEntity.Reserve(ProjectUnitIntoNeighbor(unit, neighborOffset));
                }
            }

            if (!entity.TryPlace(unit))
            {
                placementTrace.AppendLine("result=blocked reason=try-place-failed");
                LogRealisticPlacementTrace(byEntity.World.Api, placementTrace);
                CleanupCreatedEmptyTarget();
                return;
            }

            int materialCountBefore = materialSlot.Itemstack?.StackSize ?? 0;
            materialSlot.TakeOut(1);
            materialSlot.MarkDirty();
            PlayRandomSound(byEntity.World, targetPos, player, BrickSounds, brickbybrickModSystem.Config.Effects.ConstructionSoundRange);
            placementTrace.AppendLine($"inventory=consume material={path} count=1 before={materialCountBefore} after={materialSlot.Itemstack?.StackSize ?? 0}");
            placementTrace.AppendLine("result=placed");
            LogRealisticPlacementTrace(byEntity.World.Api, placementTrace);

            void CleanupCreatedEmptyTarget()
            {
                if (!createdTargetBlock) return;
                if (byEntity.World.BlockAccessor.GetBlockEntity(targetPos) is BlockEntityRealisticMasonry cleanupEntity
                    && cleanupEntity.State.Units.Count == 0
                    && cleanupEntity.State.ReservedUnits.Count == 0)
                {
                    byEntity.World.BlockAccessor.SetBlock(0, targetPos);
                }
            }
        }

        private static MasonryUnitPlacement ProjectUnitIntoNeighbor(MasonryUnitPlacement unit, (int X, int Z) neighborOffset)
        {
            return new MasonryUnitPlacement
            {
                Id = unit.Id,
                OwnerBlockX = unit.OwnerBlockX,
                OwnerBlockY = unit.OwnerBlockY,
                OwnerBlockZ = unit.OwnerBlockZ,
                Kind = unit.Kind,
                VisualShape = unit.VisualShape,
                MaterialCode = unit.MaterialCode,
                Orientation = unit.Orientation,
                Origin = new MasonryGridPosition(unit.Origin.X - neighborOffset.X * 4, unit.Origin.Y, unit.Origin.Z - neighborOffset.Z * 4),
                OffsetX = unit.OffsetX,
                OffsetZ = unit.OffsetZ,
                MortaredPositions = unit.MortaredPositions
                    .Select(position => new MasonryGridPosition(position.X - neighborOffset.X * 4, position.Y, position.Z - neighborOffset.Z * 4))
                    .ToHashSet()
            };
        }

        // A snapped diagonal may extend into a neighboring block cell. Keep
        // its center fixed while making that cell the sole authoritative owner.
        private static void CanonicalizePlacementOwner(ref BlockPos targetPos, MasonryUnitPlacement unit)
        {
            MasonryVoxelGeometry.GetUnitCenter(unit, out float centerX, out float centerZ);
            int offsetX = (int)Math.Floor(centerX);
            int offsetZ = (int)Math.Floor(centerZ);
            if (offsetX == 0 && offsetZ == 0) return;

            targetPos = targetPos.AddCopy(offsetX, 0, offsetZ);
            unit.Origin = new MasonryGridPosition(
                unit.Origin.X - offsetX * 4,
                unit.Origin.Y,
                unit.Origin.Z - offsetZ * 4);
        }

        private static Dictionary<(int X, int Z), List<MasonryGridPosition>> BuildNeighborReservations(MasonryUnitPlacement unit)
        {
            Dictionary<(int X, int Z), List<MasonryGridPosition>> neighborReservations = new();
            foreach (MasonryGridPosition position in MasonryVoxelGeometry.GetReservationFootprint(unit))
            {
                int offsetX = (int)Math.Floor(position.X / 4d);
                int offsetZ = (int)Math.Floor(position.Z / 4d);
                if (offsetX == 0 && offsetZ == 0) continue;

                (int X, int Z) key = (offsetX, offsetZ);
                if (!neighborReservations.TryGetValue(key, out List<MasonryGridPosition> positions))
                {
                    positions = new List<MasonryGridPosition>();
                    neighborReservations[key] = positions;
                }

                positions.Add(new MasonryGridPosition(
                    GameMath.Mod(position.X, 4),
                    position.Y,
                    GameMath.Mod(position.Z, 4)));
            }

            return neighborReservations;
        }

        private static IEnumerable<(int X, int Z)> GetNeighborOffsets(MasonryUnitPlacement unit, Dictionary<(int X, int Z), List<MasonryGridPosition>> reservations)
        {
            HashSet<(int X, int Z)> offsets = reservations.Keys.ToHashSet();
            foreach ((int X, int Y, int Z) voxel in MasonryVoxelGeometry.GetVoxels(unit))
            {
                int offsetX = (int)Math.Floor(voxel.X / 16d);
                int offsetZ = (int)Math.Floor(voxel.Z / 16d);
                if (offsetX != 0 || offsetZ != 0) offsets.Add((offsetX, offsetZ));
            }

            return offsets;
        }

        private static MasonryPlacementFailure GetProjectedPlacementFailure(IBlockAccessor blockAccessor, BlockPos targetPos, BlockEntityRealisticMasonry entity, MasonryUnitPlacement unit)
        {
            MasonryPlacementFailure localFailure = entity?.GetPlacementFailure(unit) ?? MasonryPlacementFailure.None;
            if (localFailure != MasonryPlacementFailure.None) return localFailure;

            if (HasProjectedNeighborOverlap(blockAccessor, targetPos, unit)) return MasonryPlacementFailure.Occupied;
            return MasonryPlacementFailure.None;
        }

        private static bool HasProjectedNeighborOverlap(IBlockAccessor blockAccessor, BlockPos targetPos, MasonryUnitPlacement candidate)
        {
            HashSet<(int X, int Y, int Z)> candidateVoxels = MasonryVoxelGeometry.GetVoxels(candidate).ToHashSet();
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
            {
                if (offsetX == 0 && offsetZ == 0) continue;

                BlockPos neighborPos = targetPos.AddCopy(offsetX, 0, offsetZ);
                if (blockAccessor.GetBlock(neighborPos)?.Code?.Path != "realisticmasonry"
                    || blockAccessor.GetBlockEntity(neighborPos) is not BlockEntityRealisticMasonry neighborEntity)
                {
                    continue;
                }

                foreach (MasonryUnitPlacement neighborUnit in neighborEntity.State.Units.Concat(neighborEntity.State.ReservedUnits))
                {
                    if (neighborUnit.Id == candidate.Id) continue;
                    MasonryUnitPlacement projected = ProjectAnchorIntoTarget(neighborUnit, offsetX, offsetZ);
                    if (MasonryVoxelGeometry.GetVoxels(projected).Any(candidateVoxels.Contains)) return true;
                }
            }

            return false;
        }

        private static void ResolveRealisticGridOrigin(BlockSelection blockSel, ref BlockPos targetPos, bool targetsExistingCell, out int gridX, out int gridZ)
        {
            double hitX = blockSel.HitPosition.X;
            double hitZ = blockSel.HitPosition.Z;
            if (targetsExistingCell && blockSel.Face?.IsHorizontal == true)
            {
                hitX += blockSel.Face.Normali.X * 0.251;
                hitZ += blockSel.Face.Normali.Z * 0.251;
            }

            int rawGridX = (int)Math.Floor(hitX * 4);
            int rawGridZ = (int)Math.Floor(hitZ * 4);
            if (targetsExistingCell)
            {
                int offsetX = (int)Math.Floor(rawGridX / 4d);
                int offsetZ = (int)Math.Floor(rawGridZ / 4d);
                if (offsetX != 0 || offsetZ != 0) targetPos = targetPos.AddCopy(offsetX, 0, offsetZ);
                gridX = GameMath.Mod(rawGridX, 4);
                gridZ = GameMath.Mod(rawGridZ, 4);
                return;
            }

            gridX = GameMath.Clamp(rawGridX, 0, 3);
            gridZ = GameMath.Clamp(rawGridZ, 0, 3);
        }

        // Selection boxes cover the whole masonry block, including empty
        // quarter-cells. Only step upward when the ray actually hit a unit.
        private static int ResolveRealisticPlacementLayer(
            BlockSelection blockSel,
            bool targetsExistingCell,
            int gridX,
            int gridZ,
            BlockEntityRealisticMasonry entity)
        {
            if (!targetsExistingCell || entity == null) return 0;

            int contactX = GameMath.Mod((int)Math.Floor((blockSel.HitPosition.X - blockSel.Face.Normali.X * 0.001) * MasonryVoxelGeometry.Resolution), MasonryVoxelGeometry.Resolution);
            int contactY = GameMath.Clamp((int)Math.Floor((blockSel.HitPosition.Y - blockSel.Face.Normali.Y * 0.001) * MasonryVoxelGeometry.Resolution), 0, MasonryVoxelGeometry.Resolution - 1);
            int contactZ = GameMath.Mod((int)Math.Floor((blockSel.HitPosition.Z - blockSel.Face.Normali.Z * 0.001) * MasonryVoxelGeometry.Resolution), MasonryVoxelGeometry.Resolution);
            bool hitOccupiedGeometry = entity.State.Units.Any(unit => MasonryVoxelGeometry.GetVoxels(unit)
                .Any(voxel => voxel.X == contactX && voxel.Y == contactY && voxel.Z == contactZ));

            if (!hitOccupiedGeometry) return 0;

            int layer = contactY / 4;
            if (blockSel.Face == BlockFacing.UP) layer++;
            return GameMath.Clamp(layer, 0, 3);
        }

        private static void TrySnapRealisticPlacement(IBlockAccessor blockAccessor, BlockPos targetPos, BlockSelection blockSel, MasonryUnitPlacement unit, StringBuilder trace = null)
        {
            if (unit.Kind is MasonryUnitKind.RammedEarth or MasonryUnitKind.SmallRammedEarth) return;
            if (!brickbybrickModSystem.Config.Realism.EnableDiagonalPlacement
                && (unit.IsDiagonal || unit.VisualShape == MasonryVisualShape.TriangleWedge)) return;

            List<DiagonalSnapCandidate> candidates = new();
            foreach (MasonryUnitPlacement anchor in CollectPlacementAnchors(blockAccessor, targetPos, unit.Origin.Y))
            {
                AddAnchorPlacementCandidates(candidates, anchor, unit);
            }

            float maxSnapDistance = blockSel.Face?.IsHorizontal == true ? 0.75f : 0.9f;
            float maxSnapDistanceSquared = maxSnapDistance * maxSnapDistance;
            bool rawValid = IsSnapCandidateValid(blockAccessor, targetPos, unit);
            float rawDistance = DistanceToHit(unit, blockSel);
            List<EvaluatedSnapCandidate> evaluatedCandidates = candidates
                .Select((candidate, index) =>
                {
                    float distance = DistanceToHit(candidate.Unit, blockSel);
                    bool inRange = distance <= maxSnapDistanceSquared;
                    bool valid = inRange && IsSnapCandidateValid(blockAccessor, targetPos, candidate.Unit);
                    return new EvaluatedSnapCandidate(candidate, index, distance, inRange, valid);
                })
                .ToList();
            EvaluatedSnapCandidate best = evaluatedCandidates
                .Where(candidate => candidate.Valid)
                .OrderBy(candidate => candidate.DistanceSquared + candidate.Candidate.Priority * 0.015f)
                .FirstOrDefault();

            if (rawValid && (best.Candidate.Unit == null || rawDistance <= best.DistanceSquared - 0.01f))
            {
                AppendSnapCandidateTrace(trace, candidates.Count, maxSnapDistanceSquared, evaluatedCandidates, rawValid, rawDistance, default);
                return;
            }

            AppendSnapCandidateTrace(trace, candidates.Count, maxSnapDistanceSquared, evaluatedCandidates, rawValid, rawDistance, best.Candidate);
            if (best.Candidate.Unit == null) return;

            unit.Origin = best.Candidate.Unit.Origin;
            unit.OffsetX = best.Candidate.Unit.OffsetX;
            unit.OffsetZ = best.Candidate.Unit.OffsetZ;
            unit.Orientation = best.Candidate.Unit.Orientation;
            unit.VisualShape = best.Candidate.Unit.VisualShape;
        }

        private readonly struct DiagonalSnapCandidate
        {
            public DiagonalSnapCandidate(MasonryUnitPlacement unit, int priority, string relation, MasonryUnitPlacement anchor)
            {
                Unit = unit;
                Priority = priority;
                Relation = relation;
                Anchor = anchor;
            }

            public MasonryUnitPlacement Unit { get; }

            public int Priority { get; }

            public string Relation { get; }

            public MasonryUnitPlacement Anchor { get; }
        }

        private readonly struct EvaluatedSnapCandidate
        {
            public EvaluatedSnapCandidate(DiagonalSnapCandidate candidate, int index, float distanceSquared, bool inRange, bool valid)
            {
                Candidate = candidate;
                Index = index;
                DistanceSquared = distanceSquared;
                InRange = inRange;
                Valid = valid;
            }

            public DiagonalSnapCandidate Candidate { get; }

            public int Index { get; }

            public float DistanceSquared { get; }

            public bool InRange { get; }

            public bool Valid { get; }
        }

        private static bool IsSnapCandidateValid(IBlockAccessor blockAccessor, BlockPos targetPos, MasonryUnitPlacement unit)
        {
            BlockPos ownerPos = targetPos.Copy();
            CanonicalizePlacementOwner(ref ownerPos, unit);
            BlockEntityRealisticMasonry ownerEntity = blockAccessor.GetBlock(ownerPos)?.Code?.Path == "realisticmasonry"
                ? blockAccessor.GetBlockEntity(ownerPos) as BlockEntityRealisticMasonry
                : null;
            if (GetProjectedPlacementFailure(blockAccessor, ownerPos, ownerEntity, unit) != MasonryPlacementFailure.None) return false;

            Dictionary<(int X, int Z), List<MasonryGridPosition>> neighborReservations = BuildNeighborReservations(unit);
            foreach ((int X, int Z) neighborOffset in GetNeighborOffsets(unit, neighborReservations))
            {
                BlockPos neighborPos = ownerPos.AddCopy(neighborOffset.X, 0, neighborOffset.Z);
                if (blockAccessor.GetBlock(neighborPos)?.Code?.Path != "realisticmasonry") continue;

                MasonryUnitPlacement neighborProjection = ProjectUnitIntoNeighbor(unit, neighborOffset);
                if (blockAccessor.GetBlockEntity(neighborPos) is not BlockEntityRealisticMasonry neighborEntity
                    || !neighborEntity.CanReserve(neighborProjection))
                {
                    return false;
                }
            }

            return true;
        }

        private static IEnumerable<MasonryUnitPlacement> CollectPlacementAnchors(IBlockAccessor blockAccessor, BlockPos targetPos, int layer)
        {
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
            {
                BlockPos anchorPos = targetPos.AddCopy(offsetX, 0, offsetZ);
                if (blockAccessor.GetBlock(anchorPos)?.Code?.Path != "realisticmasonry"
                    || blockAccessor.GetBlockEntity(anchorPos) is not BlockEntityRealisticMasonry anchorEntity)
                {
                    continue;
                }

                foreach (MasonryUnitPlacement anchor in anchorEntity.State.Units.Concat(anchorEntity.State.ReservedUnits).Where(existing => existing.Origin.Y == layer))
                {
                    yield return ProjectAnchorIntoTarget(anchor, offsetX, offsetZ);
                }
            }
        }

        private static MasonryUnitPlacement ProjectAnchorIntoTarget(MasonryUnitPlacement anchor, int blockOffsetX, int blockOffsetZ)
        {
            return new MasonryUnitPlacement
            {
                Id = anchor.Id,
                OwnerBlockX = anchor.OwnerBlockX,
                OwnerBlockY = anchor.OwnerBlockY,
                OwnerBlockZ = anchor.OwnerBlockZ,
                Kind = anchor.Kind,
                VisualShape = anchor.VisualShape,
                MaterialCode = anchor.MaterialCode,
                Orientation = anchor.Orientation,
                Origin = new MasonryGridPosition(anchor.Origin.X + blockOffsetX * 4, anchor.Origin.Y, anchor.Origin.Z + blockOffsetZ * 4),
                OffsetX = anchor.OffsetX,
                OffsetZ = anchor.OffsetZ,
                MortaredPositions = anchor.MortaredPositions
                    .Select(position => new MasonryGridPosition(position.X + blockOffsetX * 4, position.Y, position.Z + blockOffsetZ * 4))
                    .ToHashSet()
            };
        }

        private static void AddAnchorPlacementCandidates(List<DiagonalSnapCandidate> candidates, MasonryUnitPlacement anchor, MasonryUnitPlacement unit)
        {
            if (anchor.Kind is MasonryUnitKind.RammedEarth or MasonryUnitKind.SmallRammedEarth) return;

            MasonryVoxelGeometry.GetUnitAxes(
                unit,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out float sourceHalfLength,
                out float sourceHalfWidth);
            MasonryVoxelGeometry.GetUnitAxes(
                anchor,
                out float anchorCenterX,
                out float anchorCenterZ,
                out float anchorDirectionX,
                out float anchorDirectionZ,
                out float anchorPerpendicularX,
                out float anchorPerpendicularZ,
                out float anchorHalfLength,
                out float anchorHalfWidth);

            foreach (MasonryOrientation orientation in GetSnapAxisOrientations(unit.Orientation))
            {
                MasonryVoxelGeometry.GetDirection(orientation, out float unitDirectionX, out float unitDirectionZ);
                float unitPerpendicularX = -unitDirectionZ;
                float unitPerpendicularZ = unitDirectionX;
                float unitHalfLength = sourceHalfLength;
                float unitHalfWidth = sourceHalfWidth;
                float axisDot = MathF.Abs(anchorDirectionX * unitDirectionX + anchorDirectionZ * unitDirectionZ);
                float perpendicularDot = MathF.Abs(anchorPerpendicularX * unitDirectionX + anchorPerpendicularZ * unitDirectionZ);

                if (axisDot > 0.98f)
                {
                    AddCandidate(candidates, unit, orientation, anchor, "end", 0, anchorCenterX + anchorDirectionX * (anchorHalfLength + unitHalfLength), anchorCenterZ + anchorDirectionZ * (anchorHalfLength + unitHalfLength));
                    AddCandidate(candidates, unit, orientation, anchor, "end", 0, anchorCenterX - anchorDirectionX * (anchorHalfLength + unitHalfLength), anchorCenterZ - anchorDirectionZ * (anchorHalfLength + unitHalfLength));

                    float sideSeparation = anchorHalfWidth + unitHalfWidth;
                    AddSideCandidates(candidates, unit, orientation, anchor, anchorCenterX, anchorCenterZ, anchorPerpendicularX, anchorPerpendicularZ, anchorDirectionX, anchorDirectionZ, sideSeparation, 0, "side", 1);
                    float runningOffset = MathF.Min(anchorHalfLength, unitHalfLength);
                    AddSideCandidates(candidates, unit, orientation, anchor, anchorCenterX, anchorCenterZ, anchorPerpendicularX, anchorPerpendicularZ, anchorDirectionX, anchorDirectionZ, sideSeparation, runningOffset, "side-offset", 1);
                }

                if (perpendicularDot > 0.98f)
                {
                    AddPerpendicularCandidates(candidates, unit, orientation, anchor, anchorCenterX, anchorCenterZ, anchorDirectionX, anchorDirectionZ, anchorPerpendicularX, anchorPerpendicularZ, anchorHalfLength, anchorHalfWidth, unitHalfLength, unitHalfWidth);
                }

                if (unit.IsDiagonal || unit.VisualShape == MasonryVisualShape.TriangleWedge || anchor.IsDiagonal)
                {
                    foreach ((float cornerX, float cornerZ) in MasonryVoxelGeometry.GetUnitCorners(anchor))
                    foreach ((float offsetX, float offsetZ) in GetCornerOffsets(unitDirectionX, unitDirectionZ, unitPerpendicularX, unitPerpendicularZ, unitHalfLength, unitHalfWidth))
                    {
                        AddCandidate(candidates, unit, orientation, anchor, "corner", 3, cornerX - offsetX, cornerZ - offsetZ);
                    }
                }
            }
        }

        private static void AddSideCandidates(
            List<DiagonalSnapCandidate> candidates,
            MasonryUnitPlacement unit,
            MasonryOrientation orientation,
            MasonryUnitPlacement anchor,
            float anchorCenterX,
            float anchorCenterZ,
            float perpendicularX,
            float perpendicularZ,
            float directionX,
            float directionZ,
            float sideSeparation,
            float longitudinalOffset,
            string relation,
            int priority)
        {
            int[] sideSigns = { -1, 1 };
            int[] offsetSigns = longitudinalOffset > 0.0001f ? new[] { -1, 1 } : new[] { 0 };
            foreach (int sideSign in sideSigns)
            foreach (int offsetSign in offsetSigns)
            {
                AddCandidate(
                    candidates,
                    unit,
                    orientation,
                    anchor,
                    relation,
                    priority,
                    anchorCenterX + perpendicularX * sideSeparation * sideSign + directionX * longitudinalOffset * offsetSign,
                    anchorCenterZ + perpendicularZ * sideSeparation * sideSign + directionZ * longitudinalOffset * offsetSign);
            }
        }

        private static void AddPerpendicularCandidates(
            List<DiagonalSnapCandidate> candidates,
            MasonryUnitPlacement unit,
            MasonryOrientation orientation,
            MasonryUnitPlacement anchor,
            float anchorCenterX,
            float anchorCenterZ,
            float anchorDirectionX,
            float anchorDirectionZ,
            float anchorPerpendicularX,
            float anchorPerpendicularZ,
            float anchorHalfLength,
            float anchorHalfWidth,
            float unitHalfLength,
            float unitHalfWidth)
        {
            int[] signs = { -1, 1 };
            float[] sideAlongOffsets = { 0, anchorHalfLength - unitHalfWidth, -anchorHalfLength + unitHalfWidth };
            foreach (int sideSign in signs)
            foreach (float alongOffset in sideAlongOffsets)
            {
                AddCandidate(
                    candidates,
                    unit,
                    orientation,
                    anchor,
                    "perpendicular",
                    1,
                    anchorCenterX + anchorPerpendicularX * (anchorHalfWidth + unitHalfLength) * sideSign + anchorDirectionX * alongOffset,
                    anchorCenterZ + anchorPerpendicularZ * (anchorHalfWidth + unitHalfLength) * sideSign + anchorDirectionZ * alongOffset);
            }

            float[] endAcrossOffsets = { 0, anchorHalfWidth - unitHalfLength, -anchorHalfWidth + unitHalfLength };
            foreach (int endSign in signs)
            foreach (float acrossOffset in endAcrossOffsets)
            {
                AddCandidate(
                    candidates,
                    unit,
                    orientation,
                    anchor,
                    "perpendicular",
                    2,
                    anchorCenterX + anchorDirectionX * (anchorHalfLength + unitHalfWidth) * endSign + anchorPerpendicularX * acrossOffset,
                    anchorCenterZ + anchorDirectionZ * (anchorHalfLength + unitHalfWidth) * endSign + anchorPerpendicularZ * acrossOffset);
            }
        }

        private static void AddCandidate(
            List<DiagonalSnapCandidate> candidates,
            MasonryUnitPlacement unit,
            MasonryOrientation orientation,
            MasonryUnitPlacement anchor,
            string relation,
            int priority,
            float centerX,
            float centerZ)
        {
            MasonryUnitPlacement candidate = CreatePlacementCandidate(unit, orientation, centerX, centerZ);
            if (candidates.Any(existing => IsSameSnapCandidate(existing.Unit, candidate))) return;
            candidates.Add(new DiagonalSnapCandidate(candidate, priority, relation, anchor));
        }

        private static bool IsSameSnapCandidate(MasonryUnitPlacement first, MasonryUnitPlacement second)
        {
            return first != null
                && second != null
                && first.Kind == second.Kind
                && first.VisualShape == second.VisualShape
                && first.Orientation == second.Orientation
                && first.Origin.X == second.Origin.X
                && first.Origin.Y == second.Origin.Y
                && first.Origin.Z == second.Origin.Z
                && MathF.Abs(first.OffsetX - second.OffsetX) < 0.001f
                && MathF.Abs(first.OffsetZ - second.OffsetZ) < 0.001f;
        }

        private static IEnumerable<MasonryOrientation> GetSnapAxisOrientations(MasonryOrientation orientation)
        {
            yield return orientation;

            MasonryOrientation opposite = GetOppositeOrientation(orientation);
            if (opposite != orientation) yield return opposite;
        }

        private static MasonryOrientation GetOppositeOrientation(MasonryOrientation orientation)
        {
            return orientation switch
            {
                MasonryOrientation.East => MasonryOrientation.West,
                MasonryOrientation.South => MasonryOrientation.North,
                MasonryOrientation.West => MasonryOrientation.East,
                MasonryOrientation.North => MasonryOrientation.South,
                MasonryOrientation.SouthEast => MasonryOrientation.NorthWest,
                MasonryOrientation.SouthWest => MasonryOrientation.NorthEast,
                MasonryOrientation.NorthWest => MasonryOrientation.SouthEast,
                MasonryOrientation.NorthEast => MasonryOrientation.SouthWest,
                _ => orientation
            };
        }

        private static IEnumerable<(float X, float Z)> GetCornerOffsets(
            float directionX,
            float directionZ,
            float perpendicularX,
            float perpendicularZ,
            float halfLength,
            float halfWidth)
        {
            int[] signs = { -1, 1 };
            foreach (int lengthSign in signs)
            foreach (int widthSign in signs)
            {
                yield return (
                    directionX * halfLength * lengthSign + perpendicularX * halfWidth * widthSign,
                    directionZ * halfLength * lengthSign + perpendicularZ * halfWidth * widthSign);
            }
        }

        private static MasonryUnitPlacement CreatePlacementCandidate(MasonryUnitPlacement source, MasonryOrientation orientation, float centerX, float centerZ)
        {
            MasonryUnitPlacement candidate = new()
            {
                Id = source.Id,
                OwnerBlockX = source.OwnerBlockX,
                OwnerBlockY = source.OwnerBlockY,
                OwnerBlockZ = source.OwnerBlockZ,
                Kind = source.Kind,
                VisualShape = source.VisualShape,
                MaterialCode = source.MaterialCode,
                Orientation = orientation,
                Origin = new MasonryGridPosition(source.Origin.X, source.Origin.Y, source.Origin.Z),
                MortaredPositions = source.MortaredPositions.ToHashSet()
            };
            MasonryVoxelGeometry.SetUnitCenter(candidate, centerX, centerZ);
            return candidate;
        }

        private static float DistanceToHit(MasonryUnitPlacement unit, BlockSelection blockSel)
        {
            MasonryVoxelGeometry.GetUnitCenter(unit, out float centerX, out float centerZ);
            return DistanceSquared((float)blockSel.HitPosition.X, (float)blockSel.HitPosition.Z, centerX, centerZ);
        }

        private static float DistanceSquared(float firstX, float firstZ, float secondX, float secondZ)
        {
            float deltaX = firstX - secondX;
            float deltaZ = firstZ - secondZ;
            return deltaX * deltaX + deltaZ * deltaZ;
        }

        private static void AppendRealisticPlacementTraceHeader(
            StringBuilder trace,
            string phase,
            EnumAppSide side,
            BlockSelection blockSel,
            string materialPath,
            MasonryUnitPlacement unit,
            BlockPos targetPos,
            bool targetsExistingCell,
            int gridX,
            int gridZ,
            int layer)
        {
            if (trace == null) return;

            trace.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {phase} side={side}");
            trace.AppendLine($"selection pos={FormatBlockPos(blockSel?.Position)} face={blockSel?.Face?.Code ?? "null"} hit={FormatVec(blockSel?.HitPosition)}");
            trace.AppendLine($"material={materialPath} selectedUnit={FormatRealisticUnit(unit)}");
            trace.AppendLine($"resolvedTarget={FormatBlockPos(targetPos)} existingCell={targetsExistingCell} grid=({gridX},{gridZ}) layer={layer}");
        }

        private static void AppendSnapCandidateTrace(
            StringBuilder trace,
            int candidateCount,
            float maxDistanceSquared,
            List<EvaluatedSnapCandidate> candidates,
            bool rawValid,
            float rawDistanceSquared,
            DiagonalSnapCandidate chosen)
        {
            if (trace == null) return;

            trace.AppendLine($"snap candidates={candidateCount} maxDistanceSquared={maxDistanceSquared:0.####} rawValid={rawValid} rawDistanceSquared={rawDistanceSquared:0.####}");
            if (candidateCount == 0) return;

            int chosenIndex = chosen.Unit == null
                ? -1
                : candidates.FindIndex(candidate => ReferenceEquals(candidate.Candidate.Unit, chosen.Unit));
            foreach (EvaluatedSnapCandidate candidate in candidates.Take(32))
            {
                AppendSnapCandidateLine(trace, candidate, candidate.Index == chosenIndex);
            }

            if (candidates.Count > 32)
            {
                trace.AppendLine($"snap omitted={candidates.Count - 32}");
                if (chosenIndex >= 32)
                {
                    AppendSnapCandidateLine(trace, candidates[chosenIndex], true);
                }
            }
        }

        private static void AppendSnapCandidateLine(StringBuilder trace, EvaluatedSnapCandidate candidate, bool chosen)
        {
            trace.AppendLine(
                $"snap[{candidate.Index}] chosen={chosen} valid={candidate.Valid} inRange={candidate.InRange} priority={candidate.Candidate.Priority} relation={candidate.Candidate.Relation ?? "none"} distanceSquared={candidate.DistanceSquared:0.####} unit={FormatRealisticUnit(candidate.Candidate.Unit)} anchor={FormatRealisticUnit(candidate.Candidate.Anchor)}");
        }

        private static void LogRealisticPreviewTraceIfChanged(ICoreAPI api, StringBuilder trace, BlockPos targetPos, MasonryUnitPlacement unit, bool valid)
        {
            string key = $"{FormatBlockPos(targetPos)}|{valid}|{FormatRealisticUnit(unit)}";
            if (key == lastRealisticPreviewTraceKey) return;

            lastRealisticPreviewTraceKey = key;
            LogRealisticPlacementTrace(api, trace);
        }

        private static void LogRealisticPlacementTrace(ICoreAPI api, StringBuilder trace)
        {
            if (api == null || trace == null || trace.Length == 0) return;

            try
            {
                lock (RealisticPlacementLogLock)
                {
                    File.AppendAllText(
                        Path.Combine(GamePaths.Logs, RealisticPlacementLogName),
                        trace + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch (Exception exception)
            {
                api.Logger.Warning($"Could not write Brick by Brick placement log: {exception.Message}");
            }
        }

        private static string FormatRealisticUnit(MasonryUnitPlacement unit)
        {
            if (unit == null) return "null";

            MasonryVoxelGeometry.GetUnitCenter(unit, out float centerX, out float centerZ);
            return $"id={unit.Id} kind={unit.Kind} shape={unit.VisualShape} orientation={unit.Orientation} origin=({unit.Origin?.X},{unit.Origin?.Y},{unit.Origin?.Z}) offset=({unit.OffsetX:0.###},{unit.OffsetZ:0.###}) center=({centerX:0.###},{centerZ:0.###})";
        }

        private static string FormatBlockPos(BlockPos pos)
        {
            return pos == null ? "null" : $"({pos.X},{pos.Y},{pos.Z})";
        }

        private static string FormatVec(Vec3d vec)
        {
            return vec == null ? "null" : $"({vec.X:0.###},{vec.Y:0.###},{vec.Z:0.###})";
        }

        public static MasonryOrientation ResolveRealisticOrientation(IPlayer player)
        {
            int directionCount = brickbybrickModSystem.Config.Realism.EnableDiagonalPlacement ? 8 : 4;
            return (MasonryOrientation)GameMath.Mod(GetRealisticPlacementValue(player, RealisticOrientationAttribute, 0), directionCount);
        }

        public static int ResolveRealisticVariant(IPlayer player)
        {
            return GameMath.Mod(GetRealisticPlacementValue(player, RealisticVariantAttribute, 0), RealisticHalfBrickVariantCount);
        }

        public static int ResolveRealisticRammedEarthVariant(IPlayer player)
        {
            return GameMath.Mod(ResolveRealisticVariant(player), 2);
        }

        public static MasonryVisualShape ResolveRealisticVisualShape(MasonryUnitKind kind, IPlayer player)
        {
            return brickbybrickModSystem.Config.Realism.EnableDiagonalPlacement
                && kind == MasonryUnitKind.HalfBrick
                && ResolveRealisticVariant(player) > 0
                ? MasonryVisualShape.TriangleWedge
                : MasonryVisualShape.Cuboid;
        }

        public static MasonryOrientation ResolveRealisticUnitOrientation(MasonryUnitKind kind, IPlayer player)
        {
            int variant = ResolveRealisticVariant(player);
            if (brickbybrickModSystem.Config.Realism.EnableDiagonalPlacement
                && kind == MasonryUnitKind.HalfBrick
                && variant > 0)
            {
                return (MasonryOrientation)GameMath.Mod(variant - 1, 8);
            }

            return ResolveRealisticOrientation(player);
        }

        public static void SetRealisticPlacementState(IPlayer player, int orientation, int variant)
        {
            if (player?.Entity?.WatchedAttributes == null) return;

            int directionCount = brickbybrickModSystem.Config.Realism.EnableDiagonalPlacement ? 8 : 4;
            int variantCount = brickbybrickModSystem.Config.Realism.EnableDiagonalPlacement
                ? RealisticHalfBrickVariantCount
                : 1;
            int normalizedOrientation = GameMath.Mod(orientation, directionCount);
            int normalizedVariant = GameMath.Mod(variant, variantCount);
            player.Entity.WatchedAttributes.SetInt(RealisticOrientationAttribute, normalizedOrientation);
            player.Entity.WatchedAttributes.SetInt(RealisticVariantAttribute, normalizedVariant);

            if (player.Entity.World?.Side != EnumAppSide.Client) return;

            hasClientRealisticPlacementState = true;
            clientRealisticOrientation = normalizedOrientation;
            clientRealisticVariant = normalizedVariant;
        }

        private static int GetRealisticPlacementValue(IPlayer player, string attribute, int fallback)
        {
            if (player?.Entity?.WatchedAttributes == null) return fallback;
            if (player.Entity.World?.Side == EnumAppSide.Client && hasClientRealisticPlacementState)
            {
                if (attribute == RealisticOrientationAttribute) return clientRealisticOrientation;
                if (attribute == RealisticVariantAttribute) return clientRealisticVariant;
            }

            return player.Entity.WatchedAttributes.GetInt(attribute, fallback);
        }

        private static bool TryPickupMatchingRealisticBrick(EntityAgent byEntity, IPlayer player, BlockSelection blockSel, string heldPath)
        {
            if (byEntity.World.BlockAccessor.GetBlock(blockSel.Position).Code?.Path != "realisticmasonry") return false;
            if (byEntity.World.BlockAccessor.GetBlockEntity(blockSel.Position) is not BlockEntityRealisticMasonry entity) return false;

            MasonryGridPosition cell = new(
                GameMath.Clamp((int)Math.Floor((blockSel.HitPosition.X - blockSel.Face.Normali.X * 0.001) * 4), 0, 3),
                GameMath.Clamp((int)Math.Floor((blockSel.HitPosition.Y - blockSel.Face.Normali.Y * 0.001) * 4), 0, 255),
                GameMath.Clamp((int)Math.Floor((blockSel.HitPosition.Z - blockSel.Face.Normali.Z * 0.001) * 4), 0, 3));
            string color = heldPath[(heldPath.LastIndexOf('-') + 1)..];
            StringBuilder pickupTrace = new();
            pickupTrace.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] PICKUP side={byEntity.World.Side}");
            pickupTrace.AppendLine($"selection pos={FormatBlockPos(blockSel?.Position)} face={blockSel?.Face?.Code ?? "null"} hit={FormatVec(blockSel?.HitPosition)}");
            pickupTrace.AppendLine($"heldMaterial={heldPath} color={color} targetCell=({cell.X},{cell.Y},{cell.Z})");
            if (!entity.IsUnmortaredBrickOfColor(cell, color))
            {
                pickupTrace.AppendLine("result=skipped reason=no-matching-unmortared-unit");
                LogRealisticPlacementTrace(byEntity.World.Api, pickupTrace);
                return false;
            }

            ItemStack recovered = entity.TryRemoveUnmortaredUnit(cell);
            if (recovered == null)
            {
                pickupTrace.AppendLine("result=blocked reason=remove-failed");
                LogRealisticPlacementTrace(byEntity.World.Api, pickupTrace);
                return false;
            }

            AssetLocation recoveredCode = recovered.Collectible?.Code;
            int recoveredCount = recovered.StackSize;
            bool returnedToInventory = true;
            if (!player.InventoryManager.TryGiveItemstack(recovered, true))
            {
                returnedToInventory = false;
                byEntity.World.SpawnItemEntity(recovered, blockSel.Position.ToVec3d().Add(0.5, 0.5, 0.5));
            }

            pickupTrace.AppendLine($"inventory=recovered item={recoveredCode} count={recoveredCount} returnedToInventory={returnedToInventory}");
            pickupTrace.AppendLine("result=picked-up");
            LogRealisticPlacementTrace(byEntity.World.Api, pickupTrace);
            return true;
        }

        private bool HandleRealisticMortar(float secondsUsed, ItemSlot slot, EntityAgent byEntity, IPlayer player, BlockSelection blockSel)
        {
            if (!byEntity.Controls.Sneak && TryGetRealisticMaterial(player, slot, out _, out _)) return false;
            if (!HasEnoughMortar(slot, byEntity)) return false;

            float duration = brickbybrickModSystem.Config.GetConstructionActionSeconds() / Math.Max(1, slot.Itemstack.Collectible.ToolTier);
            PlayActionSoundAtMidpoint(secondsUsed, slot, byEntity, player, blockSel.Position, TrowelSounds, brickbybrickModSystem.Config.Effects.ConstructionSoundRange);
            if (secondsUsed < duration) return true;
            if (byEntity.World.Side != EnumAppSide.Server || HasInteracted(slot.Itemstack)) return false;
            BlockEntityRealisticMasonry entity = byEntity.World.BlockAccessor.GetBlockEntity(blockSel.Position) as BlockEntityRealisticMasonry;
            if (entity == null && byEntity.World.BlockAccessor.GetBlock(blockSel.Position) is BlockStaticMasonry)
            {
                if (!BlockStaticMasonry.TryRestoreEntity(byEntity.World, blockSel.Position, out entity, out string failureReason))
                {
                    NotifyPlayerDebug(player, byEntity.World, Lang.Get("brickbybrick:notice-realistic-reopen-failed", failureReason));
                    return false;
                }

                ConsumeConfiguredMortar(slot, brickbybrickModSystem.Config.Trowels.MortarCostPerAction);
                SetInteracted(slot.Itemstack, true);
                NotifyPlayerDebug(player, byEntity.World, Lang.Get("brickbybrick:notice-realistic-reopened"));
                return false;
            }

            if (entity == null) return false;

            if (entity.State.Frozen)
            {
                if (!entity.Reopen()) return false;
                ConsumeConfiguredMortar(slot, brickbybrickModSystem.Config.Trowels.MortarCostPerAction);
                SetInteracted(slot.Itemstack, true);
                NotifyPlayerDebug(player, byEntity.World, Lang.Get("brickbybrick:notice-realistic-reopened"));
                return false;
            }

            if (blockSel.Face?.IsHorizontal == true)
            {
                MasonryGridPosition sideCell = new(
                    GameMath.Clamp((int)Math.Floor((blockSel.HitPosition.X - blockSel.Face.Normali.X * 0.001) * 4), 0, 3),
                    GameMath.Clamp((int)Math.Floor(blockSel.HitPosition.Y * 4), 0, 255),
                    GameMath.Clamp((int)Math.Floor((blockSel.HitPosition.Z - blockSel.Face.Normali.Z * 0.001) * 4), 0, 3));
                if (!entity.ApplySideMortar(sideCell, blockSel.Face)) return false;

                ConsumeConfiguredMortar(slot, brickbybrickModSystem.Config.Trowels.MortarCostPerAction);
                SetInteracted(slot.Itemstack, true);
                return false;
            }

            MasonryGridPosition position = new(
                GameMath.Clamp((int)Math.Floor(blockSel.HitPosition.X * 4), 0, 3),
                GameMath.Clamp((int)Math.Floor(blockSel.HitPosition.Y * 4), 0, 255),
                GameMath.Clamp((int)Math.Floor(blockSel.HitPosition.Z * 4), 0, 3));
            MasonryUnitPlacement unit = entity.FindUnit(position);
            if (unit == null) return false;

            int changed = entity.ApplyMortar(unit);
            if (changed <= 0) return false;

            ConsumeConfiguredMortar(slot, changed * brickbybrickModSystem.Config.Trowels.MortarCostPerAction);
            SetInteracted(slot.Itemstack, true);
            return false;
        }

        // Mortar capacity follows the tool's material tier.
        public static int GetMaxCapacity(ItemStack stack)
        {
            int toolTier = stack.Collectible.ToolTier;
            int capacity = toolTier * brickbybrickModSystem.Config.Trowels.CapacityPerTier;

            // Realistic construction mortars individual units instead of one
            // abstract course, so scale capacity without changing recipe yield.
            if (brickbybrickModSystem.Config.IsRealisticConstructionEnabled())
            {
                capacity *= brickbybrickModSystem.Config.Realism.MortarCapacityMultiplier;
            }

            return capacity;
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

        private bool TryGetRealisticMaterial(IPlayer player, ItemSlot activeSlot, out ItemSlot slot, out ItemStack stack)
        {
            if (TryGetOffhandStack(player, out slot, out stack) && TryResolveRealisticKind(stack, player, out _)) return true;

            slot = activeSlot;
            stack = activeSlot?.Itemstack;
            return stack != null && stack.Collectible is not ItemTrowel && TryResolveRealisticKind(stack, player, out _);
        }

        public static bool TryResolveRealisticKind(ItemStack stack, IPlayer player, out MasonryUnitKind kind)
        {
            string path = stack?.Collectible?.Code?.Path ?? string.Empty;
            if (path == "testrammedearth")
            {
                kind = ResolveRealisticRammedEarthVariant(player) == 1 ? MasonryUnitKind.SmallRammedEarth : MasonryUnitKind.RammedEarth;
                return true;
            }

            if (path.StartsWith("halfbrick-", StringComparison.Ordinal))
            {
                kind = MasonryUnitKind.HalfBrick;
                return true;
            }

            if (path.StartsWith("burnedbrick-", StringComparison.Ordinal) && path != "burnedbrick-fire")
            {
                kind = MasonryUnitKind.WholeBrick;
                return true;
            }

            kind = (MasonryUnitKind)(-1);
            return false;
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
            if (secondsUsed < brickbybrickModSystem.Config.GetConstructionActionSeconds() / 2f) return;
            if (slot.Itemstack.Attributes.GetBool("soundPlayed", false)) return;

            PlayRandomSound(byEntity.World, pos, player, sounds, brickbybrickModSystem.Config.Effects.ConstructionSoundRange);
            slot.Itemstack.Attributes.SetBool("soundPlayed", true);
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
                variants.Set("rotation", ResolveSlabRotationCode(blockSel));
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
            else if (world.Side == EnumAppSide.Server && player is IServerPlayer serverPlayer)
            {
                serverPlayer.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
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
            WorldInteraction realisticCycleInteraction = new WorldInteraction()
            {
                ActionLangCode = "brickbybrick:heldhelp-realistic-cycle-ghost",
                HotKeyCodes = new string[] { "shift" },
                MouseButton = EnumMouseButton.None
            };
            WorldInteraction realisticVariantInteraction = new WorldInteraction()
            {
                ActionLangCode = "brickbybrick:heldhelp-realistic-cycle-variant",
                HotKeyCodes = new string[] { SecondaryPlacementModifierHotKeyCode },
                MouseButton = EnumMouseButton.None
            };

            if (brickbybrickModSystem.Config.IsRealisticConstructionEnabled())
            {
                return activeInteractions == null || activeInteractions.Length == 0
                    ? new WorldInteraction[] { realisticCycleInteraction, realisticVariantInteraction }
                    : new WorldInteraction[] { actionInteraction, realisticCycleInteraction, realisticVariantInteraction };
            }

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

        private sealed class TrowelPlacementPreviewRenderer : IRenderer, IDisposable
        {
            private const float ContactFaceOffset = 0.015625f;
            private const float RealisticJointInset = 0.0078125f;
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
                if (brickbybrickModSystem.Config.IsRealisticConstructionEnabled())
                {
                    RenderRealisticPreview();
                    return;
                }

                if (!TryResolvePreview(out BlockPos targetPos, out Block courseBlock, out Variants previewVariants, out BlockFacing selectedFace)) return;

                MeshRef meshRef = GetOrCreateMeshRef(courseBlock, previewVariants);
                if (meshRef == null) return;

                IRenderAPI rpi = capi.Render;
                float previewAlpha = brickbybrickModSystem.Config.Trowels.PlacementPreviewOpacity;
                Vec4f ghostTint = new Vec4f(1f, 1f, 1f, previewAlpha);
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

            private void RenderRealisticPreview()
            {
                if (!TryResolveRealisticPreview(
                    out BlockPos targetPos,
                    out MasonryUnitPlacement unit,
                    out MeshRef meshRef,
                    out bool valid)) return;

                IRenderAPI rpi = capi.Render;
                float alpha = brickbybrickModSystem.Config.Trowels.PlacementPreviewOpacity;
                Vec4f tint = valid ? new Vec4f(1f, 1f, 1f, alpha) : new Vec4f(1f, 0.12f, 0.12f, alpha);
                Vec3d cameraPos = capi.World.Player.Entity.CameraPos;
                IStandardShaderProgram shader = rpi.PreparedStandardShader(targetPos.X, targetPos.Y, targetPos.Z, tint);
                shader.Tex2D = capi.BlockTextureAtlas.AtlasTextures[0].TextureId;
                shader.RgbaTint = tint;
                shader.AlphaTest = 0.01f;
                shader.ExtraGlow = valid ? 72 : 128;
                shader.SsaoAttn = 0f;
                shader.OverlayOpacity = 0f;
                shader.ModelMatrix = Matrixf.Create()
                    .Translate(
                        (float)(targetPos.X - cameraPos.X),
                        (float)(targetPos.Y - cameraPos.Y),
                        (float)(targetPos.Z - cameraPos.Z))
                    .Values;

                rpi.GlToggleBlend(true);
                rpi.GLDepthMask(false);
                rpi.GlDisableCullFace();
                rpi.RenderMesh(meshRef);
                rpi.GlEnableCullFace();
                rpi.GLDepthMask(true);
                rpi.GlToggleBlend(false);
                shader.Stop();
            }

            private bool TryResolveRealisticPreview(
                out BlockPos targetPos,
                out MasonryUnitPlacement unit,
                out MeshRef meshRef,
                out bool valid)
            {
                targetPos = null;
                unit = null;
                meshRef = null;
                valid = false;

                IClientPlayer player = capi.World.Player;
                ItemSlot activeSlot = player?.InventoryManager?.ActiveHotbarSlot;
                BlockSelection blockSel = player?.CurrentBlockSelection;
                if (activeSlot?.Itemstack?.Collectible != trowel || blockSel == null) return false;
                if (!trowel.TryGetRealisticMaterial(player, activeSlot, out _, out ItemStack materialStack)) return false;

                string path = materialStack.Collectible?.Code?.Path ?? string.Empty;
                MasonryUnitKind kind = path == "testrammedearth"
                    ? ResolveRealisticRammedEarthVariant(player) == 1 ? MasonryUnitKind.SmallRammedEarth : MasonryUnitKind.RammedEarth
                    : path.StartsWith("halfbrick-", StringComparison.Ordinal)
                        ? MasonryUnitKind.HalfBrick
                        : path.StartsWith("burnedbrick-", StringComparison.Ordinal) && path != "burnedbrick-fire"
                            ? MasonryUnitKind.WholeBrick
                            : (MasonryUnitKind)(-1);
                if ((int)kind < 0) return false;

                Block selectedBlock = capi.World.BlockAccessor.GetBlock(blockSel.Position);
                bool targetsExistingCell = selectedBlock?.Code?.Path == "realisticmasonry";
                targetPos = targetsExistingCell ? blockSel.Position.Copy() : trowel.ResolvePlacementTarget(blockSel);
                ResolveRealisticGridOrigin(blockSel, ref targetPos, targetsExistingCell, out int gridX, out int gridZ);
                BlockEntityRealisticMasonry targetEntity = capi.World.BlockAccessor.GetBlockEntity(targetPos) as BlockEntityRealisticMasonry;
                int layer = ResolveRealisticPlacementLayer(blockSel, targetsExistingCell, gridX, gridZ, targetEntity);

                unit = new MasonryUnitPlacement
                {
                    Id = "preview",
                    Kind = kind,
                    VisualShape = ResolveRealisticVisualShape(kind, player),
                    MaterialCode = path,
                    Orientation = ResolveRealisticUnitOrientation(kind, player),
                    Origin = new MasonryGridPosition(gridX, layer, gridZ)
                };
                StringBuilder previewTrace = new();
                AppendRealisticPlacementTraceHeader(previewTrace, "PREVIEW", capi.Side, blockSel, path, unit, targetPos, targetsExistingCell, gridX, gridZ, layer);
                Block targetBlock = capi.World.BlockAccessor.GetBlock(targetPos);
                TrySnapRealisticPlacement(capi.World.BlockAccessor, targetPos, blockSel, unit, previewTrace);
                CanonicalizePlacementOwner(ref targetPos, unit);
                previewTrace.AppendLine($"finalTarget={FormatBlockPos(targetPos)} finalUnit={FormatRealisticUnit(unit)}");
                targetBlock = capi.World.BlockAccessor.GetBlock(targetPos);

                if (targetBlock?.Code?.Path == "realisticmasonry"
                    && capi.World.BlockAccessor.GetBlockEntity(targetPos) is BlockEntityRealisticMasonry entity)
                {
                    valid = GetProjectedPlacementFailure(capi.World.BlockAccessor, targetPos, entity, unit) == MasonryPlacementFailure.None
                        && CanReserveNeighborFootprints(targetPos, unit);
                }
                else
                {
                    Block constructionBlock = capi.World.GetBlock(new AssetLocation("brickbybrick:realisticmasonry"));
                    valid = constructionBlock != null
                        && targetBlock != null
                        && targetBlock.IsReplacableBy(constructionBlock)
                        && GetProjectedPlacementFailure(capi.World.BlockAccessor, targetPos, null, unit) == MasonryPlacementFailure.None
                        && CanReserveNeighborFootprints(targetPos, unit);
                }

                previewTrace.AppendLine($"result=preview valid={valid}");
                LogRealisticPreviewTraceIfChanged(capi, previewTrace, targetPos, unit, valid);
                meshRef = GetOrCreateRealisticMeshRef(unit, path);
                return meshRef != null;
            }

            private bool CanReserveNeighborFootprints(BlockPos ownerPos, MasonryUnitPlacement unit)
            {
                Block constructionBlock = capi.World.GetBlock(new AssetLocation("brickbybrick:realisticmasonry"));
                Dictionary<(int X, int Z), List<MasonryGridPosition>> neighborReservations = BuildNeighborReservations(unit);
                foreach ((int X, int Z) neighborOffset in GetNeighborOffsets(unit, neighborReservations))
                {
                    BlockPos neighborPos = ownerPos.AddCopy(neighborOffset.X, 0, neighborOffset.Z);
                    Block neighborBlock = capi.World.BlockAccessor.GetBlock(neighborPos);

                    if (neighborBlock?.Code?.Path == "realisticmasonry")
                    {
                        MasonryUnitPlacement neighborProjection = ProjectUnitIntoNeighbor(unit, neighborOffset);
                        if (capi.World.BlockAccessor.GetBlockEntity(neighborPos) is not BlockEntityRealisticMasonry neighborEntity
                            || !neighborEntity.CanReserve(neighborProjection)) return false;
                    }
                    else if (constructionBlock == null || neighborBlock == null || !neighborBlock.IsReplacableBy(constructionBlock))
                    {
                        return false;
                    }
                }

                return true;
            }

            private MeshRef GetOrCreateRealisticMeshRef(MasonryUnitPlacement unit, string materialCode)
            {
                string color = materialCode[(materialCode.LastIndexOf('-') + 1)..];
                string key = $"realistic-{unit.Kind}-{unit.VisualShape}-{color}-{unit.Origin.X}-{unit.Origin.Y}-{unit.Origin.Z}-{unit.OffsetX:0.###}-{unit.OffsetZ:0.###}-{unit.Orientation}";
                if (meshRefs.TryGetValue(key, out MeshRef meshRef)) return meshRef;

                Block block = capi.World.GetBlock(new AssetLocation("brickbybrick:realisticmasonry"));
                BlockBehaviorShapeTexturesFromAttributes behavior = block?.GetBehavior<BlockBehaviorShapeTexturesFromAttributes>();
                if (behavior == null) return null;

                CompositeShape shape = new()
                {
                    Base = new AssetLocation(unit.Kind is MasonryUnitKind.RammedEarth or MasonryUnitKind.SmallRammedEarth
                        ? "brickbybrick:shapes/block/realistic/rammedearth.json"
                        : "brickbybrick:shapes/block/realistic/brick.json")
                };
                Variants variants = new();
                variants.Set("color", color);
                MeshData meshData = behavior.GetOrCreateMesh(variants, shape, new BlockPos(0), key).Clone();
                if (unit.VisualShape == MasonryVisualShape.TriangleWedge) MasonryVoxelGeometry.DeformTriangle(meshData);

                meshData = MasonryVoxelGeometry.TransformUnitMesh(meshData, unit, RealisticJointInset);
                meshRef = capi.Render.UploadMesh(meshData);
                meshRefs[key] = meshRef;
                return meshRef;
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

            private bool TryResolvePreview(out BlockPos targetPos, out Block courseBlock, out Variants previewVariants, out BlockFacing selectedFace)
            {
                targetPos = null;
                courseBlock = null;
                previewVariants = null;
                selectedFace = BlockFacing.UP;

                IClientPlayer player = capi.World.Player;
                ItemSlot activeSlot = player?.InventoryManager?.ActiveHotbarSlot;
                BlockSelection blockSel = player?.CurrentBlockSelection;
                if (activeSlot?.Itemstack?.Collectible != trowel || blockSel == null) return false;
                if (brickbybrickModSystem.Config.IsRealisticConstructionEnabled()) return false;

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
