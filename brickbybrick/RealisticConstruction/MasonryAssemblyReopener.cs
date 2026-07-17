using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace brickbybrick.RealisticConstruction
{
    internal sealed class MasonryAssemblyReopenResult
    {
        public bool Success { get; set; }

        public string FailureReason { get; set; } = string.Empty;

        public List<BlockPos> ReopenedPositions { get; } = new();

        public List<string> TraceLines { get; } = new();
    }

    internal static class MasonryAssemblyReopener
    {
        internal static MasonryAssemblyReopenResult TryReopen(IWorldAccessor world, BlockPos selectedPos)
        {
            MasonryAssemblyReopenResult result = new();
            if (world?.Side != EnumAppSide.Server)
            {
                result.FailureReason = "reopening was requested outside the server";
                return result;
            }

            Queue<BlockPos> pending = new();
            HashSet<(int X, int Y, int Z)> visited = new();
            pending.Enqueue(selectedPos.Copy());

            while (pending.Count > 0)
            {
                BlockPos currentPos = pending.Dequeue();
                if (!visited.Add((currentPos.X, currentPos.Y, currentPos.Z))) continue;

                if (!TryReopenCell(world, currentPos, out BlockEntityRealisticMasonry? entity, out string action, out string failureReason))
                {
                    if (currentPos.Equals(selectedPos))
                    {
                        result.FailureReason = failureReason;
                        result.TraceLines.Add($"cell={Format(currentPos)} result=failed reason={failureReason}");
                        return result;
                    }

                    result.TraceLines.Add($"cell={Format(currentPos)} result=missing-linked-cell reason={failureReason}");
                    continue;
                }

                result.ReopenedPositions.Add(currentPos.Copy());
                result.TraceLines.Add($"cell={Format(currentPos)} action={action} units={entity!.State.Units.Count} reservedUnits={entity.State.ReservedUnits.Count}");
                foreach (BlockPos linkedPos in GetLinkedPositions(entity, currentPos)) pending.Enqueue(linkedPos);
            }

            result.Success = result.ReopenedPositions.Count > 0;
            if (!result.Success) result.FailureReason = "the selected cell did not contain realistic masonry";
            return result;
        }

        private static bool TryReopenCell(
            IWorldAccessor world,
            BlockPos pos,
            out BlockEntityRealisticMasonry? entity,
            out string action,
            out string failureReason)
        {
            failureReason = string.Empty;
            entity = world.BlockAccessor.GetBlockEntity(pos) as BlockEntityRealisticMasonry;
            if (entity != null)
            {
                if (entity.State.Frozen)
                {
                    entity.Reopen();
                    action = "unfroze-live";
                }
                else
                {
                    action = "already-editable";
                }

                failureReason = string.Empty;
                return true;
            }

            if (world.BlockAccessor.GetBlock(pos) is BlockStaticMasonry
                && BlockStaticMasonry.TryRestoreEntity(world, pos, out entity, out failureReason))
            {
                action = "restored-static";
                return true;
            }

            action = "none";
            if (string.IsNullOrEmpty(failureReason)) failureReason = "the cell is not realistic masonry";
            return false;
        }

        private static IEnumerable<BlockPos> GetLinkedPositions(BlockEntityRealisticMasonry entity, BlockPos currentPos)
        {
            foreach (MasonryUnitPlacement unit in entity.State.ReservedUnits)
            {
                if (unit.HasOwner) yield return new BlockPos(unit.OwnerBlockX, unit.OwnerBlockY, unit.OwnerBlockZ);
            }

            foreach (MasonryUnitPlacement unit in entity.State.Units)
            {
                if (unit.HasOwner
                    && (unit.OwnerBlockX != currentPos.X || unit.OwnerBlockY != currentPos.Y || unit.OwnerBlockZ != currentPos.Z))
                {
                    yield return new BlockPos(unit.OwnerBlockX, unit.OwnerBlockY, unit.OwnerBlockZ);
                    continue;
                }

                foreach (MasonryGridPosition position in MasonryVoxelGeometry.GetReservationFootprint(unit))
                {
                    int offsetX = (int)Math.Floor(position.X / 4d);
                    int offsetZ = (int)Math.Floor(position.Z / 4d);
                    if (offsetX != 0 || offsetZ != 0) yield return currentPos.AddCopy(offsetX, 0, offsetZ);
                }
            }
        }

        private static string Format(BlockPos pos)
        {
            return $"({pos.X},{pos.Y},{pos.Z})";
        }
    }
}
