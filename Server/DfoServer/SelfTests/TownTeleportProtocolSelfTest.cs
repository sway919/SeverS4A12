using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Town;

namespace DfoServer.SelfTests
{
    public static class TownTeleportProtocolSelfTest
    {
        private const int SharedListenerPort = 11011;
        private const int OtherListenerPort = 11012;
        private const int TeleporterCharacterId = 71001;
        private const int OldObserverCharacterId = 71002;
        private const int DestinationObserverCharacterId = 71003;
        private const int OtherChannelCharacterId = 71004;
        private const int FailedTeleportCharacterId = 71006;
        private const ushort TeleporterUserId = 2101;
        private const ushort OldObserverUserId = 2102;
        private const ushort DestinationObserverUserId = 2103;
        private const ushort OtherChannelUserId = 2104;

        public static int Run()
        {
            var failures = 0;
            VerifyParserAndPacketBuilders(ref failures);

            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "town_teleport_protocol_" +
                Guid.NewGuid().ToString("N") + ".db");
            var previousDatabasePath =
                Environment.GetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH");
            GameProtocolHandler protocol = null;
            ConnectedSession teleporter = null;
            ConnectedSession oldObserver = null;
            ConnectedSession destinationObserver = null;
            ConnectedSession otherChannel = null;
            ConnectedSession staleSession = null;
            ConnectedSession failedTeleport = null;

            try
            {
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    databasePath);
                Check(
                    "captured PARTY_TELEPORT destination exists in PVF",
                    Town.ContainsArea(2, 6),
                    ref failures);

                var sessions = new SessionDirectory();
                protocol = new GameProtocolHandler(sessions);
                teleporter = ConnectedSession.Create(
                    SharedListenerPort);
                oldObserver = ConnectedSession.Create(
                    SharedListenerPort);
                destinationObserver = ConnectedSession.Create(
                    SharedListenerPort);
                otherChannel = ConnectedSession.Create(
                    OtherListenerPort);
                staleSession = ConnectedSession.Create(
                    SharedListenerPort);

                ConfigurePlayer(
                    teleporter.Session,
                    TeleporterCharacterId,
                    TeleporterUserId,
                    1,
                    1,
                    100,
                    200);
                ConfigurePlayer(
                    oldObserver.Session,
                    OldObserverCharacterId,
                    OldObserverUserId,
                    1,
                    1,
                    300,
                    400);
                ConfigurePlayer(
                    destinationObserver.Session,
                    DestinationObserverCharacterId,
                    DestinationObserverUserId,
                    2,
                    6,
                    500,
                    600);
                ConfigurePlayer(
                    otherChannel.Session,
                    OtherChannelCharacterId,
                    OtherChannelUserId,
                    2,
                    6,
                    700,
                    800);
                ConfigurePlayer(
                    staleSession.Session,
                    71005,
                    2105,
                    1,
                    1,
                    900,
                    1000);
                sessions.Register(
                    TeleporterCharacterId, teleporter.Session);
                sessions.Register(
                    OldObserverCharacterId, oldObserver.Session);
                sessions.Register(
                    DestinationObserverCharacterId,
                    destinationObserver.Session);
                sessions.Register(
                    OtherChannelCharacterId, otherChannel.Session);

                var parties = GetPartyManager(protocol);
                var created = parties.CreateParty(
                    BuildPartyMember(teleporter.Session));
                var joined = parties.Join(
                    created.Party.PartyId,
                    BuildPartyMember(oldObserver.Session));
                Check(
                    "test fixture creates a two-member party",
                    created.Ok && joined.Ok,
                    ref failures);

                var destination = BuildDestination(
                    2, 6, 1798, 352, 5);
                Dispatch(
                    protocol,
                    teleporter.Session,
                    0x0024,
                    destination);
                var areaSelfPackets = teleporter.DrainPackets();
                var oldAreaPackets = oldObserver.DrainPackets();
                var destinationPackets =
                    destinationObserver.DrainPackets();
                var otherChannelPackets =
                    otherChannel.DrainPackets();

