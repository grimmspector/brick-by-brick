using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace brickbybrick.items
{
    internal class ItemTrowel : Item
    {
        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent,ref handling);

            byEntity.World.Api.Logger.Event("Trowel used!");
            if (blockSel == null) return;
            var player = (byEntity as EntityPlayer)?.Player;

     //       if (!byEntity.World.Claims.TryAccess(player, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak))
      //      {
                //api.World.BlockAccessor.MarkBlockEntityDirty(blockSel.Position.AddCopy(blockSel.Face));
                //api.World.BlockAccessor.MarkBlockDirty(blockSel.Position.AddCopy(blockSel.Face));
                //var pos = api.World.BlockAccessor.GetBlock(blockSel.Position);
                //var code = api.World.BlockAccessor.GetBlock(blockSel.Block.Code);
                byEntity.World.Api.Logger.Event("Block position: " + blockSel.Position);
                byEntity.World.Api.Logger.Event("Block selected: " + blockSel.Block.Code);
                byEntity.World.Api.Logger.Event("Block face: " + blockSel.Face);
                byEntity.World.Api.Logger.Event("Block selection hit position: " + blockSel.HitPosition);
          //      return;
        //    }
            byEntity.World.Api.Logger.Event("Entity: " + byEntity.Code);
            handling = EnumHandHandling.PreventDefault;
            return;
        }
        public override bool OnHeldInteractStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel)
        {
            base.OnHeldInteractStep(secondsUsed, slot, byEntity, blockSel, entitySel);
            byEntity.World.Api.Logger.Event("Trowel used continuously for " + secondsUsed + " seconds!");
            if (secondsUsed > 1)
            {
                byEntity.World.Api.Logger.Event("Trowel used for more than 1 second!");
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
