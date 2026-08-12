using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class PlayerTradeSelfTest
    {
        private const int FirstItemId = 10007331;
        private const int SecondItemId = 10007330;
        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== PLAYER_TRADE selftest ===");

            TestItemAndGoldExchange();
            TestStaleOfferIsAtomic();
            TestDuplicateSourceIsAtomic();
            TestRestrictedItemRejected();
            TestInsufficientGoldRejected();
            TestVirtualItemExchange();
            TestPairPersistenceUsesNewInventory();
            TestPairPersistenceFailureIsAtomic();
            TestPeerItemNotificationLayout();

            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
            return _fail == 0 ? 0 : 1;
        }

        private static void TestItemAndGoldExchange()
        {
            var first = CreateInventory(980001, 980000, 1000);
            var second = CreateInventory(980002, 980003, 250);
            PutStack(first, 9, FirstItemId, 5);
            PutStack(second, 9, SecondItemId, 3);

            Check("first item offer validates",
                PlayerTradeRuntimeService.TryCreateOffer(
                    first, 0, 9, 2, out var firstItem, out _));
            Check("first gold offer validates",
                PlayerTradeRuntimeService.TryCreateOffer(
                    first, 0, 0, 300, out var firstGold, out _));
            Check("second item offer validates",
                PlayerTradeRuntimeService.TryCreateOffer(
                    second, 0, 9, 1, out var secondItem, out _));
            Check("second gold offer validates",
                PlayerTradeRuntimeService.TryCreateOffer(
                    second, 0, 0, 50, out var secondGold, out _));

            var ok = PlayerTradeRuntimeService.TryApplyExchange(
                first,
                new[] { firstItem, firstGold },
                second,
                new[] { secondItem, secondGold },
                out var result);
            Check(
                $"mixed item/gold exchange succeeds ({result.Failure ?? "none"})",
                ok && result.Success);
            Check("first inventory receives item and net gold",
                first.CountMainItem(FirstItemId) == 3
                && first.CountMainItem(SecondItemId) == 1
                && Gold(first) == 750);
            Check("second inventory receives item and net gold",
                second.CountMainItem(FirstItemId) == 2
                && second.CountMainItem(SecondItemId) == 2
                && Gold(second) == 500);
        }

        private static void TestStaleOfferIsAtomic()
        {
            var first = CreateInventory(980011, 980010, 100);
            var second = CreateInventory(980012, 980013, 200);
            PutStack(first, 9, FirstItemId, 4);
            Check("stale-source fixture offer validates",
                PlayerTradeRuntimeService.TryCreateOffer(
                    first, 0, 9, 3, out var offer, out _));

            PutStack(first, 9, FirstItemId, 2);
            var ok = PlayerTradeRuntimeService.TryApplyExchange(
                first,
                new[] { offer },
                second,
                Array.Empty<PlayerTradeOffer>(),
                out _);
            Check("stale source rejects whole exchange", !ok);
            Check("stale rejection leaves both inventories unchanged",
                first.CountMainItem(FirstItemId) == 2
                && second.CountMainItem(FirstItemId) == 0
                && Gold(first) == 100
                && Gold(second) == 200);
        }

        private static void TestDuplicateSourceIsAtomic()
        {
            var first = CreateInventory(980021, 980020, 100);
            var second = CreateInventory(980022, 980023, 100);
            PutStack(first, 9, FirstItemId, 5);
            Check("duplicate-source fixture offer validates",
                PlayerTradeRuntimeService.TryCreateOffer(
                    first, 0, 9, 1, out var offer, out _));
            if (offer == null)
                return;

            var ok = PlayerTradeRuntimeService.TryApplyExchange(
                first,
                new[] { offer, offer.Copy() },
                second,
                Array.Empty<PlayerTradeOffer>(),
                out _);
            Check("duplicate source rejects whole exchange", !ok);
            Check("duplicate rejection does not remove items",
                first.CountMainItem(FirstItemId) == 5
                && second.CountMainItem(FirstItemId) == 0);
        }

        private static void TestRestrictedItemRejected()
        {
            var inventory = CreateInventory(980031, 980030, 0);
            var restricted = ItemCore.Create(ItemCore.KindConsumable, FirstItemId);
            restricted.Count = 1;
            restricted.TradeRestriction = 1;
            inventory.AttachItem(InventoryListType.Main, 9, restricted);

            Check("instance trade restriction rejects offer",
                !PlayerTradeRuntimeService.TryCreateOffer(
                    inventory, 0, 9, 1, out _, out _));
        }

        private static void TestInsufficientGoldRejected()
        {
            var inventory = CreateInventory(980041, 980040, 99);
            Check("gold offer cannot exceed live balance",
                !PlayerTradeRuntimeService.TryCreateOffer(
                    inventory, 0, 0, 100, out _, out _));
        }

        private static void TestVirtualItemExchange()
        {
            const short cubeSlot = 354;
            const int cubeItemId = 3037;
            var first = CreateInventory(980051, 980050, 0);
            var second = CreateInventory(980052, 980053, 0);
            first.AttachMainVirtualCount(cubeSlot, cubeItemId, 3000);
            second.AttachMainVirtualCount(cubeSlot, cubeItemId, 40);

            Check("virtual cube offer validates",
                PlayerTradeRuntimeService.TryCreateOffer(
                    first,
                    PlayerTradeRuntimeService.VirtualItemSpace,
                    cubeSlot,
                    500,
                    out var cubeOffer,
                    out _));

            var ok = PlayerTradeRuntimeService.TryApplyExchange(
                first,
                new[] { cubeOffer },
                second,
                Array.Empty<PlayerTradeOffer>(),
                out var result);
            Check(
                $"virtual cube exchange succeeds ({result.Failure ?? "none"})",
                ok && result.Success);
            Check("virtual cube counts move between matching virtual slots",
                first.GetMainVirtualCount(cubeSlot)?.Count == 2500
                && second.GetMainVirtualCount(cubeSlot)?.Count == 540);
            Check("virtual cube exchange records targeted inventory changes",
                result.FirstChanges.Slots.Count == 1
                && result.FirstChanges.Slots[0].SlotIndex == cubeSlot
                && result.SecondChanges.Slots.Count == 1
                && result.SecondChanges.Slots[0].SlotIndex == cubeSlot);
        }

        private static void TestPairPersistenceUsesNewInventory()
        {
            const int firstCharacterId = 980061;
            const int firstAccountId = 980060;
            const int secondCharacterId = 980062;
            const int secondAccountId = 980063;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "dfo-player-trade-" + Guid.NewGuid().ToString("N") + ".db");
            var previousDatabasePath = Environment.GetEnvironmentVariable(
                "INVENTORY_DATABASE_PATH");
            Environment.SetEnvironmentVariable(
                "INVENTORY_DATABASE_PATH",
                databasePath);
            try
            {
                SeedIdentity(
                    databasePath,
                    firstCharacterId,
                    firstAccountId,
                    secondCharacterId,
                    secondAccountId);
                var first = CreateInventory(
                    firstCharacterId,
                    firstAccountId,
                    1000);
                var second = CreateInventory(
                    secondCharacterId,
                    secondAccountId,
                    250);
                PutStack(first, 9, FirstItemId, 2);
                PutStack(second, 9, SecondItemId, 1);
                first.SetMainVirtualCount(0, 1001);
                first.SetMainVirtualCount(0, 1000);
                second.SetMainVirtualCount(0, 251);
                second.SetMainVirtualCount(0, 250);

                PlayerTradeRuntimeService.TryCreateOffer(
                    first, 0, 9, 1, out var firstItem, out _);
                PlayerTradeRuntimeService.TryCreateOffer(
                    first, 0, 0, 300, out var firstGold, out _);
                PlayerTradeRuntimeService.TryCreateOffer(
                    second, 0, 9, 1, out var secondItem, out _);
                var firstLease = new InventoryLease(
                    Guid.NewGuid(), firstCharacterId, first, 1);
                var secondLease = new InventoryLease(
                    Guid.NewGuid(), secondCharacterId, second, 2);

                var executed = PlayerTradeRuntimeService.TryExecuteAndPersist(
                    firstLease,
                    new[] { firstItem, firstGold },
                    secondLease,
                    new[] { secondItem },
                    out var result);
                var reloadedFirst = LoadInventory(
                    databasePath,
                    firstCharacterId,
                    firstAccountId);
                var reloadedSecond = LoadInventory(
                    databasePath,
                    secondCharacterId,
                    secondAccountId);

                Check("trade persists both leases through the new inventory tables",
                    executed
                    && result.Success
                    && reloadedFirst.CountMainItem(FirstItemId) == 1
                    && reloadedFirst.CountMainItem(SecondItemId) == 1
                    && Gold(reloadedFirst) == 700
                    && reloadedSecond.CountMainItem(FirstItemId) == 1
                    && reloadedSecond.CountMainItem(SecondItemId) == 0
                    && Gold(reloadedSecond) == 550);
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    previousDatabasePath);
                SqliteConnection.ClearAllPools();
                DeleteDatabaseFiles(databasePath);
            }
        }

        private static void TestPairPersistenceFailureIsAtomic()
        {
            const int firstCharacterId = 980071;
            const int firstAccountId = 980070;
            const int secondCharacterId = 980072;
            const int secondAccountId = 980073;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "dfo-player-trade-failure-" +
                Guid.NewGuid().ToString("N") + ".db");
            var previousDatabasePath = Environment.GetEnvironmentVariable(
                "INVENTORY_DATABASE_PATH");
            Environment.SetEnvironmentVariable(
                "INVENTORY_DATABASE_PATH",
                databasePath);
            try
            {
                SeedIdentity(
                    databasePath,
                    firstCharacterId,
                    firstAccountId,
                    secondCharacterId,
                    secondAccountId);
                var first = CreateInventory(
                    firstCharacterId,
                    firstAccountId,
                    1000);
                var second = CreateInventory(
                    secondCharacterId,
                    secondAccountId,
                    250);
                PutStack(first, 9, FirstItemId, 1);
                PutStack(second, 9, SecondItemId, 1);
                var firstLease = new InventoryLease(
                    Guid.NewGuid(), firstCharacterId, first, 1);
                var secondLease = new InventoryLease(
                    Guid.NewGuid(), secondCharacterId, second, 2);
                Check("trade failure fixture persists initial inventories",
                    InventoryPersistenceService.SaveDirtyPair(
                        firstLease,
                        secondLease));

                PlayerTradeRuntimeService.TryCreateOffer(
                    first, 0, 9, 1, out var firstItem, out _);
                PlayerTradeRuntimeService.TryCreateOffer(
                    second, 0, 9, 1, out var secondItem, out _);
                CreateSecondOwnerSaveFailure(
                    databasePath,
                    secondCharacterId);

                var executed = PlayerTradeRuntimeService.TryExecuteAndPersist(
                    firstLease,
                    new[] { firstItem },
                    secondLease,
                    new[] { secondItem },
                    out _);
                var reloadedFirst = LoadInventory(
                    databasePath,
                    firstCharacterId,
                    firstAccountId);
                var reloadedSecond = LoadInventory(
                    databasePath,
                    secondCharacterId,
                    secondAccountId);

                Check($"pair persistence failure rolls back both database owners " +
                    $"executed={executed} " +
                    $"db1={reloadedFirst.CountMainItem(FirstItemId)}/" +
                    $"{reloadedFirst.CountMainItem(SecondItemId)} " +
                    $"db2={reloadedSecond.CountMainItem(FirstItemId)}/" +
                    $"{reloadedSecond.CountMainItem(SecondItemId)}",
                    !executed
                    && reloadedFirst.CountMainItem(FirstItemId) == 1
                    && reloadedFirst.CountMainItem(SecondItemId) == 0
                    && reloadedSecond.CountMainItem(FirstItemId) == 0
                    && reloadedSecond.CountMainItem(SecondItemId) == 1);
                Check($"pair persistence failure restores both online inventories " +
                    $"live1={first.CountMainItem(FirstItemId)}/" +
                    $"{first.CountMainItem(SecondItemId)} " +
                    $"live2={second.CountMainItem(FirstItemId)}/" +
                    $"{second.CountMainItem(SecondItemId)}",
                    first.CountMainItem(FirstItemId) == 1
                    && first.CountMainItem(SecondItemId) == 0
                    && second.CountMainItem(FirstItemId) == 0
                    && second.CountMainItem(SecondItemId) == 1);
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    previousDatabasePath);
                SqliteConnection.ClearAllPools();
                DeleteDatabaseFiles(databasePath);
            }
        }

        private static void CreateSecondOwnerSaveFailure(
            string databasePath,
            int secondCharacterId)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(
                databasePath,
                ServerPaths.SchemaFilePath);
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $@"
CREATE TRIGGER player_trade_second_owner_update_abort
BEFORE UPDATE ON character_new_items
WHEN OLD.owner_scope = 'character'
 AND OLD.owner_id = {secondCharacterId}
