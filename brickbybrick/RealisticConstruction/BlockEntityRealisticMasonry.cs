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
        private static long transformedMeshCacheHits;
        private static long transformedMeshCacheMisses;
        private static long consolidatedMeshBuilds;
        private static long consolidatedMeshReuses;
        private static int baselineGenerationZeroCollections;
        private static int baselineGenerationOneCollections;
        private static int baselineGenerationTwoCollections;
        private static long baselineManagedBytes;
        private static string slowestTessellationCell = "none";
        private static readonly object TessellationProfileSync = new();
        private static readonly ConcurrentDictionary<string, MeshData> TransformedMeshCache = new();
        private readonly object frozenMeshSync = new();
        private MeshData? frozenCombinedMesh;

        public MasonryCellState State { get; private set; } = new();

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
            if (api.Side == EnumAppSide.Server)
            {
                if (State.LastModifiedTotalHours <= 0) State.LastModifiedTotalHours = api.World.Calendar.TotalHours;
                ScheduleFreeze();
            }
        }

        public override void OnBlockRemoved()
        {
            freezeRevision++;
            MasonryFrozenMeshCache.Remove(this);
            base.OnBlockRemoved();
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetBytes("masonryStatePacked", MasonryStateCodec.Encode(State));
            tree.SetBool("wet", !State.Frozen);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAttributes(tree, worldForResolving);
            MasonryFrozenMeshCache.Remove(this);
            lock (frozenMeshSync) frozenCombinedMesh = null;
            byte[] packed = tree.GetBytes("masonryStatePacked");
            if (packed?.Length > 0)
            {
                State = MasonryStateCodec.Decode(packed);
                return;
            }

            // Read the prototype JSON format so existing test worlds migrate
            // automatically when the chunk is saved again.
            string json = tree.GetString("masonryState", string.Empty);
            State = string.IsNullOrEmpty(json) ? new MasonryCellState() : JsonConvert.DeserializeObject<MasonryCellState>(json) ?? new MasonryCellState();
        }

        public bool CanPlace(MasonryUnitPlacement unit)
        {
            return GetPlacementFailure(unit) == MasonryPlacementFailure.None;
        }

        public MasonryPlacementFailure GetPlacementFailure(MasonryUnitPlacement unit)
        {
            if (State.Frozen) return MasonryPlacementFailure.Frozen;
            if (unit.GetFootprint().Any(position =>
                State.ReservedPositions.Contains(position)
                || State.Units.Any(existing => existing.Occupies(position)))) return MasonryPlacementFailure.Occupied;
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

            State.Units.Add(unit);
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
                Touch();
                MarkDirty(true);
                Api.World.BlockAccessor.MarkBlockDirty(Pos);
            }

            return changed;
        }

        public bool ApplySideMortar(MasonryGridPosition cell, BlockFacing face)
        {
            if (State.Frozen || !face.IsHorizontal) return false;

            MasonryUnitPlacement? unit = FindUnit(cell);
            if (unit == null || unit.Kind == MasonryUnitKind.RammedEarth) return false;

            // Selection faces point toward the ray on exposed brick sides, but
            // narrow joints may resolve to either brick. Try the hit-facing
            // side first, then its opposite so both sides of a seam work.
            MasonryGridPosition neighborPosition = new(cell.X + face.Normali.X, cell.Y, cell.Z + face.Normali.Z);
            MasonryUnitPlacement? neighbor = FindUnit(neighborPosition);
            if (neighbor == null || neighbor == unit || neighbor.Kind == MasonryUnitKind.RammedEarth)
            {
                neighborPosition = new(cell.X - face.Normali.X, cell.Y, cell.Z - face.Normali.Z);
                neighbor = FindUnit(neighborPosition);
            }

            if (neighbor == null || neighbor == unit || neighbor.Kind == MasonryUnitKind.RammedEarth) return false;

            string jointKey = GetJointKey(cell, neighborPosition);
            if (!State.MortaredSideJoints.Add(jointKey)) return false;

            Touch();
            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
            return true;
        }

        public ItemStack? TryRemoveUnmortaredUnit(MasonryGridPosition cell)
        {
            if (State.Frozen) return null;
            MasonryUnitPlacement? unit = FindUnit(cell);
            if (unit == null || unit.MortaredPositions.Count > 0) return null;

            MasonryGridPosition[] footprint = unit.GetFootprint().ToArray();
            if (State.MortaredSideJoints.Any(joint => footprint.Any(position => joint.Contains($"{position.X},{position.Y},{position.Z}")))) return null;

            State.Units.Remove(unit);
            ReleaseNeighborReservations(footprint);
            Touch();
            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);

            AssetLocation code = unit.Kind switch
            {
                MasonryUnitKind.HalfBrick => new AssetLocation("brickbybrick", unit.MaterialCode),
                MasonryUnitKind.RammedEarth => new AssetLocation("brickbybrick:testrammedearth"),
                _ => new AssetLocation("game", unit.MaterialCode)
            };
            Item? collectible = Api.World.GetItem(code);
            if (collectible == null) return null;

            ItemStack recovered = new(collectible);
            if (State.Units.Count == 0 && State.ReservedPositions.Count == 0)
            {
                Api.World.BlockAccessor.SetBlock(0, Pos);
            }

            return recovered;
        }

        public bool IsUnmortaredBrickOfColor(MasonryGridPosition cell, string color)
        {
            MasonryUnitPlacement? unit = FindUnit(cell);
            if (unit == null || unit.Kind == MasonryUnitKind.RammedEarth || unit.MortaredPositions.Count > 0) return false;

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
            long started = Stopwatch.GetTimestamp();
            int unitCount = State.Units.Count;
            int meshPartCount = unitCount
                + State.Units.Sum(unit => unit.MortaredPositions.Count)
                + State.MortaredSideJoints.Count;
            BlockBehaviorShapeTexturesFromAttributes? behavior = Block.GetBehavior<BlockBehaviorShapeTexturesFromAttributes>();
            if (behavior == null)
            {
                RecordTessellation(started, unitCount, meshPartCount);
                return false;
            }

            if (State.Frozen && State.FrozenShape == FrozenMasonryShape.Arbitrary)
            {
                MeshData? arbitraryMesh = MasonryStaticMeshBuilder.Build(State, behavior, Pos, out _);
                if (arbitraryMesh != null)
                {
                    mesher.AddMeshData(arbitraryMesh);
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

                // A parameterless MeshData has no backing arrays. Seed the
                // combined mesh from the first valid component before append.
                if (combinedMesh == null) combinedMesh = mesh.Clone();
                else combinedMesh.AddMeshData(mesh);
            }

            foreach (MasonryUnitPlacement unit in State.Units)
            {
                CompositeShape shape = new()
                {
                    Base = new AssetLocation(unit.Kind == MasonryUnitKind.RammedEarth
                        ? "brickbybrick:shapes/block/realistic/rammedearth.json"
                        : "brickbybrick:shapes/block/realistic/brick.json")
                };

                Variants variants = new();
                variants.Set("color", GetMaterialColor(unit));
                MasonryGridPosition[] footprint = unit.GetFootprint().ToArray();
                int minimumX = footprint.Min(position => position.X);
                int minimumZ = footprint.Min(position => position.Z);
                float width = (footprint.Max(position => position.X) - minimumX + 1) * 0.25f;
                float depth = (footprint.Max(position => position.Z) - minimumZ + 1) * 0.25f;
                string materialColor = GetMaterialColor(unit);
                string unitCacheKey = $"unit:{unit.Kind}:{materialColor}:{minimumX}:{unit.Origin.Y}:{minimumZ}:{width}:{depth}";
                MeshData unitMesh = GetTransformedMesh(unitCacheKey, () =>
                {
                    MeshData mesh = behavior.GetOrCreateMesh(variants, shape, Pos, $"{unit.Kind}-{materialColor}").Clone();
                    float[] matrix = Matrixf.Create()
                        .Translate(
                            minimumX * 0.25f + JointInset,
                            unit.Origin.Y * 0.25f + JointInset,
                            minimumZ * 0.25f + JointInset)
                        .Scale(width - JointInset * 2, 0.25f - JointInset * 2, depth - JointInset * 2)
                        .Values;
                    return mesh.MatrixTransform(matrix);
                });
                AddMesh(unitMesh);

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

            if (consolidateMesh && combinedMesh != null)
            {
                lock (frozenMeshSync)
                {
                    frozenCombinedMesh ??= combinedMesh;
                    combinedMesh = frozenCombinedMesh;
                }

                Interlocked.Increment(ref consolidatedMeshBuilds);
                MasonryFrozenMeshCache.Store(this, combinedMesh);
                mesher.AddMeshData(combinedMesh);
                meshPartCount = 1;
            }

            RecordTessellation(started, unitCount, meshPartCount);

            return true;
        }

        public static void ResetTessellationProfile()
        {
            BlockStaticMasonry.ResetProfile();
            Interlocked.Exchange(ref tessellationCalls, 0);
            Interlocked.Exchange(ref frozenTessellationCalls, 0);
            Interlocked.Exchange(ref tessellatedUnits, 0);
            Interlocked.Exchange(ref tessellatedMeshParts, 0);
            Interlocked.Exchange(ref tessellationStopwatchTicks, 0);
            Interlocked.Exchange(ref slowestTessellationTicks, 0);
            Interlocked.Exchange(ref transformedMeshCacheHits, 0);
            Interlocked.Exchange(ref transformedMeshCacheMisses, 0);
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
            long cacheHits = Interlocked.Read(ref transformedMeshCacheHits);
            long cacheMisses = Interlocked.Read(ref transformedMeshCacheMisses);
            long combinedBuilds = Interlocked.Read(ref consolidatedMeshBuilds);
            long combinedReuses = Interlocked.Read(ref consolidatedMeshReuses);
            long managedBytes = GC.GetTotalMemory(false);
            string slowestCell;
            lock (TessellationProfileSync) slowestCell = slowestTessellationCell;

            return $"Calls: {calls:N0} ({frozenCalls:N0} frozen); units: {units:N0}; mesh parts: {meshParts:N0}; "
                + $"tessellation: {elapsedMilliseconds:N1} ms total, {(calls == 0 ? 0 : elapsedMilliseconds / calls):N3} ms/cell; "
                + $"slowest: {slowestMilliseconds:N2} ms at {slowestCell}; managed memory: {managedBytes / 1048576d:N1} MiB "
                + $"({(managedBytes - baselineManagedBytes) / 1048576d:+0.0;-0.0;0.0} MiB); "
                + $"mesh cache: {cacheHits:N0} hits, {cacheMisses:N0} misses, {TransformedMeshCache.Count:N0} entries; "
                + $"consolidated: {combinedBuilds:N0} builds, {combinedReuses:N0} reuses; "
                + $"{MasonryFrozenMeshCache.GetProfile()}; "
                + $"{BlockStaticMasonry.GetProfile()}; "
                + $"GC: gen0 +{GC.CollectionCount(0) - baselineGenerationZeroCollections}, "
                + $"gen1 +{GC.CollectionCount(1) - baselineGenerationOneCollections}, "
                + $"gen2 +{GC.CollectionCount(2) - baselineGenerationTwoCollections}.";
        }

        public static void ClearTransformedMeshCache()
        {
            TransformedMeshCache.Clear();
        }

        internal void ReleaseFrozenMesh(MeshData mesh)
        {
            lock (frozenMeshSync)
            {
                if (ReferenceEquals(frozenCombinedMesh, mesh)) frozenCombinedMesh = null;
            }
        }

        private static MeshData GetTransformedMesh(string key, Func<MeshData> createMesh)
        {
            if (TransformedMeshCache.TryGetValue(key, out MeshData? cached))
            {
                Interlocked.Increment(ref transformedMeshCacheHits);
                return cached;
            }

            MeshData created = createMesh();
            MeshData selected = TransformedMeshCache.GetOrAdd(key, created);
            if (ReferenceEquals(selected, created)) Interlocked.Increment(ref transformedMeshCacheMisses);
            else Interlocked.Increment(ref transformedMeshCacheHits);
            return selected;
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

        // Mortar fills only the shared vertical joint between adjacent masonry
        // units. Rammed earth intentionally keeps direct contact with bricks.
        private void AddSideJointMortar(Action<MeshData> addMesh, BlockBehaviorShapeTexturesFromAttributes behavior)
        {
            HashSet<string> renderedJoints = new();
            (int X, int Z)[] directions = { (1, 0), (-1, 0), (0, 1), (0, -1) };
            CompositeShape mortarShape = new()
            {
                Base = new AssetLocation("brickbybrick:shapes/block/realistic/mortar.json")
            };

            foreach (MasonryUnitPlacement unit in State.Units.Where(candidate => candidate.Kind != MasonryUnitKind.RammedEarth))
            {
                foreach (MasonryGridPosition cell in unit.MortaredPositions)
                {
                    foreach ((int offsetX, int offsetZ) in directions)
                    {
                        MasonryGridPosition neighborPosition = new(cell.X + offsetX, cell.Y, cell.Z + offsetZ);
                        MasonryUnitPlacement? neighbor = State.Units.FirstOrDefault(candidate =>
                            candidate != unit
                            && candidate.Kind != MasonryUnitKind.RammedEarth
                            && candidate.Occupies(neighborPosition));
                        if (neighbor == null) continue;

                        string jointKey = GetJointKey(cell, neighborPosition);
                        bool hasTopMortar = unit.MortaredPositions.Contains(cell) || neighbor.MortaredPositions.Contains(neighborPosition);
                        if (!hasTopMortar && !State.MortaredSideJoints.Contains(jointKey)) continue;
                        if (!renderedJoints.Add(jointKey)) continue;

                        bool alongX = offsetX != 0;
                        float x = alongX
                            ? (offsetX > 0 ? (cell.X + 1) * 0.25f - JointInset : cell.X * 0.25f - JointInset)
                            : cell.X * 0.25f + JointInset;
                        float z = alongX
                            ? cell.Z * 0.25f + JointInset
                            : (offsetZ > 0 ? (cell.Z + 1) * 0.25f - JointInset : cell.Z * 0.25f - JointInset);
                        string sideCacheKey = $"mortar-side:{alongX}:{x}:{cell.Y}:{z}";
                        MeshData mortarMesh = GetTransformedMesh(sideCacheKey, () =>
                        {
                            MeshData mesh = behavior.GetOrCreateMesh(new Variants(), mortarShape, Pos, "mortar-side").Clone();
                            float[] jointMatrix = Matrixf.Create()
                                .Translate(x, cell.Y * 0.25f + JointInset, z)
                                .Scale(
                                    alongX ? JointInset * 2 : 0.25f - JointInset * 2,
                                    0.25f - JointInset * 2,
                                    alongX ? 0.25f - JointInset * 2 : JointInset * 2)
                                .Values;
                            return mesh.MatrixTransform(jointMatrix);
                        });
                        addMesh(mortarMesh);
                    }
                }
            }
        }

        public bool CanReserve(IEnumerable<MasonryGridPosition> positions)
        {
            if (State.Frozen) return false;
            return positions.All(position =>
                !State.ReservedPositions.Contains(position)
                && !State.Units.Any(existing => existing.Occupies(position)));
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

        private void ReleaseNeighborReservations(IEnumerable<MasonryGridPosition> footprint)
        {
            foreach (IGrouping<(int X, int Z), MasonryGridPosition> group in footprint
                .Where(position => position.X < 0 || position.X > 3 || position.Z < 0 || position.Z > 3)
                .GroupBy(position => ((int)Math.Floor(position.X / 4d), (int)Math.Floor(position.Z / 4d))))
            {
                BlockPos neighborPos = Pos.AddCopy(group.Key.X, 0, group.Key.Z);
                if (Api.World.BlockAccessor.GetBlockEntity(neighborPos) is not BlockEntityRealisticMasonry neighbor) continue;

                foreach (MasonryGridPosition position in group)
                {
                    neighbor.State.ReservedPositions.Remove(new MasonryGridPosition(
                        GameMath.Mod(position.X, 4), position.Y, GameMath.Mod(position.Z, 4)));
                }

                neighbor.Touch();
                if (neighbor.State.Units.Count == 0 && neighbor.State.ReservedPositions.Count == 0)
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

        public void Reopen()
        {
            if (!State.Frozen) return;

            State.Frozen = false;
            State.FrozenShape = FrozenMasonryShape.Arbitrary;
            Touch();
            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
        }

        public void PopulateForProfiling(Random random, bool frozen)
        {
            string[] colors = { "black", "brown", "cream", "gray", "orange", "red", "tan", "clinker" };
            State = new MasonryCellState();
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            for (int z = 0; z < 4; z++)
            {
                if (random.NextDouble() > 0.42) continue;

                MasonryUnitPlacement unit = new()
                {
                    Id = $"p{x}{y}{z}",
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
            State.Frozen = frozen;
            freezeRevision++;
            ScheduleFreeze();
            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
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
            State.FrozenShape = InferFrozenShape();
            State.Frozen = true;

            if (BlockStaticMasonry.TryGetBlockCode(State.FrozenShape, out AssetLocation staticCode))
            {
                Block? staticBlock = Api.World.GetBlock(staticCode);
                if (staticBlock != null)
                {
                    byte[] packedState = MasonryStateCodec.Encode(State);
                    FrozenMasonryChunkStore.Set(Api.World.BlockAccessor, Pos, packedState);
                    Api.World.BlockAccessor.ExchangeBlock(staticBlock.Id, Pos);
                    brickbybrickModSystem.BroadcastStaticMasonryState(Pos, packedState, false);
                    return;
                }
            }

            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
        }

        private void Touch()
        {
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
