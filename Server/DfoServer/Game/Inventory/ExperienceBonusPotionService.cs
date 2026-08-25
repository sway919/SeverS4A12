using DfoServer.Game.CharacterData;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{
    // Server-authoritative handling for PVF [exp bonus rate] consumables.
    // PVF 倍率写法可能是小数（如 1.5 倍秘药为 0.5），库存字段 bonus_rate 统一存
    // 千分率（倍率 * RateScale），CalculateBonus 计算时再除回来。
    internal static class ExperienceBonusPotionService
    {
        internal const int RateScale = 1000;
        internal static bool TryUse(
            InventoryLease lease,
            int characterId,
            InventoryListType listType,
            short slotIndex,
            int expectedItemId,
            bool isInDungeon,
            out InventoryMutationResult mutation,
            out string detail)
        {
            mutation = null;
            detail = null;
            if (lease?.Inventory == null || characterId <= 0
                || listType != InventoryListType.Main)
            {
                return false;
            }

            var source = lease.Inventory.GetItem(listType, slotIndex);
            if (source == null || source.IsEmpty || source.Count <= 0
                || (expectedItemId > 0 && source.ItemId != expectedItemId))
            {
                return false;
            }

            var definitionStatus = TryResolveDefinition(
                source.ItemId,
                out var rate,
                out var durationMilliseconds,
                out var cooldownMilliseconds,
                out var cooldownGroup,
                out var definitionDetail);
            if (definitionStatus == DefinitionStatus.NotApplicable)
            {
                FileLogger.Log(
                    $"[ExperienceBonusPotion] definition not applicable: "
                    + $"item={source.ItemId} detail={definitionDetail}");
                return false;
            }
            if (definitionStatus == DefinitionStatus.Invalid)
            {
                detail = definitionDetail;
                return true;
            }

            if (!isInDungeon)
            {
                detail = "experience bonus potion can only be used in a dungeon";
                return true;
            }

            var connectionString = SqliteDatabaseBootstrap.Initialize(
                ServerPaths.DatabasePath,
                ServerPaths.SchemaFilePath);
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var itemValueRepository = new CharacterItemValueRepository(connectionString);
            var activeCooldowns = itemValueRepository.LoadActiveCooldownRows(
                connection,
                transaction,
                characterId,
                now);
            if (IsCooltimeBlocked(source.ItemId, cooldownGroup, activeCooldowns))
            {
                detail = "experience bonus potion is on cooldown";
                return true;
            }

            if (!InventoryDeleteService.TryConsumeFromSlot(
                    lease.Inventory,
                    listType,
                    slotIndex,
                    source.ItemId,
                    1,
                    out var deleted)
                || !deleted.Success)
            {
                detail = "inventory deduction failed";
                return true;
            }

            var durationSeconds = Math.Max(
                1L,
                (durationMilliseconds + 999L) / 1000L);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_experience_bonus_effects(character_id, source_item_id, bonus_rate, expires_at)
VALUES ($characterId, $itemId, $rate, $expiresAt)
ON CONFLICT(character_id) DO UPDATE SET
    source_item_id = excluded.source_item_id,
    bonus_rate = excluded.bonus_rate,
    expires_at = excluded.expires_at;";
                command.Parameters.AddWithValue("$characterId", characterId);
                command.Parameters.AddWithValue("$itemId", source.ItemId);
                command.Parameters.AddWithValue("$rate", rate);
                command.Parameters.AddWithValue(
                    "$expiresAt",
                    now + durationSeconds);
                command.ExecuteNonQuery();
            }

            if (!InventoryPersistenceService.SaveDirtyInTransaction(
                    connection,
                    transaction,
                    lease))
            {
                lease.Inventory.SetItem(
                    listType,
                    slotIndex,
                    deleted.SourceSnapshot.Copy());
                detail = "inventory persistence failed";
                return true;
            }

            if (cooldownMilliseconds > 0)
            {
                var cooldownSeconds = Math.Max(
                    1L,
                    (cooldownMilliseconds + 999L) / 1000L);
                itemValueRepository.UpsertCooldown(
                    connection,
                    transaction,
                    characterId,
                    source.ItemId,
                    now + cooldownSeconds);
            }

            transaction.Commit();
            lease.Inventory.ClearDirtyState();
            mutation = new InventoryMutationResult
            {
                ListType = listType,
                SlotIndex = slotIndex,
                ItemTemplateId = source.ItemId,
                RemainingStackCount = deleted.RemainingCount,
                InstanceValue = deleted.RemainingCount,
                RequestedCount = 1,
                AppliedCount = 1,
            };
            detail = $"ratePerMille={rate} durationMs={durationMilliseconds} cooldownMs={cooldownMilliseconds}";
            return true;
        }

        internal static int GetActiveRate(int characterId)
        {
            if (characterId <= 0)
                return 0;

            var connectionString = SqliteDatabaseBootstrap.Initialize(
                ServerPaths.DatabasePath,
                ServerPaths.SchemaFilePath);
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT bonus_rate FROM character_experience_bonus_effects
WHERE character_id = $characterId AND expires_at > $now;";
            command.Parameters.AddWithValue("$characterId", characterId);
            command.Parameters.AddWithValue(
                "$now",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var value = command.ExecuteScalar();
            return value != null
                && value != DBNull.Value
                && int.TryParse(value.ToString(), out var rate)
                && rate > 0
                    ? rate
                    : 0;
        }

        internal static uint CalculateBonus(uint experience, int rate)
        {
            if (experience == 0 || rate <= 0)
                return 0;
            return (uint)Math.Min(
                uint.MaxValue,
                (ulong)experience * (ulong)rate / RateScale);
        }

        // 共享冷却判定：同道具或同 [cooltime group] 成员有未到期冷却即拦截。
        internal static bool IsCooltimeBlocked(
            int itemId,
            int cooldownGroup,
            IReadOnlyList<ItemValueEntrySnapshot> activeCooldowns)
        {
            foreach (var row in activeCooldowns)
            {
                if (row.ItemId == itemId)
                    return true;
                if (cooldownGroup > 0
                    && StackableItemProvider.ResolveCooltimeGroup(row.ItemId) == cooldownGroup)
                    return true;
            }

            return false;
        }

        // 登录初始化用：读取未到期的秘药效果，换算成剩余秒供 0x00AE 效果列表下发（客户端按秒解释、自行除以 60 显示分钟）。
        internal static bool TryGetActiveEffect(
            int characterId,
            long nowUnixSeconds,
            out int sourceItemId,
            out int remainingSeconds)
            => TryGetActiveEffect(
                SqliteDatabaseBootstrap.Initialize(
                    ServerPaths.DatabasePath,
                    ServerPaths.SchemaFilePath),
                characterId,
                nowUnixSeconds,
                out sourceItemId,
                out remainingSeconds);

        internal static bool TryGetActiveEffect(
            string connectionString,
            int characterId,
            long nowUnixSeconds,
            out int sourceItemId,
            out int remainingSeconds)
        {
            sourceItemId = 0;
            remainingSeconds = 0;
            if (characterId <= 0)
                return false;

            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT source_item_id, expires_at FROM character_experience_bonus_effects
WHERE character_id = $characterId AND expires_at > $now;";
            command.Parameters.AddWithValue("$characterId", characterId);
            command.Parameters.AddWithValue("$now", nowUnixSeconds);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return false;

            sourceItemId = reader.GetInt32(0);
            var remainingSec = reader.GetInt64(1) - nowUnixSeconds;
            if (remainingSec <= 0)
                return false;
            remainingSeconds = (int)Math.Min(int.MaxValue, remainingSec);
            return true;
        }

        private static DefinitionStatus TryResolveDefinition(
            int itemId,
            out int rate,
            out int durationMilliseconds,
            out int cooldownMilliseconds,
            out int cooldownGroup,
            out string detail)
        {
            rate = 0;
            durationMilliseconds = 0;
            cooldownMilliseconds = 0;
            cooldownGroup = 0;
            detail = null;
            var item = StackableItemProvider.Load(itemId);
            if (item?.Root == null)
            {
                detail = "stackable metadata or PVF file is unavailable";
                return DefinitionStatus.NotApplicable;
            }

            var rateNodes = item.Root.GetChildren("exp bonus rate");
            if (rateNodes.Count == 0)
            {
                detail = BuildInvalidDefinitionDetail(
                    itemId,
                    item,
                    "missing [exp bonus rate]");
                return DefinitionStatus.NotApplicable;
            }
            if (!TryReadScaledRate(item, rateNodes, out rate))
            {
                detail = BuildInvalidDefinitionDetail(
                    itemId,
                    item,
                    "invalid [exp bonus rate]");
                return DefinitionStatus.Invalid;
            }

            var nodes = item.Root.GetChildren("stat change duration");
            if (nodes.Count == 0
                || !TryReadFirstPositiveInt(
                    item,
                    nodes,
                    out durationMilliseconds))
            {
                detail = BuildInvalidDefinitionDetail(
                    itemId,
                    item,
                    "invalid [stat change duration]");
                return DefinitionStatus.Invalid;
            }

            // [cool time] 可选：缺失或异常按无冷却处理，不影响使用判定。
            var cooldownNodes = item.Root.GetChildren("cool time");
            if (cooldownNodes.Count > 0)
                TryReadFirstPositiveInt(item, cooldownNodes, out cooldownMilliseconds);

            // [cooltime group] 可选：同组道具共享冷却（如远古精灵秘药全系为 99）。
            cooldownGroup = StackableItemProvider.ResolveCooltimeGroup(itemId);

            return DefinitionStatus.Supported;
        }

        private static bool TryReadFirstPositiveInt(
            StackableItemFile item,
            IReadOnlyList<ScriptNode> nodes,
            out int value)
        {
            value = 0;
            foreach (var node in nodes)
            {
                if (TryReadFirstPositiveInt(item, node, out value))
                    return true;
            }

            value = 0;
            return false;
        }

        private static bool TryReadFirstPositiveInt(
            StackableItemFile item,
            ScriptNode node,
            out int value)
        {
            foreach (var dataItem in node.DataItems)
            {
                var raw = dataItem.GetContent(item.Content)
                    .Trim()
                    .Trim('`')
                    .Trim();
                var match = Regex.Match(raw, @"(?<!\d)\d+");
                if (match.Success
                    && int.TryParse(match.Value, out value)
                    && value > 0)
                    return true;
            }

            foreach (var child in node.Children)
            {
                if (TryReadFirstPositiveInt(item, child, out value))
                    return true;
            }

            value = 0;
            return false;
        }

        private static bool TryReadScaledRate(
            StackableItemFile item,
            IReadOnlyList<ScriptNode> nodes,
            out int scaledRate)
        {
            scaledRate = 0;
            foreach (var node in nodes)
            {
                if (TryReadScaledRate(item, node, out scaledRate))
                    return true;
            }

            return false;
        }

        private static bool TryReadScaledRate(
            StackableItemFile item,
            ScriptNode node,
            out int scaledRate)
        {
            foreach (var dataItem in node.DataItems)
            {
                if (TryParseScaledRate(dataItem.GetContent(item.Content), out scaledRate))
                    return true;
            }

            foreach (var child in node.Children)
            {
                if (TryReadScaledRate(item, child, out scaledRate))
                    return true;
            }

            scaledRate = 0;
            return false;
        }

        // PVF [exp bonus rate] 既可能是整数（1=2倍、2=3倍）也可能是小数（0.5=1.5倍），
        // 统一按不变区域性解析后乘以 RateScale 存为千分率。
        internal static bool TryParseScaledRate(string raw, out int scaledRate)
        {
            scaledRate = 0;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            var text = raw.Trim().Trim('`').Trim();
            var match = Regex.Match(text, @"(?<!\d)\d+(?:\.\d+)?");
            if (!match.Success
                || !double.TryParse(
                    match.Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var rate)
                || rate <= 0)
            {
                return false;
            }

            scaledRate = (int)Math.Round(
                rate * RateScale,
                MidpointRounding.AwayFromZero);
            return scaledRate > 0;
        }

        private static string BuildInvalidDefinitionDetail(
            int itemId,
            StackableItemFile item,
            string reason)
        {
            var tags = string.Join(
                ",",
                item.Root.Children.Select(node => node.Tag));
            return $"{reason} item={itemId} tags=[{tags}]";
        }

        private enum DefinitionStatus
        {
            NotApplicable,
            Invalid,
            Supported,
        }
    }
}
