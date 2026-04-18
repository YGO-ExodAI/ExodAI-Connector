using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using YGOSharp.Network.Enums;
using YGOSharp.OCGWrapper;
using YGOSharp.OCGWrapper.Enums;

namespace WindBot.Game.AI.Decks
{
    public class SelectChainData
    {
        public IList<ClientCard> Cards;
        public bool Forced;
    }

    public class SelectYesNoData
    {
        public long Desc;
    }

    public class SelectEffectYesNoData
    {
        public ClientCard Card;
        public long Desc;
    }

    public class SelectPositionData
    {
        public int CardId;
        public IList<CardPosition> Positions;
    }

    public class SelectCardData
    {
        public IList<ClientCard> Cards;
        public int Min;
        public int Max;
        public long Hint;
        public bool Cancelable;
    }

    public class SelectOptionData
    {
        public IList<long> Options;
    }

    public class BaseCardData
    {
        [JsonPropertyName("cleanName")] public string CleanName { get; set; }
        [JsonPropertyName("konamiCode")] public string KonamiCode { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("icon")] public string? Icon { get; set; }
        [JsonPropertyName("normal")] public bool Normal { get; set; }
        [JsonPropertyName("effect")] public bool Effect { get; set; }
        [JsonPropertyName("fusion")] public bool Fusion { get; set; }
        [JsonPropertyName("ritual")] public bool Ritual { get; set; }
        [JsonPropertyName("synchro")] public bool Synchro { get; set; }
        [JsonPropertyName("xyz")] public bool Xyz { get; set; }
        [JsonPropertyName("pendulum")] public bool Pendulum { get; set; }
        [JsonPropertyName("link")] public bool Link { get; set; }
        [JsonPropertyName("flip")] public bool Flip { get; set; }
        [JsonPropertyName("gemini")] public bool Gemini { get; set; }
        [JsonPropertyName("spirit")] public bool Spirit { get; set; }
        [JsonPropertyName("toon")] public bool Toon { get; set; }
        [JsonPropertyName("tuner")] public bool Tuner { get; set; }
        [JsonPropertyName("union")] public bool Union { get; set; }
        [JsonPropertyName("attribute")] public string Attribute { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("level")] public int? Level { get; set; }
        [JsonPropertyName("rating")] public int? Rating { get; set; }
        [JsonPropertyName("arrows")] public object? Arrows { get; set; }
        [JsonPropertyName("scale")] public int? Scale { get; set; }
        [JsonPropertyName("atk")] public int? Atk { get; set; }
        [JsonPropertyName("def")] public int? Def { get; set; }
        [JsonPropertyName("pendulumEffect")] public string? PendulumEffect { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("extraDeck")] public bool ExtraDeck { get; set; }
    }

    public class BaseCardDataDTO
    {
        public string CleanName { get; set; }
        public string KonamiCode { get; set; }
        public int[] Category { get; set; }
        public bool Normal { get; set; }
        public bool Effect { get; set; }
        public bool Flip { get; set; }
        public string Attribute { get; set; }
        public string Type { get; set; }
        public int[] Level { get; set; }
        public float? Atk { get; set; }
        public float? Def { get; set; }
        public string? Description { get; set; }

        public BaseCardDataDTO(BaseCardData baseCardData)
        {
            CleanName = baseCardData.CleanName;
            KonamiCode = baseCardData.KonamiCode;
            Category = OneHotCategory(baseCardData.Category);
            Normal = baseCardData.Normal;
            Effect = baseCardData.Effect;
            Flip = baseCardData.Flip;
            Attribute = baseCardData.Attribute;
            Type = baseCardData.Type;
            Level = OneHotEncodeLevel(baseCardData.Level.Value - 1, 12);
            Atk = baseCardData.Atk / 8000;
            Def = baseCardData.Def / 8000;
            Description = baseCardData.Description;
        }

        private int[] OneHotEncodeLevel(int ind, int length)
        {
            if (ind < 0 || ind >= length)
                throw new ArgumentOutOfRangeException(nameof(ind));
            int[] oneHot = new int[length];
            oneHot[ind] = 1;
            return oneHot;
        }

        private int[] OneHotCategory(string? category)
        {
            return category?.ToLower() switch
            {
                "Monster" => new[] { 1, 0, 0 },
                "Spell" => new[] { 0, 1, 0 },
                "Trap" => new[] { 0, 0, 1 },
                _ => new[] { 0, 0, 0 }
            };
        }
    }

    [Deck("ExodAI", "AI_Yugi_Kaiba_Beat", "Easy")]
    public class ExodAIExecutor : Executor
    {
        private int[] gameState;
        public List<int> masterDecklist;
        public List<int> currentDecklist;
        public Dictionary<int, int> cardsActivatedThisTurn;
        public Dictionary<int, BaseCardData> baseCardData;
        private bool pendingCardDraw;
        private int cardsDrawn;
        private Guid matchId;
        private int playerSlot;
        private int stepCount;
        // Re-entrancy depth for decision overrides. When OnSelectCard's
        // internal loop calls OnSelectYesNo, we still want only the
        // OnSelectCard call marked top-level (it corresponds to the single
        // MSG_SELECT_CARD packet in the replay stream).
        private int _decisionDepth;
        private List<Dictionary<string, object>> _eventBuffer;
        private const int MAX_EVENT_BUFFER = 64;
        private const string MODEL_TAG = "ExodAI_Condensed";

        // Rich metadata from the most recent /predict response. Populated by
        // GetInferenceResult and consumed by SendTopLevelThought so the replay
        // viewer can show confidence, value, and the top-K alternatives.
        private InferenceMeta _lastInferenceMeta = new InferenceMeta();

        private class InferenceMeta
        {
            public float Confidence { get; set; }
            public float Value { get; set; }
            // P(main player wins | current state), 0..1. Null when the
            // deployed checkpoint predates the win-prob head.
            public float? WinProb { get; set; }
            public int ValidCount { get; set; }
            public int DecisionType { get; set; }
            // Each entry: {"action": "n2", "prob": 0.45}. Ordered high→low.
            public List<TopAction> TopActions { get; set; } = new List<TopAction>();
        }

        private class TopAction
        {
            [JsonPropertyName("action")] public string Action { get; set; }
            [JsonPropertyName("prob")] public float Prob { get; set; }
        }

        private class InferenceResponse
        {
            [JsonPropertyName("move")] public string Move { get; set; }
            [JsonPropertyName("confidence")] public float Confidence { get; set; }
            [JsonPropertyName("value")] public float Value { get; set; }
            [JsonPropertyName("win_prob")] public float? WinProb { get; set; }
            [JsonPropertyName("valid_count")] public int ValidCount { get; set; }
            [JsonPropertyName("decision_type")] public int DecisionType { get; set; }
            [JsonPropertyName("top_actions")] public List<TopAction> TopActions { get; set; }
        }

        private static readonly string _invalidActionDumpDir =
            Environment.GetEnvironmentVariable("EXODAI_INVALID_DUMP_DIR")
            ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "invalid_actions");

        private const int MAX_RETRIES = 15;

