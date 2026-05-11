using System;
using System.Collections.Generic;
using System.Linq;
using YGOSharp.Network.Enums;
using YGOSharp.OCGWrapper.Enums;

namespace WindBot.Game.AI.Decks
{
    // Pure C# port of src/heuristic_v3.py — board-aware heuristic opponent.
    // Runs the same decision logic as the Python v3 but uses WindBot's native
    // DuelPlayer/ClientCard API directly, with no numpy or HTTP calls.
    // I1–I12 labels refer to the improvements documented in heuristic_v3.py.
    [Deck("HeuristicV3", "AI_Yugi_Kaiba_Beat", "Normal")]
    public class HeuristicV3Executor : Executor
    {
        // ── Konami card IDs for the Yugi/Kaiba starter format ─────────────────
        private static class C
        {
            public const int SUMMONED_SKULL  = 70781052;
            public const int LA_JINN         = 97590747;
            public const int BATTLE_OX       = 5053103;
            public const int NEO             = 50930991;
            public const int WALL_OF_ILLUSION= 13945283;
            public const int TRAP_MASTER     = 46461247;
            public const int MAN_EATER_BUG   = 54652250;
            public const int CHANGE_OF_HEART = 4031928;
            public const int RAIGEKI         = 12580477;
            public const int DE_SPELL        = 19159413;
            public const int DARK_HOLE       = 53129443;
            public const int POT_OF_GREED    = 55144522;
            public const int FISSURE         = 66788016;
            public const int SOUL_EXCHANGE   = 68005187;
            public const int SWORDS          = 72302403;
            public const int MONSTER_REBORN  = 83764719;
            public const int TRAP_HOLE       = 4206964;
            public const int WABOKU         = 12607053;
            public const int REINFORCEMENTS  = 17814387;
        }

        // ATK values for cards we know statically (for lethal calculator)
        private static readonly Dictionary<int, int> KnownAtk = new Dictionary<int, int>
        {
            { C.SUMMONED_SKULL,   2500 },
            { C.LA_JINN,          1800 },
            { C.BATTLE_OX,        1700 },
            { C.NEO,              1700 },
            { C.TRAP_MASTER,      1500 },
            { C.WALL_OF_ILLUSION, 1000 },
            { C.MAN_EATER_BUG,     450 },
        };

        // Kill priority (higher = eliminate first)
        private static readonly Dictionary<int, int> KillPriority = new Dictionary<int, int>
        {
            { C.SUMMONED_SKULL,   100 },
            { C.LA_JINN,           90 },
            { C.WALL_OF_ILLUSION,  80 },
        };

        // Offensive cards (prefer ATK position)
        private static readonly HashSet<int> OffensiveCards = new HashSet<int>
            { C.LA_JINN, C.BATTLE_OX, C.NEO, C.SUMMONED_SKULL };

        // Defensive cards (prefer DEF / face-down)
        private static readonly HashSet<int> DefensiveCards = new HashSet<int>
            { C.WALL_OF_ILLUSION, C.MAN_EATER_BUG, C.TRAP_MASTER };

        public HeuristicV3Executor(GameAI ai, Duel duel) : base(ai, duel)
        {
            Console.WriteLine("[HeuristicV3] Executor loaded");
        }

        // ── State helpers ──────────────────────────────────────────────────────

        private IEnumerable<ClientCard> MyMonsters()
            => Bot.GetMonsters().Where(c => c != null);

        private IEnumerable<ClientCard> OpMonsters()
            => Enemy.GetMonsters().Where(c => c != null);

        private IEnumerable<ClientCard> MyBackrow()
            => Bot.GetSpells().Where(c => c != null);

        private IEnumerable<ClientCard> OpBackrow()
            => Enemy.GetSpells().Where(c => c != null);

        private bool InHand(int cardId)
            => Bot.Hand.Any(c => c.Id == cardId);

        private bool InGrave(int cardId)
            => Bot.Graveyard.Any(c => c.Id == cardId);

        private bool OpHasOnField(int cardId)
            => OpMonsters().Any(c => c.Id == cardId)
            || OpBackrow().Any(c => c.Id == cardId);

        private bool IsBattlePhase()
        {
            return Duel.Phase == DuelPhase.BattleStart
                || Duel.Phase == DuelPhase.BattleStep
                || Duel.Phase == DuelPhase.Damage
                || Duel.Phase == DuelPhase.DamageCal
                || Duel.Phase == DuelPhase.Battle;
        }

