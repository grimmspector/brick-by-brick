using brickbybrick.Blocks;
using brickbybrick.items;
using brickbybrick.RealisticConstruction;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using static brickbybrick.items.ItemTrowel;

namespace brickbybrick
{
    public class brickbybrickModSystem : ModSystem
    {
        private const string ConfigFileName = "brickbybrick.json";
        private const string MasonryGuidePageCode = "gamemechanicinfo-brickbybrick-masonry";
        private const int ProfileResetPacket = -100;
        private const int ProfileReportPacket = -101;
        private const int ProfileResetAcknowledgementPacket = -102;
        private const int ProfileReportAcknowledgementPacket = -103;
        private const int ProfileCaptureMarkerPacket = -104;
        private const int ProfileBatchSize = 256;
        private const string ServerProfileTracker = "__server-wall-profile__";
        private const string ServerMatrixTracker = "__server-matrix__";
        private const string ServerMatrixLiveTracker = "__server-matrix-live__";
        private const string ServerMatrixStaticTracker = "__server-matrix-static__";

        internal static BrickByBrickConfig Config { get; private set; } = new();

        internal static bool ConfigLoaded { get; private set; }

        private ModSystemSurvivalHandbook? survivalHandbook;
        private ICoreClientAPI? clientApi;
        private IClientNetworkChannel? realisticClientChannel;
        private ActionConsumable<KeyCombination>? vanillaToolModeHandler;
        private static ICoreServerAPI? serverApi;
        private static readonly Dictionary<string, List<BlockPos>> ProfileCellsByPlayer = new();
        private static readonly Dictionary<string, ProfileExerciseSession> ProfileExercisesByPlayer = new();
        private static readonly HashSet<string> ActiveBenchmarks = new();
        private static readonly object ProfileLogSync = new();
        private static string profileSessionId = "not-started";

        // Registers the item and block classes referenced by the JSON assets.
        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            LoadConfig(api);
            Mod.Logger.Event($"started '{Mod.Info.Name}' mod");
            api.RegisterItemClass(Mod.Info.ModID + ".trowel", typeof(ItemTrowel));
            api.RegisterBlockClass(Mod.Info.ModID + ".cobbleblock", typeof(BlockStone));
            api.RegisterBlockClass(Mod.Info.ModID + ".brickblock", typeof(BlockBrick));
            api.RegisterBlockClass(Mod.Info.ModID + ".realisticmasonry", typeof(BlockRealisticMasonry));
            api.RegisterBlockClass(Mod.Info.ModID + ".staticmasonry", typeof(BlockStaticMasonry));
            api.RegisterBlockEntityClass(Mod.Info.ModID + ".realisticmasonry", typeof(BlockEntityRealisticMasonry));

            api.Network.RegisterChannel("brickbybrick-realistic")
                .RegisterMessageType<int>()
                .RegisterMessageType<RealisticControlPacket>()
                .RegisterMessageType<StaticMasonryStatePacket>();

        }

