using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Entities;
using Util.Commands;

namespace LandingFromSpace
{
    /// <summary>
    /// Comando de console "respawn &lt;jogador&gt;": no proximo respawn desse jogador ele recebe de novo o kit inicial
    /// (o lander). So roda no servidor: no console do servidor dedicado, no F3 do host ou, de um cliente, por
    /// "serverrun respawn &lt;jogador&gt;" (que o jogo so aceita com o ServerAuthSecret do servidor, ou seja, admin).
    /// </summary>
    public sealed class RespawnCommand : CommandBase
    {
        public const string Key = "respawn";

        public override string HelpText => "Landing From Space: gives <player> the starting lander again on their next respawn. Server only (from a client: serverrun respawn <player>). 'respawn list' shows who is marked; 'respawn cancel <player>' removes the mark.";

        public override string[] Arguments => new[] { "<player>|list|cancel <player>" };

        public override bool IsLaunchCmd => false;

        public override string Execute(string[] args)
        {
            if (NetworkManager.IsClient)
            {
                return "only the server can run this. As an admin, use: serverrun respawn <player>";
            }
            if (!GameManager.RunSimulation || GameManager.GameState != GameState.Running)
            {
                return "only available inside a running world.";
            }
            if (args.Length == 0)
            {
                return "Invalid syntax";
            }
            if (args.Length == 1 && args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                List<LandingPending> marked = LandingLog.Pending;
                return marked.Count == 0 ? "nobody is marked." : "marked for a new lander: " + string.Join(", ", marked.Select(p => p.Name));
            }
            bool cancel = args[0].Equals("cancel", StringComparison.OrdinalIgnoreCase);
            string query = string.Join(" ", cancel ? args.Skip(1) : args).Trim();
            if (query.Length == 0)
            {
                return "Invalid syntax";
            }

            if (cancel)
            {
                return LandingLog.CancelPending(query) ? $"'{query}' will no longer get a new lander." : $"'{query}' was not marked.";
            }

            // Jogador conectado (nome, parte do nome ou SteamID); senao, guarda o nome para quando ele voltar.
            List<Human> matches = FindPlayers(query);
            if (matches.Count > 1)
            {
                return "more than one player matches: " + string.Join(", ", matches.Select(h => h.DisplayName)) + ". Use the full name or the SteamID.";
            }
            if (matches.Count == 1)
            {
                Human human = matches[0];
                LandingLog.MarkPending(human.OwnerClientId, human.DisplayName);
                return $"{human.DisplayName} will get the starting lander again on the next respawn.";
            }
            if (ulong.TryParse(query, out ulong steamId))
            {
                LandingLog.MarkPending(steamId, query);
                return $"player {steamId} is not here now; they will get the starting lander on their next respawn.";
            }
            LandingLog.MarkPending(0, query);
            return $"'{query}' is not here now; they will get the starting lander on their next respawn (matched by name).";
        }

        private static List<Human> FindPlayers(string query)
        {
            List<Human> humans = Human.AllHumans.Where(h => h != null && h.OrganBrain != null && !string.IsNullOrEmpty(h.DisplayName)).ToList();
            List<Human> exact = humans.Where(h => h.DisplayName.Equals(query, StringComparison.OrdinalIgnoreCase) || h.OwnerClientId.ToString() == query).ToList();
            if (exact.Count > 0)
            {
                return exact;
            }
            return humans.Where(h => h.DisplayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }

        public override IEnumerable<string> GetCompletions(int argIndex, string prefix)
        {
            return Human.AllHumans.Where(h => h != null && !string.IsNullOrEmpty(h.DisplayName))
                .Select(h => h.DisplayName)
                .Where(n => n.StartsWith(prefix ?? "", StringComparison.OrdinalIgnoreCase))
                .Distinct();
        }
    }
}
