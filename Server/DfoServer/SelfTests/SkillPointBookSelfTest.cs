using DfoServer.Game.Inventory;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class SkillPointBookSelfTest
    {
        private const int AccountId = 939031;
        private const int SpCharacterId = 939131;
        private const int TpCharacterId = 939132;
        private const int TpResetCharacterId = 939133;
        private const short SpBookSlot = 3;
        private const short TpBookSlot = 4;
        private const short TpResetBookSlot = 5;
        private const int Sp5BookItemId = 1031;
        private const int Tp5BookItemId = 1205;
        private const int TpResetBookItemId = 1206;
        private static int _failures;

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine("=== SKILL_POINT_BOOK selftest ===");
            Check("PVF item 1031 resolves to an SP+5 book",
                IsExpectedBook(SkillPointBookDataProvider.Resolve(1031), 5));
            Check("PVF item 1038 resolves to an SP+20 book",
                IsExpectedBook(SkillPointBookDataProvider.Resolve(1038), 20));
            Check("PVF test item 80003 resolves to an SP+5 book",
                IsExpectedBook(SkillPointBookDataProvider.Resolve(80003), 5));
            Check("PVF item 1204 resolves to a TP+1 book",
                IsExpectedBook(SkillPointBookDataProvider.Resolve(1204), 0, 1, 50));
            Check("PVF item 1205 resolves to a TP+5 book",
                IsExpectedBook(SkillPointBookDataProvider.Resolve(1205), 0, 5, 50));
            Check("PVF item 1206 resolves to a TP reset book",
                IsExpectedTpResetBook(SkillPointBookDataProvider.Resolve(1206)));
            Check("PVF event item 1253 resolves to a TP reset book",
                IsExpectedTpResetBook(SkillPointBookDataProvider.Resolve(1253)));
            Check("unrelated item is not classified as a skill-point book",
                !SkillPointBookDataProvider.Resolve(42).IsSkillPointBook);

            var previousDatabasePath = Environment.GetEnvironmentVariable(
                "INVENTORY_DATABASE_PATH");
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "skill-point-book-selftest.db");
            DeleteDatabase(databasePath);
            Environment.SetEnvironmentVariable("INVENTORY_DATABASE_PATH", databasePath);
            var spSessionId = Guid.NewGuid();
            var tpSessionId = Guid.NewGuid();
            var tpResetSessionId = Guid.NewGuid();
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                SeedCharacter(connectionString);
                var tpSkillId = FindSkill(isTp: true);
                var spSkillId = FindSkill(isTp: false);
                Check("reviewed PVF exposes one TP and one SP skill for reset coverage",
                    tpSkillId > 0 && spSkillId > 0);
                SeedResetSkills(connectionString, tpSkillId, spSkillId);

                var spInventory = CreateInventoryWithBook(
                    SpCharacterId, SpBookSlot, Sp5BookItemId, 2);
                var tpInventory = CreateInventoryWithBook(
                    TpCharacterId, TpBookSlot, Tp5BookItemId, 2);
                var tpResetInventory = CreateInventoryWithBook(
                    TpResetCharacterId, TpResetBookSlot, TpResetBookItemId, 2);
                var spLease = InventoryContext.Register(spSessionId, spInventory);
                var tpLease = InventoryContext.Register(tpSessionId, tpInventory);
                var tpResetLease = InventoryContext.Register(
                    tpResetSessionId, tpResetInventory);
                PersistSeededInventory(connectionString, spLease, "SP");
                PersistSeededInventory(connectionString, tpLease, "TP");
                PersistSeededInventory(connectionString, tpResetLease, "TP reset");

                var service = new ExperienceItemUseService(
                    databasePath,
                    ServerPaths.SchemaFilePath,
                    SystemRentalTimeProvider.Instance,
                    new ExperienceItemCooldownTracker());
                var spResult = service.UseBySlot(
                        SpCharacterId,
                        AccountId,
                        InventoryListType.Main,
                        SpBookSlot,
                        ExperienceItemUseLocation.Town);

                Check("using SP+5 book succeeds", spResult.Success);
                Check("result identifies the skill-point path",
                    spResult.IsSkillPointBook
                    && spResult.GrantedSp == 5
                    && spResult.GrantedExp == 0);
                Check("one book is consumed in memory",
                    spInventory.GetItem(InventoryListType.Main, SpBookSlot)?.Count == 1);
                Check("bonus_sp is increased exactly once",
                    ReadInt(connectionString, SpCharacterId, "bonus_sp") == 5);
                Check("consumed stack is committed atomically",
                    ReadPersistedStackCount(
                        connectionString, SpCharacterId, SpBookSlot) == 1);
                Check("both skill pages receive the five-point increase",
                    spResult.SkillPoints.Page0Sp == 5
                    && spResult.SkillPoints.Page1Sp == 5);

                var tpResult = service.UseBySlot(
                    TpCharacterId,
                    AccountId,
                    InventoryListType.Main,
                    TpBookSlot,
                    ExperienceItemUseLocation.Town);
                Check("using TP+5 book succeeds",
                    tpResult.Success
                    && tpResult.IsSkillPointBook
                    && tpResult.GrantedSp == 0
                    && tpResult.GrantedTp == 5);
                Check("TP book increments bonus_tp only",
                    ReadInt(connectionString, TpCharacterId, "bonus_tp") == 5
                    && ReadInt(connectionString, TpCharacterId, "bonus_sp") == 0);
                Check("TP book consumption is committed atomically",
                    tpInventory.GetItem(InventoryListType.Main, TpBookSlot)?.Count == 1
                    && ReadPersistedStackCount(
                        connectionString, TpCharacterId, TpBookSlot) == 1);

                var resetResult = service.UseBySlot(
                    TpResetCharacterId,
                    AccountId,
                    InventoryListType.Main,
                    TpResetBookSlot,
                    ExperienceItemUseLocation.Town);
                Check("using TP reset book succeeds",
                    resetResult.Success
                    && resetResult.IsSkillPointBook
                    && resetResult.IsTpResetBook
                    && resetResult.ResetTpSkillCount == 2);
                Check("TP reset removes paid TP skills from both pages",
                    CountPersistedSkill(
                        connectionString, TpResetCharacterId, tpSkillId) == 0);
                Check("TP reset preserves learned SP skills",
                    CountPersistedSkill(
                        connectionString, TpResetCharacterId, spSkillId) == 1);
                Check("TP reset refunds both independent TP pools",
                    resetResult.SkillPoints.Page0Tp == TpTableProvider.GetTotalTp(50)
                    && resetResult.SkillPoints.Page1Tp == TpTableProvider.GetTotalTp(50));
                Check("TP reset does not change bonus_tp",
                    ReadInt(connectionString, TpResetCharacterId, "bonus_tp") == 0);
                Check("TP reset book consumption is committed atomically",
                    tpResetInventory.GetItem(
                        InventoryListType.Main, TpResetBookSlot)?.Count == 1
                    && ReadPersistedStackCount(
                        connectionString, TpResetCharacterId, TpResetBookSlot) == 1);

                var noOpResetResult = service.UseBySlot(
                    TpResetCharacterId,
                    AccountId,
                    InventoryListType.Main,
                    TpResetBookSlot,
                    ExperienceItemUseLocation.Town);
                Check("TP reset without refundable TP is rejected without consuming",
                    !noOpResetResult.Success
                    && tpResetInventory.GetItem(
                        InventoryListType.Main, TpResetBookSlot)?.Count == 1
                    && ReadPersistedStackCount(
                        connectionString, TpResetCharacterId, TpResetBookSlot) == 1);
            }
            finally
            {
                InventoryContext.Unregister(spSessionId, SpCharacterId);
                InventoryContext.Unregister(tpSessionId, TpCharacterId);
                InventoryContext.Unregister(
                    tpResetSessionId, TpResetCharacterId);
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    previousDatabasePath);
                DeleteDatabase(databasePath);
            }

            Console.WriteLine(_failures == 0 ? "PASS" : $"FAIL: {_failures}");
            return _failures == 0 ? 0 : 1;
        }

        private static bool IsExpectedBook(
            SkillPointBookDefinition definition,
            int grantedSp)
            => IsExpectedBook(definition, grantedSp, 0, 1);

        private static bool IsExpectedBook(
            SkillPointBookDefinition definition,
            int grantedSp,
            int grantedTp,
            int minimumLevel)
            => definition != null
                && definition.IsSkillPointBook
                && definition.IsSupported
                && definition.GrantedSp == grantedSp
                && definition.GrantedTp == grantedTp
                && definition.MinimumLevel == minimumLevel;

        private static bool IsExpectedTpResetBook(
            SkillPointBookDefinition definition)
            => definition != null
                && definition.IsSkillPointBook
                && definition.IsSupported
                && definition.ResetsTp
                && definition.MinimumLevel == 50;

        private static InventoryService CreateInventoryWithBook(
            int characterId,
            short slotIndex,
            int itemId,
            int count)
        {
            var inventory = new InventoryService(characterId, AccountId);
            var item = ItemCore.Create(2, itemId);
            item.Count = count;
            inventory.SetItem(InventoryListType.Main, slotIndex, item);
            return inventory;
        }

        private static void PersistSeededInventory(
            string connectionString,
            InventoryLease lease,
            string label)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    Check($"seeded {label} stack persists",
                        InventoryPersistenceService.SaveDirtyInTransaction(
                            connection,
                            transaction,
                            lease));
                    transaction.Commit();
                }
                lease.Inventory.ClearDirtyState();
            }
        }

        private static void SeedCharacter(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'skill-point-book-selftest', '');
