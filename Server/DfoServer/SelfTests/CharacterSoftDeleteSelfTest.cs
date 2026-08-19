using System;
using System.IO;
using System.Text;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Characters;

namespace DfoServer.SelfTests
{
    public static class CharacterSoftDeleteSelfTest
    {
        private const int AccountId = 8101;
        private const int CharacterId = 8101001;

        private const string CharacName = "TestCharacter";

        public static int Run()
        {
            Console.WriteLine("=== CHARACTER_SOFT_DELETE selftest ===");
            int pass = 0;
            int fail = 0;

            void Check(string name, bool ok)
            {
                if (ok)
                {
                    pass++;
                    Console.WriteLine($"  [PASS] {name}");
                }
                else
                {
                    fail++;
                    Console.WriteLine($"  [FAIL] {name}");
                }
            }

            var tempDb = Path.Combine(Path.GetTempPath(), "character_soft_delete_selftest.db");
            DeleteTempDatabase(tempDb);

            try
            {
                // 创建一个角色 并软删除 此时名称和删除标志组成的唯一键
                SeedCharacter(tempDb);

                var characRepo = new SqliteCharacterRepository(tempDb, ServerPaths.SchemaFilePath);
                Check("check character count after seed", characRepo.CountByAccount(AccountId) == 1);
                characRepo.SoftDelete(CharacterId);
                Check("check character count after delete", characRepo.CountByAccount(AccountId) == 0);

                // 创建同名角色并删除 如果删除标志仅设置为1 则此时会被唯一键拒绝删除
                var c = TemplateCharacter();
                var cid = characRepo.Create(c);
                Check("check character count after insert", characRepo.CountByAccount(AccountId) == 1);
                characRepo.SoftDelete(cid);
                Check("check character count after delete", characRepo.CountByAccount(AccountId) == 0);
            }
            finally
            {
                DeleteTempDatabase(tempDb);
            }

            Console.WriteLine($"=== result: {pass} PASS, {fail} FAIL ===");
            return fail == 0 ? 0 : 1;
        }

        private static CharacterRecord TemplateCharacter()
        {
            var record = new CharacterRecord
            {
                AccountId = AccountId,
                Name = Encoding.UTF8.GetBytes(CharacName)
            };
            return record;
        }


        private static void SeedCharacter(string databasePath)
        {
            var connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath);
            using (var conn = new SqliteConnection(connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'character-delete-selftest', '');

INSERT OR IGNORE INTO characters
    (character_id, account_id, name, job, grow_type, level, town_id, area_id, pos_x, pos_y)
VALUES
    (@cid, @aid, @charac_name, 0, 0, 1, 1, 1, 100, 100);

INSERT OR IGNORE INTO character_init_flags (character_id)
VALUES (@cid);";
                    cmd.Parameters.AddWithValue("@aid", AccountId);
                    cmd.Parameters.AddWithValue("@cid", CharacterId);
                    cmd.Parameters.AddWithValue("@charac_name", CharacName);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void DeleteTempDatabase(string path)
        {
            TryDelete(path);
            TryDelete(path + "-wal");
            TryDelete(path + "-shm");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
