using YGOSharp.OCGWrapper.Enums;
using System.Collections.Generic;
using WindBot;
using WindBot.Game;
using WindBot.Game.AI;
using System;
using System.Text;
using System.Linq;

namespace WindBot.Game.AI.Decks
{
    [Deck("Basic", "AI_Yugi_Kaiba_Beat", "Easy")]
    public class ManualExecutor : Executor
    {

        public List<int> masterDecklist;
        public List<int> currentDecklist;

        private bool pendingCardDraw;
        private int cardsDrawn;

        public ManualExecutor(GameAI ai, Duel duel) : base(ai, duel)
        {
            Console.WriteLine("Using ManualExecutor");
            if (duel.Player == 0)
            {
                Console.WriteLine("Player 0");
            }
            else
            {
                Console.WriteLine("Player 1");
            }
            var deck = WindBot.Game.Deck.Load(GetDeckName());

            masterDecklist = deck.Cards.ToList();
            currentDecklist = deck.Cards.ToList();
        }

        public override void OnStartDuel()
        {
            currentDecklist = masterDecklist.ToList();
        }

        private string GetDeckName()
        {
            var attribute = (DeckAttribute)Attribute.GetCustomAttribute(typeof(ManualExecutor), typeof(DeckAttribute));
            return attribute?.File ?? "DefaultDeckName";
        }

