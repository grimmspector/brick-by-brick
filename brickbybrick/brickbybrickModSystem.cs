using brickbybrick.Blocks;
using brickbybrick.items;
using brickbybrick.RealisticConstruction;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

        internal static BrickByBrickConfig Config { get; private set; } = new();

        private ModSystemSurvivalHandbook? survivalHandbook;
        private ICoreClientAPI? clientApi;
        private IClientNetworkChannel? realisticClientChannel;
        private static readonly Dictionary<string, List<BlockPos>> ProfileCellsByPlayer = new();
        private static readonly Dictionary<string, ProfileExerciseSession> ProfileExercisesByPlayer = new();
        private static readonly object ProfileLogSync = new();

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
            api.RegisterBlockEntityClass(Mod.Info.ModID + ".realisticmasonry", typeof(BlockEntityRealisticMasonry));

            api.Network.RegisterChannel("brickbybrick-realistic")
                .RegisterMessageType<int>()
                .RegisterMessageType<RealisticControlPacket>();

        }

        // Loads one shared settings object on each side. Vintage Story writes
        // the default file only when none exists, then validation guards edits.
        private void LoadConfig(ICoreAPI api)
        {
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
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            api.Network.GetChannel("brickbybrick-realistic")
                .SetMessageHandler<int>((player, packet) => OnRealisticServerPacket(api, player, packet));
            api.Event.RegisterGameTickListener(_ => MasonryFreezeScheduler.DrainReady(), 100);
            RegisterProfilingCommands(api);
            ValidateConstructionRegistry(api);
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
                .BeginSubCommand("clear")
                    .HandleWith(args => ClearProfileCells(api, args))
                .EndSubCommand()
                .BeginSubCommand("report")
                    .HandleWith(args => ReportProfileCells(api, args))
                .EndSubCommand()
                .BeginSubCommand("exercise")
                    .HandleWith(args => StartProfileExercise(api, args))
                .EndSubCommand()
                .BeginSubCommand("stop")
                    .HandleWith(args => StopProfileExercise(api, args))
                .EndSubCommand();
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

        private static int ClearTrackedProfileCells(ICoreServerAPI api, string playerUid)
        {
            if (!ProfileCellsByPlayer.TryGetValue(playerUid, out List<BlockPos>? positions)) return 0;

            int removed = 0;
            foreach (BlockPos pos in positions)
            {
                if (api.World.BlockAccessor.GetBlock(pos).Code?.Path != "realisticmasonry") continue;
                api.World.BlockAccessor.SetBlock(0, pos);
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

            Stopwatch stopwatch = Stopwatch.StartNew();
            int entities = 0;
            int frozen = 0;
            int units = 0;
            long serializedBytes = 0;
            foreach (BlockPos pos in positions)
            {
                if (api.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityRealisticMasonry entity) continue;
                entities++;
                if (entity.State.Frozen) frozen++;
                units += entity.State.Units.Count;
                serializedBytes += MasonryStateCodec.Encode(entity.State).Length;
            }

            stopwatch.Stop();
            string report = $"Cells: {entities:N0}; frozen: {frozen:N0}; units: {units:N0}; "
                + $"packed state: {serializedBytes / 1048576d:N2} MiB; scan: {stopwatch.ElapsedMilliseconds:N0} ms.";
            AppendProfileLog(api, "SERVER CELL REPORT", report);
            return TextCommandResult.Success($"{report} Written to {GetProfileLogPath()}.");
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
            clientApi = api;
            realisticClientChannel = api.Network.GetChannel("brickbybrick-realistic");
            realisticClientChannel.SetMessageHandler<RealisticControlPacket>(packet => OnProfileControlPacket(api, packet.Code));
            api.Event.MouseWheelMove += OnRealisticPlacementMouseWheel;
            RegisterClientProfilingCommands(api);
            survivalHandbook = api.ModLoader.GetModSystem<ModSystemSurvivalHandbook>();
            if (survivalHandbook != null)
            {
                survivalHandbook.OnInitCustomPages += MoveMasonryGuideAfterVanillaGuides;
            }
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
                .BeginSubCommand("clearcache")
                    .HandleWith(_ =>
                    {
                        BlockEntityRealisticMasonry.ClearTransformedMeshCache();
                        AppendProfileLog(api, "CLIENT MESH CACHE CLEAR", "Shared transformed-mesh cache cleared.");
                        return TextCommandResult.Success("Shared masonry mesh cache cleared. Existing chunks will rebuild it as needed.");
                    })
                .EndSubCommand();
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
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {heading}{Environment.NewLine}{report}{Environment.NewLine}{Environment.NewLine}");
                }
            }
            catch (Exception exception)
            {
                api.Logger.Warning($"Could not write Brick by Brick profiling log: {exception.Message}");
            }
        }

        public override void Dispose()
        {
            if (clientApi != null)
            {
                clientApi.Event.MouseWheelMove -= OnRealisticPlacementMouseWheel;
                clientApi = null;
                realisticClientChannel = null;
            }

            if (survivalHandbook != null)
            {
                survivalHandbook.OnInitCustomPages -= MoveMasonryGuideAfterVanillaGuides;
            }

            base.Dispose();
        }

        // Sneak-wheel is scoped to a held trowel in Realistic mode. All other
        // wheel input remains available to the hotbar and other mods.
        private void OnRealisticPlacementMouseWheel(MouseWheelEventArgs args)
        {
            if (!Config.IsRealisticConstructionEnabled() || clientApi?.World?.Player?.Entity?.Controls?.Sneak != true) return;

            ItemSlot slot = clientApi.World.Player.InventoryManager.ActiveHotbarSlot;
            if (slot?.Itemstack?.Collectible is not ItemTrowel) return;

            int orientation = slot.Itemstack.Attributes.GetInt("realisticOrientation", 0);
            int nextOrientation = GameMath.Mod(orientation + (args.delta > 0 ? 1 : -1), 4);
            slot.Itemstack.Attributes.SetInt("realisticOrientation", nextOrientation);
            slot.MarkDirty();
            realisticClientChannel?.SendPacket(nextOrientation);
            args.SetHandled(true);
        }

        private static void OnRealisticOrientationPacket(IPlayer fromPlayer, int orientation)
        {
            ItemSlot? slot = fromPlayer?.InventoryManager?.ActiveHotbarSlot;
            if (slot?.Itemstack?.Collectible is not ItemTrowel) return;

            slot.Itemstack.Attributes.SetInt("realisticOrientation", GameMath.Mod(orientation, 4));
            slot.MarkDirty();
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
