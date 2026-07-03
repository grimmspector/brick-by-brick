using AttributeRenderingLibrary;
using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace brickbybrick.RealisticConstruction
{
    // Culls internal quarter-cell faces, then greedily merges coplanar faces
    // that share a material. ARL resolves textures for each merged rectangle.
    internal static class MasonryStaticMeshBuilder
    {
        private const int GridSize = 4;
        private const float InternalEdgeOverlap = 0.0005f;

        private sealed record SurfaceMaterial(MasonryUnitKind Kind, string Color, bool IsMortar = false);

        private readonly record struct FaceDefinition(
            string Code,
            int NormalX,
            int NormalY,
            int NormalZ,
            int Axis,
            int Sign);

        private static readonly FaceDefinition[] Faces =
        {
            new("west", -1, 0, 0, 0, -1),
            new("east", 1, 0, 0, 0, 1),
            new("down", 0, -1, 0, 1, -1),
            new("up", 0, 1, 0, 1, 1),
            new("north", 0, 0, -1, 2, -1),
            new("south", 0, 0, 1, 2, 1)
        };

        internal static MeshData? Build(
            MasonryCellState state,
            BlockBehaviorShapeTexturesFromAttributes behavior,
            BlockPos pos,
            out int exposedQuads)
        {
            SurfaceMaterial?[,,] cells = BuildCellVolume(state);
            bool[,,] topMortar = BuildTopMortarVolume(state);
            MeshData? combined = null;
            int rawExposedQuads = 0;

            foreach (FaceDefinition face in Faces)
            {
                for (int slice = 0; slice < GridSize; slice++)
                {
                    SurfaceMaterial?[,] mask = new SurfaceMaterial?[GridSize, GridSize];
                    for (int u = 0; u < GridSize; u++)
                    for (int v = 0; v < GridSize; v++)
                    {
                        GetCoordinates(face.Axis, slice, u, v, out int x, out int y, out int z);
                        SurfaceMaterial? material = cells[x, y, z];
                        if (material == null) continue;

                        int neighborX = x + face.NormalX;
                        int neighborY = y + face.NormalY;
                        int neighborZ = z + face.NormalZ;
                        bool neighborOccupied = IsInside(neighborX, neighborY, neighborZ)
                            && cells[neighborX, neighborY, neighborZ] != null;
                        if (!neighborOccupied)
                        {
                            mask[u, v] = face.Code == "up" && topMortar[x, y, z]
                                ? new SurfaceMaterial(material.Kind, material.Color, true)
                                : material;
                            rawExposedQuads++;
                        }
                    }

                    MergeMask(mask, (u, v, width, height, material) =>
                    {
                        MeshData rectangle = CreateRectangle(behavior, pos, face, slice, u, v, width, height, material);
                        if (combined == null) combined = rectangle.Clone();
                        else combined.AddMeshData(rectangle);
                    });
                }
            }

            exposedQuads = rawExposedQuads;
            return combined;
        }

        internal static bool TryBuildValidated(
            MasonryCellState state,
            BlockBehaviorShapeTexturesFromAttributes behavior,
            BlockPos pos,
            out MeshData? mesh,
            out int exposedQuads,
            out string rejectionReason)
        {
            try
            {
                mesh = Build(state, behavior, pos, out exposedQuads);
                rejectionReason = Validate(mesh, exposedQuads);
                return rejectionReason.Length == 0;
            }
            catch (Exception exception)
            {
                mesh = null;
                exposedQuads = 0;
                rejectionReason = exception.Message;
                return false;
            }
        }

        private static string Validate(MeshData? mesh, int exposedQuads)
        {
            if (mesh == null) return "builder returned no mesh";
            if (mesh.VerticesCount <= 0 || mesh.IndicesCount <= 0) return "mesh contains no geometry";
            if (mesh.VerticesCount % 4 != 0 || mesh.IndicesCount % 6 != 0) return "mesh has incomplete quads";
            if (mesh.IndicesCount / 6 > exposedQuads) return "merged mesh exceeds exposed-face count";
            int quadCount = mesh.IndicesCount / 6;
            if (mesh.XyzFaces == null || mesh.XyzFacesCount != quadCount || mesh.XyzFaces.Length < quadCount)
            {
                return $"mesh face-lighting metadata is incomplete (faces {mesh.XyzFacesCount}, array {mesh.XyzFaces?.Length ?? 0}, quads {quadCount})";
            }
            if (mesh.TextureIndices == null || mesh.TextureIndicesCount != quadCount || mesh.TextureIndices.Length < quadCount) return "mesh texture-index metadata is incomplete";
            if (mesh.RenderPassesAndExtraBits != null && (mesh.RenderPassCount != quadCount || mesh.RenderPassesAndExtraBits.Length < quadCount)) return "mesh render-pass metadata is incomplete";
            if (mesh.ClimateColorMapIds != null && mesh.ClimateColorMapIds.Length < quadCount) return "mesh climate metadata is incomplete";
            if (mesh.SeasonColorMapIds != null && mesh.SeasonColorMapIds.Length < quadCount) return "mesh season metadata is incomplete";
            if (mesh.FrostableBits != null && mesh.FrostableBits.Length < quadCount) return "mesh frost metadata is incomplete";
            if (mesh.Uv == null || mesh.Uv.Length < mesh.VerticesCount * 2) return "mesh UV metadata is incomplete";
            if (mesh.Rgba == null || mesh.Rgba.Length < mesh.VerticesCount * 4) return "mesh color metadata is incomplete";
            if (mesh.Flags == null || mesh.Flags.Length < mesh.VerticesCount) return "mesh vertex flags are incomplete";
            if (mesh.Normals != null && (mesh.NormalsCount != mesh.VerticesCount || mesh.Normals.Length < mesh.VerticesCount)) return "mesh normals are incomplete";

            for (int index = 0; index < mesh.VerticesCount * 3; index++)
            {
                float coordinate = mesh.xyz[index];
                if (!float.IsFinite(coordinate)) return "mesh contains a non-finite coordinate";
                if (coordinate < -0.001f || coordinate > 1.001f) return "mesh extends outside its block cell";
            }

            for (int index = 0; index < mesh.IndicesCount; index++)
            {
                if (mesh.Indices[index] < 0 || mesh.Indices[index] >= mesh.VerticesCount) return "mesh contains an invalid vertex index";
            }

            return string.Empty;
        }

        private static SurfaceMaterial?[,,] BuildCellVolume(MasonryCellState state)
        {
            SurfaceMaterial?[,,] cells = new SurfaceMaterial?[GridSize, GridSize, GridSize];
            foreach (MasonryUnitPlacement unit in state.Units)
            {
                SurfaceMaterial material = new(unit.Kind, GetMaterialColor(unit));
                foreach (MasonryGridPosition cell in unit.GetFootprint())
                {
                    if (IsInside(cell.X, cell.Y, cell.Z)) cells[cell.X, cell.Y, cell.Z] = material;
                }
            }

            return cells;
        }

        private static bool[,,] BuildTopMortarVolume(MasonryCellState state)
        {
            bool[,,] mortar = new bool[GridSize, GridSize, GridSize];
            foreach (MasonryUnitPlacement unit in state.Units)
            foreach (MasonryGridPosition cell in unit.MortaredPositions)
            {
                if (IsInside(cell.X, cell.Y, cell.Z)) mortar[cell.X, cell.Y, cell.Z] = true;
            }

            return mortar;
        }

        private static void MergeMask(
            SurfaceMaterial?[,] mask,
            Action<int, int, int, int, SurfaceMaterial> emit)
        {
            bool[,] consumed = new bool[GridSize, GridSize];
            for (int v = 0; v < GridSize; v++)
            for (int u = 0; u < GridSize; u++)
            {
                SurfaceMaterial? material = mask[u, v];
                if (material == null || consumed[u, v]) continue;

                int width = 1;
                while (u + width < GridSize
                    && !consumed[u + width, v]
                    && Equals(mask[u + width, v], material)) width++;

                int height = 1;
                bool canGrow = true;
                while (v + height < GridSize && canGrow)
                {
                    for (int offset = 0; offset < width; offset++)
                    {
                        if (consumed[u + offset, v + height]
                            || !Equals(mask[u + offset, v + height], material))
                        {
                            canGrow = false;
                            break;
                        }
                    }

                    if (canGrow) height++;
                }

                for (int usedU = 0; usedU < width; usedU++)
                for (int usedV = 0; usedV < height; usedV++)
                    consumed[u + usedU, v + usedV] = true;
                emit(u, v, width, height, material);
            }
        }

        private static MeshData CreateRectangle(
            BlockBehaviorShapeTexturesFromAttributes behavior,
            BlockPos pos,
            FaceDefinition face,
            int slice,
            int u,
            int v,
            int width,
            int height,
            SurfaceMaterial material)
        {
            bool earth = material.Kind == MasonryUnitKind.RammedEarth;
            CompositeShape shape = new()
            {
                Base = new AssetLocation(material.IsMortar
                    ? "brickbybrick:shapes/block/realistic/mortar.json"
                    : earth
                        ? "brickbybrick:shapes/block/realistic/rammedearth.json"
                        : "brickbybrick:shapes/block/realistic/brick.json")
            };
            Variants variants = new();
            variants.Set("color", material.Color);
            MeshData cube = behavior.GetOrCreateMesh(variants, shape, pos, $"surface-source-{material.IsMortar}-{earth}-{material.Color}");
            byte requestedFace = (byte)(BlockFacing.FromCode(face.Code).Index + 1);
            MeshData mesh = CopyFace(cube, requestedFace);

            GetRectangleTransform(face, slice, u, v, width, height, out float x, out float y, out float z, out float scaleX, out float scaleY, out float scaleZ);
            return mesh.MatrixTransform(Matrixf.Create().Translate(x, y, z).Scale(scaleX, scaleY, scaleZ).Values);
        }

        private static MeshData CopyFace(MeshData source, byte requestedFace)
        {
            int faceIndex = Array.IndexOf(source.XyzFaces, requestedFace, 0, source.XyzFacesCount);
            if (faceIndex < 0) throw new InvalidOperationException($"ARL source mesh does not contain face {requestedFace}.");

            int verticesPerFace = source.VerticesPerFace > 0 ? source.VerticesPerFace : 4;
            int indicesPerFace = source.IndicesPerFace > 0 ? source.IndicesPerFace : 6;
            int sourceVertex = faceIndex * verticesPerFace;
            int sourceIndex = faceIndex * indicesPerFace;
            MeshData mesh = new(
                verticesPerFace,
                indicesPerFace,
                source.Normals != null,
                source.Uv != null,
                source.Rgba != null,
                source.Flags != null)
            {
                mode = source.mode,
                VerticesPerFace = verticesPerFace,
                IndicesPerFace = indicesPerFace,
                HasAnyWindModeSet = source.HasAnyWindModeSet,
                XyzFaces = new[] { requestedFace },
                XyzFacesCount = 1,
                TextureIds = source.TextureIds == null ? Array.Empty<int>() : (int[])source.TextureIds.Clone(),
                TextureIndices = CopyFaceValue(source.TextureIndices, faceIndex),
                TextureIndicesCount = source.TextureIndices == null ? 0 : 1,
                ClimateColorMapIds = CopyFaceValue(source.ClimateColorMapIds, faceIndex),
                SeasonColorMapIds = CopyFaceValue(source.SeasonColorMapIds, faceIndex),
                FrostableBits = CopyFaceValue(source.FrostableBits, faceIndex),
                RenderPassesAndExtraBits = CopyFaceValue(source.RenderPassesAndExtraBits, faceIndex),
                ColorMapIdsCount = source.ClimateColorMapIds == null && source.SeasonColorMapIds == null ? 0 : 1,
                RenderPassCount = source.RenderPassesAndExtraBits == null ? 0 : 1
            };

            Array.Copy(source.xyz, sourceVertex * 3, mesh.xyz, 0, verticesPerFace * 3);
            if (source.Uv != null) Array.Copy(source.Uv, sourceVertex * 2, mesh.Uv, 0, verticesPerFace * 2);
            if (source.Rgba != null) Array.Copy(source.Rgba, sourceVertex * 4, mesh.Rgba, 0, verticesPerFace * 4);
            if (source.Flags != null) Array.Copy(source.Flags, sourceVertex, mesh.Flags, 0, verticesPerFace);
            if (source.Normals != null) Array.Copy(source.Normals, sourceVertex, mesh.Normals, 0, verticesPerFace);
            for (int index = 0; index < indicesPerFace; index++) mesh.Indices[index] = source.Indices[sourceIndex + index] - sourceVertex;
            mesh.VerticesCount = verticesPerFace;
            mesh.IndicesCount = indicesPerFace;
            mesh.NormalsCount = source.Normals == null ? 0 : verticesPerFace;
            return mesh;
        }

        private static T[]? CopyFaceValue<T>(T[]? source, int faceIndex)
        {
            return source == null ? null : new[] { source[faceIndex] };
        }

        private static void GetRectangleTransform(
            FaceDefinition face,
            int slice,
            int u,
            int v,
            int width,
            int height,
            out float x,
            out float y,
            out float z,
            out float scaleX,
            out float scaleY,
            out float scaleZ)
        {
            float plane = (slice + (face.Sign > 0 ? 1 : 0)) * 0.25f;
            x = y = z = 0;
            scaleX = scaleY = scaleZ = 1;
            if (face.Axis == 0)
            {
                x = plane - (face.Sign > 0 ? 1 : 0);
                GetExpandedRange(v, height, out y, out scaleY);
                GetExpandedRange(u, width, out z, out scaleZ);
            }
            else if (face.Axis == 1)
            {
                GetExpandedRange(u, width, out x, out scaleX);
                y = plane - (face.Sign > 0 ? 1 : 0);
                GetExpandedRange(v, height, out z, out scaleZ);
            }
            else
            {
                GetExpandedRange(u, width, out x, out scaleX);
                GetExpandedRange(v, height, out y, out scaleY);
                z = plane - (face.Sign > 0 ? 1 : 0);
            }
        }

        private static void GetExpandedRange(int startCell, int cellCount, out float start, out float length)
        {
            float lowerOverlap = startCell > 0 ? InternalEdgeOverlap : 0;
            float upperOverlap = startCell + cellCount < GridSize ? InternalEdgeOverlap : 0;
            start = startCell * 0.25f - lowerOverlap;
            length = cellCount * 0.25f + lowerOverlap + upperOverlap;
        }

        private static void GetCoordinates(int axis, int slice, int u, int v, out int x, out int y, out int z)
        {
            if (axis == 0) { x = slice; y = v; z = u; return; }
            if (axis == 1) { x = u; y = slice; z = v; return; }
            x = u; y = v; z = slice;
        }

        private static bool IsInside(int x, int y, int z)
        {
            return x is >= 0 and < GridSize && y is >= 0 and < GridSize && z is >= 0 and < GridSize;
        }

        private static string GetMaterialColor(MasonryUnitPlacement unit)
        {
            int separator = unit.MaterialCode.LastIndexOf('-');
            return separator >= 0 ? unit.MaterialCode[(separator + 1)..] : "cream";
        }
    }
}
