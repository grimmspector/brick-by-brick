//Here are the imports for this script. Most of these will add automatically.
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

/*
* The namespace the class will be in. This is essentially the folder the script is found in.
* If you need to use the BlockTrampoline class in any other script, you will have to add 'using VSTutorial.Blocks' to that script.
*/
namespace brickbybrick.Blocks
{
    /*
    * The class definition. Here, you define BlockTrampoline as a child of Block, which
    * means you can 'override' many of the functions within the general Block class. 
    */
    internal class BlockTrampoline : Block
    {
        public override void OnEntityCollide(IWorldAccessor world, Entity entity, BlockPos pos, BlockFacing facing, Vec3d collideSpeed, bool isImpact)
        {
            base.OnEntityCollide(world, entity, pos, facing, collideSpeed, isImpact);
            if (isImpact && facing.IsVertical)
            {
                entity.Pos.Motion.Y = entity.Pos.Motion.Y * -0.8f;
            }
        }
    }
}