                Check(
                    "SET_USER_AREA moves the authoritative session",
                    IsAt(
                        teleporter.Session,
                        2, 6, 1798, 352, 5),
                    ref failures);
                Check(
                    "old area receives destination USER_AREA instead of USER_LEAVE",
                    oldAreaPackets.Any(packet =>
                        IsUserAreaFor(
                            packet,
                            TeleporterUserId,
                            2,
                            6,
                            1798,
                            352)) &&
                    !oldAreaPackets.Any(packet =>
                        packet.Command == 0x00 &&
                        packet.Type == 0x0006),
                    ref failures);
                Check(
                    "area transition preserves the server party binding",
                    ReferenceEquals(
                        parties.GetPartyByUser(TeleporterUserId),
                        parties.GetPartyByUser(OldObserverUserId)) &&
                    parties.GetPartyByUser(TeleporterUserId)?.Count == 2,
                    ref failures);
                Check(
                    "destination observer receives appearance and one USER_AREA increment",
                    destinationPackets.Any(packet =>
                        packet.Command == 0x00 &&
                        packet.Type == 0x0002) &&
                    destinationPackets.Count(packet =>
                        IsUserAreaFor(
                            packet,
                            TeleporterUserId,
                            2,
                            6,
                            1798,
                            352)) == 1 &&
                    !destinationPackets.Any(packet =>
                        packet.Command == 0x00 &&
                        packet.Type == 0x0018),
                    ref failures);
                Check(
                    "arriving player receives the complete same-channel roster",
                    areaSelfPackets.Any(packet =>
                        IsRosterFor(
                            packet,
                            2,
                            6,
                            new[]
                            {
                                TeleporterUserId,
                                DestinationObserverUserId
                            },
                            OtherChannelUserId)),
                    ref failures);
                var appearanceIndex = FindPacketIndex(
                    areaSelfPackets,
                    packet =>
                        packet.Command == 0x00 &&
                        packet.Type == 0x0002);
                var existingAreaIndex = FindPacketIndex(
                    areaSelfPackets,
                    packet => IsUserAreaFor(
                        packet,
                        DestinationObserverUserId,
                        2,
                        6,
                        500,
                        600));
                var rosterIndex = FindPacketIndex(
                    areaSelfPackets,
                    packet =>
                        packet.Command == 0x00 &&
                        packet.Type == 0x0018);
                Check(
                    "newcomer receives appearance before USER_AREA before roster",
                    appearanceIndex >= 0 &&
                    appearanceIndex < existingAreaIndex &&
                    existingAreaIndex < rosterIndex,
                    ref failures);
                Check(
                    "town arrival never crosses listener channels",
                    otherChannelPackets.Count == 0,
                    ref failures);

                ResetPosition(
                    teleporter.Session,
                    1,
                    1,
                    100,
                    200,
                    1);
                Dispatch(
                    protocol,
                    teleporter.Session,
                    0x0024,
                    destination.Take(6).ToArray());
                var legacyAreaPackets = teleporter.DrainPackets();
                oldObserver.DrainPackets();
                destinationObserver.DrainPackets();
                otherChannel.DrainPackets();
                Check(
                    "SET_USER_AREA keeps the six-byte legacy body and direction fallback",
                    legacyAreaPackets.Count > 0 &&
                    IsAt(
                        teleporter.Session,
                        2, 6, 1798, 352, 5),
                    ref failures);

                ResetPosition(
                    teleporter.Session,
                    1, 1, 100, 200, 5);
                teleporter.Session.Player.UserState = 0x01;
                Dispatch(
                    protocol,
                    teleporter.Session,
                    0x0024,
                    destination);
                var nonTownStateSelfPackets = teleporter.DrainPackets();
                var nonTownStateOldPackets = oldObserver.DrainPackets();
                var nonTownStateDestinationPackets =
                    destinationObserver.DrainPackets();
                var nonTownStateOtherChannelPackets =
                    otherChannel.DrainPackets();
                teleporter.Session.Player.UserState = 0x00;
                Check(
                    "SET_USER_AREA rejects a non-town UserState without publication",
                    IsAt(
                        teleporter.Session,
                        1, 1, 100, 200, 5) &&
                    nonTownStateSelfPackets.Count == 0 &&
                    nonTownStateOldPackets.Count == 0 &&
                    nonTownStateDestinationPackets.Count == 0 &&
                    nonTownStateOtherChannelPackets.Count == 0,
                    ref failures);

                ResetPosition(
                    teleporter.Session,
                    1, 1, 100, 200, 5);
                var selection =
                    teleporter.Session.Player.BeginDungeonSelection(
                        new DungeonTownReturnAnchor(
                            1, 1, 100, 200, 5, 3));
                Dispatch(
                    protocol,
                    teleporter.Session,
                    0x0024,
                    destination);
                var pendingSelectionSelfPackets =
                    teleporter.DrainPackets();
                var pendingSelectionOldPackets = oldObserver.DrainPackets();
                var pendingSelectionDestinationPackets =
                    destinationObserver.DrainPackets();
                var pendingSelectionOtherChannelPackets =
                    otherChannel.DrainPackets();
                var pendingSelectionHidden =
                    !TownHandler.IsTownObserverSelectionEligible(
                        teleporter.Session.Player);
                var returnStarted = selection != null &&
                    selection.TryBeginReturn();
                var returningSelectionVisible =
                    TownHandler.IsTownObserverSelectionEligible(
                        teleporter.Session.Player);
                selection?.CancelReturn();
                teleporter.Session.Player.ClearDungeonSelection();
                Check(
                    "SET_USER_AREA rejects CurrentDungeonSelection without publication",
                    selection != null &&
                    IsAt(
                        teleporter.Session,
                        1, 1, 100, 200, 5) &&
                    pendingSelectionSelfPackets.Count == 0 &&
                    pendingSelectionOldPackets.Count == 0 &&
                    pendingSelectionDestinationPackets.Count == 0 &&
                    pendingSelectionOtherChannelPackets.Count == 0,
                    ref failures);
                Check(
                    "selection return becomes observable before context cleanup",
                    pendingSelectionHidden &&
                    returnStarted &&
                    returningSelectionVisible,
                    ref failures);