        private int OpMaxFaceUpAtk()
            => OpMonsters()
               .Where(c => c.IsFaceup() && c.Position == (int)CardPosition.FaceUpAttack)
               .Select(c => c.Attack)
               .DefaultIfEmpty(0)
               .Max();

        private int MyMaxAtk()
            => MyMonsters()
               .Where(c => c.IsFaceup() && c.Position == (int)CardPosition.FaceUpAttack)
               .Select(c => c.Attack)
               .DefaultIfEmpty(0)
               .Max();

        private int BestHandAtk(params int[] exclude)
        {
            var excSet = new HashSet<int>(exclude);
            return Bot.Hand
                .Where(c => KnownAtk.ContainsKey(c.Id) && !excSet.Contains(c.Id))
                .Select(c => KnownAtk[c.Id])
                .DefaultIfEmpty(0)
                .Max();
        }

        private bool HaveHardRemoval()
            => InHand(C.FISSURE) || InHand(C.DARK_HOLE)
            || InHand(C.RAIGEKI) || InHand(C.CHANGE_OF_HEART);

        // I8: Conservative max-damage estimate for the lethal super-step.
        // Mirrors Python's _max_damage_this_turn.
        private int MaxDamageThisTurn(IEnumerable<ClientCard> opFieldOverride = null)
        {
            var opAtk = (opFieldOverride ?? OpMonsters())
                .Where(c => c.IsFaceup() && c.Position == (int)CardPosition.FaceUpAttack)
                .Select(c => c.Attack)
                .OrderByDescending(x => x)
                .ToList();
            int opDefCount = (opFieldOverride ?? OpMonsters())
                .Count(c => c.IsFaceup() && c.Position == (int)CardPosition.FaceUpDefence);
            int opFdCount  = (opFieldOverride ?? OpMonsters())
                .Count(c => !c.IsFaceup());

            var attackers = MyMonsters()
                .Where(c => c.IsFaceup() && c.Position == (int)CardPosition.FaceUpAttack
                         && !c.Attacked)
                .Select(c => c.Attack)
                .OrderByDescending(x => x)
                .ToList();

            // Blockers: face-up ATK + face-down/DEF treated as 0 blocker
            var blockers = opAtk.Concat(Enumerable.Repeat(0, opDefCount + opFdCount)).ToList();

            int damage = 0;
            int bi = 0;
            foreach (int atk in attackers)
            {
                if (bi < blockers.Count)
                {
                    int blk = blockers[bi++];
                    if (atk > blk) damage += atk - blk;
                }
                else
                {
                    damage += atk; // direct
                }
            }
            return damage;
        }

        // Simulated field: op field with one card removed
        private IEnumerable<ClientCard> OpFieldWithout(ClientCard removed)
            => OpMonsters().Where(c => c != removed);

        // ── Action lookup helpers ──────────────────────────────────────────────

        private int? FindActivable(int cardId)
        {
            if (Main?.ActivableCards == null) return null;
            for (int i = 0; i < Main.ActivableCards.Count; i++)
                if (Main.ActivableCards[i].Id == cardId) return i;
            return null;
        }

        private int? FindSummonable(int cardId)
        {
            if (Main?.SummonableCards == null) return null;
            for (int i = 0; i < Main.SummonableCards.Count; i++)
                if (Main.SummonableCards[i].Id == cardId) return i;
            return null;
        }

        private int? FindMSettable(int cardId)
        {
            if (Main?.MonsterSetableCards == null) return null;
            for (int i = 0; i < Main.MonsterSetableCards.Count; i++)
                if (Main.MonsterSetableCards[i].Id == cardId) return i;
            return null;
        }

        private int? FindSpSummonable(int cardId)
        {
            if (Main?.SpecialSummonableCards == null) return null;
            for (int i = 0; i < Main.SpecialSummonableCards.Count; i++)
                if (Main.SpecialSummonableCards[i].Id == cardId) return i;
            return null;
        }

        private int? FindReposable(int cardId)
        {
            if (Main?.ReposableCards == null) return null;
            for (int i = 0; i < Main.ReposableCards.Count; i++)
                if (Main.ReposableCards[i].Id == cardId) return i;
            return null;
        }

