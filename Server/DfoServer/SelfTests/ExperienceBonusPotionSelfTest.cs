using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class ExperienceBonusPotionSelfTest
    {
        private const int AccountId = 930021;
        private const int CharacterId = 930121;

        public static int Run()
        {
            Console.WriteLine("=== EXP_BONUS_POTION selftest ===");

            var failures = 0;

            // PVF [exp bonus rate] 解析：整数与小数都接受，统一成千分率。
            Check("integer rate 1 scales to 1000",
                ExperienceBonusPotionService.TryParseScaledRate("1", out var r1) && r1 == 1000,
                ref failures);
            Check("fractional rate 0.5 scales to 500",
                ExperienceBonusPotionService.TryParseScaledRate("0.5", out var r05) && r05 == 500,
                ref failures);
            Check("fractional rate 2.5 scales to 2500",
                ExperienceBonusPotionService.TryParseScaledRate("2.5", out var r25) && r25 == 2500,
                ref failures);
            Check("fractional rate 0.2 scales to 200",
                ExperienceBonusPotionService.TryParseScaledRate("0.2", out var r02) && r02 == 200,
                ref failures);
            Check("backticked rate `1.5` scales to 1500",
                ExperienceBonusPotionService.TryParseScaledRate("`1.5`", out var r15) && r15 == 1500,
                ref failures);

            // 非法输入一律拒绝。
            Check("zero rate rejected",
                !ExperienceBonusPotionService.TryParseScaledRate("0", out _),
                ref failures);
            Check("blank rate rejected",
                !ExperienceBonusPotionService.TryParseScaledRate("  ", out _),
                ref failures);
            Check("non-numeric rate rejected",
                !ExperienceBonusPotionService.TryParseScaledRate("abc", out _),
                ref failures);
            Check("tiny rate rounding to zero rejected",
                !ExperienceBonusPotionService.TryParseScaledRate("0.0001", out _),
                ref failures);

            // 加成计算：按千分率折算回实际倍率。
            Check("2x potion adds 100% bonus",
                ExperienceBonusPotionService.CalculateBonus(1000, 1000) == 1000,
                ref failures);
            Check("1.5x potion adds 50% bonus",
                ExperienceBonusPotionService.CalculateBonus(1000, 500) == 500,
                ref failures);
            Check("3.5x potion adds 250% bonus",
                ExperienceBonusPotionService.CalculateBonus(1000, 2500) == 2500,
                ref failures);
            Check("zero rate gives no bonus",
                ExperienceBonusPotionService.CalculateBonus(1000, 0) == 0,
                ref failures);
            Check("zero experience gives no bonus",
                ExperienceBonusPotionService.CalculateBonus(0, 1000) == 0,
                ref failures);
            Check("bonus saturates at uint max",
                ExperienceBonusPotionService.CalculateBonus(uint.MaxValue, 2000) == uint.MaxValue,
                ref failures);

            // ── 共享冷却（[cooltime group] 99 = 远古精灵秘药全系，依赖 PVF）──
            Check("both potions resolve cooltime group 99",
                StackableItemProvider.ResolveCooltimeGroup(10147838) == 99
                    && StackableItemProvider.ResolveCooltimeGroup(7377) == 99,
                ref failures);
            var groupMembers = StackableItemProvider.ResolveCooltimeGroupMembers(10147838);
            Check("group 99 members include both potions",
                groupMembers.Contains(10147838) && groupMembers.Contains(7377),
                ref failures);
            Check("ungrouped item expands to itself only",
                StackableItemProvider.ResolveCooltimeGroupMembers(14).Count == 1,
                ref failures);

            var activeRows = new List<ItemValueEntrySnapshot>
            {
                new ItemValueEntrySnapshot { ItemId = 10147838, Value = 2_000_000 },
            };
            Check("same item blocked while on cooldown",
                ExperienceBonusPotionService.IsCooltimeBlocked(10147838, 99, activeRows),
                ref failures);
            Check("same-group sibling blocked while on cooldown",
                ExperienceBonusPotionService.IsCooltimeBlocked(7377, 99, activeRows),
                ref failures);
            Check("ungrouped item not blocked by group cooldown",
                !ExperienceBonusPotionService.IsCooltimeBlocked(14, 0, activeRows),
                ref failures);

            RunPersistenceChecks(ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        // 冷却/效果的持久化与登录恢复换算（临时 SQLite 库）。
        private static void RunPersistenceChecks(ref int failures)
        {
            var tempDb = Path.Combine(Path.GetTempPath(), "exp_bonus_potion_selftest.db");
            DeleteTempDatabase(tempDb);
            var connStr = SqliteDatabaseBootstrap.Initialize(tempDb, ServerPaths.SchemaFilePath);
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var seed = conn.CreateCommand();
                seed.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash) VALUES (@aid, 'exp-bonus-selftest', '');
INSERT OR IGNORE INTO characters (character_id, account_id, name) VALUES (@cid, @aid, 'exp-bonus-selftest');";
                seed.Parameters.AddWithValue("@aid", AccountId);
                seed.Parameters.AddWithValue("@cid", CharacterId);
                seed.ExecuteNonQuery();
            }

            var repository = new CharacterItemValueRepository(connStr);
            const int potionItemId = 10147838;
            const long now = 1_000_000L;

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var tx = conn.BeginTransaction();
                repository.UpsertCooldown(conn, tx, CharacterId, potionItemId, now + 600);
                Check("cooldown active right after use",
                    repository.IsOnCooldown(conn, tx, CharacterId, potionItemId, now),
                    ref failures);
                Check("other item not on cooldown",
                    !repository.IsOnCooldown(conn, tx, CharacterId, 7377, now),
                    ref failures);
                tx.Commit();
            }

            // 重复写入同道具冷却应覆盖而不是堆叠。
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var tx = conn.BeginTransaction();
                repository.UpsertCooldown(conn, tx, CharacterId, potionItemId, now + 900);
                tx.Commit();
            }

            var active = repository.LoadActiveCooldowns(CharacterId, now);
            Check("login loads single cooldown entry",
                active.Count == 1 && active[0].ItemId == potionItemId,
                ref failures);
            Check("cooldown value converted to remaining seconds",
                active.Count == 1 && active[0].Value == 900,
                ref failures);

            Check("cooldown expires after its endpoint",
                repository.LoadActiveCooldowns(CharacterId, now + 901).Count == 0,
                ref failures);

            // 效果列表：未到期恢复剩余秒，到期不恢复。
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO character_experience_bonus_effects(character_id, source_item_id, bonus_rate, expires_at)
VALUES (@cid, @iid, 500, @exp);";
                cmd.Parameters.AddWithValue("@cid", CharacterId);
                cmd.Parameters.AddWithValue("@iid", potionItemId);
                cmd.Parameters.AddWithValue("@exp", now + 600);
                cmd.ExecuteNonQuery();
            }

            Check("active effect restored with remaining seconds",
                ExperienceBonusPotionService.TryGetActiveEffect(
                    connStr, CharacterId, now, out var effectItemId, out var effectRemainingSec)
                    && effectItemId == potionItemId
                    && effectRemainingSec == 600,
                ref failures);
            Check("expired effect not restored",
                !ExperienceBonusPotionService.TryGetActiveEffect(
                    connStr, CharacterId, now + 601, out _, out _),
                ref failures);

            DeleteTempDatabase(tempDb);
        }

        private static void DeleteTempDatabase(string databasePath)
        {
            try
            {
                if (File.Exists(databasePath)) File.Delete(databasePath);
                if (File.Exists(databasePath + "-wal")) File.Delete(databasePath + "-wal");
                if (File.Exists(databasePath + "-shm")) File.Delete(databasePath + "-shm");
            }
            catch
            {
            }
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
            if (!ok)
                failures++;
        }
    }
}
