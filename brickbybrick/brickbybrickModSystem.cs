using brickbybrick.Blocks;
using brickbybrick.items;
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
        private const string MasonryGuidePageCode = "gamemechanicinfo-brickbybrick-masonry";

        private ModSystemSurvivalHandbook? survivalHandbook;

        // Registers the item and block classes referenced by the JSON assets.
        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            Mod.Logger.Event($"started '{Mod.Info.Name}' mod");
            api.RegisterItemClass(Mod.Info.ModID + ".trowel", typeof(ItemTrowel));
            api.RegisterBlockClass(Mod.Info.ModID + ".cobbleblock", typeof(BlockStone));
            api.RegisterBlockClass(Mod.Info.ModID + ".brickblock", typeof(BlockBrick));

        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            ValidateConstructionRegistry(api);
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
