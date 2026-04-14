using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace brickbybrick.items
{
    internal class ItemThornsBlade : Item
    {
        public override void OnAttackingWith(IWorldAccessor world, Entity byEntity, Entity attackedEntity, ItemSlot itemslot)
        {
            base.OnAttackingWith(world, byEntity, attackedEntity, itemslot);
            world.Api.Logger.Event("Got attack with thorns blade!");
            DamageSource damage = new DamageSource()
            {
                Type = EnumDamageType.PiercingAttack,
                SourceEntity = byEntity,
                KnockbackStrength = 0
            };
            if (attackedEntity.Alive)
            {
                byEntity.ReceiveDamage(damage, 0.25f);
            }
            if (!attackedEntity.Alive)
            {
                attackedEntity.Revive();
            }
        }
    }
}
