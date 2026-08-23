using DfoServer.Game.Characters;
using DfoServer.Game.Friends;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace DfoServer.SelfTests
{
    // 好友系统纯逻辑自测(零网络, 确定性):
    //   - 单向存储: A 添加 B 只记 A→B, B→A 不成立; 反向单独成边。
    //   - 幂等: 重复 RecordFriendship 不破坏状态。
    //   - 移除: 只删 a→b, 不影响 b→a; 移除不存在关系返回 false。
    //   - 持久化往返: Record → 重置内存 → 从 united_friend_relations 表重载关系仍在;
    //     表内容 (owner_name, friend_name) 与内存图一致。
    //   - builder 字节: BuildNotificationBody(record, uid) 前 5 字节布局 + 1 参数重载默认 uid=CharacterId。
    //   - 角色删除: HandleCharacterDeleted 清 owner+friend 两方向(内存+表), X 键消失, 好友关系归零。
    //   - 角色更名: HandleCharacterRenamed 把 X 所有出现换成 Y(内存+表事务), owner 边迁移、friend 边跟随新名,
    //       目标名已有出边时合并; 同名/空名无操作。
    // 隔离: 通过反射把 UnitedFriendSystem 的 _repository 指向临时数据库(走 item_schema.sql 建表),
    //       不污染 bin/Debug/Data/inventory.db。
    public static class UnitedFriendSystemSelfTest
    {
        private static int _pass;
        private static int _fail;
        private static int _dbSeq;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== UNITED_FRIEND selftest ===");

            // 备份并接管 UnitedFriendSystem 静态状态, 用临时 SQLite 库做持久化隔离。
            // Friends 是 readonly 静态字段不能 SetValue, 只能备份/恢复其内容(Clear + 回填)。
            var originalRepository = (UnitedFriendRepository)GetStatic("_repository");
            var originalLoaded = (bool)GetStatic("_loaded");
            var friendsRef = (Dictionary<string, HashSet<string>>)GetStatic("Friends");
            var originalSnapshot = friendsRef.ToDictionary(
                kv => kv.Key,
                kv => new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase));
            var tempDir = Path.Combine(Path.GetTempPath(), $"united_friend_selftest_{Guid.NewGuid():N}");
            try
            {
                ResetState(tempDir);

                RunStorageTests();
                RunPersistenceRoundTrip(tempDir);
                RunCharacterLifecycleTests(tempDir);
                RunBuilderByteTests();
            }
            finally
            {
                friendsRef.Clear();
                foreach (var kv in originalSnapshot)
                    friendsRef[kv.Key] =
                        new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase);
                SetStatic("_repository", originalRepository);
                SetStatic("_loaded", originalLoaded);
                // Microsoft.Data.Sqlite 默认连接池会持有文件句柄, 先清池才能删临时库。
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }

            PrintSummary();
            return _fail == 0 ? 0 : 1;
        }

        private static UnitedFriendRepository Repository
            => (UnitedFriendRepository)GetStatic("_repository");

        private static void ResetState(string tempDir)
        {
            Directory.CreateDirectory(tempDir);
            // 每个测试阶段用独立库文件: SqliteDatabaseBootstrap 按路径缓存"已初始化",
            // 同路径重建不会重跑 schema+迁移, 会残留上一阶段数据。
            var tempDb = Path.Combine(tempDir, $"inventory_{_dbSeq++}.db");
            // 临时库预置 user_version = 最新版本-1, Apply 只跑最后一条迁移(v57, 建好友表, 无 PVF 依赖)。
            // 好友表 united_friend_relations 同时由 item_schema.sql(每次初始化幂等执行)创建, 迁移 v57
            // 与 schema 双路径一致; 键为 BINARY 大小写敏感, 与 characters.name 对齐。
            using (var conn = new SqliteConnection($"Data Source={tempDb}"))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        $"PRAGMA user_version = {SqliteMigrations.LatestVersion - 1};";
                    cmd.ExecuteNonQuery();
                }
            }
            SetStatic("_repository", new UnitedFriendRepository(tempDb, ServerPaths.SchemaFilePath));
            SetStatic("_loaded", false);
            ((Dictionary<string, HashSet<string>>)GetStatic("Friends")).Clear();
        }

        private static void RunStorageTests()
        {
            // 单向存储: A 添加 B 只记 A→B。
            UnitedFriendSystem.RecordFriendship("A", "B");
            Check("单向: A→B 成立", UnitedFriendSystem.IsFriend("A", "B"));
            Check("单向: B→A 不成立", !UnitedFriendSystem.IsFriend("B", "A"));
            Check("单向: GetFriends(A) 含 B",
                UnitedFriendSystem.GetFriends("A").Contains("B"));
            Check("单向: GetFriends(B) 不含 A",
                !UnitedFriendSystem.GetFriends("B").Contains("A"));

            // 反向独立: B 添加 A 单独成边, 与 A→B 互不影响。
            UnitedFriendSystem.RecordFriendship("B", "A");
            Check("反向单独成边: B→A 成立", UnitedFriendSystem.IsFriend("B", "A"));

            // 幂等: 重复记录不破坏状态。
            UnitedFriendSystem.RecordFriendship("A", "B");
            Check("幂等: 重复记录 A→B 仍成立",
                UnitedFriendSystem.IsFriend("A", "B"));

            // 移除单向: 只删 a→b, 不动 b→a。
            var removed = UnitedFriendSystem.RemoveFriendship("A", "B");
            Check("移除存在关系返回 true", removed);
            Check("移除后 A→B 不再成立", !UnitedFriendSystem.IsFriend("A", "B"));
            Check("移除不影响反向 B→A", UnitedFriendSystem.IsFriend("B", "A"));
            Check("移除不存在关系返回 false",
                !UnitedFriendSystem.RemoveFriendship("A", "B"));

            // 空/非法输入被忽略。
            UnitedFriendSystem.RecordFriendship("", "X");
            UnitedFriendSystem.RecordFriendship(null, "X");
            Check("空名记录被忽略", UnitedFriendSystem.GetFriends("").Count == 0);
            Check("null 名记录被忽略", UnitedFriendSystem.GetFriends(null).Count == 0);

            // ---- 大小写敏感(对齐角色系统 characters.name BINARY) + 排序 + 防御 ----
            UnitedFriendSystem.RecordFriendship("Case_A", "Zed");
            Check("大小写: IsFriend(\"Case_A\",\"Zed\") 成立",
                UnitedFriendSystem.IsFriend("Case_A", "Zed"));
            Check("大小写: IsFriend(\"case_a\",\"zed\") 不成立(敏感)",
                !UnitedFriendSystem.IsFriend("case_a", "zed"));
            UnitedFriendSystem.RecordFriendship("case_a", "zed"); // 与 Case_A→Zed 是两条独立边
            Check("大小写: 两条变体边互不污染",
                UnitedFriendSystem.GetFriends("Case_A").Count == 1
                && UnitedFriendSystem.GetFriends("case_a").Count == 1
                && UnitedFriendSystem.GetFriends("Case_A")[0] == "Zed"
                && UnitedFriendSystem.GetFriends("case_a")[0] == "zed");
            // Ordinal 排序: 'B'(0x42) < 'a'(0x61)，与忽略大小写排序方向相反。
            UnitedFriendSystem.RecordFriendship("SortA", "B");
            UnitedFriendSystem.RecordFriendship("SortA", "a");
            var sorted = UnitedFriendSystem.GetFriends("SortA");
            Check("GetFriends Ordinal 排序: [B, a]",
                sorted.Count == 2 && sorted[0] == "B" && sorted[1] == "a");
            Check("GetFriends 不存在名: 空数组",
                UnitedFriendSystem.GetFriends("NONAME").Count == 0);
            Check("IsFriend 空名防御: null/空 返回 false 不抛异常",
                !UnitedFriendSystem.IsFriend(null, "X")
                && !UnitedFriendSystem.IsFriend("", "X")
                && !UnitedFriendSystem.IsFriend("X", null));
            Check("大小写: 单向仍成立(反查不成立)",
                !UnitedFriendSystem.IsFriend("Zed", "Case_A"));
        }

        private static void RunPersistenceRoundTrip(string tempDir)
        {
            // 从干净状态开始持久化测试。
            ResetState(tempDir);

            UnitedFriendSystem.RecordFriendship("P1", "Q1");
            UnitedFriendSystem.RecordFriendship("P2", "Q2");

            // 模拟进程重启: 清空内存后从好友表重载。
            SetStatic("_loaded", false);
            ((Dictionary<string, HashSet<string>>)GetStatic("Friends")).Clear();

            Check("持久化往返: 重启后 P1→Q1 仍在",
                UnitedFriendSystem.IsFriend("P1", "Q1"));
            Check("持久化往返: 重启后 P2→Q2 仍在",
                UnitedFriendSystem.IsFriend("P2", "Q2"));
            Check("持久化往返: 重启后单向仍成立",
                !UnitedFriendSystem.IsFriend("Q1", "P1"));

            // 表内容校验: 恰好 2 条关系, 与内存图一致。
            var rows = Repository.LoadAll();
            Check("表行数 = 关系数(2)", rows.Count == 2);
            Check("表含 P1→Q1",
                rows.TryGetValue("P1", out var s1) && s1.Contains("Q1"));
            Check("表含 P2→Q2",
                rows.TryGetValue("P2", out var s2) && s2.Contains("Q2"));
            Check("表不含反向 Q1→P1", !rows.TryGetValue("Q1", out _));

            // 大小写往返: 存储层大小写敏感(与角色系统一致), 重启后精确大小写命中。
            ResetState(tempDir);
            UnitedFriendSystem.RecordFriendship("P3", "q3");
            SetStatic("_loaded", false);
            ((Dictionary<string, HashSet<string>>)GetStatic("Friends")).Clear();
            Check("持久化大小写: 重启后 IsFriend(\"P3\",\"q3\") 成立",
                UnitedFriendSystem.IsFriend("P3", "q3"));
            Check("持久化大小写: 重启后 IsFriend(\"p3\",\"Q3\") 不成立(敏感)",
                !UnitedFriendSystem.IsFriend("p3", "Q3"));

            // 表级大小写敏感: BINARY PK 允许 Abc→X 与 abc→X 两条独立边并存。
            ResetState(tempDir);
            Repository.InsertRelation("Abc", "X");
            Repository.InsertRelation("abc", "X");
            var tableRows = Repository.LoadAll();
            Check("表级大小写: 两条变体边都插入成功",
                tableRows.TryGetValue("Abc", out var ab1) && ab1.Contains("X")
                && tableRows.TryGetValue("abc", out var ab2) && ab2.Contains("X"));
        }

        private static void RunCharacterLifecycleTests(string tempDir)
        {
            // ---- 删除角色: 清 owner+friend 两方向 ----
            ResetState(tempDir);
            UnitedFriendSystem.RecordFriendship("A", "X");
            UnitedFriendSystem.RecordFriendship("B", "X");
            UnitedFriendSystem.RecordFriendship("X", "C");
            UnitedFriendSystem.HandleCharacterDeletedAsync("X", null)
                .GetAwaiter().GetResult();

            Check("删除: A→X 清除", !UnitedFriendSystem.IsFriend("A", "X"));
            Check("删除: B→X 清除", !UnitedFriendSystem.IsFriend("B", "X"));
            Check("删除: X 键消失(X→C 清除)",
                !UnitedFriendSystem.IsFriend("X", "C"));
            Check("删除: GetFriends(A) 不再含 X",
                !UnitedFriendSystem.GetFriends("A").Contains("X"));
            var delRows = Repository.LoadAll();
            Check("删除: 表归零(3 条关系全清)",
                delRows.Count == 0);

            // 删除不存在的名字: 无影响。
            ResetState(tempDir);
            UnitedFriendSystem.RecordFriendship("A", "B");
            UnitedFriendSystem.HandleCharacterDeletedAsync("NONAME", null)
                .GetAwaiter().GetResult();
            Check("删除不存在名: A→B 仍成立",
                UnitedFriendSystem.IsFriend("A", "B"));

            // ---- 更名: owner 边迁移 + friend 边跟随新名 ----
            ResetState(tempDir);
            UnitedFriendSystem.RecordFriendship("A", "X");
            UnitedFriendSystem.RecordFriendship("X", "C");
            UnitedFriendSystem.HandleCharacterRenamedAsync("X", "Y", null)
                .GetAwaiter().GetResult();

            Check("更名: A→Y 成立(原 A→X)", UnitedFriendSystem.IsFriend("A", "Y"));
            Check("更名: A→X 不再成立", !UnitedFriendSystem.IsFriend("A", "X"));
            Check("更名: Y→C 成立(原 X→C)", UnitedFriendSystem.IsFriend("Y", "C"));
            Check("更名: X 键消失", !UnitedFriendSystem.IsFriend("X", "C"));
            Check("更名: GetFriends(A) 含 Y 不含 X",
                UnitedFriendSystem.GetFriends("A").Contains("Y")
                && !UnitedFriendSystem.GetFriends("A").Contains("X"));

            // 表内容校验: 键全部换新名。
            var renRows = Repository.LoadAll();
            Check("更名: 表 A→Y",
                renRows.TryGetValue("A", out var r1) && r1.Contains("Y"));
            Check("更名: 表 Y→C",
                renRows.TryGetValue("Y", out var r2) && r2.Contains("C"));
            Check("更名: 表不含 X",
                !renRows.TryGetValue("X", out _) && !renRows.Values.Any(s => s.Contains("X")));

            // 更名合并: 新名已有出边时并集。
            ResetState(tempDir);
            UnitedFriendSystem.RecordFriendship("X", "C");
            UnitedFriendSystem.RecordFriendship("Y", "D");
            UnitedFriendSystem.HandleCharacterRenamedAsync("X", "Y", null)
                .GetAwaiter().GetResult();
            var merged = UnitedFriendSystem.GetFriends("Y");
            Check("更名合并: Y 出边含 C 与 D(并集)",
                merged.Contains("C") && merged.Contains("D"));

            // 同名/空名更名: 无操作。
            ResetState(tempDir);
            UnitedFriendSystem.RecordFriendship("X", "C");
            UnitedFriendSystem.HandleCharacterRenamedAsync("X", "X", null)
                .GetAwaiter().GetResult();
            Check("更名同名: 无操作 X→C 仍成立",
                UnitedFriendSystem.IsFriend("X", "C"));
            UnitedFriendSystem.HandleCharacterRenamedAsync("X", "", null)
                .GetAwaiter().GetResult();
            Check("更名空名: 无操作 X→C 仍成立",
                UnitedFriendSystem.IsFriend("X", "C"));
        }

        private static void RunBuilderByteTests()
        {
            var record = new CharacterRecord
            {
                CharacterId = 0x0102,   // 258
                Name = Encoding.UTF8.GetBytes("测试"),
                Job = 2,
                GrowType = 1,
                Level = 50,
                UserState = 1,
            };
            const ushort uid = 0x1234;

            var body = UserInfoSubtype0Builder.BuildNotificationBody(record, uid);
            Check("builder: body 非空且够长",
                body != null && body.Length > 6);
            Check("builder: body[0]==0",
                body != null && body.Length > 0 && body[0] == 0);
            Check("builder: u16 count==1",
                body != null && body.Length > 2 && body[1] == 1 && body[2] == 0);
            Check("builder: uid 落点正确(小端)",
                body != null && body.Length > 4 && body[3] == 0x34 && body[4] == 0x12);

            // 1 参数重载: 默认 uid = (ushort)CharacterId。
            var body1 = UserInfoSubtype0Builder.BuildNotificationBody(record);
            Check("builder(1参): 默认 uid=CharacterId",
                body1 != null && body1.Length > 4 && body1[3] == 0x02 && body1[4] == 0x01);
        }

        private static object GetStatic(string field)
            => typeof(UnitedFriendSystem)
                .GetField(field, BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null);

        private static void SetStatic(string field, object value)
            => typeof(UnitedFriendSystem)
                .GetField(field, BindingFlags.NonPublic | BindingFlags.Static)
                ?.SetValue(null, value);

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) _pass++;
            else _fail++;
        }

        private static void PrintSummary()
        {
            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
        }
    }
}
