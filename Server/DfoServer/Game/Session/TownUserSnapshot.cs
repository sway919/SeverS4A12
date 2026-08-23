namespace DfoServer.Game.Session
{
    public sealed class TownUserSnapshot
    {
        public ushort UserId { get; set; }

        public byte TownId { get; set; }

        public byte AreaId { get; set; }

        public short PosX { get; set; }

        public short PosY { get; set; }

        public byte Direction { get; set; }

        public byte State { get; set; }

        // SET_USER_POSITION 包体第 5-6 字节的原样值(疑似移动速度), 仅 0x0016 广播使用。
        public ushort PositionExtra { get; set; }
    }
}
