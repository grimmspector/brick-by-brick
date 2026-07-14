# brick-by-brick

Vintage Story mod Brick-by-Brick for manual masonry construction. Version 0.1.1 (pre-alpha).

## STATUS

Brick-by-Brick currently supports metal masonry trowels, liquid mortar handling, staged fireclay brick masonry, placement previews, structural support checks, handbook guidance, and a generated configuration file. Several broader realism and compatibility systems are intentionally represented in config and planning docs before their full gameplay implementation.

## GOALS

### COMPLETED

+ [x] Add metal trowel tool variants.
	+ [x] Add copper, bronze, iron, meteoric iron, and steel trowels.
	+ [x] Add metal trowel grid recipes.
	+ [x] Add trowel head smithing recipes and items.
+ [x] Add liquid mortar foundation.
	+ [x] Add metal trowel liquid mortar storage.
	+ [x] Add liquid mortar barrel recipe.
	+ [x] Add liquid clay barrel recipes.
	+ [x] Add bucket and barrel refill support for trowels.
	+ [x] Add liquid items for clay, mortar, and refractory mortar.
+ [x] Add staged fireclay brick construction foundation.
	+ [x] Add staged brick block construction.
	+ [x] Add staged brick slab placement.
	+ [x] Add staged brick stair placement.
	+ [x] Add trowel modes for build, slab, stair, and block placement.
	+ [x] Add placement preview for slab, stair, and block modes.
	+ [x] Add structural support checks for placement and advancement.
+ [x] Add configuration file support.
	+ [x] Add active trowel, pacing, support, preview, sound, and particle settings.
	+ [x] Add planned config sections for curing, realism, mortar tiers, and visual variants.
	+ [x] Normalize and clamp config values on load.
+ [x] Add player-facing guidance.
	+ [x] Add English language entries for current items, blocks, notices, tool modes, and handbook text.
	+ [x] Add in-game masonry handbook page.
	+ [x] Add extra handbook sections for trowels, mortar, liquid clay, refractory mortar, construction courses, and finished masonry.

### IN DEVELOPMENT

+ [ ] Expand sequential construction beyond the current fireclay brick path.
	+ [ ] Add broader brick pattern support.
	+ [ ] Add stone construction stages.
	+ [ ] Add refractory brick construction stages.
	+ [ ] Ensure all supported blocks have correct opaqueness, texture alignment, hitboxes, and collision.
+ [ ] Improve material and variant selection.
	+ [ ] Add context menu or other selection flow for brick block variants.
	+ [ ] Support more brick colors and masonry families through data-driven variants.
+ [ ] Polish current code and data.
	+ [ ] Remove or gate temporary interaction debug logging.
	+ [ ] Decide whether to normalize Vintage Story JSON extensions to strict JSON for external tooling.
	+ [ ] Add concise comments where current C# and JSON behavior is not self-explanatory.
+ [ ] Tune balance.
	+ [ ] Rebalance mortar production ratios and consumption ratios.
	+ [ ] Connect active settings to any remaining planned recipe-yield behavior.

### PLANNED

+ [ ] Add primitive wooden trowel with low durability and no repairability.
+ [ ] Add primitive wooden trowel liquid mortar functionality.
+ [ ] Change brick and stone item textures except when holding a single brick.
+ [ ] Change brick and stone ground-placement behaviours for stackability, pathmaking, and block assembly.
+ [ ] Add compatibility with XSkills for clayworking and mining XP.
+ [ ] Add tiered mortar system for high-temperature uses.
+ [ ] Add sequential construction of bloomeries.
+ [ ] Add tiers to bloomeries, or compatibility with an existing bloomery-tier mod.
+ [ ] Add tiered mortar support for cementation furnaces and beehive kilns.
+ [ ] Add visibly different mortar colors on supported blocks.
+ [ ] Add full compatibility with rock and brick types from other mods.
+ [ ] Add compatibility with modded classes that change ingredient ratios for refractory bricks.
+ [ ] Add immersive dry-stone and cobblestone shapes.
+ [ ] Add curing time for mortared masonry.
+ [ ] Add wet final masonry blocks that dry into normal blocks over time.
+ [ ] Add sledgehammer tool for dismantling placed masonry.
+ [ ] Add total configuration coverage for supported systems.
+ [ ] Correct stone and brick textures for stairs, slabs, and blocks.
+ [ ] Add optional realistic mode with one-to-one visible masonry units.
+ [ ] Extend construction beyond stairs, slabs, and blocks.
+ [ ] Add extra-vanilla brick bonds.
+ [ ] Use Attribute Rendering Library to optimize masonry variants after the initial public release.
+ [ ] Add lime kiln support for bulk lime production.
+ [ ] Add localization beyond English.
+ [ ] Add polished artwork for the ModDB page.

### PRIORITIES

1. Finish and verify current staged masonry paths.
2. Rebalance mortar recipes and consumption.
3. Remove temporary debug logging and strict-JSON warnings.
4. Improve art and textures.
5. Add curing timer on mortared bricks.
6. Add broad compatibility with vanilla, modded rocks, modded bricks, and scaffolding-style support.
7. Add sledgehammer dismantling.
8. Expand immersive construction changes.
9. Add visible mortar color variants.
10. Implement optional realistic mode.
11. Extend construction to more block types.