        public override MainPhaseAction GenerateMainPhaseAction()
        {
            if(Duel.Player == 0)
            {
                Console.WriteLine("I'm player 0");
            }
            else
            {
                Console.WriteLine("I'm player 1");
            }
            Console.WriteLine(LogMainPhaseOptions());
            char group;
            int index;

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

            while (true)
            {
                try
                {
                    string response = GetUserInput("Enter action: ");

                    if (!TryParseAction(response, validGroups, out group, out index))
                        throw new FormatException("Input must be a valid action (e.g., 'n2', 'd1').");

                    if (group == 'p' && (index < 1 || index > 2))
                        throw new FormatException("Phase options are 1 for Battle Phase, 2 for End Phase.");

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

        public override void OnDraw(int player, int count)
        {
            Console.WriteLine($"Player {player} drew {count} cards");
            if(player != 0)
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
                var drawnCards = currentDecklist.Skip(currentDecklist.Count - cardsDrawn).ToList();

                foreach (var card in drawnCards)
                {
                    Console.WriteLine($"Card {card} was drawn");
                    currentDecklist.Remove(card);
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

        public override BattlePhaseAction GenerateBattlePhaseAction()
        {
            Console.WriteLine(LogBattlePhaseOptions());
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
                    string response = GetUserInput("Enter action: ");

                    if (!TryParseAction(response, validGroups, out group, out index))
                        throw new FormatException("Input must be a valid action (e.g., 'b2', 'a1').");

                    if (group == 'p' && (index < 1 || index > 2))
                        throw new FormatException("Phase options are 1 for Main Phase 2, 2 for End Phase.");

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
                return index == 1 || index == 2; // Specific indices for phase options
            }
            else
            {
                return index < validGroups[group];
            }
        }

        private string GetUserInput(string prompt)
        {
            while (true)
            {
                Console.Write(prompt);
                string response = Console.ReadLine();

                // Process commands
                if (response.Equals("board", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(LogGameState());
                    continue; // Re-prompt
                }
                else if (response.Equals("help", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Available commands:");
                    Console.WriteLine("- 'board': Display the current game state.");
                    // Add other commands as needed
                    continue; // Re-prompt
                }

                return response;
            }
        }

        public override IList<ClientCard> OnSelectCard(IList<ClientCard> cards, int min, int max, long hint, bool cancelable)
        {
            Console.WriteLine("Selection Reason: " + HintMsg.GetHintName(hint));

            for (int i = 0; i < cards.Count; i++)
                Console.WriteLine($"{i} - {cards[i].Name}");

            IList<ClientCard> selectedCards = new List<ClientCard>();

            while (true)
            {
                try
                {
                    string response = GetUserInput($"Please select between {min} and {max} cards by entering the corresponding indices (comma-separated): ");

                    if (!IsValidCardSelection(response, cards.Count, min, max, out IList<int> selectedIndices))
                        throw new FormatException("Invalid selection. Please enter a valid comma-separated list of indices.");

                    foreach (var i in selectedIndices)
                        selectedCards.Add(cards[i]);

                    break;
                }
                catch (FormatException ex)
                {
                    Console.WriteLine($"{ex.Message} Please try again.");
                }
            }

            return selectedCards;
        }

        private bool IsValidCardSelection(string response, int totalCards, int min, int max, out IList<int> selectedIndices)
        {
            selectedIndices = new List<int>();

            string[] parts = response.Split(',');

            if (parts.Length < min || parts.Length > max)
                return false;

            foreach (var part in parts)
            {
                if (int.TryParse(part.Trim(), out int index) && index >= 0 && index < totalCards)
                {
                    if (!selectedIndices.Contains(index))
                        selectedIndices.Add(index);
                }
                else
                {
                    return false;
                }
            }

            return true;
        }

        public override bool OnSelectYesNo(long desc)
        {
            Console.WriteLine("Select Yes or No for the following description: " + desc);

            while (true)
            {
                string response = GetUserInput("Enter 'y' for Yes or 'n' for No: ");
                if (response.Equals("y", StringComparison.OrdinalIgnoreCase))
                    return true;
                else if (response.Equals("n", StringComparison.OrdinalIgnoreCase))
                    return false;
                else
                    Console.WriteLine("Invalid input. Please enter 'y' for Yes or 'n' for No.");
            }
        }

        public override bool OnSelectEffectYesNo(ClientCard card, long desc)
        {
            Console.WriteLine("Select Yes or No to activate the effect with the following description: " + desc);

            while (true)
            {
                string response = GetUserInput("Enter 'y' for Yes or 'n' for No: ");
                if (response.Equals("y", StringComparison.OrdinalIgnoreCase))
                    return true;
                else if (response.Equals("n", StringComparison.OrdinalIgnoreCase))
                    return false;
                else
                    Console.WriteLine("Invalid input. Please enter 'y' for Yes or 'n' for No.");
            }
        }

        public override int OnSelectOption(IList<long> options)
        {
            Console.WriteLine("Select an option from the following list:");
            for (int i = 0; i < options.Count; i++)
            {
                Console.WriteLine($"{i} - {options[i]}");
            }
            Console.WriteLine("Enter the index of the option you want to select.");
            while (true)
            {
                try
                {
                    string response = GetUserInput("Index: ");
                    int index = int.Parse(response);
                    if (index >= 0 && index < options.Count)
                        return index;
                    else
                        Console.WriteLine("Invalid index. Please try again.");
                }
                catch (FormatException)
                {
                    Console.WriteLine("Invalid input. Please enter a valid number.");
                }
            }
        }

        public override int OnSelectChain(IList<ClientCard> cards, bool forced)
        {
            Console.WriteLine("Chain Card?");

            for (int i = 0; i < cards.Count; i++)
                Console.WriteLine($"{i} - {cards[i].Name}");

            Console.WriteLine("Enter the index of the card you want to chain. Enter -1 to pass.");

            while (true)
            {
                try
                {
                    string response = GetUserInput("Index: ");
                    int index = int.Parse(response);

                    if ((index >= 0 && index < cards.Count) || index == -1)
                        return index;
                    else
                        Console.WriteLine("Invalid index. Please try again.");
                }
                catch (FormatException)
                {
                    Console.WriteLine("Invalid input. Please enter a valid number.");
                }
            }
        }

        public override CardPosition OnSelectPosition(int cardId, IList<CardPosition> positions)
        {
            Console.WriteLine("Select the position from the available options for the card with ID " + cardId);
            int index = 0;
            foreach (CardPosition pos in positions)
            {
                Console.WriteLine($"{index++} - {pos}");
            }

            while (true)
            {
                try
                {
                    string response = GetUserInput("Position: ");
                    int posIndex = int.Parse(response);

                    if (posIndex >= 0 && posIndex < positions.Count)
                        return positions[posIndex];
                    else
                        Console.WriteLine("Invalid index. Please try again.");
                }
                catch (FormatException)
                {
                    Console.WriteLine("Invalid input. Please enter a valid number.");
                }
            }
        }

        private MainPhaseAction GenerateMainPhaseChangeAction(int index)
        {
            if (index == 1 && Main.CanBattlePhase)
                return new MainPhaseAction(MainPhaseAction.MainAction.ToBattlePhase);
            else if (index == 2 && Main.CanEndPhase)
                return new MainPhaseAction(MainPhaseAction.MainAction.ToEndPhase);
            else
            {
                Console.WriteLine("You cannot move to that phase at this time.");
                return GenerateMainPhaseAction(); // Re-prompt for valid action
            }
        }

        private BattlePhaseAction GenerateBattlePhaseChangeAction(int index)
        {
            if (index == 1 && Battle.CanMainPhaseTwo)
                return new BattlePhaseAction(BattlePhaseAction.BattleAction.ToMainPhaseTwo);
            else if (index == 2 && Battle.CanEndPhase)
                return new BattlePhaseAction(BattlePhaseAction.BattleAction.ToEndPhase);
            else
            {
                Console.WriteLine("You cannot move to that phase at this time.");
                return GenerateBattlePhaseAction(); // Re-prompt for valid action
            }
        }

        public string LogMainPhaseOptions()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"Turn {Duel.Turn}, Phase {Duel.Phase}:");
            int turnPlayer = (Duel.Turn % 2 == (Duel.IsFirst ? 1 : 0)) ? 0 : 1;
            sb.AppendLine($"Player {turnPlayer} ({(turnPlayer == 0 ? "Bot" : "Enemy")})");
            sb.AppendLine("Cards in hand:");
            foreach (var card in Bot.Hand)
                sb.AppendLine($"- {card.Name}");

            sb.AppendLine("Normal Summon Options (n):");
            for (int i = 0; i < Main.SummonableCards.Count; i++)
                sb.AppendLine($"{i} - {Main.SummonableCards[i].Name}");

            sb.AppendLine("Set Options (d):");
            for (int i = 0; i < Main.MonsterSetableCards.Count; i++)
                sb.AppendLine($"{i} - {Main.MonsterSetableCards[i].Name}");

            sb.AppendLine("Special Summon Options (x):");
            for (int i = 0; i < Main.SpecialSummonableCards.Count; i++)
                sb.AppendLine($"{i} - {Main.SpecialSummonableCards[i].Name}");

            sb.AppendLine("Activate Options (a):");
            for (int i = 0; i < Main.ActivableCards.Count; i++)
                sb.AppendLine($"{i} - {Main.ActivableCards[i].Name + " " + Main.ActivableDescs[i]}");

            sb.AppendLine("Spell Set Options (s):");
            for (int i = 0; i < Main.SpellSetableCards.Count; i++)
                sb.AppendLine($"{i} - {Main.SpellSetableCards[i].Name}");

            sb.AppendLine("Reposition Options (r):");
            for (int i = 0; i < Main.ReposableCards.Count; i++)
                sb.AppendLine($"{i} - {Main.ReposableCards[i].Name}");

            sb.AppendLine("Phase Options (p):");
            sb.AppendLine($"1 - Can Battle Phase: {Main.CanBattlePhase}");
            sb.AppendLine($"2 - Can End Phase: {Main.CanEndPhase}");

            return sb.ToString();
        }

        public string LogBattlePhaseOptions()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"Turn {Duel.Turn}, Phase {Duel.Phase}:");

            sb.AppendLine("Attacker Options (b):");
            for (int i = 0; i < Battle.AttackableCards.Count; i++)
                sb.AppendLine($"{i} - {Battle.AttackableCards[i].Name}");

            sb.AppendLine("Activate Options (a):");
            for (int i = 0; i < Battle.ActivableCards.Count; i++)
                sb.AppendLine($"{i} - {Battle.ActivableCards[i].Name}");

            sb.AppendLine("Phase Options (p):");
            sb.AppendLine($"1 - Can Main2 Phase: {Battle.CanMainPhaseTwo}");
            sb.AppendLine($"2 - Can End Phase: {Battle.CanEndPhase}");

            return sb.ToString();
        }

        public string LogGameState()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Current Game State:");
            sb.AppendLine("Cards in hand:");
            foreach (var card in Bot.Hand)
                sb.AppendLine($"- {card.Name}");

            sb.AppendLine("Monsters on field:");
            foreach (var card in Bot.GetMonsters())
                if (card != null)
                    sb.AppendLine($"- {card.Name}");

            sb.AppendLine("Spells/Traps on field:");
            foreach (var card in Bot.GetSpells())
                if (card != null)
                    sb.AppendLine($"- {card.Name}");

            return sb.ToString();
        }
    }

}

