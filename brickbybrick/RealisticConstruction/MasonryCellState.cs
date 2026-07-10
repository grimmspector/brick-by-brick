using System.Collections.Generic;
using System.Linq;

namespace brickbybrick.RealisticConstruction
{
    public enum FrozenMasonryShape
    {
        Arbitrary,
        Block,
        SlabDown,
        SlabUp,
        SlabNorth,
        SlabEast,
        SlabSouth,
        SlabWest,
        StairNorth,
        StairEast,
        StairSouth,
        StairWest,
        StairDownNorth,
        StairDownEast,
        StairDownSouth,
        StairDownWest
    }

    // Stores construction progress by slot instead of forcing a block through
    // a linear sequence. A future block entity will persist this state.
    public sealed class MasonryCellState
    {
        public string LayoutCode { get; set; } = string.Empty;

        public HashSet<string> PlacedSlots { get; set; } = new();

        public HashSet<string> MortaredSlots { get; set; } = new();

        public int FillUnits { get; set; }

        public List<MasonryUnitPlacement> Units { get; set; } = new();

        public HashSet<MasonryGridPosition> ReservedPositions { get; set; } = new();

        public List<MasonryUnitPlacement> ReservedUnits { get; set; } = new();

        public HashSet<string> MortaredSideJoints { get; set; } = new();

        public HashSet<MasonryGridPosition> EarthGapVoxels { get; set; } = new();

        public HashSet<MasonryGridPosition> MortarGapVoxels { get; set; } = new();

        public string MortarMaterialCode { get; set; } = "default";

        public bool Frozen { get; set; }

        public FrozenMasonryShape FrozenShape { get; set; }

        public double LastModifiedTotalHours { get; set; }

        public bool IsSlotPlaced(string slotCode)
        {
            return PlacedSlots.Contains(slotCode);
        }

        public bool IsSlotMortared(string slotCode)
        {
            return MortaredSlots.Contains(slotCode);
        }

        // Every declared support must exist and have mortar before the next
        // unit can bridge it. Empty support lists represent the bottom layer.
        public bool HasSupportedBase(MasonrySlotDefinition slot)
        {
            foreach (string supportCode in slot.SupportSlots)
            {
                if (!PlacedSlots.Contains(supportCode) || !MortaredSlots.Contains(supportCode))
                {
                    return false;
                }
            }

            return true;
        }

        public bool TryPlace(MasonrySlotDefinition slot)
        {
            return HasSupportedBase(slot) && PlacedSlots.Add(slot.Code);
        }

        public bool TryApplyMortar(string slotCode)
        {
            return PlacedSlots.Contains(slotCode) && MortaredSlots.Add(slotCode);
        }

        // Mortar belongs to the occupied footprint in this cell. Calling this
        // twice returns zero so an already covered unit is never charged again.
        public int ApplyMortar(IEnumerable<MasonryGridPosition> positions)
        {
            int changed = 0;

            foreach (MasonryGridPosition position in positions.Distinct())
            {
                MasonryUnitPlacement? unit = Units.FirstOrDefault(candidate => candidate.Occupies(position));
                if (unit == null || unit.Kind is MasonryUnitKind.RammedEarth or MasonryUnitKind.SmallRammedEarth || unit.MortaredPositions.Contains(position)) continue;

                unit.MortaredPositions.Add(position);
                changed++;
            }

            return changed;
        }
    }

    public enum MasonryUnitKind
    {
        WholeBrick,
        HalfBrick,
        RammedEarth,
        ObsoleteTriangleBrick,
        SmallRammedEarth
    }

    public enum MasonryVisualShape
    {
        Cuboid,
        TriangleWedge
    }

    public enum MasonryOrientation
    {
        East,
        South,
        West,
        North,
        SouthEast,
        SouthWest,
        NorthWest,
        NorthEast
    }

    public enum MasonryPlacementFailure
    {
        None,
        Frozen,
        Occupied,
        Unsupported
    }

    // Coordinates are quarter-block cells matching one half-brick footprint.
    // X and Z may cross a block boundary;
    // ownership stays with the origin cell while touched neighbors mirror only
    // their local footprint for collision, mortar, and support checks.
    public sealed class MasonryUnitPlacement
    {
        public string Id { get; set; } = string.Empty;

        public string MaterialCode { get; set; } = "game:burnedbrick-cream";

        public MasonryUnitKind Kind { get; set; }

        public MasonryOrientation Orientation { get; set; }

        public MasonryVisualShape VisualShape { get; set; }

        public MasonryGridPosition Origin { get; set; } = new();

        public float OffsetX { get; set; }

        public float OffsetZ { get; set; }

        public HashSet<MasonryGridPosition> MortaredPositions { get; set; } = new();

        public IEnumerable<MasonryGridPosition> GetFootprint()
        {
            if (Kind is MasonryUnitKind.HalfBrick or MasonryUnitKind.SmallRammedEarth)
            {
                yield return Origin;
                yield break;
            }

            if (Kind == MasonryUnitKind.RammedEarth)
            {
                int xStep = Orientation == MasonryOrientation.West || Orientation == MasonryOrientation.North ? -1 : 1;
                int zStep = Orientation == MasonryOrientation.North || Orientation == MasonryOrientation.East ? -1 : 1;
                for (int x = 0; x < 2; x++)
                {
                    for (int z = 0; z < 2; z++)
                    {
                        yield return new MasonryGridPosition(Origin.X + x * xStep, Origin.Y, Origin.Z + z * zStep);
                    }
                }

                yield break;
            }

            if (IsDiagonal)
            {
                foreach (MasonryGridPosition position in MasonryVoxelGeometry.GetQuarterFootprint(this)) yield return position;
                yield break;
            }

            int unitX = Orientation == MasonryOrientation.East ? 1 : Orientation == MasonryOrientation.West ? -1 : 0;
            int unitZ = Orientation == MasonryOrientation.South ? 1 : Orientation == MasonryOrientation.North ? -1 : 0;
            yield return Origin;
            yield return new MasonryGridPosition(Origin.X + unitX, Origin.Y, Origin.Z + unitZ);
        }

        public bool IsDiagonal => Orientation >= MasonryOrientation.SouthEast;

        public bool Occupies(MasonryGridPosition position)
        {
            return GetFootprint().Contains(position);
        }

        public bool Supports(MasonryGridPosition position)
        {
            return Kind is MasonryUnitKind.RammedEarth or MasonryUnitKind.SmallRammedEarth
                ? Occupies(position)
                : Occupies(position) && MortaredPositions.Contains(position);
        }
    }

    public sealed class MasonryGridPosition
    {
        public MasonryGridPosition()
        {
        }

        public MasonryGridPosition(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public int X { get; set; }

        public int Y { get; set; }

        public int Z { get; set; }

        public override bool Equals(object? obj)
        {
            return obj is MasonryGridPosition other && X == other.X && Y == other.Y && Z == other.Z;
        }

        public override int GetHashCode()
        {
            return System.HashCode.Combine(X, Y, Z);
        }
    }
}
