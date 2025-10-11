using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using YGOSharp.Network.Enums;
using YGOSharp.OCGWrapper;
using YGOSharp.OCGWrapper.Enums;
using static WindBot.Game.AI.Decks.TearlamentsExecutor;

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
        [JsonPropertyName("cleanName")]
        public string CleanName { get; set; }

        [JsonPropertyName("konamiCode")]
        public string KonamiCode { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("icon")]
        public string? Icon { get; set; }

        [JsonPropertyName("normal")]
        public bool Normal { get; set; }

        [JsonPropertyName("effect")]
        public bool Effect { get; set; }

        [JsonPropertyName("fusion")]
        public bool Fusion { get; set; }

        [JsonPropertyName("ritual")]
        public bool Ritual { get; set; }

        [JsonPropertyName("synchro")]
        public bool Synchro { get; set; }

        [JsonPropertyName("xyz")]
        public bool Xyz { get; set; }

        [JsonPropertyName("pendulum")]
        public bool Pendulum { get; set; }

        [JsonPropertyName("link")]
        public bool Link { get; set; }

        [JsonPropertyName("flip")]
        public bool Flip { get; set; }

        [JsonPropertyName("gemini")]
        public bool Gemini { get; set; }

        [JsonPropertyName("spirit")]
        public bool Spirit { get; set; }

        [JsonPropertyName("toon")]
        public bool Toon { get; set; }

        [JsonPropertyName("tuner")]
        public bool Tuner { get; set; }

        [JsonPropertyName("union")]
        public bool Union { get; set; }

        [JsonPropertyName("attribute")]
        public string Attribute { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("level")]
        public int? Level { get; set; }

        [JsonPropertyName("rating")]
        public int? Rating { get; set; }

        [JsonPropertyName("arrows")]
        public object? Arrows { get; set; }

        [JsonPropertyName("scale")]
        public int? Scale { get; set; }

        [JsonPropertyName("atk")]
        public int? Atk { get; set; }

        [JsonPropertyName("def")]
        public int? Def { get; set; }

        [JsonPropertyName("pendulumEffect")]
        public string? PendulumEffect { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("extraDeck")]
        public bool ExtraDeck { get; set; }
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
                throw new ArgumentOutOfRangeException(nameof(ind), "Index must be within the range of the one-hot vector length.");

            int[] oneHot = new int[length];
            oneHot[ind] = 1;
            return oneHot;
        }

        private int[] OneHotCategory(string? category)
        {
            // Order: [Monster, Spell, Trap]
            return category?.ToLower() switch
            {
                "Monster" => new[] { 1, 0, 0 },
                "Spell" => new[] { 0, 1, 0 },
                "Trap" => new[] { 0, 0, 1 },
                _ => new[] { 0, 0, 0 } // fallback for null/unknown
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

        // Constants for maximum sizes
        private const int MAX_DECK_SIZE = 40;
        private const int MAX_HAND_SIZE = 12;
        private const int MAX_GRAVEYARD_SIZE = 40;
        private const int MAX_BANISHED_SIZE = 40;
        private const int MAX_SUMMONING_CARDS = 3;
        private const int MAX_LAST_SUMMONED_CARDS = 3;
        private const int MAX_CURRENT_CHAIN = 5;
        private const int MAX_CHAIN_TARGETS = 5;
        private const int MAX_CHAIN_TARGET_ONLY = 5;
        private const int MAX_OPTIONS = 20;
        private const int MAX_CARD_SELECTION_OPTIONS = 20;
        private const int MAX_CHAIN_SELECTION_OPTIONS = 10;

        public ExodAIExecutor(GameAI ai, Duel duel) : base(ai, duel)
        {
            Console.WriteLine("ExodAIExecutor");
            gameState = new int[500];

            var deck = WindBot.Game.Deck.Load(GetDeckName());
            masterDecklist = deck.Cards.ToList();
            currentDecklist = deck.Cards.ToList();
            LoadBaseCardData();
        }

        private void LoadBaseCardData()
        {
            baseCardData = new Dictionary<int, BaseCardData>();
            // Load base card data from a file

            string filePath = "C:\\Users\\Joe\\Documents\\Windbot\\windbot\\card_metadata.json";

            try
            {
                foreach (string line in File.ReadLines(filePath))
                {
                    string validJson = line.Replace("NaN", "null")
                                           .Replace("None", "null")
                                           .Replace("True", "true")
                                           .Replace("False", "false");

                    var cardData = JsonConvert.DeserializeObject<BaseCardData>(validJson);
                    if (cardData != null)
                    {
                        int konamiCode = int.Parse(cardData.KonamiCode);

                        // If its a normal monster, remove it's description as its just flavor text. TODO: Handle cases where the description is actually useful for normal monsters
                        if (cardData.Normal)
                        {
                            cardData.Description = string.Empty;
                        }

                        // Prepend the card name to the description
                        cardData.Description = cardData.CleanName + (cardData.Description != string.Empty ? "|||" + cardData.Description : string.Empty) + (cardData.PendulumEffect != null ? "|||" + cardData.PendulumEffect : string.Empty);

                        baseCardData.Add(konamiCode, cardData);
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error reading card metadata file: " + e.Message);
            }

            Console.WriteLine("Loaded " + baseCardData.Count + " base card data entries");
        }

        public override void OnStartDuel()
        {
            currentDecklist = masterDecklist.ToList();
        }

        public override void OnNewTurn()
        {
            cardsActivatedThisTurn = new Dictionary<int, int>();
        }

        public override void OnDraw(int player, int count)
        {
            Console.WriteLine($"Player {player} drew {count} cards");
            if (player != 0)
            {
                return;
            }

            pendingCardDraw = true;
            cardsDrawn = count;
        }

        public override void OnUpdateData(int player, CardLocation location)
        {
            // If the player is not the bot, we don't care about the update
            if (player != 0)
            {
                return;
            }

            // If this update is a draw update and we have the flag still active, we need to update the decklist with the newly drawn cards
            if (pendingCardDraw)
            {
                var drawnCards = Bot.Hand.Skip(Bot.Hand.Count - cardsDrawn).ToList();

                foreach (var card in drawnCards)
                {
                    Console.WriteLine($"Card {card} was drawn");
                    currentDecklist.Remove(card.Id);
                }

                Console.WriteLine($"Cards left in deck: {currentDecklist.Count}");
                pendingCardDraw = false;
            }
        }

        public override void OnMoveCard(CardLocation source, int sourceController, CardLocation dest, int destController, int reason, int cardId)
        {
            if (source == dest)
            {
                return; // Ignore cards moving to the same location
            }

            // If the card is leaving the deck, we need to update our deck list to reflect the change
            if (sourceController == 0 && source == CardLocation.Deck)
            {
                Console.WriteLine($"Card {cardId} was removed from my deck");
                currentDecklist.Remove(cardId);
            }

            // If the card is entering the deck, we need to update our deck list to reflect the change
            else if (destController == 0 && dest == CardLocation.Deck)
            {
                Console.WriteLine($"Card {cardId} was added to my deck");
                currentDecklist.Add(cardId);
            }

            Console.WriteLine($"Cards left in deck: {currentDecklist.Count}");
        }

        public override void OnActivateCard(Dictionary<int, int> activatedCards)
        {
            cardsActivatedThisTurn = activatedCards;
        }

        public override MainPhaseAction GenerateMainPhaseAction()
        {
            //Duel.Fields[0];
            var gamestate = GetGameState(GameMessage.SelectIdleCmd);

            char group;
            int index;
            while (true)
            {
                try
                {
                    // Call the model inference endpoint
                    string response = GetInferenceResult(gamestate);

                    var validGroups = new Dictionary<char, int>
                    {
                        { 'n', Main.SummonableCards.Count },
                        { 'd', Main.MonsterSetableCards.Count },
                        { 'x', Main.SpecialSummonableCards.Count },
                        { 'a', Main.ActivableCards.Count },
                        { 's', Main.SpellSetableCards.Count },
                        { 'r', Main.ReposableCards.Count },
                        { 'p', 3 } // Options: 1 for Battle Phase, 2 for End Phase
                    };

                    if (!TryParseAction(response, validGroups, out group, out index))
                        throw new FormatException("Input must be a valid action (e.g., 'n2', 'd1').");

                    if (group == 'p' && index != 1 && index != 3)
                        throw new FormatException("Phase options are 1 for Battle Phase, or 3 for End Phase.");

                    break;
                }
                catch (FormatException ex)
                {
                    Console.WriteLine($"Invalid input. {ex.Message} Please try again.");
                }
            }

            MainPhaseAction action;
            switch (group)
            {
                case 'n':
                    action = new MainPhaseAction(MainPhaseAction.MainAction.Summon, Main.SummonableCards[index].ActionIndex);
                    break;
                case 'd':
                    action = new MainPhaseAction(MainPhaseAction.MainAction.SetMonster, Main.MonsterSetableCards[index].ActionIndex);
                    break;
                case 'x':
                    action = new MainPhaseAction(MainPhaseAction.MainAction.SpSummon, Main.SpecialSummonableCards[index].ActionIndex);
                    break;
                case 'a':
                    action = new MainPhaseAction(MainPhaseAction.MainAction.Activate, Main.ActivableCards[index].ActionActivateIndex[Main.ActivableDescs[index]]);
                    break;
                case 's':
                    action = new MainPhaseAction(MainPhaseAction.MainAction.SetSpell, Main.SpellSetableCards[index].ActionIndex);
                    break;
                case 'r':
                    action = new MainPhaseAction(MainPhaseAction.MainAction.Repos, Main.ReposableCards[index].ActionIndex);
                    break;
                case 'p':
                    action = GenerateMainPhaseChangeAction(index);
                    break;
                default:
                    action = new MainPhaseAction(MainPhaseAction.MainAction.ToEndPhase);
                    break;
            }

            return action;
        }

        public override BattlePhaseAction GenerateBattlePhaseAction()
        {

            //Duel.Fields[0];
            var gamestate = GetGameState(GameMessage.SelectBattleCmd);

            char group;
            int index;

            var validGroups = new Dictionary<char, int>
            {
                { 'b', Battle.AttackableCards.Count },
                { 'a', Battle.ActivableCards.Count },
                { 'p', 3 } // Options: 1 for Main Phase 2, 2 for End Phase
            };

            while (true)
            {
                try
                {
                    string response = GetInferenceResult(gamestate);

                    if (!TryParseAction(response, validGroups, out group, out index))
                        throw new FormatException("Input must be a valid action (e.g., 'b2', 'a1').");

                    if (group == 'p' && (index < 2 || index > 3))
                        throw new FormatException("Phase options are 2 for Main Phase 2, or 3 for End Phase.");

                    break;
                }
                catch (FormatException ex)
                {
                    Console.WriteLine($"Invalid input. {ex.Message} Please try again.");
                }
            }

            BattlePhaseAction action;
            switch (group)
            {
                case 'b':
                    action = new BattlePhaseAction(BattlePhaseAction.BattleAction.Attack, Battle.AttackableCards[index].ActionIndex);
                    break;
                case 'a':
                    action = new BattlePhaseAction(BattlePhaseAction.BattleAction.Activate, Battle.ActivableCards[index].ActionIndex);
                    break;
                case 'p':
                    action = GenerateBattlePhaseChangeAction(index);
                    break;
                default:
                    action = new BattlePhaseAction(BattlePhaseAction.BattleAction.ToEndPhase);
                    break;
            }

            return action;
        }

        private MainPhaseAction GenerateMainPhaseChangeAction(int index)
        {
            if (index == 1 && Main.CanBattlePhase)
                return new MainPhaseAction(MainPhaseAction.MainAction.ToBattlePhase);
            else if (index == 3 && Main.CanEndPhase)
                return new MainPhaseAction(MainPhaseAction.MainAction.ToEndPhase);
            else
            {
                Console.WriteLine("You cannot move to that phase at this time.");
                return GenerateMainPhaseAction(); // Re-prompt for valid action
            }
        }

        private BattlePhaseAction GenerateBattlePhaseChangeAction(int index)
        {
            if (index == 2 && Battle.CanMainPhaseTwo)
                return new BattlePhaseAction(BattlePhaseAction.BattleAction.ToMainPhaseTwo);
            else if (index == 3 && Battle.CanEndPhase)
                return new BattlePhaseAction(BattlePhaseAction.BattleAction.ToEndPhase);
            else
            {
                Console.WriteLine("You cannot move to that phase at this time.");
                return GenerateBattlePhaseAction(); // Re-prompt for valid action
            }
        }

        public override IList<ClientCard> OnSelectCard(IList<ClientCard> cards, int min, int max, long hint, bool cancelable)
        {
            var selectCardData = new SelectCardData { Cards = cards, Min = min, Max = max, Hint = hint, Cancelable = cancelable };
            var gamestate = GetGameState(GameMessage.SelectCard, cardData: selectCardData);

            var validGroups = new Dictionary<char, int> { { 'i', cards.Count } };
            var selectedCards = new List<ClientCard>();
            var chosenIndices = new HashSet<int>();

            // Collect mandatory minimum
            while (selectedCards.Count < min)
            {
                int idx = RequestUniqueIndexFromBot(gamestate, validGroups, cards.Count, chosenIndices);
                chosenIndices.Add(idx);
                selectedCards.Add(cards[idx]);
            }

            // Optionally continue up to max
            while (selectedCards.Count < max && OnSelectYesNo(hint))
            {
                int idx = RequestUniqueIndexFromBot(gamestate, validGroups, cards.Count, chosenIndices);
                chosenIndices.Add(idx);
                selectedCards.Add(cards[idx]);
            }

            return selectedCards;
        }

        private int RequestUniqueIndexFromBot(string gamestate, Dictionary<char, int> validGroups, int totalCards, HashSet<int> alreadyChosen)
        {
            while (true)
            {
                try
                {
                    string response = GetInferenceResult(gamestate);

                    if (!TryParseAction(response, validGroups, out _, out int index))
                        throw new FormatException("Input must be a valid action (e.g., 'i3').");

                    if (index < 0 || index >= totalCards)
                        throw new FormatException($"Index out of range. Must be between 0 and {totalCards - 1}.");

                    if (alreadyChosen.Contains(index))
                        throw new FormatException("Duplicate index selected. Choose a different card.");

                    return index;
                }
                catch (FormatException ex)
                {
                    Console.WriteLine($"{ex.Message} Please try again.");
                }
            }
        }


        public override bool OnSelectYesNo(long desc)
        {
            var selectYesNoData = new SelectYesNoData() { Desc = desc };
            var gamestate = GetGameState(GameMessage.SelectYesNo, yesNoData: selectYesNoData);

            int index;
            var validGroups = new Dictionary<char, int> {{ 'i', 2}}; // 0 for no, 1 for yes
            while (true)
            {
                try
                {
                    string response = GetInferenceResult(gamestate);
                    if (!TryParseAction(response, validGroups, out _, out index))
                        throw new FormatException("Input must be a valid action (e.g., 'b2', 'a1').");

                    if (index == 1)
                        return true;
                    else if (index == 0)
                        return false;
                    else
                        throw new ArgumentException("Invalid input. Please enter '0' for No or '1' for Yes.");
                }
                catch (FormatException ex)
                {
                    Console.WriteLine($"Invalid input. {ex.Message} Please try again.");
                }
            }
        }

        public override bool OnSelectEffectYesNo(ClientCard card, long desc)
        {
            var selectEffYesNoData = new SelectEffectYesNoData() { Card = card, Desc = desc };
            var gamestate = GetGameState(GameMessage.SelectEffectYn, effectYesNoData: selectEffYesNoData);

            int index;
            var validGroups = new Dictionary<char, int> { { 'i', 2 } }; // 0 for no, 1 for yes
            while (true)
            {
                try
                {
                    string response = GetInferenceResult(gamestate);
                    if (!TryParseAction(response, validGroups, out _, out index))
                        throw new FormatException("Input must be a valid action (e.g., 'b2', 'a1').");

                    if (index == 1)
                        return true;
                    else if (index == 0)
                        return false;
                    else
                        throw new ArgumentException("Please enter '0' for No or '1' for Yes.");
                }
                catch (FormatException ex)
                {
                    Console.WriteLine($"Invalid input. {ex.Message} Please try again.");
                }
            }
        }

        public override int OnSelectChain(IList<ClientCard> cards, bool forced)
        {
            var chainCardData = new SelectChainData() { Cards = cards, Forced = forced };
            var gamestate = GetGameState(GameMessage.SelectChain, chainData: chainCardData);

            int index;
            var validGroups = new Dictionary<char, int> { { 'i', cards.Count } };
            while (true)
            {
                try
                {
                    string response = GetInferenceResult(gamestate);
                    if (!TryParseAction(response, validGroups, out _, out index))
                        throw new FormatException("Input must be a valid action (e.g., 'b2', 'a1').");

                    return index - 1; // Adjusting index to match game logic (0 for no chain, 1+ for card index)
                }
                catch (FormatException ex)
                {
                    Console.WriteLine($"Invalid input. {ex.Message} Please try again.");
                }
            }
        }

        public override int OnSelectOption(IList<long> options)
        {
            var selectOptionData = new SelectOptionData() { Options = options };
            var gamestate = GetGameState(GameMessage.SelectOption, optionData: selectOptionData);

            int index;
            var validGroups = new Dictionary<char, int> { { 'i', options.Count } };
            while (true)
            {
                try
                {
                    string response = GetInferenceResult(gamestate);
                    if (!TryParseAction(response, validGroups, out _, out index))
                        throw new FormatException("Input must be a valid action (e.g., 'b2', 'a1').");
                    if (index < 0 || index >= options.Count)
                        throw new FormatException($"Index out of range. Must be between 0 and {options.Count - 1}.");
                    return index; // Return the selected option index
                }
                catch (FormatException ex)
                {
                    Console.WriteLine($"Invalid input. {ex.Message} Please try again.");
                }
            }
        }

        public override CardPosition OnSelectPosition(int cardId, IList<CardPosition> positions)
        {
            var positionData = new SelectPositionData() { CardId = cardId, Positions = positions };
            var gamestate = GetGameState(GameMessage.SelectPosition, positionData: positionData);

            int index;
            var validGroups = new Dictionary<char, int> { { 'i', positions.Count } };
            while (true)
            {
                try
                {
                    string response = GetInferenceResult(gamestate);
                    if (!TryParseAction(response, validGroups, out _, out index))
                        throw new FormatException("Input must be a valid action (e.g., 'b2', 'a1').");

                    return positions[index];
                }
                catch (FormatException ex)
                {
                    Console.WriteLine($"Invalid input. {ex.Message} Please try again.");
                }
            }
        }

        public string GetInferenceResult(string arguments)
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    var content = new StringContent(arguments, Encoding.UTF8, "application/json");
                    var response = client.PostAsync("http://localhost:8000/predict", content).Result;
                    Console.WriteLine($"Status Code: {response.StatusCode}");

                    if (response.IsSuccessStatusCode)
                    {
                        string result = response.Content.ReadAsStringAsync().Result;
                        Console.WriteLine($"Raw response content: '{result}'");

                        // Try to parse as JSON
                        try
                        {
                            var jsonResponse = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(result);
                            Console.WriteLine($"Returned value: {jsonResponse["move"].Trim()}");
                            return jsonResponse["move"].Trim();
                        }
                        catch
                        {
                            // If not JSON, return as is
                            return result.Trim();
                        }
                    }
                    else
                    {
                        throw new Exception($"API request failed with status code {response.StatusCode}");
                    }
                }
            }
            catch (Exception ex)
            {
                return $"Error calling Python API: {ex.Message}";
            }
        }

        private bool TryParseAction(string response, Dictionary<char, int> validGroups, out char group, out int index)
        {
            group = ' ';
            index = -1;

            if (string.IsNullOrEmpty(response) || response.Length < 2)
                return false;

            group = response[0];
            if (!validGroups.ContainsKey(group))
                return false;

            if (!int.TryParse(response.Substring(1), out index) || index < 0)
                return false;

            if (group == 'p')
            {
                return index > 0 && index < 4; // Specific indices for phase options
            }
            else
            {
                return index < validGroups[group];
            }
        }

        // Helper method to pad arrays of card data
        private int[][] PadCardDataArray(IEnumerable<ClientCard> cards, int maxSize)
        {
            var cardArray = cards?.Select(c => GetCardDataFromKonamiCode(c.Id)).ToArray() ?? new int[0][];
            return PadCardDataArray(cardArray, maxSize);
        }

        private int[][] PadCardDataArray(IEnumerable<int> cardIds, int maxSize)
        {
            var cardArray = cardIds?.Select(id => GetCardDataFromKonamiCode(id)).ToArray() ?? new int[0][];
            return PadCardDataArray(cardArray, maxSize);
        }

        private int[][] PadCardDataArray(int[][] cardArray, int maxSize)
        {
            var result = new int[maxSize][];
            for (int i = 0; i < maxSize; i++)
            {
                if (i < cardArray.Length)
                {
                    result[i] = cardArray[i];
                }
                else
                {
                    // Pad with empty card data (array of zeros)
                    result[i] = GetCardDataFromKonamiCode(0);
                }
            }
            return result;
        }

        private object BuildOptionGroup(IList<ClientCard> cards,
                               int padTo = MAX_OPTIONS,
                               bool withLoc = false)
        {
            cards ??= Array.Empty<ClientCard>();

            // fixed-size mask (pad with zeros so every sample has the same length)
            var mask = Enumerable.Range(0, padTo)
                                  .Select(i => i < cards.Count ? 1 : 0)
                                  .ToArray();

            // align option features to mask order
            var feats = new object[padTo];
            for (int i = 0; i < padTo; i++)
            {
                if (i < cards.Count)
                {
                    feats[i] = new
                    {
                        BaseCardData = GetCardDataFromKonamiCode(cards[i].Id),
                        Location = OneHotEncodeLocation(cards[i].Location, locationMatters: withLoc)
                    };
                }
                else
                {
                    // Pad with empty data
                    feats[i] = new
                    {
                        BaseCardData = GetCardDataFromKonamiCode(0),
                        Location = OneHotEncodeLocation(CardLocation.Deck, locationMatters: withLoc)
                    };
                }
            }

            return new
            {
                Count = cards.Count,
                Mask = mask,
                Cards = feats
            };
        }


        public string GetGameState(GameMessage currentGameState,
            SelectCardData cardData = null, SelectChainData chainData = null,
            SelectYesNoData yesNoData = null, SelectEffectYesNoData effectYesNoData = null,
            SelectPositionData positionData = null, SelectOptionData optionData = null)
        {
            var bot = Util.Bot;
            var enemy = Util.Enemy;

            var gameStateInput = new
            {
                DuelMetadata = new
                {
                    TurnNumber = GetNormalizedValue(Duel.Turn, 100),
                    Phase = GetOneHotPhase(Duel.Phase),
                    CurrentTurnPlayer = GetBoolAsIntValue(IsBotsTurn()),
                    GameState = GetOneHotGameMessage(currentGameState),
                    Duel.LastSummonPlayer,
                    SummoningCards = PadCardDataArray(Duel.SummoningCards, MAX_SUMMONING_CARDS),
                    LastSummonedCards = PadCardDataArray(Duel.LastSummonedCards, MAX_LAST_SUMMONED_CARDS),
                    ActivatedCardsThisTurn = cardsActivatedThisTurn,
                },
                Bot = new
                {
                    Lifepoints = GetNormalizedValue(bot.LifePoints, 8000),
                    UnderAttack = GetBoolAsIntValue(bot.UnderAttack),
                    Deck = new
                    {
                        CardsInDeck = GetNormalizedValue(bot.Deck.Count, 100),
                        Cards = PadCardDataArray(currentDecklist, MAX_DECK_SIZE)
                    },
                    Hand = new
                    {
                        CardsInHand = GetNormalizedValue(bot.Hand.Count, 100),
                        Cards = PadCardDataArray(bot.Hand, MAX_HAND_SIZE)
                    },
                    Field = new
                    {
                        MonsterZone = Enumerable.Range(0, 5).Select(i => new
                        {
                            Occupied = GetBoolAsIntValue(bot.MonsterZone[i] != null),
                            BaseCardData = GetCardDataFromKonamiCode(bot.MonsterZone[i]?.Id ?? 0),
                            CurrAttack = GetNormalizedValue(bot.MonsterZone[i]?.Attack ?? 0, 8000),
                            CurrDefense = GetNormalizedValue(bot.MonsterZone[i]?.Defense ?? 0, 8000),
                            IsFaceup = GetBoolAsIntValue(bot.MonsterZone[i]?.IsFaceup() ?? false),
                            Position = OneHotEncodePosition((int)(bot.MonsterZone[i]?.Position ?? 0)),
                            Battling = GetBoolAsIntValue(Bot.BattlingMonster == bot.MonsterZone[i]),
                            Attacked = GetBoolAsIntValue(bot.MonsterZone[i]?.Attacked ?? false),
                            Owner = bot.MonsterZone[i]?.Owner ?? 0,
                            Controller = bot.MonsterZone[i]?.Controller ?? 0,
                            EquipCards = bot.MonsterZone[i]?.EquipCards.Select(c => c.Id).ToArray() ?? new int[0],
                        }),
                        Backrow = Enumerable.Range(0, 5).Select(i => new
                        {
                            Occupied = GetBoolAsIntValue(bot.SpellZone[i] != null),
                            BaseCardData = GetCardDataFromKonamiCode(bot.SpellZone[i]?.Id ?? 0),
                            IsFaceup = GetBoolAsIntValue(bot.SpellZone[i]?.IsFaceup() ?? false),
                            Position = OneHotEncodePosition((int)(bot.SpellZone[i]?.Position ?? 0)),
                            Owner = bot.SpellZone[i]?.Owner ?? 0,
                            Controller = bot.SpellZone[i]?.Controller ?? 0
                        })
                    },
                    Graveyard = new
                    {
                        CardsInGrave = GetNormalizedValue(bot.Graveyard.Count, 100),
                        Cards = PadCardDataArray(bot.Graveyard, MAX_GRAVEYARD_SIZE)
                    },
                    Banished = new
                    {
                        CardsBanished = GetNormalizedValue(bot.Banished.Count, 100),
                        Cards = PadCardDataArray(bot.Banished, MAX_BANISHED_SIZE)
                    },
                },
                Enemy = new
                {
                    Lifepoints = GetNormalizedValue(enemy.LifePoints, 8000),
                    UnderAttack = GetBoolAsIntValue(enemy.UnderAttack),
                    Deck = new
                    {
                        CardsInDeck = GetNormalizedValue(enemy.Deck.Count, 100),
                    },
                    Hand = new
                    {
                        CardsInHands = GetNormalizedValue(enemy.Hand.Count, 100),
                    },
                    Field = new
                    {
                        MonsterZone = Enumerable.Range(0, 5).Select(i => new
                        {
                            Occupied = GetBoolAsIntValue(enemy.MonsterZone[i] != null),
                            BaseCardData = GetCardDataFromKonamiCode(enemy.MonsterZone[i]?.Id ?? 0),
                            CurrAttack = GetNormalizedValue(enemy.MonsterZone[i]?.Attack ?? 0, 8000),
                            CurrDefense = GetNormalizedValue(enemy.MonsterZone[i]?.Defense ?? 0, 8000),
                            IsFaceup = GetBoolAsIntValue(enemy.MonsterZone[i]?.IsFaceup() ?? false),
                            Position = OneHotEncodePosition((int)(enemy.MonsterZone[i]?.Position ?? 0)),
                            Battling = GetBoolAsIntValue(Bot.BattlingMonster == enemy.MonsterZone[i]),
                            Attacked = GetBoolAsIntValue(enemy.MonsterZone[i]?.Attacked ?? false),
                            Owner = enemy.MonsterZone[i]?.Owner ?? 0,
                            Controller = enemy.MonsterZone[i]?.Controller ?? 0,
                            EquipCards = enemy.MonsterZone[i]?.EquipCards.Select(c => c.Id).ToArray() ?? new int[0],
                        }),
                        Backrow = Enumerable.Range(0, 5).Select(i => new
                        {
                            Occupied = GetBoolAsIntValue(enemy.SpellZone[i] != null),
                            BaseCardData = GetCardDataFromKonamiCode(enemy.SpellZone[i]?.Id ?? 0),
                            IsFaceup = GetBoolAsIntValue(enemy.SpellZone[i]?.IsFaceup() ?? false),
                            Position = OneHotEncodePosition((int)(enemy.SpellZone[i]?.Position ?? 0)),
                            Owner = enemy.SpellZone[i]?.Owner ?? 0,
                            Controller = enemy.SpellZone[i]?.Controller ?? 0
                        })
                    },
                    Graveyard = new
                    {
                        CardsInGrave = GetNormalizedValue(enemy.Graveyard.Count, 100),
                        Cards = PadCardDataArray(enemy.Graveyard, MAX_GRAVEYARD_SIZE)
                    },
                    Banished = new
                    {
                        CardsBanished = GetNormalizedValue(enemy.Banished.Count, 100),
                        Cards = PadCardDataArray(enemy.Banished, MAX_BANISHED_SIZE)
                    },
                },
                CurrentChain = new
                {
                    ChainCount = GetNormalizedValue(Duel.CurrentChain.Count, 20),
                    Duel.LastChainPlayer,
                    CurrentChain = PadCardDataArray(Duel.CurrentChain, MAX_CURRENT_CHAIN),
                    ChainTargets = PadCardDataArray(Duel.ChainTargets, MAX_CHAIN_TARGETS),
                    ChainTargetOnly = PadCardDataArray(Duel.ChainTargetOnly, MAX_CHAIN_TARGET_ONLY)
                },
                AvailableOptions = new
                {
                    NormalSummon = BuildOptionGroup(Main?.SummonableCards),
                    SetMonster = BuildOptionGroup(Main?.MonsterSetableCards),
                    SpecialSummon = BuildOptionGroup(Main?.SpecialSummonableCards),
                    Activate = BuildOptionGroup(Main?.ActivableCards ?? Battle?.ActivableCards, withLoc: true),
                    Repos = BuildOptionGroup(Main?.ReposableCards),
                    SetSpell = BuildOptionGroup(Main?.SpellSetableCards),
                    Attack = BuildOptionGroup(Battle?.AttackableCards),
                    ChangePhase = new
                    {
                        CanBattlePhase = Main?.CanBattlePhase ?? false,
                        CanMainPhaseTwo = Battle?.CanMainPhaseTwo ?? false,
                        CanEndPhase = CanEndPhase()
                    },
                    CardData = new
                    {
                        Count = cardData?.Cards.Count ?? 0,
                        Options = PadCardSelectionOptions(cardData?.Cards, MAX_CARD_SELECTION_OPTIONS),
                        Min = cardData?.Min ?? 0,
                        Max = cardData?.Max ?? 0,
                        Hint = cardData?.Hint ?? 0,
                        Cancelable = cardData?.Cancelable ?? false
                    },
                    ChainData = new
                    {
                        Count = chainData?.Cards.Count ?? 0,
                        Options = PadChainSelectionOptions(chainData?.Cards, MAX_CHAIN_SELECTION_OPTIONS),
                        Forced = chainData?.Forced ?? false
                    },
                    YesNoData = new
                    {
                        Desc = yesNoData?.Desc ?? -1
                    },
                    EffectYesNoData = new
                    {
                        CardId = GetCardDataFromKonamiCode(effectYesNoData?.Card?.Id ?? -1),
                        Desc = effectYesNoData?.Desc ?? -1
                    },
                    OptionData = new
                    {
                        Count = optionData?.Options.Count ?? 0,
                        Options = optionData?.Options?.ToArray() ?? new long[5] { 0, 0, 0, 0, 0 },
                    },
                    PositionData = new
                    {
                        CardId = GetCardDataFromKonamiCode(positionData?.CardId ?? -1),
                        Positions = PadPositionArray(positionData?.Positions, 5) // Assuming max 5 position options
                    }
                }
            };


            string json = JsonConvert.SerializeObject(gameStateInput, Formatting.Indented);
            Console.WriteLine(json);

            return json;
        }

        // Helper method to pad card selection options
        private object[] PadCardSelectionOptions(IList<ClientCard> cards, int maxSize)
        {
            var result = new object[maxSize];
            for (int i = 0; i < maxSize; i++)
            {
                if (cards != null && i < cards.Count)
                {
                    result[i] = new
                    {
                        Index = i,
                        BaseCardData = GetCardDataFromKonamiCode(cards[i].Id),
                        Location = (int)cards[i].Location,
                    };
                }
                else
                {
                    result[i] = new
                    {
                        Index = -1,
                        BaseCardData = GetCardDataFromKonamiCode(0),
                        Location = -1,
                    };
                }
            }
            return result;
        }

        // Helper method to pad chain selection options
        private object[] PadChainSelectionOptions(IList<ClientCard> cards, int maxSize)
        {
            var result = new object[maxSize];
            for (int i = 0; i < maxSize; i++)
            {
                if (cards != null && i < cards.Count)
                {
                    result[i] = new
                    {
                        Index = i,
                        BaseCardData = GetCardDataFromKonamiCode(cards[i].Id)
                    };
                }
                else
                {
                    result[i] = new
                    {
                        Index = -1,
                        BaseCardData = GetCardDataFromKonamiCode(0)
                    };
                }
            }
            return result;
        }

        // Helper method to pad position arrays
        private int[] PadPositionArray(IList<CardPosition> positions, int maxSize)
        {
            var result = new int[maxSize];
            for (int i = 0; i < maxSize; i++)
            {
                if (positions != null && i < positions.Count)
                {
                    result[i] = (int)positions[i];
                }
                else
                {
                    result[i] = -1; // Use -1 to indicate empty position
                }
            }
            return result;
        }

        private bool IsBotsTurn()
        {
            // It's the bots turn if the turn number is odd and it went first, or if the turn number is even and it went second
            return Duel.IsFirst ? (Duel.Turn % 2 == 1) : (Duel.Turn % 2 == 0);
        }

        private bool CanEndPhase()
        {
            return (Main != null && Main.CanEndPhase) || (Battle != null && Battle.CanEndPhase);
        }

        private string GetDeckName()
        {
            var attribute = (DeckAttribute)Attribute.GetCustomAttribute(typeof(ExodAIExecutor), typeof(DeckAttribute));
            return attribute?.File ?? "DefaultDeckName";
        }

        private int[] GetCardDataFromKonamiCode(int konamiCode)
        {
            // The Konami‑codes for each of your 19 cards, in deck order:
            int[] codes = new int[]
            {
                70781052,  // Summoned Skull
                97590747,  // La Jinn the Mystical Genie of the Lamp :contentReference[oaicite:0]{index=0}
                5053103,   // Battle Ox
                50930991,  // Neo the Magic Swordsman :contentReference[oaicite:1]{index=1}
                13945283,  // Wall of Illusion :contentReference[oaicite:2]{index=2}
                46461247,  // Trap Master :contentReference[oaicite:3]{index=3}
                54652250,  // Man‑Eater Bug
                4031928,   // Change of Heart
                12580477,  // Raigeki
                19159413,  // De‑Spell
                53129443,  // Dark Hole
                55144522,  // Pot of Greed
                66788016,  // Fissure
                15735108,  // Soul Exchange :contentReference[oaicite:4]{index=4}
                72302403,  // Swords of Revealing Light
                83764718,  // Monster Reborn
                4206964,   // Trap Hole
                12607053,  // Waboku :contentReference[oaicite:5]{index=5}
                17814387   // Reinforcements :contentReference[oaicite:6]{index=6}
            };

            var oneHot = new int[codes.Length];
            for (int i = 0; i < codes.Length; i++)
            {
                if (codes[i] == konamiCode)
                {
                    oneHot[i] = 1;
                    break;
                }
            }

            return oneHot;

            // Use when eventually migrating to embedding the effects
            //if (baseCardData.ContainsKey(konamiCode))
            //{
            //    return new BaseCardDataDTO(baseCardData[konamiCode]);
            //}

            //return null;
        }

        private int[] OneHotEncodeLocation(CardLocation location, bool locationMatters = true)
        {
            // Order: Deck, Hand, MonsterZone, SpellZone, Grave
            int[] oneHot = new int[5];

            if (!locationMatters)
            {
                return oneHot;
            }

            if ((location & CardLocation.Deck) != 0) oneHot[0] = 1;
            else if ((location & CardLocation.Hand) != 0) oneHot[1] = 1;
            else if ((location & CardLocation.MonsterZone) != 0) oneHot[2] = 1;
            else if ((location & CardLocation.SpellZone) != 0) oneHot[3] = 1;
            else if ((location & CardLocation.Grave) != 0) oneHot[4] = 1;
            // If none of the tracked bits are present, array stays all-zero.

            return oneHot;
        }


        private int[] GetOneHotPhase(DuelPhase phase)
        {
            DuelPhase[] phaseOrder = new DuelPhase[]
            {
                DuelPhase.Draw,
                DuelPhase.Standby,
                DuelPhase.Main1,
                DuelPhase.BattleStart,
                DuelPhase.BattleStep,
                DuelPhase.Damage,
                DuelPhase.DamageCal,
                DuelPhase.Battle,
                DuelPhase.Main2,
                DuelPhase.End
            };

            int[] oneHot = new int[phaseOrder.Length];

            for (int i = 0; i < phaseOrder.Length; i++)
            {
                if (phase == phaseOrder[i])
                {
                    oneHot[i] = 1;
                    break;
                }
            }

            return oneHot;
        }

        private int[] GetOneHotGameMessage(GameMessage message)
        {
            GameMessage[] usedMessages = new GameMessage[]
            {
                GameMessage.SelectIdleCmd,
                GameMessage.SelectBattleCmd,
                GameMessage.SelectCard,
                GameMessage.SelectYesNo,
                GameMessage.SelectEffectYn,
                GameMessage.SelectChain,
                GameMessage.SelectPosition
            };

            int[] oneHot = new int[usedMessages.Length];

            for (int i = 0; i < usedMessages.Length; i++)
            {
                if (message == usedMessages[i])
                {
                    oneHot[i] = 1;
                    break;
                }
            }

            return oneHot;
        }

        private static int[] OneHotEncodePosition(int positionCode)
        {
            // Position codes: 1 = face‑up attack, 4 = face‑up defense, 8 = face‑down defense, 5 = face-up backrow, 10 face-down backrow
            int[] codes = new int[] { 1, 4, 8, 5, 10 };

            int[] oneHot = new int[codes.Length];
            for (int i = 0; i < codes.Length; i++)
            {
                if (codes[i] == positionCode)
                {
                    oneHot[i] = 1;
                    break;
                }
            }

            return oneHot;
        }


        private float GetNormalizedValue(int value, int divider)
        {
            return (float)value / divider;
        }

        private int GetBoolAsIntValue(bool value)
        {
            return value ? 1 : 0;
        }

    }
}
