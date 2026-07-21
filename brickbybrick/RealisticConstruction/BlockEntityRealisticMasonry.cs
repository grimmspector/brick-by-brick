using AttributeRenderingLibrary;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace brickbybrick.RealisticConstruction
{
    // One entity owns every masonry unit inside a block cell. Units are merged
    // into the terrain mesh, so they do not create individual entities.
    public sealed class BlockEntityRealisticMasonry : BlockEntity
    {
        private const float JointInset = 0.0078125f;
        private int freezeRevision;
        private static long tessellationCalls;
        private static long frozenTessellationCalls;
        private static long tessellatedUnits;
        private static long tessellatedMeshParts;
        private static long tessellationStopwatchTicks;
        private static long slowestTessellationTicks;
        private static long consolidatedMeshBuilds;
        private static long consolidatedMeshReuses;
        private static readonly ConcurrentDictionary<string, byte> RejectedOptimizedMeshKeys = new();
        private static int baselineGenerationZeroCollections;
        private static int baselineGenerationOneCollections;
        private static int baselineGenerationTwoCollections;
        private static int rejectNextOptimizedMesh;
        private static long baselineManagedBytes;
        private static string slowestTessellationCell = "none";
        private static readonly object TessellationProfileSync = new();
        private readonly object frozenMeshSync = new();
        private MeshData? frozenCombinedMesh;
        private Cuboidf[]? cachedGeometryBoxes;
        private MasonryCellState? state = new();
        private byte[]? packedFrozenState;

        // Frozen cells retain their compact payload until geometry or gameplay
        // actually needs the expanded lists, sets, and unit objects.
        public MasonryCellState State
        {
            get
            {
                if (state != null) return state;
                state = MasonryStateCodec.Decode(packedFrozenState ?? Array.Empty<byte>());
                packedFrozenState = null;
                NormalizeUnitOwnership(state);
                return state;
            }
            private set
            {
                state = value;
                packedFrozenState = null;
                cachedGeometryBoxes = null;
            }
        }

        internal void RestorePackedState(byte[] packedState)
        {
            State = MasonryStateCodec.Decode(packedState);
            State.Frozen = false;
            State.FrozenShape = FrozenMasonryShape.Arbitrary;
            Touch();
            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
        }

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);
            if (packedFrozenState == null)
            {
                NormalizeUnitOwnership(State);
            }
            if (api.Side == EnumAppSide.Server)
            {
                if (packedFrozenState != null) return;
                if (State.LastModifiedTotalHours <= 0) State.LastModifiedTotalHours = api.World.Calendar.TotalHours;
                ScheduleFreeze();
            }
        }

        public override void OnBlockRemoved()
        {
            freezeRevision++;
            MasonryFrozenMeshCache.Remove(this);
            if (Api?.Side == EnumAppSide.Server)
            {
                ReleaseAllNeighborReservations();
            }
            base.OnBlockRemoved();
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            byte[] packed = packedFrozenState ?? MasonryStateCodec.Encode(State);
            tree.SetBytes("masonryStatePacked", packed);
            tree.SetBool("wet", !MasonryStateCodec.IsFrozen(packed));
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAttributes(tree, worldForResolving);
            MasonryFrozenMeshCache.Remove(this);
            lock (frozenMeshSync) frozenCombinedMesh = null;
            cachedGeometryBoxes = null;
            byte[] packed = tree.GetBytes("masonryStatePacked");
            if (packed?.Length > 0)
            {
                if (MasonryStateCodec.IsFrozen(packed))
                {
                    packedFrozenState = packed;
                    state = null;
                }
                else
                {
                    State = MasonryStateCodec.Decode(packed);
                }
            }
            else
            {
                // Read the prototype JSON format so existing test worlds
                // migrate automatically when the chunk is saved again.
                string json = tree.GetString("masonryState", string.Empty);
                State = string.IsNullOrEmpty(json) ? new MasonryCellState() : JsonConvert.DeserializeObject<MasonryCellState>(json) ?? new MasonryCellState();
            }

            // State packets may arrive after the chunk's first tessellation.
            // Requeue the cell after clearing its retained frozen mesh so the
            // client never keeps the initial empty construction mesh.
            if (worldForResolving.Side == EnumAppSide.Client)
            {
                worldForResolving.BlockAccessor.MarkBlockDirty(Pos);
            }
        }

        public bool CanPlace(MasonryUnitPlacement unit)
        {
            return GetPlacementFailure(unit) == MasonryPlacementFailure.None;
        }

        public MasonryPlacementFailure GetPlacementFailure(MasonryUnitPlacement unit)
        {
            if (State.Frozen) return MasonryPlacementFailure.Frozen;
            if (MasonryVoxelGeometry.Overlaps(State, unit)) return MasonryPlacementFailure.Occupied;
            if (unit.Origin.Y == 0) return MasonryPlacementFailure.None;

            MasonryGridPosition[] footprint = unit.GetFootprint().ToArray();
            int supportedCells = footprint.Count(position => State.Units.Any(existing =>
                existing.Supports(new MasonryGridPosition(position.X, position.Y - 1, position.Z))));

            // A whole brick may cantilever by one half-brick cell in any
            // direction. Half bricks and rammed earth require full support.
            bool supported = unit.Kind == MasonryUnitKind.WholeBrick
                ? supportedCells >= 1
                : supportedCells == footprint.Length;
            return supported ? MasonryPlacementFailure.None : MasonryPlacementFailure.Unsupported;
        }

        public bool TryPlace(MasonryUnitPlacement unit)
        {
            if (!CanPlace(unit)) return false;

            if (!unit.HasOwner)
            {
                unit.OwnerBlockX = Pos.X;
                unit.OwnerBlockY = Pos.Y;
                unit.OwnerBlockZ = Pos.Z;
            }

            State.Units.Add(unit);
            if (unit.VisualShape == MasonryVisualShape.TriangleWedge)
            {
                HashSet<MasonryGridPosition> occupied = MasonryVoxelGeometry.GetVoxels(unit)
                    .Select(voxel => new MasonryGridPosition(voxel.X, voxel.Y, voxel.Z))
                    .ToHashSet();
                State.EarthGapVoxels.RemoveWhere(occupied.Contains);
                State.MortarGapVoxels.RemoveWhere(occupied.Contains);
            }
            if (unit.Kind is MasonryUnitKind.RammedEarth or MasonryUnitKind.SmallRammedEarth)
            {
                State.EarthGapVoxels.UnionWith(MasonryVoxelGeometry.FindUnusableGaps(State));
            }
            Touch();
            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
            return true;
        }

        public bool TryFillSmallEarthGap(MasonryGridPosition seed)
        {
            if (State.Frozen) return false;

            HashSet<MasonryGridPosition> gap = MasonryVoxelGeometry.FindSmallEnclosedGapAt(State, seed);
            if (gap.Count == 0) return false;

            State.EarthGapVoxels.UnionWith(gap);
            Touch();
            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
            return true;
        }

        public int ApplyMortar(MasonryUnitPlacement unit)
        {
            if (State.Frozen) return 0;
            int changed = State.ApplyMortar(unit.GetFootprint());
            if (changed > 0)
            {
                State.MortarGapVoxels.UnionWith(MasonryVoxelGeometry.FindUnusableGaps(State));
                Touch();
                MarkDirty(true);
                Api.World.BlockAccessor.MarkBlockDirty(Pos);
            }

            return changed;
        }

        public bool IsMortarFree(MasonryGridPosition cell, BlockFacing? face = null)
        {
            MasonryUnitPlacement? unit = FindUnit(cell);
            if (unit?.VisualShape == MasonryVisualShape.TriangleWedge) return true;
            if (face?.IsHorizontal != true) return false;

            int searchX = face.Normali.X == 0 ? 1 : 0;
            int searchZ = face.Normali.Z == 0 ? 1 : 0;
            MasonryUnitPlacement? neighbor = FindAdjacentUnit(cell, searchX, searchZ, unit, out _)
                ?? FindAdjacentUnit(cell, -searchX, -searchZ, unit, out _);
            return neighbor?.VisualShape == MasonryVisualShape.TriangleWedge;
        }

        public bool ApplySideMortar(MasonryGridPosition cell, BlockFacing face)
        {
            if (State.Frozen || !face.IsHorizontal) return false;

            MasonryUnitPlacement? unit = FindUnit(cell);
            if (unit == null || unit.Kind is MasonryUnitKind.RammedEarth or MasonryUnitKind.SmallRammedEarth) return false;

            // Selection faces point toward the ray on exposed brick sides, but
            // narrow joints may resolve to either brick. Try the hit-facing
            // side first, then its opposite so both sides of a seam work.
            int searchX = face.Normali.X == 0 ? 1 : 0;
            int searchZ = face.Normali.Z == 0 ? 1 : 0;
            MasonryUnitPlacement? neighbor = FindAdjacentUnit(cell, searchX, searchZ, unit, out MasonryGridPosition neighborPosition)
                ?? FindAdjacentUnit(cell, -searchX, -searchZ, unit, out neighborPosition);

            if (neighbor == null || neighbor == unit || neighbor.Kind is MasonryUnitKind.RammedEarth or MasonryUnitKind.SmallRammedEarth) return false;

            string jointKey = GetJointKey(cell, neighborPosition);
            if (!State.MortaredSideJoints.Add(jointKey)) return false;

            State.MortarGapVoxels.UnionWith(MasonryVoxelGeometry.FindUnusableGaps(State));
            Touch();
            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
            return true;
        }

        private MasonryUnitPlacement? FindAdjacentUnit(
            MasonryGridPosition cell,
            int stepX,
            int stepZ,
            MasonryUnitPlacement source,
            out MasonryGridPosition position)
        {
            position = cell;
            for (int distance = 1; distance < 4; distance++)
            {
                position = new MasonryGridPosition(cell.X + stepX * distance, cell.Y, cell.Z + stepZ * distance);
                MasonryUnitPlacement? candidate = FindUnit(position);
                if (candidate == null) return null;
                if (candidate != source) return candidate;
            }

            return null;
        }

        public ItemStack? TryRemoveUnmortaredUnit(MasonryGridPosition cell)
        {
            if (State.Frozen) return null;
            MasonryUnitPlacement? unit = FindUnit(cell);
            if (unit == null || unit.MortaredPositions.Count > 0) return null;

            MasonryGridPosition[] footprint = unit.GetFootprint().ToArray();
            if (State.MortaredSideJoints.Any(joint => footprint.Any(position => joint.Contains($"{position.X},{position.Y},{position.Z}")))) return null;

            State.Units.Remove(unit);
            ReleaseNeighborReservations(unit);
            Touch();
            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);

            AssetLocation code = unit.Kind switch
            {
                MasonryUnitKind.HalfBrick => new AssetLocation("brickbybrick", unit.MaterialCode),
                MasonryUnitKind.RammedEarth or MasonryUnitKind.SmallRammedEarth => new AssetLocation("brickbybrick:testrammedearth"),
                _ => new AssetLocation("game", unit.MaterialCode)
            };
            Item? collectible = Api.World.GetItem(code);
            if (collectible == null) return null;

            ItemStack recovered = new(collectible);
            if (State.Units.Count == 0 && State.ReservedUnits.Count == 0)
            {
                Api.World.BlockAccessor.SetBlock(0, Pos);
            }

            return recovered;
        }

        public bool IsUnmortaredBrickOfColor(MasonryGridPosition cell, string color)
        {
            MasonryUnitPlacement? unit = FindUnit(cell);
            if (unit == null || unit.Kind is MasonryUnitKind.RammedEarth or MasonryUnitKind.SmallRammedEarth || unit.MortaredPositions.Count > 0) return false;

            MasonryGridPosition[] footprint = unit.GetFootprint().ToArray();
            if (State.MortaredSideJoints.Any(joint => footprint.Any(position => joint.Contains($"{position.X},{position.Y},{position.Z}")))) return false;

            return string.Equals(GetMaterialColor(unit), color, StringComparison.Ordinal);
        }

        public MasonryUnitPlacement? FindUnit(MasonryGridPosition position)
        {
            return State.Units.LastOrDefault(unit => unit.Occupies(position));
        }

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
        {
            try
            {
            long started = Stopwatch.GetTimestamp();
            int unitCount = State.Units.Count;
            int meshPartCount = unitCount
                + State.Units.Sum(unit => unit.MortaredPositions.Count)
                + State.MortaredSideJoints.Count;
            if (unitCount == 0)
            {
                RecordTessellation(started, 0, 0);
                return true;
            }
            BlockBehaviorShapeTexturesFromAttributes? behavior = Block.GetBehavior<BlockBehaviorShapeTexturesFromAttributes>();
            if (behavior == null)
            {
                RecordTessellation(started, unitCount, meshPartCount);
                return false;
            }

            if (State.Frozen && brickbybrickModSystem.Config.Realism.EnableOptimizedFrozenMeshes)
            {
                string optimizedMeshKey = MasonryMeshKey.Create(State, true);
                MeshData? optimizedMesh;
                lock (frozenMeshSync) optimizedMesh = frozenCombinedMesh;
                if (optimizedMesh != null)
                {
                    mesher.AddMeshData(optimizedMesh);
                    Interlocked.Increment(ref consolidatedMeshReuses);
                    MasonryFrozenMeshCache.Touch(this);
                    RecordTessellation(started, unitCount, 1);
                    return true;
                }

                int exposedQuadCount = 0;
                string rejectionReason = string.Empty;
                bool hasDiagonal = State.Units.Any(unit => unit.IsDiagonal);
                bool built = hasDiagonal
                    ? MasonryAngledMeshBuilder.TryBuildValidated(State, behavior, Pos, out optimizedMesh, out _, out rejectionReason)
                    : State.EarthGapVoxels.Count == 0
                        && State.MortarGapVoxels.Count == 0
                        && MasonryStaticMeshBuilder.TryBuildValidated(State, behavior, Pos, out optimizedMesh, out exposedQuadCount, out rejectionReason);
                if (built && optimizedMesh != null)
                {
                    lock (frozenMeshSync) frozenCombinedMesh ??= optimizedMesh;
                    optimizedMesh = frozenCombinedMesh;
                    Interlocked.Increment(ref consolidatedMeshBuilds);
                    BlockStaticMasonry.RecordMeshBuild(exposedQuadCount, optimizedMesh.IndicesCount / 6);
                    bool retained = MasonryFrozenMeshCache.Store(this, optimizedMeshKey, optimizedMesh);
                    mesher.AddMeshData(optimizedMesh);
                    if (!retained) ReleaseFrozenMesh(optimizedMesh, false);
                    RecordTessellation(started, unitCount, 1);
                    return true;
                }
            }

            if (State.Frozen)
            {
                MeshData? cachedFrozenMesh;
                lock (frozenMeshSync) cachedFrozenMesh = frozenCombinedMesh;
                if (cachedFrozenMesh != null)
                {
                    Interlocked.Increment(ref consolidatedMeshReuses);
                    MasonryFrozenMeshCache.Touch(this);
                    mesher.AddMeshData(cachedFrozenMesh);
                    RecordTessellation(started, unitCount, 1);
                    return true;
                }
            }

            bool consolidateMesh = State.Frozen;
            MeshData? combinedMesh = null;
            void AddMesh(MeshData mesh)
            {
                if (!consolidateMesh)
                {
                    mesher.AddMeshData(mesh);
                    return;
                }
                if (combinedMesh == null) combinedMesh = mesh.Clone();
                else combinedMesh.AddMeshData(mesh);
            }

            foreach (MasonryUnitPlacement unit in State.Units.Concat(State.ReservedUnits))
            {
                CompositeShape shape = new()
                {
                    Base = new AssetLocation(unit.Kind is MasonryUnitKind.RammedEarth or MasonryUnitKind.SmallRammedEarth
                        ? "brickbybrick:shapes/block/realistic/rammedearth.json"
                        : "brickbybrick:shapes/block/realistic/brick.json")
                };

                Variants variants = new();
                variants.Set("color", GetMaterialColor(unit));
                string materialColor = GetMaterialColor(unit);
                {
                    string unitCacheKey = $"unit:{unit.Kind}:{unit.VisualShape}:{materialColor}:{unit.Origin.X}:{unit.Origin.Y}:{unit.Origin.Z}:{unit.OffsetX:0.###}:{unit.OffsetZ:0.###}:{unit.Orientation}";
                    MeshData unitMesh = GetTransformedMesh(unitCacheKey, () =>
                    {
                        MeshData mesh = behavior.GetOrCreateMesh(variants, shape, Pos, $"{unit.Kind}-{materialColor}").Clone();
                        if (unit.VisualShape == MasonryVisualShape.TriangleWedge) MasonryVoxelGeometry.DeformTriangle(mesh);
                        return MasonryVoxelGeometry.TransformUnitMesh(mesh, unit, JointInset);
                    });
                    AddMesh(unitMesh);
                }

                foreach (MasonryGridPosition mortared in unit.MortaredPositions)
                {
                    CompositeShape mortarShape = new()
                    {
                        Base = new AssetLocation("brickbybrick:shapes/block/realistic/mortar.json")
                    };
                    string mortarCacheKey = $"mortar-top:{mortared.X}:{mortared.Y}:{mortared.Z}";
                    MeshData mortarMesh = GetTransformedMesh(mortarCacheKey, () =>
                    {
                        MeshData mesh = behavior.GetOrCreateMesh(new Variants(), mortarShape, Pos, "mortar").Clone();
                        float[] mortarMatrix = Matrixf.Create()
                            .Translate(mortared.X * 0.25f, (mortared.Y + 1) * 0.25f - 0.015625f, mortared.Z * 0.25f)
                            .Scale(0.25f, 0.03125f, 0.25f)
                            .Values;
                        return mesh.MatrixTransform(mortarMatrix);
                    });
                    AddMesh(mortarMesh);
                }
            }

            AddSideJointMortar(AddMesh, behavior);
            AddGapFillMeshes(AddMesh, behavior);

            if (consolidateMesh && combinedMesh != null)
            {
                lock (frozenMeshSync)
                {
                    frozenCombinedMesh ??= combinedMesh;
                    combinedMesh = frozenCombinedMesh;
                }
                Interlocked.Increment(ref consolidatedMeshBuilds);
                string meshKey = MasonryMeshKey.Create(State, false);
                bool retained = MasonryFrozenMeshCache.Store(this, meshKey, combinedMesh);
                mesher.AddMeshData(combinedMesh);
                if (!retained) ReleaseFrozenMesh(combinedMesh, false);
                meshPartCount = 1;
            }

            RecordTessellation(started, unitCount, meshPartCount);

            return true;
            }
            finally
            {
                ReleaseDecodedFrozenState();
            }
        }

        public static void ResetTessellationProfile()
        {
            BlockStaticMasonry.ResetProfile();
            MasonryFrozenMeshCache.ResetProfile();
            Interlocked.Exchange(ref tessellationCalls, 0);
            Interlocked.Exchange(ref frozenTessellationCalls, 0);
            Interlocked.Exchange(ref tessellatedUnits, 0);
            Interlocked.Exchange(ref tessellatedMeshParts, 0);
            Interlocked.Exchange(ref tessellationStopwatchTicks, 0);
            Interlocked.Exchange(ref slowestTessellationTicks, 0);
            MasonryTransformedMeshCache.ResetProfile();
            Interlocked.Exchange(ref consolidatedMeshBuilds, 0);
            Interlocked.Exchange(ref consolidatedMeshReuses, 0);
            baselineManagedBytes = GC.GetTotalMemory(false);
            baselineGenerationZeroCollections = GC.CollectionCount(0);
            baselineGenerationOneCollections = GC.CollectionCount(1);
            baselineGenerationTwoCollections = GC.CollectionCount(2);
            lock (TessellationProfileSync) slowestTessellationCell = "none";
        }

        public static string GetTessellationProfile()
        {
            long calls = Interlocked.Read(ref tessellationCalls);
            long frozenCalls = Interlocked.Read(ref frozenTessellationCalls);
            long units = Interlocked.Read(ref tessellatedUnits);
            long meshParts = Interlocked.Read(ref tessellatedMeshParts);
            double elapsedMilliseconds = Interlocked.Read(ref tessellationStopwatchTicks) * 1000d / Stopwatch.Frequency;
            double slowestMilliseconds = Interlocked.Read(ref slowestTessellationTicks) * 1000d / Stopwatch.Frequency;
            long combinedBuilds = Interlocked.Read(ref consolidatedMeshBuilds);
            long combinedReuses = Interlocked.Read(ref consolidatedMeshReuses);
            long managedBytes = GC.GetTotalMemory(false);
            string slowestCell;
            lock (TessellationProfileSync) slowestCell = slowestTessellationCell;

            return $"Calls: {calls:N0} ({frozenCalls:N0} frozen); units: {units:N0}; mesh parts: {meshParts:N0}; "
                + $"tessellation: {elapsedMilliseconds:N1} ms total, {(calls == 0 ? 0 : elapsedMilliseconds / calls):N3} ms/cell; "
                + $"slowest: {slowestMilliseconds:N2} ms at {slowestCell}; managed memory: {managedBytes / 1048576d:N1} MiB "
                + $"({(managedBytes - baselineManagedBytes) / 1048576d:+0.0;-0.0;0.0} MiB); "
                + $"{MasonryTransformedMeshCache.GetProfile()}; "
                + $"consolidated: {combinedBuilds:N0} builds, {combinedReuses:N0} reuses; "
                + $"{MasonryFrozenMeshCache.GetProfile()}; "
                + $"{BlockStaticMasonry.GetProfile()}; "
                + $"GC: gen0 +{GC.CollectionCount(0) - baselineGenerationZeroCollections}, "
                + $"gen1 +{GC.CollectionCount(1) - baselineGenerationOneCollections}, "
                + $"gen2 +{GC.CollectionCount(2) - baselineGenerationTwoCollections}.";
        }

        public static void ClearTransformedMeshCache()
        {
            MasonryTransformedMeshCache.Clear();
        }

        internal static void ResetOptimizedMeshRuntimeGuard()
        {
            RejectedOptimizedMeshKeys.Clear();
            Interlocked.Exchange(ref rejectNextOptimizedMesh, 0);
        }

        internal static void RejectNextOptimizedMeshForProfiling()
        {
            Interlocked.Exchange(ref rejectNextOptimizedMesh, 1);
        }

        private bool TryAddOptimizedMesh(ITerrainMeshPool mesher, MeshData mesh, string meshKey)
        {
            if (Interlocked.Exchange(ref rejectNextOptimizedMesh, 0) != 0)
            {
                if (RejectedOptimizedMeshKeys.TryAdd(meshKey, 0))
                {
                    BlockStaticMasonry.RecordRejectedBuild();
                    Api.Logger.Warning($"Profiling deliberately rejected optimized masonry arrangement {meshKey} at {Pos}. This arrangement will use component fallback.");
                }

                MasonryFrozenMeshCache.Remove(this);
                ReleaseFrozenMesh(mesh, false);
                return false;
            }

            try
            {
                mesher.AddMeshData(mesh);
                return true;
            }
            catch (IndexOutOfRangeException exception)
            {
                if (RejectedOptimizedMeshKeys.TryAdd(meshKey, 0))
                {
                    BlockStaticMasonry.RecordRejectedBuild();
                    Api.Logger.Warning($"Vintage Story rejected optimized masonry arrangement {meshKey} at {Pos}: {exception.Message}. This arrangement will use component fallback.");
                }

                MasonryFrozenMeshCache.Remove(this);
                ReleaseFrozenMesh(mesh, false);
                return false;
            }
        }

        internal void ReleaseFrozenMesh(MeshData mesh, bool evicted = true)
        {
            lock (frozenMeshSync)
            {
                if (ReferenceEquals(frozenCombinedMesh, mesh)) frozenCombinedMesh = null;
            }
        }

        internal void AdoptFrozenMesh(MeshData mesh)
        {
            lock (frozenMeshSync) frozenCombinedMesh = mesh;
        }

        private static MeshData GetTransformedMesh(string key, Func<MeshData> createMesh)
        {
            return MasonryTransformedMeshCache.GetOrCreate(key, createMesh);
        }

        private void RecordTessellation(long started, int unitCount, int meshPartCount)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Increment(ref tessellationCalls);
            if (State.Frozen) Interlocked.Increment(ref frozenTessellationCalls);
            Interlocked.Add(ref tessellatedUnits, unitCount);
            Interlocked.Add(ref tessellatedMeshParts, meshPartCount);
            Interlocked.Add(ref tessellationStopwatchTicks, elapsed);

            long previousSlowest = Interlocked.Read(ref slowestTessellationTicks);
            while (elapsed > previousSlowest)
            {
                long observed = Interlocked.CompareExchange(ref slowestTessellationTicks, elapsed, previousSlowest);
                if (observed == previousSlowest)
                {
                    lock (TessellationProfileSync) slowestTessellationCell = $"{Pos.X}, {Pos.Y}, {Pos.Z}";
                    break;
                }

                previousSlowest = observed;
            }
        }

        // Side joints fill explicit mortar and back dry seams with recessed
        // brick, keeping unmortared units visually separated.
        private void AddSideJointMortar(Action<MeshData> addMesh, BlockBehaviorShapeTexturesFromAttributes behavior)
        {
            if (State.Units.Any(unit => unit.IsDiagonal)) return;

            HashSet<string> renderedJoints = new();
            (int X, int Z)[] directions = { (1, 0), (0, 1) };
            CompositeShape mortarShape = new()
            {
                Base = new AssetLocation("brickbybrick:shapes/block/realistic/mortar.json")
            };
            CompositeShape brickShape = new()
            {
                Base = new AssetLocation("brickbybrick:shapes/block/realistic/brick.json")
            };

            foreach (MasonryUnitPlacement unit in State.Units.Where(candidate => candidate.Kind is not MasonryUnitKind.RammedEarth and not MasonryUnitKind.SmallRammedEarth))
            {
                foreach (MasonryGridPosition cell in unit.GetFootprint())
                {
                    foreach ((int offsetX, int offsetZ) in directions)
                    {
                        MasonryGridPosition neighborPosition = new(cell.X + offsetX, cell.Y, cell.Z + offsetZ);
                        MasonryUnitPlacement? neighbor = State.Units.FirstOrDefault(candidate =>
                            candidate != unit
                            && candidate.Kind is not MasonryUnitKind.RammedEarth and not MasonryUnitKind.SmallRammedEarth
                            && candidate.Occupies(neighborPosition));
                        if (neighbor == null) continue;

                        string jointKey = GetJointKey(cell, neighborPosition);
                        if (!renderedJoints.Add(jointKey)) continue;

                        bool alongX = offsetX != 0;
                        float x = alongX
                            ? (offsetX > 0 ? (cell.X + 1) * 0.25f - JointInset : cell.X * 0.25f - JointInset)
                            : cell.X * 0.25f + JointInset;
                        float z = alongX
                            ? cell.Z * 0.25f + JointInset
                            : (offsetZ > 0 ? (cell.Z + 1) * 0.25f - JointInset : cell.Z * 0.25f - JointInset);
                        bool mortared = State.MortaredSideJoints.Contains(jointKey);
                        string materialColor = mortared ? string.Empty : GetMaterialColor(unit);
                        string sideCacheKey = $"{(mortared ? "mortar" : "shadow")}-side:{alongX}:{x}:{cell.Y}:{z}:{materialColor}";
                        MeshData jointMesh = GetTransformedMesh(sideCacheKey, () =>
                        {
                            Variants variants = new();
                            if (!mortared) variants.Set("color", materialColor);
                            MeshData mesh = behavior.GetOrCreateMesh(
                                variants,
                                mortared ? mortarShape : brickShape,
                                Pos,
                                mortared ? "mortar-side" : $"shadow-side-{materialColor}").Clone();
                            float[] jointMatrix = Matrixf.Create()
                                .Translate(x, cell.Y * 0.25f + JointInset, z)
                                .Scale(
                                    alongX ? JointInset * 2 : 0.25f - JointInset * 2,
                                    0.25f - JointInset * 2,
                                    alongX ? 0.25f - JointInset * 2 : JointInset * 2)
                                .Values;
                            return mesh.MatrixTransform(jointMatrix);
                        });
                        addMesh(jointMesh);
                    }
                }
            }
        }

        public bool CanReserve(IEnumerable<MasonryGridPosition> positions)
        {
            if (State.Frozen) return false;
            return positions.All(position =>
                !State.ReservedPositions.Contains(position)
                && !State.Units.Any(existing => existing.Occupies(position))
                && !State.ReservedUnits.Any(existing => existing.Occupies(position)));
        }

        public bool CanReserve(MasonryUnitPlacement unit)
        {
            if (State.Frozen) return false;
            return !MasonryVoxelGeometry.Overlaps(State, unit);
        }

        // Gap fills are stored as compact microvoxels but rendered as merged
        // cuboids, keeping triangle-like diagonal pockets inexpensive.
        private void AddGapFillMeshes(Action<MeshData> addMesh, BlockBehaviorShapeTexturesFromAttributes behavior)
        {
            AddMaterial(State.EarthGapVoxels, true);
            AddMaterial(State.MortarGapVoxels, false);

            void AddMaterial(IEnumerable<MasonryGridPosition> voxels, bool earth)
            {
                Cuboidf[] boxes = MasonryVoxelGeometry.BuildMergedBoxes(voxels);
                if (boxes.Length == 0) return;
                CompositeShape shape = new()
                {
                    Base = new AssetLocation(earth
                        ? "brickbybrick:shapes/block/realistic/rammedearth.json"
                        : "brickbybrick:shapes/block/realistic/mortar.json")
                };
                foreach (Cuboidf box in boxes)
                {
                    string key = $"gap:{earth}:{box.X1}:{box.Y1}:{box.Z1}:{box.X2}:{box.Y2}:{box.Z2}";
                    MeshData mesh = GetTransformedMesh(key, () =>
                    {
                        MeshData source = behavior.GetOrCreateMesh(new Variants(), shape, Pos, earth ? "gap-earth" : "gap-mortar").Clone();
                        return source.MatrixTransform(Matrixf.Create()
                            .Translate(box.X1, box.Y1, box.Z1)
                            .Scale(box.XSize, box.YSize, box.ZSize)
                            .Values);
                    });
                    addMesh(mesh);
                }
            }
        }

        internal Cuboidf[] GetGeometryBoxes()
        {
            return cachedGeometryBoxes ??= MasonryVoxelGeometry.BuildMergedBoxes(State);
        }

        // Room sealing requires mortar in every eligible vertical joint. Top
        // bed mortar is intentionally ignored because it does not seal sides.
        internal bool HasCompleteSideMortarCoverage()
        {
            bool foundEligibleJoint = false;
            (int X, int Z)[] directions = { (1, 0), (0, 1) };

            foreach (MasonryUnitPlacement unit in State.Units.Where(candidate => candidate.Kind is not MasonryUnitKind.RammedEarth and not MasonryUnitKind.SmallRammedEarth))
            {
                foreach (MasonryGridPosition cell in unit.GetFootprint())
                {
                    foreach ((int offsetX, int offsetZ) in directions)
                    {
                        MasonryGridPosition neighborPosition = new(cell.X + offsetX, cell.Y, cell.Z + offsetZ);
                        MasonryUnitPlacement? neighbor = State.Units.FirstOrDefault(candidate =>
                            candidate != unit
                            && candidate.Kind is not MasonryUnitKind.RammedEarth and not MasonryUnitKind.SmallRammedEarth
                            && candidate.Occupies(neighborPosition));
                        if (neighbor == null) continue;

                        foundEligibleJoint = true;
                        if (!State.MortaredSideJoints.Contains(GetJointKey(cell, neighborPosition))) return false;
                    }
                }
            }

            return foundEligibleJoint;
        }

        public void Reserve(IEnumerable<MasonryGridPosition> positions)
        {
            foreach (MasonryGridPosition position in positions)
            {
                State.ReservedPositions.Add(position);
            }

            Touch();
            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
        }

        public void Reserve(MasonryUnitPlacement unit)
        {
            State.ReservedUnits.RemoveAll(existing => existing.Id == unit.Id);
            State.ReservedUnits.Add(unit);
            Touch();
            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
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

        private void ReleaseNeighborReservations(MasonryUnitPlacement owner)
        {
            Dictionary<(int X, int Z), List<MasonryGridPosition>> reservations = BuildNeighborReservations(owner);
            foreach ((int X, int Z) offset in GetNeighborOffsets(owner, reservations))
            {
                BlockPos neighborPos = Pos.AddCopy(offset.X, 0, offset.Z);
                if (Api.World.BlockAccessor.GetBlockEntity(neighborPos) is not BlockEntityRealisticMasonry neighbor) continue;

                neighbor.State.ReservedUnits.RemoveAll(unit =>
                    unit.Id == owner.Id
                    && unit.OwnerBlockX == owner.OwnerBlockX
                    && unit.OwnerBlockY == owner.OwnerBlockY
                    && unit.OwnerBlockZ == owner.OwnerBlockZ);

                neighbor.Touch();
                if (neighbor.State.Units.Count == 0 && neighbor.State.ReservedUnits.Count == 0)
                {
                    Api.World.BlockAccessor.SetBlock(0, neighborPos);
                }
                else
                {
                    neighbor.MarkDirty(true);
                    Api.World.BlockAccessor.MarkBlockDirty(neighborPos);
                }
            }
        }

        private static Dictionary<(int X, int Z), List<MasonryGridPosition>> BuildNeighborReservations(MasonryUnitPlacement unit)
        {
            Dictionary<(int X, int Z), List<MasonryGridPosition>> reservations = new();
            foreach (MasonryGridPosition position in MasonryVoxelGeometry.GetReservationFootprint(unit))
            {
                int offsetX = (int)Math.Floor(position.X / 4d);
                int offsetZ = (int)Math.Floor(position.Z / 4d);
                if (offsetX == 0 && offsetZ == 0) continue;

                (int X, int Z) key = (offsetX, offsetZ);
                if (!reservations.TryGetValue(key, out List<MasonryGridPosition>? positions))
                {
                    positions = new List<MasonryGridPosition>();
                    reservations[key] = positions;
                }

                positions.Add(new MasonryGridPosition(
                    GameMath.Mod(position.X, 4),
                    position.Y,
                    GameMath.Mod(position.Z, 4)));
            }

            return reservations;
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

        private void ReleaseAllNeighborReservations()
        {
            foreach (MasonryUnitPlacement unit in State.Units.ToArray())
            {
                ReleaseNeighborReservations(unit);
            }
        }

        private void NormalizeUnitOwnership(MasonryCellState cell)
        {
            foreach (MasonryUnitPlacement unit in cell.Units)
            {
                if (unit.HasOwner) continue;
                unit.OwnerBlockX = Pos.X;
                unit.OwnerBlockY = Pos.Y;
                unit.OwnerBlockZ = Pos.Z;
            }

            // Older saves cannot identify a mirror's source after reload.
            // Drop those stale caches; new mirrors include durable ownership.
            cell.ReservedPositions.Clear();
            cell.ReservedUnits.RemoveAll(unit => !unit.HasOwner);
        }

        public bool Reopen()
        {
            if (!State.Frozen) return false;

            State.Frozen = false;
            State.FrozenShape = FrozenMasonryShape.Arbitrary;
            Touch();
            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
            return true;
        }

        public void PopulateForProfiling(Random random, bool frozen)
        {
            string[] colors = { "black", "brown", "cream", "gray", "orange", "red", "tan", "clinker" };
            State = new MasonryCellState();
            bool fillRecognizedBlock = frozen && random.NextDouble() < 0.5;
            for (int y = 0; y < 4; y++)
            for (int z = 0; z < 4; z++)
            {
                bool staggered = y % 2 != 0;
                foreach (int x in staggered ? new[] { 1 } : new[] { 0, 2 })
                {
                    if (!fillRecognizedBlock && random.NextDouble() > 0.42) continue;
                    AddProfileBrick($"p{x}{y}{z}", MasonryUnitKind.WholeBrick, x, y, z, colors[random.Next(colors.Length)], random.NextDouble() < 0.75);
                }

                if (!staggered) continue;
                foreach (int x in new[] { 0, 3 })
                {
                    if (!fillRecognizedBlock && random.NextDouble() > 0.42) continue;
                    AddProfileBrick($"p{x}{y}{z}", MasonryUnitKind.HalfBrick, x, y, z, colors[random.Next(colors.Length)], random.NextDouble() < 0.75);
                }
            }

            State.LastModifiedTotalHours = Api.World.Calendar.TotalHours;
            State.FrozenShape = InferFrozenShape();
            State.Frozen = false;
            freezeRevision++;
            if (frozen)
            {
                Freeze();
                return;
            }

            ScheduleFreeze();
            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
        }

        internal void PopulateDuplicatePrototypeForProfiling(Random random, int prototypeIndex)
        {
            string[] colors = { "black", "brown", "cream", "gray", "orange", "red", "tan", "clinker" };
            State = new MasonryCellState();
            int shape = prototypeIndex % 10;
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            for (int z = 0; z < 4; z++)
            {
                bool occupied = shape switch
                {
                    0 => true,
                    1 => y < 2,
                    2 => y >= 2,
                    3 => z < 2 || y < 2,
                    4 => x >= 2 || y < 2,
                    5 => z >= 2 || y >= 2,
                    _ => random.NextDouble() < 0.35 + (shape - 6) * 0.08
                };
                if (!occupied) continue;

                MasonryUnitPlacement unit = new()
                {
                    Id = $"d{x}{y}{z}",
                    Kind = MasonryUnitKind.HalfBrick,
                    MaterialCode = $"burnedbrick-{colors[random.Next(colors.Length)]}",
                    Orientation = MasonryOrientation.East,
                    Origin = new MasonryGridPosition(x, y, z)
                };
                if (random.NextDouble() < 0.75) unit.MortaredPositions.Add(unit.Origin);
                State.Units.Add(unit);
            }

            State.LastModifiedTotalHours = Api.World.Calendar.TotalHours;
            State.FrozenShape = InferFrozenShape();
            State.Frozen = true;
            freezeRevision++;
            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
        }

        internal void PopulateGeometryCorpusForProfiling(int caseIndex, bool exhaustive)
        {
            State = new MasonryCellState();
            if (!exhaustive && caseIndex < 10)
            {
                PopulateDuplicatePrototypeForProfiling(new Random(7919 + caseIndex), caseIndex);
                return;
            }

            ulong occupancy;
            if (exhaustive)
            {
                occupancy = (ulong)(caseIndex + 1);
            }
            else
            {
                Random random = new(104729 + caseIndex * 7919);
                occupancy = ((ulong)(uint)random.Next() << 32) | (uint)random.Next();
                occupancy |= 1UL << (caseIndex % 64);
            }

            for (int bit = 0; bit < 64; bit++)
            {
                if ((occupancy & (1UL << bit)) == 0) continue;
                int x = bit & 3;
                int z = bit >> 2 & 3;
                int y = exhaustive ? 0 : bit >> 4;
                MasonryGridPosition position = new(x, y, z);
                MasonryUnitPlacement unit = new()
                {
                    Id = $"c{caseIndex}-{bit}",
                    Kind = MasonryUnitKind.HalfBrick,
                    MaterialCode = "burnedbrick-cream",
                    Orientation = MasonryOrientation.East,
                    Origin = position
                };
                if ((bit + caseIndex) % 3 != 0) unit.MortaredPositions.Add(position);
                State.Units.Add(unit);
            }

            State.LastModifiedTotalHours = Api.World.Calendar.TotalHours;
            State.FrozenShape = InferFrozenShape();
            State.Frozen = true;
            freezeRevision++;
            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
        }

        internal void PopulateRealisticBaseCellForProfiling(Random random, int caseIndex)
        {
            State = new MasonryCellState();
            bool rammedEarth = caseIndex % 20 == 0;
            int shape = random.Next(100);
            bool sideMortarOnly = !rammedEarth && shape is >= 87 and < 95;
            bool partialBuild = !rammedEarth && shape >= 95;
            int maximumY = shape < 8 ? 1 : 3;
            string[] colors = { "brown", "cream", "gray", "orange", "red", "tan" };
            string color = colors[(caseIndex / 24) % colors.Length];

            if (rammedEarth)
            {
                for (int y = 0; y <= maximumY; y++)
                for (int x = 0; x < 4; x += 2)
                for (int z = 0; z < 4; z += 2)
                {
                    State.Units.Add(new MasonryUnitPlacement
                    {
                        Id = $"r{caseIndex}-{x}-{y}-{z}",
                        Kind = MasonryUnitKind.RammedEarth,
                        MaterialCode = "rammedearth-clay",
                        Orientation = MasonryOrientation.South,
                        Origin = new MasonryGridPosition(x, y, z)
                    });
                }
            }
            else
            {
                for (int y = 0; y <= maximumY; y++)
                for (int z = 0; z < 4; z++)
                {
                    if (shape is >= 8 and < 14 && y >= 2 && z >= 2) continue;
                    bool staggered = y % 2 != 0;
                    foreach (int x in staggered ? new[] { 1 } : new[] { 0, 2 })
                    {
                        if (partialBuild && random.NextDouble() < 0.55) continue;
                        AddProfileBrick($"b{caseIndex}-{x}-{y}-{z}", MasonryUnitKind.WholeBrick, x, y, z, color, !sideMortarOnly, random);
                    }

                    if (!staggered) continue;
                    foreach (int x in new[] { 0, 3 })
                    {
                        if (partialBuild && random.NextDouble() < 0.55) continue;
                        AddProfileBrick($"b{caseIndex}-{x}-{y}-{z}", MasonryUnitKind.HalfBrick, x, y, z, color, !sideMortarOnly, random);
                    }
                }

                if (sideMortarOnly)
                {
                    foreach (MasonryUnitPlacement unit in State.Units)
                    foreach (MasonryGridPosition position in unit.GetFootprint())
                    {
                        MasonryGridPosition east = new(position.X + 1, position.Y, position.Z);
                        MasonryGridPosition south = new(position.X, position.Y, position.Z + 1);
                        if (State.Units.Any(candidate => candidate != unit && candidate.Occupies(east))
                            && random.NextDouble() < 0.4)
                        {
                            State.MortaredSideJoints.Add(GetJointKey(position, east));
                        }
                        if (State.Units.Any(candidate => candidate != unit && candidate.Occupies(south))
                            && random.NextDouble() < 0.4)
                        {
                            State.MortaredSideJoints.Add(GetJointKey(position, south));
                        }
                    }
                }
            }

            State.LastModifiedTotalHours = Api.World.Calendar.TotalHours;
            State.FrozenShape = InferFrozenShape();
            State.Frozen = !partialBuild;
            freezeRevision++;
            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
        }

        // Fills a profiling cell with a running bond or a repeated diagonal
        // course, matching the long exterior walls used by the wall corpus.
        internal void PopulateWallCellForProfiling(int sectionIndex, bool diagonal)
        {
            State = new MasonryCellState();
            string[] colors = { "cream", "gray", "tan" };
            string color = colors[(sectionIndex / 8) % colors.Length];
            MasonryOrientation orientation = sectionIndex % 2 == 0 ? MasonryOrientation.SouthEast : MasonryOrientation.NorthEast;

            for (int y = 0; y < 4; y++)
            {
                if (diagonal)
                {
                    foreach ((int x, int z) in new[] { (0, 0), (2, 2) })
                    {
                        MasonryUnitPlacement unit = new()
                        {
                            Id = $"wall-diagonal-{sectionIndex}-{x}-{y}-{z}",
                            Kind = MasonryUnitKind.WholeBrick,
                            MaterialCode = $"burnedbrick-{color}",
                            Orientation = orientation,
                            Origin = new MasonryGridPosition(x, y, z)
                        };
                        if (MasonryVoxelGeometry.Overlaps(State, unit)) continue;
                        foreach (MasonryGridPosition position in unit.GetFootprint()) unit.MortaredPositions.Add(position);
                        State.Units.Add(unit);
                    }
                    continue;
                }

                for (int z = 0; z < 4; z++)
                {
                    bool staggered = y % 2 != 0;
                    foreach (int x in staggered ? new[] { 1 } : new[] { 0, 2 })
                        AddProfileBrick($"wall-{sectionIndex}-{x}-{y}-{z}", MasonryUnitKind.WholeBrick, x, y, z, color, true);
                    if (staggered)
                    foreach (int x in new[] { 0, 3 })
                        AddProfileBrick($"wall-{sectionIndex}-{x}-{y}-{z}", MasonryUnitKind.HalfBrick, x, y, z, color, true);
                }
            }

            State.LastModifiedTotalHours = Api.World.Calendar.TotalHours;
            State.FrozenShape = InferFrozenShape();
            State.Frozen = true;
            freezeRevision++;
            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
        }

        private void AddProfileBrick(string id, MasonryUnitKind kind, int x, int y, int z, string color, bool mortarAll, Random? random = null)
        {
            MasonryUnitPlacement unit = new()
            {
                Id = id,
                Kind = kind,
                MaterialCode = $"burnedbrick-{color}",
                Orientation = MasonryOrientation.East,
                Origin = new MasonryGridPosition(x, y, z)
            };
            foreach (MasonryGridPosition position in unit.GetFootprint())
            {
                if (mortarAll && (random == null || random.NextDouble() < 0.68)) unit.MortaredPositions.Add(position);
            }
            State.Units.Add(unit);
        }

        internal void PopulateAngledCorpusForProfiling(int caseIndex, bool mixed)
        {
            State = new MasonryCellState();
            Random random = new(104729 + caseIndex * 7919 + (mixed ? 1 : 0));
            MasonryOrientation[] diagonal =
            {
                MasonryOrientation.SouthEast,
                MasonryOrientation.SouthWest,
                MasonryOrientation.NorthWest,
                MasonryOrientation.NorthEast
            };
            MasonryOrientation[] straight =
            {
                MasonryOrientation.East,
                MasonryOrientation.South,
                MasonryOrientation.West,
                MasonryOrientation.North
            };
            string[] colors = { "brown", "cream", "gray", "orange", "red", "tan" };
            int layers = 1 + caseIndex % 4;
            int attempts = 28 + caseIndex % 20;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                bool useStraight = mixed && attempt % 3 == 0;
                MasonryUnitKind kind = attempt % 11 == 0
                    ? MasonryUnitKind.RammedEarth
                    : attempt % 5 == 0 ? MasonryUnitKind.HalfBrick : MasonryUnitKind.WholeBrick;
                MasonryUnitPlacement unit = new()
                {
                    Id = $"angled-{caseIndex}-{attempt}",
                    Kind = kind,
                    MaterialCode = kind == MasonryUnitKind.RammedEarth ? "rammedearth-clay" : $"burnedbrick-{colors[(caseIndex + attempt) % colors.Length]}",
                    Orientation = useStraight ? straight[(caseIndex + attempt) % straight.Length] : diagonal[(caseIndex + attempt) % diagonal.Length],
                    Origin = new MasonryGridPosition(random.Next(4), random.Next(layers), random.Next(4))
                };
                if (kind == MasonryUnitKind.HalfBrick && !useStraight) unit.VisualShape = MasonryVisualShape.TriangleWedge;
                if (MasonryVoxelGeometry.Overlaps(State, unit)) continue;
                State.Units.Add(unit);
                if (kind != MasonryUnitKind.RammedEarth && attempt % 2 == 0)
                    foreach (MasonryGridPosition position in unit.GetFootprint()) unit.MortaredPositions.Add(position);
            }

            if (mixed)
            {
                State.MortarGapVoxels.UnionWith(MasonryVoxelGeometry.FindUnusableGaps(State));
            }
            else
            {
                State.EarthGapVoxels.UnionWith(MasonryVoxelGeometry.FindUnusableGaps(State));
            }
            State.LastModifiedTotalHours = Api.World.Calendar.TotalHours;
            State.FrozenShape = FrozenMasonryShape.Arbitrary;
            State.Frozen = true;
            freezeRevision++;
            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
        }

        internal void ForceFreezeForProfiling()
        {
            if (Api?.Side == EnumAppSide.Server && !State.Frozen) Freeze();
        }

        internal void TryApplyScheduledFreeze(int revision)
        {
            if (Api?.Side != EnumAppSide.Server || revision != freezeRevision || State.Frozen) return;

            if (brickbybrickModSystem.Config.Curing.EnableMortarCuring && !HasReachedCuringAge())
            {
                ScheduleFreeze();
                return;
            }

            Freeze();
        }

        private bool HasReachedCuringAge()
        {
            double requiredHours = brickbybrickModSystem.Config.Curing.MortarCuringHours
                / brickbybrickModSystem.Config.Curing.CuringSpeedMultiplier;
            return Api.World.Calendar.TotalHours - State.LastModifiedTotalHours >= requiredHours;
        }

        private void Freeze()
        {
            if (State.Units.Count == 0 && State.ReservedUnits.Count == 0)
            {
                Api.World.BlockAccessor.SetBlock(0, Pos);
                return;
            }

            State.FrozenShape = InferFrozenShape();
            State.Frozen = true;
            packedFrozenState = MasonryStateCodec.Encode(State);
            cachedGeometryBoxes = null;
            MarkDirty(true);
            state = null;
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
        }

        internal byte[] GetPackedStateForProfiling()
        {
            return packedFrozenState ?? MasonryStateCodec.Encode(State);
        }

        // Drops one retained frozen mesh so the next client tessellation uses
        // the currently selected profiling renderer mode.
        internal void InvalidateFrozenMeshForProfiling()
        {
            MasonryFrozenMeshCache.Remove(this);
            lock (frozenMeshSync) frozenCombinedMesh = null;
        }

        private void ReleaseDecodedFrozenState()
        {
            if (state?.Frozen != true) return;
            packedFrozenState = MasonryStateCodec.Encode(state);
            cachedGeometryBoxes = null;
            state = null;
        }

        private void Touch()
        {
            cachedGeometryBoxes = null;
            State.LastModifiedTotalHours = Api?.World?.Calendar?.TotalHours ?? State.LastModifiedTotalHours;
            State.Frozen = false;
            State.FrozenShape = FrozenMasonryShape.Arbitrary;
            freezeRevision++;

            // Every modification restarts the deadline. Completed geometry
            // also waits so players can adjust a newly finished arrangement.
            ScheduleFreeze();
        }

        private void ScheduleFreeze()
        {
            if (Api?.Side != EnumAppSide.Server || State.Frozen) return;

            TimeSpan delay;
            if (!brickbybrickModSystem.Config.Curing.EnableMortarCuring)
            {
                delay = TimeSpan.FromSeconds(brickbybrickModSystem.Config.Curing.InactiveFreezeSeconds);
            }
            else
            {
                double requiredHours = brickbybrickModSystem.Config.Curing.MortarCuringHours
                    / brickbybrickModSystem.Config.Curing.CuringSpeedMultiplier;
                double remainingHours = Math.Max(0, requiredHours - (Api.World.Calendar.TotalHours - State.LastModifiedTotalHours));
                double calendarRate = Api.World.Calendar.SpeedOfTime * Api.World.Calendar.CalendarSpeedMul;
                delay = calendarRate <= 0
                    ? TimeSpan.FromSeconds(brickbybrickModSystem.Config.Curing.InactiveFreezeSeconds)
                    : TimeSpan.FromSeconds(remainingHours * 3600d / calendarRate);
            }

            MasonryFreezeScheduler.Schedule(this, freezeRevision, delay);
        }

        private FrozenMasonryShape InferFrozenShape()
        {
            if (State.Units.Any(unit => unit.IsDiagonal)
                || State.EarthGapVoxels.Count > 0
                || State.MortarGapVoxels.Count > 0) return FrozenMasonryShape.Arbitrary;

            bool[,,] occupied = new bool[4, 4, 4];
            foreach (MasonryUnitPlacement unit in State.Units)
            {
                foreach (MasonryGridPosition position in unit.GetFootprint())
                {
                    if (position.X is >= 0 and < 4 && position.Y is >= 0 and < 4 && position.Z is >= 0 and < 4)
                    {
                        occupied[position.X, position.Y, position.Z] = true;
                    }
                }
            }

            if (AllCells(occupied, (_, _, _) => true)) return FrozenMasonryShape.Block;
            if (MatchesHalf(occupied, 0, 2)) return FrozenMasonryShape.SlabDown;
            if (MatchesHalf(occupied, 2, 4)) return FrozenMasonryShape.SlabUp;
            if (MatchesAxisHalf(occupied, true, 0, 2)) return FrozenMasonryShape.SlabWest;
            if (MatchesAxisHalf(occupied, true, 2, 4)) return FrozenMasonryShape.SlabEast;
            if (MatchesAxisHalf(occupied, false, 0, 2)) return FrozenMasonryShape.SlabNorth;
            if (MatchesAxisHalf(occupied, false, 2, 4)) return FrozenMasonryShape.SlabSouth;
            if (MatchesStair(occupied, false, 0, 2)) return FrozenMasonryShape.StairNorth;
            if (MatchesStair(occupied, true, 2, 4)) return FrozenMasonryShape.StairEast;
            if (MatchesStair(occupied, false, 2, 4)) return FrozenMasonryShape.StairSouth;
            if (MatchesStair(occupied, true, 0, 2)) return FrozenMasonryShape.StairWest;
            if (MatchesUpsideDownStair(occupied, false, 0, 2)) return FrozenMasonryShape.StairDownNorth;
            if (MatchesUpsideDownStair(occupied, true, 2, 4)) return FrozenMasonryShape.StairDownEast;
            if (MatchesUpsideDownStair(occupied, false, 2, 4)) return FrozenMasonryShape.StairDownSouth;
            if (MatchesUpsideDownStair(occupied, true, 0, 2)) return FrozenMasonryShape.StairDownWest;

            return HasCompleteOuterShell(occupied) ? FrozenMasonryShape.Block : FrozenMasonryShape.Arbitrary;
        }

        private static bool AllCells(bool[,,] occupied, System.Func<int, int, int, bool> expected)
        {
            for (int x = 0; x < 4; x++)
            for (int y = 0; y < 4; y++)
            for (int z = 0; z < 4; z++)
                if (occupied[x, y, z] != expected(x, y, z)) return false;
            return true;
        }

        private static bool MatchesHalf(bool[,,] occupied, int minimumY, int maximumY)
        {
            return AllCells(occupied, (_, y, _) => y >= minimumY && y < maximumY);
        }

        private static bool MatchesAxisHalf(bool[,,] occupied, bool useX, int minimum, int maximum)
        {
            return AllCells(occupied, (x, _, z) => (useX ? x : z) >= minimum && (useX ? x : z) < maximum);
        }

        private static bool MatchesStair(bool[,,] occupied, bool useX, int upperMinimum, int upperMaximum)
        {
            return AllCells(occupied, (x, y, z) => y < 2 || (y < 4 && (useX ? x : z) >= upperMinimum && (useX ? x : z) < upperMaximum));
        }

        private static bool MatchesUpsideDownStair(bool[,,] occupied, bool useX, int lowerMinimum, int lowerMaximum)
        {
            return AllCells(occupied, (x, y, z) => y >= 2 || (y >= 0 && (useX ? x : z) >= lowerMinimum && (useX ? x : z) < lowerMaximum));
        }

        private static bool HasCompleteOuterShell(bool[,,] occupied)
        {
            return AllCells(occupied, (x, y, z) => x == 0 || x == 3 || y == 0 || y == 3 || z == 0 || z == 3 ? true : occupied[x, y, z]);
        }
    }
}
