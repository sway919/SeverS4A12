using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class PartyHandler
    {
        private readonly object _tradePairsLock = new object();
        private readonly Dictionary<ushort, ushort> _tradePeers =
            new Dictionary<ushort, ushort>();
        private readonly Dictionary<ushort, Dictionary<short, PlayerTradeOffer>>
            _tradeOffers =
                new Dictionary<ushort, Dictionary<short, PlayerTradeOffer>>();
        private readonly Dictionary<ulong, PendingTradeInvite>
            _pendingTradePeerIds =
                new Dictionary<ulong, PendingTradeInvite>();
        private readonly HashSet<ushort> _tradeFinalReady =
            new HashSet<ushort>();
        private readonly HashSet<ushort> _tradeOfferReady =
            new HashSet<ushort>();
        private readonly HashSet<ushort> _tradeOfferStageOpen =
            new HashSet<ushort>();
        private readonly InventoryRefreshSender _inventoryRefresh;

        public async Task Handle_SET_ITEMTRADE_STATE(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var uid = session?.Player?.UserId ?? 0;
            ushort peer;
            lock (_tradePairsLock)
                _tradePeers.TryGetValue(uid, out peer);

            FileLogger.Log(
                $"[{ProtocolName}] SET_ITEMTRADE_STATE " +
                $"uid={uid} peer={peer} body({body?.Length ?? 0}B): " +
                $"{(body == null ? "null" : BitConverter.ToString(body))}");
            if (uid == 0 || peer == 0 || body == null || body.Length != 1)
                return;

            var peerSession = FindSessionByUserId(peer);
            if (peerSession == null
                || !IsSameGameChannel(session, peerSession))
            {
                ClearTradePair(uid);
                return;
            }

            if (body[0] == 3)
            {
                var bothConfirmed = false;
                var offerStageReady = false;
                lock (_tradePairsLock)
                {
                    if (_tradePeers.TryGetValue(uid, out var currentPeer)
                        && currentPeer == peer)
                    {
                        offerStageReady = _tradeOfferStageOpen.Contains(uid)
                            && _tradeOfferStageOpen.Contains(peer);
                        if (offerStageReady)
                        {
                            _tradeFinalReady.Add(uid);
                            bothConfirmed = _tradeFinalReady.Contains(peer);
                        }
                    }
                }

                FileLogger.Log(
                    $"[{ProtocolName}] TRADE FINAL_CONFIRM " +
                    $"uid={uid} peer={peer} " +
                    $"offerReady={offerStageReady} both={bothConfirmed}");
                if (bothConfirmed)
                    await CompleteTradeAsync(session, peerSession);
                return;
            }

            if (body[0] == 2)
            {
                ClearTradePair(uid);
                await SendTradeTerminalAsync(
                    session,
                    peerSession,
                    NotiPacketType.CANCEL_ITEMTRADE);
                return;
            }

            // Action 5 is the offer-stage "finish adding" state. It must be
            // echoed unchanged: state 1 makes the 86JP client immediately open
            // the final confirmation popup as soon as both sides are ready,
            // skipping the locked-offer view and its Trade button.
            var notificationState = body[0];
            var openOfferStage = false;
            lock (_tradePairsLock)
            {
                if (body[0] == 5)
                {
                    _tradeOfferReady.Add(uid);
                    if (_tradeOfferReady.Contains(peer)
                        && !_tradeOfferStageOpen.Contains(uid))
                    {
                        _tradeOfferStageOpen.Add(uid);
                        _tradeOfferStageOpen.Add(peer);
                        openOfferStage = true;
                    }
                }
                else if (notificationState == 0)
                {
                    _tradeOfferReady.Remove(uid);
                    _tradeFinalReady.Remove(uid);
                }
            }
            await SendTradeStateAsync(
                session,
                peerSession,
                uid,
                notificationState);
            if (openOfferStage)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] TRADE OFFER_STAGE_OPEN " +
                    $"first={uid} second={peer} noti=0x028F");
                await SendTradeOfferStageOpenAsync(session, peerSession);
            }
        }

        public async Task<bool> TryHandleTradeMoveAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (session?.Player == null || body == null || body.Length < 14)
                return false;

            var sourceList = (InventoryListType)body[0];
            var destinationList = (InventoryListType)body[11];
            if (sourceList != InventoryListType.Trade
                && destinationList != InventoryListType.Trade)
            {
                return false;
            }

            var uid = session.Player.UserId;
            ushort peer;
            lock (_tradePairsLock)
                _tradePeers.TryGetValue(uid, out peer);
            if (peer == 0)
            {
                await SendTradeMoveErrorAsync(
                    session,
                    header.type,
                    sourceList,
                    destinationList);
                return true;
            }

            var peerSession = FindSessionByUserId(peer);
            if (peerSession == null
                || !IsSameGameChannel(session, peerSession))
            {
                ClearTradePair(uid);
                await SendTradeMoveErrorAsync(
                    session,
                    header.type,
                    sourceList,
                    destinationList);
                return true;
            }

            var sourceSlot = BitConverter.ToInt16(body, 1);
            var moveValue = BitConverter.ToInt32(body, 7);
            var destinationSlot = BitConverter.ToInt16(body, 12);
            byte[] peerChangeBody;
            ushort[] resetReadyUsers = Array.Empty<ushort>();

            if (destinationList == InventoryListType.Trade)
            {
                if (!InventoryContext.TryGetOwnedLease(
                        session.SessionId,
                        session.Player.CharacterId,
                        out var lease))
                {
                    await SendTradeMoveErrorAsync(
                        session,
                        header.type,
                        sourceList,
                        destinationList);
                    return true;
                }

                PlayerTradeOffer offer;
                string failure;
                lock (lease.SyncRoot)
                {
                    if (!PlayerTradeRuntimeService.TryCreateOffer(
                            lease.Inventory,
                            body[0],
                            sourceSlot,
                            moveValue,
                            out offer,
                            out failure))
                    {
                        offer = null;
                    }
                }
                if (offer == null)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] TRADE offer rejected uid={uid} " +
                        $"source={body[0]}:{sourceSlot} reason={failure}");
                    await SendTradeMoveErrorAsync(
                        session,
                        header.type,
                        sourceList,
                        destinationList);
                    return true;
                }

                lock (_tradePairsLock)
                {
                    if (!_tradePeers.TryGetValue(uid, out var currentPeer)
                        || currentPeer != peer)
                    {
                        peerChangeBody = null;
                    }
                    else
                    {
                        var offers = _tradeOffers[uid];
                        var existing = offers.Values.FirstOrDefault(value =>
                            value.SourceList == offer.SourceList
                            && value.SourceSlot == offer.SourceSlot);
                        if (existing != null)
                            offers.Remove(existing.TradeSlot);

                        destinationSlot = offer.IsGold
                            ? (short)0
                            : existing?.TradeSlot ?? FindAvailableTradeSlot(offers);
                        if (destinationSlot < 0)
                        {
                            peerChangeBody = null;
                        }
                        else
                        {
                            offer.TradeSlot = destinationSlot;
                            offers[destinationSlot] = offer;
                            resetReadyUsers = ResetTradeReadyLocked(uid, peer);
                            peerChangeBody = offer.IsGold
                                ? TradeItemChangeBodyBuilder.BuildGold(
                                    offer.TradeSlot,
                                    offer.Amount)
                                : TradeItemChangeBodyBuilder.BuildItem(
                                    offer.TradeSlot,
                                    offer.Item);
                        }
                    }
                }
            }
            else
            {
                lock (_tradePairsLock)
                {
                    if (!_tradeOffers.TryGetValue(uid, out var offers)
                        || !offers.Remove(sourceSlot))
                    {
                        peerChangeBody = null;
                    }
                    else
                    {
                        resetReadyUsers = ResetTradeReadyLocked(uid, peer);
                        peerChangeBody =
                            TradeItemChangeBodyBuilder.BuildEmpty(sourceSlot);
                    }
                }
            }

            if (peerChangeBody == null)
            {
                await SendTradeMoveErrorAsync(
                    session,
                    header.type,
                    sourceList,
                    destinationList);
                return true;
            }

            await session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x01,
                    header.type,
                    MoveItemSpaceAckBuilder.Build(
                        new InventoryMoveResult
                        {
                            SourceListType = sourceList,
                            SourceSlotIndex = sourceSlot,
                            MoveValue32 = moveValue,
                            DestinationListType = destinationList,
                            DestinationSlotIndex = destinationSlot,
                            Mutated = false,
                        })));
            foreach (var readyUid in resetReadyUsers)
            {
                await SendTradeStateAsync(
                    session,
                    peerSession,
                    readyUid,
                    0);
            }
            await peerSession.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.CHANGE_ITEMTRADE_ITEM,
                    peerChangeBody));
            FileLogger.Log(
                $"[{ProtocolName}] TRADE MOVE uid={uid} peer={peer} " +
                $"src=({sourceList},{sourceSlot}) " +
                $"dst=({destinationList},{destinationSlot}) " +
                $"value={moveValue} change({peerChangeBody.Length}B)=" +
                BitConverter.ToString(peerChangeBody));
            return true;
        }

        private async Task CompleteTradeAsync(
            EnhancedClientSession firstSession,
            EnhancedClientSession secondSession)
        {
            var firstUid = firstSession.Player.UserId;
            var secondUid = secondSession.Player.UserId;
            List<PlayerTradeOffer> firstOffers;
            List<PlayerTradeOffer> secondOffers;
            lock (_tradePairsLock)
            {
                if (!_tradePeers.TryGetValue(firstUid, out var peer)
                    || peer != secondUid
                    || !_tradeFinalReady.Contains(firstUid)
                    || !_tradeFinalReady.Contains(secondUid))
                {
                    return;
                }

                firstOffers = _tradeOffers[firstUid]
                    .Values.Select(value => value.Copy()).ToList();
                secondOffers = _tradeOffers[secondUid]
                    .Values.Select(value => value.Copy()).ToList();
                // Claim the pair before inventory mutation so duplicate final
                // confirmation packets cannot execute the exchange twice.
                ClearTradePairLocked(firstUid);
            }

            PlayerTradeExecutionResult result = null;
            var executed = InventoryContext.TryGetOwnedLease(
                    firstSession.SessionId,
                    firstSession.Player.CharacterId,
                    out var firstLease)
                && InventoryContext.TryGetOwnedLease(
                    secondSession.SessionId,
                    secondSession.Player.CharacterId,
                    out var secondLease)
                && PlayerTradeRuntimeService.TryExecuteAndPersist(
                    firstLease,
                    firstOffers,
                    secondLease,
                    secondOffers,
                    out result);
            result ??= new PlayerTradeExecutionResult
            {
                Failure = "inventory lease missing",
            };

            FileLogger.Log(
                $"[{ProtocolName}] TRADE COMPLETE " +
                $"first={firstUid} second={secondUid} ok={executed} " +
                $"failure={result.Failure ?? "none"}");

            await SendTradeTerminalAsync(
                firstSession,
                secondSession,
                executed
                    ? NotiPacketType.FINISH_ITEMTRADE
                    : NotiPacketType.CANCEL_ITEMTRADE);

            if (executed && _inventoryRefresh != null)
            {
                await SendTradeInventoryChangesAsync(
                    firstSession,
                    result.FirstChanges);
                await SendTradeInventoryChangesAsync(
                    secondSession,
                    result.SecondChanges);
            }
        }

        private async Task SendTradeInventoryChangesAsync(
            EnhancedClientSession session,
            InventoryMutationSet changes)
        {
            foreach (var group in changes.Slots.GroupBy(value => value.ListType))
            {
                await _inventoryRefresh.SendUpdateItemList(
                    session,
                    group.Key,
                    group.Select(value => value.SlotIndex));
            }
        }

        private static async Task SendTradeTerminalAsync(
            EnhancedClientSession first,
            EnhancedClientSession second,
            NotiPacketType packetType)
        {
            var body = new GamePacketWriter();
            body.WriteUInt16(0);
            var packet = GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)packetType,
                body.ToArray());
            await Task.WhenAll(
                first.SendPacketAsync(packet),
                second.SendPacketAsync(packet));
            await SendTradeStateAsync(first, second, first.Player.UserId, 0);
            await SendTradeStateAsync(first, second, second.Player.UserId, 0);
            await SendTradeIdleUserStateAsync(
                first,
                second,
                first.Player.UserId,
                second.Player.UserId);
        }

        private static Task SendTradeStateAsync(
            EnhancedClientSession first,
            EnhancedClientSession second,
            ushort actorUid,
            byte stateValue)
        {
            var state = new GamePacketWriter();
            state.WriteUInt16(actorUid);
            state.WriteByte(stateValue);
            var packet = GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.STATE_ITEMTRADE,
                state.ToArray());
            return Task.WhenAll(
                first.SendPacketAsync(packet),
                second.SendPacketAsync(packet));
        }

        private static Task SendTradeOfferStageOpenAsync(
            EnhancedClientSession first,
            EnhancedClientSession second)
        {
            // The 86JP client keeps offer-ready state (NOTI 0x0011) and the
            // locked-offer phase as separate signals. This empty notification
            // commits the two cached trade lists, reveals the peer's items,
            // and enables the final Trade button.
            var packet = GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.ITEMTRADE_REG_ITEM_FINISH,
                Array.Empty<byte>());
            return Task.WhenAll(
                first.SendPacketAsync(packet),
                second.SendPacketAsync(packet));
        }

        private static Task SendTradeIdleUserStateAsync(
            EnhancedClientSession first,
            EnhancedClientSession second,
            ushort firstUid,
            ushort secondUid)
        {
            first.Player.UserState = 0;
            second.Player.UserState = 0;
            var packet = GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketType.USER_STATE,
                EnterSelectDungeonStateBuilder.BuildUserState(
                    new[] { firstUid, secondUid },
                    0));
            return Task.WhenAll(
                first.SendPacketAsync(packet),
                second.SendPacketAsync(packet));
        }

        private static Task SendTradeMoveErrorAsync(
            EnhancedClientSession session,
            ushort packetType,
            InventoryListType sourceList,
            InventoryListType destinationList)
        {
            return session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x01,
                    packetType,
                    MoveItemSpaceAckBuilder.BuildError(
                        0x04,
                        (byte)sourceList,
                        (byte)destinationList)));
        }

        private void RegisterTradePair(ushort first, ushort second)
        {
            lock (_tradePairsLock)
            {
                ClearTradePairLocked(first);
                ClearTradePairLocked(second);
                _tradePeers[first] = second;
                _tradePeers[second] = first;
                _tradeOffers[first] =
                    new Dictionary<short, PlayerTradeOffer>();
                _tradeOffers[second] =
                    new Dictionary<short, PlayerTradeOffer>();
                ResetTradeReadyLocked(first, second);
            }
        }

        private void ClearTradePair(ushort userId)
        {
            lock (_tradePairsLock)
                ClearTradePairLocked(userId);
        }

        private void ClearTradePairLocked(ushort userId)
        {
            var pendingKeys = _pendingTradePeerIds.Keys
                .Where(key =>
                    (ushort)(key >> 32) == userId
                    || (ushort)key == userId)
                .ToArray();
            foreach (var key in pendingKeys)
                _pendingTradePeerIds.Remove(key);

            if (!_tradePeers.TryGetValue(userId, out var peer))
                return;
            _tradePeers.Remove(userId);
            _tradeOffers.Remove(userId);
            _tradeFinalReady.Remove(userId);
            _tradeOfferReady.Remove(userId);
            _tradeOfferStageOpen.Remove(userId);
            if (_tradePeers.TryGetValue(peer, out var reverse)
                && reverse == userId)
            {
                _tradePeers.Remove(peer);
                _tradeOffers.Remove(peer);
                _tradeFinalReady.Remove(peer);
                _tradeOfferReady.Remove(peer);
                _tradeOfferStageOpen.Remove(peer);
            }
        }

        private bool HasPendingTradeInviteLocked(ushort userId)
        {
            return _pendingTradePeerIds.Keys.Any(key =>
                (ushort)(key >> 32) == userId
                || (ushort)key == userId);
        }

        private ushort[] ResetTradeReadyLocked(ushort first, ushort second)
        {
            var reset = new List<ushort>(2);
            if (_tradeOfferReady.Contains(first)
                || _tradeFinalReady.Contains(first))
                reset.Add(first);
            if (_tradeOfferReady.Contains(second)
                || _tradeFinalReady.Contains(second))
                reset.Add(second);

            _tradeOfferReady.Remove(first);
            _tradeOfferReady.Remove(second);
            _tradeOfferStageOpen.Remove(first);
            _tradeOfferStageOpen.Remove(second);
            _tradeFinalReady.Remove(first);
            _tradeFinalReady.Remove(second);
            return reset.ToArray();
        }

        private static short FindAvailableTradeSlot(
            IDictionary<short, PlayerTradeOffer> offers)
        {
            // Native CTradeSpace reserves 0..2 for currencies and 3..26 for items.
            for (short slot = 3; slot <= 26; slot++)
            {
                if (!offers.ContainsKey(slot))
                    return slot;
            }
            return -1;
        }

        private static ulong MakeDirectedTradeKey(
            ushort inviter,
            ushort accepter)
        {
            return ((ulong)inviter << 32) | accepter;
        }

        private sealed class PendingTradeInvite
        {
            internal PendingTradeInvite(
                Guid inviterSessionId,
                Guid accepterSessionId,
                int peerInt)
            {
                InviterSessionId = inviterSessionId;
                AccepterSessionId = accepterSessionId;
                PeerInt = peerInt;
            }

            internal Guid InviterSessionId { get; }

            internal Guid AccepterSessionId { get; }

            internal int PeerInt { get; }
        }
    }
}
