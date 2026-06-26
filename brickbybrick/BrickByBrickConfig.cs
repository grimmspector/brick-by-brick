namespace brickbybrick
{
    // Holds shared gameplay settings. Validation keeps malformed
    // or manually edited values within limits that the construction code supports.
    public sealed class BrickByBrickConfig
    {
        public int TrowelCapacityPerTier { get; set; } = 16;

        public float ConstructionActionSeconds { get; set; } = 2f;

        public int MortarCostPerAction { get; set; } = 1;

        public int MasonryCostPerAction { get; set; } = 1;

        public void Validate()
        {
            TrowelCapacityPerTier = Clamp(TrowelCapacityPerTier, 1, 1024);
            ConstructionActionSeconds = Clamp(ConstructionActionSeconds, 0.1f, 30f);
            MortarCostPerAction = Clamp(MortarCostPerAction, 0, 64);
            MasonryCostPerAction = Clamp(MasonryCostPerAction, 0, 64);
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return value < minimum ? minimum : value > maximum ? maximum : value;
        }

        private static float Clamp(float value, float minimum, float maximum)
        {
            return value < minimum ? minimum : value > maximum ? maximum : value;
        }
    }
}
