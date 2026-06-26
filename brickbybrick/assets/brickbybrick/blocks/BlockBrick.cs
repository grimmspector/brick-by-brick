using AttributeRenderingLibrary;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

#nullable disable

namespace brickbybrick.Blocks
{
    internal class BlockBrick : BlockGeneric
    {
        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
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

        public override int GetRandomColor(ICoreClientAPI capi, BlockPos pos, BlockFacing facing, int rndIndex = -1)
        {
            ItemStack materialStack = GetCourseMaterialStack(capi, pos);

            if (materialStack?.Collectible != null)
            {
                return materialStack.Collectible.GetRandomColor(capi, materialStack);
            }

            return base.GetRandomColor(capi, pos, facing, rndIndex);
        }

        private ItemStack GetCourseMaterialStack(ICoreClientAPI capi, BlockPos pos)
        {
            BlockEntity blockEntity = capi?.World?.BlockAccessor?.GetBlockEntity(pos);
            BlockEntityBehaviorShapeTexturesFromAttributes behavior =
                blockEntity?.GetBehavior<BlockEntityBehaviorShapeTexturesFromAttributes>();

            if (behavior?.Variants?.Any != true) return null;

            string materialDomain = behavior.Variants.Get("materialDomain");
            string materialPath = behavior.Variants.Get("materialPath");

            if (string.IsNullOrEmpty(materialDomain) || string.IsNullOrEmpty(materialPath)) return null;

            Item item = capi.World.GetItem(new AssetLocation(materialDomain, materialPath));
            return item == null ? null : new ItemStack(item);
        }
    }
}