        private MainPhaseAction Activate(int i) =>
            new MainPhaseAction(MainPhaseAction.MainAction.Activate,
                Main.ActivableCards[i].ActionActivateIndex[Main.ActivableDescs[i]]);

        private MainPhaseAction Summon(int i) =>
            new MainPhaseAction(MainPhaseAction.MainAction.Summon,
                Main.SummonableCards[i].ActionIndex);

        private MainPhaseAction SetMonster(int i) =>
            new MainPhaseAction(MainPhaseAction.MainAction.SetMonster,
                Main.MonsterSetableCards[i].ActionIndex);

        private MainPhaseAction SpSummon(int i) =>
            new MainPhaseAction(MainPhaseAction.MainAction.SpSummon,
                Main.SpecialSummonableCards[i].ActionIndex);

        private MainPhaseAction SetSpell(int i) =>
            new MainPhaseAction(MainPhaseAction.MainAction.SetSpell,
                Main.SpellSetableCards[i].ActionIndex);

        private MainPhaseAction Repos(int i) =>
            new MainPhaseAction(MainPhaseAction.MainAction.Repos,
                Main.ReposableCards[i].ActionIndex);

        // ── I8: Lethal super-step ──────────────────────────────────────────────

        private MainPhaseAction TryLethalSuperStep()
        {
            int baseDmg = MaxDamageThisTurn();
            if (baseDmg >= Enemy.LifePoints)
                return null; // already lethal without spells

            var opFaceUpAtk = OpMonsters()
                .Where(c => c.IsFaceup() && c.Position == (int)CardPosition.FaceUpAttack)
                .ToList();

            // CoH: steal strongest face-up attacker → lethal?
            int? cohIdx = FindActivable(C.CHANGE_OF_HEART);
            if (cohIdx.HasValue && opFaceUpAtk.Count > 0)
            {
                var stolen = opFaceUpAtk.OrderByDescending(c => c.Attack).First();
                int newDmg = MaxDamageThisTurn(OpFieldWithout(stolen));
                // Add stolen card as our attacker (approximate: direct or vs remaining)
                newDmg += stolen.Attack; // rough upper bound already conservative elsewhere
                if (newDmg >= Enemy.LifePoints)
                    return Activate(cohIdx.Value);
            }

            // Raigeki: clears all → all our ATK monsters go direct
            int? rkIdx = FindActivable(C.RAIGEKI);
            if (rkIdx.HasValue && OpMonsters().Any())
            {
                int totalAtk = MyMonsters()
                    .Where(c => c.IsFaceup() && c.Position == (int)CardPosition.FaceUpAttack && !c.Attacked)
                    .Sum(c => c.Attack);
                if (totalAtk >= Enemy.LifePoints)
                    return Activate(rkIdx.Value);
            }

            // Fissure: removes weakest → lethal?
            int? fsIdx = FindActivable(C.FISSURE);
            if (fsIdx.HasValue && opFaceUpAtk.Count > 0)
            {
                var weakest = opFaceUpAtk.OrderBy(c => c.Attack).First();
                int newDmg = MaxDamageThisTurn(OpFieldWithout(weakest));
                if (newDmg >= Enemy.LifePoints)
                    return Activate(fsIdx.Value);
            }

            // Soul Exchange + SS combo approximation
            int? seIdx = FindActivable(C.SOUL_EXCHANGE);
            if (seIdx.HasValue && InHand(C.SUMMONED_SKULL) && OpMonsters().Any())
            {
                var target = opFaceUpAtk.Count > 0
                    ? opFaceUpAtk.OrderByDescending(c => c.Attack).First()
                    : OpMonsters().First();
                var opAfter = OpFieldWithout(target)
                    .Where(c => c.IsFaceup() && c.Position == (int)CardPosition.FaceUpAttack)
                    .ToList();
                int ssDmg = opAfter.Count > 0
                    ? Math.Max(0, 2500 - opAfter.Max(c => c.Attack))
                    : 2500;
                int newDmg = MaxDamageThisTurn(OpFieldWithout(target)) + ssDmg;
                if (newDmg >= Enemy.LifePoints)
                    return Activate(seIdx.Value);
            }

            return null;
        }

        // ── MP1 step helpers ───────────────────────────────────────────────────

