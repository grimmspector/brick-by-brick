# Brick-by-Brick Configuration

Brick-by-Brick creates `brickbybrick.json` in the Vintage Story `ModConfig` directory the first time the mod starts. Stop the game or server before editing the file, then restart it to apply changes.

The default configuration preserves the mod's current immersive gameplay. Invalid numeric values are automatically moved to the nearest supported value when the configuration loads.

> [!IMPORTANT]
> Standard JSON does not support comments. Do not add `//` or `/* */` comments to `brickbybrick.json`.

## Current support

Settings marked **Active** affect the current release. Settings marked **Planned** are included so future systems can adopt a stable configuration structure; changing them does not affect gameplay yet.

## Construction

| Setting | Values | Default | Status | Description |
| --- | --- | --- | --- | --- |
| `Mode` | `Cosmetic`, `Immersive`, `Realistic`, `Builder` | `Immersive` | Active | Selects the overall construction experience. Realistic currently falls back to Immersive. |
| `DisableVanillaBlockRecipes` | `true`, `false` | `false` | Active | Disables covered vanilla brick or stone block recipes when their material family is enabled. |
| `EnableBloomeryConstruction` | `true`, `false` | `false` | Planned | Enables sequential bloomery construction when that system is available. |

The planned construction modes are:

- `Cosmetic`: 0.3-second actions with normal material costs.
- `Immersive`: current pacing with vanilla-equivalent costs.
- `Realistic`: planned one-to-one visible masonry units, structural support rules, and curing. It currently falls back to Immersive.
- `Builder`: 0.3-second actions with half the normal mortar consumption.

`ConstructionActionSeconds` controls Immersive pacing and the current Realistic fallback. Cosmetic and Builder always use 0.3 seconds per stage. Builder carries half portions between actions, so one mortar portion pays for two actions when the normal cost is one.

## Trowels

| Setting | Range | Default | Status | Description |
| --- | ---: | ---: | --- | --- |
| `EnableDiagonalPlacement` | `true`, `false` | `true` | Experimental | Adds 45-degree ground-plane steps to the realistic masonry rotation control. |
| `CapacityPerTier` | 1–1024 | 16 | Active | Mortar capacity added for each trowel tool tier. |
| `ConstructionActionSeconds` | 0.1–30.0 | 2.0 | Active | Seconds required in Immersive mode and the current Realistic fallback. |
| `MortarCostPerAction` | 0–64 | 1 | Active | Mortar portions consumed by each successful action. |
| `MasonryCostPerAction` | 0–64 | 1 | Active | Bricks or stones consumed by each masonry action. |
| `AllowContainerRefill` | `true`, `false` | `true` | Active | Allows trowels to collect mortar from compatible liquid containers. |
| `EnablePlacementPreview` | `true`, `false` | `true` | Active | Shows the translucent finished-shape preview while placing masonry. |
| `PlacementPreviewOpacity` | 0.0–1.0 | 0.52 | Active | Controls preview transparency. Zero is invisible and one is opaque. |

Setting a material or mortar cost to zero makes that resource free, but the player may still need to hold the correct material so Brick-by-Brick can determine the block variant.

## Effects

| Setting | Range | Default | Status | Description |
| --- | ---: | ---: | --- | --- |
| `EnableConstructionSounds` | `true`, `false` | `true` | Active | Enables trowel and masonry action sounds. |
| `ConstructionSoundRange` | 0.0–128.0 | 20.0 | Active | Maximum audible range of construction sounds in blocks. |
| `EnableConstructionParticles` | `true`, `false` | `true` | Active | Enables mortar, masonry-chip, and completion particles. |

## Materials

| Setting | Values | Default | Status | Description |
| --- | --- | --- | --- | --- |
| `EnableBrickConstruction` | `true`, `false` | `true` | Partially active | Controls whether vanilla brick recipes are disabled. Full construction-family filtering is planned. |
| `EnableStoneConstruction` | `true`, `false` | `true` | Partially active | Controls whether vanilla stone recipes are disabled. Full construction-family filtering is planned. |
| `EnableRefractoryConstruction` | `true`, `false` | `true` | Planned | Enables refractory masonry construction. |
| `EnableModdedMaterials` | `true`, `false` | `true` | Planned | Allows compatible materials supplied by other mods. |

`DisableVanillaBlockRecipes` affects a family only when its matching material setting is enabled. It does not disable recipes added by other mods.

## Mortar

| Setting | Range | Default | Status | Description |
| --- | ---: | ---: | --- | --- |
| `MortarRecipeYieldMultiplier` | 0.01–100.0 | 1.0 | Planned | Multiplies ordinary mortar recipe output. |
| `ClayRecipeYieldMultiplier` | 0.01–100.0 | 1.0 | Planned | Multiplies liquid clay recipe output. |
| `RefractoryRecipeYieldMultiplier` | 0.01–100.0 | 1.0 | Planned | Multiplies refractory mortar recipe output. |
| `EnableTieredMortar` | `true`, `false` | `false` | Planned | Enables mortar tiers for temperature-sensitive structures. |
| `EnforceMortarTemperatureTier` | `true`, `false` | `true` | Planned | Requires mortar of the appropriate tier for high-temperature construction. |

## Curing

