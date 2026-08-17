using System.Buffers.Binary;
using System;

namespace DfoServer.Network.Parsers.Inventory
{
    internal sealed class EventServerLevelUpRequest
    {
        // 86JP 的 0x01A2 请求体已由实机使用升级券验证：
        // +0x00 [i16 LE] 主背包槽位，+0x02 [u8] 客户端状态。
        internal const int BodyLength = 3;

        internal short SlotIndex { get; private set; }
        internal byte State { get; private set; }

        internal static bool TryParse(byte[] body, out EventServerLevelUpRequest request)
        {
            request = null;
            if (body == null || body.Length != BodyLength)
                return false;

            var slotIndex = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(0, 2));
            if (slotIndex < 0)
                return false;

            request = new EventServerLevelUpRequest
            {
                SlotIndex = slotIndex,
                State = body[2],
            };
            return true;
        }
    }
}
