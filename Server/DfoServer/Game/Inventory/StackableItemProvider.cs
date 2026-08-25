using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{
    internal static class StackableItemProvider
    {
        internal const string LegacyType = "[legacy]";
        internal const string UpgradableLegacyType = "[upgradable legacy]";
        internal const string RandomUpgradableLegacyType = "[random upgradable legacy]";
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<int, StackableItemFile> Cache = new Dictionary<int, StackableItemFile>();

        internal static StackableItemFile Load(int itemTemplateId)
        {
            if (itemTemplateId <= 0)
                return null;

            lock (CacheLock)
            {
                if (Cache.TryGetValue(itemTemplateId, out var cached))
                    return cached;
            }

            try
            {
                var entry = ItemMetadataResolver.GetStackableEntry(itemTemplateId);
                if (entry == null)
                    return null;

                var parsed = StackableItemFile.Parse(GameWorld.PvfArchiveAccessor.ReadText(Path.Combine("stackable", entry.FilePath)));
                lock (CacheLock)
                    Cache[itemTemplateId] = parsed;
                return parsed;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"  [StackableItemProvider] failed to load item=0x{itemTemplateId:X8}: {ex.Message}");
                return null;
            }
        }

        // [cooltime group]：同组消耗品共享冷却（如远古精灵秘药全系为 99）。
        internal static int ResolveCooltimeGroup(int itemTemplateId)
        {
            var item = Load(itemTemplateId);
            if (item?.Root == null)
                return 0;

            var nodes = item.Root.GetChildren("cooltime group");
            if (nodes.Count == 0)
                return 0;
            return TryReadFirstPositiveInt(item, nodes, out var group) ? group : 0;
        }

        // 返回与 item 同冷却组的全部道具 id（含自身）；无组或索引不可用时只含自身。
        internal static IReadOnlyList<int> ResolveCooltimeGroupMembers(int itemTemplateId)
        {
            var group = ResolveCooltimeGroup(itemTemplateId);
            if (group > 0
                && CooltimeGroupMembers.Value.TryGetValue(group, out var members)
                && members.Count > 0)
            {
                return members;
            }

            return new[] { itemTemplateId };
        }

        private static readonly Lazy<Dictionary<int, List<int>>> CooltimeGroupMembers =
            new Lazy<Dictionary<int, List<int>>>(BuildCooltimeGroupMembers);

        // 全量扫描 stackable.lst 建立 组→成员 索引；只在首次需要时构建一次。
        private static Dictionary<int, List<int>> BuildCooltimeGroupMembers()
        {
            var map = new Dictionary<int, List<int>>();
            try
            {
                var list = LstFile.Parse(GameWorld.PvfArchiveAccessor.ReadText("stackable/stackable.lst"));
                foreach (var entry in list.Entries)
                {
                    var group = ResolveCooltimeGroup(entry.Id);
                    if (group <= 0)
                        continue;
                    if (!map.TryGetValue(group, out var members))
                        map[group] = members = new List<int>();
                    members.Add(entry.Id);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"  [StackableItemProvider] cooltime group index build failed: {ex.Message}");
            }
            return map;
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

        internal static bool IsLegacyContainer(int itemTemplateId)
        {
            var stackable = Load(itemTemplateId);
            if (stackable == null)
                return false;

            var type = NormalizeType(stackable.StackableType);
            return type.Equals(UpgradableLegacyType, StringComparison.OrdinalIgnoreCase)
                || type.Equals(RandomUpgradableLegacyType, StringComparison.OrdinalIgnoreCase);
        }

        internal static string NormalizeType(string stackableType)
        {
            if (string.IsNullOrWhiteSpace(stackableType))
                return string.Empty;

            var text = stackableType.Trim();
            var firstQuote = text.IndexOf('`');
            if (firstQuote >= 0)
            {
                var secondQuote = text.IndexOf('`', firstQuote + 1);
                if (secondQuote > firstQuote)
                    return text.Substring(firstQuote + 1, secondQuote - firstQuote - 1).Trim();
            }

            var bracketStart = text.IndexOf('[');
            if (bracketStart >= 0)
            {
                var bracketEnd = text.IndexOf(']', bracketStart + 1);
                if (bracketEnd > bracketStart)
                    return text.Substring(bracketStart, bracketEnd - bracketStart + 1).Trim();
            }

            return text.Replace("`", string.Empty).Trim();
        }
    }
}