        private static readonly HttpClient _httpClient = new HttpClient(
            new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip }
        )
        { Timeout = TimeSpan.FromSeconds(60) };

        private static readonly string _rlServerUrl =
            Environment.GetEnvironmentVariable("EXODAI_RL_SERVER") ?? "http://localhost:8000";
        private static readonly string _machineId =
            Environment.GetEnvironmentVariable("EXODAI_MACHINE_ID") ?? Environment.MachineName;

        // ExodAI eval harness overrides. When EXODAI_EVAL_HAND is set to 1/2/3,
        // OnRockPaperScissors returns that value verbatim so the two bots can
        // be pinned to a known outcome (e.g. A=rock, B=paper → B wins every
        // time). When EXODAI_EVAL_GO_FIRST is set to 0/1, OnSelectHand returns
        // that value so the RPS winner's go-first choice is also deterministic.
        // Unset → default random behavior from the base Executor.
        private static readonly int? _evalHand = ParseOptionalInt("EXODAI_EVAL_HAND");
        private static readonly bool? _evalGoFirst = ParseOptionalBool("EXODAI_EVAL_GO_FIRST");

        private static int? ParseOptionalInt(string name)
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(v)) return null;
            return int.TryParse(v, out int r) ? r : (int?)null;
        }

        private static bool? ParseOptionalBool(string name)
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(v)) return null;
            if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return null;
        }

        public override int OnRockPaperScissors()
        {
            if (_evalHand.HasValue && _evalHand.Value >= 1 && _evalHand.Value <= 3)
            {
                Console.WriteLine($"[ExodAI][eval] OnRockPaperScissors pinned to {_evalHand.Value}");
                return _evalHand.Value;
            }
            return base.OnRockPaperScissors();
        }

        public override bool OnSelectHand()
        {
            if (_evalGoFirst.HasValue)
            {
                Console.WriteLine($"[ExodAI][eval] OnSelectHand pinned to {_evalGoFirst.Value}");
                return _evalGoFirst.Value;
            }
            return base.OnSelectHand();
        }

        public ExodAIExecutor(GameAI ai, Duel duel) : base(ai, duel)
        {
            Console.WriteLine("ExodAIExecutor");
            gameState = new int[500];
            _eventBuffer = new List<Dictionary<string, object>>();
            var deck = WindBot.Game.Deck.Load(GetDeckName());
            masterDecklist = deck.Cards.ToList();
            currentDecklist = deck.Cards.ToList();
            LoadBaseCardData();
            try { Directory.CreateDirectory(_invalidActionDumpDir); }
            catch { /* best effort */ }
        }

        // ════════════════════════════════════════════════════════
        // Error reporting infrastructure
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// Dump game state + metadata to a JSON file when an invalid action occurs.
        /// Only called once per decision (the first failure), not on every retry.
        /// </summary>
        private void DumpInvalidAction(string decisionType, string gameStateJson, string modelResponse,
            Dictionary<char, int> validGroups, string errorMessage)
        {
            try
            {
                string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
                string filename = $"invalid_{decisionType}_{timestamp}.json";
                string filepath = Path.Combine(_invalidActionDumpDir, filename);
                var dump = new
                {
                    Timestamp = DateTime.UtcNow,
                    MatchId = matchId,
                    Step = stepCount,
                    DecisionType = decisionType,
                    ModelResponse = modelResponse,
                    ErrorMessage = errorMessage,
                    ValidGroups = validGroups.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                    GameState = gameStateJson
                };
                File.WriteAllText(filepath, JsonConvert.SerializeObject(dump, Formatting.Indented));
                Console.WriteLine($"[InvalidDump] Saved to {filepath}");
            }
            catch (Exception ex) { Console.WriteLine($"[InvalidDump] Failed to save: {ex.Message}"); }
        }

        /// <summary>
        /// Report an error to the RL server's /errorReport endpoint so it appears on the dashboard.
        /// Fire-and-forget: errors in reporting are logged but don't affect gameplay.
        /// </summary>
        private void ReportErrorToServer(string errorType, string decisionType, string modelResponse,
            Dictionary<char, int> validGroups, string message, string fallbackAction = null)
        {
            try
            {
                var payload = new
                {
                    error_type = errorType,
                    machine_id = _machineId,
                    match_id = matchId,
                    step = stepCount,
                    decision_type = decisionType,
                    model_response = modelResponse,
                    valid_groups = validGroups?.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                    fallback_action = fallbackAction,
                    message = message,
                    timestamp_utc = DateTime.UtcNow
                };
                string json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                // Fire-and-forget: don't block gameplay waiting for the response
                _httpClient.PostAsync($"{_rlServerUrl}/errorReport", content);
            }
            catch (Exception ex) { Console.WriteLine($"[ErrorReport] Failed to send: {ex.Message}"); }
        }

        /// <summary>
        /// Handle the case where MAX_RETRIES is exceeded: pick a random valid action,
        /// report the error to the server, and return the fallback action string.
        /// </summary>
        private string HandleRetryLimitExceeded(string decisionType, string lastResponse,
            Dictionary<char, int> validGroups, string errorContext)
        {
            string fallback = PickRandomValidAction(validGroups, decisionType);

            string message = $"Retry limit ({MAX_RETRIES}) exceeded. " +
                             $"Last response: '{lastResponse}'. Fallback: '{fallback}'. {errorContext}";
            Console.WriteLine($"[RETRY_LIMIT] {decisionType}: {message}");

            // Report to server dashboard
            ReportErrorToServer("retry_limit_exceeded", decisionType, lastResponse,
                validGroups, message, fallback);

            return fallback;
        }

        private string PickRandomValidAction(Dictionary<char, int> validGroups, string decisionType)
        {
            var rng = new Random();
            var candidates = new List<string>();
            foreach (var kv in validGroups)
            {
                char g = kv.Key; int count = kv.Value;
                if (g == 'p')
                {
                    if (decisionType == "BattlePhase")
                    {
                        if (Battle != null && Battle.CanMainPhaseTwo) candidates.Add("p2");
                        if (Battle != null && Battle.CanEndPhase) candidates.Add("p3");
                    }
                    else
                    {
                        if (Main != null && Main.CanBattlePhase) candidates.Add("p1");
                        if (Main != null && Main.CanEndPhase) candidates.Add("p3");
                    }
                }
                else { for (int i = 0; i < count; i++) candidates.Add($"{g}{i}"); }
            }
            if (candidates.Count == 0) return "p3";
            return candidates[rng.Next(candidates.Count)];
        }

        // ════════════════════════════════════════════════════════
        // Data loading + game lifecycle
        // ════════════════════════════════════════════════════════

        private void LoadBaseCardData()
        {
            baseCardData = new Dictionary<int, BaseCardData>();
            string filePath = Environment.GetEnvironmentVariable("EXODAI_CARD_METADATA")
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "card_metadata.json");
            try
            {
                foreach (string line in File.ReadLines(filePath))
                {
                    string validJson = line.Replace("NaN", "null").Replace("None", "null")
                                           .Replace("True", "true").Replace("False", "false");
                    var cardData = JsonConvert.DeserializeObject<BaseCardData>(validJson);
                    if (cardData != null)
                    {
                        int konamiCode = int.Parse(cardData.KonamiCode);
                        if (cardData.Normal) cardData.Description = string.Empty;
                        cardData.Description = cardData.CleanName
                            + (cardData.Description != string.Empty ? "|||" + cardData.Description : string.Empty)
                            + (cardData.PendulumEffect != null ? "|||" + cardData.PendulumEffect : string.Empty);
                        baseCardData.Add(konamiCode, cardData);
                    }
                }
            }
            catch (Exception e) { Console.WriteLine("Error reading card metadata file: " + e.Message); }
            Console.WriteLine("Loaded " + baseCardData.Count + " base card data entries");
        }

        public override void OnStartDuel()
        {
            currentDecklist = masterDecklist.ToList();
            matchId = Guid.NewGuid();
            playerSlot = Duel.IsFirst ? 0 : 1;
            stepCount = 0;
            _decisionDepth = 0;
            _eventBuffer.Clear();
        }

        // Send a MSG_AI_THOUGHT payload alongside the CTOS.Response for the
        // current top-level decision. No-op if we're nested inside another
        // override (OnSelectYesNo from OnSelectCard's loop) or if the network
        // channel isn't wired up. Called once per override, right after the
        // final move token is accepted.
        //
        // `describe` translates an action code (e.g. "n2") into human-readable
        // Yu-Gi-Oh! terminology ("Normal Summon Blue-Eyes White Dragon") using
        // the current decision's card context.
        private void SendTopLevelThought(string decisionType, string move, Func<string, string> describe)
        {
            if (_decisionDepth != 1) return;
            if (SendCtosAiThought == null) return;
            try
            {
                describe ??= (code => code);
                var meta = _lastInferenceMeta ?? new InferenceMeta();
                var topActions = (meta.TopActions ?? new List<TopAction>())
                    .Select(t => new
                    {
                        action = t.Action,
                        prob = t.Prob,
                        description = SafeDescribe(describe, t.Action),
                    })
                    .ToArray();
                var payload = new
                {
                    turn = Duel.Turn,
                    decision_type = decisionType,
                    move,
                    move_description = SafeDescribe(describe, move),
                    confidence = meta.Confidence,
                    value = meta.Value,
                    // Clean 0..1 "how confident is the model that it wins
                    // from here?" reading. Distinct from `value` (shaped).
                    // null when an older checkpoint without the head is loaded.
                    win_prob = meta.WinProb,
                    valid_count = meta.ValidCount,
                    top_actions = topActions,
                    phase = PhaseToString(Duel.Phase),
                    // Canonical seat index from the server's perspective. The
                    // replay viewer uses this to filter thoughts by POV.
                    player = playerSlot,
                    match_id = matchId,
                    step = stepCount,
                };
                string json = JsonConvert.SerializeObject(payload);
                SendCtosAiThought(Encoding.UTF8.GetBytes(json));
            }
            catch (Exception ex) { Console.WriteLine("[AiThought] send failed: " + ex.Message); }
        }

        private static string SafeDescribe(Func<string, string> describe, string code)
        {
            if (string.IsNullOrEmpty(code)) return code;
            try { return describe(code) ?? code; }
            catch { return code; }
        }

        // ════════════════════════════════════════════════════════
        // Action-code → human-readable text. Each decision type has its own
        // context (Main/Battle/cards list/positions list), so we build a
        // closure per override and hand it to SendTopLevelThought.
        // ════════════════════════════════════════════════════════

        private static string CardNameOr(ClientCard c, string fallback = "card")
            => c?.Name ?? fallback;

        private static string PhaseDescription(int index, bool fromBattle)
        {
            switch (index)
            {
                case 1: return "Go to Battle Phase";
                case 2: return "Go to Main Phase 2";
                case 3: return "Go to End Phase";
                default: return fromBattle ? "End Battle" : "End Phase";
            }
        }

        private string DescribeMainAction(string code)
        {
            if (!TrySplitCode(code, out char g, out int i)) return code;
            switch (g)
            {
                case 'n':
                    return $"Normal Summon {CardNameOr(SafeGet(Main?.SummonableCards, i))}";
                case 'd':
                    return $"Set {CardNameOr(SafeGet(Main?.MonsterSetableCards, i))} face-down";
                case 'x':
                    return $"Special Summon {CardNameOr(SafeGet(Main?.SpecialSummonableCards, i))}";
                case 'a':
                    return $"Activate {CardNameOr(SafeGet(Main?.ActivableCards, i))}";
                case 's':
                    return $"Set {CardNameOr(SafeGet(Main?.SpellSetableCards, i))} (S/T)";
                case 'r':
                    return $"Change position of {CardNameOr(SafeGet(Main?.ReposableCards, i))}";
                case 'p':
                    return PhaseDescription(i, fromBattle: false);
                default:
                    return code;
            }
        }

        private string DescribeBattleAction(string code)
        {
            if (!TrySplitCode(code, out char g, out int i)) return code;
            switch (g)
            {
                case 'b':
                    return $"Attack with {CardNameOr(SafeGet(Battle?.AttackableCards, i))}";
                case 'a':
                    return $"Activate {CardNameOr(SafeGet(Battle?.ActivableCards, i))}";
                case 'p':
                    return PhaseDescription(i, fromBattle: true);
                default:
                    return code;
            }
        }

        private static string DescribeSelectCardAction(string code, IList<ClientCard> cards)
        {
            if (!TrySplitCode(code, out char g, out int i)) return code;
            if (g != 'i') return code;
            var card = SafeGet(cards, i);
            return card == null ? $"Select index {i}" : $"Select {card.Name}";
        }

        private static string DescribeChainAction(string code, IList<ClientCard> cards)
        {
            if (!TrySplitCode(code, out char g, out int i)) return code;
            if (g != 'i') return code;
            if (i == -1) return "Don't chain";
            var card = SafeGet(cards, i);
            return card == null ? $"Chain index {i}" : $"Chain {card.Name}";
        }

        private static string DescribeYesNoAction(string code)
        {
            if (!TrySplitCode(code, out char g, out int i)) return code;
            if (g != 'i') return code;
            return i == 1 ? "Yes" : (i == 0 ? "No" : code);
        }

        private static string DescribeOptionAction(string code, IList<long> options)
        {
            if (!TrySplitCode(code, out char g, out int i)) return code;
            if (g != 'i') return code;
            return $"Select option #{i + 1}";
        }

        private static string DescribePositionAction(string code, int cardId, IList<CardPosition> positions)
        {
            if (!TrySplitCode(code, out char g, out int i)) return code;
            if (g != 'i') return code;
            if (positions == null || i < 0 || i >= positions.Count)
                return $"Select position #{i}";
            string label = positions[i] switch
            {
                CardPosition.FaceUpAttack => "Face-up Attack",
                CardPosition.FaceDownAttack => "Face-down Attack",
                CardPosition.FaceUpDefence => "Face-up Defense",
                CardPosition.FaceDownDefence => "Face-down Defense",
                _ => $"position #{i}",
            };
            var name = NamedCard.Get(cardId)?.Name;
            return name == null ? $"Set {label}" : $"Set {name} in {label}";
        }

        private static bool TrySplitCode(string code, out char group, out int index)
        {
            group = ' '; index = -1;
            if (string.IsNullOrEmpty(code) || code.Length < 2) return false;
            group = code[0];
            return int.TryParse(code.Substring(1), out index);
        }

        private static T SafeGet<T>(IList<T> list, int i) where T : class
            => (list != null && i >= 0 && i < list.Count) ? list[i] : null;

        public override void OnGameEnd(string textResult, bool gameError = false)
        {
            textResult = textResult.ToLower();
            int reward = textResult == "win" ? 1 : textResult == "lose" ? -1 : 0;
            int? winnerSlot = null;
            if (textResult == "win") winnerSlot = playerSlot;
            else if (textResult == "lose") winnerSlot = 1 - playerSlot;
            var payload = new
            {
                match_id = matchId,
                machine_id = _machineId,
                player_slot = playerSlot,
                went_first = Duel.IsFirst,
                our_result = textResult,
                winner_slot = winnerSlot,
                reward,
                turns = Duel.Turn,
                bot_lp_final = Util.Bot.LifePoints,
                enemy_lp_final = Util.Enemy.LifePoints,
                timestamp_utc = DateTime.UtcNow,
                model = new { tag = MODEL_TAG },
                Error = gameError
            };
            string json = JsonConvert.SerializeObject(payload, Formatting.Indented,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            Console.WriteLine(json);
            try
            {
                var content = CreateGzipContent(json);
                var response = _httpClient.PostAsync($"{_rlServerUrl}/gameResult", content).Result;
            }
            catch (Exception ex) { Console.WriteLine("Failed to POST gameResult: " + ex.Message); }
        }

        public override void OnNewTurn()
        {
            Console.WriteLine($"[trace] OnNewTurn t={Duel.Turn} self={(Duel.Player == 0 ? "bot" : "enemy")}");
            cardsActivatedThisTurn = new Dictionary<int, int>();
            RecordEvent("turn_change", Duel.Turn, 0, "none", "none");
        }

        public override void OnNewPhase()
        {
            Console.WriteLine($"[trace] OnNewPhase phase={PhaseToString(Duel.Phase)}");
            RecordEvent("phase_change", -1, 0, "none", PhaseToString(Duel.Phase));
        }

        public override void OnDraw(int player, int count)
        {
            Console.WriteLine($"[trace] OnDraw player={player} count={count}");
            for (int i = 0; i < count; i++) RecordEvent("draw", 0, player, "deck", "hand");
            if (player != 0) return;
            pendingCardDraw = true;
            cardsDrawn = count;
        }

        public override void OnUpdateData(int player, CardLocation location)
        {
            if (player != 0) return;
            if (pendingCardDraw)
            {
                var drawnCards = Bot.Hand.Skip(Bot.Hand.Count - cardsDrawn).ToList();
                foreach (var card in drawnCards) currentDecklist.Remove(card.Id);
                pendingCardDraw = false;
            }
        }

        public override void OnMoveCard(CardLocation source, int sourceController, CardLocation dest, int destController, int reason, int cardId)
        {
            if (source == dest) return;
            RecordEvent("card_moved", cardId, sourceController, LocationToString(source), LocationToString(dest));
            if (sourceController == 0 && source == CardLocation.Deck) currentDecklist.Remove(cardId);
            else if (destController == 0 && dest == CardLocation.Deck) currentDecklist.Add(cardId);
        }

        public override void OnChaining(int player, ClientCard card)
        {
            int cardId = (card != null) ? card.Id : 0;
            string loc = (card != null) ? LocationToString(card.Location) : "unknown";
            RecordEvent("chain", cardId, player, loc, "none");
        }

        public override void OnActivateCard(Dictionary<int, int> activatedCards)
        {
            cardsActivatedThisTurn = activatedCards;
            foreach (var kvp in activatedCards) RecordEvent("activate", kvp.Key, 0, "none", "none");
        }

        private void RecordEvent(string eventType, int cardId, int player, string fromLocation, string toLocation)
        {
            _eventBuffer.Add(new Dictionary<string, object>
            {
                { "type", eventType }, { "card_id", cardId }, { "player", player },
                { "from", fromLocation }, { "to", toLocation }
            });
            while (_eventBuffer.Count > MAX_EVENT_BUFFER) _eventBuffer.RemoveAt(0);
        }

        private static string LocationToString(CardLocation location)
        {
            if ((location & CardLocation.Deck) != 0) return "deck";
            if ((location & CardLocation.Hand) != 0) return "hand";
            if ((location & CardLocation.MonsterZone) != 0) return "monster";
            if ((location & CardLocation.SpellZone) != 0) return "spell";
            if ((location & CardLocation.Grave) != 0) return "grave";
            if ((location & CardLocation.Removed) != 0) return "banished";
            if ((location & CardLocation.Extra) != 0) return "extra";
            if ((location & CardLocation.Overlay) != 0) return "overlay";
            return "unknown";
        }

        private static string PhaseToString(DuelPhase phase)
        {
            return phase switch
            {
                DuelPhase.Draw => "draw",
                DuelPhase.Standby => "standby",
                DuelPhase.Main1 => "main1",
                DuelPhase.BattleStart => "battle_start",
                DuelPhase.BattleStep => "battle_step",
                DuelPhase.Damage => "damage",
                DuelPhase.DamageCal => "damage_cal",
                DuelPhase.Battle => "battle",
                DuelPhase.Main2 => "main2",
                DuelPhase.End => "end",
                _ => "unknown"
            };
        }

        // ════════════════════════════════════════════════════════
        // Action methods with dump-on-failure + retry limit + server error reporting
        // ════════════════════════════════════════════════════════

        public override MainPhaseAction GenerateMainPhaseAction()
        {
            _decisionDepth++;
            try {
            Console.WriteLine("[trace] GenerateMainPhaseAction entry");
            var gamestate = GetGameState(GameMessage.SelectIdleCmd);
            char group; int index; string response = "";
            int retries = 0; bool dumped = false;
            while (true)
            {
                try
                {
                    response = GetInferenceResult(gamestate);
                    var validGroups = new Dictionary<char, int>
                    {
                        { 'n', Main.SummonableCards.Count }, { 'd', Main.MonsterSetableCards.Count },
                        { 'x', Main.SpecialSummonableCards.Count }, { 'a', Main.ActivableCards.Count },
                        { 's', Main.SpellSetableCards.Count }, { 'r', Main.ReposableCards.Count }, { 'p', 3 }
                    };
                    if (!TryParseAction(response, validGroups, out group, out index))
                    {
                        string err = $"Failed to parse '{response}' against valid groups: {string.Join(", ", validGroups.Select(kv => $"{kv.Key}:{kv.Value}"))}";
                        if (!dumped) { DumpInvalidAction("MainPhase", gamestate, response, validGroups, err); dumped = true; }
                        if (++retries >= MAX_RETRIES) { response = HandleRetryLimitExceeded("MainPhase", response, validGroups, err); TryParseAction(response, validGroups, out group, out index); break; }
                        throw new FormatException(err);
                    }
                    if (group == 'p' && index != 1 && index != 3)
                    {
                        string err = $"Phase index {index} invalid, must be 1 (battle) or 3 (end).";
                        if (!dumped) { DumpInvalidAction("MainPhase", gamestate, response, validGroups, err); dumped = true; }
                        if (++retries >= MAX_RETRIES) { response = HandleRetryLimitExceeded("MainPhase", response, validGroups, err); TryParseAction(response, validGroups, out group, out index); break; }
                        throw new FormatException(err);
                    }
                    break;
                }
                catch (FormatException ex) { Console.WriteLine($"[MainPhase] Invalid action: '{response}' -- {ex.Message}"); }
            }
            SendTopLevelThought("main", response, DescribeMainAction);
            switch (group)
            {
                case 'n': return new MainPhaseAction(MainPhaseAction.MainAction.Summon, Main.SummonableCards[index].ActionIndex);
                case 'd': return new MainPhaseAction(MainPhaseAction.MainAction.SetMonster, Main.MonsterSetableCards[index].ActionIndex);
                case 'x': return new MainPhaseAction(MainPhaseAction.MainAction.SpSummon, Main.SpecialSummonableCards[index].ActionIndex);
                case 'a': return new MainPhaseAction(MainPhaseAction.MainAction.Activate, Main.ActivableCards[index].ActionActivateIndex[Main.ActivableDescs[index]]);
                case 's': return new MainPhaseAction(MainPhaseAction.MainAction.SetSpell, Main.SpellSetableCards[index].ActionIndex);
                case 'r': return new MainPhaseAction(MainPhaseAction.MainAction.Repos, Main.ReposableCards[index].ActionIndex);
                case 'p': return GenerateMainPhaseChangeAction(index);
                default: return new MainPhaseAction(MainPhaseAction.MainAction.ToEndPhase);
            }
            } finally { _decisionDepth--; }
        }

        public override BattlePhaseAction GenerateBattlePhaseAction()
        {
            _decisionDepth++;
            try {
            var gamestate = GetGameState(GameMessage.SelectBattleCmd);
            char group; int index; string response = "";
            int retries = 0; bool dumped = false;
            var validGroups = new Dictionary<char, int>
            {
                { 'b', Battle.AttackableCards.Count }, { 'a', Battle.ActivableCards.Count }, { 'p', 3 }
            };
            while (true)
            {
                try
                {
                    response = GetInferenceResult(gamestate);
                    if (!TryParseAction(response, validGroups, out group, out index))
                    {
                        string err = $"Failed to parse '{response}' against valid groups: {string.Join(", ", validGroups.Select(kv => $"{kv.Key}:{kv.Value}"))}";
                        if (!dumped) { DumpInvalidAction("BattlePhase", gamestate, response, validGroups, err); dumped = true; }
                        if (++retries >= MAX_RETRIES) { response = HandleRetryLimitExceeded("BattlePhase", response, validGroups, err); TryParseAction(response, validGroups, out group, out index); break; }
                        throw new FormatException(err);
                    }
                    if (group == 'p' && (index < 2 || index > 3))
                    {
                        string err = $"Phase index {index} invalid, must be 2 (MP2) or 3 (end).";
                        if (!dumped) { DumpInvalidAction("BattlePhase", gamestate, response, validGroups, err); dumped = true; }
                        if (++retries >= MAX_RETRIES) { response = HandleRetryLimitExceeded("BattlePhase", response, validGroups, err); TryParseAction(response, validGroups, out group, out index); break; }
                        throw new FormatException(err);
                    }
                    break;
                }
                catch (FormatException ex) { Console.WriteLine($"[BattlePhase] Invalid action: '{response}' -- {ex.Message}"); }
            }
            SendTopLevelThought("battle", response, DescribeBattleAction);
            switch (group)
            {
                case 'b': return new BattlePhaseAction(BattlePhaseAction.BattleAction.Attack, Battle.AttackableCards[index].ActionIndex);
                case 'a': return new BattlePhaseAction(BattlePhaseAction.BattleAction.Activate, Battle.ActivableCards[index].ActionIndex);
                case 'p': return GenerateBattlePhaseChangeAction(index);
                default: return new BattlePhaseAction(BattlePhaseAction.BattleAction.ToEndPhase);
            }
            } finally { _decisionDepth--; }
        }

        private MainPhaseAction GenerateMainPhaseChangeAction(int index)
        {
            if (index == 1 && Main.CanBattlePhase) return new MainPhaseAction(MainPhaseAction.MainAction.ToBattlePhase);
            else if (index == 3 && Main.CanEndPhase) return new MainPhaseAction(MainPhaseAction.MainAction.ToEndPhase);
            else { Console.WriteLine($"[MainPhaseChange] Cannot move to phase index {index}."); return GenerateMainPhaseAction(); }
        }

        private BattlePhaseAction GenerateBattlePhaseChangeAction(int index)
        {
            if (index == 2 && Battle.CanMainPhaseTwo) return new BattlePhaseAction(BattlePhaseAction.BattleAction.ToMainPhaseTwo);
            else if (index == 3 && Battle.CanEndPhase) return new BattlePhaseAction(BattlePhaseAction.BattleAction.ToEndPhase);
            else { Console.WriteLine($"[BattlePhaseChange] Cannot move to phase index {index}."); return GenerateBattlePhaseAction(); }
        }

        public override IList<ClientCard> OnSelectCard(IList<ClientCard> cards, int min, int max, long hint, bool cancelable)
        {
            _decisionDepth++;
            try {
            var selectCardData = new SelectCardData { Cards = cards, Min = min, Max = max, Hint = hint, Cancelable = cancelable };
            var gamestate = GetGameState(GameMessage.SelectCard, cardData: selectCardData);
            var validGroups = new Dictionary<char, int> { { 'i', cards.Count } };
            var selectedCards = new List<ClientCard>();
            var chosenIndices = new HashSet<int>();
            while (selectedCards.Count < min)
            {
                int idx = RequestUniqueIndexFromBot("SelectCard", gamestate, validGroups, cards.Count, chosenIndices);
                chosenIndices.Add(idx); selectedCards.Add(cards[idx]);
            }
            while (selectedCards.Count < max && OnSelectYesNo(hint))
            {
                int idx = RequestUniqueIndexFromBot("SelectCard", gamestate, validGroups, cards.Count, chosenIndices);
                chosenIndices.Add(idx); selectedCards.Add(cards[idx]);
            }
            SendTopLevelThought("select_card",
                string.Join(",", chosenIndices.Select(i => "i" + i)),
                code => DescribeSelectCardAction(code, cards));
            return selectedCards;
            } finally { _decisionDepth--; }
        }

        private int RequestUniqueIndexFromBot(string decisionType, string gamestate, Dictionary<char, int> validGroups, int totalCards, HashSet<int> alreadyChosen)
        {
            string response = ""; int retries = 0; bool dumped = false;
            while (true)
            {
                try
                {
                    response = GetInferenceResult(gamestate);
                    if (!TryParseAction(response, validGroups, out _, out int index))
                    {
                        string err = $"Failed to parse '{response}'.";
                        if (!dumped) { DumpInvalidAction(decisionType, gamestate, response, validGroups, err); dumped = true; }
                        if (++retries >= MAX_RETRIES) { HandleRetryLimitExceeded(decisionType, response, validGroups, err); return Enumerable.Range(0, totalCards).FirstOrDefault(i => !alreadyChosen.Contains(i)); }
                        throw new FormatException(err);
                    }
                    if (index < 0 || index >= totalCards)
                    {
                        string err = $"Index {index} out of range [0, {totalCards - 1}].";
                        if (!dumped) { DumpInvalidAction(decisionType, gamestate, response, validGroups, err); dumped = true; }
                        if (++retries >= MAX_RETRIES) { HandleRetryLimitExceeded(decisionType, response, validGroups, err); return Enumerable.Range(0, totalCards).FirstOrDefault(i => !alreadyChosen.Contains(i)); }
                        throw new FormatException(err);
                    }
                    if (alreadyChosen.Contains(index))
                    {
                        if (++retries >= MAX_RETRIES) { HandleRetryLimitExceeded(decisionType, response, validGroups, $"Duplicate index {index}"); return Enumerable.Range(0, totalCards).First(i => !alreadyChosen.Contains(i)); }
                        throw new FormatException($"Duplicate index {index}.");
                    }
                    return index;
                }
                catch (FormatException ex) { Console.WriteLine($"[{decisionType}] Invalid action: '{response}' -- {ex.Message}"); }
            }
        }

        public override bool OnSelectYesNo(long desc)
        {
            _decisionDepth++;
            try {
            var gamestate = GetGameState(GameMessage.SelectYesNo, yesNoData: new SelectYesNoData { Desc = desc });
            int index; string response = ""; int retries = 0; bool dumped = false;
            var validGroups = new Dictionary<char, int> { { 'i', 2 } };
            while (true)
            {
                try
                {
                    response = GetInferenceResult(gamestate);
                    if (!TryParseAction(response, validGroups, out _, out index))
                    {
                        string err = $"Failed to parse '{response}'.";
                        if (!dumped) { DumpInvalidAction("YesNo", gamestate, response, validGroups, err); dumped = true; }
                        if (++retries >= MAX_RETRIES) { HandleRetryLimitExceeded("YesNo", response, validGroups, err); return false; }
                        throw new FormatException(err);
                    }
                    if (index == 1 || index == 0) { SendTopLevelThought("yes_no", response, DescribeYesNoAction); return index == 1; }
                    else { if (++retries >= MAX_RETRIES) { HandleRetryLimitExceeded("YesNo", response, validGroups, $"Index {index} invalid"); return false; } throw new FormatException($"Index {index} invalid, must be 0 or 1."); }
                }
                catch (FormatException ex) { Console.WriteLine($"[YesNo] Invalid action: '{response}' -- {ex.Message}"); }
            }
            } finally { _decisionDepth--; }
        }

        public override bool OnSelectEffectYesNo(ClientCard card, long desc)
        {
            _decisionDepth++;
            try {
            var gamestate = GetGameState(GameMessage.SelectEffectYn, effectYesNoData: new SelectEffectYesNoData { Card = card, Desc = desc });
            int index; string response = ""; int retries = 0; bool dumped = false;
            var validGroups = new Dictionary<char, int> { { 'i', 2 } };
            while (true)
            {
                try
                {
                    response = GetInferenceResult(gamestate);
                    if (!TryParseAction(response, validGroups, out _, out index))
                    {
                        string err = $"Failed to parse '{response}'.";
                        if (!dumped) { DumpInvalidAction("EffectYesNo", gamestate, response, validGroups, err); dumped = true; }
                        if (++retries >= MAX_RETRIES) { HandleRetryLimitExceeded("EffectYesNo", response, validGroups, err); return false; }
                        throw new FormatException(err);
                    }
                    if (index == 1 || index == 0) { SendTopLevelThought("effect_yes_no", response, DescribeYesNoAction); return index == 1; }
                    else { if (++retries >= MAX_RETRIES) { HandleRetryLimitExceeded("EffectYesNo", response, validGroups, $"Index {index} invalid"); return false; } throw new FormatException($"Index {index} invalid, must be 0 or 1."); }
                }
                catch (FormatException ex) { Console.WriteLine($"[EffectYesNo] Invalid action: '{response}' -- {ex.Message}"); }
            }
            } finally { _decisionDepth--; }
        }

        public override int OnSelectChain(IList<ClientCard> cards, bool forced)
        {
            _decisionDepth++;
            try {
            var gamestate = GetGameState(GameMessage.SelectChain, chainData: new SelectChainData { Cards = cards, Forced = forced });
            int index; string response = ""; int retries = 0; bool dumped = false;
            var validGroups = new Dictionary<char, int> { { 'i', cards.Count } };
            while (true)
            {
                try
                {
                    response = GetInferenceResult(gamestate);
                    if (!TryParseAction(response, validGroups, out _, out index))
                    {
                        string err = $"Failed to parse '{response}' (valid: i0..i{cards.Count - 1}).";
                        if (!dumped) { DumpInvalidAction("Chain", gamestate, response, validGroups, err); dumped = true; }
                        if (++retries >= MAX_RETRIES) { HandleRetryLimitExceeded("Chain", response, validGroups, err); return forced ? 0 : -1; }
                        throw new FormatException(err);
                    }
                    if (index < -1 || index >= cards.Count)
                    {
                        string err = $"Index {index} out of range [-1, {cards.Count - 1}].";
                        if (!dumped) { DumpInvalidAction("Chain", gamestate, response, validGroups, err); dumped = true; }
                        if (++retries >= MAX_RETRIES) { HandleRetryLimitExceeded("Chain", response, validGroups, err); return forced ? 0 : -1; }
                        throw new FormatException(err);
                    }
                    SendTopLevelThought("chain", response, code => DescribeChainAction(code, cards));
                    return index;
                }
                catch (FormatException ex) { Console.WriteLine($"[Chain] Invalid action: '{response}' -- {ex.Message}"); }
            }
            } finally { _decisionDepth--; }
        }

        public override int OnSelectOption(IList<long> options)
        {
            _decisionDepth++;
            try {
            var gamestate = GetGameState(GameMessage.SelectOption, optionData: new SelectOptionData { Options = options });
            int index; string response = ""; int retries = 0; bool dumped = false;
            var validGroups = new Dictionary<char, int> { { 'i', options.Count } };
            while (true)
            {
                try
                {
                    response = GetInferenceResult(gamestate);
                    if (!TryParseAction(response, validGroups, out _, out index))
                    {
                        string err = $"Failed to parse '{response}' (valid: i0..i{options.Count - 1}).";
                        if (!dumped) { DumpInvalidAction("Option", gamestate, response, validGroups, err); dumped = true; }
                        if (++retries >= MAX_RETRIES) { HandleRetryLimitExceeded("Option", response, validGroups, err); return 0; }
                        throw new FormatException(err);
                    }
                    if (index < 0 || index >= options.Count)
                    {
                        string err = $"Index {index} out of range [0, {options.Count - 1}].";
                        if (!dumped) { DumpInvalidAction("Option", gamestate, response, validGroups, err); dumped = true; }
                        if (++retries >= MAX_RETRIES) { HandleRetryLimitExceeded("Option", response, validGroups, err); return 0; }
                        throw new FormatException(err);
                    }
                    SendTopLevelThought("option", response, code => DescribeOptionAction(code, options));
                    return index;
                }
                catch (FormatException ex) { Console.WriteLine($"[Option] Invalid action: '{response}' -- {ex.Message}"); }
            }
            } finally { _decisionDepth--; }
        }

        public override CardPosition OnSelectPosition(int cardId, IList<CardPosition> positions)
        {
            _decisionDepth++;
            try {
            var gamestate = GetGameState(GameMessage.SelectPosition, positionData: new SelectPositionData { CardId = cardId, Positions = positions });
            int index; string response = ""; int retries = 0; bool dumped = false;
            var validGroups = new Dictionary<char, int> { { 'i', positions.Count } };
            while (true)
            {
                try
                {
                    response = GetInferenceResult(gamestate);
                    if (!TryParseAction(response, validGroups, out _, out index))
                    {
                        string err = $"Failed to parse '{response}' (valid: i0..i{positions.Count - 1}).";
                        if (!dumped) { DumpInvalidAction("Position", gamestate, response, validGroups, err); dumped = true; }
                        if (++retries >= MAX_RETRIES) { HandleRetryLimitExceeded("Position", response, validGroups, err); return positions[0]; }
                        throw new FormatException(err);
                    }
                    SendTopLevelThought("position", response, code => DescribePositionAction(code, cardId, positions));
                    return positions[index];
                }
                catch (FormatException ex) { Console.WriteLine($"[Position] Invalid action: '{response}' -- {ex.Message}"); }
            }
            } finally { _decisionDepth--; }
        }

        // ════════════════════════════════════════════════════════
        // HTTP + parsing utilities
        // ════════════════════════════════════════════════════════

        public string GetInferenceResult(string arguments)
        {
            try
            {
                var content = CreateGzipContent(arguments);
                var response = _httpClient.PostAsync($"{_rlServerUrl}/predict", content).Result;
                if (response.IsSuccessStatusCode)
                {
                    string result = response.Content.ReadAsStringAsync().Result;
                    try
                    {
                        var parsed = System.Text.Json.JsonSerializer.Deserialize<InferenceResponse>(result);
                        if (parsed != null && !string.IsNullOrEmpty(parsed.Move))
                        {
                            _lastInferenceMeta = new InferenceMeta
                            {
                                Confidence = parsed.Confidence,
                                Value = parsed.Value,
                                WinProb = parsed.WinProb,
                                ValidCount = parsed.ValidCount,
                                DecisionType = parsed.DecisionType,
                                TopActions = parsed.TopActions ?? new List<TopAction>(),
                            };
                            return parsed.Move.Trim();
                        }
                    }
                    catch { /* fall through to legacy {"move": "..."} parse */ }
                    try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(result)["move"].Trim(); }
                    catch { return result.Trim(); }
                }
                else throw new Exception($"API request failed with status code {response.StatusCode}");
            }
            catch (Exception ex)
            {
                // .Result wraps faults in AggregateException, which itself wraps
                // HttpRequestException, which wraps the underlying SocketException
                // ("Connection refused" / "Timeout" / etc.). Walk the whole chain
                // so we don't lose the leaf-level reason.
                var sb = new StringBuilder();
                for (Exception cur = ex; cur != null; cur = cur.InnerException)
                {
                    if (sb.Length > 0) sb.Append(" -> ");
                    sb.Append(cur.GetType().Name).Append(": ").Append(cur.Message);
                }
                return $"Error calling Python API: {sb}";
            }
        }

        private static ByteArrayContent CreateGzipContent(string json)
        {
            byte[] raw = Encoding.UTF8.GetBytes(json);
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress, true)) gz.Write(raw, 0, raw.Length);
                var content = new ByteArrayContent(ms.ToArray());
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                content.Headers.ContentEncoding.Add("gzip");
                return content;
            }
        }

        private bool TryParseAction(string response, Dictionary<char, int> validGroups, out char group, out int index)
        {
            group = ' '; index = -1;
            if (string.IsNullOrEmpty(response) || response.Length < 2) return false;
            group = response[0];
            if (!validGroups.ContainsKey(group)) return false;
            if (!int.TryParse(response.Substring(1), out index) || index < -1) return false;
            return group == 'p' ? (index > 0 && index < 4) : (index < validGroups[group]);
        }

        // ════════════════════════════════════════════════════════
        // Game state serialization
        //
        // CRITICAL: The ActionSpace must only include actions that
        // are legal for the CURRENT decision type. During battle
        // phase (SelectBattleCmd), main-phase-only actions like
        // NormalSummon, SetSpell, etc. must be empty even though
        // the Main object is non-null (it holds stale MP1 data).
        // Similarly, during main phase, Attack must be empty even
        // if Battle is non-null from a previous battle phase.
        // ════════════════════════════════════════════════════════

        private static readonly int[] EmptyIntArray = new int[0];
        private static readonly long[] EmptyLongArray = new long[0];

        public string GetGameState(GameMessage currentGameState,
            SelectCardData cardData = null, SelectChainData chainData = null,
            SelectYesNoData yesNoData = null, SelectEffectYesNoData effectYesNoData = null,
            SelectPositionData positionData = null, SelectOptionData optionData = null)
        {
            Console.WriteLine("Generating game state for state: " + currentGameState);
            var bot = Util.Bot; var enemy = Util.Enemy;

            bool isBattle = (currentGameState == GameMessage.SelectBattleCmd);

            var gameStateInput = new
            {
                MetaInformation = new
                {
                    MatchId = matchId,
                    MachineId = _machineId,
                    PlayerSlot = playerSlot,
                    ModelTag = "MODEL_TAG",
                    StepCount = stepCount++,
                    SentAtUtc = DateTime.UtcNow,
                    // When true, this inference is a direct response to an
                    // engine-emitted MSG_SELECT_*. When false, it's a bot-
                    // internal sub-decision (e.g. OnSelectYesNo fired from
                    // OnSelectCard's loop) and shouldn't appear in the
                    // replay-aligned thoughts sidecar.
                    TopLevel = _decisionDepth == 1,
                    TurnNumber = Duel.Turn,
                    DecisionType = currentGameState.ToString()
                },
                State = new
                {
                    DuelMetadata = new
                    {
                        TurnNumber = GetNormalizedValue(Duel.Turn, 30),
                        Phase = GetOneHotPhase(Duel.Phase),
                        CurrentTurnPlayer = GetBoolAsIntValue(IsBotsTurn()),
                        GameState = GetOneHotGameMessage(currentGameState),
                        Duel.LastSummonPlayer,
                        SummoningCards = ToIdList(Duel.SummoningCards),
                        LastSummonedCards = ToIdList(Duel.LastSummonedCards),
                        ActivatedCardsThisTurn = cardsActivatedThisTurn,
                    },
                    Bot = new
                    {
                        Lifepoints = GetNormalizedValue(bot.LifePoints, 8000),
                        UnderAttack = GetBoolAsIntValue(bot.UnderAttack),
                        Deck = new { CardsInDeck = GetNormalizedValue(bot.Deck.Count, 100), Cards = currentDecklist?.ToArray() ?? EmptyIntArray },
                        Hand = new { CardsInHand = GetNormalizedValue(bot.Hand.Count, 100), Cards = ToIdList(bot.Hand) },
                        Field = new
                        {
                            MonsterZone = Enumerable.Range(0, 5).Select(i => (object)BuildMonsterSlotPayload(bot.MonsterZone[i], Bot.BattlingMonster)).ToArray(),
                            Backrow = Enumerable.Range(0, 5).Select(i => (object)BuildBackrowSlotPayload(bot.SpellZone[i])).ToArray()
                        },
                        Graveyard = new { CardsInGrave = GetNormalizedValue(bot.Graveyard.Count, 100), Cards = ToIdList(bot.Graveyard) },
                        Banished = new { CardsBanished = GetNormalizedValue(bot.Banished.Count, 100), Cards = ToIdList(bot.Banished) },
                    },
                    Enemy = new
                    {
                        Lifepoints = GetNormalizedValue(enemy.LifePoints, 8000),
                        UnderAttack = GetBoolAsIntValue(enemy.UnderAttack),
                        Deck = new { CardsInDeck = GetNormalizedValue(enemy.Deck.Count, 100) },
                        Hand = new { CardsInHands = GetNormalizedValue(enemy.Hand.Count, 100) },
                        Field = new
                        {
                            MonsterZone = Enumerable.Range(0, 5).Select(i => (object)BuildMonsterSlotPayload(enemy.MonsterZone[i], Bot.BattlingMonster)).ToArray(),
                            Backrow = Enumerable.Range(0, 5).Select(i => (object)BuildBackrowSlotPayload(enemy.SpellZone[i])).ToArray()
                        },
                        Graveyard = new { CardsInGrave = GetNormalizedValue(enemy.Graveyard.Count, 100), Cards = ToIdList(enemy.Graveyard) },
                        Banished = new { CardsBanished = GetNormalizedValue(enemy.Banished.Count, 100), Cards = ToIdList(enemy.Banished) },
                    },
                    CurrentChain = new
                    {
                        ChainCount = GetNormalizedValue(Duel.CurrentChain.Count, 20),
                        Duel.LastChainPlayer,
                        CurrentChain = ToIdList(Duel.CurrentChain),
                        ChainTargets = ToIdList(Duel.ChainTargets),
                        ChainTargetOnly = ToIdList(Duel.ChainTargetOnly)
                    },
                    RecentEvents = _eventBuffer.ToArray()
                },
                ActionSpace = new
                {
                    // Main-phase-only actions: empty during battle phase
                    NormalSummon = BuildOptionGroupCompact(isBattle ? null : Main?.SummonableCards),
                    SetMonster = BuildOptionGroupCompact(isBattle ? null : Main?.MonsterSetableCards),
                    SpecialSummon = BuildOptionGroupCompact(isBattle ? null : Main?.SpecialSummonableCards, withLoc: !isBattle),
                    Repos = BuildOptionGroupCompact(isBattle ? null : Main?.ReposableCards),
                    SetSpell = BuildOptionGroupCompact(isBattle ? null : Main?.SpellSetableCards),

                    // Activate: use the correct source for the current phase
                    Activate = BuildOptionGroupCompact(
                        isBattle ? Battle?.ActivableCards : Main?.ActivableCards,
                        withLoc: true,
                        descriptions: isBattle ? Battle?.ActivableDescs : Main?.ActivableDescs),

                    // Battle-phase-only actions: empty during main phase
                    Attack = BuildOptionGroupCompact(isBattle ? Battle?.AttackableCards : null),

                    // Phase transitions: only show transitions valid for the current phase
                    ChangePhase = new
                    {
                        CanBattlePhase = isBattle ? false : (Main?.CanBattlePhase ?? false),
                        CanMainPhaseTwo = isBattle ? (Battle?.CanMainPhaseTwo ?? false) : false,
                        CanEndPhase = isBattle ? (Battle?.CanEndPhase ?? false) : (Main?.CanEndPhase ?? false)
                    },

                    // Selection prompts: unaffected by phase
                    SelectCardData = new { Count = cardData?.Cards.Count ?? 0, Options = BuildCardSelectionOptions(cardData?.Cards), Min = cardData?.Min ?? 0, Max = cardData?.Max ?? 0, Hint = cardData?.Hint ?? 0, Cancelable = cardData?.Cancelable ?? false },
                    ChainData = new { Count = chainData?.Cards.Count ?? 0, Options = BuildChainSelectionOptions(chainData?.Cards), Forced = chainData?.Forced ?? false },
                    YesNoData = new { Desc = yesNoData?.Desc ?? -1 },
                    EffectYesNoData = new { CardId = effectYesNoData?.Card?.Id ?? -1, Desc = effectYesNoData?.Desc ?? -1 },
                    OptionData = new { Count = optionData?.Options.Count ?? 0, Options = optionData?.Options?.ToArray() ?? EmptyLongArray },
                    PositionData = new { CardId = positionData?.CardId ?? -1, Positions = BuildPositionArray(positionData?.Positions) }
                }
            };

            return JsonConvert.SerializeObject(gameStateInput, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        }

        private object BuildMonsterSlotPayload(ClientCard card, ClientCard battlingRef)
        {
            if (card == null) return new { Occupied = 0 };
            int[] equip = (card.EquipCards != null && card.EquipCards.Count > 0) ? card.EquipCards.Select(c => c.Id).ToArray() : null;
            return new
            {
                Occupied = 1,
                BaseCardData = card.Id,
                CurrAttack = GetNormalizedValue(card.Attack, 8000),
                CurrDefense = GetNormalizedValue(card.Defense, 8000),
                IsFaceup = GetBoolAsIntValue(card.IsFaceup()),
                Position = OneHotEncodePosition((int)card.Position),
                Battling = GetBoolAsIntValue(battlingRef == card),
                Attacked = GetBoolAsIntValue(card.Attacked),
                Owner = card.Owner,
                Controller = card.Controller,
                EquipCards = equip
            };
        }

        private object BuildBackrowSlotPayload(ClientCard card)
        {
            if (card == null) return new { Occupied = 0 };
            return new
            {
                Occupied = 1,
                BaseCardData = card.Id,
                IsFaceup = GetBoolAsIntValue(card.IsFaceup()),
                Position = OneHotEncodePosition((int)card.Position),
                Owner = card.Owner,
                Controller = card.Controller
            };
        }

        private int[] ToIdList(IEnumerable<ClientCard> cards) => cards == null ? EmptyIntArray : cards.Select(c => c.Id).ToArray();

        private object BuildOptionGroupCompact(IList<ClientCard> cards, bool withLoc = false, IList<long> descriptions = null)
        {
            cards ??= Array.Empty<ClientCard>(); int ind = 0;
            var feats = cards.Select(c => new
            {
                BaseCardData = c.Id,
                Location = withLoc ? OneHotEncodeLocation(c.Location, locationMatters: true) : null,
                Description = descriptions?[ind++],
            }).ToArray();
            return new { Count = feats.Length, Cards = feats };
        }

        private object[] BuildCardSelectionOptions(IList<ClientCard> cards)
        {
            if (cards == null) return Array.Empty<object>();
            return cards.Select((c, i) => (object)new { Index = i, BaseCardData = c.Id, Location = (int)c.Location }).ToArray();
        }

        private object[] BuildChainSelectionOptions(IList<ClientCard> cards)
        {
            if (cards == null) return Array.Empty<object>();
            return cards.Select((c, i) => (object)new { Index = i, BaseCardData = c.Id }).ToArray();
        }

        private int[] BuildPositionArray(IList<CardPosition> positions) => positions == null ? Array.Empty<int>() : positions.Select(p => (int)p).ToArray();

        private bool IsBotsTurn() => Duel.IsFirst ? (Duel.Turn % 2 == 1) : (Duel.Turn % 2 == 0);
        private bool CanEndPhase() => (Main != null && Main.CanEndPhase) || (Battle != null && Battle.CanEndPhase);
        private string GetDeckName() => ((DeckAttribute)Attribute.GetCustomAttribute(typeof(ExodAIExecutor), typeof(DeckAttribute)))?.File ?? "DefaultDeckName";

        private int[] OneHotEncodeLocation(CardLocation location, bool locationMatters = true)
        {
            int[] oneHot = new int[5];
            if (!locationMatters) return oneHot;
            if ((location & CardLocation.Deck) != 0) oneHot[0] = 1;
            else if ((location & CardLocation.Hand) != 0) oneHot[1] = 1;
            else if ((location & CardLocation.MonsterZone) != 0) oneHot[2] = 1;
            else if ((location & CardLocation.SpellZone) != 0) oneHot[3] = 1;
            else if ((location & CardLocation.Grave) != 0) oneHot[4] = 1;
            return oneHot;
        }

        private int[] GetOneHotPhase(DuelPhase phase)
        {
            DuelPhase[] phaseOrder = { DuelPhase.Draw, DuelPhase.Standby, DuelPhase.Main1, DuelPhase.BattleStart, DuelPhase.BattleStep, DuelPhase.Damage, DuelPhase.DamageCal, DuelPhase.Battle, DuelPhase.Main2, DuelPhase.End };
            int[] oneHot = new int[phaseOrder.Length];
            for (int i = 0; i < phaseOrder.Length; i++) { if (phase == phaseOrder[i]) { oneHot[i] = 1; break; } }
            return oneHot;
        }

        private int[] GetOneHotGameMessage(GameMessage message)
        {
            GameMessage[] usedMessages = { GameMessage.SelectIdleCmd, GameMessage.SelectBattleCmd, GameMessage.SelectCard, GameMessage.SelectYesNo, GameMessage.SelectEffectYn, GameMessage.SelectChain, GameMessage.SelectPosition };
            int[] oneHot = new int[usedMessages.Length];
            for (int i = 0; i < usedMessages.Length; i++) { if (message == usedMessages[i]) { oneHot[i] = 1; break; } }
            return oneHot;
        }

        private static int[] OneHotEncodePosition(int positionCode)
        {
            int[] codes = { 1, 4, 8, 5, 10 };
            int[] oneHot = new int[codes.Length];
            for (int i = 0; i < codes.Length; i++) { if (codes[i] == positionCode) { oneHot[i] = 1; break; } }
            return oneHot;
        }

        private float GetNormalizedValue(int value, int divider) => (float)value / divider;
        private int GetBoolAsIntValue(bool value) => value ? 1 : 0;
    }
}
