using brickbybrick.Blocks;
using brickbybrick.items;
using Newtonsoft.Json.Linq;
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

        // Registers the item and block classes referenced by the JSON assets.
        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            LoadConfig(api);
            Mod.Logger.Event($"started '{Mod.Info.Name}' mod");
            api.RegisterItemClass(Mod.Info.ModID + ".trowel", typeof(ItemTrowel));
            api.RegisterBlockClass(Mod.Info.ModID + ".cobbleblock", typeof(BlockStone));
            api.RegisterBlockClass(Mod.Info.ModID + ".brickblock", typeof(BlockBrick));

        }

        // Rebuilds the saved object from the current schema. Valid values are
        // retained while missing, malformed, and obsolete fields are replaced.
        private void LoadConfig(ICoreAPI api)
        {
            try
            {
                BrickByBrickConfig defaults = new();
                JObject defaultObject = JObject.FromObject(defaults);
                JObject? savedObject = api.LoadModConfig<JObject>(ConfigFileName);
                JObject normalizedObject = NormalizeConfigObject(savedObject, defaultObject);

                Config = normalizedObject.ToObject<BrickByBrickConfig>() ?? defaults;
            }
            catch (Exception exception)
            {
                Mod.Logger.Error($"Could not load {ConfigFileName}; defaults will be used. {exception.Message}");
                Config = new BrickByBrickConfig();
            }

            Config.Validate();
            api.StoreModConfig(Config, ConfigFileName);
        }

        // Walks only known properties so stale settings are removed whenever
        // the normalized configuration is written back to disk.
        private static JObject NormalizeConfigObject(JObject? savedObject, JObject defaultObject, string path = "")
        {
            JObject normalizedObject = new();

            foreach (JProperty defaultProperty in defaultObject.Properties())
            {
                string propertyPath = string.IsNullOrEmpty(path)
                    ? defaultProperty.Name
                    : $"{path}.{defaultProperty.Name}";
                JToken? savedValue = savedObject?[defaultProperty.Name];

                if (defaultProperty.Value is JObject defaultChild)
                {
                    normalizedObject[defaultProperty.Name] = NormalizeConfigObject(
                        savedValue as JObject,
                        defaultChild,
                        propertyPath);
                    continue;
                }

                normalizedObject[defaultProperty.Name] = IsValidConfigValue(savedValue, defaultProperty.Value, propertyPath)
                    ? savedValue!.DeepClone()
                    : defaultProperty.Value.DeepClone();
            }

            return normalizedObject;
        }

        // JSON numbers may be stored as either integers or decimals. All other
        // settings must retain their exact schema type before deserialization.
        private static bool IsValidConfigValue(JToken? savedValue, JToken defaultValue, string path)
        {
            if (savedValue == null || savedValue.Type == JTokenType.Null) return false;

            if (path == "Construction.Mode")
            {
                return savedValue.Type == JTokenType.String
                    && Enum.TryParse(savedValue.Value<string>(), true, out ConstructionMode _);
            }

            if (defaultValue.Type == JTokenType.Float)
            {
                if (savedValue.Type != JTokenType.Float && savedValue.Type != JTokenType.Integer) return false;

                double numericValue = savedValue.Value<double>();
                return !double.IsNaN(numericValue) && !double.IsInfinity(numericValue);
            }

            return savedValue.Type == defaultValue.Type;
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
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
            survivalHandbook = api.ModLoader.GetModSystem<ModSystemSurvivalHandbook>();
            if (survivalHandbook != null)
            {
                survivalHandbook.OnInitCustomPages += MoveMasonryGuideAfterVanillaGuides;
            }
        }

        public override void Dispose()
        {
            if (survivalHandbook != null)
            {
                survivalHandbook.OnInitCustomPages -= MoveMasonryGuideAfterVanillaGuides;
            }

            base.Dispose();
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
