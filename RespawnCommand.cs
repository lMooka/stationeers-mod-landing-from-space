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

        public override string HelpText => "Landing From Space: on their next respawn, <player> comes back with the new player equipment and gets the starting lander again. Server only (from a client: serverrun respawn <player>). 'respawn list' shows who is marked; 'respawn cancel <player>' removes the mark.";

        public override string[] Arguments => new[] { "<player>|list|cancel <player>" };

        public override bool IsLaunchCmd => false;

        /// <summary>
        /// SteamID de quem mandou o comando por "serverrun" (preenchido por um gancho no processamento da mensagem),
        /// para a resposta voltar a essa pessoa no chat. Sem isso ela so aparece no console do servidor.
        /// </summary>
        [ThreadStatic] public static ulong? Invoker;

        public static void ServerRunPrefix(object __instance)
        {
            try
            {
                Invoker = HarmonyLib.Traverse.Create(__instance).Property("ClientId").GetValue<ulong>();
            }
            catch
            {
                Invoker = null;
            }
        }

        public static void ServerRunPostfix()
        {
            Invoker = null;
        }

        public override string Execute(string[] args)
        {
            string reply = Run(args);
            if (Invoker.HasValue && !string.IsNullOrEmpty(reply))
            {
                Tell(Invoker.Value, reply == "Invalid syntax" ? "usage: respawn <player> | respawn list | respawn cancel <player>" : reply);
            }
            return reply;
        }

        /// <summary>Resposta no chat de quem usou serverrun (o jogo sem o mod mostra como mensagem do servidor).</summary>
        private static void Tell(ulong clientId, string text)
        {
            try
            {
                Client client = Client.Find(clientId);
                if (client == null || !NetworkManager.IsServer)
                {
                    return;
                }
                NetworkServer.SendToClient(new ChatMessage
                {
                    HumanId = -1L,
                    DisplayName = "[respawn]",
                    ChatText = text
                }, NetworkChannel.GeneralTraffic, client);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not send the respawn reply: {ex.Message}");
            }
        }

        private static string Run(string[] args)
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

            // Jogador (nome, parte do nome ou SteamID). Os corpos de vidas anteriores continuam na lista de personagens
            // com o mesmo nome e SteamID, entao a busca e por jogador (SteamID), nao por personagem.
            List<KeyValuePair<ulong, string>> matches = FindPlayers(query);
            if (matches.Count > 1)
            {
                return "more than one player matches: " + string.Join(", ", matches.Select(m => $"{m.Value} ({m.Key})")) + ". Use the full name or the SteamID.";
            }
            if (matches.Count == 1)
            {
                LandingLog.MarkPending(matches[0].Key, matches[0].Value);
                return $"{matches[0].Value} will respawn with the new player equipment and the starting lander.";
            }
            if (ulong.TryParse(query, out ulong steamId))
            {
                LandingLog.MarkPending(steamId, query);
                return $"player {steamId} is not here now; they will get the starting lander on their next respawn.";
            }
            LandingLog.MarkPending(0, query);
            return $"'{query}' is not here now; they will get the starting lander on their next respawn (matched by name).";
        }

        /// <summary>Jogadores conhecidos agora: clientes conectados e personagens (vivos ou corpos), um por SteamID.</summary>
        private static Dictionary<ulong, string> KnownPlayers()
        {
            Dictionary<ulong, string> players = new Dictionary<ulong, string>();
            foreach (Human human in Human.AllHumans)
            {
                if (human != null && human.OwnerClientId != 0 && !string.IsNullOrEmpty(human.DisplayName) && !players.ContainsKey(human.OwnerClientId))
                {
                    players[human.OwnerClientId] = human.DisplayName;
                }
            }
            try
            {
                foreach (Client client in NetworkBase.Clients)
                {
                    if (client != null && client.ClientId != 0 && !string.IsNullOrEmpty(client.name))
                    {
                        players[client.ClientId] = client.name;
                    }
                }
            }
            catch
            {
                // Sem lista de clientes (singleplayer): ficam os personagens.
            }
            return players;
        }

        private static List<KeyValuePair<ulong, string>> FindPlayers(string query)
        {
            Dictionary<ulong, string> players = KnownPlayers();
            List<KeyValuePair<ulong, string>> exact = players.Where(p => p.Value.Equals(query, StringComparison.OrdinalIgnoreCase) || p.Key.ToString() == query).ToList();
            if (exact.Count > 0)
            {
                return exact;
            }
            return players.Where(p => p.Value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }

        public override IEnumerable<string> GetCompletions(int argIndex, string prefix)
        {
            return KnownPlayers().Values
                .Where(n => n.StartsWith(prefix ?? "", StringComparison.OrdinalIgnoreCase))
                .Distinct();
        }
    }
}