                ResetPosition(
                    teleporter.Session,
                    1, 1, 100, 200, 5);
                teleporter.Session.Player.CurrentRun =
                    new DungeonRun(1, 0);
                Dispatch(
                    protocol,
                    teleporter.Session,
                    0x0024,
                    destination);
                var currentRunSelfPackets = teleporter.DrainPackets();
                var currentRunOldPackets = oldObserver.DrainPackets();
                var currentRunDestinationPackets =
                    destinationObserver.DrainPackets();
                var currentRunOtherChannelPackets =
                    otherChannel.DrainPackets();
                teleporter.Session.Player.CurrentRun = null;
                Check(
                    "SET_USER_AREA rejects CurrentRun without publication",
                    IsAt(
                        teleporter.Session,
                        1, 1, 100, 200, 5) &&
                    currentRunSelfPackets.Count == 0 &&
                    currentRunOldPackets.Count == 0 &&
                    currentRunDestinationPackets.Count == 0 &&
                    currentRunOtherChannelPackets.Count == 0,
                    ref failures);

                ResetPosition(
                    teleporter.Session,
                    1,
                    1,
                    100,
                    200,
                    5);
                Dispatch(
                    protocol,
                    teleporter.Session,
                    (ushort)CmdPacketType.PARTY_TELEPORT,
                    destination);
                var rejectedPackets = teleporter.DrainPackets();
                var rejectedOldPackets = oldObserver.DrainPackets();
                var rejectedDestinationPackets = destinationObserver.DrainPackets();
                var rejectedOtherChannelPackets = otherChannel.DrainPackets();
                Check(
                    "multi-member PARTY_TELEPORT is rejected pending confirmation protocol",
                    HasCommandError(
                        rejectedPackets,
                        (ushort)CmdPacketType.PARTY_TELEPORT,
                        0x04) &&
                    IsAt(
                        teleporter.Session,
                        1, 1, 100, 200, 5),
                    ref failures);
                Check(
                    "rejected multi-member teleport emits no area publication",
                    rejectedOldPackets.Count == 0 &&
                    rejectedDestinationPackets.Count == 0 &&
                    rejectedOtherChannelPackets.Count == 0,
                    ref failures);

                parties.Leave(TeleporterUserId);
                parties.Leave(OldObserverUserId);

                Dispatch(
                    protocol,
                    staleSession.Session,
                    0x01B3,
                    destination);
                Check(
                    "stale unregistered session cannot publish a teleport",
                    staleSession.DrainPackets().Count == 0 &&
                    IsAt(
                        staleSession.Session,
                        1, 1, 900, 1000, 5),
                    ref failures);

                Dispatch(
                    protocol,
                    teleporter.Session,
                    0x01B3,
                    BuildDestination(0, 0, 1, 1, 5));
                var invalidPackets = teleporter.DrainPackets();
                var invalidOldPackets = oldObserver.DrainPackets();
                var invalidDestinationPackets =
                    destinationObserver.DrainPackets();
                var invalidOtherChannelPackets =
                    otherChannel.DrainPackets();
                Check(
                    "invalid PARTY_TELEPORT area fails closed",
                    HasCommandError(
                        invalidPackets,
                        0x01B3,
                        0x04) &&
                    IsAt(
                        teleporter.Session,
                        1, 1, 100, 200, 5) &&
                    invalidOldPackets.Count == 0 &&
                    invalidDestinationPackets.Count == 0 &&
                    invalidOtherChannelPackets.Count == 0,
                    ref failures);

                Dispatch(
                    protocol,
                    teleporter.Session,
                    (ushort)CmdPacketType.PARTY_TELEPORT,
                    destination);
                var teleportSelfPackets = teleporter.DrainPackets();
                var teleportOldPackets = oldObserver.DrainPackets();
                var teleportDestinationPackets =
                    destinationObserver.DrainPackets();
                var teleportOtherChannelPackets =
                    otherChannel.DrainPackets();

                Check(
                    "production dispatch handles PARTY_TELEPORT 0x01B3",
                    HasSuccessAck(
                        teleportSelfPackets,
                        (ushort)CmdPacketType.PARTY_TELEPORT) &&
                    IsAt(
                        teleporter.Session,
                        2, 6, 1798, 352, 5),
                    ref failures);
                Check(
                    "PARTY_TELEPORT removes the old-area avatar without USER_LEAVE",
                    teleportOldPackets.Any(packet =>
                        IsUserAreaFor(
                            packet,
                            TeleporterUserId,
                            2,
                            6,
                            1798,
                            352)) &&
                    !teleportOldPackets.Any(packet =>
                        packet.Command == 0x00 &&
                        packet.Type == 0x0006),
                    ref failures);
                Check(
                    "PARTY_TELEPORT publishes the teleporter in the destination",
                    teleportDestinationPackets.Any(packet =>
                        IsUserAreaFor(
                            packet,
                            TeleporterUserId,
                            2,
                            6,
                            1798,
                            352)) &&
                    !teleportDestinationPackets.Any(packet =>
                        packet.Command == 0x00 &&
                        packet.Type == 0x0018),
                    ref failures);
                Check(
                    "PARTY_TELEPORT remains channel-local",
                    teleportOtherChannelPackets.Count == 0,
                    ref failures);

