using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace brickbybrick
{
    // Serialize profile names instead of numeric enum values.
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ConstructionMode
    {
        Cosmetic,
        Immersive,
        Realistic,
        Builder
    }

    // Root model for brickbybrick.json.
    public sealed class BrickByBrickConfig
    {
        public ConstructionSettings Construction { get; set; } = new();

        public TrowelSettings Trowels { get; set; } = new();

        public EffectSettings Effects { get; set; } = new();

        public MaterialSettings Materials { get; set; } = new();

        public MortarSettings Mortar { get; set; } = new();

        public CuringSettings Curing { get; set; } = new();

        public RealismSettings Realism { get; set; } = new();

        public VisualSettings Visuals { get; set; } = new();

        public void Validate()
        {
            Construction ??= new ConstructionSettings();
            Trowels ??= new TrowelSettings();
            Effects ??= new EffectSettings();
            Materials ??= new MaterialSettings();
            Mortar ??= new MortarSettings();
            Curing ??= new CuringSettings();
            Realism ??= new RealismSettings();
            Visuals ??= new VisualSettings();

            Trowels.Validate();
            Effects.Validate();
            Mortar.Validate();
            Curing.Validate();
            Realism.Validate();
        }

        public ConstructionMode GetEffectiveMode()
        {
            return Construction.Mode;
        }

        public bool IsRealisticConstructionEnabled()
        {
            return Construction.Mode == ConstructionMode.Realistic;
        }

        public float GetConstructionActionSeconds()
        {
            ConstructionMode mode = GetEffectiveMode();

            return mode == ConstructionMode.Cosmetic || mode == ConstructionMode.Builder
                ? 0.3f
                : Trowels.ConstructionActionSeconds;
        }

        public float GetMortarCostMultiplier()
        {
            return GetEffectiveMode() == ConstructionMode.Builder ? 0.5f : 1.0f;
        }
    }

    public sealed class ConstructionSettings
    {
        public ConstructionMode Mode { get; set; } = ConstructionMode.Immersive;

        public bool DisableVanillaBlockRecipes { get; set; } = false;

        public bool EnableBloomeryConstruction { get; set; } = false;
    }

    public sealed class TrowelSettings
    {
        public int CapacityPerTier { get; set; } = 16;

        public float ConstructionActionSeconds { get; set; } = 2.0f;

        public int MortarCostPerAction { get; set; } = 1;

        public int MasonryCostPerAction { get; set; } = 1;

        public bool AllowContainerRefill { get; set; } = true;

        public bool EnablePlacementPreview { get; set; } = true;

        public float PlacementPreviewOpacity { get; set; } = 0.52f;

        internal void Validate()
        {
            CapacityPerTier = ConfigRange.Clamp(CapacityPerTier, 1, 1024);
            ConstructionActionSeconds = ConfigRange.Clamp(ConstructionActionSeconds, 0.1f, 30.0f);
            MortarCostPerAction = ConfigRange.Clamp(MortarCostPerAction, 0, 64);
            MasonryCostPerAction = ConfigRange.Clamp(MasonryCostPerAction, 0, 64);
            PlacementPreviewOpacity = ConfigRange.Clamp(PlacementPreviewOpacity, 0.0f, 1.0f);
        }
    }

    public sealed class EffectSettings
    {
        public bool EnableConstructionSounds { get; set; } = true;

        public float ConstructionSoundRange { get; set; } = 20.0f;

        public bool EnableConstructionParticles { get; set; } = true;

        internal void Validate()
        {
            ConstructionSoundRange = ConfigRange.Clamp(ConstructionSoundRange, 0.0f, 128.0f);
        }
    }

    public sealed class MaterialSettings
    {
        public bool EnableBrickConstruction { get; set; } = true;

        public bool EnableStoneConstruction { get; set; } = true;

        public bool EnableRefractoryConstruction { get; set; } = true;

        public bool EnableModdedMaterials { get; set; } = true;
    }

    public sealed class MortarSettings
    {
        public float MortarRecipeYieldMultiplier { get; set; } = 1.0f;

        public float ClayRecipeYieldMultiplier { get; set; } = 1.0f;

        public float RefractoryRecipeYieldMultiplier { get; set; } = 1.0f;

        public bool EnableTieredMortar { get; set; } = false;

        public bool EnforceMortarTemperatureTier { get; set; } = true;

        internal void Validate()
        {
            MortarRecipeYieldMultiplier = ConfigRange.Clamp(MortarRecipeYieldMultiplier, 0.01f, 100.0f);
            ClayRecipeYieldMultiplier = ConfigRange.Clamp(ClayRecipeYieldMultiplier, 0.01f, 100.0f);
            RefractoryRecipeYieldMultiplier = ConfigRange.Clamp(RefractoryRecipeYieldMultiplier, 0.01f, 100.0f);
        }
    }

    public sealed class CuringSettings
    {
        public bool EnableMortarCuring { get; set; } = false;

        public float MortarCuringHours { get; set; } = 24.0f;

        public float CuringSpeedMultiplier { get; set; } = 0.1f;

        public float InactiveFreezeSeconds { get; set; } = 60.0f;

        public bool AllowWetBlockPickup { get; set; } = false;

        public bool AllowWetBlockDismantling { get; set; } = true;

        public float DismantlingMortarRecovery { get; set; } = 0.0f;

        public float DismantlingMasonryRecovery { get; set; } = 1.0f;

        internal void Validate()
        {
            MortarCuringHours = ConfigRange.Clamp(MortarCuringHours, 0.0f, 720.0f);
            CuringSpeedMultiplier = ConfigRange.Clamp(CuringSpeedMultiplier, 0.01f, 100.0f);
            InactiveFreezeSeconds = ConfigRange.Clamp(InactiveFreezeSeconds, 1.0f, 300.0f);
            DismantlingMortarRecovery = ConfigRange.Clamp(DismantlingMortarRecovery, 0.0f, 1.0f);
            DismantlingMasonryRecovery = ConfigRange.Clamp(DismantlingMasonryRecovery, 0.0f, 1.0f);
        }
    }

    public sealed class RealismSettings
    {
        // Experimental until visually validated across every masonry shape.
        // Invalid meshes automatically fall back to component rendering.
        public bool EnableOptimizedFrozenMeshes { get; set; } = false;

        public int FrozenMeshCacheMiB { get; set; } = 64;

        public int TransformedMeshCacheMiB { get; set; } = 16;

        public int MortarCapacityMultiplier { get; set; } = 4;

        public int FillUnitsPerItem { get; set; } = 2;

        public bool EnableGroundPlacedStacks { get; set; } = true;

        public bool EnablePathmaking { get; set; } = true;

        public bool EnableSledgehammer { get; set; } = true;

        public float SledgehammerRecoveryMultiplier { get; set; } = 1.0f;

        internal void Validate()
        {
            FrozenMeshCacheMiB = ConfigRange.Clamp(FrozenMeshCacheMiB, 32, 1024);
            TransformedMeshCacheMiB = ConfigRange.Clamp(TransformedMeshCacheMiB, 8, 256);
            MortarCapacityMultiplier = ConfigRange.Clamp(MortarCapacityMultiplier, 1, 64);
            FillUnitsPerItem = ConfigRange.Clamp(FillUnitsPerItem, 1, 64);
            SledgehammerRecoveryMultiplier = ConfigRange.Clamp(SledgehammerRecoveryMultiplier, 0.0f, 1.0f);
        }
    }

    public sealed class VisualSettings
    {
        public bool EnableMortarColorVariants { get; set; } = true;

        public bool EnableImmersiveStoneShapes { get; set; } = true;
    }

    internal static class ConfigRange
    {
        internal static int Clamp(int value, int minimum, int maximum)
        {
            return value < minimum ? minimum : value > maximum ? maximum : value;
        }

        internal static float Clamp(float value, float minimum, float maximum)
        {
            return value < minimum ? minimum : value > maximum ? maximum : value;
        }
    }
}
