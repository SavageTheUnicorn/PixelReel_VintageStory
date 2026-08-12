using System.Collections.Generic;
using PixelReel.BlockEntities;
using PixelReel.Displays;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace PixelReel.Blocks
{
    /// <summary>
    /// A projector: one ordinary 1x1 block that throws a floating screen of its type's
    /// size in front of itself.
    ///
    /// This replaced the multiblock panel system. No filler blocks means no rotated
    /// collision boxes to get wrong, no footprint validation, and players frame the
    /// picture with whatever blocks they like -- which is what the bezels were for.
    /// </summary>
    public class BlockDisplay : Block
    {
        private DisplayType cachedType;

        public DisplayType Type
        {
            get
            {
                if (cachedType == null)
                {
                    cachedType = DisplayType.FromId(Variant["type"]) ?? DisplayType.CompactTelevision;
                }
                return cachedType;
            }
        }

        public BlockFacing Facing
        {
            get
            {
                BlockFacing f = BlockFacing.FromCode(Variant["side"]);
                return f ?? BlockFacing.NORTH;
            }
        }

        public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack,
                                           BlockSelection blockSel, ref string failureCode)
        {
            if (!CanPlaceBlock(world, byPlayer, blockSel, ref failureCode)) return false;

            BlockFacing screenFacing = SuggestedHVOrientation(byPlayer, blockSel)[0].Opposite;

            Block oriented = world.GetBlock(CodeWithVariants(new Dictionary<string, string> {
                { "type", Type.Id },
                { "side", screenFacing.Code }
            }));
            if (oriented == null)
            {
                failureCode = "cantplace";
                return false;
            }

            oriented.DoPlaceBlock(world, byPlayer, blockSel, itemstack);
            return true;
        }

        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            BlockEntityDisplay be = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BlockEntityDisplay;
            if (be == null) return base.OnBlockInteractStart(world, byPlayer, blockSel);

            bool sneaking = byPlayer?.Entity?.Controls?.Sneak == true;
            return be.OnInteract(byPlayer, sneaking);
        }

        public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
        {
            return new WorldInteraction[]
            {
                new WorldInteraction
                {
                    ActionLangCode = "pixelreel:blockhelp-openmenu",
                    MouseButton = EnumMouseButton.Right
                },
                new WorldInteraction
                {
                    ActionLangCode = "pixelreel:blockhelp-togglepower",
                    MouseButton = EnumMouseButton.Right,
                    HotKeyCode = "sneak"
                }
            }.Append(base.GetPlacedBlockInteractionHelp(world, selection, forPlayer));
        }
    }
}
