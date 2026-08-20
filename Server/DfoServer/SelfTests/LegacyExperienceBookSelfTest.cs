using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class LegacyExperienceBookSelfTest
    {
        private const int AccountId = 939032;
        private const int CharacterId = 939133;
        private const short BookSlot = 3;
        private static int _failures;

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine("=== LEGACY_EXPERIENCE_BOOK selftest ===");
            Check("all reviewed legacy experience books resolve",
                IsExpectedBook(1034, 100)
                && IsExpectedBook(1035, 1_000)
                && IsExpectedBook(1036, 10_000)
                && IsExpectedBook(1037, 100_000)
                && IsExpectedBook(1231, 1_000_000)
                && IsExpectedBook(1232, 10_000_000));

            var previousDatabasePath = Environment.GetEnvironmentVariable(
                "INVENTORY_DATABASE_PATH");
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "legacy-experience-book-selftest.db");
            DeleteDatabase(databasePath);
            Environment.SetEnvironmentVariable("INVENTORY_DATABASE_PATH", databasePath);
            var sessionId = Guid.NewGuid();
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                Seed(connectionString);
                var inventory = CreateInventoryWithBook(1034, 2);
                var lease = InventoryContext.Register(sessionId, inventory);
                PersistSeededInventory(connectionString, lease);

                var result = new ExperienceItemUseService(
                    databasePath,
                    ServerPaths.SchemaFilePath,
                    SystemRentalTimeProvider.Instance,
                    new ExperienceItemCooldownTracker()).UseBySlot(
                        CharacterId,
                        AccountId,
                        InventoryListType.Main,
                        BookSlot,
                        ExperienceItemUseLocation.Town);

                Check("using 100 EXP book succeeds",
                    result.Success
                    && result.GrantedExp == 100);
                Check("experience book grants exactly 100 EXP",
                    ReadCharacterExp(connectionString) == 100);
                Check("experience book consumption is committed",
                    inventory.GetItem(InventoryListType.Main, BookSlot)?.Count == 1
                    && ReadPersistedStackCount(connectionString) == 1);
            }
            finally
            {
                InventoryContext.Unregister(sessionId, CharacterId);
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    previousDatabasePath);
                DeleteDatabase(databasePath);
            }

            Console.WriteLine(_failures == 0 ? "PASS" : $"FAIL: {_failures}");
            return _failures == 0 ? 0 : 1;
        }

        private static bool IsExpectedBook(int itemId, uint value)
        {
            var definition = LegacyExperienceBookDataProvider.Resolve(itemId);
            return definition.IsExperienceLike
                && definition.IsSupported
                && definition.GrantKind == ExperienceItemGrantKind.Fixed
                && definition.Value == value
                && definition.MinimumLevel == 1;
        }

        private static InventoryService CreateInventoryWithBook(int itemId, int count)
        {
            var inventory = new InventoryService(CharacterId, AccountId);
            var item = ItemCore.Create(2, itemId);
            item.Count = count;
            inventory.SetItem(InventoryListType.Main, BookSlot, item);
            return inventory;
        }

        private static void PersistSeededInventory(
            string connectionString,
            InventoryLease lease)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    Check("seeded book stack persists",
                        InventoryPersistenceService.SaveDirtyInTransaction(
                            connection,
                            transaction,
                            lease));
                    transaction.Commit();
                }
                lease.Inventory.ClearDirtyState();
            }
        }

        private static void Seed(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'legacy-experience-book-selftest', '');
INSERT INTO characters (
    character_id, account_id, name, job, level, exp, bonus_sp, bonus_tp)
VALUES
    (@cid, @aid, 'legacy-exp-book-selftest', 0, 1, 0, 0, 0);
INSERT INTO character_subtype1_fields (character_id)
VALUES (@cid);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static long ReadCharacterExp(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "SELECT exp FROM characters WHERE character_id=@cid;";
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    return Convert.ToInt64(command.ExecuteScalar());
                }
            }
        }

        private static int ReadPersistedStackCount(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT item_core
FROM character_new_items
WHERE character_id=@cid AND list_type=@listType AND slot_index=@slot;";
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue("@listType", 0);
                    command.Parameters.AddWithValue("@slot", BookSlot);
                    return command.ExecuteScalar() is byte[] data
                        ? ItemCore.FromBytes(data).Count
                        : -1;
                }
            }
        }

        private static void DeleteDatabase(string databasePath)
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
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

        private static void Check(string label, bool success)
        {
            Console.WriteLine($"  [{(success ? "PASS" : "FAIL")}] {label}");
            if (!success)
                _failures++;
        }
    }
}
