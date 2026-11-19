using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.1.0-y12 (the tester, 17/11: "eu tenho certeza que a queima roupa eles
    // erram mt ainda" / "ate uns 12m da pra acertar tranquilo").
    //
    // The recordings of 15-17/11, each of the tester's files lined up with the
    // bots' by the hits (offsets -0.35 to +0.65s), rounds fired within 12m:
    // the tester hit the bots with 23 of 28 (82%), the first round of an exchange
    // 9 of 10; the bots hit him with 18 of 69 (26%).
    //
    // Two causes:
    // - The bot's aim trailed him. With him moving across its sights, 41 of
    //   53 rounds went behind him, about 0.08s of his movement behind (0.10s
    //   on a moving bot): one step of the game's input (PlayerInput.RATE,
    //   0.08s), between the position the bot reads and the look it sends.
    //   The aim now leads by one step at his speed as the server moves him.
    // - The drawn miss came from the players of 08/11: 6.4 degrees within
    //   8m, and the first sight of him 6 degrees off settling over 1.5s.
    //   the tester within 12m: a spread of 2.8 degrees (hits per round, the body
    //   0.8 x 1.85m at the chest) and no first-sight miss (9 of 10).
    //   0.1.0-y13: his spread at every range (SkaliaBotPvpFeelY).
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float PvpLeadMaxSpeed = 12f;

        // Where he will be when the look the bot sends now is applied.
        private static Vector3 PvpInputStepLead(Player target)
        {
            var velocity = target.movement.velocity;
            velocity.y = 0f;
            if (velocity.magnitude > PvpLeadMaxSpeed)
            {
                return Vector3.zero;
            }
            return velocity * PlayerInput.RATE;
        }
    }
}