        // Set face-down traps
        private MainPhaseAction TryTrapSet()
        {
            if (Main?.SpellSetableCards == null) return null;
            for (int i = 0; i < Main.SpellSetableCards.Count; i++)
            {
                int id = Main.SpellSetableCards[i].Id;
                if (id == C.WABOKU || id == C.TRAP_HOLE || id == C.REINFORCEMENTS)
                    return SetSpell(i);
            }
            return null;
        }

        // Activate Dark Hole or Raigeki unconditionally (they're always good)
        private MainPhaseAction TryDarkHoleRaigeki()
        {
            if (OpMonsters().Any())
            {
                int? dh = FindActivable(C.DARK_HOLE);
                if (dh.HasValue) return Activate(dh.Value);
                int? rk = FindActivable(C.RAIGEKI);
                if (rk.HasValue) return Activate(rk.Value);
            }
            return null;
        }

        // I9: Soul Exchange when SS is in hand
        private MainPhaseAction TrySoulExchange()
        {
            if (!InHand(C.SUMMONED_SKULL)) return null;
            int? seIdx = FindActivable(C.SOUL_EXCHANGE);
            if (!seIdx.HasValue) return null;
            var opFaceUp = OpMonsters().Where(c => c.IsFaceup()).ToList();
            if (opFaceUp.Count > 0) return Activate(seIdx.Value);
            // Fallback: v2 logic — SE if we can't beat the opponent otherwise
            int strongest = OpMonsters().Select(c => c.Attack).DefaultIfEmpty(0).Max();
            int ourBest = BestHandAtk(C.SUMMONED_SKULL);
            if (!MyMonsters().Any() && OpMonsters().Any()) return Activate(seIdx.Value);
            if (strongest > ourBest) return Activate(seIdx.Value);
            return null;
        }

        // I5 + I10: summon logic with La Jinn priority and face-down caution
        private MainPhaseAction TrySummonV3()
        {
            bool opHasFaceDown = OpMonsters().Any(c => !c.IsFaceup());
            bool haveRemoval = HaveHardRemoval();

            // I10: if opp has face-down monsters and we lack removal, prefer setting defensive cards
            if (opHasFaceDown && !haveRemoval)
            {
                int? wall = FindMSettable(C.WALL_OF_ILLUSION);
                if (wall.HasValue) return SetMonster(wall.Value);
                int? bug = FindMSettable(C.MAN_EATER_BUG);
                if (bug.HasValue) return SetMonster(bug.Value);
            }

            // Override — normal summon to kill if possible
            foreach (int id in new[] { C.LA_JINN, C.BATTLE_OX, C.NEO })
            {
                int? si = FindSummonable(id);
                if (!si.HasValue) continue;
                int myAtk = KnownAtk.ContainsKey(id) ? KnownAtk[id] : 0;
                if (CanNormalSummonKill(myAtk)) return Summon(si.Value);
            }

            // Summoned Skull (tribute)
            int? ssIdx = FindSummonable(C.SUMMONED_SKULL);
            if (ssIdx.HasValue)
            {
                bool opHasLaJinn = OpHasOnField(C.LA_JINN);
                bool tributeOwn = MyMonsters().Any(); // _tribute_would_use_own_monster
                if (!tributeOwn) return Summon(ssIdx.Value);
                if (opHasLaJinn && !haveRemoval) return Summon(ssIdx.Value);
            }
            int? ssSpIdx = FindSpSummonable(C.SUMMONED_SKULL);
            if (ssSpIdx.HasValue) return SpSummon(ssSpIdx.Value);

            // Set defensive monsters
            int? wallSet = FindMSettable(C.WALL_OF_ILLUSION);
            if (wallSet.HasValue) return SetMonster(wallSet.Value);
            int? bugSet = FindMSettable(C.MAN_EATER_BUG);
            if (bugSet.HasValue) return SetMonster(bugSet.Value);

            // I5: La Jinn before Battle Ox/Neo
            foreach (int id in new[] { C.LA_JINN, C.BATTLE_OX, C.NEO })
            {
                int? si = FindSummonable(id);
                if (si.HasValue) return Summon(si.Value);
            }

            int? tm = FindMSettable(C.TRAP_MASTER);
            if (tm.HasValue) return SetMonster(tm.Value);

            // Fallback: any summon/mset/spsummon
            if (Main?.SummonableCards?.Count > 0) return Summon(0);
            if (Main?.MonsterSetableCards?.Count > 0) return SetMonster(0);
            if (Main?.SpecialSummonableCards?.Count > 0) return SpSummon(0);
            return null;
        }

