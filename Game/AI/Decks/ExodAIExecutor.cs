using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WindBot.Game.AI.Decks
{

    [Deck("ExodAI", "AI_Yugi_Kaiba_Beat", "Easy")]
    public class ExodAIExecutor : Executor
    {
        private int[] gameState;

        public ExodAIExecutor(GameAI ai, Duel duel) : base(ai, duel)
        {
            Console.WriteLine("ExodAIExecutor");
            gameState = new int[500];
            if(duel.Player == 0)
            {
                Console.WriteLine("Player 0");
            }
            else
            {
                Console.WriteLine("Player 1");
            }
        }

        public override MainPhaseAction GenerateMainPhaseAction()
        {
            //Duel.Fields[0];


            return null;
        }

        public int[] GetGameState()
        {
            // Duel Metadata
            gameState[0] = Duel.Turn; // Current turn number
            gameState[1] = (Duel.Turn % 2 == (Duel.IsFirst ? 1 : 0)) ? 0 : 1; // Current turn player. 0 for bot, 1 for enemy
            gameState[2] = (int)Duel.Phase; // Current phase
            gameState[3] = 0;//(int)CurrentDecision; // Current decision
            gameState[4] = 0;//(int)MasterRule; // Master Rule

            // Bot
            var bot = Duel.Fields[0];
            gameState[5] = bot.LifePoints; // Bot life points
            gameState[6] = bot.Deck.Count; // Bot deck count
            gameState[7] = 0; // has normal summoned
            gameState[8] = 0; // has pendulum summoned
            gameState[9] = 0; // Special summon count



            return gameState;
        }
    }
}
