using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using Microsoft.Data.Sqlite;
using System;
using System.Linq;

namespace DfoServer.Game.Skills
{
    internal sealed class TpSkillResetResult
    {
        internal bool Success { get; set; }
        internal int ResetSkillCount { get; set; }
        internal SkillInfoSnapshot Skills { get; set; }
        internal SkillPointState Points { get; set; }
        internal string Detail { get; set; }
    }

    // TP 初始化属于技能域：物品层只触发命令，不直接改 character_skills。
    internal sealed class TpSkillResetService
    {
        private readonly SqliteCharacterProgressRepository _repository;

        internal TpSkillResetService(SqliteCharacterProgressRepository repository)
        {
            _repository = repository
                ?? throw new ArgumentNullException(nameof(repository));
        }

        internal TpSkillResetResult ApplyInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            byte job,
            byte level,
            int bonusSp,
            int bonusTp,
            byte growType)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));

            var skills = _repository.LoadSkills(connection, transaction, characterId);
            CharacterStatComputer.DecodeGrowType(
                growType,
                out var firstGrow,
                out var secondGrow);
            var baseline = SkillPointLedger.BuildFreeBaseline(
                job,
                firstGrow,
                secondGrow);
            var resetCount = 0;

            foreach (var page in skills.Pages)
            {
                foreach (var entry in page.Entries.ToArray())
                {
                    var definition = SkillDataProvider.GetSkill(job, entry.SkillId);
                    if (definition == null || !definition.IsTpSkill)
                        continue;

                    baseline.TryGetValue(entry.SkillId, out var freeLevel);
                    if (entry.Level <= freeLevel)
                        continue;

                    if (freeLevel == 0)
                        page.Entries.Remove(entry);
                    else
                        entry.Level = freeLevel;
                    resetCount++;
                }
            }

            if (resetCount == 0)
            {
                return new TpSkillResetResult
                {
                    Detail = "no refundable TP skill level was found",
                };
            }

            var points = SkillStateService.ResolvePointState(
                skills,
                job,
                level,
                bonusSp,
                bonusTp,
                firstGrow,
                secondGrow);
            SkillStateService.ApplyProtocolMirrors(skills, points);
            _repository.SaveSkillProgress(
                connection,
                transaction,
                characterId,
                skills);

            return new TpSkillResetResult
            {
                Success = true,
                ResetSkillCount = resetCount,
                Skills = skills,
                Points = points,
            };
        }
    }
}
