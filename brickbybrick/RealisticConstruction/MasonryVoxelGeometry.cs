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

        internal static IEnumerable<MasonryGridPosition> GetQuarterFootprint(MasonryUnitPlacement unit)
        {
            HashSet<MasonryGridPosition> cells = new();
            foreach ((int x, int y, int z) in GetVoxels(unit))
            {
                cells.Add(new MasonryGridPosition(FloorDiv(x, 4), FloorDiv(y, 4), FloorDiv(z, 4)));
            }

            return cells;
        }

        internal static IEnumerable<(int X, int Y, int Z)> GetVoxels(MasonryUnitPlacement unit)
        {
            GetDimensions(unit.Kind, out float length, out float width);
            float angle = GetAngleDegrees(unit.Orientation) * GameMath.DEG2RAD;
            float directionX = MathF.Cos(angle);
            float directionZ = MathF.Sin(angle);
            float centerX = (unit.Origin.X + 0.5f) * 0.25f;
            float centerZ = (unit.Origin.Z + 0.5f) * 0.25f;

            if (unit.Kind == MasonryUnitKind.WholeBrick)
            {
                centerX += directionX * 0.125f;
                centerZ += directionZ * 0.125f;
            }

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
                if (unit.Kind == MasonryUnitKind.TriangleBrick && localLength + localWidth > Epsilon) continue;

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
            return state.Units.SelectMany(GetVoxels).Any(candidateVoxels.Contains);
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

        private static void GetDimensions(MasonryUnitKind kind, out float length, out float width)
        {
            length = kind == MasonryUnitKind.WholeBrick ? 0.5f : kind == MasonryUnitKind.RammedEarth ? 0.5f : 0.25f;
            width = kind == MasonryUnitKind.RammedEarth ? 0.5f : 0.25f;
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
