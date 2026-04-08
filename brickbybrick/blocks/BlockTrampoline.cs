//Here are the imports for this script. Most of these will add automatically.
using Vintagestory.API.Common;
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
        //Any code within this 'override' function will be called when a trampoline block is placed. 
        public override void OnBlockPlaced(IWorldAccessor world, BlockPos blockPos, ItemStack byItemStack = null)
        {
            //Log a message to the console.
            api.Logger.Event("Trampoline Block Placed!");
            //Perform any default logic when our block is placed.
            base.OnBlockPlaced(world, blockPos, byItemStack);
        }

        //Any code within this 'override' function will be called when a trampoline block is broken.
        public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
        {
            //Log a message to the console.
            api.Logger.Event("Trampoline Block Broken!");
            //Perform any default logic when our block is broken (e.g., dropping the block as an item.)
            base.OnBlockBroken(world, pos, byPlayer, dropQuantityMultiplier);
        }
    }
}