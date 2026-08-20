using DfoServer.GameWorld;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;

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

            // 任务状态读取、活动任务 CAS 删除与完成标记必须属于同一事务。
            // 物品层只负责触发任务领域编排，不直接操作任务表。
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var clearedFlags = QuestRepository.LoadClearedFlags(
                        connection,
                        transaction,
                        characterId);
                    var activeByQuestId = QuestRepository.LoadActiveQuests(
                            connection, transaction, characterId)
                        .ToDictionary(quest => quest.QuestId);
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

                    foreach (var questId in skipped)
                    {
                        QuestRepository.MarkQuestCleared(
                            connection,
                            transaction,
                            characterId,
                            questId);
                        if (activeByQuestId.TryGetValue(questId, out var activeQuest)
                            && !QuestRepository.TryDeleteActiveQuestCas(
                                connection,
                                transaction,
                                characterId,
                                questId,
                                activeQuest.ActivationId,
                                activeQuest.Version,
                                activeQuest.TriggerValue))
                        {
                            // CAS 失败时抛出并回滚，禁止留下完成标记与活动任务不一致的半状态。
                            throw new InvalidOperationException(
                                $"mainline skip lost quest activation race: " +
                                $"cid={characterId} quest={questId}");
                        }
                    }
                    QuestClearProgressRules.SynchronizeActiveParents(
                        connection,
                        transaction,
                        characterId);
                    transaction.Commit();
                    return skipped;
                }
            }
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

    }
}
