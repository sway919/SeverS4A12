using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Town;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class TownHandler
    {
        private static readonly TimeSpan PositionPersistThrottle = TimeSpan.FromSeconds(5);

        private readonly struct TownProjectionGuard
        {
            private TownProjectionGuard(
                DungeonRunIdentity endedRun,
                DungeonSelectionContext selection)
            {
                EndedRun = endedRun;
                Selection = selection;
            }

            internal DungeonRunIdentity EndedRun { get; }
            internal DungeonSelectionContext Selection { get; }

            internal static TownProjectionGuard ForEndedRun(
                DungeonRunIdentity identity) =>
                new TownProjectionGuard(identity, null);

            internal static TownProjectionGuard ForSelection(
                DungeonSelectionContext selection) =>
                new TownProjectionGuard(default(DungeonRunIdentity), selection);
        }

        private enum TownMoveResult
        {
            Rejected = 0,
            ProjectionFailed = 1,
            Succeeded = 2,
        }

        private readonly ICharacterRepository _characterRepository;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly GrowthCapsuleSyncService _growthCapsule;
        private readonly SqliteSubtype0FieldsRepository _subtype0Repository;
        private readonly Game.SelectCharacter.SqliteSelectCharacterDataSource _selectDataSource;
        private readonly Game.Party.PartyManager _partyManager;   // 可空: 副本退出/回城时把队员一起拉回城(跟随退出)
        // 可空: 会话目录(charId→session)。同屏区域查询与队员定位共用这一份注册表, 不另设区域广播器。
        private readonly Game.Session.ISessionDirectory _sessions;
        private readonly DungeonInstanceRegistry _dungeonInstances;
        private readonly CharacterTransitionCoordinator _characterTransitions;
        private readonly object _areaArrivalGatesLock = new object();
        private readonly Dictionary<long, AreaArrivalGateEntry>
            _areaArrivalGates =
                new Dictionary<long, AreaArrivalGateEntry>();

        private readonly InventoryRefreshSender _refresh;

        public string ProtocolName => "GameProtocol";

        public TownHandler(
            ICharacterRepository characterRepository,
            Game.SelectCharacter.SqliteSelectCharacterDataSource selectDataSource = null,
            Game.Party.PartyManager partyManager = null,
            Game.Session.ISessionDirectory sessions = null,
            InventoryRefreshSender refresh = null)
            : this(
                characterRepository,
                selectDataSource,
                partyManager,
                sessions,
                refresh,
                dungeonInstances: null,
                characterTransitions: null)
        {
        }

        internal TownHandler(
            ICharacterRepository characterRepository,
            Game.SelectCharacter.SqliteSelectCharacterDataSource selectDataSource,
            Game.Party.PartyManager partyManager,
            Game.Session.ISessionDirectory sessions,
            InventoryRefreshSender refresh,
            DungeonInstanceRegistry dungeonInstances,
            CharacterTransitionCoordinator characterTransitions = null)
        {
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            _honorLevel = new HonorLevelSyncService(_characterRepository);
            _growthCapsule = new GrowthCapsuleSyncService(_characterRepository);
            _subtype0Repository = new SqliteSubtype0FieldsRepository(
                Infrastructure.ServerPaths.DatabasePath,
                Infrastructure.ServerPaths.SchemaFilePath);
            _refresh = refresh;
            _selectDataSource = selectDataSource;  // 可空: 用于同屏推送他人完整 USERINFO(subtype1, 让客户端认其可组队邀请)
            _partyManager = partyManager;          // 可空: 组队副本收尾 fan-out(跟随退出); 与副本共享同一 PartyManager
            _sessions = sessions;                  // 可空: 未注入时退化为单人(不广播)
            _dungeonInstances = dungeonInstances;
            _characterTransitions = characterTransitions;
        }

        // 构建某在线会话玩家的【完整 USERINFO subtype1】(0x0002 occ1, ~1458B: 属性/装备/技能)。
        // 同屏时仅推 subtype0(精简外观)客户端能渲染但判定"对方不在城镇/不可邀请"; self 进游戏收的是 subtype0+subtype1
        // 两份, 故给同屏他人补 subtype1。id 头(bytes 3-4)由 CharacterId 改写为 UserId 以对齐城镇名册。
        internal byte[] BuildFullUserInfoPacket(EnhancedClientSession s)
        {
            if (_selectDataSource == null || s?.Player == null || s.Player.CharacterId <= 0)
                return null;
            try
            {
                var snap = _selectDataSource.Load(s.Player.CharacterId, s.Account?.AccountId ?? 1);
                if (snap?.CharacterRecord == null || snap.InitializationSnapshot?.UserInfoAddition == null)
                    return null;
                if (!new Network.Builders.UserInfoBodyBuilder().TryBuild(snap, 1, out var fullBody) || fullBody == null || fullBody.Length < 5)
                    return null;
                BitConverter.GetBytes(s.Player.UserId).CopyTo(fullBody, 3);
                return GamePacketEnvelopeBuilder.Build(0x00, 0x0002, fullBody);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] BuildFullUserInfoPacket cid={s.Player.CharacterId} 失败: {ex.Message}");
                return null;
            }
        }

        public void PersistPosition(EnhancedClientSession session, bool forceImmediate, string source)
        {
            try
            {
                if (session?.Player == null || session.Player.CharacterId <= 0)
                    return;

                var now = DateTime.UtcNow;
                if (!forceImmediate)
                {
                    if (now - session.Player.LastPositionPersistAt < PositionPersistThrottle)
                        return;
                }

                var gate = GameWorld.Town.GetCeraRoomInfo(session.Player.CurTownId);
                if (gate.Town <= 0)
                    return;

                _characterRepository.UpdatePosition(
                    session.Player.CharacterId,
                    session.Player.CurTownId,
                    session.Player.CurAreaId,
                    session.Player.CurPosX,
                    session.Player.CurPosY,
                    session.Player.CurDirection,
                    session.Player.CurAreaState);
                session.Player.LastPositionPersistAt = now;
                FileLogger.Log($"[{ProtocolName}] Persisted position ({source}) character_id={session.Player.CharacterId} town={session.Player.CurTownId} area={session.Player.CurAreaId} pos=({session.Player.CurPosX},{session.Player.CurPosY})");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] Persist position ({source}) failed: {ex.Message}");
            }
        }

        public async Task Handle_ENUM_CMDPACKET_SET_USER_POSITION(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 4) return;
            var gotoPosX = BitConverter.ToInt16(body, 0);
            var gotoPosY = BitConverter.ToInt16(body, 2);
            session.Player.CurPosX = gotoPosX;
            session.Player.CurPosY = gotoPosY;
            PersistPosition(session, forceImmediate: false, source: "set_user_position");

            // 联机同屏: 把移动广播给同区域其它玩家(USER_POSITION 0x0016)。
            if (_sessions != null && session.Player.CharacterId > 0)
            {
                var snap = TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player);
                await _sessions.BroadcastToAreaAsync(
                    session.Player.CurTownId, session.Player.CurAreaId, session.Player.CharacterId,
                    GamePacketEnvelopeBuilder.Build(0x00, 0x0016, TownAreaNotificationBuilder.BuildUserPosition(snap)));
            }
        }

        public async Task Handle_ENUM_CMDPACKET_SET_USER_AREA(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (_characterTransitions != null)
            {
                await _characterTransitions.RunIfCurrentAsync(
                    session,
                    () => SetUserAreaCoreAsync(
                        session,
                        body,
                        default(TownProjectionGuard)));
                return;
            }

            await SetUserAreaCoreAsync(
                session,
                body,
                default(TownProjectionGuard));
        }

        private async Task<TownMoveResult> SetUserAreaCoreAsync(
            EnhancedClientSession session,
            byte[] body,
            TownProjectionGuard projectionGuard)
        {
            if (body == null || body.Length < 6)
                return TownMoveResult.Rejected;
            if (!CanContinueTownTransition(session, projectionGuard))
                return TownMoveResult.Rejected;

            return await MoveToTownAreaWithinTransitionAsync(
                session,
                body[0],
                body[1],
                BitConverter.ToInt16(body, 2),
                BitConverter.ToInt16(body, 4),
                body.Length >= 7 ? body[6] : (byte)0x05,
                0x03,
                "set_user_area",
                projectionGuard);
        }

        private async Task<TownMoveResult> MoveToTownAreaWithinTransitionAsync(
            EnhancedClientSession session,
            byte townId,
            byte areaId,
            short posX,
            short posY,
            byte direction,
            byte areaState,
            string persistSource,
            TownProjectionGuard projectionGuard)
        {
            var player = session?.Player;
            if (player == null ||
                !CanContinueTownTransition(session, projectionGuard))
            {
                return TownMoveResult.Rejected;
            }

            var oldTownId = player.CurTownId;
            var oldAreaId = player.CurAreaId;
            using (await AcquireAreaTransitionGatesAsync(
                       session.ListenerPort,
                       oldTownId,
                       oldAreaId,
                       townId,
                       areaId))
            {
                if (!CanContinueTownTransition(
                        session, projectionGuard) ||
                    player.CurTownId != oldTownId ||
                    player.CurAreaId != oldAreaId)
                {
                    return TownMoveResult.Rejected;
                }

                var wasPresenceReady = player.TownPresenceReady;
                player.TownPresenceReady = false;
                player.CurTownId = townId;
                player.CurAreaId = areaId;
                player.CurPosX = posX;
                player.CurPosY = posY;
                player.CurDirection = direction;
                player.CurAreaState = areaState;
                var destination =
                    TownAreaNotificationBuilder.CreateCurrentSnapshot(player);

                if (wasPresenceReady &&
                    HasAreaChanged(
                        oldTownId,
                        oldAreaId,
                        townId,
                        areaId))
                {
                    await NotifyAreaTransitionAsync(
                        session,
                        oldTownId,
                        oldAreaId,
                        destination);
                }

                if (!CanPublishTownArrival(
                        session,
                        townId,
                        areaId,
                        projectionGuard))
                {
                    CloseFailedTownArrival(
                        session,
                        persistSource);
                    return TownMoveResult.ProjectionFailed;
                }

                if (!await BroadcastAreaRosterAsync(
                        session,
                        destination,
                        projectionGuard))
                {
                    CloseFailedTownArrival(
                        session,
                        persistSource);
                    return TownMoveResult.ProjectionFailed;
                }

                PersistPosition(
                    session,
                    forceImmediate: true,
                    source: persistSource);
                return TownMoveResult.Succeeded;
            }
        }

        private void CloseFailedTownArrival(
            EnhancedClientSession session,
            string source)
        {
            FileLogger.Log(
                $"[{ProtocolName}] Town arrival publication failed; " +
                $"closing partial session cid=" +
                $"{session?.Player?.CharacterId ?? 0} source={source}");
            try
            {
                session?.Close();
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] Close partial town session failed: " +
                    ex.Message);
            }
        }

        private async Task NotifyAreaTransitionAsync(
            EnhancedClientSession session,
            byte oldTownId,
            byte oldAreaId,
            TownUserSnapshot destination)
        {
            if (_sessions == null ||
                session?.Player == null ||
                session.Player.CharacterId <= 0 ||
                !Game.Session.SessionDirectory.IsShareableTownArea(
                    oldTownId,
                    oldAreaId))
            {
                return;
            }

            var observers = _sessions.GetSessionsInArea(
                oldTownId,
                oldAreaId,
                session.Player.CharacterId,
                session.ListenerPort);
            if (observers.Count == 0)
                return;

            var packet = BuildAreaTransitionPacket(destination);
            var targets = new List<EnhancedClientSession>(
                observers.Count);
            var sends = new List<Task<bool>>(observers.Count);
            foreach (var observer in observers)
            {
                if (!IsCurrentTownObserver(
                        observer,
                        oldTownId,
                        oldAreaId,
                        session.ListenerPort))
                {
                    continue;
                }

                targets.Add(observer);
                sends.Add(SessionDirectory.TrySendBestEffortAsync(
                    cancellationToken => observer.SendPacketAsync(
                        packet,
                        cancellationToken),
                    $"characterId=" +
                    $"{observer.Player?.CharacterId ?? 0}"));
            }

            if (sends.Count == 0)
                return;
            await Task.WhenAll(sends);
            for (var index = 0; index < sends.Count; index++)
            {
                if (!sends[index].Result)
                    CloseFailedTownObserver(
                        targets[index],
                        "old-area-transition");
            }
        }

        private void CloseFailedTownObserver(
            EnhancedClientSession observer,
            string source)
        {
            FileLogger.Log(
                $"[{ProtocolName}] Closing town observer after partial " +
                $"projection cid=" +
                $"{observer?.Player?.CharacterId ?? 0} source={source}");
            try
            {
                observer?.Close();
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] Close partial observer failed: " +
                    ex.Message);
            }
        }

        internal static bool HasAreaChanged(
            byte oldTownId,
            byte oldAreaId,
            byte newTownId,
            byte newAreaId) =>
            oldTownId != newTownId || oldAreaId != newAreaId;

        internal static byte[] BuildAreaTransitionPacket(
            TownUserSnapshot destination) =>
            GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0017,
                TownAreaNotificationBuilder.BuildUserArea(destination));

        // 0x0017 is the single-user arrival increment. 0x0018 is a complete
        // roster and must only be sent to the arriving client; sending a
        // one-user 0x0018 to existing observers erases their local roster.
        internal static byte[][] BuildCoPresenceInserts(
            TownUserSnapshot snapshot) =>
            new[] { BuildAreaTransitionPacket(snapshot) };

        /// <summary>
        /// 城镇同屏核心: 收集同区域全部会话, 给每个人下发含全体的 AREA_USERS(0x0018)。
        /// _sessions 为空(单人/未注入)时退化为只发自己 —— 与既有单机行为等价。
        /// </summary>
        private async Task<bool> BroadcastAreaRosterAsync(
            EnhancedClientSession session,
            TownUserSnapshot selfSnapshot,
            TownProjectionGuard projectionGuard = default(TownProjectionGuard))
        {
            if (!CanPublishTownArrival(
                    session,
                    selfSnapshot.TownId,
                    selfSnapshot.AreaId,
                    projectionGuard))
            {
                return false;
            }
            var townId = session.Player.CurTownId;
            var areaId = session.Player.CurAreaId;

            IReadOnlyList<EnhancedClientSession> others =
                _sessions != null &&
                Game.Session.SessionDirectory.IsShareableTownArea(
                    townId, areaId)
                    ? _sessions.GetSessionsInArea(
                        townId, areaId, session.Player.CharacterId,
                        session.ListenerPort)
                    : System.Array.Empty<EnhancedClientSession>();

            var targets = new List<(
                EnhancedClientSession Session,
                TownUserSnapshot Snapshot,
                byte[] Appearance,
                byte[] FullInfo)>(others.Count);
            foreach (var other in others)
            {
                if (!IsCurrentTownObserver(
                        other,
                        townId,
                        areaId,
                        session.ListenerPort))
                {
                    continue;
                }

                targets.Add((
                    other,
                    TownAreaNotificationBuilder.CreateCurrentSnapshot(
                        other.Player),
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x0002,
                        Game.Appearance.AppearanceService.BuildNoti2Body(
                            other.Player)),
                    BuildFullUserInfoPacket(other)));
            }

            FileLogger.Log(
                $"[{ProtocolName}] AREA co-presence: " +
                $"uid={session.Player.UserId} town={townId} " +
                $"area={areaId} others={targets.Count}");

            // 全体名册(自己 + 其它人)。
            var roster = new List<TownUserSnapshot>(targets.Count + 1)
            {
                selfSnapshot
            };
            foreach (var target in targets)
                roster.Add(target.Snapshot);

            // 真机实测(逆向+抓包结论): 只发 0x17/0x18 客户端既不生成他人角色对象、也不主动拉外观。
            // self 能渲染是因为进游戏时收了【完整外观】(USERINFO 0x0002 含形象)。故照"自身入场先有外观后有位置"
            // 主动 PUSH: 给新人为【每个已在场玩家】先推一份 USERINFO(0x0002 外观)、再发 0x0017(定位/生成), 最后补 0x0018 名册。
            // 给新人: 每个已在场玩家 subtype0(精简外观, 生成对象)+ subtype1(完整属性/装备/技能, 让客户端认其可组队邀请)+ 0x0017 定位。
            async Task SendRosterToNewcomerAsync(
                CancellationToken cancellationToken)
            {
                await session.SendPacketAsync(
                    BuildAreaTransitionPacket(selfSnapshot),
                    cancellationToken);
                foreach (var target in targets)
                {
                    await session.SendPacketAsync(
                        target.Appearance,
                        cancellationToken);
                    if (target.FullInfo != null)
                    {
                        await session.SendPacketAsync(
                            target.FullInfo,
                            cancellationToken);
                    }
                    foreach (var packet in
                             BuildCoPresenceInserts(target.Snapshot))
                    {
                        await session.SendPacketAsync(
                            packet,
                            cancellationToken);
                    }
                }

                await session.SendPacketAsync(
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x0018,
                        TownAreaNotificationBuilder.BuildAreaUsers(
                            townId, areaId, roster)),
                    cancellationToken);
            }

            // 给每个已在场玩家推【新人】的 subtype0 + subtype1 + 0x0017(insert), 让他们生成并认可新人。
            var selfAppearance = GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0002,
                Game.Appearance.AppearanceService.BuildNoti2Body(
                    session.Player));
            var selfFull = BuildFullUserInfoPacket(session);
            var selfAreas = BuildCoPresenceInserts(selfSnapshot);
            var existingTargets = new List<(
                string Target,
                Func<CancellationToken, Task> Send)>(targets.Count);
            foreach (var target in targets)
            {
                var captured = target;
                existingTargets.Add((
                    $"characterId={captured.Session.Player?.CharacterId ?? 0}",
                    async cancellationToken =>
                    {
                        if (!IsCurrentTownObserver(
                                captured.Session,
                                townId,
                                areaId,
                                session.ListenerPort))
                        {
                            return;
                        }

                        await captured.Session.SendPacketAsync(
                            selfAppearance,
                            cancellationToken);
                        if (selfFull != null)
                        {
                            await captured.Session.SendPacketAsync(
                                selfFull,
                                cancellationToken);
                        }
                        foreach (var packet in selfAreas)
                        {
                            await captured.Session.SendPacketAsync(
                                packet,
                                cancellationToken);
                        }
                    }));
            }

            return await FanOutAreaRosterAsync(
                SendRosterToNewcomerAsync,
                $"characterId={session.Player.CharacterId}",
                existingTargets,
                () =>
                {
                    if (!CanPublishTownArrival(
                            session,
                            townId,
                            areaId,
                            projectionGuard))
                    {
                        return false;
                    }

                    // Publish only after the newcomer owns a complete roster.
                    // The area gate makes this commit visible to the next arrival.
                    session.Player.TownPresenceReady = true;
                    return true;
                },
                failedIndex => CloseFailedTownObserver(
                    targets[failedIndex].Session,
                    "destination-arrival"));
        }

        /// <summary>联机同屏: 断线/离开区域时通知同区域其它玩家移除该分身(USER_LEAVE 0x0006)。</summary>
        public async Task NotifyLeaveAsync(EnhancedClientSession session)
        {
            if (session?.Player == null ||
                session.Player.CharacterId <= 0)
                return;

            var townId = session.Player.CurTownId;
            var areaId = session.Player.CurAreaId;
            session.Player.TownPresenceReady = false;
            if (_sessions == null ||
                !Game.Session.SessionDirectory.IsShareableTownArea(
                    townId, areaId))
            {
                return;
            }

            using (await AcquireAreaArrivalGateAsync(
                       session.ListenerPort,
                       townId,
                       areaId))
            {
                await _sessions.BroadcastToAreaAsync(
                    townId,
                    areaId,
                    session.Player.CharacterId,
                    BuildUserLeavePacket(session.Player.UserId),
                    session.ListenerPort);
            }
        }

        public async Task Handle_ENUM_CMDPACKET_FINISH_LOADING(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0025, CommonPacketBodyBuilder.BuildSuccessAck()));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x001E, FinishLoadingBuilder.BuildNotification()));
            await _growthCapsule.SendExpProgressAsync(session, "finish-loading");
        }

        public async Task Handle_ENUM_CMDPACKET_TELEPORT(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (_characterTransitions != null)
            {
                await _characterTransitions.RunIfCurrentAsync(
                    session,
                    () => HandleTeleportWithinTransitionAsync(
                        session, header, body));
                return;
            }

            await HandleTeleportWithinTransitionAsync(
                session, header, body);
        }

        private async Task HandleTeleportWithinTransitionAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!IsTownTeleportEligible(session?.Player) ||
                body == null || body.Length < 8)
                return;

            var type = BitConverter.ToInt16(body, 0);
            var itemCode = BitConverter.ToInt32(body, 2);
            if (itemCode != 0x0027AC4E)
                return;

            var townId = body[7];
            var ceraRoomInfo = Town.GetCeraRoomInfo(townId);
            if (ceraRoomInfo.Town <= 0 ||
                !Town.ContainsArea(
                    ceraRoomInfo.Town,
                    ceraRoomInfo.Area))
            {
                return;
            }

            var (cid, _) = InventoryHandler.ResolveOwner(session);
            if (!InventoryContext.TryGetLease(
                    cid, out var lease) ||
                !lease.IsOwnedBy(session.SessionId))
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TELEPORT rejected: online " +
                    $"inventory missing cid={cid}");
                return;
            }

            ItemCore consumedSnapshot = null;
            InventoryMainItemConsumeResult consumeResult = null;
            lock (lease.SyncRoot)
            {
                foreach (var pair in lease.Inventory.GetItems(
                             InventoryListType.Main))
                {
                    if (pair.Value == null ||
                        pair.Value.ItemId != itemCode)
                    {
                        continue;
                    }

                    consumedSnapshot = pair.Value.Copy();
                    break;
                }

                if (consumedSnapshot == null ||
                    !lease.Inventory.TryConsumeMainItem(
                        itemCode,
                        1,
                        out consumeResult) ||
                    !consumeResult.Success)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] TELEPORT rejected: " +
                        $"required item missing cid={cid} " +
                        $"item={itemCode}");
                    return;
                }
            }

            var moveResult = await MoveToTownAreaWithinTransitionAsync(
                    session,
                    ceraRoomInfo.Town,
                    ceraRoomInfo.Area,
                    ceraRoomInfo.X,
                    ceraRoomInfo.Y,
                    0,
                    3,
                    "teleport",
                    default(TownProjectionGuard));
            if (moveResult != TownMoveResult.Succeeded)
            {
                if (moveResult == TownMoveResult.Rejected &&
                    !TryRestoreConsumedTeleportItem(
                        lease,
                        session.SessionId,
                        consumeResult,
                        consumedSnapshot))
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] TELEPORT rollback failed " +
                        $"cid={cid} slot={consumeResult.SlotIndex}");
                    session.Close();
                }
                else if (moveResult ==
                         TownMoveResult.ProjectionFailed)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] TELEPORT projection failed " +
                        $"after authoritative move; consumed item is " +
                        $"retained cid={cid} " +
                        $"slot={consumeResult.SlotIndex}");
                }
                return;
            }

            FileLogger.Log(
                $"[{ProtocolName}] TELEPORT: consumed 1x " +
                $"teleport item slot={consumeResult.SlotIndex} " +
                $"remaining={consumeResult.RemainingCount}");
            if (_refresh != null)
            {
                await _refresh.SendUpdateItemList(
                    session,
                    InventoryListType.Main,
                    consumeResult.SlotIndex);
            }
            await session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x00ED,
                    TeleportPacketBuilder.BuildTeleportResponse(
                        type, itemCode)));
        }

        private static bool TryRestoreConsumedTeleportItem(
            InventoryLease lease,
            Guid sessionId,
            InventoryMainItemConsumeResult consumeResult,
            ItemCore snapshot)
        {
            if (lease == null ||
                consumeResult == null ||
                snapshot == null ||
                !lease.IsOwnedBy(sessionId))
            {
                return false;
            }

            lock (lease.SyncRoot)
            {
                var current = lease.Inventory.GetItem(
                    InventoryListType.Main,
                    consumeResult.SlotIndex);
                var isExpectedPostConsumeState =
                    consumeResult.RemainingCount == 0
                        ? current == null
                        : current != null &&
                          current.ItemId == snapshot.ItemId &&
                          current.Count == consumeResult.RemainingCount;
                return isExpectedPostConsumeState &&
                       lease.Inventory.SetItem(
                           InventoryListType.Main,
                           consumeResult.SlotIndex,
                           snapshot);
            }
        }

        public async Task Handle_ENUM_CMDPACKET_PARTY_TELEPORT(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (_characterTransitions != null)
            {
                await _characterTransitions.RunIfCurrentAsync(
                    session,
                    () => HandlePartyTeleportWithinTransitionAsync(
                        session, header, body));
                return;
            }

            await HandlePartyTeleportWithinTransitionAsync(
                session, header, body);
        }

        private async Task HandlePartyTeleportWithinTransitionAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!IsTownTeleportEligible(session?.Player) ||
                !PartyTeleportRequest.TryParse(
                    body, out var request) ||
                !Town.ContainsArea(
                    request.TownId, request.AreaId))
            {
                await RejectPartyTeleportAsync(
                    session, header, "invalid request");
                return;
            }

            var party = _partyManager?.GetPartyByUser(
                session.Player.UserId);
            if (party != null && party.Count > 1)
            {
                // Multi-member teleport requires PARTY_TELEPORT_STATUS and
                // PARTY_TELEPORT_CONFIRM. Do not move only the requester.
                await RejectPartyTeleportAsync(
                    session,
                    header,
                    $"party confirmation required party={party.PartyId}");
                return;
            }

            if (await MoveToTownAreaWithinTransitionAsync(
                    session,
                    request.TownId,
                    request.AreaId,
                    request.X,
                    request.Y,
                    request.Direction,
                    0x03,
                    "party_teleport",
                    default(TownProjectionGuard)) !=
                TownMoveResult.Succeeded)
            {
                return;
            }

            await session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x01,
                    header.type,
                    CommonPacketBodyBuilder.BuildSuccessAck()));

            FileLogger.Log(
                $"[{ProtocolName}] PARTY_TELEPORT: " +
                $"cid={session.Player.CharacterId} " +
                $"town={request.TownId} area={request.AreaId} " +
                $"pos=({request.X},{request.Y}) " +
                $"direction={request.Direction}");
        }

        private async Task RejectPartyTeleportAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            string reason)
        {
            FileLogger.Log(
                $"[{ProtocolName}] PARTY_TELEPORT rejected: " +
                $"cid={session?.Player?.CharacterId ?? 0} {reason}");
            if (session == null)
                return;

            await session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x01,
                    header.type,
                    CommonPacketBodyBuilder.BuildCmdError(0x04)));
        }

        public async Task Handle_ENUM_CMDPACKET_GIVEUP_GAME(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (_characterTransitions == null)
            {
                await HandleGiveupWithoutCoordinatorAsync(
                    session,
                    header,
                    body);
                return;
            }

            await HandleGiveupWithGenerationLeaseAsync(
                session,
                header);
        }

        private async Task HandleGiveupWithGenerationLeaseAsync(
            EnhancedClientSession session,
            GamePacketHeader header)
        {
            DungeonRunIdentity sourceRunIdentity;
            using (var lease = await _characterTransitions
                       .AcquireIfCurrentAsync(session))
            {
                if (lease == null)
                    return;

                var sourceRun = session?.Player?.CurrentRun;
                if (sourceRun == null)
                {
                    var selection =
                        session?.Player?.CurrentDungeonSelection;
                    if (selection == null ||
                        !selection.TryBeginReturn())
                    {
                        FileLogger.Log(
                            $"[{ProtocolName}] RETURN_TO_TOWN rejected " +
                            $"without run: type=0x{header.type:X4} " +
                            $"cid={session?.Player?.CharacterId ?? 0} " +
                            $"selection=" +
                            $"{(selection?.SelectionId ?? 0)}");
                        return;
                    }

                    var selectionGuard =
                        TownProjectionGuard.ForSelection(selection);
                    try
                    {
                        if (!await ReturnSelectionToTownAsync(
                                session,
                                selection,
                                selectionGuard))
                        {
                            selection.CancelReturn();
                            return;
                        }
                        await SendTownAccountStateAsync(
                            session,
                            "leave-dungeon-selection",
                            selectionGuard);
                        if (!CanContinueTownProjection(
                                session,
                                selectionGuard))
                        {
                            return;
                        }
                        await session.SendPacketAsync(
                            BuildReturnToTownSuccessPacket(
                                header.type));
                        if (CanContinueTownProjection(
                                session,
                                selectionGuard))
                        {
                            session.Player.CompleteDungeonSelection(
                                selection);
                        }
                        FileLogger.Log(
                            $"[{ProtocolName}] RETURN_TO_TOWN from " +
                            $"selection: type=0x{header.type:X4} " +
                            $"cid={session.Player.CharacterId} " +
                            $"selection={selection.SelectionId}");
                    }
                    catch
                    {
                        if (session?.Player?.IsCurrentDungeonSelection(
                                selection) == true)
                        {
                            selection.CancelReturn();
                        }
                        throw;
                    }
                    return;
                }

                sourceRunIdentity = sourceRun.CaptureIdentity();
                var runGuard =
                    TownProjectionGuard.ForEndedRun(sourceRunIdentity);
                if (!await ReturnSelfToTownAsync(
                        session,
                        header,
                        sourceRunIdentity,
                        sourceRun.TownReturnAnchor))
                {
                    return;
                }
                await SendTownAccountStateAsync(
                    session,
                    "giveup-game",
                    runGuard);
                if (!CanContinueTownProjection(session, runGuard))
                    return;
            }

            var completedRunGuard =
                TownProjectionGuard.ForEndedRun(sourceRunIdentity);
            if (header.type == 0x0084)
            {
                await TryFanOutLeaderReturnToTownCoordinatedAsync(
                    session,
                    header,
                    sourceRunIdentity);
            }
            else
            {
                FileLogger.Log(
                    $"[{ProtocolName}] GIVEUP_GAME" +
                    $"(type=0x{header.type:X2}): cid=" +
                    $"{session.Player?.CharacterId} returns alone");
            }

            using (var ackLease = await _characterTransitions
                       .AcquireIfCurrentAsync(session))
            {
                if (ackLease == null ||
                    !CanContinueTownProjection(
                        session,
                        completedRunGuard))
                {
                    return;
                }
                await session.SendPacketAsync(
                    BuildReturnToTownSuccessPacket(header.type));
            }
        }

        private async Task HandleGiveupWithoutCoordinatorAsync(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var sourceRun = session?.Player?.CurrentRun;
            if (sourceRun == null)
            {
                var selection = session?.Player?.CurrentDungeonSelection;
                if (selection == null || !selection.TryBeginReturn())
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] RETURN_TO_TOWN rejected without run: " +
                        $"type=0x{header.type:X4} cid={session?.Player?.CharacterId ?? 0} " +
                        $"selection={(selection?.SelectionId ?? 0)}");
                    return;
                }

                var selectionGuard = TownProjectionGuard.ForSelection(selection);
                try
                {
                    if (!await ReturnSelectionToTownAsync(
                            session,
                            selection,
                            selectionGuard))
                    {
                        selection.CancelReturn();
                        return;
                    }
                    await SendTownAccountStateAsync(
                        session,
                        "leave-dungeon-selection",
                        selectionGuard);
                    if (!CanContinueTownProjection(session, selectionGuard))
                        return;
                    await session.SendPacketAsync(
                        BuildReturnToTownSuccessPacket(header.type));
                    if (CanContinueTownProjection(session, selectionGuard))
                        session.Player.CompleteDungeonSelection(selection);
                    FileLogger.Log(
                        $"[{ProtocolName}] RETURN_TO_TOWN from selection: " +
                        $"type=0x{header.type:X4} cid={session.Player.CharacterId} " +
                        $"selection={selection.SelectionId}");
                }
                catch
                {
                    if (session?.Player?.IsCurrentDungeonSelection(selection) == true)
                        selection.CancelReturn();
                    throw;
                }
                return;
            }

            var sourceRunIdentity = sourceRun.CaptureIdentity();
            var runGuard = TownProjectionGuard.ForEndedRun(sourceRunIdentity);
            if (!await ReturnSelfToTownAsync(
                    session,
                    header,
                    sourceRunIdentity,
                    sourceRun.TownReturnAnchor))
            {
                return;
            }
            await SendTownAccountStateAsync(
                session,
                "giveup-game",
                runGuard);
            // ★跟随退出(item17)只在【通关回城 BACK_2_VILLAGE 0x84】触发: 副本结束队长回城 → 队员跟随。
            //   ⚠️【放弃 GIVEUP_GAME 0x2A = 未完成中途退出】绝不 fan-out:
            //     放弃者独自回城、【留队】; 其余队员【继续留在副本、留队】(真机确认的正确语义)。
            //   0x2A/0x84 同路由到本 handler, 靠 header.type 区分。
            if (header.type == 0x0084)
                await TryFanOutLeaderReturnToTownAsync(
                    session,
                    header,
                    sourceRunIdentity);
            else
                FileLogger.Log($"[{ProtocolName}] GIVEUP_GAME(type=0x{header.type:X2}): 未完成放弃退出, cid={session.Player?.CharacterId} 独自回城留队, 不拉队员(其余留本)");

            if (!CanContinueTownProjection(session, runGuard))
                return;
            await session.SendPacketAsync(
                BuildReturnToTownSuccessPacket(header.type));
        }

        // 把【单个会话】自己拉回城镇(EndRun + 城镇区域同步)。队长/队员复用同一序列。
        private async Task<bool> ReturnSelfToTownAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            DfoServer.Game.Dungeon.DungeonRunIdentity runIdentity,
            DungeonTownReturnAnchor returnAnchor)
        {
            if (!await Dungeon.DungeonRunLifecycle.EndRunAsync(
                    session,
                    DfoServer.Game.Dungeon.DungeonRunEndReason.ReturnToTown,
                    runIdentity,
                    _dungeonInstances))
            {
                return false;
            }
            var projectionGuard = TownProjectionGuard.ForEndedRun(runIdentity);
            if (!CanContinueTownProjection(session, projectionGuard))
            {
                return false;
            }
            session.Player.TownPresenceReady = false;
            ApplyTownReturnAnchor(session.Player, returnAnchor);
            session.Player.UserState = 0x00;
            var moveResult = await SetUserAreaCoreAsync(
                session,
                BuildTownAreaProjectionBody(session.Player),
                projectionGuard);
            return moveResult == TownMoveResult.Succeeded &&
                   CanContinueTownProjection(
                session,
                projectionGuard);
        }

        private async Task<bool> ReturnSelectionToTownAsync(
            EnhancedClientSession session,
            DungeonSelectionContext selection,
            TownProjectionGuard projectionGuard)
        {
            if (!CanContinueTownProjection(session, projectionGuard))
                return false;

            session.Player.TownPresenceReady = false;
            ApplyTownReturnAnchor(session.Player, selection.ReturnAnchor);
            session.Player.UserState = 0x00;
            var moveResult = await SetUserAreaCoreAsync(
                session,
                BuildTownAreaProjectionBody(session.Player),
                projectionGuard);
            return moveResult == TownMoveResult.Succeeded &&
                   CanContinueTownProjection(session, projectionGuard);
        }

        private static byte[] BuildTownAreaProjectionBody(PlayerContext player)
        {
            var list = new List<byte>();
            list.Add(player.CurTownId);
            list.Add(player.CurAreaId);
            list.AddRange(BitConverter.GetBytes(player.CurPosX));
            list.AddRange(BitConverter.GetBytes(player.CurPosY));
            list.Add(player.CurDirection);
            list.Add(player.CurTownId);
            list.Add(player.CurAreaState);
            list.Add(player.CurAreaId);
            return list.ToArray();
        }

        private static void ApplyTownReturnAnchor(
            PlayerContext player,
            DungeonTownReturnAnchor returnAnchor)
        {
            if (!returnAnchor.IsValid
                && Town.TryGetDungeonGateReturnInfo(
                    player.CurTownId,
                    player.CurAreaId,
                    out var configured))
            {
                returnAnchor = new DungeonTownReturnAnchor(
                    configured.Town,
                    configured.Area,
                    configured.X,
                    configured.Y,
                    player.CurDirection,
                    player.CurAreaState);
            }
            if (!returnAnchor.IsValid)
                return;

            player.CurTownId = returnAnchor.TownId;
            player.CurAreaId = returnAnchor.AreaId;
            player.CurPosX = returnAnchor.X;
            player.CurPosY = returnAnchor.Y;
            player.CurDirection = returnAnchor.Direction;
            player.CurAreaState = returnAnchor.AreaState;
        }

        internal static byte[] BuildReturnToTownSuccessPacket(ushort packetType) =>
            GamePacketEnvelopeBuilder.Build(
                0x01,
                packetType,
                CommonPacketBodyBuilder.BuildSuccessAck());

        // ★组队副本收尾 fan-out(⚠️协议/渲染, 待真机)。仅当【队长】+开 DFO_PARTY_DUNGEON_COOP + 队伍>1:
        //   把每个仍在副本内(CurrentRun!=null)的在线队员也拉回其城镇 → 客户端呈现"跟着队长退出"。
        //   非队长放弃(item16 个人退出)不 fan-out, 只回自己, 其余人继续留本。
        private async Task TryFanOutLeaderReturnToTownAsync(
            EnhancedClientSession leader,
            GamePacketHeader header,
            DfoServer.Game.Dungeon.DungeonRunIdentity leaderRunIdentity)
        {
            var leaderGuard = TownProjectionGuard.ForEndedRun(leaderRunIdentity);
            if (!CanContinueTownProjection(leader, leaderGuard)) return;
            if (Environment.GetEnvironmentVariable("DFO_PARTY_DUNGEON_COOP") == "0") return;
            if (_partyManager == null || _sessions == null || leader?.Player == null) return;

            var leaderUid = (ushort)leader.Player.CharacterId;
            var party = _partyManager.GetPartyByUser(leaderUid);
            if (party == null || party.Count <= 1 || !party.IsLeader(leaderUid)) return;

            FileLogger.Log($"[{ProtocolName}] PARTY_RETURN_VILLAGE: leader={leader.Player.CharacterId} party={party.PartyId} members={party.Count} → fan-out 跟随退出");
            foreach (var m in party.MembersBySlot())
            {
                if (!CanContinueTownProjection(leader, leaderGuard)) return;
                if (m.UserId == leaderUid) continue;
                _sessions.TryGet(m.CharacterId, out var bs);
                if (bs?.Player == null || bs.TcpClient == null || !bs.TcpClient.Connected) continue;
                var memberRun = bs.Player.CurrentRun;
                if (memberRun == null
                    || memberRun.PartyDungeonInstanceId
                        != leaderRunIdentity.PartyDungeonInstanceId)
                {
                    continue;
                }
                var memberRunIdentity = memberRun.CaptureIdentity();
                try
                {
                    if (!await ReturnSelfToTownAsync(
                            bs,
                            header,
                            memberRunIdentity,
                            memberRun.TownReturnAnchor))
                    {
                        continue;
                    }
                    await SendTownAccountStateAsync(
                        bs,
                        "party-return-village",
                        TownProjectionGuard.ForEndedRun(memberRunIdentity));
                    FileLogger.Log($"[{ProtocolName}] PARTY_RETURN_VILLAGE: member cid={bs.Player.CharacterId} 跟随退出→城镇");
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[{ProtocolName}] PARTY_RETURN_VILLAGE: member uid={m.UserId} 跟随异常: {ex.Message}");
                }
            }
        }

        private async Task TryFanOutLeaderReturnToTownCoordinatedAsync(
            EnhancedClientSession leader,
            GamePacketHeader header,
            DungeonRunIdentity leaderRunIdentity)
        {
            var leaderGuard =
                TownProjectionGuard.ForEndedRun(leaderRunIdentity);
            if (!CanContinueTownProjection(leader, leaderGuard) ||
                Environment.GetEnvironmentVariable(
                    "DFO_PARTY_DUNGEON_COOP") == "0" ||
                _partyManager == null ||
                _sessions == null ||
                leader?.Player == null)
            {
                return;
            }

            var leaderUserId = leader.Player.UserId;
            var partyReference =
                _partyManager.GetPartyByUser(leaderUserId);
            var party = partyReference == null
                ? null
                : _partyManager.GetPartySnapshot(
                    partyReference.PartyId);
            var leaderMember = party?.GetMember(leaderUserId);
            if (party == null ||
                party.Count <= 1 ||
                !party.IsLeader(leaderUserId) ||
                leaderMember == null ||
                leaderMember.CharacterId !=
                    leader.Player.CharacterId ||
                leaderMember.SessionId != leader.SessionId)
            {
                return;
            }

            FileLogger.Log(
                $"[{ProtocolName}] PARTY_RETURN_VILLAGE: " +
                $"leader={leader.Player.CharacterId} " +
                $"party={party.PartyId} members={party.Count}");
            foreach (var memberSnapshot in party.MembersBySlot())
            {
                if (!CanContinueTownProjection(
                        leader,
                        leaderGuard))
                {
                    return;
                }
                if (memberSnapshot.UserId == leaderUserId)
                    continue;
                if (!_sessions.TryGet(
                        memberSnapshot.CharacterId,
                        out var member) ||
                    member?.Player == null ||
                    member.SessionId != memberSnapshot.SessionId ||
                    member.TcpClient == null ||
                    !member.TcpClient.Connected)
                {
                    continue;
                }

                try
                {
                    await _characterTransitions.RunIfBothCurrentAsync(
                        leader,
                        member,
                        async () =>
                        {
                            if (!CanContinueTownProjection(
                                    leader,
                                    leaderGuard))
                            {
                                return;
                            }

                            var currentPartyReference =
                                _partyManager.GetPartyByUser(
                                    leaderUserId);
                            var currentParty =
                                currentPartyReference == null
                                    ? null
                                    : _partyManager.GetPartySnapshot(
                                        currentPartyReference.PartyId);
                            var currentLeader = currentParty?
                                .GetMember(leaderUserId);
                            var currentMember = currentParty?
                                .GetMember(memberSnapshot.UserId);
                            if (currentParty == null ||
                                currentParty.PartyId != party.PartyId ||
                                !currentParty.IsLeader(leaderUserId) ||
                                currentLeader == null ||
                                currentLeader.CharacterId !=
                                    leader.Player.CharacterId ||
                                currentLeader.SessionId !=
                                    leader.SessionId ||
                                currentMember == null ||
                                currentMember.CharacterId !=
                                    member.Player.CharacterId ||
                                currentMember.SessionId !=
                                    member.SessionId)
                            {
                                return;
                            }

                            var memberRun = member.Player.CurrentRun;
                            if (memberRun == null ||
                                memberRun.PartyDungeonInstanceId !=
                                    leaderRunIdentity
                                        .PartyDungeonInstanceId)
                            {
                                return;
                            }
                            var memberRunIdentity =
                                memberRun.CaptureIdentity();
                            if (!await ReturnSelfToTownAsync(
                                    member,
                                    header,
                                    memberRunIdentity,
                                    memberRun.TownReturnAnchor))
                            {
                                return;
                            }
                            await SendTownAccountStateAsync(
                                member,
                                "party-return-village",
                                TownProjectionGuard.ForEndedRun(
                                    memberRunIdentity));
                            FileLogger.Log(
                                $"[{ProtocolName}] " +
                                $"PARTY_RETURN_VILLAGE: member " +
                                $"cid={member.Player.CharacterId} " +
                                $"returned to town");
                        });
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] PARTY_RETURN_VILLAGE: " +
                        $"member uid={memberSnapshot.UserId} failed: " +
                        ex.Message);
                }
            }
        }

        private async Task SendTownAccountStateAsync(
            EnhancedClientSession session,
            string reason,
            TownProjectionGuard projectionGuard)
        {
            if (!CanContinueTownProjection(session, projectionGuard))
                return;
            var accountId = session?.Account?.AccountId ?? 0;
            var characterId = session?.Player?.CharacterId ?? 0;
            if (accountId <= 0 || characterId <= 0)
                return;

            var summary = _honorLevel.LoadSummary(accountId);
            await UserInfoBroadcastService.SendSubtype0Async(
                session,
                _characterRepository,
                _subtype0Repository,
                _honorLevel,
                $"{reason} subtype0",
                summary);
            if (!CanContinueTownProjection(session, projectionGuard))
                return;

            await _honorLevel.SendInfoAsync(session, ProtocolName, reason, summary);
        }

        private bool CanContinueTownTransition(
            EnhancedClientSession session,
            TownProjectionGuard projectionGuard)
        {
            if (!CanContinueTownProjection(session, projectionGuard))
                return false;

            return HasControlledProjection(projectionGuard) ||
                   IsTownTeleportEligible(session?.Player);
        }

        private bool IsCurrentSession(
            EnhancedClientSession session)
        {
            if (session?.Player == null)
                return false;
            if (_characterTransitions != null)
                return _characterTransitions.IsCurrent(session);
            if (_sessions == null)
                return true;

            var characterId = session.Player.CharacterId;
            return characterId > 0 &&
                   _sessions.TryGet(
                       characterId, out var current) &&
                   ReferenceEquals(current, session);
        }

        private bool CanContinueTownProjection(
            EnhancedClientSession session,
            TownProjectionGuard projectionGuard)
        {
            if (!IsCurrentSession(session))
                return false;

            if (projectionGuard.Selection != null)
            {
                return projectionGuard.Selection.IsReturning
                    && session?.Player?.IsCurrentDungeonSelection(
                        projectionGuard.Selection) == true;
            }

            return !projectionGuard.EndedRun.IsValid
                || Dungeon.DungeonRunLifecycle.CanProjectTownState(
                    session,
                    projectionGuard.EndedRun);
        }

        internal static byte[] BuildUserLeavePacket(ushort userId)
            => GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0006,
                TownAreaNotificationBuilder.BuildUserLeave(userId));

        internal static bool IsTownArrivalStateEligible(
            PlayerContext player)
            => player != null
               && player.TownPresenceReady
               && player.CharacterId > 0
               && player.CurrentRun == null
               && player.CurrentDungeonSelection == null
               && player.UserState == 0x00
               && !player.DungeonSelectionPending;

        internal static bool IsTownTeleportEligible(
            PlayerContext player) =>
            IsTownArrivalStateEligible(player);
        internal static async Task<bool> FanOutAreaRosterAsync(
            Func<CancellationToken, Task> sendRosterToNewcomer,
            string newcomerTarget,
            IReadOnlyList<(
                string Target,
                Func<CancellationToken, Task> Send)> existingTargets,
            Func<bool> canAnnounceExisting = null,
            Action<int> onExistingSendFailure = null)
        {
            if (!await Game.Session.SessionDirectory.TrySendBestEffortAsync(
                    sendRosterToNewcomer,
                    newcomerTarget))
            {
                return false;
            }

            if (canAnnounceExisting != null &&
                !canAnnounceExisting())
            {
                return false;
            }

            if (existingTargets == null || existingTargets.Count == 0)
                return true;

            var sends = new Task<bool>[existingTargets.Count];
            for (var index = 0; index < existingTargets.Count; index++)
            {
                var target = existingTargets[index];
                sends[index] =
                    Game.Session.SessionDirectory.TrySendBestEffortAsync(
                        target.Send,
                        target.Target);
            }

            await Task.WhenAll(sends);
            if (onExistingSendFailure != null)
            {
                for (var index = 0; index < sends.Length; index++)
                {
                    if (!sends[index].Result)
                        onExistingSendFailure(index);
                }
            }
            return true;
        }

        private bool IsCurrentTownObserver(
            EnhancedClientSession session,
            byte expectedTownId,
            byte expectedAreaId,
            int listenerPort)
        {
            var player = session?.Player;
            if (_sessions == null ||
                player == null ||
                player.CharacterId <= 0 ||
                !_sessions.TryGet(
                    player.CharacterId, out var current) ||
                !ReferenceEquals(current, session) ||
                (listenerPort > 0 &&
                 session.ListenerPort != listenerPort) ||
                !Game.Session.SessionDirectory.IsTownPresence(player) ||
                !IsTownObserverSelectionEligible(player) ||
                player.DungeonSelectionPending ||
                player.CurTownId != expectedTownId ||
                player.CurAreaId != expectedAreaId)
            {
                return false;
            }

            return session.TcpClient != null &&
                   session.TcpClient.Connected;
        }

        internal static bool IsTownObserverSelectionEligible(
            PlayerContext player)
        {
            var selection = player?.CurrentDungeonSelection;
            return selection == null || selection.IsReturning;
        }

        private bool CanPublishTownArrival(
            EnhancedClientSession session,
            byte expectedTownId,
            byte expectedAreaId,
            TownProjectionGuard projectionGuard)
        {
            var player = session?.Player;
            if (!CanContinueTownProjection(session, projectionGuard) ||
                player == null ||
                player.CurTownId != expectedTownId ||
                player.CurAreaId != expectedAreaId)
            {
                return false;
            }

            if (HasControlledProjection(projectionGuard))
                return true;

            return player.CharacterId > 0 &&
                   player.CurrentRun == null &&
                   player.CurrentDungeonSelection == null &&
                   player.UserState == 0x00 &&
                   !player.DungeonSelectionPending;
        }

        private async Task<IDisposable>
            AcquireAreaTransitionGatesAsync(
                int listenerPort,
                byte oldTownId,
                byte oldAreaId,
                byte newTownId,
                byte newAreaId)
        {
            var orderedKeys = OrderAreaTransitionGateKeys(
                listenerPort,
                oldTownId,
                oldAreaId,
                newTownId,
                newAreaId);
            var firstKey = orderedKeys.First;
            var secondKey = orderedKeys.Second;
            var first = await AcquireAreaArrivalGateAsync(firstKey);
            if (firstKey == secondKey)
                return first;

            try
            {
                var second =
                    await AcquireAreaArrivalGateAsync(secondKey);
                return new CombinedAreaArrivalGateLease(first, second);
            }
            catch
            {
                first.Dispose();
                throw;
            }
        }

        private Task<IDisposable> AcquireAreaArrivalGateAsync(
            int listenerPort,
            byte townId,
            byte areaId) =>
            AcquireAreaArrivalGateAsync(
                CreateAreaGateKey(listenerPort, townId, areaId));

        private async Task<IDisposable> AcquireAreaArrivalGateAsync(
            long key)
        {
            AreaArrivalGateEntry entry;
            lock (_areaArrivalGatesLock)
            {
                if (!_areaArrivalGates.TryGetValue(key, out entry))
                {
                    entry = new AreaArrivalGateEntry();
                    _areaArrivalGates.Add(key, entry);
                }
                entry.ReferenceCount++;
            }

            try
            {
                await entry.Semaphore.WaitAsync();
                return new AreaArrivalGateLease(this, key, entry);
            }
            catch
            {
                ReleaseAreaArrivalGateReference(key, entry);
                throw;
            }
        }

        private static long CreateAreaGateKey(
            int listenerPort,
            byte townId,
            byte areaId) =>
            ((long)(uint)listenerPort << 16) |
            ((long)townId << 8) |
            areaId;

        internal static (long First, long Second)
            OrderAreaTransitionGateKeys(
                int listenerPort,
                byte oldTownId,
                byte oldAreaId,
                byte newTownId,
                byte newAreaId)
        {
            var oldKey = CreateAreaGateKey(
                listenerPort, oldTownId, oldAreaId);
            var newKey = CreateAreaGateKey(
                listenerPort, newTownId, newAreaId);
            return oldKey <= newKey
                ? (oldKey, newKey)
                : (newKey, oldKey);
        }

        private void ReleaseAreaArrivalGate(
            long key,
            AreaArrivalGateEntry entry)
        {
            entry.Semaphore.Release();
            ReleaseAreaArrivalGateReference(key, entry);
        }

        private void ReleaseAreaArrivalGateReference(
            long key,
            AreaArrivalGateEntry entry)
        {
            lock (_areaArrivalGatesLock)
            {
                entry.ReferenceCount--;
                if (entry.ReferenceCount == 0 &&
                    _areaArrivalGates.TryGetValue(
                        key, out var current) &&
                    ReferenceEquals(current, entry))
                {
                    _areaArrivalGates.Remove(key);
                    entry.Semaphore.Dispose();
                }
            }
        }

        private static bool HasControlledProjection(
            TownProjectionGuard projectionGuard) =>
            projectionGuard.Selection != null ||
            projectionGuard.EndedRun.IsValid;

        private sealed class AreaArrivalGateEntry
        {
            internal readonly SemaphoreSlim Semaphore =
                new SemaphoreSlim(1, 1);
            internal int ReferenceCount;
        }

        private sealed class AreaArrivalGateLease : IDisposable
        {
            private TownHandler _owner;
            private readonly long _key;
            private readonly AreaArrivalGateEntry _entry;

            internal AreaArrivalGateLease(
                TownHandler owner,
                long key,
                AreaArrivalGateEntry entry)
            {
                _owner = owner;
                _key = key;
                _entry = entry;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.ReleaseAreaArrivalGate(_key, _entry);
            }
        }

        private sealed class CombinedAreaArrivalGateLease : IDisposable
        {
            private IDisposable _first;
            private IDisposable _second;

            internal CombinedAreaArrivalGateLease(
                IDisposable first,
                IDisposable second)
            {
                _first = first;
                _second = second;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _second, null)?.Dispose();
                Interlocked.Exchange(ref _first, null)?.Dispose();
            }
        }

    }
}
