using System;
using System.Threading;

namespace DfoServer.Game.Session
{
    public partial class PlayerContext
    {
        private int _townPresenceReady = 1;
        private int _dungeonSelectionPending;
        private long _townMovementSequence;

        public byte CurTownId { get; set; }
        public byte CurAreaId { get; set; }
        public short CurPosX { get; set; }
        public short CurPosY { get; set; }
        public byte CurDirection { get; set; } = 0x05;
        public byte CurAreaState { get; set; } = 0x03;

        // Kept separate from the protocol-level UserState. A replacement
        // session is published to the character directory before asynchronous
        // teardown of the old generation completes, but must not enter a town
        // roster until the old avatar's USER_LEAVE has been delivered.
        public bool TownPresenceReady
        {
            get => Volatile.Read(ref _townPresenceReady) != 0;
            set => Volatile.Write(
                ref _townPresenceReady, value ? 1 : 0);
        }

        // Party dungeon selection keeps UserState at town until every member
        // has received the mandatory shared-roster packets. This independent
        // gate prevents a queued town-arrival task from republishing a member
        // during that window.
        public bool DungeonSelectionPending
        {
            get => Volatile.Read(ref _dungeonSelectionPending) != 0;
            internal set => Volatile.Write(
                ref _dungeonSelectionPending, value ? 1 : 0);
        }

        internal long NextTownMovementSequence()
            => Interlocked.Increment(ref _townMovementSequence);

        internal long LatestTownMovementSequence
            => Interlocked.Read(ref _townMovementSequence);

        /// <summary>
        /// </summary>
        public DateTime LastPositionPersistAt { get; set; } = DateTime.MinValue;

        // SET_USER_POSITION(0x0023) 包体第 5-6 字节(uint16 小端)的原样值, 实机抓包显示各角色恒定
        // 且随装备/装扮不同(见过 100/230), 疑似移动速度; 出生点首包为 0x0068=104, 作为默认值。
        // 语义未完全确认, 仅用于 0x0016 广播时按客户端原始布局原样回带。
        public ushort CurPositionExtra { get; set; } = 104;
    }
}
