using Vintagestory.API.Common;
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

        private static Cuboidf[] GetBoxes(IBlockAccessor blockAccessor, BlockPos pos)
        {
            if (blockAccessor.GetBlockEntity(pos) is not BlockEntityRealisticMasonry entity) return new[] { new Cuboidf(0, 0, 0, 1, 0.25f, 1) };

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

            return boxes.Count == 0 ? new[] { new Cuboidf(0, 0, 0, 1, 0.01f, 1) } : boxes.ToArray();
        }
    }
}
