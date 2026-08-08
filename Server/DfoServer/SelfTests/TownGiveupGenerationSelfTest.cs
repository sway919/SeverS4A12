using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace DfoServer.SelfTests
{
    public static class TownGiveupGenerationSelfTest
    {
        private const int ListenerPort = 11021;
        private const int ObserverCharacterId = 8209;

        public static int Run()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "town-giveup-generation-" +
                Guid.NewGuid().ToString("N") + ".db");
            var previousDatabasePath =
                Environment.GetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH");
            var previousPartyCoop =
                Environment.GetEnvironmentVariable(
                    "DFO_PARTY_DUNGEON_COOP");

            try
            {
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    databasePath);
                Environment.SetEnvironmentVariable(
                    "DFO_PARTY_DUNGEON_COOP",
                    "1");
                SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);

                VerifyProductionDispatchRejectsStale(
                    ref failures);
                VerifyStaleGiveupIsInert(
                    0x002A,
                    "GIVEUP_GAME",
                    8201,
                    ref failures);
                VerifyStaleGiveupIsInert(
                    0x0084,
                    "BACK_2_VILLAGE",
                    8202,
                    ref failures);
                VerifyPartyGenerationMismatchIsSkipped(
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] town giveup generation fixture: " + ex);
                failures++;
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    previousDatabasePath);
                Environment.SetEnvironmentVariable(
                    "DFO_PARTY_DUNGEON_COOP",
                    previousPartyCoop);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteSqliteFiles(databasePath);
            }

            Console.WriteLine(
                $"=== TOWN_GIVEUP_GENERATION result: failures={failures} ===");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyProductionDispatchRejectsStale(
            ref int failures)
        {
            const int characterId = 8199;
            using var oldConnection = ConnectedSession.Create();
            using var replacementConnection = ConnectedSession.Create();
            ConfigurePlayer(
                oldConnection.Session,
                characterId,
                (ushort)characterId,
                townId: 1,
                areaId: 1,
                presenceReady: false);
            ConfigurePlayer(
                replacementConnection.Session,
                characterId,
                (ushort)characterId,
                townId: 2,
                areaId: 6,
                presenceReady: true);
            var oldRun = AttachRun(
                oldConnection.Session,
                new DungeonInstance(1002, 1));
            var sessions = new SessionDirectory();
            using var protocol = new GameProtocolHandler(sessions);
            sessions.Register(characterId, oldConnection.Session);
            sessions.RegisterReplacingAsync(
                    characterId,
                    replacementConnection.Session)
                .GetAwaiter()
                .GetResult();

            protocol.OnPacketReceived_86JP(
                    oldConnection.Session,
                    CreateHeader(0x002A),
                    Array.Empty<byte>())
                .GetAwaiter()
                .GetResult();

            Check(
                "production dispatch rejects a stale GIVEUP generation",
                ReferenceEquals(
                    oldConnection.Session.Player.CurrentRun,
                    oldRun) &&
                oldRun.RunState == DungeonRunState.Active &&
                oldConnection.DrainByteCount() == 0,
                ref failures);
        }

        private static void VerifyStaleGiveupIsInert(
            ushort packetType,
            string requestName,
            int characterId,
            ref int failures)
        {
            using var oldConnection = ConnectedSession.Create();
            using var replacementConnection = ConnectedSession.Create();
            using var observerConnection = ConnectedSession.Create();

            ConfigurePlayer(
                oldConnection.Session,
                characterId,
                (ushort)characterId,
                townId: 1,
                areaId: 1,
                presenceReady: false);
            ConfigurePlayer(
                replacementConnection.Session,
                characterId,
                (ushort)characterId,
                townId: 2,
                areaId: 6,
                presenceReady: true);
            ConfigurePlayer(
                observerConnection.Session,
                ObserverCharacterId,
                (ushort)ObserverCharacterId,
                townId: 1,
                areaId: 1,
                presenceReady: true);

            var oldRun = AttachRun(
                oldConnection.Session,
                new DungeonInstance(1002, 1));
            var repository = new RecordingCharacterRepository();
            var sessions = new SessionDirectory();
            var transitions =
                new CharacterTransitionCoordinator(sessions);
            var handler = new TownHandler(
                repository,
                selectDataSource: null,
                partyManager: null,
                sessions,
                refresh: null,
                dungeonInstances: null,
                transitions);

            sessions.Register(characterId, oldConnection.Session);
            sessions.Register(
                ObserverCharacterId,
                observerConnection.Session);
            var displaced = sessions.RegisterReplacingAsync(
                    characterId,
                    replacementConnection.Session)
                .GetAwaiter()
                .GetResult();
            Check(
                requestName + " fixture replaces the old generation",
                ReferenceEquals(displaced, oldConnection.Session),
                ref failures);

            handler.Handle_ENUM_CMDPACKET_GIVEUP_GAME(
                    oldConnection.Session,
                    CreateHeader(packetType),
                    Array.Empty<byte>())
                .GetAwaiter()
                .GetResult();

            Check(
                requestName +
                " cannot detach the stale session's dungeon run",
                ReferenceEquals(
                    oldConnection.Session.Player.CurrentRun,
                    oldRun) &&
                oldRun.RunState == DungeonRunState.Active,
                ref failures);
            Check(
                requestName +
                " cannot persist the stale session's town position",
                !repository.WasPositionUpdated(characterId),
                ref failures);
            Check(
                requestName +
                " sends no ACK or town projection to the stale socket",
                oldConnection.DrainByteCount() == 0,
                ref failures);
            Check(
                requestName +
                " sends no area broadcast from the stale generation",
                observerConnection.DrainByteCount() == 0,
                ref failures);
        }

        private static void VerifyPartyGenerationMismatchIsSkipped(
            ref int failures)
        {
            const int leaderCharacterId = 8211;
            const int memberCharacterId = 8212;

            using var leaderConnection = ConnectedSession.Create();
            using var oldMemberConnection = ConnectedSession.Create();
            using var newMemberConnection = ConnectedSession.Create();

            ConfigurePlayer(
                leaderConnection.Session,
                leaderCharacterId,
                (ushort)leaderCharacterId,
                townId: 1,
                areaId: 1,
                presenceReady: false);
            ConfigurePlayer(
                oldMemberConnection.Session,
                memberCharacterId,
                (ushort)memberCharacterId,
                townId: 1,
                areaId: 1,
                presenceReady: false);
            ConfigurePlayer(
                newMemberConnection.Session,
                memberCharacterId,
                (ushort)memberCharacterId,
                townId: 1,
                areaId: 1,
                presenceReady: false);

            var sharedInstance = new DungeonInstance(1002, 1);
            AttachRun(leaderConnection.Session, sharedInstance);
            var replacementRun =
                AttachRun(newMemberConnection.Session, sharedInstance);

            var parties = new PartyManager();
            var created = parties.CreateParty(
                BuildPartyMember(leaderConnection.Session));
            var joined = created.Ok && parties.Join(
                created.Party.PartyId,
                BuildPartyMember(oldMemberConnection.Session)).Ok;
            Check(
                "party mismatch fixture records the old member generation",
                joined &&
                created.Party.GetMember(
                    (ushort)memberCharacterId)?.SessionId ==
                    oldMemberConnection.Session.SessionId,
                ref failures);

            var repository = new RecordingCharacterRepository();
            var sessions = new SessionDirectory();
            var transitions =
                new CharacterTransitionCoordinator(sessions);
            var handler = new TownHandler(
                repository,
                selectDataSource: null,
                parties,
                sessions,
                refresh: null,
                dungeonInstances: null,
                transitions);

            sessions.Register(
                leaderCharacterId,
                leaderConnection.Session);
            sessions.Register(
                memberCharacterId,
                oldMemberConnection.Session);
            sessions.RegisterReplacingAsync(
                    memberCharacterId,
                    newMemberConnection.Session)
                .GetAwaiter()
                .GetResult();

            handler.Handle_ENUM_CMDPACKET_GIVEUP_GAME(
                    leaderConnection.Session,
                    CreateHeader(0x0084),
                    Array.Empty<byte>())
                .GetAwaiter()
                .GetResult();

            var leaderProjectionBytes =
                leaderConnection.DrainByteCount();
            Check(
                "BACK_2_VILLAGE still completes the current leader return",
                leaderConnection.Session.Player.CurrentRun == null &&
                repository.WasPositionUpdated(leaderCharacterId) &&
                leaderProjectionBytes > 0,
                ref failures);
            Check(
                "BACK_2_VILLAGE does not pull a replacement that is absent " +
                "from the party generation",
                ReferenceEquals(
                    newMemberConnection.Session.Player.CurrentRun,
                    replacementRun) &&
                replacementRun.RunState == DungeonRunState.Active &&
                !repository.WasPositionUpdated(memberCharacterId),
                ref failures);
            Check(
                "party generation mismatch sends no town projection to the " +
                "replacement member",
                newMemberConnection.DrainByteCount() == 0,
                ref failures);
        }

        private static DungeonRun AttachRun(
            EnhancedClientSession session,
            DungeonInstance instance)
        {
            var run = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                runGeneration: 1,
                DungeonRunState.Active)
            {
                TownReturnAnchor = new DungeonTownReturnAnchor(
                    townId: 1,
                    areaId: 1,
                    x: 100,
                    y: 200,
                    direction: 5,
                    areaState: 3)
            };
            if (!session.Player.TryAttachResumedDungeonRun(run))
            {
                throw new InvalidOperationException(
                    "could not attach the giveup self-test dungeon run");
            }

            session.Player.UserState = 0x01;
            session.Player.TownPresenceReady = false;
            return run;
        }

        private static void ConfigurePlayer(
            EnhancedClientSession session,
            int characterId,
            ushort userId,
            byte townId,
            byte areaId,
            bool presenceReady)
        {
            session.Player.CharacterId = characterId;
            session.Player.UserId = userId;
            session.Player.Level = 1;
            session.Player.CurTownId = townId;
            session.Player.CurAreaId = areaId;
            session.Player.CurPosX = 100;
            session.Player.CurPosY = 200;
            session.Player.CurDirection = 5;
            session.Player.CurAreaState = 3;
            session.Player.UserState = 0x00;
            session.Player.TownPresenceReady = presenceReady;
        }

        private static PartyMember BuildPartyMember(
            EnhancedClientSession session) =>
            new PartyMember
            {
                UserId = session.Player.UserId,
                CharacterId = session.Player.CharacterId,
                SessionId = session.SessionId,
                Name = "generation-test",
                Level = session.Player.Level,
                Job = session.Player.Job
            };

        private static GamePacketHeader CreateHeader(ushort type) =>
            new GamePacketHeader
            {
                cmd = 0x01,
                type = type,
                length = 15
            };

        private static void DeleteSqliteFiles(string databasePath)
        {
            foreach (var suffix in new[]
                     {
                         string.Empty,
                         "-journal",
                         "-wal",
                         "-shm"
                     })
            {
                var path = databasePath + suffix;
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        "[WARN] could not clean town giveup test file " +
                        path + ": " + ex.Message);
                }
            }
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine(
                (condition ? "[OK] " : "[FAIL] ") + name);
            if (!condition)
                failures++;
        }

        private sealed class RecordingCharacterRepository
            : ICharacterRepository
        {
            private readonly HashSet<int> _positionUpdates =
                new HashSet<int>();

            internal bool WasPositionUpdated(int characterId)
            {
                lock (_positionUpdates)
                    return _positionUpdates.Contains(characterId);
            }

            public CharacterRecord GetById(int characterId) => null;

            public IReadOnlyList<CharacterRecord> ListByAccount(
                int accountId) => Array.Empty<CharacterRecord>();

            public int Create(CharacterRecord record) => 0;

            public void UpdatePosition(
                int characterId,
                byte townId,
                byte areaId,
                short posX,
                short posY,
                byte direction,
                byte areaState)
            {
                lock (_positionUpdates)
                    _positionUpdates.Add(characterId);
            }

            public void UpdateSeedFields(
                int characterId,
                byte[] name,
                byte job,
                byte growType,
                byte level,
                byte pvpGrade,
                byte pvpRatingGrade,
                byte userState,
                CharacterAppearanceEntry[] appearance,
                DateTime? createdAt = null)
            {
            }

            public void UpdateAppearance(
                int characterId,
                CharacterAppearanceEntry[] appearance)
            {
            }

            public void SoftDelete(int characterId)
            {
            }

            public CharacterRecord GetByName(string name) => null;

            public CharacterRecord GetByNameIncludingDeleted(
                string name) => null;

            public int CountByAccount(int accountId) => 0;

            public void SwapSlotIndexes(
                int accountId,
                byte slotA,
                byte slotB)
            {
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

            internal static ConnectedSession Create()
            {
                var listener = new TcpListener(
                    IPAddress.Loopback,
                    0);
                listener.Start();
                var endpoint = (IPEndPoint)listener.LocalEndpoint;
                var peer = new TcpClient();
                var connect = peer.ConnectAsync(
                    IPAddress.Loopback,
                    endpoint.Port);
                var server = listener.AcceptTcpClient();
                connect.GetAwaiter().GetResult();
                return new ConnectedSession(
                    listener,
                    peer,
                    server,
                    new EnhancedClientSession(
                        server,
                        new GamePacketHeader(),
                        ListenerPort));
            }

            internal int DrainByteCount()
            {
                var total = 0;
                var quiet = DateTime.UtcNow;
                while (DateTime.UtcNow - quiet <
                       TimeSpan.FromMilliseconds(50))
                {
                    var available = _peer.Available;
                    if (available <= 0)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    var buffer = new byte[available];
                    var read = _peer.GetStream().Read(
                        buffer,
                        0,
                        buffer.Length);
                    total += read;
                    quiet = DateTime.UtcNow;
                }
                return total;
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