        private bool CanNormalSummonKill(int myAtk)
            => OpMonsters()
               .Any(c => c.IsFaceup() && c.Position == (int)CardPosition.FaceUpAttack && myAtk > c.Attack);

        // I6, I7, I12: spell activation (Pot, Monster Reborn, De-Spell, CoH, Fissure, SoRL)
        private MainPhaseAction TrySpellV3()
        {
            // Pot of Greed — always
            int? pot = FindActivable(C.POT_OF_GREED);
            if (pot.HasValue) return Activate(pot.Value);

            // Monster Reborn (I6)
            int? mr = FindActivable(C.MONSTER_REBORN);
            if (mr.HasValue)
            {
                // Original: SS or La Jinn in GY
                if (InGrave(C.SUMMONED_SKULL) || InGrave(C.LA_JINN))
                    return Activate(mr.Value);
                // New: any beater in GY when field empty and no hand summon
                bool beaterInGy = InGrave(C.BATTLE_OX) || InGrave(C.NEO)
                    || InGrave(C.WALL_OF_ILLUSION) || InGrave(C.MAN_EATER_BUG);
                bool haveHandSummon = Bot.Hand.Any(c =>
                    c.Id == C.SUMMONED_SKULL || c.Id == C.LA_JINN
                    || c.Id == C.BATTLE_OX || c.Id == C.NEO
                    || c.Id == C.WALL_OF_ILLUSION || c.Id == C.MAN_EATER_BUG
                    || c.Id == C.TRAP_MASTER);
                if (!MyMonsters().Any() && beaterInGy && !haveHandSummon)
                    return Activate(mr.Value);
                // Revive Man-Eater Bug vs 1700+ face-up
                if (InGrave(C.MAN_EATER_BUG) && OpMaxFaceUpAtk() >= 1700)
                    return Activate(mr.Value);
                // SS in hand, want to revive something stronger than opp
                if (InHand(C.SUMMONED_SKULL))
                {
                    int opBest = OpMonsters().Select(c => c.Attack).DefaultIfEmpty(0).Max();
                    int ourBest = BestHandAtk(C.SUMMONED_SKULL);
                    if (opBest > ourBest) return Activate(mr.Value);
                }
            }

            // De-Spell: only if opponent has SoRL
            int? de = FindActivable(C.DE_SPELL);
            if (de.HasValue && OpHasOnField(C.SWORDS))
                return Activate(de.Value);

            // Swords of Revealing Light: if opp has 2+ more monsters
            int opCount = OpMonsters().Count();
            int myCount = MyMonsters().Count();
            if (opCount - myCount >= 2)
            {
                int? sw = FindActivable(C.SWORDS);
                if (sw.HasValue) return Activate(sw.Value);
            }

            // Change of Heart (I12)
            int? coh = FindActivable(C.CHANGE_OF_HEART);
            if (coh.HasValue)
            {
                var action = TryCohDecision(coh.Value);
                if (action != null) return action;
            }

            // Fissure (I7)
            int? fs = FindActivable(C.FISSURE);
            if (fs.HasValue)
            {
                var action = TryFissureDecision(fs.Value);
                if (action != null) return action;
            }

            return null;
        }