                var gate = Town.GetCeraRoomInfo(1);
                Check(
                    "item teleport target resolves to a valid town area",
                    gate.Town == 1 &&
                    Town.ContainsArea(gate.Town, gate.Area),
                    ref failures);
                var teleportInventory = new InventoryService(
                    TeleporterCharacterId,
                    1);
                teleportInventory.SetItem(
                    InventoryListType.Main,
                    3,
                    new ItemCore
                    {
                        ItemKind = ItemCore.KindConsumable,
                        ItemId = 0x0027AC4E,
                        Count = 2
                    });
                InventoryContext.Register(
                    teleporter.Session.SessionId,
                    TeleporterCharacterId,
                    teleportInventory);
                ResetPosition(
                    oldObserver.Session,
                    gate.Town,
                    gate.Area,
                    gate.X,
                    gate.Y,
                    0);
                oldObserver.DrainPackets();
                destinationObserver.DrainPackets();
                otherChannel.DrainPackets();
                Dispatch(
                    protocol,
                    teleporter.Session,
                    0x00ED,
                    BuildItemTeleportRequest(1));
                var itemTeleportSelfPackets =
                    teleporter.DrainPackets();
                var itemTeleportOldPackets =
                    destinationObserver.DrainPackets();
                var itemTeleportOtherChannelPackets =
                    otherChannel.DrainPackets();
                var itemTeleportPrivatePackets =
                    oldObserver.DrainPackets();
                Check(
                    "item TELEPORT 0x00ED reuses the transition pipeline",
                    HasPacket(
                        itemTeleportSelfPackets,
                        0x01,
                        0x00ED) &&
                    IsAt(
                        teleporter.Session,
                        gate.Town,
                        gate.Area,
                        gate.X,
                        gate.Y,
                        0),
                    ref failures);
                Check(
                    "item TELEPORT also clears the old area with destination USER_AREA",
                    itemTeleportOldPackets.Any(packet =>
                        IsUserAreaFor(
                            packet,
                            TeleporterUserId,
                            gate.Town,
                            gate.Area,
                            gate.X,
                            gate.Y)) &&
                    !itemTeleportOldPackets.Any(packet =>
                        packet.Command == 0x00 &&
                        packet.Type == 0x0006),
                    ref failures);
                Check(
                    "item TELEPORT old-area cleanup stays channel-local",
                    itemTeleportOtherChannelPackets.Count == 0,
                    ref failures);
                Check(
                    "item TELEPORT consumes one item and refreshes its slot",
                    teleportInventory.CountMainItem(0x0027AC4E) == 1 &&
                    HasPacket(
                        itemTeleportSelfPackets,
                        0x00,
                        0x000E),
                    ref failures);
                Check(
                    "private Cera room never shares its roster",
                    itemTeleportPrivatePackets.Count == 0,
                    ref failures);

                teleportInventory.RemoveItem(
                    InventoryListType.Main,
                    3);
                ResetPosition(
                    teleporter.Session,
                    2, 6, 1798, 352, 5);
                teleporter.DrainPackets();
                oldObserver.DrainPackets();
                destinationObserver.DrainPackets();
                otherChannel.DrainPackets();
                Dispatch(
                    protocol,
                    teleporter.Session,
                    0x00ED,
                    BuildItemTeleportRequest(1));
                var noItemSelfPackets = teleporter.DrainPackets();
                var noItemPrivatePackets = oldObserver.DrainPackets();
                var noItemOldAreaPackets =
                    destinationObserver.DrainPackets();
                var noItemOtherChannelPackets =
                    otherChannel.DrainPackets();
                Check(
                    "item TELEPORT without an item neither moves nor publishes",
                    teleportInventory.CountMainItem(0x0027AC4E) == 0 &&
                    IsAt(
                        teleporter.Session,
                        2, 6, 1798, 352, 5) &&
                    !HasPacket(noItemSelfPackets, 0x01, 0x00ED) &&
                    !HasTownAreaPublication(noItemSelfPackets) &&
                    noItemPrivatePackets.Count == 0 &&
                    noItemOldAreaPackets.Count == 0 &&
                    noItemOtherChannelPackets.Count == 0,
                    ref failures);

                InventoryContext.Unregister(
                    teleporter.Session.SessionId,
                    TeleporterCharacterId);
                ResetPosition(
                    teleporter.Session,
                    2, 6, 1798, 352, 5);
                teleporter.DrainPackets();
                oldObserver.DrainPackets();
                destinationObserver.DrainPackets();
                otherChannel.DrainPackets();
                Dispatch(
                    protocol,
                    teleporter.Session,
                    0x00ED,
                    BuildItemTeleportRequest(1));
                var noLeaseSelfPackets = teleporter.DrainPackets();
                var noLeasePrivatePackets = oldObserver.DrainPackets();
                var noLeaseOldAreaPackets =
                    destinationObserver.DrainPackets();
                var noLeaseOtherChannelPackets =
                    otherChannel.DrainPackets();
                Check(
                    "item TELEPORT without its inventory lease neither moves nor publishes",
                    IsAt(
                        teleporter.Session,
                        2, 6, 1798, 352, 5) &&
                    !HasPacket(noLeaseSelfPackets, 0x01, 0x00ED) &&
                    !HasTownAreaPublication(noLeaseSelfPackets) &&
                    noLeasePrivatePackets.Count == 0 &&
                    noLeaseOldAreaPackets.Count == 0 &&
                    noLeaseOtherChannelPackets.Count == 0,
                    ref failures);

