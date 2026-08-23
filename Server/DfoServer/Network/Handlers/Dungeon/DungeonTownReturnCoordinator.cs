using System;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonTownReturnCoordinator
    {
        private readonly DungeonInstanceRegistry _instanceRegistry;
        private readonly DungeonProgressNotificationProjector
            _progressNotifications;
        private readonly Game.Session.ISessionDirectory _sessions;

        internal DungeonTownReturnCoordinator(
            DungeonInstanceRegistry instanceRegistry,
            DungeonProgressNotificationProjector progressNotifications,
            Game.Session.ISessionDirectory sessions = null)
        {
            _instanceRegistry = instanceRegistry
                ?? throw new ArgumentNullException(nameof(instanceRegistry));
            _progressNotifications = progressNotifications
                ?? throw new ArgumentNullException(nameof(progressNotifications));
            _sessions = sessions;
        }

        internal async Task<bool> ReturnAsync(
            EnhancedClientSession session,
            DungeonRunIdentity runIdentity,
            DungeonRunEndReason reason = DungeonRunEndReason.ReturnToTown)
        {
            if (!await DungeonRunLifecycle.EndRunAsync(
                    session,
                    reason,
                    runIdentity,
                    _instanceRegistry))
            {
                return false;
            }
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return false;

            session.Player.UserState = 0x00;
            // 副本结算回城：UserState 归位(0x00)，向同频道把 self 加为好友的人推 USERINFO
            // 刷新好友面板状态图标（否则一直显示"繁忙"直到下线重登）。
            if (_sessions != null)
            {
                try
                {
                    await DfoServer.Game.Friends.UnitedFriendSystem
                        .NotifyUserStateChanged(session, _sessions);
                }
                catch (Exception stateEx)
                {
                    FileLogger.Log(
                        $"[DungeonTownReturn] NotifyUserStateChanged 失败: {stateEx.Message}");
                }
            }
            var snapshot = TownAreaNotificationBuilder.CreateCurrentSnapshot(
                session.Player);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0003,
                EnterSelectDungeonStateBuilder.BuildUserState(session.Player)));
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return true;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0017,
                TownAreaNotificationBuilder.BuildUserArea(snapshot)));
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return true;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0018,
                TownAreaNotificationBuilder.BuildAreaUsers(snapshot)));
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return true;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x00CA,
                new byte[] { 0x00 }));
            if (!DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return true;
            await _progressNotifications.SendUserInfoSubtype0Broadcast(session);
            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"ReturnToVillage: town state + subtype0 sent " +
                $"run={runIdentity.RunId}/{runIdentity.RunGeneration}");
            return true;
        }
    }
}