        // I12: Change of Heart triggers
        private MainPhaseAction TryCohDecision(int cohIdx)
        {
            var opFaceUpAtk = OpMonsters()
                .Where(c => c.IsFaceup() && c.Position == (int)CardPosition.FaceUpAttack)
                .ToList();
            var opFaceDown = OpMonsters().Where(c => !c.IsFaceup()).ToList();
            int myTotal = MyMonsters()
                .Where(c => c.IsFaceup() && c.Position == (int)CardPosition.FaceUpAttack)
                .Sum(c => c.Attack);

            // (1) SS in hand + opp stronger than our non-SS best
            if (InHand(C.SUMMONED_SKULL))
            {
                int opBest = OpMonsters().Select(c => c.Attack).DefaultIfEmpty(0).Max();
                int ourBest = BestHandAtk(C.SUMMONED_SKULL);
                if (opBest > ourBest) return Activate(cohIdx);
            }

            // (2) Steal strongest → lethal math (no face-downs)
            if (opFaceUpAtk.Count > 0 && opFaceDown.Count == 0)
            {
                var stealTarget = opFaceUpAtk.OrderByDescending(c => c.Attack).First();
                if (myTotal + stealTarget.Attack >= Enemy.LifePoints)
                    return Activate(cohIdx);
            }

            // (3) Reach steal — opp has face-up ATK monster stronger than our best
            if (opFaceUpAtk.Count > 0)
            {
                int opMax = opFaceUpAtk.Max(c => c.Attack);
                int myMax = MyMonsters()
                    .Where(c => c.IsFaceup() && c.Position == (int)CardPosition.FaceUpAttack)
                    .Select(c => c.Attack).DefaultIfEmpty(0).Max();
                if (opMax > myMax && opMax >= 1500) return Activate(cohIdx);
            }

            // (4) Tribute Steal — SS in hand and opp has face-up
            if (InHand(C.SUMMONED_SKULL) && opFaceUpAtk.Count > 0)
                return Activate(cohIdx);

            // (5) v2 face-down-only trigger
            if (opFaceDown.Count == 1 && opFaceUpAtk.Count == 0
                && !OpMonsters().Any(c => c.IsFaceup() && c.Position == (int)CardPosition.FaceUpDefence)
                && myTotal >= Enemy.LifePoints)
                return Activate(cohIdx);

            return null;
        }

        // I7: Fissure on ≥1500 ATK opp that our beaters can't cleanly handle
        private MainPhaseAction TryFissureDecision(int fsIdx)
        {
            if (!OpMonsters().Any()) return null;

            // Priority kill targets
            var opMons = OpMonsters().ToList();
            var weakest = opMons.OrderBy(c => c.Attack).First();
            if (weakest.Id == C.LA_JINN || weakest.Id == C.WALL_OF_ILLUSION
                || weakest.Id == C.SUMMONED_SKULL)
                return Activate(fsIdx);

            // I7: strongest face-up opp ATK ≥ 1500 and ≥ our best
            var opFaceUpAtk = opMons
                .Where(c => c.IsFaceup() && c.Position == (int)CardPosition.FaceUpAttack)
                .ToList();
            if (opFaceUpAtk.Count > 0)
            {
                int opStrongest = opFaceUpAtk.Max(c => c.Attack);
                int myBest = MyMonsters()
                    .Select(c => c.Attack)
                    .Concat(Bot.Hand.Where(c => KnownAtk.ContainsKey(c.Id)).Select(c => KnownAtk[c.Id]))
                    .DefaultIfEmpty(0).Max();
                if (opStrongest >= 1500 && opStrongest >= myBest)
                    return Activate(fsIdx);
            }

            // If we have no monsters at all
            bool hasOwnMon = MyMonsters().Any()
                || Bot.Hand.Any(c => KnownAtk.ContainsKey(c.Id));
            if (!hasOwnMon) return Activate(fsIdx);

            return null;
        }

        // I10: repositioning with caution vs face-downs
        private MainPhaseAction TryReposV3()
        {
            if (Main?.ReposableCards == null) return null;
            bool opHasSS = OpHasOnField(C.SUMMONED_SKULL);
            bool opHasMon = OpMonsters().Any();
            bool opHasFaceDown = OpMonsters().Any(c => !c.IsFaceup());
            bool haveRemoval = HaveHardRemoval();

            for (int i = 0; i < Main.ReposableCards.Count; i++)
            {
                var card = Main.ReposableCards[i];
                bool inAtk = card.Position == (int)CardPosition.FaceUpAttack;

                if (OffensiveCards.Contains(card.Id))
                {
                    if (!inAtk) // want to flip to ATK
                    {
                        if (opHasSS) continue;
                        if (opHasFaceDown && !haveRemoval) continue;
                        return Repos(i);
                    }
                    if (inAtk && opHasSS) return Repos(i); // retreat to DEF
                }
                else if (DefensiveCards.Contains(card.Id))
                {
                    if (card.Id == C.MAN_EATER_BUG && !card.IsFaceup())
                    {
                        if (!opHasMon) continue;
                        return Repos(i);
                    }
                    if (!inAtk && !opHasMon) return Repos(i);
                    if (inAtk && opHasMon) return Repos(i);
                }
            }
            return null;
        }

