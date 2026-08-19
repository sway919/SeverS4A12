using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Inventory
{
    internal sealed class PlayerTradeOffer
    {
        public short TradeSlot { get; set; }

        public byte SourceList { get; set; }

        public short SourceSlot { get; set; }

        public int Amount { get; set; }

        public bool IsGold { get; set; }

        public ItemCore SourceSnapshot { get; set; }

        public ItemCore Item { get; set; }

        public PlayerTradeOffer Copy()
        {
            return new PlayerTradeOffer
            {
                TradeSlot = TradeSlot,
                SourceList = SourceList,
                SourceSlot = SourceSlot,
                Amount = Amount,
                IsGold = IsGold,
                SourceSnapshot = SourceSnapshot?.Copy(),
                Item = Item?.Copy(),
            };
        }
    }

    internal sealed class PlayerTradeExecutionResult
    {
        private readonly List<Action> _rollback = new List<Action>();

        public bool Success { get; set; }

        public string Failure { get; set; }

        public InventoryMutationSet FirstChanges { get; } =
            new InventoryMutationSet();

        public InventoryMutationSet SecondChanges { get; } =
            new InventoryMutationSet();

        internal List<Action> RollbackActions => _rollback;

        internal void Rollback()
        {
            for (var index = _rollback.Count - 1; index >= 0; index--)
                _rollback[index]();
            _rollback.Clear();
            Success = false;
        }

        internal void Commit()
        {
            _rollback.Clear();
        }
    }

    internal static class PlayerTradeRuntimeService
    {
        internal const byte VirtualItemSpace = 36;

        internal static bool TryCreateOffer(
            InventoryService inventory,
            byte sourceList,
            short sourceSlot,
            int amount,
            out PlayerTradeOffer offer,
            out string failure)
        {
            offer = null;
            failure = null;
            if (inventory == null || amount <= 0)
                return Fail(out failure, "invalid amount");

            if (sourceList == (byte)InventoryListType.Main
                && sourceSlot == InventoryService.MainVirtualCurrencySlotStart)
            {
                var gold = inventory.GetMainVirtualCount(sourceSlot)?.Count ?? 0;
                if (gold < amount)
                    return Fail(out failure, "insufficient gold");

                offer = new PlayerTradeOffer
                {
                    IsGold = true,
                    SourceList = sourceList,
                    SourceSlot = sourceSlot,
                    Amount = amount,
                };
                return true;
            }

            if (sourceList == VirtualItemSpace)
            {
                if (!inventory.TryGetMainVirtualCount(sourceSlot, out var virtualItem)
                    || virtualItem.Count < amount)
                {
                    return Fail(out failure, "virtual source changed");
                }

                var virtualCore = ItemCore.Create(
                    ItemCore.KindMaterial,
                    virtualItem.ItemId);
                virtualCore.Count = amount;
                offer = new PlayerTradeOffer
                {
                    SourceList = sourceList,
                    SourceSlot = sourceSlot,
                    Amount = amount,
                    SourceSnapshot = virtualCore.Copy(),
                    Item = virtualCore,
                };
                return true;
            }

            // Player trade only accepts the character's main inventory. Avatar,
            // creature and cargo items carry owner-scoped detail/state that cannot
            // be transferred by copying an ItemCore.
            if (sourceList != (byte)InventoryListType.Main
                || InventoryService.IsVirtualMainSlot(sourceSlot))
            {
                return Fail(out failure, "unsupported source list");
            }

            var source = inventory.GetItem(InventoryListType.Main, sourceSlot)?.Copy();
            if (source == null || source.IsEmpty)
                return Fail(out failure, "source item missing");

            var stackable = InventoryStackRuleService.IsStackable(source);
            if ((!stackable && amount != 1)
                || (stackable && source.Count < amount))
            {
                return Fail(out failure, "invalid item amount");
            }

            if (!IsTradable(source, out failure))
                return false;

            var transfer = source.Copy();
            if (stackable)
                transfer.Count = amount;
            offer = new PlayerTradeOffer
            {
                SourceList = sourceList,
                SourceSlot = sourceSlot,
                Amount = amount,
                SourceSnapshot = source,
                Item = transfer,
            };
            return true;
        }

        internal static bool TryExecuteAndPersist(
            InventoryLease firstLease,
            IReadOnlyList<PlayerTradeOffer> firstOffers,
            InventoryLease secondLease,
            IReadOnlyList<PlayerTradeOffer> secondOffers,
            out PlayerTradeExecutionResult result)
        {
            result = new PlayerTradeExecutionResult();
            if (firstLease == null
                || secondLease == null
                || firstLease.Inventory == null
                || secondLease.Inventory == null
                || firstLease.CharacterId == secondLease.CharacterId)
            {
                result.Failure = "invalid inventory leases";
                return false;
            }

            var low = firstLease.CharacterId < secondLease.CharacterId
                ? firstLease
                : secondLease;
            var high = ReferenceEquals(low, firstLease)
                ? secondLease
                : firstLease;

            lock (low.SyncRoot)
            lock (high.SyncRoot)
            {
                if (!TryApplyExchange(
                        firstLease.Inventory,
                        firstOffers,
                        secondLease.Inventory,
                        secondOffers,
                        out result))
                {
                    return false;
                }

                if (!InventoryPersistenceService.SaveDirtyPair(
                        firstLease,
                        secondLease))
                {
                    result.Failure = "inventory persistence rejected";
                    result.Rollback();
                    return false;
                }

                result.Commit();
                return true;
            }
        }

        internal static bool TryApplyExchange(
            InventoryService firstInventory,
            IReadOnlyList<PlayerTradeOffer> firstOffers,
            InventoryService secondInventory,
            IReadOnlyList<PlayerTradeOffer> secondOffers,
            out PlayerTradeExecutionResult result)
        {
            result = new PlayerTradeExecutionResult();
            if (firstInventory == null || secondInventory == null)
            {
                result.Failure = "inventory missing";
                return false;
            }

            firstOffers = firstOffers ?? Array.Empty<PlayerTradeOffer>();
            secondOffers = secondOffers ?? Array.Empty<PlayerTradeOffer>();

            // Plan on isolated copies first. This guarantees that an inventory
            // capacity failure cannot partially mutate either live character.
            var firstPlan = InventoryCompoundPlanning.CloneInventory(firstInventory);
            var secondPlan = InventoryCompoundPlanning.CloneInventory(secondInventory);
            var planning = new PlayerTradeExecutionResult();
            if (!TryApplyExchangeCore(
                    firstPlan,
                    firstOffers,
                    secondPlan,
                    secondOffers,
                    planning))
            {
                result.Failure = planning.Failure;
                return false;
            }

            if (!TryApplyExchangeCore(
                    firstInventory,
                    firstOffers,
                    secondInventory,
                    secondOffers,
                    result))
            {
                result.Rollback();
                return false;
            }

            result.Success = true;
            return true;
        }

        private static bool TryApplyExchangeCore(
            InventoryService firstInventory,
            IReadOnlyList<PlayerTradeOffer> firstOffers,
            InventoryService secondInventory,
            IReadOnlyList<PlayerTradeOffer> secondOffers,
            PlayerTradeExecutionResult result)
        {
            if (!TryRemoveOffers(
                    firstInventory,
                    firstOffers,
                    result.FirstChanges,
                    result.RollbackActions,
                    out var failure)
                || !TryRemoveOffers(
                    secondInventory,
                    secondOffers,
                    result.SecondChanges,
                    result.RollbackActions,
                    out failure))
            {
                result.Failure = failure;
                return false;
            }

            var firstGold = SumGold(firstOffers);
            var secondGold = SumGold(secondOffers);
            if (!TryExchangeGold(
                    firstInventory,
                    firstGold,
                    secondGold,
                    result.FirstChanges,
                    result.RollbackActions,
                    out failure)
                || !TryExchangeGold(
                    secondInventory,
                    secondGold,
                    firstGold,
                    result.SecondChanges,
                    result.RollbackActions,
                    out failure))
            {
                result.Failure = failure;
                return false;
            }

            if (!TryInsertOffers(
                    firstInventory,
                    secondOffers,
                    result.FirstChanges,
                    result.RollbackActions,
                    out failure)
                || !TryInsertOffers(
                    secondInventory,
                    firstOffers,
                    result.SecondChanges,
                    result.RollbackActions,
                    out failure))
            {
                result.Failure = failure;
                return false;
            }

            return true;
        }

        private static bool TryRemoveOffers(
            InventoryService inventory,
            IEnumerable<PlayerTradeOffer> offers,
            InventoryMutationSet changes,
            ICollection<Action> rollback,
            out string failure)
        {
            failure = null;
            var sources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var offer in offers.Where(value => value != null && !value.IsGold))
            {
                var sourceKey = $"{offer.SourceList}:{offer.SourceSlot}";
                if (!sources.Add(sourceKey))
                    return Fail(out failure, "duplicate source item");

                var listType = InventoryListType.Main;
                if (offer.SourceList == VirtualItemSpace)
                {
                    var before = inventory.GetMainVirtualCount(offer.SourceSlot);
                    if (before == null
                        || offer.Item == null
                        || before.ItemId != offer.Item.ItemId
                        || before.Count < offer.Amount)
                    {
                        return Fail(out failure, "virtual source changed");
                    }

                    if (!InventoryDeleteService.TryDeleteForClient(
                            inventory,
                            listType,
                            offer.SourceSlot,
                            offer.Amount,
                            out _))
                    {
                        return Fail(out failure, "virtual source remove failed");
                    }
                    rollback.Add(() => inventory.SetMainVirtualCount(
                        offer.SourceSlot,
                        before.ItemId,
                        before.Count));
                    changes.AddSlot(InventoryListType.Main, offer.SourceSlot);
                    continue;
                }

                if (offer.SourceList != (byte)InventoryListType.Main)
                    return Fail(out failure, "unsupported source list");

                var source = inventory.GetItem(listType, offer.SourceSlot)?.Copy();
                if (!MatchesSource(source, offer.SourceSnapshot, offer.Amount))
                    return Fail(out failure, "source item changed");

                if (!InventoryDeleteService.TryDeleteForClient(
                        inventory,
                        listType,
                        offer.SourceSlot,
                        offer.Amount,
                        out _))
                {
                    return Fail(out failure, "source item remove failed");
                }
                rollback.Add(() => inventory.SetItem(
                    listType,
                    offer.SourceSlot,
                    source.Copy()));
                changes.AddSlot(listType, offer.SourceSlot);
            }
            return true;
        }

        private static bool TryExchangeGold(
            InventoryService inventory,
            int outgoing,
            int incoming,
            InventoryMutationSet changes,
            ICollection<Action> rollback,
            out string failure)
        {
            failure = null;
            if (outgoing == 0 && incoming == 0)
                return true;

            var slot = InventoryService.MainVirtualCurrencySlotStart;
            var before = inventory.GetMainVirtualCount(slot)?.Count ?? 0;
            var after = (long)before - outgoing + incoming;
            if (after < 0 || after > int.MaxValue)
                return Fail(out failure, "invalid gold balance");
            if (!inventory.SetMainVirtualCount(slot, (int)after))
                return Fail(out failure, "gold update failed");

            rollback.Add(() => inventory.SetMainVirtualCount(slot, before));
            changes.AddSlot(InventoryListType.Main, slot);
            return true;
        }

        private static bool TryInsertOffers(
            InventoryService inventory,
            IEnumerable<PlayerTradeOffer> offers,
            InventoryMutationSet changes,
            ICollection<Action> rollback,
            out string failure)
        {
            failure = null;
            foreach (var offer in offers.Where(value => value != null && !value.IsGold))
            {
                if (offer.SourceList == VirtualItemSpace)
                {
                    var virtualBefore = inventory.GetMainVirtualCount(offer.SourceSlot);
                    var currentCount = virtualBefore?.Count ?? 0;
                    var itemId = offer.Item?.ItemId ?? 0;
                    var after = (long)currentCount + offer.Amount;
                    if (virtualBefore == null
                        || itemId <= 0
                        || offer.Amount <= 0
                        || virtualBefore.ItemId != itemId
                        || after > int.MaxValue
                        || !inventory.SetMainVirtualCount(
                            offer.SourceSlot,
                            itemId,
                            (int)after))
                    {
                        return Fail(out failure, "recipient virtual item update failed");
                    }

                    rollback.Add(() => inventory.SetMainVirtualCount(
                        offer.SourceSlot,
                        virtualBefore.ItemId,
                        currentCount));
                    changes.AddSlot(InventoryListType.Main, offer.SourceSlot);
                    continue;
                }

                var item = PrepareTransferredItem(offer.Item);
                if (item == null)
                    return Fail(out failure, "recipient item missing");

                if (!InventoryInsertService.TryPlanInsertByDefaultRule(
                        inventory,
                        item,
                        offer.Amount,
                        out var plan)
                    || plan.RemainingCount != 0)
                {
                    return Fail(
                        out failure,
                        $"recipient insert plan failed: {plan?.Error}");
                }

                var before = inventory.GetItem(plan.ListType, plan.SlotIndex)?.Copy();
                if (!InventoryRewardGrantService.TryInsertExisting(
                        inventory,
                        item,
                        offer.Amount,
                        ItemCreateReason.PlayerTrade,
                        null,
                        out var grant)
                    || !grant.Success)
                {
                    return Fail(out failure, "recipient insert failed");
                }

                rollback.Add(() =>
                {
                    if (before == null)
                        inventory.RemoveItem(plan.ListType, plan.SlotIndex);
                    else
                        inventory.SetItem(plan.ListType, plan.SlotIndex, before.Copy());
                });
                changes.AddRange(grant.Changes);
            }
            return true;
        }

        private static ItemCore PrepareTransferredItem(ItemCore source)
        {
            if (source == null)
                return null;

            var copy = source.Copy();
            var metadata = ItemMetadataResolver.Resolve(copy.ItemId);
            if (IsTradeLimit(metadata) && copy.StackTradeCount > 0)
                copy.StackTradeCount--;
            return copy;
        }

        private static bool MatchesSource(
            ItemCore current,
            ItemCore snapshot,
            int amount)
        {
            if (current == null
                || snapshot == null
                || current.ItemId != snapshot.ItemId)
            {
                return false;
            }

            var stackable = InventoryStackRuleService.IsStackable(current);
            if ((!stackable && amount != 1)
                || (stackable && current.Count < amount))
            {
                return false;
            }

            var left = current.Copy();
            var right = snapshot.Copy();
            if (stackable)
            {
                left.Count = 0;
                right.Count = 0;
            }
            return left.ToBytes().SequenceEqual(right.ToBytes());
        }

        private static bool IsTradable(ItemCore item, out string failure)
        {
            failure = null;
            if (item.TradeRestriction != 0)
                return Fail(out failure, "instance trade restriction");
            if (item.ExpireTime >= 1000000000
                && item.ExpireTime <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                return Fail(out failure, "item expired");
            }

            var metadata = ItemMetadataResolver.Resolve(item.ItemId);
            var attachType = NormalizeAttachType(metadata?.AttachType);
            if (IsTradeLimit(metadata) && item.StackTradeCount == 0)
                return Fail(out failure, "trade limit exhausted");
            if ((attachType == "sealing" || attachType == "seal")
                && item.SealFlag == 0)
            {
                return Fail(out failure, "unsealed item");
            }
            if (attachType.Contains("account")
                || attachType.Contains("character")
                || attachType.Contains("no trade")
                || attachType.Contains("not trade")
                || attachType.Contains("untrade")
                || attachType == "bind"
                || attachType == "bound")
            {
                return Fail(out failure, "PVF attach restriction");
            }
            return true;
        }

        private static bool IsTradeLimit(ItemMetadata metadata)
        {
            return NormalizeAttachType(metadata?.AttachType) == "trade limit";
        }

        private static string NormalizeAttachType(string value)
        {
            return (value ?? string.Empty)
                .Replace("`", string.Empty)
                .Replace("[", string.Empty)
                .Replace("]", string.Empty)
                .Trim()
                .ToLowerInvariant();
        }

        private static int SumGold(IEnumerable<PlayerTradeOffer> offers)
        {
            long total = offers
                .Where(value => value != null && value.IsGold)
                .Sum(value => (long)value.Amount);
            return total > int.MaxValue ? int.MaxValue : (int)Math.Max(0, total);
        }

        private static bool Fail(out string failure, string value)
        {
            failure = value;
            return false;
        }
    }
}
