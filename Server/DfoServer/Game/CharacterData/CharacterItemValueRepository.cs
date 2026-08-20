using DfoServer.Game.SelectCharacter;
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.CharacterData
{
    internal sealed class CharacterItemValueRepository
    {
        private readonly string _connectionString;

        internal CharacterItemValueRepository(string connectionString)
        {
            _connectionString = connectionString;
        }

        internal List<ItemValueEntrySnapshot> LoadItemValueList(int characterId, string listKind)
        {
            var items = new List<ItemValueEntrySnapshot>();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "SELECT item_id, value FROM character_item_values WHERE character_id = @cid AND list_kind = @kind ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@kind", listKind);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            items.Add(new ItemValueEntrySnapshot { ItemId = reader.GetInt32(0), Value = reader.GetInt32(1) });
                    }
                }
            }
            return items;
        }

        internal void SaveItemValueList(int characterId, string listKind, List<ItemValueEntrySnapshot> items)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand("DELETE FROM character_item_values WHERE character_id = @cid AND list_kind = @kind", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.Parameters.AddWithValue("@kind", listKind);
                        cmd.ExecuteNonQuery();
                    }
                    for (int i = 0; i < items.Count; i++)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_item_values (character_id, list_kind, sort_order, item_id, value) VALUES (@cid, @kind, @ord, @iid, @val)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@kind", listKind);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@iid", items[i].ItemId);
                            cmd.Parameters.AddWithValue("@val", items[i].Value);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }

        internal void SaveItemValueListIfEmpty(int characterId, string kind, List<ItemValueEntrySnapshot> items)
        {
            if (LoadItemValueList(characterId, kind).Count == 0 && items.Count > 0)
                SaveItemValueList(characterId, kind, items);
        }

        // 消耗品冷却持久化：value 存到期 unix 秒（不是剩余毫秒），sort_order 复用为
        // item_id 保证每个道具一行。登录下发时再换算成剩余秒（见 LoadActiveCooldowns）。
        internal void UpsertCooldown(
            SqliteConnection conn,
            SqliteTransaction tx,
            int characterId,
            int itemId,
            long expiresAtUnixSeconds)
        {
            using (var cmd = new SqliteCommand(
                "DELETE FROM character_item_values WHERE character_id = @cid AND list_kind = 'cooltime' AND item_id = @iid", conn, tx))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@iid", itemId);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = new SqliteCommand(
                "INSERT INTO character_item_values (character_id, list_kind, sort_order, item_id, value) VALUES (@cid, 'cooltime', @iid, @iid, @exp)", conn, tx))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@iid", itemId);
                cmd.Parameters.AddWithValue("@exp", expiresAtUnixSeconds);
                cmd.ExecuteNonQuery();
            }
        }

        // 读取未到期冷却并把 value 换算成剩余秒（客户端按秒解释，自行除以 60 显示分钟）；
        // 顺手清掉已过期行。
        internal List<ItemValueEntrySnapshot> LoadActiveCooldowns(int characterId, long nowUnixSeconds)
        {
            var items = new List<ItemValueEntrySnapshot>();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand(
                        "DELETE FROM character_item_values WHERE character_id = @cid AND list_kind = 'cooltime' AND value <= @now", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.Parameters.AddWithValue("@now", nowUnixSeconds);
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = new SqliteCommand(
                        "SELECT item_id, value FROM character_item_values WHERE character_id = @cid AND list_kind = 'cooltime' ORDER BY sort_order", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var remainingSeconds = reader.GetInt32(1) - nowUnixSeconds;
                                if (remainingSeconds <= 0)
                                    continue;
                                items.Add(new ItemValueEntrySnapshot
                                {
                                    ItemId = reader.GetInt32(0),
                                    Value = (int)Math.Min(int.MaxValue, remainingSeconds),
                                });
                            }
                        }
                    }

                    tx.Commit();
                }
            }
            return items;
        }

        // 道具是否仍在冷却期（TryUse 等服务端消费路径的复用拦截）。
        internal bool IsOnCooldown(
            SqliteConnection conn,
            SqliteTransaction tx,
            int characterId,
            int itemId,
            long nowUnixSeconds)
        {
            using (var cmd = new SqliteCommand(
                "SELECT COUNT(*) FROM character_item_values WHERE character_id = @cid AND list_kind = 'cooltime' AND item_id = @iid AND value > @now", conn, tx))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@iid", itemId);
                cmd.Parameters.AddWithValue("@now", nowUnixSeconds);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
        }

        // 读取全部未到期冷却原始行（Value = 到期 unix 秒），供共享冷却的组级判定。
        internal List<ItemValueEntrySnapshot> LoadActiveCooldownRows(
            SqliteConnection conn,
            SqliteTransaction tx,
            int characterId,
            long nowUnixSeconds)
        {
            var rows = new List<ItemValueEntrySnapshot>();
            using (var cmd = new SqliteCommand(
                "SELECT item_id, value FROM character_item_values WHERE character_id = @cid AND list_kind = 'cooltime' AND value > @now", conn, tx))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@now", nowUnixSeconds);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        rows.Add(new ItemValueEntrySnapshot
                        {
                            ItemId = reader.GetInt32(0),
                            Value = reader.GetInt32(1),
                        });
                }
            }
            return rows;
        }
    }
}
