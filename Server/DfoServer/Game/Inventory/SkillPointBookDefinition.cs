namespace DfoServer.Game.Inventory
{
    internal enum SkillPointBookKind
    {
        Grant,
        ResetTp,
    }

    internal sealed class SkillPointBookDefinition
    {
        internal SkillPointBookDefinition(int itemTemplateId)
        {
            ItemTemplateId = itemTemplateId;
        }

        internal int ItemTemplateId { get; }
        internal int GrantedSp { get; set; }
        internal int GrantedTp { get; set; }
        internal SkillPointBookKind Kind { get; set; }
        internal int MinimumLevel { get; set; } = -1;
        internal int MaximumLevel { get; set; } = -1;
        internal int AbsoluteExpirationUnixTime { get; set; }
        internal int UsablePeriodDays { get; set; }
        internal bool IsSkillPointBook { get; set; }
        internal bool ResetsTp => Kind == SkillPointBookKind.ResetTp;
        internal bool IsSupported { get; set; }
        internal string UnsupportedReason { get; set; }

        internal bool IsTemplateAvailableAt(uint unixTime)
            => IsSupported
                && (AbsoluteExpirationUnixTime <= 0
                    || (uint)AbsoluteExpirationUnixTime > unixTime);
    }
}
