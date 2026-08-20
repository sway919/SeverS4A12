using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{
    internal static class LegacyExperienceBookDataProvider
    {
        // 旧版经验书没有 [increase status type]，其经验量只能由已核对的文件路径确定。
        // 其余 PVF 结构仍逐项校验，避免把普通 [etc] 道具错误消耗掉。
        private static readonly Dictionary<string, uint> KnownBooks
            = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
            {
                ["book_exp1.stk"] = 100,
                ["book_exp2.stk"] = 1_000,
                ["book_exp3.stk"] = 10_000,
                ["book_exp4.stk"] = 100_000,
                ["book_exp5.stk"] = 1_000_000,
                ["book_exp6.stk"] = 10_000_000,
            };

        private static readonly HashSet<string> AllowedRootTags
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "name", "explain", "grade", "attach type", "rarity", "weight",
                "usable job", "minimum level", "maximum level", "stack limit",
                "icon", "field image", "stackable type", "price", "value",
                "move wav", "use wav", "expiration date", "usable period",
            };

        internal static ExperienceItemDefinition Resolve(int itemTemplateId)
            => Resolve(
                itemTemplateId,
                ItemMetadataResolver.GetStackableEntry(itemTemplateId)?.FilePath,
                StackableItemProvider.Load(itemTemplateId));

        internal static ExperienceItemDefinition Resolve(
            int itemTemplateId,
            string filePath,
            StackableItemFile stackable)
        {
            var result = new ExperienceItemDefinition(itemTemplateId);
            if (!KnownBooks.TryGetValue(NormalizePath(filePath), out var grantedExp))
                return result;

            result.IsExperienceLike = true;
            if (stackable?.Root == null)
                return Reject(result, "legacy experience-book PVF definition is unavailable");

            if (!string.Equals(
                    NormalizeText(stackable.Name),
                    $"增加{grantedExp}经验值的书",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Reject(result, "legacy experience-book name does not match its reviewed definition");
            }

            if (!IsEtcVariantOne(stackable.StackableType))
                return Reject(result, "legacy experience book is not a variant-1 [etc] stackable");

            if (!string.Equals(
                    NormalizeText(stackable.GetStringValue("use wav")),
                    "EXPERIENCE_UP_ITEM",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Reject(result, "legacy experience book is missing the EXPERIENCE_UP_ITEM use marker");
            }

            var effects = stackable.StatusIncreases;
            if ((effects != null && effects.Count > 0)
                || !string.IsNullOrWhiteSpace(stackable.ActionTypeName))
            {
                return Reject(result, "legacy experience book is mixed with another stackable behavior");
            }

            var unknownTag = stackable.Root.Children.FirstOrDefault(node =>
                !AllowedRootTags.Contains(node.Tag));
            if (unknownTag != null)
                return Reject(result, $"unreviewed PVF behavior tag [{unknownTag.Tag}]");

            if (!HasAllJobsOnly(stackable))
                return Reject(result, "legacy experience-book usable-job definition is not [all]");

            if (!TryReadLevelRestrictions(
                    stackable,
                    out var minimumLevel,
                    out var maximumLevel,
                    out var error))
            {
                return Reject(result, error);
            }

            if (!StackableExpirationPolicyResolver.TryResolve(stackable, out var expiration))
                return Reject(result, "invalid expiration definition");

            result.GrantKind = ExperienceItemGrantKind.Fixed;
            result.Value = grantedExp;
            result.MinimumLevel = minimumLevel;
            result.MaximumLevel = maximumLevel;
            result.AbsoluteExpirationUnixTime = expiration.AbsoluteExpirationUnixTime;
            result.UsablePeriodDays = expiration.UsablePeriodDays;
            result.IsSupported = true;
            return result;
        }

        private static bool TryReadLevelRestrictions(
            StackableItemFile stackable,
            out int minimumLevel,
            out int maximumLevel,
            out string error)
        {
            minimumLevel = -1;
            maximumLevel = -1;
            error = null;
            if (!StackablePvfValueReader.TryReadOptionalNonNegativeInt(
                    stackable,
                    "minimum level",
                    out var hasMinimum,
                    out var minimum)
                || !StackablePvfValueReader.TryReadOptionalNonNegativeInt(
                    stackable,
                    "maximum level",
                    out var hasMaximum,
                    out var maximum))
            {
                error = "invalid level restriction definition";
                return false;
            }

            minimumLevel = hasMinimum ? minimum : -1;
            maximumLevel = hasMaximum ? maximum : -1;
            if (!hasMinimum || !hasMaximum || minimum <= maximum)
                return true;

            error = "minimum level exceeds maximum level";
            return false;
        }

        private static bool HasAllJobsOnly(StackableItemFile stackable)
        {
            var nodes = stackable.Root.GetChildren("usable job");
            if (nodes.Count != 1 || nodes[0].Children.Count != 0)
                return false;

            var jobs = new List<string>();
            foreach (var item in nodes[0].DataItems)
            {
                foreach (Match match in Regex.Matches(
                             item.GetContent(stackable.Content) ?? string.Empty,
                             @"\[(?<job>[^\]]+)\]"))
                {
                    var job = NormalizeText(match.Groups["job"].Value);
                    if (job.Length > 0)
                        jobs.Add(job);
                }
            }

            return jobs.Count == 1
                && string.Equals(jobs[0], "all", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEtcVariantOne(string raw)
        {
            var match = Regex.Match(
                raw ?? string.Empty,
                @"^\s*`?\[(?<type>[^\]]+)\]`?\s+(?<variant>-?\d+)\s*$");
            return match.Success
                && string.Equals(
                    match.Groups["type"].Value.Trim(),
                    "etc",
                    StringComparison.OrdinalIgnoreCase)
                && match.Groups["variant"].Value == "1";
        }

        private static string NormalizePath(string raw)
            => (raw ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');

        private static string NormalizeText(string raw)
            => (raw ?? string.Empty).Trim().Trim('`').Trim();

        private static ExperienceItemDefinition Reject(
            ExperienceItemDefinition result,
            string reason)
        {
            result.IsSupported = false;
            result.UnsupportedReason = reason;
            return result;
        }
    }
}