INSERT INTO characters (
    character_id, account_id, name, job, level, exp, bonus_sp, bonus_tp)
VALUES
    (@spCid, @aid, 'sp-book-selftest', 0, 1, 0, 0, 0),
    (@tpCid, @aid, 'tp-book-selftest', 0, 50, 0, 0, 0),
    (@tpResetCid, @aid, 'tp-reset-book-selftest', 0, 50, 0, 0, 0);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@spCid", SpCharacterId);
                    command.Parameters.AddWithValue("@tpCid", TpCharacterId);
                    command.Parameters.AddWithValue("@tpResetCid", TpResetCharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static ushort FindSkill(bool isTp)
        {
            for (var skillId = 1; skillId <= ushort.MaxValue; skillId++)
            {
                var definition = SkillDataProvider.GetSkill(0, skillId);
                if (definition == null || definition.IsTpSkill != isTp)
                    continue;
                var cost = isTp
                    ? definition.TpCostFor(0, 1)
                    : definition.SpCostFor(0, 1);
                if (cost > 0)
                    return (ushort)skillId;
            }
            return 0;
        }

        private static void SeedResetSkills(
            string connectionString,
            ushort tpSkillId,
            ushort spSkillId)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO character_skills
    (character_id, page_index, slot, skill_id, level, extra_values)
VALUES
    (@cid, 0, 1, @tpSkill, 1, NULL),
    (@cid, 1, 1, @tpSkill, 1, NULL),
    (@cid, 0, 2, @spSkill, 1, NULL);";
                    command.Parameters.AddWithValue("@cid", TpResetCharacterId);
                    command.Parameters.AddWithValue("@tpSkill", tpSkillId);
                    command.Parameters.AddWithValue("@spSkill", spSkillId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static int CountPersistedSkill(
            string connectionString,
            int characterId,
            ushort skillId)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT COUNT(*)
FROM character_skills
WHERE character_id=@cid AND skill_id=@skillId;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@skillId", skillId);
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static int ReadInt(
            string connectionString,
            int characterId,
            string column)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $"SELECT {column} FROM characters WHERE character_id=@cid;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static int ReadPersistedStackCount(
            string connectionString,
            int characterId,
            short slotIndex)
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
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@listType", 0);
                    command.Parameters.AddWithValue("@slot", slotIndex);
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