        // Loads one shared settings object on each side. Vintage Story writes
        // the default file only when none exists, then validation guards edits.
        private void LoadConfig(ICoreAPI api)
        {
            ConfigLoaded = false;
            try
            {
                Config = api.LoadModConfig<BrickByBrickConfig>(ConfigFileName) ?? new BrickByBrickConfig();
            }
            catch (Exception exception)
            {
                Mod.Logger.Error($"Could not load {ConfigFileName}; defaults will be used. {exception.Message}");
                Config = new BrickByBrickConfig();
            }

            Config.Validate();
            api.StoreModConfig(Config, ConfigFileName);
            ConfigLoaded = true;
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            ResetWorldScopedProfileState();
            serverApi = api;
            profileSessionId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24];
            AppendProfileLog(
                api,
                "PROFILE SESSION START",
                $"Session: {profileSessionId}; mod version: {Mod.Info.Version}; "
                + $"optimized frozen meshes: {Config.Realism.EnableOptimizedFrozenMeshes}; "
                + $"frozen cache: {Config.Realism.FrozenMeshCacheMiB} MiB; "
                + $"transformed cache: {Config.Realism.TransformedMeshCacheMiB} MiB; "
                + $"static cache: {Config.Realism.StaticMeshCacheMiB} MiB; "
                + $"curing enabled: {Config.Curing.EnableMortarCuring}; "
                + $"inactive freeze: {Config.Curing.InactiveFreezeSeconds:N1} seconds.");
            api.Network.GetChannel("brickbybrick-realistic")
                .SetMessageHandler<int>((player, packet) => OnRealisticServerPacket(api, player, packet))
                .SetMessageHandler<RealisticControlPacket>((player, packet) => OnRealisticServerPacket(api, player, packet));
            api.Event.RegisterGameTickListener(_ =>
            {
                MasonryFreezeScheduler.DrainReady();
                FrozenMasonryChunkStore.FlushDue();
            }, 100);
            RegisterProfilingCommands(api);
            RegisterMasonryDiagnosticCommand(api);
            ValidateConstructionRegistry(api);
        }

        // Static profiling collections survive integrated-server world swaps.
        // Reset them before the new world can accept profiling commands.
        private static void ResetWorldScopedProfileState()
        {
            foreach (ProfileExerciseSession session in ProfileExercisesByPlayer.Values)
            {
                session.Active = false;
            }

            ProfileCellsByPlayer.Clear();
            ProfileExercisesByPlayer.Clear();
            ActiveBenchmarks.Clear();
        }

        private static void RegisterProfilingCommands(ICoreServerAPI api)
        {
            api.ChatCommands.Create("bbbprofile")
                .WithDescription("Profiles large sets of Realistic masonry cells.")
                .RequiresPrivilege(Privilege.controlserver)
                .BeginSubCommand("spawn")
                    .WithArgs(api.ChatCommands.Parsers.Int("count"))
                    .HandleWith(args => SpawnProfileCells(api, args))
                .EndSubCommand()
                .BeginSubCommand("duplicates")
                    .WithArgs(
                        api.ChatCommands.Parsers.Int("arrangements"),
                        api.ChatCommands.Parsers.Int("copies"))
                    .HandleWith(args => SpawnDuplicateProfileCells(api, args))
                .EndSubCommand()
                .BeginSubCommand("geometryquick")
                    .HandleWith(args => SpawnGeometryCorpus(api, args, false))
                .EndSubCommand()
                .BeginSubCommand("geometrycorpus")
                    .HandleWith(args => SpawnGeometryCorpus(api, args, true))
                .EndSubCommand()
                .BeginSubCommand("realistic")
                    .WithArgs(api.ChatCommands.Parsers.Int("count"))
                    .HandleWith(args => SpawnRealisticBaseCorpus(api, args))
                .EndSubCommand()
                .BeginSubCommand("diagonal")
                    .WithArgs(api.ChatCommands.Parsers.Int("count"))
                    .HandleWith(args => SpawnAngledCorpus(api, args, false))
                .EndSubCommand()
                .BeginSubCommand("mixedangles")
                    .WithArgs(api.ChatCommands.Parsers.Int("count"))
                    .HandleWith(args => SpawnAngledCorpus(api, args, true))
                .EndSubCommand()
                .BeginSubCommand("walls")
                    .WithArgs(
                        api.ChatCommands.Parsers.Int("length"),
                        api.ChatCommands.Parsers.Int("height"))
                    .HandleWith(args => SpawnWallCorpus(api, args))
                .EndSubCommand()
                .BeginSubCommand("serverrun")
                    .WithArgs(
                        api.ChatCommands.Parsers.Int("length"),
                        api.ChatCommands.Parsers.Int("height"))
                    .HandleWith(args => RunServerWallProfile(api, args))
                .EndSubCommand()
                .BeginSubCommand("servercompact")
                    .WithArgs(
                        api.ChatCommands.Parsers.Int("length"),
                        api.ChatCommands.Parsers.Int("height"))
                    .HandleWith(args => RunServerWallProfile(api, args, true))
                .EndSubCommand()
                .BeginSubCommand("servermatrix")
                    .WithArgs(
                        api.ChatCommands.Parsers.Int("length"),
                        api.ChatCommands.Parsers.Int("height"))
                    .HandleWith(args => RunServerProfileMatrix(api, args))
                .EndSubCommand()
                .BeginSubCommand("serverclear")
                    .HandleWith(_ => ClearServerWallProfile(api))
                .EndSubCommand()
                .BeginSubCommand("runtime")
                    .HandleWith(_ => ReportMasonryRuntime(api))
                .EndSubCommand()
                .BeginSubCommand("runtimereset")
                    .HandleWith(_ => ResetMasonryRuntimeProfile(api))
                .EndSubCommand()
                .BeginSubCommand("clear")
                    .HandleWith(args => ClearProfileCells(api, args))
                .EndSubCommand()
                .BeginSubCommand("report")
                    .HandleWith(args => ReportProfileCells(api, args))
                .EndSubCommand()
                .BeginSubCommand("exercise")
                    .HandleWith(args => StartProfileExercise(api, args))
                .EndSubCommand()
                .BeginSubCommand("benchmark")
                    .HandleWith(args => StartTrackedBenchmark(api, args))
                .EndSubCommand()
                .BeginSubCommand("random")
                    .WithArgs(api.ChatCommands.Parsers.Int("actions"))
                    .HandleWith(args => RunRandomProfileActions(api, args))
                .EndSubCommand()
                .BeginSubCommand("stop")
                    .HandleWith(args => StopProfileExercise(api, args))
                .EndSubCommand();
        }

        // Reports saved geometry without changing the selected masonry cell.
        private static void RegisterMasonryDiagnosticCommand(ICoreServerAPI api)
        {
            api.ChatCommands.Create("bbbmeshdump")
                .WithDescription("Reports realistic masonry geometry diagnostics.")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(args => DumpTargetedMasonry(api, args))
                .BeginSubCommand("target")
                    .HandleWith(args => DumpTargetedMasonry(api, args))
                .EndSubCommand()
                .BeginSubCommand("radius")
                    .WithArgs(api.ChatCommands.Parsers.OptionalInt("radius", 4))
                    .HandleWith(args => DumpMasonryRadius(api, args))
                .EndSubCommand()
                .BeginSubCommand("cases")
                    .HandleWith(args => DumpMasonryGeometryCases(api, args))
                .EndSubCommand();
        }

        private static TextCommandResult DumpTargetedMasonry(ICoreServerAPI api, TextCommandCallingArgs args)
        {
            IServerPlayer? player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("This command requires a player.");
            BlockSelection? selection = player.CurrentBlockSelection;
            if (selection == null) return TextCommandResult.Error("Look directly at the affected masonry block first.");
            if (api.World.BlockAccessor.GetBlockEntity(selection.Position) is not BlockEntityRealisticMasonry entity)
            {
                return TextCommandResult.Error("The targeted block is not live realistic masonry.");
            }

            string report = BuildMasonryDiagnosticReport(api, selection.Position, entity, "TARGET");
            AppendProfileLog(api, "MASONRY GEOMETRY TARGET", report);
            api.Logger.Notification(report);
            return TextCommandResult.Success($"Dumped targeted masonry geometry to {GetProfileLogPath()}.");
        }

        private static TextCommandResult DumpMasonryRadius(ICoreServerAPI api, TextCommandCallingArgs args)
        {
            IServerPlayer? player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("This command requires a player.");

            int radius = GameMath.Clamp((int)args[0], 1, 16);
            BlockPos center = player.Entity.Pos.AsBlockPos;
            List<string> reports = new();
            for (int x = center.X - radius; x <= center.X + radius; x++)
            for (int y = center.Y - radius; y <= center.Y + radius; y++)
            for (int z = center.Z - radius; z <= center.Z + radius; z++)
            {
                BlockPos pos = new(x, y, z);
                if (api.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityRealisticMasonry entity) continue;
                reports.Add(BuildMasonryDiagnosticReport(api, pos, entity, "RADIUS"));
            }

            string report = reports.Count == 0
                ? $"No live realistic masonry found within radius {radius} of {center}."
                : string.Join(Environment.NewLine + Environment.NewLine, reports);
            AppendProfileLog(api, "MASONRY GEOMETRY RADIUS", report);
            api.Logger.Notification($"Masonry radius dump: center={center}, radius={radius}, cells={reports.Count}. Written to {GetProfileLogPath()}.");
            return TextCommandResult.Success($"Dumped {reports.Count} masonry cells to {GetProfileLogPath()}.");
        }

        private static TextCommandResult DumpMasonryGeometryCases(ICoreServerAPI api, TextCommandCallingArgs args)
        {
            List<MasonryUnitPlacement> cases = new()
            {
                CreateDiagnosticUnit("full-east", MasonryUnitKind.WholeBrick, MasonryVisualShape.Cuboid, MasonryOrientation.East, 1, 0, 1),
                CreateDiagnosticUnit("full-southeast", MasonryUnitKind.WholeBrick, MasonryVisualShape.Cuboid, MasonryOrientation.SouthEast, 1, 0, 1),
                CreateDiagnosticUnit("half-east", MasonryUnitKind.HalfBrick, MasonryVisualShape.Cuboid, MasonryOrientation.East, 1, 0, 1),
                CreateDiagnosticUnit("half-southeast", MasonryUnitKind.HalfBrick, MasonryVisualShape.Cuboid, MasonryOrientation.SouthEast, 1, 0, 1),
                CreateDiagnosticUnit("wedge-southeast", MasonryUnitKind.HalfBrick, MasonryVisualShape.TriangleWedge, MasonryOrientation.SouthEast, 1, 0, 1),
                CreateDiagnosticUnit("earth-2x2-east", MasonryUnitKind.RammedEarth, MasonryVisualShape.Cuboid, MasonryOrientation.East, 1, 0, 1),
                CreateDiagnosticUnit("earth-1x1-southeast", MasonryUnitKind.SmallRammedEarth, MasonryVisualShape.Cuboid, MasonryOrientation.SouthEast, 1, 0, 1)
            };

            string report = string.Join(Environment.NewLine + Environment.NewLine, cases.Select(unit => DescribeDiagnosticUnit(unit, "CASE")));
            AppendProfileLog(api, "MASONRY GEOMETRY CASES", report);
            api.Logger.Notification($"Dumped {cases.Count} deterministic masonry geometry cases to {GetProfileLogPath()}.");
            return TextCommandResult.Success($"Dumped deterministic geometry cases to {GetProfileLogPath()}.");
        }

        private static MasonryUnitPlacement CreateDiagnosticUnit(string id, MasonryUnitKind kind, MasonryVisualShape shape, MasonryOrientation orientation, int x, int y, int z)
        {
            return new MasonryUnitPlacement
            {
                Id = id,
                Kind = kind,
                VisualShape = shape,
                MaterialCode = kind is MasonryUnitKind.RammedEarth or MasonryUnitKind.SmallRammedEarth ? "testrammedearth" : "burnedbrick-cream",
                Orientation = orientation,
                Origin = new MasonryGridPosition(x, y, z)
            };
        }

        private static string BuildMasonryDiagnosticReport(ICoreServerAPI api, BlockPos pos, BlockEntityRealisticMasonry entity, string source)
        {
            MasonryCellState state = entity.State;
            Dictionary<MasonryGridPosition, List<MasonryUnitPlacement>> occupants = new();
            foreach (MasonryUnitPlacement unit in state.Units.Concat(state.ReservedUnits))
            {
                foreach (MasonryGridPosition position in unit.GetFootprint())
                {
                    if (!occupants.TryGetValue(position, out List<MasonryUnitPlacement>? units))
                    {
                        units = new List<MasonryUnitPlacement>();
                        occupants[position] = units;
                    }
                    units.Add(unit);
                }
            }

            List<KeyValuePair<MasonryGridPosition, List<MasonryUnitPlacement>>> overlaps = occupants
                .Where(entry => entry.Value.Count > 1)
                .OrderBy(entry => entry.Key.Y)
                .ThenBy(entry => entry.Key.Z)
                .ThenBy(entry => entry.Key.X)
                .ToList();

            Block block = api.World.BlockAccessor.GetBlock(pos);
            Cuboidf[] selectionBoxes = block.GetSelectionBoxes(api.World.BlockAccessor, pos) ?? Array.Empty<Cuboidf>();
            Cuboidf[] collisionBoxes = block.GetCollisionBoxes(api.World.BlockAccessor, pos) ?? Array.Empty<Cuboidf>();
            Cuboidf[] geometryBoxes = entity.GetGeometryBoxes();

            List<string> lines = new()
            {
                $"[{source}] cell={pos}, block={block.Code}, frozen={state.Frozen}, frozenShape={state.FrozenShape}, units={state.Units.Count}, reservedUnits={state.ReservedUnits.Count}, reservedPositions={state.ReservedPositions.Count}, occupiedCells={occupants.Count}, overlaps={overlaps.Count}, topMortar={state.Units.Sum(unit => unit.MortaredPositions.Count)}, sideMortar={state.MortaredSideJoints.Count}",
                $"selectionBoxes count={selectionBoxes.Length} bounds={FormatCuboidUnion(selectionBoxes)} boxes={FormatCuboids(selectionBoxes)}",
                $"collisionBoxes count={collisionBoxes.Length} bounds={FormatCuboidUnion(collisionBoxes)} boxes={FormatCuboids(collisionBoxes)}",
                $"geometryBoxes count={geometryBoxes.Length} bounds={FormatCuboidUnion(geometryBoxes)} boxes={FormatCuboids(geometryBoxes)}",
                $"reservedPositions=[{FormatPositions(state.ReservedPositions)}]"
            };

            lines.AddRange(state.Units.Select(unit => DescribeDiagnosticUnit(unit, "UNIT")));
            lines.AddRange(state.ReservedUnits.Select(unit => DescribeDiagnosticUnit(unit, "RESERVED_UNIT")));
            foreach (KeyValuePair<MasonryGridPosition, List<MasonryUnitPlacement>> overlap in overlaps)
            {
                lines.Add($"OVERLAP cell={FormatPosition(overlap.Key)} units=[{string.Join(",", overlap.Value.Select(unit => $"{unit.Id}/{unit.Kind}/{unit.VisualShape}/{unit.Orientation}"))}]");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string DescribeDiagnosticUnit(MasonryUnitPlacement unit, string label)
        {
            (int X1, int Y1, int Z1, int X2, int Y2, int Z2, int Count) voxelBounds = GetVoxelBounds(unit);
            MasonryGridPosition[] footprint = unit.GetFootprint().ToArray();
            MasonryGridPosition[] reservation = MasonryVoxelGeometry.GetReservationFootprint(unit).ToArray();
            return $"{label} id={unit.Id}, kind={unit.Kind}, visual={unit.VisualShape}, material={unit.MaterialCode}, orientation={unit.Orientation}, angle={MasonryVoxelGeometry.GetAngleDegrees(unit.Orientation):0.###}, origin={FormatPosition(unit.Origin)}, "
                + $"footprintCount={footprint.Length}, footprint=[{FormatPositions(footprint)}], reservationCount={reservation.Length}, reservation=[{FormatPositions(reservation)}], "
                + $"voxelCount={voxelBounds.Count}, voxelBounds=[{voxelBounds.X1},{voxelBounds.Y1},{voxelBounds.Z1} -> {voxelBounds.X2},{voxelBounds.Y2},{voxelBounds.Z2}], voxelWorldBounds=[{voxelBounds.X1 / 16d:0.###},{voxelBounds.Y1 / 16d:0.###},{voxelBounds.Z1 / 16d:0.###} -> {(voxelBounds.X2 + 1) / 16d:0.###},{(voxelBounds.Y2 + 1) / 16d:0.###},{(voxelBounds.Z2 + 1) / 16d:0.###}], mortared=[{FormatPositions(unit.MortaredPositions)}]";
        }

        private static (int X1, int Y1, int Z1, int X2, int Y2, int Z2, int Count) GetVoxelBounds(MasonryUnitPlacement unit)
        {
            (int X, int Y, int Z)[] voxels = MasonryVoxelGeometry.GetVoxels(unit).ToArray();
            if (voxels.Length == 0) return (0, 0, 0, 0, 0, 0, 0);
            return (
                voxels.Min(voxel => voxel.X),
                voxels.Min(voxel => voxel.Y),
                voxels.Min(voxel => voxel.Z),
                voxels.Max(voxel => voxel.X),
                voxels.Max(voxel => voxel.Y),
                voxels.Max(voxel => voxel.Z),
                voxels.Length);
        }

        private static string FormatCuboidUnion(Cuboidf[] boxes)
        {
            if (boxes.Length == 0) return "empty";
            return $"[{boxes.Min(box => box.X1):0.###},{boxes.Min(box => box.Y1):0.###},{boxes.Min(box => box.Z1):0.###} -> {boxes.Max(box => box.X2):0.###},{boxes.Max(box => box.Y2):0.###},{boxes.Max(box => box.Z2):0.###}]";
        }

        private static string FormatCuboids(Cuboidf[] boxes)
        {
            return string.Join(";", boxes.Select(box => $"[{box.X1:0.###},{box.Y1:0.###},{box.Z1:0.###}->{box.X2:0.###},{box.Y2:0.###},{box.Z2:0.###}]"));
        }

        private static string FormatPositions(IEnumerable<MasonryGridPosition> positions)
        {
            return string.Join(";", positions
                .OrderBy(position => position.Y)
                .ThenBy(position => position.Z)
                .ThenBy(position => position.X)
                .Select(FormatPosition));
        }

        private static string FormatPosition(MasonryGridPosition position)
        {
            return $"{position.X},{position.Y},{position.Z}";
        }

        private static TextCommandResult StartProfileExercise(ICoreServerAPI api, TextCommandCallingArgs args)
        {
            IServerPlayer? player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("This command requires a player.");
            if (ProfileExercisesByPlayer.TryGetValue(player.PlayerUID, out ProfileExerciseSession? existing) && existing.Active)
            {
                return TextCommandResult.Error("A profiling exercise is already running for this player.");
            }

            Block? testBlock = api.World.GetBlock(new AssetLocation("game:rock-granite"));
            if (testBlock == null || testBlock.Id == 0) return TextCommandResult.Error("The profiling test block game:rock-granite is unavailable.");

            ProfileExerciseSession session = new(player, player.Entity.Pos.AsBlockPos.Copy(), testBlock.Id);
            ProfileExercisesByPlayer[player.PlayerUID] = session;
            if (api.Server.IsDedicated)
            {
                api.Network.GetChannel("brickbybrick-realistic").SendPacket(new RealisticControlPacket { Code = ProfileResetPacket }, player);
            }
            else
            {
                // Integrated single-player shares this assembly and its static
                // profiling counters with the client, so no packet is needed.
                BlockEntityRealisticMasonry.ResetTessellationProfile();
                session.ClientResetAcknowledged = true;
                AppendProfileLog(api, "AUTOMATED EXERCISE INTEGRATED RESET", "Client tessellation counters reset in-process.");
            }
            AppendProfileLog(api, "AUTOMATED EXERCISE START", $"Player: {player.PlayerName}; duration: 120 seconds; rate: 10 edits per second; origin: {session.Origin}.");
            ScheduleProfileExerciseEdit(api, session);
            api.Event.RegisterCallback(_ => CompleteProfileExercise(api, session, false), 120000);
            return TextCommandResult.Success("Started the two-minute masonry profiling exercise. Random ordinary edits will be restored automatically.");
        }

        private static TextCommandResult StopProfileExercise(ICoreServerAPI api, TextCommandCallingArgs args)
        {
            IServerPlayer? player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("This command requires a player.");
            if (!ProfileExercisesByPlayer.TryGetValue(player.PlayerUID, out ProfileExerciseSession? session) || !session.Active)
            {
                return TextCommandResult.Success("No profiling exercise is running.");
            }

            CompleteProfileExercise(api, session, true);
            return TextCommandResult.Success("Stopped the profiling exercise and restored its test blocks.");
        }

        private static void ScheduleProfileExerciseEdit(ICoreServerAPI api, ProfileExerciseSession session)
        {
            const int delayMilliseconds = 100;
            api.Event.RegisterCallback(_ =>
            {
                if (!session.Active) return;
                PerformProfileExerciseEdit(api, session);
                ScheduleProfileExerciseEdit(api, session);
            }, delayMilliseconds);
        }

        private static void PerformProfileExerciseEdit(ICoreServerAPI api, ProfileExerciseSession session)
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                int offsetX = session.Random.Next(-24, 25);
                int offsetZ = session.Random.Next(-24, 25);
                if (Math.Abs(offsetX) <= 1 && Math.Abs(offsetZ) <= 1) continue;

                // Keep edits above the four-cell-high profiling volume while
                // remaining in the same nearby terrain chunks.
                BlockPos pos = session.Origin.AddCopy(offsetX, session.Random.Next(5, 9), offsetZ);
                if (session.OriginalBlockIds.ContainsKey(pos)) continue;
                Block existing = api.World.BlockAccessor.GetBlock(pos);
                if (existing.Id != 0 || existing.Code?.Path == "realisticmasonry") continue;

                session.OriginalBlockIds[pos.Copy()] = existing.Id;
                Stopwatch stopwatch = Stopwatch.StartNew();
                api.World.BlockAccessor.SetBlock(session.TestBlockId, pos);
                stopwatch.Stop();
                session.RecordMutation(stopwatch.Elapsed.TotalMilliseconds);
                return;
            }

            session.SkippedEdits++;
        }

        private static void CompleteProfileExercise(ICoreServerAPI api, ProfileExerciseSession session, bool stopped)
        {
            if (!session.Active) return;
            session.Active = false;

            foreach (KeyValuePair<BlockPos, int> original in session.OriginalBlockIds)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                api.World.BlockAccessor.SetBlock(original.Value, original.Key);
                stopwatch.Stop();
                session.RecordMutation(stopwatch.Elapsed.TotalMilliseconds);
            }

            string report = $"Player: {session.Player.PlayerName}; stopped: {stopped}; edits placed: {session.OriginalBlockIds.Count:N0}; "
                + $"mutations including restore: {session.MutationCount:N0}; skipped: {session.SkippedEdits:N0}; "
                + $"client reset acknowledged: {session.ClientResetAcknowledged}; "
                + $"server SetBlock time: {session.TotalMutationMilliseconds:N2} ms total, "
                + $"{(session.MutationCount == 0 ? 0 : session.TotalMutationMilliseconds / session.MutationCount):N3} ms average, "
                + $"{session.SlowestMutationMilliseconds:N3} ms slowest.";
            AppendProfileLog(api, "AUTOMATED EXERCISE SERVER REPORT", report);

            // Let restoration-triggered chunk rebuilds finish before the client
            // snapshots its complete tessellation and memory measurements.
            api.Event.RegisterCallback(_ =>
            {
                if (api.Server.IsDedicated)
                {
                    api.Network.GetChannel("brickbybrick-realistic").SendPacket(new RealisticControlPacket { Code = ProfileReportPacket }, session.Player);
                }
                else
                {
                    string clientReport = BlockEntityRealisticMasonry.GetTessellationProfile();
                    AppendProfileLog(api, "AUTOMATED EXERCISE INTEGRATED CLIENT REPORT", clientReport);
                    session.ClientReportReceived = true;
                }

                session.Player.SendMessage(GlobalConstants.GeneralChatGroup, $"Profiling exercise complete. {report}", EnumChatType.Notification);
                api.Event.RegisterCallback(__ =>
                {
                    if (!session.ClientReportReceived)
                    {
                        AppendProfileLog(api, "AUTOMATED EXERCISE CLIENT REPORT MISSING", $"No client report acknowledgement was received for {session.Player.PlayerName}.");
                    }

                    ProfileExercisesByPlayer.Remove(session.Player.PlayerUID);
                }, 5000);
            }, 5000);
        }

        private static void OnRealisticServerPacket(ICoreServerAPI api, IServerPlayer player, int packet)
        {
            if (packet == ProfileResetAcknowledgementPacket)
            {
                if (ProfileExercisesByPlayer.TryGetValue(player.PlayerUID, out ProfileExerciseSession? session))
                {
                    session.ClientResetAcknowledged = true;
                }

                AppendProfileLog(api, "AUTOMATED EXERCISE CLIENT RESET ACK", $"Client reset acknowledged by {player.PlayerName}.");
                return;
            }

            if (packet == ProfileReportAcknowledgementPacket)
            {
                if (ProfileExercisesByPlayer.TryGetValue(player.PlayerUID, out ProfileExerciseSession? activeSession))
                {
                    activeSession.ClientReportReceived = true;
                }

                AppendProfileLog(api, $"AUTOMATED EXERCISE CLIENT REPORT ACK ({player.PlayerName})", "Client report was written to the client profiling log.");
                return;
            }

            OnRealisticOrientationPacket(player, packet);
        }

        private static void OnRealisticServerPacket(ICoreServerAPI api, IServerPlayer player, RealisticControlPacket packet)
        {
            if (packet.PlacementState)
            {
                ItemTrowel.SetRealisticPlacementState(player, packet.Orientation, packet.Variant);
                return;
            }

            OnRealisticServerPacket(api, player, packet.Code);
        }

        private static TextCommandResult SpawnProfileCells(ICoreServerAPI api, TextCommandCallingArgs args)
        {
            IServerPlayer? player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("This command requires a player.");

            int requested = GameMath.Clamp((int)args[0], 1, 100000);
            ClearTrackedProfileCells(api, player.PlayerUID);

            Block? constructionBlock = api.World.GetBlock(new AssetLocation("brickbybrick:realisticmasonry"));
            if (constructionBlock == null) return TextCommandResult.Error("Realistic masonry block is unavailable.");

            BlockPos center = player.Entity.Pos.AsBlockPos;
            List<BlockPos> created = new(requested);
            Random random = new(center.X ^ center.Z ^ requested);
            int horizontalRadius = (int)Math.Ceiling(Math.Sqrt(requested / 4d)) + 2;
            Stopwatch stopwatch = Stopwatch.StartNew();

            for (int radius = 2; radius <= horizontalRadius && created.Count < requested; radius++)
            {
                for (int x = -radius; x <= radius && created.Count < requested; x++)
                for (int z = -radius; z <= radius && created.Count < requested; z++)
                {
                    if (Math.Max(Math.Abs(x), Math.Abs(z)) != radius || Math.Abs(x) <= 1 && Math.Abs(z) <= 1) continue;

                    for (int y = 0; y < 4 && created.Count < requested; y++)
                    {
                        BlockPos pos = center.AddCopy(x, y, z);
                        Block existing = api.World.BlockAccessor.GetBlock(pos);
                        if (!existing.IsReplacableBy(constructionBlock)) continue;

                        api.World.BlockAccessor.SetBlock(constructionBlock.Id, pos);
                        if (api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityRealisticMasonry entity)
                        {
                            entity.PopulateForProfiling(random, random.NextDouble() < 0.7);
                            created.Add(pos.Copy());
                        }
                    }
                }
            }

            stopwatch.Stop();
            ProfileCellsByPlayer[player.PlayerUID] = created;
            return TextCommandResult.Success($"Created {created.Count:N0} masonry cells in {stopwatch.ElapsedMilliseconds:N0} ms.");
        }

        private static TextCommandResult ClearProfileCells(ICoreServerAPI api, TextCommandCallingArgs args)
        {
            IServerPlayer? player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("This command requires a player.");

            int removed = ClearTrackedProfileCells(api, player.PlayerUID);
            return TextCommandResult.Success($"Removed {removed:N0} profiling cells.");
        }

        private static TextCommandResult SpawnDuplicateProfileCells(ICoreServerAPI api, TextCommandCallingArgs args)
        {
            IServerPlayer? player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("This command requires a player.");

            int arrangementCount = GameMath.Clamp((int)args[0], 1, 1000);
            int copiesPerArrangement = GameMath.Clamp((int)args[1], 2, 10000);
            int requested = Math.Min(100000, arrangementCount * copiesPerArrangement);
            ClearTrackedProfileCells(api, player.PlayerUID);
            ResetClientProfile(api, player);

            Block? constructionBlock = api.World.GetBlock(new AssetLocation("brickbybrick:realisticmasonry"));
            if (constructionBlock == null) return TextCommandResult.Error("Realistic masonry block is unavailable.");

            BlockPos center = player.Entity.Pos.AsBlockPos;
            Random random = new(center.X ^ center.Z ^ arrangementCount ^ copiesPerArrangement);
            List<byte[]> arrangements = new(arrangementCount);
            List<BlockPos> created = new(requested);
            int horizontalRadius = (int)Math.Ceiling(Math.Sqrt(requested / 4d)) + 2;
            Stopwatch stopwatch = Stopwatch.StartNew();

            for (int radius = 2; radius <= horizontalRadius && created.Count < requested; radius++)
            for (int x = -radius; x <= radius && created.Count < requested; x++)
            for (int z = -radius; z <= radius && created.Count < requested; z++)
            {
                if (Math.Max(Math.Abs(x), Math.Abs(z)) != radius || Math.Abs(x) <= 1 && Math.Abs(z) <= 1) continue;

                for (int y = 0; y < 4 && created.Count < requested; y++)
                {
                    BlockPos pos = center.AddCopy(x, y, z);
                    Block existing = api.World.BlockAccessor.GetBlock(pos);
                    if (!existing.IsReplacableBy(constructionBlock)) continue;

                    api.World.BlockAccessor.SetBlock(constructionBlock.Id, pos);
                    if (api.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityRealisticMasonry entity) continue;

                    int arrangementIndex = created.Count / copiesPerArrangement;
                    if (arrangementIndex >= arrangements.Count)
                    {
                        entity.PopulateDuplicatePrototypeForProfiling(random, arrangementIndex);
                        arrangements.Add(MasonryStateCodec.Encode(entity.State));
                    }
                    else
                    {
                        entity.RestorePackedState(arrangements[arrangementIndex]);
                        entity.ForceFreezeForProfiling();
                    }

                    created.Add(pos.Copy());
                }
            }

            stopwatch.Stop();
            ProfileCellsByPlayer[player.PlayerUID] = created;
            AppendProfileLog(api, "DETERMINISTIC DUPLICATE SPAWN", $"Arrangements: {arrangements.Count:N0}; copies each: {copiesPerArrangement:N0}; cells: {created.Count:N0}; generation: {stopwatch.Elapsed.TotalMilliseconds:N1} ms.");
            api.Event.RegisterCallback(_ => RequestClientProfile(api, player), 5000);
            return TextCommandResult.Success($"Created {created.Count:N0} cells from {arrangements.Count:N0} deterministic arrangements in {stopwatch.ElapsedMilliseconds:N0} ms.");
        }

        private static TextCommandResult SpawnGeometryCorpus(ICoreServerAPI api, TextCommandCallingArgs args, bool exhaustive)
        {
            IServerPlayer? player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("This command requires a player.");

            int requested = exhaustive ? ushort.MaxValue : 2048;
            ClearTrackedProfileCells(api, player.PlayerUID);
            ResetClientProfile(api, player);
            Block? constructionBlock = api.World.GetBlock(new AssetLocation("brickbybrick:realisticmasonry"));
            if (constructionBlock == null) return TextCommandResult.Error("Realistic masonry block is unavailable.");

            BlockPos center = player.Entity.Pos.AsBlockPos;
            List<BlockPos> created = new(requested);
            int horizontalRadius = (int)Math.Ceiling(Math.Sqrt(requested / 4d)) + 2;
            ProfileCellsByPlayer[player.PlayerUID] = created;
            string corpus = exhaustive ? "exhaustive single-layer" : "quick deterministic 3D";
            Stopwatch stopwatch = Stopwatch.StartNew();
            IEnumerator<BlockPos> candidates = EnumerateProfilePositions(center, horizontalRadius).GetEnumerator();

            void GenerateBatch(float _)
            {
                int attempted = 0;
                while (created.Count < requested && attempted++ < ProfileBatchSize && candidates.MoveNext())
                {
                    BlockPos pos = candidates.Current;
                    if (!api.World.BlockAccessor.GetBlock(pos).IsReplacableBy(constructionBlock)) continue;
                    api.World.BlockAccessor.SetBlock(constructionBlock.Id, pos);
                    if (api.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityRealisticMasonry entity) continue;
                    entity.PopulateGeometryCorpusForProfiling(created.Count, exhaustive);
                    created.Add(pos.Copy());
                }

                if (created.Count < requested && attempted >= ProfileBatchSize)
                {
                    api.Event.RegisterCallback(GenerateBatch, 1);
                    return;
                }

                candidates.Dispose();
                stopwatch.Stop();
                AppendProfileLog(api, "GEOMETRY CORPUS SPAWN", $"Corpus: {corpus}; cells: {created.Count:N0}; batched generation: {stopwatch.Elapsed.TotalMilliseconds:N1} ms.");
                api.Event.RegisterCallback(_ => RequestClientProfile(api, player), exhaustive ? 30000 : 10000);
                player.SendMessage(GlobalConstants.GeneralChatGroup, $"Created the {corpus} corpus with {created.Count:N0} cells in {stopwatch.ElapsedMilliseconds:N0} ms.", EnumChatType.Notification);
            }

            api.Event.RegisterCallback(GenerateBatch, 1);
            return TextCommandResult.Success($"Started batched generation of the {corpus} corpus ({requested:N0} cells, {ProfileBatchSize:N0} attempts per tick).");
        }

        // Produces the same compact four-layer rings used by profiling while
        // allowing large corpora to pause cleanly between server ticks.
        private static IEnumerable<BlockPos> EnumerateProfilePositions(BlockPos center, int horizontalRadius)
        {
            for (int radius = 2; radius <= horizontalRadius; radius++)
            for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
            {
                if (Math.Max(Math.Abs(x), Math.Abs(z)) != radius || Math.Abs(x) <= 1 && Math.Abs(z) <= 1) continue;
                for (int y = 0; y < 4; y++) yield return center.AddCopy(x, y, z);
            }
        }

        private static TextCommandResult SpawnRealisticBaseCorpus(ICoreServerAPI api, TextCommandCallingArgs args)
        {
            IServerPlayer? player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("This command requires a player.");

            int requested = GameMath.Clamp((int)args[0], 1, 10000);
            ClearTrackedProfileCells(api, player.PlayerUID);
            ResetClientProfile(api, player);
            Block? constructionBlock = api.World.GetBlock(new AssetLocation("brickbybrick:realisticmasonry"));
            if (constructionBlock == null) return TextCommandResult.Error("Realistic masonry block is unavailable.");

            BlockPos center = player.Entity.Pos.AsBlockPos;
            Random random = new(center.X ^ center.Z ^ requested ^ 486187739);
            List<BlockPos> candidates = BuildRealisticBasePositions(center, requested, random);
            List<BlockPos> created = new(requested);
            ProfileCellsByPlayer[player.PlayerUID] = created;
            Stopwatch stopwatch = Stopwatch.StartNew();
            int nextCandidate = 0;

            void GenerateBatch(float _)
            {
                int attempted = 0;
                while (created.Count < requested
                    && nextCandidate < candidates.Count
                    && attempted++ < ProfileBatchSize)
                {
                    BlockPos pos = candidates[nextCandidate++];
                    if (!api.World.BlockAccessor.GetBlock(pos).IsReplacableBy(constructionBlock)) continue;
                    api.World.BlockAccessor.SetBlock(constructionBlock.Id, pos);
                    if (api.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityRealisticMasonry entity) continue;
                    entity.PopulateRealisticBaseCellForProfiling(random, created.Count);
                    created.Add(pos.Copy());
                }

                if (created.Count < requested && nextCandidate < candidates.Count)
                {
                    api.Event.RegisterCallback(GenerateBatch, 1);
                    return;
                }

                stopwatch.Stop();
                AppendProfileLog(api, "REALISTIC BASE CORPUS SPAWN", $"Requested: {requested:N0}; cells: {created.Count:N0}; candidate footprint: {candidates.Count:N0}; batched generation: {stopwatch.Elapsed.TotalMilliseconds:N1} ms.");
                api.Event.RegisterCallback(_ => RequestClientProfile(api, player), 10000);
                player.SendMessage(GlobalConstants.GeneralChatGroup, $"Created {created.Count:N0} realistic-base cells in {stopwatch.ElapsedMilliseconds:N0} ms.", EnumChatType.Notification);
            }

            api.Event.RegisterCallback(GenerateBatch, 1);
            return TextCommandResult.Success($"Started a batched realistic-base corpus of {requested:N0} cells.");
        }

        private static TextCommandResult SpawnAngledCorpus(ICoreServerAPI api, TextCommandCallingArgs args, bool mixed)
        {
            IServerPlayer? player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("This command requires a player.");

            int requested = GameMath.Clamp((int)args[0], 1, 10000);
            ClearTrackedProfileCells(api, player.PlayerUID);
            ResetClientProfile(api, player);
            Block? constructionBlock = api.World.GetBlock(new AssetLocation("brickbybrick:realisticmasonry"));
            if (constructionBlock == null) return TextCommandResult.Error("Realistic masonry block is unavailable.");

            BlockPos center = player.Entity.Pos.AsBlockPos;
            List<BlockPos> created = new(requested);
            IEnumerator<BlockPos> candidates = EnumerateProfilePositions(center, (int)Math.Ceiling(Math.Sqrt(requested / 4d)) + 2).GetEnumerator();
            ProfileCellsByPlayer[player.PlayerUID] = created;
            Stopwatch stopwatch = Stopwatch.StartNew();

            void GenerateBatch(float _)
            {
                int attempted = 0;
                while (created.Count < requested && attempted++ < ProfileBatchSize && candidates.MoveNext())
                {
                    BlockPos pos = candidates.Current;
                    if (!api.World.BlockAccessor.GetBlock(pos).IsReplacableBy(constructionBlock)) continue;
                    api.World.BlockAccessor.SetBlock(constructionBlock.Id, pos);
                    if (api.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityRealisticMasonry entity) continue;
                    entity.PopulateAngledCorpusForProfiling(created.Count, mixed);
                    created.Add(pos.Copy());
                }

                if (created.Count < requested && attempted >= ProfileBatchSize)
                {
                    api.Event.RegisterCallback(GenerateBatch, 1);
                    return;
                }

                candidates.Dispose();
                stopwatch.Stop();
                string corpus = mixed ? "mixed straight/diagonal" : "diagonal-only";
                AppendProfileLog(api, "ANGLED CORPUS SPAWN", $"Corpus: {corpus}; cells: {created.Count:N0}; generation: {stopwatch.Elapsed.TotalMilliseconds:N1} ms.");
                api.Event.RegisterCallback(_ => RequestClientProfile(api, player), 10000);
                player.SendMessage(GlobalConstants.GeneralChatGroup, $"Created {created.Count:N0} {corpus} cells in {stopwatch.ElapsedMilliseconds:N0} ms.", EnumChatType.Notification);
            }

            api.Event.RegisterCallback(GenerateBatch, 1);
            return TextCommandResult.Success($"Started a batched {(mixed ? "mixed-angle" : "diagonal-only")} corpus of {requested:N0} cells.");
        }

        // Creates contiguous base-wall runs that cross block and chunk
        // boundaries. This complements the scattered stress corpora with the
        // wall shapes players are most likely to leave loaded for long spans.
        private static TextCommandResult SpawnWallCorpus(ICoreServerAPI api, TextCommandCallingArgs args)
        {
            IServerPlayer? player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("This command requires a player.");

            int length = GameMath.Clamp((int)args[0], 8, 512);
            int height = GameMath.Clamp((int)args[1], 1, 16);
            return StartWallCorpus(api, player.PlayerUID, player.Entity.Pos.AsBlockPos, length, height, false, false, player);
        }

        // Runs from the dedicated-server console only. The isolated runner
        // starts the server with a separate data path, so this never touches
        // a player's ordinary world or profile cells.
        private static TextCommandResult RunServerWallProfile(ICoreServerAPI api, TextCommandCallingArgs args, bool compactStatic = false)
        {
            int length = GameMath.Clamp((int)args[0], 8, 512);
            int height = GameMath.Clamp((int)args[1], 1, 16);
            BlockPos spawn = api.World.DefaultSpawnPosition.AsBlockPos;
            int profileY = Math.Max(4, api.World.BlockAccessor.MapSizeY - height - 8);
            return StartWallCorpus(api, ServerProfileTracker, new BlockPos(spawn.X, profileY, spawn.Z), length, height, true, compactStatic, null);
        }

        private static TextCommandResult ClearServerWallProfile(ICoreServerAPI api)
        {
            int removed = ClearTrackedProfileCells(api, ServerProfileTracker);
            AppendProfileLog(api, "AUTOMATED SERVER WALL PROFILE CLEAR", $"Removed: {removed:N0} tracked wall cells.");
            return TextCommandResult.Success($"Removed {removed:N0} automated server wall-profile cells.");
        }

        // Runs both live and compacted wall phases from a server console and
        // removes each corpus after its settled report is written.
        private static TextCommandResult RunServerProfileMatrix(ICoreServerAPI api, TextCommandCallingArgs args)
        {
            if (!ActiveBenchmarks.Add(ServerMatrixTracker)) return TextCommandResult.Error("A server profiling matrix is already running.");

            int length = GameMath.Clamp((int)args[0], 8, 512);
            int height = GameMath.Clamp((int)args[1], 1, 16);
            BlockPos spawn = api.World.DefaultSpawnPosition.AsBlockPos;
            int profileY = Math.Max(4, api.World.BlockAccessor.MapSizeY - height - 8);
            BlockPos origin = new(spawn.X, profileY, spawn.Z);
            AppendProfileLog(api, "AUTOMATED SERVER MATRIX START", $"Length: {length:N0}; height: {height:N0}; phases: live, compacted; cleanup: enabled.");

            RunPhase(false);
            return TextCommandResult.Success($"Started unattended server masonry matrix with {length:N0}-cell runs at {height:N0} blocks high.");

            void RunPhase(bool compactStatic)
            {
                string tracker = compactStatic ? ServerMatrixStaticTracker : ServerMatrixLiveTracker;
                string phase = compactStatic ? "COMPACTED" : "LIVE";
                AppendProfileLog(api, $"AUTOMATED SERVER MATRIX {phase} START", "Client capture marker emitted. GPU residency must be collected by an external GPU telemetry tool.");
                BroadcastProfileCaptureMarker(api);
                StartWallCorpus(api, tracker, origin, length, height, true, compactStatic, null, success =>
                {
                    int removed = ClearTrackedProfileCells(api, tracker);
                    AppendProfileLog(api, $"AUTOMATED SERVER MATRIX {phase} CLEANUP", $"Success: {success}; removed tracked cells: {removed:N0}.");
                    if (!success)
                    {
                        ActiveBenchmarks.Remove(ServerMatrixTracker);
                        AppendProfileLog(api, "AUTOMATED SERVER MATRIX FAILED", $"Phase: {phase}; see the preceding server wall profile entry.");
                        return;
                    }

                    if (!compactStatic)
                    {
                        api.Event.RegisterCallback(_ => RunPhase(true), 250);
                        return;
                    }

                    ActiveBenchmarks.Remove(ServerMatrixTracker);
                    BroadcastProfileCaptureMarker(api);
                    AppendProfileLog(api, "AUTOMATED SERVER MATRIX COMPLETE", BuildMasonryRuntimeProfile());
                });
            }
        }

        private static TextCommandResult ReportMasonryRuntime(ICoreServerAPI api)
        {
            string report = BuildMasonryRuntimeProfile();
            AppendProfileLog(api, "MASONRY RUNTIME PROFILE", report);
            api.Logger.Notification(report);
            return TextCommandResult.Success($"Masonry runtime profile written to {GetProfileLogPath()}.");
        }

        private static TextCommandResult ResetMasonryRuntimeProfile(ICoreServerAPI api)
        {
            MasonryFreezeScheduler.ResetProfile();
            MasonryFrozenMeshCache.ResetProfile();
            MasonryTransformedMeshCache.ResetProfile();
            FrozenMasonryChunkStore.ResetProfile();
            BlockStaticMasonry.ResetProfileCounters();
            AppendProfileLog(api, "MASONRY RUNTIME PROFILE RESET", "Scheduler, cache, static-mesh, and sidecar write counters reset without clearing cached meshes or world data.");
            return TextCommandResult.Success("Masonry runtime profiling counters reset.");
        }

        private static string BuildMasonryRuntimeProfile()
        {
            return $"{MasonryFreezeScheduler.GetProfile()}{Environment.NewLine}"
                + $"{MasonryFrozenMeshCache.GetProfile()}{Environment.NewLine}"
                + $"{MasonryTransformedMeshCache.GetProfile()}{Environment.NewLine}"
                + $"{BlockStaticMasonry.GetProfile()}{Environment.NewLine}"
                + $"{BlockStaticMasonry.GetCacheProfile()}{Environment.NewLine}"
                + $"{FrozenMasonryChunkStore.GetProfile()}{Environment.NewLine}"
                + "GPU residency: no verified Vintage Story client API was found for VRAM counters. Use the emitted capture markers with external GPU telemetry for direct residency and frame-time evidence.";
        }

        private static TextCommandResult StartWallCorpus(
            ICoreServerAPI api,
            string tracker,
            BlockPos center,
            int length,
            int height,
            bool automated,
            bool compactStatic,
            IServerPlayer? player,
            Action<bool>? completed = null)
        {
            if (!ActiveBenchmarks.Add(tracker)) return TextCommandResult.Error("A wall profiling run is already settling.");

            ClearTrackedProfileCells(api, tracker);
            if (player != null) ResetClientProfile(api, player);
            Block? constructionBlock = api.World.GetBlock(new AssetLocation("brickbybrick:realisticmasonry"));
            if (constructionBlock == null)
            {
                ActiveBenchmarks.Remove(tracker);
                completed?.Invoke(false);
                return TextCommandResult.Error("Realistic masonry block is unavailable.");
            }

            List<(BlockPos Position, bool Diagonal)> candidates = new(length * height * 3);
            for (int section = 0; section < length; section++)
            for (int level = 0; level < height; level++)
            {
                candidates.Add((center.AddCopy(8 + section, level, 8), false));
                candidates.Add((center.AddCopy(8 + section, level, 24 + section), true));
                candidates.Add((center.AddCopy(8 + section, level, length + 48), section % 3 != 0));
            }

            List<BlockPos> created = new(candidates.Count);
            ProfileCellsByPlayer[tracker] = created;
            ServerRuntimeSnapshot runtimeBefore = CaptureServerRuntimeSnapshot();
            Stopwatch stopwatch = Stopwatch.StartNew();
            int nextCandidate = 0;
            int rejected = 0;
            string? firstRejectedBlock = null;

            void GenerateBatch(float _)
            {
                int attempted = 0;
                while (nextCandidate < candidates.Count && attempted++ < ProfileBatchSize)
                {
                    (BlockPos pos, bool diagonal) = candidates[nextCandidate++];
                    Block existing = api.World.BlockAccessor.GetBlock(pos);
                    if (!existing.IsReplacableBy(constructionBlock))
                    {
                        rejected++;
                        firstRejectedBlock ??= existing.Code?.ToString() ?? "unknown";
                        continue;
                    }
                    api.World.BlockAccessor.SetBlock(constructionBlock.Id, pos);
                    if (api.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityRealisticMasonry entity) continue;
                    entity.PopulateWallCellForProfiling(nextCandidate, diagonal);
                    created.Add(pos.Copy());
                }

                if (nextCandidate < candidates.Count)
                {
                    api.Event.RegisterCallback(GenerateBatch, 1);
                    return;
                }

                stopwatch.Stop();
                string spawnReport = $"Length: {length:N0}; height: {height:N0}; cardinal, diagonal, and mixed wall cells: {created.Count:N0}; rejected: {rejected:N0}; first rejected block: {firstRejectedBlock ?? "none"}; generation: {stopwatch.Elapsed.TotalMilliseconds:N1} ms.";
                AppendProfileLog(api, automated ? "AUTOMATED SERVER WALL PROFILE SPAWN" : "WALL CORPUS SPAWN", spawnReport);

                if (!automated)
                {
                    if (player != null)
                    {
                        api.Event.RegisterCallback(_ => RequestClientProfile(api, player), 10000);
                        player.SendMessage(GlobalConstants.GeneralChatGroup, $"Created {created.Count:N0} long-wall masonry cells in {stopwatch.ElapsedMilliseconds:N0} ms.", EnumChatType.Notification);
                    }
                    ActiveBenchmarks.Remove(tracker);
                    completed?.Invoke(true);
                    return;
                }

                void BeginAutomatedMeasurement(int compacted, int notCompactible, double compactionMilliseconds)
                {
                    string compactionReport = compactStatic
                        ? $"static compaction: {compacted:N0} converted, {notCompactible:N0} retained as arbitrary; {compactionMilliseconds:N1} ms."
                        : "static compaction: disabled.";
                    BuildTrackedStateReportBatched(api, created, baselineReport =>
                    {
                        AppendProfileLog(api, "AUTOMATED SERVER WALL PROFILE BASELINE", $"{baselineReport}{Environment.NewLine}{compactionReport}{Environment.NewLine}{CaptureServerRuntimeSnapshot().DescribeDelta(runtimeBefore)}");
                        MarkTrackedCellsDirtyInBatches(api, created, 0, () => api.Event.RegisterCallback(_ =>
                            BuildTrackedStateReportBatched(api, created, settledReport =>
                            {
                                AppendProfileLog(
                                    api,
                                    "AUTOMATED SERVER WALL PROFILE COMPLETE",
                                    $"{settledReport}{Environment.NewLine}{compactionReport}{Environment.NewLine}{CaptureServerRuntimeSnapshot().DescribeDelta(runtimeBefore)}{Environment.NewLine}Client-only metrics not measured: draw calls, vertices submitted, VRAM, and frame time.");
                                ActiveBenchmarks.Remove(tracker);
                                completed?.Invoke(true);
                            }), 5000));
                    });
                }

                if (compactStatic)
                {
                    CompactTrackedProfileCellsInBatches(api, created, 0, 0, 0, Stopwatch.StartNew(), BeginAutomatedMeasurement);
                }
                else
                {
                    BeginAutomatedMeasurement(0, 0, 0);
                }
            }

            if (automated)
            {
                HashSet<(int X, int Z)> chunkColumns = candidates
                    .Select(candidate => (candidate.Position.X / api.WorldManager.ChunkSize, candidate.Position.Z / api.WorldManager.ChunkSize))
                    .ToHashSet();
                foreach ((int chunkX, int chunkZ) in chunkColumns)
                {
                    api.WorldManager.LoadChunkColumn(chunkX, chunkZ, true);
                }

                int loadAttempts = 0;
                void WaitForChunks(float _)
                {
                    bool loaded = chunkColumns.All(column => api.WorldManager.GetChunk(new BlockPos(
                        column.X * api.WorldManager.ChunkSize,
                        center.Y,
                        column.Z * api.WorldManager.ChunkSize)) != null);
                    if (loaded)
                    {
                        api.Event.RegisterCallback(GenerateBatch, 1);
                        return;
                    }

                    if (++loadAttempts >= 100)
                    {
                        AppendProfileLog(api, "AUTOMATED SERVER WALL PROFILE LOAD FAILURE", $"Timed out loading {chunkColumns.Count:N0} chunk columns near {center}.");
                        ActiveBenchmarks.Remove(tracker);
                        completed?.Invoke(false);
                        return;
                    }

                    api.Event.RegisterCallback(WaitForChunks, 100);
                }

                api.Event.RegisterCallback(WaitForChunks, 100);
            }
            else
            {
                api.Event.RegisterCallback(GenerateBatch, 1);
            }
            return TextCommandResult.Success(automated
                ? $"Started isolated server wall profile with {length:N0}-cell runs at {height:N0} blocks high."
                : $"Started a wall corpus with {length:N0}-cell runs at {height:N0} blocks high.");
        }

        // Converts only canonical frozen shapes to entityless static blocks.
        // Diagonal and otherwise arbitrary cells intentionally remain live.
        private static void CompactTrackedProfileCellsInBatches(
            ICoreServerAPI api,
            List<BlockPos> positions,
            int startIndex,
            int compacted,
            int notCompactible,
            Stopwatch stopwatch,
            Action<int, int, double> completed)
        {
            int endIndex = Math.Min(startIndex + ProfileBatchSize, positions.Count);
            for (int index = startIndex; index < endIndex; index++)
            {
                if (TryCompactProfileCell(api, positions[index])) compacted++;
                else notCompactible++;
            }

            if (endIndex < positions.Count)
            {
                api.Event.RegisterCallback(_ => CompactTrackedProfileCellsInBatches(api, positions, endIndex, compacted, notCompactible, stopwatch, completed), 1);
                return;
            }

            stopwatch.Stop();
            completed(compacted, notCompactible, stopwatch.Elapsed.TotalMilliseconds);
        }

        private static bool TryCompactProfileCell(ICoreServerAPI api, BlockPos pos)
        {
            if (api.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityRealisticMasonry entity
                || !entity.State.Frozen
                || !BlockStaticMasonry.TryGetBlockCode(entity.State.FrozenShape, out AssetLocation staticCode)) return false;

            Block? staticBlock = api.World.GetBlock(staticCode);
            if (staticBlock == null || staticBlock.Id == 0) return false;

            byte[] packed = entity.GetPackedStateForProfiling();
            FrozenMasonryChunkStore.Set(api.World.BlockAccessor, pos, packed);
            api.World.BlockAccessor.SetBlock(staticBlock.Id, pos);
            api.World.BlockAccessor.MarkBlockDirty(pos);
            if (api.World.AllOnlinePlayers.Length > 0) BroadcastStaticMasonryState(pos, packed, false);
            return true;
        }

        private static ServerRuntimeSnapshot CaptureServerRuntimeSnapshot()
        {
            using Process process = Process.GetCurrentProcess();
            return new ServerRuntimeSnapshot(
                GC.GetTotalMemory(false),
                process.WorkingSet64,
                process.PrivateMemorySize64,
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2));
        }

        private readonly struct ServerRuntimeSnapshot
        {
            private readonly long managedBytes;
            private readonly long workingSetBytes;
            private readonly long privateBytes;
            private readonly int generationZeroCollections;
            private readonly int generationOneCollections;
            private readonly int generationTwoCollections;

            internal ServerRuntimeSnapshot(long managedBytes, long workingSetBytes, long privateBytes, int generationZeroCollections, int generationOneCollections, int generationTwoCollections)
            {
                this.managedBytes = managedBytes;
                this.workingSetBytes = workingSetBytes;
                this.privateBytes = privateBytes;
                this.generationZeroCollections = generationZeroCollections;
                this.generationOneCollections = generationOneCollections;
                this.generationTwoCollections = generationTwoCollections;
            }

            internal string DescribeDelta(ServerRuntimeSnapshot baseline)
            {
                return $"server memory: managed {managedBytes / 1048576d:N1} MiB ({(managedBytes - baseline.managedBytes) / 1048576d:+0.0;-0.0;0.0}); "
                    + $"working set {workingSetBytes / 1048576d:N1} MiB ({(workingSetBytes - baseline.workingSetBytes) / 1048576d:+0.0;-0.0;0.0}); "
                    + $"private {privateBytes / 1048576d:N1} MiB ({(privateBytes - baseline.privateBytes) / 1048576d:+0.0;-0.0;0.0}); "
                    + $"GC +{generationZeroCollections - baseline.generationZeroCollections}/+{generationOneCollections - baseline.generationOneCollections}/+{generationTwoCollections - baseline.generationTwoCollections}.";
            }
        }

        // Builds separated room shells instead of solid profiling rings. The
        // result crosses chunks naturally and resembles walls, floors, and
        // corners that an established player base would keep loaded together.
        private static List<BlockPos> BuildRealisticBasePositions(BlockPos center, int requested, Random random)
        {
            List<BlockPos> positions = new(requested * 2);
            HashSet<(int X, int Y, int Z)> unique = new();
            int building = 0;
            while (positions.Count < requested * 2)
            {
                int width = random.Next(7, 16);
                int depth = random.Next(7, 16);
                int height = random.Next(3, 7);
                int column = building % 5;
                int row = building / 5;
                int originX = center.X + 6 + column * 22;
                int originZ = center.Z + 6 + row * 22;

                for (int x = 0; x < width; x++)
                for (int z = 0; z < depth; z++)
                {
                    Add(originX + x, center.Y, originZ + z);
                    if (x != 0 && x != width - 1 && z != 0 && z != depth - 1) continue;
                    for (int y = 1; y <= height; y++)
                    {
                        bool doorway = z == 0 && x is >= 2 and <= 3 && y <= 2;
                        bool window = y is 2 or 3 && ((x + z + building) % 5 == 0);
                        if (!doorway && !window) Add(originX + x, center.Y + y, originZ + z);
                    }
                }

                building++;
            }

            return positions;

            void Add(int x, int y, int z)
            {
                if (unique.Add((x, y, z))) positions.Add(new BlockPos(x, y, z));
            }
        }

        private static int ClearTrackedProfileCells(ICoreServerAPI api, string playerUid)
        {
            if (!ProfileCellsByPlayer.TryGetValue(playerUid, out List<BlockPos>? positions)) return 0;

            int removed = 0;
            foreach (BlockPos pos in positions)
            {
                Block block = api.World.BlockAccessor.GetBlock(pos);
                if (block.Code?.Path != "realisticmasonry" && block is not BlockStaticMasonry) continue;
                if (block is BlockStaticMasonry)
                {
                    FrozenMasonryChunkStore.Remove(api.World.BlockAccessor, pos, out _);
                    BroadcastStaticMasonryState(pos, Array.Empty<byte>(), true);
                }
                api.World.BlockAccessor.SetBlock(0, pos);
                api.World.BlockAccessor.MarkBlockDirty(pos);
                removed++;
            }

            ProfileCellsByPlayer.Remove(playerUid);
            return removed;
        }

        private static TextCommandResult ReportProfileCells(ICoreServerAPI api, TextCommandCallingArgs args)
        {
            IServerPlayer? player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("This command requires a player.");
            if (!ProfileCellsByPlayer.TryGetValue(player.PlayerUID, out List<BlockPos>? positions))
            {
                return TextCommandResult.Success("No profiling cells are tracked for this player.");
            }

            BuildTrackedStateReportBatched(api, positions, report =>
            {
                AppendProfileLog(api, "SERVER CELL REPORT", report);
                player.SendMessage(GlobalConstants.GeneralChatGroup, $"{report} Written to {GetProfileLogPath()}.", EnumChatType.Notification);
            });
            return TextCommandResult.Success($"Started a batched scan of {positions.Count:N0} profiling cells.");
        }

        private static TextCommandResult StartTrackedBenchmark(ICoreServerAPI api, TextCommandCallingArgs args)
        {
            IServerPlayer? player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("This command requires a player.");
            if (!ProfileCellsByPlayer.TryGetValue(player.PlayerUID, out List<BlockPos>? positions) || positions.Count == 0)
            {
                return TextCommandResult.Error("Generate cells first with /bbbprofile spawn <count>.");
            }
            if (!ActiveBenchmarks.Add(player.PlayerUID)) return TextCommandResult.Error("A tracked benchmark is already settling for this player.");

            ResetClientProfile(api, player);
            BuildTrackedStateReportBatched(api, positions, baselineReport =>
            {
                AppendProfileLog(api, "TRACKED BENCHMARK BASELINE", baselineReport);
                MarkTrackedCellsDirtyInBatches(api, positions, 0, () => api.Event.RegisterCallback(_ =>
                    BuildTrackedStateReportBatched(api, positions, report =>
                    {
                        AppendProfileLog(api, "TRACKED BENCHMARK SETTLED", report);
                        RequestClientProfile(api, player);
                        ActiveBenchmarks.Remove(player.PlayerUID);
                        player.SendMessage(GlobalConstants.GeneralChatGroup, $"Tracked benchmark complete. {report}", EnumChatType.Notification);
                    }), 5000));
            });
            return TextCommandResult.Success($"Started a deterministic rebuild benchmark for {positions.Count:N0} tracked cells.");
        }

        private static void MarkTrackedCellsDirtyInBatches(
            ICoreServerAPI api,
            List<BlockPos> positions,
            int startIndex,
            Action completed)
        {
            int endIndex = Math.Min(startIndex + ProfileBatchSize, positions.Count);
            for (int index = startIndex; index < endIndex; index++)
            {
                api.World.BlockAccessor.MarkBlockDirty(positions[index]);
            }

            if (endIndex < positions.Count)
            {
                api.Event.RegisterCallback(_ => MarkTrackedCellsDirtyInBatches(api, positions, endIndex, completed), 1);
                return;
            }

            completed();
        }

        private static TextCommandResult RunRandomProfileActions(ICoreServerAPI api, TextCommandCallingArgs args)
        {
            IServerPlayer? player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("This command requires a player.");
            if (!ProfileCellsByPlayer.TryGetValue(player.PlayerUID, out List<BlockPos>? positions) || positions.Count == 0)
            {
                return TextCommandResult.Error("Generate cells first with /bbbprofile spawn <count>.");
            }
            if (ActiveBenchmarks.Contains(player.PlayerUID)) return TextCommandResult.Error("Wait for the tracked benchmark to finish before running random stress.");

            int requested = GameMath.Clamp((int)args[0], 1, 100000);
            Random random = new(player.Entity.Pos.AsBlockPos.X ^ Environment.TickCount ^ requested);
            Block? liveBlock = api.World.GetBlock(new AssetLocation("brickbybrick:realisticmasonry"));
            if (liveBlock == null) return TextCommandResult.Error("Realistic masonry block is unavailable.");

            ResetClientProfile(api, player);
            Stopwatch stopwatch = Stopwatch.StartNew();
            int rebuilt = 0;
            int reopened = 0;
            int regenerated = 0;
            for (int action = 0; action < requested; action++)
            {
                BlockPos pos = positions[random.Next(positions.Count)];
                Block block = api.World.BlockAccessor.GetBlock(pos);
                int operation = random.Next(3);
                if (operation == 0)
                {
                    api.World.BlockAccessor.MarkBlockDirty(pos);
                    rebuilt++;
                }
                else if (block is BlockStaticMasonry && BlockStaticMasonry.TryRestoreEntity(api.World, pos, out _))
                {
                    reopened++;
                }
                else
                {
                    if (block.Code?.Path != "realisticmasonry") api.World.BlockAccessor.SetBlock(liveBlock.Id, pos);
                    if (api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityRealisticMasonry entity)
                    {
                        entity.PopulateForProfiling(random, false);
                        if (random.NextDouble() < 0.7) entity.ForceFreezeForProfiling();
                        regenerated++;
                    }
                }
            }

            stopwatch.Stop();
            string report = $"Actions: {requested:N0}; dirty rebuilds: {rebuilt:N0}; reopened: {reopened:N0}; "
                + $"regenerated/frozen: {regenerated:N0}; server action time: {stopwatch.Elapsed.TotalMilliseconds:N2} ms; "
                + $"{BuildTrackedStateReport(api, positions)}";
            AppendProfileLog(api, "RANDOM TRACKED STRESS", report);
            api.Event.RegisterCallback(_ => RequestClientProfile(api, player), 5000);
            return TextCommandResult.Success($"Random tracked-cell stress complete. {report}");
        }

        private static string BuildTrackedStateReport(ICoreServerAPI api, List<BlockPos> positions)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            int liveEntities = 0;
            int frozenEntities = 0;
            int staticCells = 0;
            int arbitraryCells = 0;
            int units = 0;
            int orphanedSidecars = 0;
            int missingSidecars = 0;
            int corruptSidecars = 0;
            long residentSidecarBytes = 0;
            long livePackedBytes = 0;

            foreach (BlockPos pos in positions)
            {
                Block block = api.World.BlockAccessor.GetBlock(pos);
                bool hasSidecar = FrozenMasonryChunkStore.TryGet(api.World.BlockAccessor, pos, out byte[] packed);
                if (block is BlockStaticMasonry)
                {
                    staticCells++;
                    if (!hasSidecar) missingSidecars++;
                    else
                    {
                        residentSidecarBytes += packed.Length;
                        try { units += MasonryStateCodec.Decode(packed).Units.Count; }
                        catch { corruptSidecars++; }
                    }
                }
                else if (hasSidecar)
                {
                    orphanedSidecars++;
                    residentSidecarBytes += packed.Length;
                }

                if (api.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityRealisticMasonry entity) continue;
                liveEntities++;
                units += entity.State.Units.Count;
                livePackedBytes += MasonryStateCodec.Encode(entity.State).Length;
                if (entity.State.Frozen)
                {
                    frozenEntities++;
                    if (entity.State.FrozenShape == FrozenMasonryShape.Arbitrary) arbitraryCells++;
                }
            }

            stopwatch.Stop();
            return $"tracked: {positions.Count:N0}; live entities: {liveEntities:N0}; frozen entities: {frozenEntities:N0}; "
                + $"static cells: {staticCells:N0}; arbitrary frozen: {arbitraryCells:N0}; units: {units:N0}; "
                + $"resident sidecar: {residentSidecarBytes:N0} bytes; live packed: {livePackedBytes:N0} bytes; "
                + $"missing/orphaned/corrupt sidecars: {missingSidecars:N0}/{orphanedSidecars:N0}/{corruptSidecars:N0}; "
                + $"scan: {stopwatch.Elapsed.TotalMilliseconds:N2} ms.";
        }

        private static void BuildTrackedStateReportBatched(
            ICoreServerAPI api,
            List<BlockPos> positions,
            Action<string> completed)
        {
            TrackedStateScan scan = new(api, positions);

            void ScanBatch(float _)
            {
                if (scan.Process(ProfileBatchSize))
                {
                    completed(scan.BuildReport());
                    return;
                }

                api.Event.RegisterCallback(ScanBatch, 1);
            }

            api.Event.RegisterCallback(ScanBatch, 1);
        }

        private sealed class TrackedStateScan
        {
            private readonly ICoreServerAPI api;
            private readonly List<BlockPos> positions;
            private readonly Stopwatch stopwatch = Stopwatch.StartNew();
            private int nextIndex;
            private int liveEntities;
            private int frozenEntities;
            private int staticCells;
            private int arbitraryCells;
            private int units;
            private int orphanedSidecars;
            private int missingSidecars;
            private int corruptSidecars;
            private long residentSidecarBytes;
            private long livePackedBytes;

            internal TrackedStateScan(ICoreServerAPI api, List<BlockPos> positions)
            {
                this.api = api;
                this.positions = positions;
            }

            internal bool Process(int count)
            {
                int endIndex = Math.Min(nextIndex + count, positions.Count);
                for (; nextIndex < endIndex; nextIndex++) Process(positions[nextIndex]);
                return nextIndex >= positions.Count;
            }

            internal string BuildReport()
            {
                stopwatch.Stop();
                return $"tracked: {positions.Count:N0}; live entities: {liveEntities:N0}; frozen entities: {frozenEntities:N0}; "
                    + $"static cells: {staticCells:N0}; arbitrary frozen: {arbitraryCells:N0}; units: {units:N0}; "
                    + $"resident sidecar: {residentSidecarBytes:N0} bytes; live packed: {livePackedBytes:N0} bytes; "
                    + $"missing/orphaned/corrupt sidecars: {missingSidecars:N0}/{orphanedSidecars:N0}/{corruptSidecars:N0}; "
                    + $"batched scan: {stopwatch.Elapsed.TotalMilliseconds:N2} ms.";
            }

            private void Process(BlockPos pos)
            {
                Block block = api.World.BlockAccessor.GetBlock(pos);
                bool hasSidecar = FrozenMasonryChunkStore.TryGet(api.World.BlockAccessor, pos, out byte[] packed);
                if (block is BlockStaticMasonry)
                {
                    staticCells++;
                    if (!hasSidecar) missingSidecars++;
                    else
                    {
                        residentSidecarBytes += packed.Length;
                        try { units += MasonryStateCodec.ReadSummary(packed).Units; }
                        catch { corruptSidecars++; }
                    }
                }
                else if (hasSidecar)
                {
                    orphanedSidecars++;
                    residentSidecarBytes += packed.Length;
                }

                if (api.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityRealisticMasonry entity) return;
                liveEntities++;
                byte[] livePacked = entity.GetPackedStateForProfiling();
                (bool frozen, FrozenMasonryShape shape, int unitCount) = MasonryStateCodec.ReadSummary(livePacked);
                units += unitCount;
                livePackedBytes += livePacked.Length;
                if (!frozen) return;
                frozenEntities++;
                if (shape == FrozenMasonryShape.Arbitrary) arbitraryCells++;
            }
        }

        private static void ResetClientProfile(ICoreServerAPI api, IServerPlayer player)
        {
            if (api.Server.IsDedicated) api.Network.GetChannel("brickbybrick-realistic").SendPacket(new RealisticControlPacket { Code = ProfileResetPacket }, player);
            else BlockEntityRealisticMasonry.ResetTessellationProfile();
        }

        private static void RequestClientProfile(ICoreServerAPI api, IServerPlayer player)
        {
            if (api.Server.IsDedicated) api.Network.GetChannel("brickbybrick-realistic").SendPacket(new RealisticControlPacket { Code = ProfileReportPacket }, player);
            else AppendProfileLog(api, "INTEGRATED CLIENT PROFILE", BlockEntityRealisticMasonry.GetTessellationProfile());
        }

        private static void BroadcastProfileCaptureMarker(ICoreServerAPI api)
        {
            if (api.World.AllOnlinePlayers.Length == 0) return;
            api.Network.GetChannel("brickbybrick-realistic").BroadcastPacket(new RealisticControlPacket { Code = ProfileCaptureMarkerPacket });
        }

        public override void AssetsFinalize(ICoreAPI api)
        {
            base.AssetsFinalize(api);

            if (!Config.Construction.DisableVanillaBlockRecipes) return;

            foreach (GridRecipe recipe in api.World.GridRecipes)
            {
                if (IsDisabledVanillaBlockRecipe(recipe))
                {
                    recipe.Enabled = false;
                }
            }
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            BlockEntityRealisticMasonry.ResetOptimizedMeshRuntimeGuard();
            clientApi = api;
            HotKey? toolModeHotKey = api.Input.GetHotKeyByCode("toolmodeselect");
            vanillaToolModeHandler = toolModeHotKey?.Handler;
            api.Input.SetHotKeyHandler("toolmodeselect", HandleToolModeHotKey);
            realisticClientChannel = api.Network.GetChannel("brickbybrick-realistic");
            realisticClientChannel.SetMessageHandler<RealisticControlPacket>(packet => OnProfileControlPacket(api, packet.Code));
            realisticClientChannel.SetMessageHandler<StaticMasonryStatePacket>(packet => OnStaticMasonryStatePacket(api, packet));
            api.Event.RegisterGameTickListener(_ => FrozenMasonryChunkStore.FlushDue(), 100);
            api.Event.MouseWheelMove += OnRealisticPlacementMouseWheel;
            RegisterClientProfilingCommands(api);
            survivalHandbook = api.ModLoader.GetModSystem<ModSystemSurvivalHandbook>();
            if (survivalHandbook != null)
            {
                survivalHandbook.OnInitCustomPages += MoveMasonryGuideAfterVanillaGuides;
            }
        }

        private bool HandleToolModeHotKey(KeyCombination combination)
        {
            if (!Config.IsRealisticConstructionEnabled())
            {
                return vanillaToolModeHandler?.Invoke(combination) ?? false;
            }

            IClientPlayer? player = clientApi?.World?.Player;
            if (player != null && TryGetRealisticPlacementKind(player, out MasonryUnitKind heldKind))
            {
                int orientation = (int)ItemTrowel.ResolveRealisticOrientation(player);
                int variant = ItemTrowel.ResolveRealisticVariant(player);
                CycleRealisticVariantState(heldKind, 1, ref orientation, ref variant);
                ItemTrowel.SetRealisticPlacementState(player, orientation, variant);
                SendRealisticPlacementState(player);
            }

            // Realistic mode owns F globally. Never delegate to vanilla, even
            // when no item or placement material is selected.
            return true;
        }

        // Resend the active pose with the click. This keeps server placement
        // aligned with the client preview even when wheel and interaction
        // packets arrive on different game-channel schedules.
        internal void SendRealisticPlacementState(IPlayer player)
        {
            if (player?.Entity?.World?.Side != EnumAppSide.Client || realisticClientChannel == null) return;

            realisticClientChannel.SendPacket(new RealisticControlPacket
            {
                PlacementState = true,
                Orientation = (int)ItemTrowel.ResolveRealisticOrientation(player),
                Variant = ItemTrowel.ResolveRealisticVariant(player)
            });
        }

        internal static void BroadcastStaticMasonryState(BlockPos pos, byte[] state, bool remove)
        {
            if (serverApi == null) return;

            StaticMasonryStatePacket packet = new()
            {
                X = pos.X,
                Y = pos.InternalY,
                Z = pos.Z,
                State = state,
                Remove = remove
            };
            serverApi.Network.GetChannel("brickbybrick-realistic").BroadcastPacket(packet);
        }

        private static void OnStaticMasonryStatePacket(ICoreClientAPI api, StaticMasonryStatePacket packet)
        {
            BlockPos pos = new(packet.X, packet.Y, packet.Z);
            if (packet.Remove) FrozenMasonryChunkStore.Remove(api.World.BlockAccessor, pos, out _);
            else FrozenMasonryChunkStore.Set(api.World.BlockAccessor, pos, packet.State);
            api.World.BlockAccessor.MarkBlockDirty(pos);
        }

        private void OnProfileControlPacket(ICoreClientAPI api, int packet)
        {
            if (packet == ProfileResetPacket)
            {
                BlockEntityRealisticMasonry.ResetTessellationProfile();
                AppendProfileLog(api, "AUTOMATED EXERCISE CLIENT RESET", "Client counters reset by the server exercise.");
                realisticClientChannel?.SendPacket(ProfileResetAcknowledgementPacket);
                return;
            }

            if (packet == ProfileCaptureMarkerPacket)
            {
                string markerReport = $"UTC: {DateTime.UtcNow:O}; {BlockEntityRealisticMasonry.GetTessellationProfile()}{Environment.NewLine}"
                    + "GPU residency is not exposed by a verified Vintage Story API. Correlate this marker with external GPU telemetry for VRAM, draw calls, and frame time.";
                AppendProfileLog(api, "CLIENT GPU CAPTURE MARKER", markerReport);
                return;
            }

            if (packet != ProfileReportPacket) return;
            string report = BlockEntityRealisticMasonry.GetTessellationProfile();
            AppendProfileLog(api, "AUTOMATED EXERCISE CLIENT REPORT", report);
            realisticClientChannel?.SendPacket(ProfileReportAcknowledgementPacket);
            api.ShowChatMessage($"Automated masonry profile written to {GetProfileLogPath()}.");
        }

        private static void RegisterClientProfilingCommands(ICoreClientAPI api)
        {
            api.ChatCommands.Create("bbbmeshprofile")
                .WithDescription("Profiles Realistic masonry client tessellation.")
                .BeginSubCommand("reset")
                    .HandleWith(_ =>
                    {
                        BlockEntityRealisticMasonry.ResetTessellationProfile();
                        AppendProfileLog(api, "CLIENT MESH RESET", "Counters reset. Perform one ordinary block placement or break before reporting.");
                        return TextCommandResult.Success($"Masonry mesh profiling counters reset. Place or break one ordinary block, then run .bbbmeshprofile report. Log: {GetProfileLogPath()}.");
                    })
                .EndSubCommand()
                .BeginSubCommand("report")
                    .HandleWith(_ =>
                    {
                        string report = BlockEntityRealisticMasonry.GetTessellationProfile();
                        AppendProfileLog(api, "CLIENT MESH REPORT", report);
                        return TextCommandResult.Success($"{report} Written to {GetProfileLogPath()}.");
                    })
                .EndSubCommand()
                .BeginSubCommand("marker")
                    .HandleWith(_ =>
                    {
                        string report = $"UTC: {DateTime.UtcNow:O}; {BlockEntityRealisticMasonry.GetTessellationProfile()}{Environment.NewLine}"
                            + "GPU residency is not exposed by a verified Vintage Story API. Correlate this marker with external GPU telemetry for VRAM, draw calls, and frame time.";
                        AppendProfileLog(api, "CLIENT GPU CAPTURE MARKER", report);
                        return TextCommandResult.Success($"GPU capture marker written to {GetProfileLogPath()}.");
                    })
                .EndSubCommand()
                .BeginSubCommand("clearcache")
                    .HandleWith(_ =>
                    {
                        BlockEntityRealisticMasonry.ClearTransformedMeshCache();
                        AppendProfileLog(api, "CLIENT MESH CACHE CLEAR", "Shared transformed-mesh cache cleared.");
                        return TextCommandResult.Success("Shared masonry mesh cache cleared. Existing chunks will rebuild it as needed.");
                    })
                .EndSubCommand()
                .BeginSubCommand("optimized")
                    .BeginSubCommand("on")
                        .HandleWith(_ => SetClientOptimizedFrozenMeshes(api, true))
                    .EndSubCommand()
                    .BeginSubCommand("off")
                        .HandleWith(_ => SetClientOptimizedFrozenMeshes(api, false))
                    .EndSubCommand()
                .EndSubCommand()
                .BeginSubCommand("rejecttest")
                    .HandleWith(_ =>
                    {
                        BlockSelection? selection = api.World.Player.CurrentBlockSelection;
                        if (selection == null) return TextCommandResult.Error("Look at a frozen, mortar-free masonry cell first.");
                        if (api.World.BlockAccessor.GetBlockEntity(selection.Position) is not BlockEntityRealisticMasonry entity
                            || !entity.State.Frozen)
                        {
                            return TextCommandResult.Error("The selected block is not a frozen masonry cell.");
                        }
                        if (entity.State.Units.Any(unit => unit.MortaredPositions.Count > 0)
                            || entity.State.MortaredSideJoints.Count > 0)
                        {
                            return TextCommandResult.Error("Select a mortar-free cell so it reaches the optimized renderer before rejection.");
                        }

                        BlockEntityRealisticMasonry.RejectNextOptimizedMeshForProfiling();
                        api.World.BlockAccessor.MarkBlockDirty(selection.Position);
                        AppendProfileLog(api, "CLIENT FORCED REJECTION", $"Queued one optimized-mesh rejection at {selection.Position}.");
                        return TextCommandResult.Success("Queued one forced rejection. The selected arrangement should use component fallback and remain rejected after later dirty rebuilds.");
                    })
                .EndSubCommand();
        }

        // Switches the client renderer only. The server keeps its saved
        // construction state unchanged, allowing comparable A/B captures.
        private static TextCommandResult SetClientOptimizedFrozenMeshes(ICoreClientAPI api, bool enabled)
        {
            Config.Realism.EnableOptimizedFrozenMeshes = enabled;
            BlockEntityRealisticMasonry.ResetTessellationProfile();

            BlockSelection? selection = api.World.Player.CurrentBlockSelection;
            if (selection != null && api.World.BlockAccessor.GetBlockEntity(selection.Position) is BlockEntityRealisticMasonry entity)
            {
                entity.InvalidateFrozenMeshForProfiling();
                api.World.BlockAccessor.MarkBlockDirty(selection.Position);
                AppendProfileLog(api, "CLIENT OPTIMIZED MESH MODE", $"Enabled: {enabled}; invalidated selected cell: {selection.Position}.");
                return TextCommandResult.Success($"Optimized frozen meshes {(enabled ? "enabled" : "disabled")} for client profiling. The selected cell was invalidated and queued for retessellation.");
            }

            AppendProfileLog(api, "CLIENT OPTIMIZED MESH MODE", $"Enabled: {enabled}; no selected masonry cell to invalidate.");
            return TextCommandResult.Success($"Optimized frozen meshes {(enabled ? "enabled" : "disabled")} for future client tessellation. Select a frozen masonry cell and dirty it to include it in the next sample.");
        }

        private static string GetProfileLogPath()
        {
            return Path.Combine(GamePaths.Logs, "brickbybrick-profile.log");
        }

        private static void AppendProfileLog(ICoreAPI api, string heading, string report)
        {
            try
            {
                lock (ProfileLogSync)
                {
                    File.AppendAllText(
                        GetProfileLogPath(),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{profileSessionId}] {heading}{Environment.NewLine}{report}{Environment.NewLine}{Environment.NewLine}");
                }
            }
            catch (Exception exception)
            {
                api.Logger.Warning($"Could not write Brick by Brick profiling log: {exception.Message}");
            }
        }

        public override void Dispose()
        {
            ResetWorldScopedProfileState();
            FrozenMasonryChunkStore.FlushAll();
            MasonryFrozenMeshCache.Clear();
            BlockEntityRealisticMasonry.ClearTransformedMeshCache();
            BlockStaticMasonry.ClearCaches();
            if (clientApi != null)
            {
                clientApi.Event.MouseWheelMove -= OnRealisticPlacementMouseWheel;
                clientApi.Input.SetHotKeyHandler("toolmodeselect", vanillaToolModeHandler);
                clientApi = null;
                realisticClientChannel = null;
            }

            if (survivalHandbook != null)
            {
                survivalHandbook.OnInitCustomPages -= MoveMasonryGuideAfterVanillaGuides;
            }

            serverApi = null;

            base.Dispose();
        }

        // Modifier-wheel is scoped to realistic masonry materials. All other
        // wheel input remains available to the hotbar and other mods.
        private void OnRealisticPlacementMouseWheel(MouseWheelEventArgs args)
        {
            if (!Config.IsRealisticConstructionEnabled() || clientApi?.World?.Player == null) return;

            IClientPlayer player = clientApi.World.Player;
            var entity = player.Entity;
            if (entity == null) return;

            bool primaryCycle = entity.Controls?.Sneak == true;
            // Vintage Story exposes the generic secondary modifier here; the
            // help text and interaction code keep it behind our binding name.
            bool secondaryCycle = entity.Controls?.CtrlKey == true;
            if (!primaryCycle && !secondaryCycle) return;

            if (!TryGetRealisticPlacementKind(player, out MasonryUnitKind heldKind)) return;

            int orientation = (int)ItemTrowel.ResolveRealisticOrientation(player);
            int variant = ItemTrowel.ResolveRealisticVariant(player);
            int step = args.delta > 0 ? 1 : -1;
            CycleRealisticPlacementState(heldKind, primaryCycle, step, ref orientation, ref variant);

            ItemTrowel.SetRealisticPlacementState(player, orientation, variant);
            realisticClientChannel?.SendPacket(new RealisticControlPacket
            {
                PlacementState = true,
                Orientation = orientation,
                Variant = variant
            });
            args.SetHandled(true);
        }

        private static void CycleRealisticPlacementState(MasonryUnitKind heldKind, bool primaryCycle, int step, ref int orientation, ref int variant)
        {
            int[] cardinal = { 0, 1, 2, 3 };
            int[] diagonal = Config.Realism.EnableDiagonalPlacement ? new[] { 4, 5, 6, 7 } : cardinal;
            int[] all = Config.Realism.EnableDiagonalPlacement ? new[] { 0, 4, 1, 5, 2, 6, 3, 7 } : cardinal;

            switch (heldKind)
            {
                case MasonryUnitKind.WholeBrick:
                    variant = 0;
                    orientation = CycleIn(primaryCycle ? cardinal : diagonal, orientation, step);
                    break;

                case MasonryUnitKind.RammedEarth:
                case MasonryUnitKind.SmallRammedEarth:
                    variant = primaryCycle ? 0 : 1;
                    orientation = CycleIn(all, orientation, step);
                    break;

                case MasonryUnitKind.HalfBrick:
                    if (primaryCycle)
                    {
                        variant = 0;
                        orientation = CycleIn(all, orientation, step);
                    }
                    else
                    {
                        if (!Config.Realism.EnableDiagonalPlacement)
                        {
                            variant = 0;
                            orientation = CycleIn(cardinal, orientation, step);
                            break;
                        }

                        variant = GameMath.Mod(Math.Max(1, variant) - 1 + step, ItemTrowel.RealisticHalfBrickVariantCount - 1) + 1;
                        orientation = GameMath.Mod(variant - 1, 8);
                    }
                    break;

                default:
                    orientation = CycleIn(all, orientation, step);
                    variant = 0;
                    break;
            }
        }

        private static int CycleIn(int[] cycle, int current, int step)
        {
            int index = Array.IndexOf(cycle, GameMath.Mod(current, 8));
            if (index < 0) index = step > 0 ? -1 : 0;
            return cycle[GameMath.Mod(index + step, cycle.Length)];
        }

        private static void CycleRealisticVariantState(MasonryUnitKind heldKind, int step, ref int orientation, ref int variant)
        {
            switch (heldKind)
            {
                case MasonryUnitKind.RammedEarth:
                case MasonryUnitKind.SmallRammedEarth:
                    variant = GameMath.Mod(variant + step, 2);
                    break;

                case MasonryUnitKind.HalfBrick:
                    if (!Config.Realism.EnableDiagonalPlacement)
                    {
                        variant = 0;
                        break;
                    }

                    variant = GameMath.Mod(variant + step, ItemTrowel.RealisticHalfBrickVariantCount);
                    if (variant > 0) orientation = GameMath.Mod(variant - 1, 8);
                    break;

                default:
                    variant = 0;
                    break;
            }
        }

        private bool TryGetRealisticPlacementKind(IClientPlayer player, out MasonryUnitKind kind)
        {
            ItemSlot slot = player.InventoryManager.ActiveHotbarSlot;
            if (slot?.Itemstack?.Collectible is ItemTrowel
                && TryGetOffhandRealisticMaterial(player, out ItemStack? offhandStack))
            {
                return ItemTrowel.TryResolveRealisticKind(offhandStack, player, out kind);
            }

            kind = (MasonryUnitKind)(-1);
            return slot?.Itemstack != null && ItemTrowel.TryResolveRealisticKind(slot.Itemstack, player, out kind);
        }

        private static bool TryGetOffhandRealisticMaterial(IClientPlayer player, out ItemStack? stack)
        {
            stack = player.InventoryManager.OffhandHotbarSlot?.Itemstack;
            return stack != null;
        }

        private static void OnRealisticOrientationPacket(IPlayer fromPlayer, int packet)
        {
            int directionCount = Config.Realism.EnableDiagonalPlacement ? 8 : 4;
            int orientation = GameMath.Mod(packet & 0xFF, directionCount);
            int variantCount = Config.Realism.EnableDiagonalPlacement ? ItemTrowel.RealisticHalfBrickVariantCount : 1;
            int variant = GameMath.Mod(packet >> 8, variantCount);
            ItemTrowel.SetRealisticPlacementState(fromPlayer, orientation, variant);
        }

        private static void MoveMasonryGuideAfterVanillaGuides(List<GuiHandbookPage> pages)
        {
            int masonryGuideIndex = pages.FindIndex(page => page.PageCode == MasonryGuidePageCode);
            if (masonryGuideIndex < 0)
            {
                return;
            }

            // The handbook sorts text pages by full asset key, which places
            // brickbybrick before survival. Move our guide to the end instead.
            GuiHandbookPage masonryGuide = pages[masonryGuideIndex];
            pages.RemoveAt(masonryGuideIndex);
            pages.Add(masonryGuide);
        }

        private sealed class ProfileExerciseSession
        {
            internal ProfileExerciseSession(IServerPlayer player, BlockPos origin, int testBlockId)
            {
                Player = player;
                Origin = origin;
                TestBlockId = testBlockId;
                Random = new Random(origin.X ^ origin.Z ^ Environment.TickCount);
            }

            internal IServerPlayer Player { get; }
            internal BlockPos Origin { get; }
            internal int TestBlockId { get; }
            internal Random Random { get; }
            internal Dictionary<BlockPos, int> OriginalBlockIds { get; } = new();
            internal bool Active { get; set; } = true;
            internal bool ClientResetAcknowledged { get; set; }
            internal bool ClientReportReceived { get; set; }
            internal int MutationCount { get; private set; }
            internal int SkippedEdits { get; set; }
            internal double TotalMutationMilliseconds { get; private set; }
            internal double SlowestMutationMilliseconds { get; private set; }

            internal void RecordMutation(double milliseconds)
            {
                MutationCount++;
                TotalMutationMilliseconds += milliseconds;
                SlowestMutationMilliseconds = Math.Max(SlowestMutationMilliseconds, milliseconds);
            }
        }

        // Disables only vanilla recipes whose outputs belong to an enabled
        // material family. Modded recipes and unrelated decorative recipes stay intact.
        private static bool IsDisabledVanillaBlockRecipe(GridRecipe recipe)
        {
            if (recipe?.Name?.Domain != GlobalConstants.DefaultDomain) return false;

            string? outputPath = recipe.Output?.Code?.Path;
            if (string.IsNullOrEmpty(outputPath)) return false;

            if (Config.Materials.EnableBrickConstruction)
            {
                if (outputPath.StartsWith("brickcourse-", StringComparison.Ordinal)
                    || outputPath.StartsWith("brickslab", StringComparison.Ordinal)
                    || outputPath.StartsWith("brickstair", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            if (Config.Materials.EnableStoneConstruction)
            {
                if (outputPath.StartsWith("cobblestone-", StringComparison.Ordinal)
                    || outputPath.StartsWith("cobblestoneslab", StringComparison.Ordinal)
                    || outputPath.StartsWith("cobblestonestair", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // Fail loudly when a required construction asset is missing. Optional
        // material families remain data-driven and may be supplied by add-ons.
        private void ValidateConstructionRegistry(ICoreAPI api)
        {
            string[] requiredBlocks =
            {
                "brickbybrick:masonrycourse",
                "brickbybrick:brickblock-good-fire"
            };

            foreach (string code in requiredBlocks)
            {
                if (api.World.GetBlock(new AssetLocation(code)) == null)
                {
                    Mod.Logger.Error($"Required construction block is not registered: {code}");
                }
            }

            Block? course = api.World.GetBlock(new AssetLocation("brickbybrick:masonrycourse"));
            if (course?.Attributes?["trowelable"].AsBool(false) != true)
            {
                Mod.Logger.Error("The masonry course is missing its trowelable attribute.");
            }
        }
    }   
}