                failedTeleport = ConnectedSession.Create(
                    SharedListenerPort);
                ConfigurePlayer(
                    failedTeleport.Session,
                    FailedTeleportCharacterId,
                    2106,
                    2,
                    6,
                    1500,
                    300);
                sessions.Register(
                    FailedTeleportCharacterId,
                    failedTeleport.Session);
                var failedTeleportInventory = new InventoryService(
                    FailedTeleportCharacterId,
                    1);
                failedTeleportInventory.SetItem(
                    InventoryListType.Main,
                    3,
                    new ItemCore
                    {
                        ItemKind = ItemCore.KindConsumable,
                        ItemId = 0x0027AC4E,
                        Count = 1
                    });
                InventoryContext.Register(
                    failedTeleport.Session.SessionId,
                    FailedTeleportCharacterId,
                    failedTeleportInventory);
                destinationObserver.DrainPackets();
                otherChannel.DrainPackets();
                failedTeleport.DisableServerSends();
                Dispatch(
                    protocol,
                    failedTeleport.Session,
                    0x00ED,
                    BuildItemTeleportRequest(1));
                var failedTeleportSelfPackets =
                    failedTeleport.DrainPackets();
                destinationObserver.DrainPackets();
                var failedTeleportOtherChannelPackets =
                    otherChannel.DrainPackets();
                Check(
                    "item TELEPORT projection failure keeps the move paid and hidden",
                    failedTeleportInventory.CountMainItem(
                        0x0027AC4E) == 0 &&
                    failedTeleport.Session.Player.CurTownId ==
                        gate.Town &&
                    failedTeleport.Session.Player.CurAreaId ==
                        gate.Area &&
                    failedTeleport.Session.Player.CurPosX == gate.X &&
                    failedTeleport.Session.Player.CurPosY == gate.Y &&
                    !failedTeleport.Session.Player.TownPresenceReady &&
                    !HasPacket(
                        failedTeleportSelfPackets,
                        0x01,
                        0x00ED) &&
                    failedTeleportOtherChannelPackets.Count == 0,
                    ref failures);

                ResetPosition(
                    teleporter.Session,
                    1, 1, 100, 200, 5);
                ResetPosition(
                    oldObserver.Session,
                    1, 1, 300, 400, 5);
                destinationObserver.Session.Player.TownPresenceReady = false;
                teleporter.DrainPackets();
                oldObserver.DrainPackets();
                destinationObserver.DrainPackets();
                otherChannel.DrainPackets();

                using var arrivalStart = new ManualResetEventSlim(false);
                using var arrivalReady = new CountdownEvent(2);
                var firstArrival = Task.Run(() =>
                {
                    arrivalReady.Signal();
                    arrivalStart.Wait();
                    Dispatch(
                        protocol,
                        teleporter.Session,
                        0x0024,
                        destination);
                });
                var secondArrival = Task.Run(() =>
                {
                    arrivalReady.Signal();
                    arrivalStart.Wait();
                    Dispatch(
                        protocol,
                        oldObserver.Session,
                        0x0024,
                        BuildDestination(2, 6, 1700, 300, 5));
                });
                var arrivalsReachedBarrier = arrivalReady.Wait(
                    TimeSpan.FromSeconds(5));
                arrivalStart.Set();
                var arrivalsCompleted = Task.WaitAll(
                    new[] { firstArrival, secondArrival },
                    TimeSpan.FromSeconds(10));
                var firstArrivalPackets = teleporter.DrainPackets();
                var secondArrivalPackets = oldObserver.DrainPackets();
                destinationObserver.DrainPackets();
                var concurrentOtherChannelPackets =
                    otherChannel.DrainPackets();
                Check(
                    "concurrent arrivals publish both players to each other",
                    arrivalsReachedBarrier &&
                    arrivalsCompleted &&
                    IsAt(
                        teleporter.Session,
                        2, 6, 1798, 352, 5) &&
                    IsAt(
                        oldObserver.Session,
                        2, 6, 1700, 300, 5) &&
                    firstArrivalPackets.Any(packet =>
                        IsUserAreaFor(
                            packet,
                            OldObserverUserId,
                            2, 6, 1700, 300)) &&
                    secondArrivalPackets.Any(packet =>
                        IsUserAreaFor(
                            packet,
                            TeleporterUserId,
                            2, 6, 1798, 352)),
                    ref failures);
                Check(
                    "concurrent arrival gate remains channel-local",
                    concurrentOtherChannelPackets.Count == 0,
                    ref failures);

                ResetPosition(
                    teleporter.Session,
                    1, 1, 100, 200, 5);
                ResetPosition(
                    oldObserver.Session,
                    2, 6, 300, 400, 5);
                destinationObserver.Session.Player.TownPresenceReady = false;
                teleporter.DrainPackets();
                oldObserver.DrainPackets();
                destinationObserver.DrainPackets();
                otherChannel.DrainPackets();