        // I11: MP2 set Wall / Bug / Trap Master
        private MainPhaseAction TryMP2MSet()
        {
            int? wall = FindMSettable(C.WALL_OF_ILLUSION);
            if (wall.HasValue) return SetMonster(wall.Value);
            int? bug = FindMSettable(C.MAN_EATER_BUG);
            if (bug.HasValue) return SetMonster(bug.Value);
            int? tm = FindMSettable(C.TRAP_MASTER);
            if (tm.HasValue) return SetMonster(tm.Value);
            return null;
        }

        private MainPhaseAction TryPhaseChange()
        {
            if (Main?.CanBattlePhase == true)
                return new MainPhaseAction(MainPhaseAction.MainAction.ToBattlePhase);
            if (Main?.CanEndPhase == true)
                return new MainPhaseAction(MainPhaseAction.MainAction.ToEndPhase);
            return null;
        }

        // ── GenerateMainPhaseAction ────────────────────────────────────────────

        public override MainPhaseAction GenerateMainPhaseAction()
        {
            bool isMP1 = Duel.Phase == DuelPhase.Main1;

            if (isMP1)
            {
                return TryLethalSuperStep()
                    ?? TryTrapSet()
                    ?? TryDarkHoleRaigeki()
                    ?? TrySoulExchange()
                    ?? TrySummonV3()
                    ?? TrySpellV3()
                    ?? TryReposV3()
                    ?? TryPhaseChange()
                    ?? new MainPhaseAction(MainPhaseAction.MainAction.ToEndPhase);
            }
            else // MP2
            {
                return TryDarkHoleRaigeki()
                    ?? TrySpellV3()
                    ?? TryMP2MSet()
                    ?? TryPhaseChange()
                    ?? new MainPhaseAction(MainPhaseAction.MainAction.ToEndPhase);
            }
        }

        // ── GenerateBattlePhaseAction ──────────────────────────────────────────

        // Pick strongest/highest-priority attacker. Target selection happens in OnSelectCard.
        public override BattlePhaseAction GenerateBattlePhaseAction()
        {
            if (Battle == null || Battle.AttackableCards.Count == 0)
            {
                if (Battle?.CanMainPhaseTwo == true)
                    return new BattlePhaseAction(BattlePhaseAction.BattleAction.ToMainPhaseTwo);
                return new BattlePhaseAction(BattlePhaseAction.BattleAction.ToEndPhase);
            }

            // Pick best attacker: (ATK, kill-priority)
            int best = -1;
            int bestScore = -1;
            for (int i = 0; i < Battle.AttackableCards.Count; i++)
            {
                var c = Battle.AttackableCards[i];
                var _kpMap = new Dictionary<int, int>
                {
                    { C.SUMMONED_SKULL, 4 }, { C.LA_JINN, 3 },
                    { C.NEO, 2 }, { C.BATTLE_OX, 2 },
                };
                int kp = _kpMap.ContainsKey(c.Id) ? _kpMap[c.Id] : 1;
                int score = c.Attack * 10 + kp;
                if (score > bestScore) { bestScore = score; best = i; }
            }

            return new BattlePhaseAction(BattlePhaseAction.BattleAction.Attack,
                Battle.AttackableCards[best].ActionIndex);
        }

        // ── OnSelectChain ──────────────────────────────────────────────────────

