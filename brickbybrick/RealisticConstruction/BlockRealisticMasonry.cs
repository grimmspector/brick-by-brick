using System;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace brickbybrick.RealisticConstruction
{
    public sealed class BlockRealisticMasonry : Block
    {
        private const float JointInset = 0.0078125f;

        public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
        {
            return GetBoxes(blockAccessor, pos);
        }

        public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
        {
            return GetBoxes(blockAccessor, pos);
        }

        public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
        {
            if (world.BlockAccessor.GetBlockEntity(pos) is not BlockEntityRealisticMasonry entity) return base.GetPlacedBlockInfo(world, pos, forPlayer);
            if (entity.State.Frozen)
            {
                return Lang.Get("brickbybrick:blockinfo-realistic-frozen", entity.State.FrozenShape);
            }

            if (!brickbybrickModSystem.Config.Curing.EnableMortarCuring)
            {
                return Lang.Get("brickbybrick:blockinfo-realistic-wet-curing-disabled");
            }

            double requiredHours = brickbybrickModSystem.Config.Curing.MortarCuringHours
                / brickbybrickModSystem.Config.Curing.CuringSpeedMultiplier;
            double remainingHours = Math.Max(0, requiredHours - (world.Calendar.TotalHours - entity.State.LastModifiedTotalHours));
            double calendarRate = world.Calendar.SpeedOfTime * world.Calendar.CalendarSpeedMul;
            string realTimeRemaining = calendarRate <= 0
                ? Lang.Get("brickbybrick:blockinfo-realistic-paused")
                : FormatRealDuration(TimeSpan.FromSeconds(remainingHours * 3600d / calendarRate));
            return Lang.Get("brickbybrick:blockinfo-realistic-wet", realTimeRemaining);
        }

        private static string FormatRealDuration(TimeSpan duration)
        {
            if (duration.TotalDays >= 1) return $"{duration.TotalDays:0.0} days";
            if (duration.TotalHours >= 1) return $"{duration.TotalHours:0.0} hours";
            if (duration.TotalMinutes >= 1) return $"{duration.TotalMinutes:0} minutes";
            return $"{Math.Max(0, duration.TotalSeconds):0} seconds";
        }

        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (byPlayer == null) return false;
            IPlayerInventoryManager? inventoryManager = byPlayer.InventoryManager;
            if (inventoryManager == null) return false;
            if (inventoryManager.ActiveHotbarSlot?.Itemstack != null) return base.OnBlockInteractStart(world, byPlayer, blockSel);
            if (world.Side != EnumAppSide.Server) return true;
            if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is not BlockEntityRealisticMasonry entity) return false;

            MasonryGridPosition cell = new(
                GameMath.Clamp((int)Math.Floor((blockSel.HitPosition.X - blockSel.Face.Normali.X * 0.001) * 4), 0, 3),
                GameMath.Clamp((int)Math.Floor((blockSel.HitPosition.Y - blockSel.Face.Normali.Y * 0.001) * 4), 0, 255),
                GameMath.Clamp((int)Math.Floor((blockSel.HitPosition.Z - blockSel.Face.Normali.Z * 0.001) * 4), 0, 3));
            ItemStack? recovered = entity.TryRemoveUnmortaredUnit(cell);
            if (recovered == null) return false;

            if (!inventoryManager.TryGiveItemstack(recovered, true))
            {
                world.SpawnItemEntity(recovered, blockSel.Position.ToVec3d().Add(0.5, 0.5, 0.5));
            }

            return true;
        }

        private static Cuboidf[] GetBoxes(IBlockAccessor blockAccessor, BlockPos pos)
        {
            if (blockAccessor.GetBlockEntity(pos) is not BlockEntityRealisticMasonry entity) return new[] { new Cuboidf(0, 0, 0, 1, 0.25f, 1) };
            if (entity.State.Frozen)
            {
                Cuboidf[]? frozenBoxes = GetFrozenBoxes(entity.State.FrozenShape);
                if (frozenBoxes != null) return frozenBoxes;
            }

            System.Collections.Generic.List<Cuboidf> boxes = new();
            foreach (MasonryUnitPlacement unit in entity.State.Units)
            {
                foreach (MasonryGridPosition cell in unit.GetFootprint())
                {
                    boxes.Add(new Cuboidf(
                        cell.X * 0.25f + JointInset,
                        cell.Y * 0.25f + JointInset,
                        cell.Z * 0.25f + JointInset,
                        (cell.X + 1) * 0.25f - JointInset,
                        (cell.Y + 1) * 0.25f - JointInset,
                        (cell.Z + 1) * 0.25f - JointInset));
                }
            }

            foreach (MasonryGridPosition cell in entity.State.ReservedPositions)
            {
                boxes.Add(new Cuboidf(
                    cell.X * 0.25f + JointInset,
                    cell.Y * 0.25f + JointInset,
                    cell.Z * 0.25f + JointInset,
                    (cell.X + 1) * 0.25f - JointInset,
                    (cell.Y + 1) * 0.25f - JointInset,
                    (cell.Z + 1) * 0.25f - JointInset));
            }

            return boxes.Count == 0 ? new[] { new Cuboidf(0, 0, 0, 1, 0.01f, 1) } : boxes.ToArray();
        }

        internal static Cuboidf[]? GetFrozenBoxes(FrozenMasonryShape shape)
        {
            return shape switch
            {
                FrozenMasonryShape.Block => new[] { new Cuboidf(0, 0, 0, 1, 1, 1) },
                FrozenMasonryShape.SlabDown => new[] { new Cuboidf(0, 0, 0, 1, 0.5f, 1) },
                FrozenMasonryShape.SlabUp => new[] { new Cuboidf(0, 0.5f, 0, 1, 1, 1) },
                FrozenMasonryShape.SlabNorth => new[] { new Cuboidf(0, 0, 0, 1, 1, 0.5f) },
                FrozenMasonryShape.SlabEast => new[] { new Cuboidf(0.5f, 0, 0, 1, 1, 1) },
                FrozenMasonryShape.SlabSouth => new[] { new Cuboidf(0, 0, 0.5f, 1, 1, 1) },
                FrozenMasonryShape.SlabWest => new[] { new Cuboidf(0, 0, 0, 0.5f, 1, 1) },
                FrozenMasonryShape.StairNorth => new[] { new Cuboidf(0, 0, 0, 1, 0.5f, 1), new Cuboidf(0, 0.5f, 0, 1, 1, 0.5f) },
                FrozenMasonryShape.StairEast => new[] { new Cuboidf(0, 0, 0, 1, 0.5f, 1), new Cuboidf(0.5f, 0.5f, 0, 1, 1, 1) },
                FrozenMasonryShape.StairSouth => new[] { new Cuboidf(0, 0, 0, 1, 0.5f, 1), new Cuboidf(0, 0.5f, 0.5f, 1, 1, 1) },
                FrozenMasonryShape.StairWest => new[] { new Cuboidf(0, 0, 0, 1, 0.5f, 1), new Cuboidf(0, 0.5f, 0, 0.5f, 1, 1) },
                FrozenMasonryShape.StairDownNorth => new[] { new Cuboidf(0, 0.5f, 0, 1, 1, 1), new Cuboidf(0, 0, 0, 1, 0.5f, 0.5f) },
                FrozenMasonryShape.StairDownEast => new[] { new Cuboidf(0, 0.5f, 0, 1, 1, 1), new Cuboidf(0.5f, 0, 0, 1, 0.5f, 1) },
                FrozenMasonryShape.StairDownSouth => new[] { new Cuboidf(0, 0.5f, 0, 1, 1, 1), new Cuboidf(0, 0, 0.5f, 1, 0.5f, 1) },
                FrozenMasonryShape.StairDownWest => new[] { new Cuboidf(0, 0.5f, 0, 1, 1, 1), new Cuboidf(0, 0, 0, 0.5f, 0.5f, 1) },
                _ => null
            };
        }
    }
}
