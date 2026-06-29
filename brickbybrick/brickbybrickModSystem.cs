using brickbybrick.Blocks;
using brickbybrick.items;
using brickbybrick.RealisticConstruction;
using System;
using System.Collections.Generic;
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

        internal static BrickByBrickConfig Config { get; private set; } = new();

        private ModSystemSurvivalHandbook? survivalHandbook;
        private ICoreClientAPI? clientApi;
        private IClientNetworkChannel? realisticClientChannel;

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
                .RegisterMessageType<int>();

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
                .SetMessageHandler<int>(OnRealisticOrientationPacket);
            ValidateConstructionRegistry(api);
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
            api.Event.MouseWheelMove += OnRealisticPlacementMouseWheel;
            survivalHandbook = api.ModLoader.GetModSystem<ModSystemSurvivalHandbook>();
            if (survivalHandbook != null)
            {
                survivalHandbook.OnInitCustomPages += MoveMasonryGuideAfterVanillaGuides;
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
