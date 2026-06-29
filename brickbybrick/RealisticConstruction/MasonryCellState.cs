using System.Collections.Generic;

namespace brickbybrick.RealisticConstruction
{
    // Stores construction progress by slot instead of forcing a block through
    // a linear sequence. A future block entity will persist this state.
    public sealed class MasonryCellState
    {
        public string LayoutCode { get; set; } = string.Empty;

        public HashSet<string> PlacedSlots { get; set; } = new();

        public HashSet<string> MortaredSlots { get; set; } = new();

        public int FillUnits { get; set; }

        public bool IsSlotPlaced(string slotCode)
        {
            return PlacedSlots.Contains(slotCode);
        }

        public bool IsSlotMortared(string slotCode)
        {
            return MortaredSlots.Contains(slotCode);
        }

        // Every declared support must exist and have mortar before the next
        // unit can bridge it. Empty support lists represent the bottom layer.
        public bool HasSupportedBase(MasonrySlotDefinition slot)
        {
            foreach (string supportCode in slot.SupportSlots)
            {
                if (!PlacedSlots.Contains(supportCode) || !MortaredSlots.Contains(supportCode))
                {
                    return false;
                }
            }

            return true;
        }

        public bool TryPlace(MasonrySlotDefinition slot)
        {
            return HasSupportedBase(slot) && PlacedSlots.Add(slot.Code);
        }

        public bool TryApplyMortar(string slotCode)
        {
            return PlacedSlots.Contains(slotCode) && MortaredSlots.Add(slotCode);
        }
    }
}