                using var swapStart = new ManualResetEventSlim(false);
                using var swapReady = new CountdownEvent(2);
                var moveAToB = Task.Run(() =>
                {
                    swapReady.Signal();
                    swapStart.Wait();
                    Dispatch(
                        protocol,
                        teleporter.Session,
                        0x0024,
                        BuildDestination(2, 6, 1600, 320, 5));
                });
                var moveBToA = Task.Run(() =>
                {
                    swapReady.Signal();
                    swapStart.Wait();
                    Dispatch(
                        protocol,
                        oldObserver.Session,
                        0x0024,
                        BuildDestination(1, 1, 240, 360, 5));
                });
                var swapsReachedBarrier = swapReady.Wait(
                    TimeSpan.FromSeconds(5));
                swapStart.Set();
                var swapsCompleted = Task.WaitAll(
                    new[] { moveAToB, moveBToA },
                    TimeSpan.FromSeconds(10));
                Check(
                    "opposite-direction area transitions use ordered dual locks",
                    swapsReachedBarrier &&
                    swapsCompleted &&
                    IsAt(
                        teleporter.Session,
                        2, 6, 1600, 320, 5) &&
                    IsAt(
                        oldObserver.Session,
                        1, 1, 240, 360, 5),
                    ref failures);
                var forwardOrder =
                    TownHandler.OrderAreaTransitionGateKeys(
                        SharedListenerPort,
                        1, 1,
                        2, 6);
                var reverseOrder =
                    TownHandler.OrderAreaTransitionGateKeys(
                        SharedListenerPort,
                        2, 6,
                        1, 1);
                Check(
                    "opposite directions deterministically acquire identical gate order",
                    forwardOrder.First == reverseOrder.First &&
                    forwardOrder.Second == reverseOrder.Second &&
                    forwardOrder.First < forwardOrder.Second,
                    ref failures);
                teleporter.DrainPackets();
                oldObserver.DrainPackets();
                destinationObserver.DrainPackets();
                otherChannel.DrainPackets();
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] town teleport protocol threw: " + ex);
                failures++;
            }
            finally
            {
                if (teleporter != null)
                {
                    try
                    {
                        InventoryContext.Unregister(
                            teleporter.Session.SessionId,
                            TeleporterCharacterId);
                    }
                    catch
                    {
                    }
                }
                protocol?.Dispose();
                teleporter?.Dispose();
                oldObserver?.Dispose();
                destinationObserver?.Dispose();
                otherChannel?.Dispose();
                staleSession?.Dispose();
                if (failedTeleport != null)
                {
                    try
                    {
                        InventoryContext.Unregister(
                            failedTeleport.Session.SessionId,
                            FailedTeleportCharacterId);
                    }
                    catch
                    {
                    }
                }
                failedTeleport?.Dispose();
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    previousDatabasePath);
                DeleteDatabaseFiles(databasePath);
            }

            Console.WriteLine(
                failures == 0
                    ? "TownTeleportProtocolSelfTest OK"
                    : "TownTeleportProtocolSelfTest FAIL (" +
                      failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyParserAndPacketBuilders(
            ref int failures)
        {
            Check(
                "PARTY_TELEPORT keeps the captured wire type 0x01B3",
                (ushort)CmdPacketType.PARTY_TELEPORT == 0x01B3,
                ref failures);

            var captured = new byte[]
            {
                0x02, 0x06, 0x06, 0x07, 0x60, 0x01, 0x05
            };
            var parsed = PartyTeleportRequest.TryParse(
                captured, out var request);
            Check(
                "PARTY_TELEPORT parses the captured seven-byte layout",
                parsed &&
                request.TownId == 2 &&
                request.AreaId == 6 &&
                request.X == 1798 &&
                request.Y == 352 &&
                request.Direction == 5,
                ref failures);
            Check(
                "PARTY_TELEPORT rejects truncated and trailing bytes",
                !PartyTeleportRequest.TryParse(
                    new byte[6], out _) &&
                !PartyTeleportRequest.TryParse(
                    new byte[8], out _),
                ref failures);

            var snapshot = new TownUserSnapshot
            {
                UserId = TeleporterUserId,
                TownId = 2,
                AreaId = 6,
                PosX = 1798,
                PosY = 352,
                Direction = 5,
                State = 3
            };
            var transition =
                TownHandler.BuildAreaTransitionPacket(snapshot);
            var increments =
                TownHandler.BuildCoPresenceInserts(snapshot);
            Check(
                "area transition is destination USER_AREA 0x0017",
                IsUserAreaFor(
                    CapturedPacket.Parse(transition),
                    TeleporterUserId,
                    2,
                    6,
                    1798,
                    352),
                ref failures);
            Check(
                "existing observers receive exactly one 0x0017 increment",
                increments.Length == 1 &&
                IsUserAreaFor(
                    CapturedPacket.Parse(increments[0]),
                    TeleporterUserId,
                    2,
                    6,
                    1798,
                    352),
                ref failures);

            var observerCalled = false;
            var visibilityCommitted = false;
            var failedPublication =
                TownHandler.FanOutAreaRosterAsync(
                        cancellationToken =>
                            Task.FromException(
                                new IOException(
                                    "simulated newcomer disconnect")),
                        "newcomer",
                        new[]
                        {
                            (
                                Target: "observer",
                                Send: (Func<CancellationToken, Task>)(
                                    cancellationToken =>
                                    {
                                        observerCalled = true;
                                        return Task.CompletedTask;
                                    }))
                        },
                        () =>
                        {
                            visibilityCommitted = true;
                            return true;
                        })
                    .GetAwaiter()
                    .GetResult();
            Check(
                "failed newcomer roster stays hidden and skips observers",
                !failedPublication &&
                !visibilityCommitted &&
                !observerCalled,
                ref failures);

            var failedObserverIndex = -1;
            var committedWithFailedObserver =
                TownHandler.FanOutAreaRosterAsync(
                        cancellationToken => Task.CompletedTask,
                        "newcomer",
                        new[]
                        {
                            (
                                Target: "observer",
                                Send: (Func<CancellationToken, Task>)(
                                    cancellationToken =>
                                        Task.FromException(
                                            new IOException(
                                                "simulated observer disconnect"))))
                        },
                        () => true,
                        index => failedObserverIndex = index)
                    .GetAwaiter()
                    .GetResult();
            Check(
                "failed existing observer is selected for forced resync",
                committedWithFailedObserver &&
                failedObserverIndex == 0,
                ref failures);
        }

        private static PartyManager GetPartyManager(
            GameProtocolHandler protocol)
        {
            var field = typeof(GameProtocolHandler).GetField(
                "_partyManager",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return (PartyManager)field?.GetValue(protocol) ??
                   throw new InvalidOperationException(
                       "GameProtocolHandler party manager missing");
        }

        private static PartyMember BuildPartyMember(
            EnhancedClientSession session) =>
            new PartyMember
            {
                UserId = session.Player.UserId,
                CharacterId = session.Player.CharacterId,
                Name = Encoding.ASCII.GetString(
                    session.Player.Name),
                Level = session.Player.Level,
                Job = session.Player.Job,
                SessionId = session.SessionId
            };

        private static void ConfigurePlayer(
            EnhancedClientSession session,
            int characterId,
            ushort userId,
            byte townId,
            byte areaId,
            short x,
            short y)
        {
            session.Player.CharacterId = characterId;
            session.Player.UserId = userId;
            session.Player.Name = Encoding.ASCII.GetBytes(
                "Town" + userId);
            session.Player.Level = 1;
            session.Player.Job = 0;
            session.Player.UserState = 0;
            ResetPosition(
                session, townId, areaId, x, y, 5);
        }

        private static void ResetPosition(
            EnhancedClientSession session,
            byte townId,
            byte areaId,
            short x,
            short y,
            byte direction)
        {
            session.Player.CurTownId = townId;
            session.Player.CurAreaId = areaId;
            session.Player.CurPosX = x;
            session.Player.CurPosY = y;
            session.Player.CurDirection = direction;
            session.Player.CurAreaState = 3;
            session.Player.UserState = 0;
            session.Player.DungeonSelectionPending = false;
            session.Player.TownPresenceReady = true;
        }

        private static byte[] BuildDestination(
            byte townId,
            byte areaId,
            short x,
            short y,
            byte direction)
        {
            var body = new byte[7];
            body[0] = townId;
            body[1] = areaId;
            BitConverter.GetBytes(x).CopyTo(body, 2);
            BitConverter.GetBytes(y).CopyTo(body, 4);
            body[6] = direction;
            return body;
        }

        private static byte[] BuildItemTeleportRequest(
            byte townId)
        {
            var body = new byte[8];
            BitConverter.GetBytes((short)0).CopyTo(body, 0);
            BitConverter.GetBytes(0x0027AC4E).CopyTo(body, 2);
            body[7] = townId;
            return body;
        }

        private static void Dispatch(
            GameProtocolHandler protocol,
            EnhancedClientSession session,
            ushort type,
            byte[] body)
        {
            protocol.OnPacketReceived_86JP(
                    session,
                    new GamePacketHeader
                    {
                        cmd = 0x01,
                        type = type,
                        length = (uint)(15 + (body?.Length ?? 0))
                    },
                    body)
                .GetAwaiter()
                .GetResult();
        }

        private static bool IsAt(
            EnhancedClientSession session,
            byte townId,
            byte areaId,
            short x,
            short y,
            byte direction) =>
            session.Player.CurTownId == townId &&
            session.Player.CurAreaId == areaId &&
            session.Player.CurPosX == x &&
            session.Player.CurPosY == y &&
            session.Player.CurDirection == direction &&
            session.Player.TownPresenceReady;

        private static int FindPacketIndex(
            IReadOnlyList<CapturedPacket> packets,
            Func<CapturedPacket, bool> predicate)
        {
            for (var index = 0; index < packets.Count; index++)
            {
                if (predicate(packets[index]))
                    return index;
            }

            return -1;
        }

        private static bool HasPacket(
            IReadOnlyList<CapturedPacket> packets,
            byte command,
            ushort type) =>
            packets.Any(packet =>
                packet.Command == command && packet.Type == type);

        private static bool HasTownAreaPublication(
            IReadOnlyList<CapturedPacket> packets) =>
            packets.Any(packet =>
                packet.Command == 0x00 &&
                (packet.Type == 0x0002 ||
                 packet.Type == 0x0017 ||
                 packet.Type == 0x0018));

        private static bool HasSuccessAck(
            IReadOnlyList<CapturedPacket> packets,
            ushort type) =>
            packets.Any(packet =>
                packet.Command == 0x01 &&
                packet.Type == type &&
                packet.Body.SequenceEqual(new byte[] { 0x01 }));

        private static bool HasCommandError(
            IReadOnlyList<CapturedPacket> packets,
            ushort type,
            byte error) =>
            packets.Any(packet =>
                packet.Command == 0x01 &&
                packet.Type == type &&
                packet.Body.SequenceEqual(
                    new byte[] { 0x00, error }));

        private static bool IsUserAreaFor(
            CapturedPacket packet,
            ushort userId,
            byte townId,
            byte areaId,
            short x,
            short y) =>
            packet != null &&
            packet.Command == 0x00 &&
            packet.Type == 0x0017 &&
            packet.Body.Length >= 10 &&
            BitConverter.ToUInt16(packet.Body, 0) == userId &&
            packet.Body[2] == townId &&
            packet.Body[3] == areaId &&
            BitConverter.ToInt16(packet.Body, 4) == x &&
            BitConverter.ToInt16(packet.Body, 6) == y;

        private static bool IsRosterFor(
            CapturedPacket packet,
            byte townId,
            byte areaId,
            IReadOnlyCollection<ushort> expectedUsers,
            ushort excludedUser)
        {
            if (packet == null ||
                packet.Command != 0x00 ||
                packet.Type != 0x0018 ||
                packet.Body.Length < 4 ||
                packet.Body[0] != townId ||
                packet.Body[1] != areaId)
            {
                return false;
            }

            var count = BitConverter.ToUInt16(packet.Body, 2);
            if (packet.Body.Length != 4 + count * 8)
                return false;
            var users = new HashSet<ushort>();
            for (var index = 0; index < count; index++)
            {
                users.Add(BitConverter.ToUInt16(
                    packet.Body, 4 + index * 8));
            }

            return expectedUsers.All(users.Contains) &&
                   !users.Contains(excludedUser) &&
                   users.Count == expectedUsers.Count;
        }

        private static void Check(
            string label,
            bool condition,
            ref int failures)
        {
            Console.WriteLine(
                "[" + (condition ? "PASS" : "FAIL") + "] " +
                label);
            if (!condition)
                failures++;
        }

        private static void DeleteDatabaseFiles(
            string databasePath)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                try
                {
                    var path = databasePath + suffix;
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }
            }
        }

        private sealed class CapturedPacket
        {
            internal byte Command { get; private set; }
            internal ushort Type { get; private set; }
            internal byte[] Body { get; private set; }

            internal static CapturedPacket Parse(byte[] data)
            {
                if (data == null || data.Length < 15)
                    throw new InvalidDataException(
                        "truncated game packet");
                var length = BitConverter.ToInt32(data, 3);
                if (length != data.Length)
                    throw new InvalidDataException(
                        "invalid game packet length");

                var body = new byte[length - 15];
                Buffer.BlockCopy(data, 15, body, 0, body.Length);
                return new CapturedPacket
                {
                    Command = data[0],
                    Type = BitConverter.ToUInt16(data, 1),
                    Body = body
                };
            }
        }

        private sealed class ConnectedSession : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly TcpClient _peer;
            private readonly TcpClient _server;

            private ConnectedSession(
                TcpListener listener,
                TcpClient peer,
                TcpClient server,
                EnhancedClientSession session)
            {
                _listener = listener;
                _peer = peer;
                _server = server;
                Session = session;
            }

            internal EnhancedClientSession Session { get; }

            internal static ConnectedSession Create(
                int listenerPort)
            {
                var listener = new TcpListener(
                    IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)
                    listener.LocalEndpoint).Port;
                var peer = new TcpClient
                {
                    ReceiveBufferSize = 1024 * 1024
                };
                var connect = peer.ConnectAsync(
                    IPAddress.Loopback, port);
                var server = listener.AcceptTcpClient();
                connect.GetAwaiter().GetResult();
                server.SendBufferSize = 1024 * 1024;
                return new ConnectedSession(
                    listener,
                    peer,
                    server,
                    new EnhancedClientSession(
                        server,
                        new GamePacketHeader(),
                        listenerPort));
            }

            internal IReadOnlyList<CapturedPacket> DrainPackets()
            {
                var bytes = new List<byte>();
                var quiet = Stopwatch.StartNew();
                while (quiet.Elapsed <
                       TimeSpan.FromMilliseconds(50))
                {
                    var available = _peer.Available;
                    if (available <= 0)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    var chunk = new byte[available];
                    var offset = 0;
                    while (offset < chunk.Length)
                    {
                        var read = _peer.GetStream().Read(
                            chunk,
                            offset,
                            chunk.Length - offset);
                        if (read <= 0)
                            throw new EndOfStreamException(
                                "town teleport test socket closed");
                        offset += read;
                    }
                    bytes.AddRange(chunk);
                    quiet.Restart();
                }

                var packets = new List<CapturedPacket>();
                var packetOffset = 0;
                var data = bytes.ToArray();
                while (packetOffset + 15 <= data.Length)
                {
                    var length = BitConverter.ToInt32(
                        data, packetOffset + 3);
                    if (length < 15 ||
                        packetOffset + length > data.Length)
                    {
                        throw new InvalidDataException(
                            "truncated town teleport packet");
                    }

                    var packet = new byte[length];
                    Buffer.BlockCopy(
                        data,
                        packetOffset,
                        packet,
                        0,
                        length);
                    packets.Add(CapturedPacket.Parse(packet));
                    packetOffset += length;
                }

                if (packetOffset != data.Length)
                {
                    throw new InvalidDataException(
                        "trailing town teleport packet bytes");
                }
                return packets;
            }

            internal void DisableServerSends()
            {
                _server.Client.Shutdown(SocketShutdown.Send);
            }

            public void Dispose()
            {
                _server.Dispose();
                _peer.Dispose();
                _listener.Stop();
            }
        }
    }
}
