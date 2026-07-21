using System;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace brickbybrick.items
{
    // A crafted supply represents one vanilla rammed-earth block without
    // creating a separate inventory item for every placement-sized piece.
    internal sealed class ItemRammedEarthSupply : Item
    {
        public const int MaxPoints = 192;
        public const int CellPoints = 3;
        public const int SmallEarthPoints = CellPoints;
        public const int RammedEarthPoints = CellPoints * 4;
        public const int GapFillPoints = 1;
        private const string PointsAttribute = "rammedEarthPoints";

        public override int GetMaxDurability(ItemStack itemstack)
        {
            return MaxPoints;
        }

        public static int GetPoints(ItemStack? stack)
        {
            return GameMath.Clamp(stack?.Attributes?.GetInt(PointsAttribute, MaxPoints) ?? 0, 0, MaxPoints);
        }

        public static bool HasPoints(ItemStack? stack, int cost)
        {
            return cost >= 0 && GetPoints(stack) >= cost;
        }

        public static int GetPlacementCost(RealisticConstruction.MasonryUnitKind kind)
        {
            return kind switch
            {
                RealisticConstruction.MasonryUnitKind.RammedEarth => RammedEarthPoints,
                RealisticConstruction.MasonryUnitKind.SmallRammedEarth => SmallEarthPoints,
                _ => 0
            };
        }

        public static bool TryConsume(ItemSlot slot, int cost)
        {
            if (slot?.Itemstack == null || cost <= 0 || !HasPoints(slot.Itemstack, cost)) return false;

            SetPoints(slot, GetPoints(slot.Itemstack) - cost);
            return true;
        }

        private static void SetPoints(ItemSlot slot, int points)
        {
            if (slot?.Itemstack == null) return;

            points = GameMath.Clamp(points, 0, MaxPoints);
            if (points == 0)
            {
                slot.Itemstack = null;
                slot.MarkDirty();
                return;
            }

            slot.Itemstack.Attributes.SetInt(PointsAttribute, points);
            // The standard durability overlay becomes the remaining supply bar.
            slot.Itemstack.Attributes.SetInt("durability", points);
            slot.MarkDirty();
        }

        public override bool Equals(ItemStack thisStack, ItemStack otherStack, params string[] ignoreAttributeSubTrees)
        {
            return otherStack?.Collectible is ItemRammedEarthSupply
                || base.Equals(thisStack, otherStack, ignoreAttributeSubTrees);
        }

        public override bool Satisfies(ItemStack thisStack, ItemStack otherStack)
        {
            return otherStack?.Collectible is ItemRammedEarthSupply || base.Satisfies(thisStack, otherStack);
        }

        public override int GetMergableQuantity(ItemStack sinkStack, ItemStack sourceStack, EnumMergePriority priority)
        {
            if (sourceStack?.Collectible is not ItemRammedEarthSupply) return 0;
            return GetPoints(sinkStack) < MaxPoints ? 1 : 0;
        }

        // This hook is used by normal left-click inventory merges and by
        // automatic placement into an inventory. Point capacity, not stack
        // count, decides whether the source can join the destination.
        public override void TryMergeStacks(ItemStackMergeOperation op)
        {
            if (op == null) return;

            ItemSlot? sink = op.SinkSlot;
            ItemSlot? source = op.SourceSlot;
            if (sink?.Itemstack is not ItemStack sinkStack
                || source?.Itemstack is not ItemStack sourceStack
                || sinkStack.Collectible is not ItemRammedEarthSupply
                || sourceStack.Collectible is not ItemRammedEarthSupply)
            {
                base.TryMergeStacks(op);
                return;
            }

            int sinkPoints = GetPoints(sinkStack);
            int sourcePoints = GetPoints(sourceStack);
            int transferred = Math.Min(MaxPoints - sinkPoints, sourcePoints);
            if (transferred <= 0)
            {
                op.MovedQuantity = 0;
                return;
            }

            SetPoints(sink!, sinkPoints + transferred);
            SetPoints(source!, sourcePoints - transferred);
            op.MovedQuantity = source!.Itemstack == null ? 1 : 0;
        }

        public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
        {
            base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);

            string durabilityText = Lang.Get("Durability");
            string[] lines = dsc.ToString().Split('\n');
            dsc.Clear();
            foreach (string line in lines)
            {
                if (!line.Contains(durabilityText, StringComparison.Ordinal)) dsc.AppendLine(line);
            }

            dsc.AppendLine(Lang.Get("brickbybrick:tooltip-rammed-earth-points", GetPoints(inSlot.Itemstack), MaxPoints));
            dsc.AppendLine(Lang.Get("brickbybrick:tooltip-rammed-earth-consolidate"));
        }
    }
}
