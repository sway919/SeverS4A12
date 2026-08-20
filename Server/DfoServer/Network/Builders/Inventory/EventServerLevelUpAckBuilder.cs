namespace DfoServer.Network.Builders
{
    internal static class EventServerLevelUpAckBuilder
    {
        internal const int SuccessBodyLength = 12;

        internal static byte[] BuildSuccess(short slotIndex)
        {
            var writer = new GamePacketWriter();

            // 86JP 0x01A2 成功体；实机已验证连续使用两张升级券后客户端正常刷新。
            writer.WriteByte(0x01);       // +0x00 [u8]    指令成功。
            writer.WriteInt16(slotIndex); // +0x01 [i16LE] 被消耗的主背包槽位。
            writer.WriteZeroBytes(9);     // +0x03 [9B]    兼容尾，语义待确认，固定为零。
            return writer.ToArray();
        }
    }
}
