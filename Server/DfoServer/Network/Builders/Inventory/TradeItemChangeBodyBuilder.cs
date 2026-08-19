using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders
{
    internal static class TradeItemChangeBodyBuilder
    {
        // CHANGE_ITEMTRADE_ITEM uses a fixed compact item shape. Unlike an
        // ITEM_LIST entry it has no seal, emblem, or random-option blocks. The
        // 86JP client reads the common enchant block, expire time, and the
        // regular ten-byte item tail directly.
        internal const int EmptyOrPlainItemLength = 35;

        internal static byte[] BuildItem(short tradeSlot, ItemCore core)
        {
            return Build(tradeSlot, core, core?.ItemId ?? -1, core?.Value ?? 0);
        }

        internal static byte[] BuildGold(short tradeSlot, int amount)
        {
            return Build(tradeSlot, null, 0, amount);
        }

        internal static byte[] BuildEmpty(short tradeSlot)
        {
            return Build(tradeSlot, null, -1, 0);
        }

        private static byte[] Build(
            short tradeSlot,
            ItemCore core,
            int itemId,
            int value)
        {
            var writer = new GamePacketWriter();
            writer.WriteInt16(tradeSlot);
            writer.WriteInt32(itemId);
            writer.WriteInt32(value);
            writer.WriteByte(core?.Attr ?? 0);
            writer.WriteUInt16(core?.Durability ?? 0);
            writer.WriteInt32(core?.EnchantCardId ?? 0);
            writer.WriteByte(core?.EnchantUpgradeCount ?? 0);
            writer.WriteByte(core?.AmplifyType ?? 0);
            writer.WriteUInt16(core?.AmplifyValue ?? 0);
            writer.WriteInt32(core?.ExpireTime ?? 0);
            writer.WriteByte(core?.GenuineUpgrade ?? 0);
            writer.WriteByte(core?.EmancipateEquipmentLevel ?? 0);
            writer.WriteByte(core?.TradeRestriction ?? 0);
            writer.WriteUInt16(core?.TailUnknown0 ?? 0);
            writer.WriteByte(core?.TailUnknown1 ?? 0);
            writer.WriteByte(core?.TailUnknown2 ?? 0);
            writer.WriteByte(core?.TailUnknown3 ?? 0);
            writer.WriteByte(core?.RemainUseCount ?? 0);
            writer.WriteByte(core?.SortLockFlag == 1 ? (byte)1 : (byte)0);
            return writer.ToArray();
        }
    }
}
