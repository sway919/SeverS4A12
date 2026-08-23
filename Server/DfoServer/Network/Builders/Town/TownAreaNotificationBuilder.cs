using System.Collections.Generic;
using DfoServer.Game.Session;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class TownAreaNotificationBuilder
    {
        public static TownUserSnapshot CreateCurrentSnapshot(PlayerContext player)
        {
            return new TownUserSnapshot
            {
                UserId = player.UserId,
                TownId = player.CurTownId,
                AreaId = player.CurAreaId,
                PosX = player.CurPosX,
                PosY = player.CurPosY,
                Direction = player.CurDirection,
                State = player.CurAreaState,
                PositionExtra = player.CurPositionExtra,
            };
        }

        public static byte[] BuildUserArea(TownUserSnapshot snapshot)
        {
            var writer = new GamePacketWriter();

            writer.WriteUInt16(snapshot.UserId);
            writer.WriteByte(snapshot.TownId);
            writer.WriteByte(snapshot.AreaId);
            writer.WriteInt16(snapshot.PosX);
            writer.WriteInt16(snapshot.PosY);
            writer.WriteByte(snapshot.Direction);
            writer.WriteByte(snapshot.State);
            return writer.ToArray();
        }

        // 单人重载: 保持既有(已验证的单机)行为——AREA_USERS 里含自己 count=1。
        public static byte[] BuildAreaUsers(TownUserSnapshot snapshot)
        {
            return BuildAreaUsers(snapshot.TownId, snapshot.AreaId, new[] { snapshot });
        }

        /// <summary>
        /// AREA_USERS(0x0018): 区域内玩家名册。字节布局与既有单人版一致
        /// (townId, areaId, uint16 count, 每人[uint16 userId, int16 x, int16 y, byte dir, byte state]),
        /// 仅把 count 从写死 1 改为可变、追加多个 per-user 块 —— 是已验证格式的自然扩展。
        /// </summary>
        public static byte[] BuildAreaUsers(byte townId, byte areaId, IReadOnlyList<TownUserSnapshot> users)
        {
            var writer = new GamePacketWriter();

            writer.WriteByte(townId);
            writer.WriteByte(areaId);
            writer.WriteUInt16((ushort)users.Count);
            foreach (var u in users)
            {
                writer.WriteUInt16(u.UserId);
                writer.WriteInt16(u.PosX);
                writer.WriteInt16(u.PosY);
                writer.WriteByte(u.Direction);
                writer.WriteByte(u.State);
            }
            return writer.ToArray();
        }

        /// <summary>
        /// USER_POSITION(0x0016) NOTI: 把某玩家的移动广播给同区域其它人。
        /// 布局 = userId + SET_USER_POSITION(0x0023) 客户端包体的原始 7 字节
        /// (int16 x, int16 y, byte direction, uint16 extra/疑似移动速度)。
        /// 依据 2026-08-21 实机抓包: 客户端 0x0023 包体固定 7 字节, 第 5 字节取值 01-08(8 方向),
        /// 第 6-7 字节 uint16 各角色恒定且随装备不同(100/230)。此前版本广播 dir+state(各 1 字节
        /// 且为硬编码值), 与客户端上报布局不一致, 接收端只能硬摆坐标(瞬移、无走路动画)。
        /// </summary>
        public static byte[] BuildUserPosition(TownUserSnapshot snapshot)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(snapshot.UserId);
            writer.WriteInt16(snapshot.PosX);
            writer.WriteInt16(snapshot.PosY);
            writer.WriteByte(snapshot.Direction);
            writer.WriteUInt16(snapshot.PositionExtra);
            return writer.ToArray();
        }

        /// <summary>
        /// USER_LEAVE(0x0006) NOTI: 某玩家离开区域(断线/切区域)时广播给同区域其它人以移除其分身。
        /// ⚠️ 字节布局为推测(userId), 需真机抓包校验。
        /// </summary>
        public static byte[] BuildUserLeave(ushort userId)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(userId);
            return writer.ToArray();
        }
    }
}