        // I1, I2, I3: trap chain activation
        public override int OnSelectChain(IList<ClientCard> cards, bool forced)
        {
            // I1: Waboku
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i].Id != C.WABOKU) continue;
                bool inBattle = IsBattlePhase();
                int opStrongestAtk = OpMaxFaceUpAtk();
                bool weDie = MyMonsters()
                    .Any(c => c.IsFaceup() && c.Position == (int)CardPosition.FaceUpAttack
                           && c.Attack <= opStrongestAtk);
                int opTotalAtk = OpMonsters()
                    .Where(c => c.IsFaceup() && c.Position == (int)CardPosition.FaceUpAttack)
                    .Sum(c => c.Attack);
                bool emptyLethal = !MyMonsters().Any() && opTotalAtk >= Bot.LifePoints;
                if (inBattle && (weDie || emptyLethal)) return i;
            }

            // I2: Trap Hole — gate on target ATK ≥ 1500
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i].Id != C.TRAP_HOLE) continue;
                var candidates = OpMonsters()
                    .Where(c => c.IsFaceup() && c.Position == (int)CardPosition.FaceUpAttack && !c.Attacked)
                    .ToList();
                if (candidates.Count > 0)
                {
                    var target = candidates.OrderByDescending(c => c.Attack).First();
                    if (target.Attack >= 1500
                        || target.Id == C.SUMMONED_SKULL || target.Id == C.LA_JINN
                        || target.Id == C.BATTLE_OX || target.Id == C.NEO)
                        return i;
                    return -1; // cheap summon — save TH
                }
                return -1; // no clear target
            }

            // I3: Reinforcements — gate on +500 flipping a fight
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i].Id != C.REINFORCEMENTS) continue;
                var myAtkers = MyMonsters()
                    .Where(c => c.IsFaceup() && c.Position == (int)CardPosition.FaceUpAttack)
                    .ToList();
                var opAtks = OpMonsters()
                    .Where(c => c.IsFaceup() && c.Position == (int)CardPosition.FaceUpAttack)
                    .Select(c => c.Attack).ToList();
                bool useful = false;
                if (myAtkers.Count > 0 && opAtks.Count > 0)
                {
                    int bestMyAtk = myAtkers.Max(c => c.Attack);
                    foreach (int opAtk in opAtks)
                        if (bestMyAtk <= opAtk && bestMyAtk + 500 > opAtk) { useful = true; break; }
                }
                if (useful) return i;
                return -1;
            }

            return -1; // cancel chain by default
        }

        // ── OnSelectCard ───────────────────────────────────────────────────────

        // I4: target selection — weakest killable enemy in battle, strongest in MP
        public override IList<ClientCard> OnSelectCard(
            IList<ClientCard> cards, int min, int max, long hint, bool cancelable)
        {
            if (cards.Count == 0) return new List<ClientCard>();

            bool inBattle = IsBattlePhase();
            var enemyCards = cards.Where(c => c.Controller == 1).ToList();
            var myCards    = cards.Where(c => c.Controller == 0).ToList();

            ClientCard pick;

            if (enemyCards.Count > 0)
            {
                if (inBattle)
                {
                    // I4: pick weakest killable first; fallback strongest
                    var faceUp = enemyCards.Where(c => c.IsFaceup()).ToList();
                    var pool = faceUp.Count > 0 ? faceUp : enemyCards;
                    int myMaxAtk = MyMonsters()
                        .Where(c => c.IsFaceup() && c.Position == (int)CardPosition.FaceUpAttack && !c.Attacked)
                        .Select(c => c.Attack).DefaultIfEmpty(0).Max();
                    var killable = pool
                        .Where(c => c.Position == (int)CardPosition.FaceUpAttack && c.Attack < myMaxAtk)
                        .ToList();
                    if (killable.Count > 0)
                        pick = killable.OrderBy(c => c.Attack).First();
                    else
                        pick = pool
                            .OrderByDescending(c => c.Attack)
                            .ThenByDescending(c => (KillPriority.ContainsKey(c.Id) ? KillPriority[c.Id] : 0))
                            .First();
                }
                else
                {
                    // MP: CoH/Fissure/MR — pick strongest face-up enemy
                    var faceUp = enemyCards.Where(c => c.IsFaceup()).ToList();
                    var pool = faceUp.Count > 0 ? faceUp : enemyCards;
                    pick = pool
                        .OrderByDescending(c => c.Attack)
                        .ThenByDescending(c => (KillPriority.ContainsKey(c.Id) ? KillPriority[c.Id] : 0))
                        .First();
                }
            }
            else if (myCards.Count > 0)
            {
                // Tribute / Man-Eater target: pick our weakest
                pick = myCards.OrderBy(c => c.Attack).First();
            }
            else
            {
                pick = cards[0];
            }

            return new List<ClientCard> { pick };
        }

        // ── Trivial overrides ──────────────────────────────────────────────────

        // Yes/No: always yes (mirrors v2's _decide_yes_no)
        public override bool OnSelectYesNo(long desc) => true;
        public override bool OnSelectEffectYesNo(ClientCard card, long desc) => true;

        // Option: first option (mirrors v2's _decide_option)
        public override int OnSelectOption(IList<long> options) => 0;

        // Position: prefer face-up attack (mirrors v2's _decide_position)
        public override CardPosition OnSelectPosition(int cardId, IList<CardPosition> positions)
        {
            if (positions.Contains(CardPosition.FaceUpAttack))
                return CardPosition.FaceUpAttack;
            return positions[0];
        }

        // Go first (aggressive playstyle)
        public override bool OnSelectHand() => true;
    }
}
