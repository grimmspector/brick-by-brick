using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace brickbybrick.RealisticConstruction
{
    // Converts masonry units to a chisel-scale occupancy volume. Derived data
    // is rebuilt only when a cell changes and is never persisted or networked.
    internal static class MasonryVoxelGeometry
    {
        internal const int Resolution = 16;
        private const float Epsilon = 0.0001f;

        internal static float GetAngleDegrees(MasonryOrientation orientation)
        {
            return orientation switch
            {
                MasonryOrientation.East => 0,
                MasonryOrientation.SouthEast => 45,
                MasonryOrientation.South => 90,
                MasonryOrientation.SouthWest => 135,
                MasonryOrientation.West => 180,
                MasonryOrientation.NorthWest => 225,
                MasonryOrientation.North => 270,
                MasonryOrientation.NorthEast => 315,
                _ => 0
            };
        }

        internal static float GetWholeBrickCenterOffset(MasonryOrientation orientation)
        {
            return 0.125f;
        }

        internal static void GetRammedEarthFootprintSteps(MasonryOrientation orientation, out int xStep, out int zStep)
        {
            xStep = orientation is MasonryOrientation.West or MasonryOrientation.North ? -1 : 1;
            zStep = orientation is MasonryOrientation.North or MasonryOrientation.East ? -1 : 1;
        }

        internal static void GetDimensions(MasonryUnitKind kind, out float length, out float width)
        {
            length = kind is MasonryUnitKind.WholeBrick or MasonryUnitKind.RammedEarth ? 0.5f : 0.25f;
            width = kind == MasonryUnitKind.RammedEarth ? 0.5f : 0.25f;
        }

        internal static void GetDirection(MasonryOrientation orientation, out float directionX, out float directionZ)
        {
            float angle = GetAngleDegrees(orientation) * GameMath.DEG2RAD;
            directionX = MathF.Cos(angle);
            directionZ = MathF.Sin(angle);
        }

        internal static void GetUnitCenter(MasonryUnitPlacement unit, out float centerX, out float centerZ)
        {
            GetDirection(unit.Orientation, out float directionX, out float directionZ);
            centerX = (unit.Origin.X + 0.5f) * 0.25f + unit.OffsetX;
            centerZ = (unit.Origin.Z + 0.5f) * 0.25f + unit.OffsetZ;

            if (unit.Kind == MasonryUnitKind.WholeBrick)
            {
                float centerOffset = GetWholeBrickCenterOffset(unit.Orientation);
                centerX += directionX * centerOffset;
                centerZ += directionZ * centerOffset;
            }
            else if (unit.Kind == MasonryUnitKind.RammedEarth)
            {
                GetRammedEarthFootprintSteps(unit.Orientation, out int xStep, out int zStep);
                centerX += xStep * 0.125f;
                centerZ += zStep * 0.125f;
            }
        }

        internal static void SetUnitCenter(MasonryUnitPlacement unit, float centerX, float centerZ)
        {
            GetDirection(unit.Orientation, out float directionX, out float directionZ);
            if (unit.Kind == MasonryUnitKind.WholeBrick)
            {
                float centerOffset = GetWholeBrickCenterOffset(unit.Orientation);
                centerX -= directionX * centerOffset;
                centerZ -= directionZ * centerOffset;
            }
            else if (unit.Kind == MasonryUnitKind.RammedEarth)
            {
                GetRammedEarthFootprintSteps(unit.Orientation, out int xStep, out int zStep);
                centerX -= xStep * 0.125f;
                centerZ -= zStep * 0.125f;
            }

            unit.Origin.X = (int)MathF.Floor(centerX * 4);
            unit.Origin.Z = (int)MathF.Floor(centerZ * 4);
            unit.OffsetX = centerX - (unit.Origin.X + 0.5f) * 0.25f;
            unit.OffsetZ = centerZ - (unit.Origin.Z + 0.5f) * 0.25f;
        }

        internal static void GetUnitAxes(
            MasonryUnitPlacement unit,
            out float centerX,
            out float centerZ,
            out float directionX,
            out float directionZ,
            out float perpendicularX,
            out float perpendicularZ,
            out float halfLength,
            out float halfWidth)
        {
            GetUnitCenter(unit, out centerX, out centerZ);
            GetDirection(unit.Orientation, out directionX, out directionZ);
            perpendicularX = -directionZ;
            perpendicularZ = directionX;
            GetDimensions(unit.Kind, out float length, out float width);
            halfLength = length * 0.5f;
            halfWidth = width * 0.5f;
        }

        internal static IEnumerable<(float X, float Z)> GetUnitCorners(MasonryUnitPlacement unit)
        {
            GetUnitAxes(
                unit,
                out float centerX,
                out float centerZ,
                out float directionX,
                out float directionZ,
                out float perpendicularX,
                out float perpendicularZ,
                out float halfLength,
                out float halfWidth);
            int[] signs = { -1, 1 };
            foreach (int lengthSign in signs)
            foreach (int widthSign in signs)
            {
                yield return (
                    centerX + directionX * halfLength * lengthSign + perpendicularX * halfWidth * widthSign,
                    centerZ + directionZ * halfLength * lengthSign + perpendicularZ * halfWidth * widthSign);
            }
        }

        internal static IEnumerable<MasonryGridPosition> GetQuarterFootprint(MasonryUnitPlacement unit)
        {
            HashSet<MasonryGridPosition> cells = new();
            foreach ((int x, int y, int z) in GetVoxels(unit))
            {
                cells.Add(new MasonryGridPosition(FloorDiv(x, 4), FloorDiv(y, 4), FloorDiv(z, 4)));
            }

            return cells;
        }

        internal static IEnumerable<MasonryGridPosition> GetReservationFootprint(MasonryUnitPlacement unit)
        {
            MasonryGridPosition[] footprint = unit.GetFootprint().ToArray();
            if (!unit.IsDiagonal || footprint.Length == 0) return footprint;
            return footprint;
        }

        internal static IEnumerable<(int X, int Y, int Z)> GetVoxels(MasonryUnitPlacement unit)
        {
            GetDimensions(unit.Kind, out float length, out float width);
            GetDirection(unit.Orientation, out float directionX, out float directionZ);
            GetUnitCenter(unit, out float centerX, out float centerZ);

            int minimumX = (int)MathF.Floor((centerX - 0.5f) * Resolution);
            int maximumX = (int)MathF.Ceiling((centerX + 0.5f) * Resolution);
            int minimumZ = (int)MathF.Floor((centerZ - 0.5f) * Resolution);
            int maximumZ = (int)MathF.Ceiling((centerZ + 0.5f) * Resolution);
            int minimumY = unit.Origin.Y * 4;

            for (int x = minimumX; x < maximumX; x++)
            for (int z = minimumZ; z < maximumZ; z++)
            {
                float sampleX = (x + 0.5f) / Resolution - centerX;
                float sampleZ = (z + 0.5f) / Resolution - centerZ;
                float localLength = sampleX * directionX + sampleZ * directionZ;
                float localWidth = -sampleX * directionZ + sampleZ * directionX;
                if (MathF.Abs(localLength) > length * 0.5f + Epsilon
                    || MathF.Abs(localWidth) > width * 0.5f + Epsilon) continue;
                if (unit.VisualShape == MasonryVisualShape.TriangleWedge && localLength - localWidth > Epsilon) continue;

                for (int y = minimumY; y < minimumY + 4; y++) yield return (x, y, z);
            }
        }

        internal static Cuboidf[] BuildMergedBoxes(MasonryCellState state)
        {
            bool[,,] occupied = new bool[Resolution, Resolution, Resolution];
            foreach (MasonryUnitPlacement unit in state.Units)
            foreach ((int x, int y, int z) in GetVoxels(unit))
            {
                if (x is >= 0 and < Resolution && y is >= 0 and < Resolution && z is >= 0 and < Resolution)
                {
                    occupied[x, y, z] = true;
                }
            }

            foreach (MasonryUnitPlacement unit in state.ReservedUnits)
            foreach ((int x, int y, int z) in GetVoxels(unit))
            {
                if (x is >= 0 and < Resolution && y is >= 0 and < Resolution && z is >= 0 and < Resolution)
                {
                    occupied[x, y, z] = true;
                }
            }

            foreach (MasonryGridPosition voxel in state.EarthGapVoxels.Concat(state.MortarGapVoxels))
            {
                if (voxel.X is >= 0 and < Resolution && voxel.Y is >= 0 and < Resolution && voxel.Z is >= 0 and < Resolution)
                    occupied[voxel.X, voxel.Y, voxel.Z] = true;
            }

            return MergeVolume(occupied);
        }

        internal static Cuboidf[] BuildMergedBoxes(IEnumerable<MasonryGridPosition> voxels)
        {
            bool[,,] occupied = new bool[Resolution, Resolution, Resolution];
            foreach (MasonryGridPosition voxel in voxels)
            {
                if (voxel.X is >= 0 and < Resolution && voxel.Y is >= 0 and < Resolution && voxel.Z is >= 0 and < Resolution)
                    occupied[voxel.X, voxel.Y, voxel.Z] = true;
            }

            return MergeVolume(occupied, false);
        }

        // Finds enclosed microvoxel pockets too small for the smallest masonry
        // unit. The result is material-free derived geometry until an action
        // assigns it to rammed earth or mortar.
        internal static HashSet<MasonryGridPosition> FindUnusableGaps(MasonryCellState state)
        {
            bool[,,] occupied = new bool[Resolution, Resolution, Resolution];
            foreach (MasonryUnitPlacement unit in state.Units)
            foreach ((int x, int y, int z) in GetVoxels(unit))
                if (x is >= 0 and < Resolution && y is >= 0 and < Resolution && z is >= 0 and < Resolution) occupied[x, y, z] = true;
            foreach (MasonryUnitPlacement unit in state.ReservedUnits)
            foreach ((int x, int y, int z) in GetVoxels(unit))
                if (x is >= 0 and < Resolution && y is >= 0 and < Resolution && z is >= 0 and < Resolution) occupied[x, y, z] = true;
            foreach (MasonryGridPosition voxel in state.EarthGapVoxels.Concat(state.MortarGapVoxels))
                if (voxel.X is >= 0 and < Resolution && voxel.Y is >= 0 and < Resolution && voxel.Z is >= 0 and < Resolution) occupied[voxel.X, voxel.Y, voxel.Z] = true;

            HashSet<MasonryGridPosition> result = new();
            bool[,,] visited = new bool[Resolution, Resolution, Resolution];
            (int X, int Z)[] steps = { (1, 0), (-1, 0), (0, 1), (0, -1) };
            for (int y = 0; y < Resolution; y++)
            for (int z = 1; z < Resolution - 1; z++)
            for (int x = 1; x < Resolution - 1; x++)
            {
                if (occupied[x, y, z] || visited[x, y, z]) continue;
                Queue<(int X, int Z)> queue = new();
                List<(int X, int Z)> component = new();
                bool reachesBoundary = false;
                queue.Enqueue((x, z));
                visited[x, y, z] = true;
                while (queue.Count > 0)
                {
                    (int currentX, int currentZ) = queue.Dequeue();
                    component.Add((currentX, currentZ));
                    if (currentX == 0 || currentX == Resolution - 1 || currentZ == 0 || currentZ == Resolution - 1) reachesBoundary = true;
                    foreach ((int stepX, int stepZ) in steps)
                    {
                        int nextX = currentX + stepX;
                        int nextZ = currentZ + stepZ;
                        if (nextX is < 0 or >= Resolution || nextZ is < 0 or >= Resolution
                            || occupied[nextX, y, nextZ] || visited[nextX, y, nextZ]) continue;
                        visited[nextX, y, nextZ] = true;
                        queue.Enqueue((nextX, nextZ));
                    }
                }

                if (reachesBoundary || component.Count >= 16) continue;
                foreach ((int gapX, int gapZ) in component) result.Add(new MasonryGridPosition(gapX, y, gapZ));
            }

            return result;
        }

        // Keeps diagonal triangle and trapezoid pockets compact without
        // pretending that their irregular shape is a rectangular unit.
        internal static HashSet<MasonryGridPosition> FindSmallEnclosedGapAt(MasonryCellState state, MasonryGridPosition seed)
        {
            const int MaximumArea = 63;
            if (seed.X is < 0 or >= Resolution || seed.Y is < 0 or >= Resolution || seed.Z is < 0 or >= Resolution)
                return new HashSet<MasonryGridPosition>();

            bool[,,] occupied = new bool[Resolution, Resolution, Resolution];
            foreach (MasonryUnitPlacement unit in state.Units.Concat(state.ReservedUnits))
            foreach ((int x, int y, int z) in GetVoxels(unit))
                if (x is >= 0 and < Resolution && y is >= 0 and < Resolution && z is >= 0 and < Resolution) occupied[x, y, z] = true;
            foreach (MasonryGridPosition voxel in state.EarthGapVoxels.Concat(state.MortarGapVoxels))
                if (voxel.X is >= 0 and < Resolution && voxel.Y is >= 0 and < Resolution && voxel.Z is >= 0 and < Resolution) occupied[voxel.X, voxel.Y, voxel.Z] = true;
            if (occupied[seed.X, seed.Y, seed.Z]) return new HashSet<MasonryGridPosition>();

            (int X, int Z)[] steps = { (1, 0), (-1, 0), (0, 1), (0, -1) };
            Queue<(int X, int Z)> queue = new();
            HashSet<(int X, int Z)> visited = new();
            bool reachesBoundary = false;
            queue.Enqueue((seed.X, seed.Z));
            visited.Add((seed.X, seed.Z));
            while (queue.Count > 0)
            {
                (int x, int z) = queue.Dequeue();
                if (x == 0 || x == Resolution - 1 || z == 0 || z == Resolution - 1) reachesBoundary = true;
                foreach ((int stepX, int stepZ) in steps)
                {
                    int nextX = x + stepX;
                    int nextZ = z + stepZ;
                    if (nextX is < 0 or >= Resolution || nextZ is < 0 or >= Resolution
                        || occupied[nextX, seed.Y, nextZ] || !visited.Add((nextX, nextZ))) continue;
                    queue.Enqueue((nextX, nextZ));
                }
            }

            return reachesBoundary || visited.Count > MaximumArea
                ? new HashSet<MasonryGridPosition>()
                : visited.Select(position => new MasonryGridPosition(position.X, seed.Y, position.Z)).ToHashSet();
        }

        private static Cuboidf[] MergeVolume(bool[,,] occupied, bool includeEmptyFallback = true)
        {

            bool[,,] consumed = new bool[Resolution, Resolution, Resolution];
            List<Cuboidf> boxes = new();
            for (int y = 0; y < Resolution; y++)
            for (int z = 0; z < Resolution; z++)
            for (int x = 0; x < Resolution; x++)
            {
                if (!occupied[x, y, z] || consumed[x, y, z]) continue;
                int width = 1;
                while (x + width < Resolution && occupied[x + width, y, z] && !consumed[x + width, y, z]) width++;
                int depth = 1;
                while (z + depth < Resolution && RectangleFree(occupied, consumed, x, y, z + depth, width)) depth++;
                int height = 1;
                while (y + height < Resolution && PrismFree(occupied, consumed, x, y + height, z, width, depth)) height++;

                Mark(consumed, x, y, z, width, height, depth);
                boxes.Add(new Cuboidf(
                    x / 16f, y / 16f, z / 16f,
                    (x + width) / 16f, (y + height) / 16f, (z + depth) / 16f));
            }

            return boxes.Count == 0 && includeEmptyFallback ? new[] { new Cuboidf(0, 0, 0, 1, 0.01f, 1) } : boxes.ToArray();
        }

        internal static bool Overlaps(MasonryCellState state, MasonryUnitPlacement candidate)
        {
            HashSet<(int X, int Y, int Z)> candidateVoxels = GetVoxels(candidate).ToHashSet();
            return state.Units.Concat(state.ReservedUnits).SelectMany(GetVoxels).Any(candidateVoxels.Contains);
        }

        internal static MeshData DeformTriangle(MeshData mesh)
        {
            for (int vertex = 0; vertex < mesh.VerticesCount; vertex++)
            {
                int index = vertex * 3;
                float x = mesh.xyz[index];
                float z = mesh.xyz[index + 2];
                mesh.xyz[index] = x * (1f - z);
            }
            return mesh;
        }

        internal static MeshData TransformUnitMesh(MeshData mesh, MasonryUnitPlacement unit, float jointInset)
        {
            GetDimensions(unit.Kind, out float length, out float width);
            GetDirection(unit.Orientation, out float directionX, out float directionZ);
            GetUnitCenter(unit, out float centerX, out float centerZ);

            float scaledLength = length - jointInset * 2;
            float scaledHeight = 0.25f - jointInset * 2;
            float scaledWidth = width - jointInset * 2;
            for (int vertex = 0; vertex < mesh.VerticesCount; vertex++)
            {
                int index = vertex * 3;
                float localLength = -length * 0.5f + jointInset + mesh.xyz[index] * scaledLength;
                float localHeight = unit.Origin.Y * 0.25f + jointInset + mesh.xyz[index + 1] * scaledHeight;
                float localWidth = -width * 0.5f + jointInset + mesh.xyz[index + 2] * scaledWidth;
                mesh.xyz[index] = centerX + localLength * directionX - localWidth * directionZ;
                mesh.xyz[index + 1] = localHeight;
                mesh.xyz[index + 2] = centerZ + localLength * directionZ + localWidth * directionX;
            }

            return mesh;
        }

        private static bool RectangleFree(bool[,,] occupied, bool[,,] consumed, int x, int y, int z, int width)
        {
            for (int offsetX = 0; offsetX < width; offsetX++)
                if (!occupied[x + offsetX, y, z] || consumed[x + offsetX, y, z]) return false;
            return true;
        }

        private static bool PrismFree(bool[,,] occupied, bool[,,] consumed, int x, int y, int z, int width, int depth)
        {
            for (int offsetZ = 0; offsetZ < depth; offsetZ++)
            for (int offsetX = 0; offsetX < width; offsetX++)
                if (!occupied[x + offsetX, y, z + offsetZ] || consumed[x + offsetX, y, z + offsetZ]) return false;
            return true;
        }

        private static void Mark(bool[,,] consumed, int x, int y, int z, int width, int height, int depth)
        {
            for (int offsetY = 0; offsetY < height; offsetY++)
            for (int offsetZ = 0; offsetZ < depth; offsetZ++)
            for (int offsetX = 0; offsetX < width; offsetX++) consumed[x + offsetX, y + offsetY, z + offsetZ] = true;
        }

        private static int FloorDiv(int value, int divisor)
        {
            return (int)Math.Floor(value / (double)divisor);
        }
    }
}
