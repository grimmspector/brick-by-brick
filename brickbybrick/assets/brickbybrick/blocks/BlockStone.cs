using Vintagestory.API.Common;

namespace brickbybrick.Blocks
{
    internal class BlockStone : Block
    {
        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent,ref handling);
            byEntity.World.Api.Logger.Event("Block placed! ");
            byEntity.World.Api.Logger.Event("Block selected: " + blockSel.Block.Code);
            byEntity.World.Api.Logger.Event("Block position: " + blockSel.Position);
            byEntity.World.Api.Logger.Event("Block face: " + blockSel.Face);
            byEntity.World.Api.Logger.Event("Block selection hit position: " + blockSel.HitPosition);
            byEntity.World.Api.Logger.Event("Entity: " + byEntity.Code);
            return;
        }
        public override bool OnHeldInteractStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel)
        {
            base.OnHeldInteractStep(secondsUsed, slot, byEntity, blockSel, entitySel);
            if (secondsUsed > 1)
            {
                byEntity.World.Api.Logger.Event("Block used for more than one second!");
                byEntity.World.Api.Logger.Event("Seconds used: " + secondsUsed);
                byEntity.World.Api.Logger.Event("Block selected: " + blockSel.Block.Code);
                byEntity.World.Api.Logger.Event("Block position: " + blockSel.Position);
                byEntity.World.Api.Logger.Event("Block face: " + blockSel.Face);
                byEntity.World.Api.Logger.Event("Block selection hit position: " + blockSel.HitPosition);
                byEntity.World.Api.Logger.Event("Entity: " + byEntity.Code);
                return false;
            }
            return true;
        }
    }
}
