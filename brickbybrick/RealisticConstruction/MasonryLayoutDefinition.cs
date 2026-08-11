using System;
using System.Collections.Generic;

namespace brickbybrick.RealisticConstruction
{
    // Describes one selectable construction ghost without tying the layout to
    // brick or another masonry family supplied by a future branch.
    public sealed class MasonryLayoutDefinition
    {
        public string Code { get; set; } = string.Empty;

        public string Shape { get; set; } = "block";

        public bool RequiresFillForSupport { get; set; }

        public List<MasonrySlotDefinition> Slots { get; set; } = new();
    }

    // A slot is one independently placeable unit. Coordinates use sixteenths
    // of a block so future ghosts can share the shape coordinate system.
    public sealed class MasonrySlotDefinition
    {
        public string Code { get; set; } = string.Empty;

        public int Layer { get; set; }

        public int[] From { get; set; } = Array.Empty<int>();

        public int[] To { get; set; } = Array.Empty<int>();

        public string Orientation { get; set; } = "north";

        public bool IsHalfUnit { get; set; }

        public List<string> SupportSlots { get; set; } = new();
    }
}
