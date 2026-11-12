using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.Networking;
using UnityEngine;

namespace LandingFromSpace
{
    /// <summary>
    /// Canal escondido entre host e clientes que tem o mod, usando a mensagem Handshake do jogo.
    ///
    ///  - Host -> cliente: o Handshake vai com ConnectionId igual ao do proprio cliente. O jogo sem o mod
    ///    ignora esse caso logo na primeira linha (NetworkClient.Handshake), entao nada aparece para quem nao tem o mod.
    ///  - Cliente -> host: so e enviado depois que o host se apresentou ("H"), ou seja, o host tem o mod
    ///    e intercepta a mensagem antes do jogo. Um host sem o mod nunca recebe nada.
    ///
    /// Mensagens (campo Message, prefixo <see cref="Tag"/>):
    ///   H                     host e cliente se apresentam
    ///   P|capsula             host: escolha onde esta capsula vai cair
    ///   S|capsula             host: comecou a descida guiada (WASD desvia a capsula)
    ///   E|capsula             host: acabou (a capsula entrou no pouso final do jogo)
    ///   I|capsula|dx|dz|rapido  cliente: direcao atual (mundo) e queda rapida
    ///   T|capsula|x|z         cliente: cair neste ponto do mapa
    ///   T|capsula             cliente: cair no lugar padrao
    /// </summary>
    public static class Net
    {
        public const string Tag = "#LFS1#";

        /// <summary>Host: ClientIds dos jogadores que responderam que tem o mod.</summary>
        public static readonly HashSet<ulong> ModdedClients = new HashSet<ulong>();

        /// <summary>Cliente: o host desta sessao tem o mod.</summary>
        public static bool HostHasMod;

        private static string Num(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static float ParseNum(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        private static NetworkMessages.Handshake Build(long connectionId, string body)
        {
            return new NetworkMessages.Handshake
            {
                ConnectionId = connectionId,
                Username = string.Empty,
                ClientId = 0,
                HandshakeState = HandshakeType.None,
                LoadingState = string.Empty,
                LoadingProgress = 0,
                Message = Tag + body
            };
        }

        public static void SendToClient(Client client, string body)
        {
            try
            {
                if (client == null || !NetworkManager.IsServer)
                {
                    return;
                }
                NetworkServer.SendToClient(Build(client.connectionId, body), NetworkChannel.GeneralTraffic, client);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Failed to send to client: {ex.Message}");
            }
        }

        public static void SendToServer(string body)
        {
            try
            {
                if (!NetworkManager.IsClient || !HostHasMod)
                {
                    return;
                }
                NetworkClient.SendToServer(Build(NetworkClient.ConnectionId, body));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Failed to send to host: {ex.Message}");
            }
        }

        public static void SayHello(Client client) => SendToClient(client, "H");

        public static void SendInput(long capsuleId, Vector2 dir, bool fast)
        {
            SendToServer($"I|{capsuleId}|{Num(dir.x)}|{Num(dir.y)}|{(fast ? 1 : 0)}");
        }

        public static void SendTarget(long capsuleId, Vector2? point)
        {
            SendToServer(point.HasValue ? $"T|{capsuleId}|{Num(point.Value.x)}|{Num(point.Value.y)}" : $"T|{capsuleId}");
        }

        /// <summary>Host: um jogador terminou de entrar; apresenta o mod para ele.</summary>
        public static void ServerHandshakePostfix(NetworkMessages.Handshake __0)
        {
            try
            {
                if (__0 != null && __0.HandshakeState == HandshakeType.ClientReady && Plugin.Enabled.Value)
                {
                    SayHello(Client.Find(__0.ConnectionId));
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Hello failed: {ex.Message}");
            }
        }

        /// <summary>Intercepta as mensagens do mod (nos dois lados) antes de o jogo processar o Handshake.</summary>
        public static bool HandshakeProcessPrefix(NetworkMessages.Handshake __instance, long hostId)
        {
            string message = __instance?.Message;
            if (message == null || !message.StartsWith(Tag, StringComparison.Ordinal))
            {
                return true;
            }
            try
            {
                string[] parts = message.Substring(Tag.Length).Split('|');
                if (NetworkManager.IsClient)
                {
                    OnClientMessage(parts);
                }
                else if (NetworkManager.IsServer)
                {
                    Client client = Client.Find(hostId) ?? Client.Find(__instance.ConnectionId);
                    if (client != null)
                    {
                        OnServerMessage(client, parts);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Bad mod message '{message}': {ex.Message}");
            }
            return false;
        }

        private static void OnClientMessage(string[] parts)
        {
            switch (parts[0])
            {
                case "H":
                    if (!Plugin.Enabled.Value)
                    {
                        return;
                    }
                    HostHasMod = true;
                    SendToServer("H");
                    break;
                case "P":
                    HostHasMod = true;
                    MapPicker.OpenRemote(long.Parse(parts[1], CultureInfo.InvariantCulture));
                    break;
                case "S":
                    SteerPilot.BeginRemote(long.Parse(parts[1], CultureInfo.InvariantCulture));
                    break;
                case "E":
                    MapPicker.Close(long.Parse(parts[1], CultureInfo.InvariantCulture));
                    SteerPilot.End(long.Parse(parts[1], CultureInfo.InvariantCulture));
                    break;
            }
        }

        private static void OnServerMessage(Client client, string[] parts)
        {
            switch (parts[0])
            {
                case "H":
                    if (ModdedClients.Add(client.ClientId))
                    {
                        Plugin.Log.LogInfo($"Player {client.name} ({client.ClientId}) has the mod.");
                    }
                    break;
                case "I":
                    ServerPilot.SetInput(long.Parse(parts[1], CultureInfo.InvariantCulture), client.ClientId,
                        new Vector2(ParseNum(parts[2]), ParseNum(parts[3])), parts.Length > 4 && parts[4] == "1");
                    break;
                case "T":
                    ServerPilot.RequestTarget(long.Parse(parts[1], CultureInfo.InvariantCulture), client.ClientId,
                        parts.Length >= 4 ? new Vector2(ParseNum(parts[2]), ParseNum(parts[3])) : (Vector2?)null);
                    break;
            }
        }
    }
}
