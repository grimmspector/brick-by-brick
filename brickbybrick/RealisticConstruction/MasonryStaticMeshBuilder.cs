using AttributeRenderingLibrary;
using System;
using System.Collections.Generic;
using System.Linq;
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
        private const int HorizontalGridSize = 6;
        private const int HorizontalGridOffset = 1;
        private const float InternalEdgeOverlap = 0.0005f;
        private const float MortarJointInset = 0.0078125f;
        private const float MortarRecess = 0.0078125f;

        private readonly record struct SurfaceMaterial(
            MasonryUnitKind Kind,
            string Color,
            int UnitIndex,
            byte MortarEdges,
            byte FilledMortarEdges,
            string MortarMaterial,
            bool IsMortar = false);

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
                GetFaceDimensions(face.Axis, out int sliceCount, out int uCount, out int vCount);
                for (int slice = 0; slice < sliceCount; slice++)
                {
                    SurfaceMaterial?[] mask = new SurfaceMaterial?[uCount * vCount];
                    for (int u = 0; u < uCount; u++)
                    for (int v = 0; v < vCount; v++)
                    {
                        GetCoordinates(face.Axis, slice, u, v, out int x, out int y, out int z);
                        SurfaceMaterial? material = cells[x + HorizontalGridOffset, y, z + HorizontalGridOffset];
                        if (material == null) continue;

                        int neighborX = x + face.NormalX;
                        int neighborY = y + face.NormalY;
                        int neighborZ = z + face.NormalZ;
                        bool neighborOccupied = IsInside(neighborX, neighborY, neighborZ)
                            && cells[neighborX + HorizontalGridOffset, neighborY, neighborZ + HorizontalGridOffset] != null;
                        if (!neighborOccupied)
                        {
                            mask[u + v * uCount] = face.Code == "up" && topMortar[x + HorizontalGridOffset, y, z + HorizontalGridOffset]
                                ? material.Value with { IsMortar = true, MortarEdges = 0, FilledMortarEdges = 0 }
                                : material;
                            rawExposedQuads++;
                        }
                    }

                    ApplyTopologyEdges(state, mask, uCount, vCount);
                    MergeMask(mask, uCount, vCount, behavior, pos, face, slice, ref combined);
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
                if (state.Units.Count == 0)
                {
                    mesh = null;
                    exposedQuads = 0;
                    rejectionReason = "cell has no locally owned units";
                    return false;
                }
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
            // A face may contain brick, four reveals, one cavity backing, and
            // four mortar strips; internal contacts add no geometry.
            if (mesh.IndicesCount / 6 > exposedQuads * 10) return "mesh exceeds the exposed-face mortar budget";
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
                if (coordinate < -0.251f || coordinate > 1.251f) return "mesh extends beyond its owner-cell apron";
            }

            for (int index = 0; index < mesh.IndicesCount; index++)
            {
                if (mesh.Indices[index] < 0 || mesh.Indices[index] >= mesh.VerticesCount) return "mesh contains an invalid vertex index";
            }

            return string.Empty;
        }

        private static SurfaceMaterial?[,,] BuildCellVolume(MasonryCellState state)
        {
            SurfaceMaterial?[,,] cells = new SurfaceMaterial?[HorizontalGridSize, GridSize, HorizontalGridSize];
            for (int unitIndex = 0; unitIndex < state.Units.Count; unitIndex++)
            {
                MasonryUnitPlacement unit = state.Units[unitIndex];
                SurfaceMaterial material = new(
                    unit.Kind,
                    GetMaterialColor(unit),
                    unitIndex,
                    0,
                    0,
                    state.MortarMaterialCode,
                    false);
                foreach (MasonryGridPosition cell in unit.GetFootprint())
                {
                    if (IsInside(cell.X, cell.Y, cell.Z))
                    {
                        cells[cell.X + HorizontalGridOffset, cell.Y, cell.Z + HorizontalGridOffset] = material;
                    }
                }
            }

            return cells;
        }

        private static bool[,,] BuildTopMortarVolume(MasonryCellState state)
        {
            bool[,,] mortar = new bool[HorizontalGridSize, GridSize, HorizontalGridSize];
            foreach (MasonryUnitPlacement unit in state.Units)
            foreach (MasonryGridPosition cell in unit.MortaredPositions)
            {
                if (IsInside(cell.X, cell.Y, cell.Z))
                {
                    mortar[cell.X + HorizontalGridOffset, cell.Y, cell.Z + HorizontalGridOffset] = true;
                }
            }

            return mortar;
        }

        private static void ApplyTopologyEdges(MasonryCellState state, SurfaceMaterial?[] mask, int uCount, int vCount)
        {
            for (int v = 0; v < vCount; v++)
            for (int u = 0; u < uCount; u++)
            {
                int index = u + v * uCount;
                SurfaceMaterial? material = mask[index];
                if (material == null || material.Value.Kind == MasonryUnitKind.RammedEarth || material.Value.IsMortar) continue;

                byte edges = 0;
                byte filledEdges = 0;
                AddEdge(u - 1, v, 1);
                AddEdge(u + 1, v, 2);
                AddEdge(u, v - 1, 4);
                AddEdge(u, v + 1, 8);
                mask[index] = material.Value with { MortarEdges = edges, FilledMortarEdges = filledEdges };

                void AddEdge(int neighborU, int neighborV, byte edge)
                {
                    SurfaceMaterial? neighbor = GetDifferentUnit(neighborU, neighborV, material.Value.UnitIndex);
                    if (neighbor == null) return;
                    edges |= edge;
                    if (AreUnitsMortared(state, material.Value.UnitIndex, neighbor.Value.UnitIndex)) filledEdges |= edge;
                }
            }

            SurfaceMaterial? GetDifferentUnit(int u, int v, int unitIndex)
            {
                if (u < 0 || u >= uCount || v < 0 || v >= vCount) return null;
                SurfaceMaterial? neighbor = mask[u + v * uCount];
                return neighbor != null && neighbor.Value.UnitIndex != unitIndex ? neighbor : null;
            }
        }

        private static bool AreUnitsMortared(MasonryCellState state, int firstIndex, int secondIndex)
        {
            MasonryUnitPlacement first = state.Units[firstIndex];
            MasonryUnitPlacement second = state.Units[secondIndex];
            foreach (MasonryGridPosition firstPosition in first.GetFootprint())
            foreach (MasonryGridPosition secondPosition in second.GetFootprint())
            {
                int distance = Math.Abs(firstPosition.X - secondPosition.X)
                    + Math.Abs(firstPosition.Y - secondPosition.Y)
                    + Math.Abs(firstPosition.Z - secondPosition.Z);
                if (distance != 1) continue;
                if (first.MortaredPositions.Contains(firstPosition) || second.MortaredPositions.Contains(secondPosition)) return true;
                if (state.MortaredSideJoints.Contains(GetJointKey(firstPosition, secondPosition))) return true;
            }

            return false;
        }

        private static void MergeMask(
            SurfaceMaterial?[] mask,
            int uCount,
            int vCount,
            BlockBehaviorShapeTexturesFromAttributes behavior,
            BlockPos pos,
            FaceDefinition face,
            int slice,
            ref MeshData? combined)
        {
            // Sixteen bits cover one face slice, avoiding a second heap array
            // and its multidimensional index overhead on every cold build.
            ulong consumed = 0;
            for (int v = 0; v < vCount; v++)
            for (int u = 0; u < uCount; u++)
            {
                int cellIndex = u + v * uCount;
                SurfaceMaterial? material = mask[cellIndex];
                if (material == null || (consumed & 1UL << cellIndex) != 0) continue;

                int width = 1;
                while (u + width < uCount
                    && (consumed & 1UL << (cellIndex + width)) == 0
                    && Equals(mask[cellIndex + width], material)) width++;

                int height = 1;
                bool canGrow = true;
                while (v + height < vCount && canGrow)
                {
                    for (int offset = 0; offset < width; offset++)
                    {
                        int candidateIndex = u + offset + (v + height) * uCount;
                        if ((consumed & 1UL << candidateIndex) != 0
                            || !Equals(mask[candidateIndex], material))
                        {
                            canGrow = false;
                            break;
                        }
                    }

                    if (canGrow) height++;
                }

                for (int usedU = 0; usedU < width; usedU++)
                for (int usedV = 0; usedV < height; usedV++)
                    consumed |= 1UL << (u + usedU + (v + usedV) * uCount);

                MeshData rectangle = CreateRectangle(behavior, pos, face, slice, u, v, width, height, material.Value);
                if (combined == null) combined = rectangle.Clone();
                else combined.AddMeshData(rectangle);
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
            MeshData brick = CreateSurfaceRectangle(
                behavior,
                pos,
                face,
                slice,
                u,
                v,
                width,
                height,
                material,
                material.MortarEdges);
            if (material.IsMortar || material.MortarEdges == 0) return brick;
            AddMortarReveals(brick, behavior, pos, face, slice, u, v, width, height, material);
            AddFullSeamBacking(brick, behavior, pos, face, slice, u, v, width, height, material);
            AddSeamBackings(brick, behavior, pos, face, slice, u, v, width, height, material);
            return brick;
        }

        private static MeshData CreateSurfaceRectangle(
            BlockBehaviorShapeTexturesFromAttributes behavior,
            BlockPos pos,
            FaceDefinition face,
            int slice,
            int u,
            int v,
            int width,
            int height,
            SurfaceMaterial material,
            byte mortarEdges,
            float planeOffset = 0)
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
            if (!material.IsMortar) variants.Set("color", material.Color);
            MeshData cube = behavior.GetOrCreateMesh(variants, shape, pos, $"surface-source-{material.IsMortar}-{earth}-{material.Color}");
            byte requestedFace = (byte)(BlockFacing.FromCode(face.Code).Index + 1);
            MeshData mesh = CopyFace(cube, requestedFace);

            GetRectangleTransform(face, slice, u, v, width, height, out float x, out float y, out float z, out float scaleX, out float scaleY, out float scaleZ);
            ApplyMortarInsets(face.Axis, mortarEdges, ref x, ref y, ref z, ref scaleX, ref scaleY, ref scaleZ);
            x += face.NormalX * planeOffset;
            y += face.NormalY * planeOffset;
            z += face.NormalZ * planeOffset;
            return mesh.MatrixTransform(Matrixf.Create().Translate(x, y, z).Scale(scaleX, scaleY, scaleZ).Values);
        }

        private static void AddMortarReveals(
            MeshData combined,
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
            GetRectangleTransform(face, slice, u, v, width, height, out float x, out float y, out float z, out float scaleX, out float scaleY, out float scaleZ);
            ApplyMortarInsets(face.Axis, material.MortarEdges, ref x, ref y, ref z, ref scaleX, ref scaleY, ref scaleZ);
            float recess = MortarRecess;
            float[] minimum = { x, y, z };
            float[] maximum = { x + scaleX, y + scaleY, z + scaleZ };
            int logicalSlice = face.Axis == 1 ? slice : slice - HorizontalGridOffset;
            float outerPlane = (logicalSlice + (face.Sign > 0 ? 1 : 0)) * 0.25f;
            minimum[face.Axis] = Math.Min(outerPlane, outerPlane - face.Sign * recess);
            maximum[face.Axis] = Math.Max(outerPlane, outerPlane - face.Sign * recess);

            GetTangentAxes(face.Axis, out int firstAxis, out int secondAxis);
            if ((material.MortarEdges & 1) != 0) AddReveal(firstAxis, -1, minimum[firstAxis]);
            if ((material.MortarEdges & 2) != 0) AddReveal(firstAxis, 1, maximum[firstAxis]);
            if ((material.MortarEdges & 4) != 0) AddReveal(secondAxis, -1, minimum[secondAxis]);
            if ((material.MortarEdges & 8) != 0) AddReveal(secondAxis, 1, maximum[secondAxis]);

            void AddReveal(int revealAxis, int sign, float plane)
            {
                FaceDefinition revealFace = GetFace(revealAxis, sign);
                float[] revealMinimum = (float[])minimum.Clone();
                float[] revealMaximum = (float[])maximum.Clone();
                revealMinimum[revealAxis] = plane;
                revealMaximum[revealAxis] = plane;
                combined.AddMeshData(CreateBoundedFace(behavior, pos, revealFace, material, revealMinimum, revealMaximum));
            }
        }

        private static void AddSeamBackings(
            MeshData combined,
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
            if (material.FilledMortarEdges == 0) return;

            GetRectangleTransform(face, slice, u, v, width, height, out float x, out float y, out float z, out float scaleX, out float scaleY, out float scaleZ);
            float[] outerMinimum = { x, y, z };
            float[] outerMaximum = { x + scaleX, y + scaleY, z + scaleZ };
            ApplyMortarInsets(face.Axis, material.MortarEdges, ref x, ref y, ref z, ref scaleX, ref scaleY, ref scaleZ);
            float[] innerMinimum = { x, y, z };
            float[] innerMaximum = { x + scaleX, y + scaleY, z + scaleZ };
            int logicalSlice = face.Axis == 1 ? slice : slice - HorizontalGridOffset;
            float plane = (logicalSlice + (face.Sign > 0 ? 1 : 0)) * 0.25f
                - face.Sign * (MortarRecess - InternalEdgeOverlap);
            GetTangentAxes(face.Axis, out int firstAxis, out int secondAxis);

            if ((material.FilledMortarEdges & 1) != 0) AddStrip(1, firstAxis, outerMinimum[firstAxis], innerMinimum[firstAxis]);
            if ((material.FilledMortarEdges & 2) != 0) AddStrip(2, firstAxis, innerMaximum[firstAxis], outerMaximum[firstAxis]);
            if ((material.FilledMortarEdges & 4) != 0) AddStrip(4, secondAxis, outerMinimum[secondAxis], innerMinimum[secondAxis]);
            if ((material.FilledMortarEdges & 8) != 0) AddStrip(8, secondAxis, innerMaximum[secondAxis], outerMaximum[secondAxis]);

            void AddStrip(byte edge, int stripAxis, float minimumValue, float maximumValue)
            {
                float[] minimum = (float[])outerMinimum.Clone();
                float[] maximum = (float[])outerMaximum.Clone();
                minimum[face.Axis] = maximum[face.Axis] = plane;
                minimum[stripAxis] = minimumValue;
                maximum[stripAxis] = maximumValue;
                bool filled = (material.FilledMortarEdges & edge) != 0;
                SurfaceMaterial backing = filled
                    ? material with { Color = material.MortarMaterial, IsMortar = true }
                    : material with { IsMortar = false };
                combined.AddMeshData(CreateBoundedFace(behavior, pos, face, backing, minimum, maximum));
            }
        }

        private static void AddFullSeamBacking(
            MeshData combined,
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
            GetRectangleTransform(face, slice, u, v, width, height, out float x, out float y, out float z, out float scaleX, out float scaleY, out float scaleZ);
            float[] minimum = { x, y, z };
            float[] maximum = { x + scaleX, y + scaleY, z + scaleZ };
            int logicalSlice = face.Axis == 1 ? slice : slice - HorizontalGridOffset;
            float plane = (logicalSlice + (face.Sign > 0 ? 1 : 0)) * 0.25f - face.Sign * MortarRecess;
            minimum[face.Axis] = maximum[face.Axis] = plane;
            SurfaceMaterial brickBacking = material with { IsMortar = false };
            combined.AddMeshData(CreateBoundedFace(behavior, pos, face, brickBacking, minimum, maximum));
        }

        private static MeshData CreateBoundedFace(
            BlockBehaviorShapeTexturesFromAttributes behavior,
            BlockPos pos,
            FaceDefinition face,
            SurfaceMaterial material,
            float[] minimum,
            float[] maximum)
        {
            CompositeShape shape = new()
            {
                Base = new AssetLocation(material.IsMortar
                    ? "brickbybrick:shapes/block/realistic/mortar.json"
                    : material.Kind == MasonryUnitKind.RammedEarth
                    ? "brickbybrick:shapes/block/realistic/rammedearth.json"
                    : "brickbybrick:shapes/block/realistic/brick.json")
            };
            Variants variants = new();
            if (!material.IsMortar) variants.Set("color", material.Color);
            MeshData cube = behavior.GetOrCreateMesh(variants, shape, pos, $"surface-source-{material.IsMortar}-{material.Kind == MasonryUnitKind.RammedEarth}-{material.Color}");
            MeshData mesh = CopyFace(cube, (byte)(BlockFacing.FromCode(face.Code).Index + 1));
            float[] scale = { maximum[0] - minimum[0], maximum[1] - minimum[1], maximum[2] - minimum[2] };
            float translateX = minimum[0] - (face.Axis == 0 && face.Sign > 0 ? 1 : 0);
            float translateY = minimum[1] - (face.Axis == 1 && face.Sign > 0 ? 1 : 0);
            float translateZ = minimum[2] - (face.Axis == 2 && face.Sign > 0 ? 1 : 0);
            if (face.Axis == 0) scale[0] = 1;
            if (face.Axis == 1) scale[1] = 1;
            if (face.Axis == 2) scale[2] = 1;
            return mesh.MatrixTransform(Matrixf.Create().Translate(translateX, translateY, translateZ).Scale(scale[0], scale[1], scale[2]).Values);
        }

        private static void ApplyMortarInsets(
            int axis,
            byte edges,
            ref float x,
            ref float y,
            ref float z,
            ref float scaleX,
            ref float scaleY,
            ref float scaleZ)
        {
            if (edges == 0) return;

            if (axis == 0)
            {
                InsetRange(ref z, ref scaleZ, edges);
                InsetRange(ref y, ref scaleY, (byte)((edges >> 2) | (edges << 2)));
            }
            else if (axis == 1)
            {
                InsetRange(ref x, ref scaleX, edges);
                InsetRange(ref z, ref scaleZ, (byte)((edges >> 2) | (edges << 2)));
            }
            else
            {
                InsetRange(ref x, ref scaleX, edges);
                InsetRange(ref y, ref scaleY, (byte)((edges >> 2) | (edges << 2)));
            }
        }

        private static void InsetRange(ref float start, ref float length, byte edges)
        {
            if ((edges & 1) != 0)
            {
                start += MortarJointInset;
                length -= MortarJointInset;
            }
            if ((edges & 2) != 0) length -= MortarJointInset;
        }

        private static void GetTangentAxes(int normalAxis, out int firstAxis, out int secondAxis)
        {
            if (normalAxis == 0) { firstAxis = 2; secondAxis = 1; return; }
            if (normalAxis == 1) { firstAxis = 0; secondAxis = 2; return; }
            firstAxis = 0;
            secondAxis = 1;
        }

        private static FaceDefinition GetFace(int axis, int sign)
        {
            foreach (FaceDefinition face in Faces)
            {
                if (face.Axis == axis && face.Sign == sign) return face;
            }

            throw new InvalidOperationException("Masonry reveal face is unavailable.");
        }

        private static MeshData CopyFace(MeshData source, byte requestedFace)
        {
            int faceIndex = Array.IndexOf(source.XyzFaces, requestedFace, 0, source.XyzFacesCount);
            if (faceIndex < 0) throw new InvalidOperationException($"ARL source mesh does not contain face {requestedFace}.");

            int indicesPerFace = source.IndicesPerFace > 0 ? source.IndicesPerFace : 6;
            int sourceIndex = faceIndex * indicesPerFace;
            Span<int> sourceVertices = stackalloc int[4];
            int vertexCount = 0;
            for (int index = 0; index < indicesPerFace; index++)
            {
                int sourceVertex = source.Indices[sourceIndex + index];
                bool known = false;
                for (int vertex = 0; vertex < vertexCount; vertex++)
                {
                    if (sourceVertices[vertex] != sourceVertex) continue;
                    known = true;
                    break;
                }
                if (!known && vertexCount < sourceVertices.Length) sourceVertices[vertexCount++] = sourceVertex;
            }
            if (vertexCount != 4) throw new InvalidOperationException($"ARL source face {requestedFace} does not reference exactly four vertices.");

            MeshData mesh = new(
                vertexCount,
                indicesPerFace,
                source.Normals != null,
                source.Uv != null,
                source.Rgba != null,
                source.Flags != null)
            {
                mode = source.mode,
                VerticesPerFace = vertexCount,
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

            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                int sourceVertex = sourceVertices[vertex];
                Array.Copy(source.xyz, sourceVertex * 3, mesh.xyz, vertex * 3, 3);
                if (source.Uv != null) Array.Copy(source.Uv, sourceVertex * 2, mesh.Uv, vertex * 2, 2);
                if (source.Rgba != null) Array.Copy(source.Rgba, sourceVertex * 4, mesh.Rgba, vertex * 4, 4);
                if (source.Flags != null) mesh.Flags[vertex] = source.Flags[sourceVertex];
                if (source.Normals != null) mesh.Normals[vertex] = source.Normals[sourceVertex];
            }
            for (int index = 0; index < indicesPerFace; index++)
            {
                int sourceVertex = source.Indices[sourceIndex + index];
                for (int vertex = 0; vertex < vertexCount; vertex++)
                {
                    if (sourceVertices[vertex] != sourceVertex) continue;
                    mesh.Indices[index] = vertex;
                    break;
                }
            }
            mesh.VerticesCount = vertexCount;
            mesh.IndicesCount = indicesPerFace;
            mesh.NormalsCount = source.Normals == null ? 0 : vertexCount;
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
            int logicalSlice = face.Axis == 1 ? slice : slice - HorizontalGridOffset;
            float plane = (logicalSlice + (face.Sign > 0 ? 1 : 0)) * 0.25f;
            x = y = z = 0;
            scaleX = scaleY = scaleZ = 1;
            if (face.Axis == 0)
            {
                x = plane - (face.Sign > 0 ? 1 : 0);
                GetExpandedRange(v, height, 0, GridSize, out y, out scaleY);
                GetExpandedRange(u - HorizontalGridOffset, width, -HorizontalGridOffset, GridSize + 1, out z, out scaleZ);
            }
            else if (face.Axis == 1)
            {
                GetExpandedRange(u - HorizontalGridOffset, width, -HorizontalGridOffset, GridSize + 1, out x, out scaleX);
                y = plane - (face.Sign > 0 ? 1 : 0);
                GetExpandedRange(v - HorizontalGridOffset, height, -HorizontalGridOffset, GridSize + 1, out z, out scaleZ);
            }
            else
            {
                GetExpandedRange(u - HorizontalGridOffset, width, -HorizontalGridOffset, GridSize + 1, out x, out scaleX);
                GetExpandedRange(v, height, 0, GridSize, out y, out scaleY);
                z = plane - (face.Sign > 0 ? 1 : 0);
            }
        }

        private static void GetExpandedRange(int startCell, int cellCount, int minimumCell, int maximumCell, out float start, out float length)
        {
            float lowerOverlap = startCell > minimumCell ? InternalEdgeOverlap : 0;
            float upperOverlap = startCell + cellCount < maximumCell ? InternalEdgeOverlap : 0;
            start = startCell * 0.25f - lowerOverlap;
            length = cellCount * 0.25f + lowerOverlap + upperOverlap;
        }

        private static void GetCoordinates(int axis, int slice, int u, int v, out int x, out int y, out int z)
        {
            if (axis == 0) { x = slice - HorizontalGridOffset; y = v; z = u - HorizontalGridOffset; return; }
            if (axis == 1) { x = u - HorizontalGridOffset; y = slice; z = v - HorizontalGridOffset; return; }
            x = u - HorizontalGridOffset; y = v; z = slice - HorizontalGridOffset;
        }

        private static bool IsInside(int x, int y, int z)
        {
            return x is >= -HorizontalGridOffset and < GridSize + 1
                && y is >= 0 and < GridSize
                && z is >= -HorizontalGridOffset and < GridSize + 1;
        }

        private static void GetFaceDimensions(int axis, out int sliceCount, out int uCount, out int vCount)
        {
            sliceCount = axis == 1 ? GridSize : HorizontalGridSize;
            uCount = HorizontalGridSize;
            vCount = axis == 1 ? HorizontalGridSize : GridSize;
        }

        private static string GetMaterialColor(MasonryUnitPlacement unit)
        {
            int separator = unit.MaterialCode.LastIndexOf('-');
            return separator >= 0 ? unit.MaterialCode[(separator + 1)..] : "cream";
        }

        private static string GetJointKey(MasonryGridPosition firstPosition, MasonryGridPosition secondPosition)
        {
            string first = $"{firstPosition.X},{firstPosition.Y},{firstPosition.Z}";
            string second = $"{secondPosition.X},{secondPosition.Y},{secondPosition.Z}";
            return string.CompareOrdinal(first, second) < 0 ? $"{first}|{second}" : $"{second}|{first}";
        }

    }
}