| Setting | Range | Default | Status | Description |
| --- | ---: | ---: | --- | --- |
| `EnableMortarCuring` | `true`, `false` | `false` | Planned | Enables wet masonry and curing time. |
| `MortarCuringHours` | 0.0–720.0 | 24.0 | Planned | Base in-game hours required for mortar to cure. |
| `CuringSpeedMultiplier` | 0.01–100.0 | 0.1 | Active | Multiplies curing speed. Higher values cure faster. |
| `InactiveFreezeSeconds` | 1.0–300.0 | 60.0 | Active | Real-time inactivity before Realistic masonry closes when curing is disabled. |
| `AllowWetBlockPickup` | `true`, `false` | `false` | Planned | Allows wet masonry blocks to be picked up intact. |
| `AllowWetBlockDismantling` | `true`, `false` | `true` | Planned | Allows wet masonry to be dismantled into components. |
| `DismantlingMortarRecovery` | 0.0–1.0 | 0.0 | Planned | Fraction of mortar recovered while dismantling. |
| `DismantlingMasonryRecovery` | 0.0–1.0 | 1.0 | Planned | Fraction of bricks or stones recovered while dismantling. |

Recovery values are fractions: `0.25` is 25%, `0.5` is 50%, and `1.0` is 100%.

## Realism

| Setting | Range | Default | Status | Description |
| --- | ---: | ---: | --- | --- |
| `EnableOptimizedFrozenMeshes` | `true`, `false` | `false` | Experimental | Uses validated exposed-face and greedy-merged meshes for frozen masonry, with automatic component-renderer fallback. |
| `AllowUnmortaredRoomSealing` | `true`, `false` | `false` | Active | Allows realistic masonry without complete mortar coverage in every eligible vertical side joint to seal rooms. Top mortar does not affect room sealing. |
| `FrozenMeshCacheMiB` | 32–1024 | 64 | Active | Maximum estimated client memory retained for reusable frozen masonry meshes. |
| `TransformedMeshCacheMiB` | 8–256 | 16 | Active | Maximum estimated client memory retained for shared transformed masonry components. |
| `EnableGroundPlacedStacks` | `true`, `false` | `true` | Planned | Allows loose bricks and stones to form visible ground stacks. |
| `EnablePathmaking` | `true`, `false` | `true` | Planned | Allows supported loose masonry materials to create paths. |
| `EnableSledgehammer` | `true`, `false` | `true` | Planned | Enables the masonry dismantling tool. |
| `SledgehammerRecoveryMultiplier` | 0.0–1.0 | 1.0 | Planned | Fraction of eligible materials recovered with a sledgehammer. |

## Visuals

| Setting | Values | Default | Status | Description |
| --- | --- | --- | --- | --- |
| `EnableMortarColorVariants` | `true`, `false` | `true` | Planned | Displays the appropriate mortar color on supported masonry. |
| `EnableImmersiveStoneShapes` | `true`, `false` | `true` | Planned | Uses varied dry-stone and cobblestone shapes where supported. |

## Example

This example contains every available setting and its default value:

```json
{
  "Construction": {
    "Mode": "Immersive",
    "DisableVanillaBlockRecipes": false,
    "EnableBloomeryConstruction": false
  },
  "Trowels": {
    "CapacityPerTier": 16,
    "ConstructionActionSeconds": 2.0,
    "MortarCostPerAction": 1,
    "MasonryCostPerAction": 1,
    "AllowContainerRefill": true,
    "EnablePlacementPreview": true,
    "PlacementPreviewOpacity": 0.52
  },
  "Effects": {
    "EnableConstructionSounds": true,
    "ConstructionSoundRange": 20.0,
    "EnableConstructionParticles": true
  },
  "Materials": {
    "EnableBrickConstruction": true,
    "EnableStoneConstruction": true,
    "EnableRefractoryConstruction": true,
    "EnableModdedMaterials": true
  },
  "Mortar": {
    "MortarRecipeYieldMultiplier": 1.0,
    "ClayRecipeYieldMultiplier": 1.0,
    "RefractoryRecipeYieldMultiplier": 1.0,
    "EnableTieredMortar": false,
    "EnforceMortarTemperatureTier": true
  },
  "Curing": {
    "EnableMortarCuring": false,
    "MortarCuringHours": 24.0,
    "CuringSpeedMultiplier": 0.1,
    "InactiveFreezeSeconds": 60.0,
    "AllowWetBlockPickup": false,
    "AllowWetBlockDismantling": true,
    "DismantlingMortarRecovery": 0.0,
    "DismantlingMasonryRecovery": 1.0
  },
    "Realism": {
      "AllowUnmortaredRoomSealing": false,
      "EnableOptimizedFrozenMeshes": false,
      "FrozenMeshCacheMiB": 64,
      "TransformedMeshCacheMiB": 16,
    "EnableGroundPlacedStacks": true,
    "EnablePathmaking": true,
    "EnableSledgehammer": true,
    "SledgehammerRecoveryMultiplier": 1.0
  },
  "Visuals": {
    "EnableMortarColorVariants": true,
    "EnableImmersiveStoneShapes": true
  }
}
```

## Restoring defaults

Stop Vintage Story, delete `brickbybrick.json` from the `ModConfig` directory, and start the game or server again. Brick-by-Brick will create a fresh configuration with default values. Existing worlds and placed blocks are not deleted by resetting the configuration.
