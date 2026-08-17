using DfoServer.GameWorld;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Quests
{
    internal sealed class QuestMainlineLevelSkipService
    {
        private readonly string _connectionString;

        internal QuestMainlineLevelSkipService(string connectionString)
        {
            _connectionString = connectionString
                ?? throw new ArgumentNullException(nameof(connectionString));
        }

        internal IReadOnlyCollection<ushort> SkipBelowLevel(
            int characterId,
            int newLevel,
            int characterJob,
            int growType)
        {
            if (characterId <= 0 || newLevel <= 1)
                return Array.Empty<ushort>();

            var repository = new QuestRepository(_connectionString);
            var clearedFlags = repository.LoadClearedFlags(characterId);
            var clearedQuestIds = new HashSet<int>(clearedFlags.Keys);
            var skipped = new HashSet<ushort>();

            // 升级券跨级时逐级展开前置链，只自动补记目标等级以下的史诗主线；
            // 普通、活动、转职与副职业任务保持原状。
            for (var level = 1; level < newLevel; level++)
            {
                var changed = true;
                while (changed)
                {
                    changed = false;
                    foreach (var questId in QuestData.ComputeAcceptableQuests(
                                 level,
                                 characterJob,
                                 growType,
                                 clearedQuestIds,
                                 clearedFlags))
                    {
                        if (!IsMainlineQuest(questId) || !clearedQuestIds.Add(questId))
                            continue;

                        clearedFlags[questId] = 1;
                        skipped.Add(questId);
                        changed = true;
                    }
                }
            }

            if (skipped.Count == 0)
                return Array.Empty<ushort>();

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    foreach (var questId in skipped)
                    {
                        QuestRepository.MarkQuestCleared(
                            connection,
                            transaction,
                            characterId,
                            questId);
                        DeleteActiveQuest(
                            connection,
                            transaction,
                            characterId,
                            questId);
                    }
                    transaction.Commit();
                }
            }
            return skipped;
        }

        private static bool IsMainlineQuest(ushort questId)
        {
            QuestFile quest = QuestCatalog.Get(questId);
            return quest != null
                && !quest.IsEvent
                && string.Equals(
                    (quest.Grade ?? string.Empty).Trim(),
                    "[epic]",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static void DeleteActiveQuest(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            ushort questId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
DELETE FROM character_active_quests
WHERE character_id=@cid AND quest_id=@qid;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@qid", (int)questId);
                command.ExecuteNonQuery();
            }
        }
    }
}