BEGIN
    SELECT RAISE(ABORT, 'player trade second owner save failure');
END;
CREATE TRIGGER player_trade_second_owner_insert_abort
BEFORE INSERT ON character_new_items
WHEN NEW.owner_scope = 'character'
 AND NEW.owner_id = {secondCharacterId}
BEGIN
    SELECT RAISE(ABORT, 'player trade second owner save failure');
END;
CREATE TRIGGER player_trade_second_owner_delete_abort
BEFORE DELETE ON character_new_items
WHEN OLD.owner_scope = 'character'
 AND OLD.owner_id = {secondCharacterId}
BEGIN
    SELECT RAISE(ABORT, 'player trade second owner save failure');
END;";
            command.ExecuteNonQuery();
        }

        private static void SeedIdentity(
            string databasePath,
            int firstCharacterId,
            int firstAccountId,
            int secondCharacterId,
            int secondAccountId)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(
                databasePath,
                ServerPaths.SchemaFilePath);
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@firstAccount, 'player-trade-first', ''),
       (@secondAccount, 'player-trade-second', '');
INSERT INTO characters (character_id, account_id, name)
VALUES (@firstCharacter, @firstAccount, 'player-trade-first'),
       (@secondCharacter, @secondAccount, 'player-trade-second');";
            command.Parameters.AddWithValue("@firstCharacter", firstCharacterId);
            command.Parameters.AddWithValue("@firstAccount", firstAccountId);
            command.Parameters.AddWithValue("@secondCharacter", secondCharacterId);
            command.Parameters.AddWithValue("@secondAccount", secondAccountId);
            command.ExecuteNonQuery();
        }

        private static InventoryService LoadInventory(
            string databasePath,
            int characterId,
            int accountId)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(
                databasePath,
                ServerPaths.SchemaFilePath);
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            return InventoryService.LoadFromDb(
                connection,
                characterId,
                accountId);
        }

        private static void DeleteDatabaseFiles(string databasePath)
        {
            foreach (var path in new[]
                     {
                         databasePath,
                         databasePath + "-wal",
                         databasePath + "-shm",
                     })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        private static void TestPeerItemNotificationLayout()
        {
            var item = ItemCore.Create(ItemCore.KindEquipment, 0x11223344);
            item.Value = 0x55667788;
            item.Attr = 0x21;
            item.Durability = 0x3243;
            item.SealFlag = 4;
            item.EnchantCardId = 0x01020304;
            item.EnchantUpgradeCount = 5;
            item.AmplifyType = 6;
            item.AmplifyValue = 0x0708;
            item.ExpireTime = 0x10203040;
            item.GenuineUpgrade = 9;
            item.EmancipateEquipmentLevel = 10;
            item.TradeRestriction = 11;
            item.TailUnknown0 = 0x1213;
            item.TailUnknown1 = 14;
            item.TailUnknown2 = 15;
            item.TailUnknown3 = 16;
            item.RemainUseCount = 17;
            item.SortLockFlag = 1;

            var body = TradeItemChangeBodyBuilder.BuildItem(3, item);
            Check("peer-item notification uses the compact 86JP trade entry",
                body.Length == TradeItemChangeBodyBuilder.EmptyOrPlainItemLength);
            Check("peer-item notification keeps verified common fields",
                BitConverter.ToInt16(body, 0) == 3
                && BitConverter.ToInt32(body, 2) == item.ItemId
                && BitConverter.ToInt32(body, 6) == item.Value
                && body[10] == item.Attr
                && BitConverter.ToUInt16(body, 11) == item.Durability
                && BitConverter.ToInt32(body, 13) == item.EnchantCardId
                && body[17] == item.EnchantUpgradeCount
                && body[18] == item.AmplifyType
                && BitConverter.ToUInt16(body, 19) == item.AmplifyValue
                && BitConverter.ToInt32(body, 21) == item.ExpireTime
                && body[25] == item.GenuineUpgrade
                && body[26] == item.EmancipateEquipmentLevel
                && body[27] == item.TradeRestriction
                && BitConverter.ToUInt16(body, 28) == item.TailUnknown0
                && body[30] == item.TailUnknown1
                && body[31] == item.TailUnknown2
                && body[32] == item.TailUnknown3
                && body[33] == item.RemainUseCount
                && body[34] == 1);

            var gold = TradeItemChangeBodyBuilder.BuildGold(0, 123456);
            var empty = TradeItemChangeBodyBuilder.BuildEmpty(3);
            Check("gold and removal notifications keep the same fixed tail",
                gold.Length == TradeItemChangeBodyBuilder.EmptyOrPlainItemLength
                && BitConverter.ToInt32(gold, 2) == 0
                && BitConverter.ToInt32(gold, 6) == 123456
                && empty.Length == TradeItemChangeBodyBuilder.EmptyOrPlainItemLength
                && BitConverter.ToInt32(empty, 2) == -1);
        }

        private static InventoryService CreateInventory(
            int characterId,
            int accountId,
            int gold)
        {
            var inventory = new InventoryService(characterId, accountId);
            inventory.SetListParam16(InventoryListType.Main, 24);
            inventory.AttachMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart,
                0,
                gold);
            inventory.ClearDirtyState();
            return inventory;
        }

        private static void PutStack(
            InventoryService inventory,
            short slot,
            int itemId,
            int count)
        {
            var item = ItemCore.Create(ItemCore.KindConsumable, itemId);
            item.Count = count;
            inventory.SetItem(InventoryListType.Main, slot, item);
        }

        private static int Gold(InventoryService inventory)
        {
            return inventory.GetMainVirtualCount(
                InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
        }

        private static void Check(string name, bool ok)
        {
            if (ok)
            {
                _pass++;
                Console.WriteLine($"[PASS] {name}");
            }
            else
            {
                _fail++;
                Console.WriteLine($"[FAIL] {name}");
            }
        }
    }
}
