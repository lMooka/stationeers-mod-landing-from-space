using System;
using System.Collections;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using UnityEngine;

namespace LandingFromSpace
{
    /// <summary>
    /// Kit inicial (lander, caixa do modo brutal, tanques...): o que a condicao de inicio cria fora da capsula.
    ///  - Kit do mundo: criado no evento NewWorld (condicao padrao). Vai com a primeira capsula do mundo,
    ///    se ela for pilotada; se o primeiro jogador nao tiver o mod, fica onde o jogo colocou.
    ///  - Kit do jogador: criado nos eventos NewPlayer/RespawnPlayer (ex.: DefaultStartCommunity da um lander
    ///    para cada jogador novo). Vai com a capsula do proprio jogador, se ele pilotar.
    /// O kit cai do ceu perto da capsula quando o dono sai dela.
    /// </summary>
    public static class Kit
    {
        private sealed class Entry
        {
            public readonly List<long> Ids = new List<long>();
            public string Source;
        }

        private enum Kind { None, World, Player }

        private static Entry _worldKit;                                                                  // ate a primeira capsula do mundo decidir
        private static readonly Dictionary<long, Entry> PlayerKits = new Dictionary<long, Entry>();         // por jogador (ReferenceId)
        private static readonly Dictionary<long, List<Entry>> Claims = new Dictionary<long, List<Entry>>(); // por capsula

        private static HashSet<long> _before;
        private static Kind _kind;
        private static Human _player;

        private static readonly List<long> LastClaimed = new List<long>();
        private static long _lastClaimedBy;

        /// <summary>Distancia minima entre a capsula e qualquer item do kit depois de mudar de lugar.</summary>
        private const float Clearance = 9f;

        /// <summary>Somente para o autoteste: itens do ultimo kit reservado.</summary>
        public static IReadOnlyList<long> Things => LastClaimed;
        public static bool IsClaimed => Claims.Count > 0;
        public static long ClaimedBy => _lastClaimedBy;
        public static string LastSources { get; private set; } = "";

        /// <summary>Saiu do mundo: os kits pendentes pertencem a ele e nao valem para o proximo.</summary>
        public static void Reset()
        {
            _worldKit = null;
            PlayerKits.Clear();
            Claims.Clear();
            LastClaimed.Clear();
            _lastClaimedBy = 0;
            _before = null;
            _kind = Kind.None;
            _player = null;
        }

        private static Kind KindOf(SpawnData spawn, Thing parent, Human player)
        {
            if ((object)parent != null || spawn == null)
            {
                return Kind.None;
            }
            bool listed = Contains(_spawningWorld?.StartConditionData, spawn) || Contains(WorldSetting.Current?.StartConditionData, spawn);
            if (!listed)
            {
                return Kind.None;
            }
            switch (spawn.EventType)
            {
                case SpawnEvent.NewWorld:
                    return Kind.World;
                case SpawnEvent.NewPlayer:
                case SpawnEvent.RespawnPlayer:
                    return (object)player != null ? Kind.Player : Kind.None;
                default:
                    return Kind.None;
            }
        }

        private static bool Contains(StartConditionData start, SpawnData spawn)
        {
            return start?.Spawns != null && start.Spawns.Contains(spawn);
        }

        /// <summary>O mundo que esta criando os itens iniciais agora (o jogo pode usar outro objeto que nao o WorldSetting.Current).</summary>
        private static WorldSetting _spawningWorld;

        public static void SpawnOnNewWorldPrefix(WorldSetting __instance)
        {
            _spawningWorld = __instance;
            _worldKit = null;
            try
            {
                StartConditionData start = __instance?.StartConditionData;
                string spawns = start?.Spawns == null ? "-" : string.Join(", ", start.Spawns.ConvertAll(s => s.Id + ":" + s.EventType));
                Plugin.Log.LogInfo($"New world {__instance?.Id}: start condition {start?.Id ?? "(default)"} [{spawns}]; same as current: {ReferenceEquals(__instance, WorldSetting.Current)}.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"New world log failed: {ex.Message}");
            }
        }

        public static void SpawnExecutePrefix(SpawnData __instance, Thing parent, Human player)
        {
            try
            {
                // Nao exige RunSimulation: durante a criacao do mundo ela pode ainda nao valer; so o host cria os kits.
                if (NetworkManager.IsClient)
                {
                    return;
                }
                Kind kind = KindOf(__instance, parent, player);
                if (kind == Kind.None)
                {
                    return;
                }
                _kind = kind;
                _player = player;
                _before = new HashSet<long>(ThingSpawnData.PreSpawnedThingIds);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Kit tracking failed: {ex.Message}");
                _before = null;
            }
        }

        public static void SpawnExecutePostfix(SpawnData __instance, Thing parent, Human player)
        {
            try
            {
                if (_before == null || KindOf(__instance, parent, player) != _kind)
                {
                    return;
                }
                List<long> created = new List<long>();
                bool capsule = false;
                foreach (long id in ThingSpawnData.PreSpawnedThingIds)
                {
                    if (!_before.Contains(id))
                    {
                        created.Add(id);
                        capsule |= Thing.Find(id) is LanderCapsule;
                    }
                }
                _before = null;
                // A propria capsula tambem vem de um spawn NewPlayer: ela nao faz parte do kit.
                if (capsule || created.Count == 0)
                {
                    return;
                }
                Entry entry;
                if (_kind == Kind.World)
                {
                    entry = _worldKit ??= new Entry { Source = "world" };
                }
                else
                {
                    long owner = _player.ReferenceId;
                    if (!PlayerKits.TryGetValue(owner, out entry))
                    {
                        entry = new Entry { Source = "player " + _player.DisplayName };
                        PlayerKits[owner] = entry;
                    }
                }
                foreach (long id in created)
                {
                    if (!entry.Ids.Contains(id))
                    {
                        entry.Ids.Add(id);
                    }
                }
                Plugin.Log.LogInfo($"Starting kit '{__instance.Id}' ({entry.Source}): {entry.Ids.Count} thing(s) tracked so far.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Kit tracking failed: {ex.Message}");
            }
        }

        /// <summary>
        /// A capsula de <paramref name="human"/> decidiu se vai ser pilotada. Reserva para ela o kit desse jogador e,
        /// se for a primeira capsula do mundo, o kit do mundo. Retorna true se ela leva algum kit.
        /// </summary>
        public static bool OnCapsuleDecided(LanderCapsule capsule, Human human, bool steered)
        {
            List<Entry> taken = new List<Entry>();
            if ((object)human != null && PlayerKits.TryGetValue(human.ReferenceId, out Entry own))
            {
                PlayerKits.Remove(human.ReferenceId);
                taken.Add(own);
            }
            if (_worldKit != null)
            {
                taken.Add(_worldKit);
                _worldKit = null;
            }
            if (!steered || !Plugin.MoveStartingKit.Value)
            {
                return false;
            }
            taken.RemoveAll(e => e.Ids.Count == 0);
            if (taken.Count == 0)
            {
                return false;
            }
            Claims[capsule.ReferenceId] = taken;
            LastClaimed.Clear();
            foreach (Entry entry in taken)
            {
                LastClaimed.AddRange(entry.Ids);
            }
            _lastClaimedBy = capsule.ReferenceId;
            LastSources = string.Join(", ", taken.ConvertAll(e => e.Source));
            return true;
        }

        public static void TerminateDescentPostfix(LanderCapsule __instance)
        {
            try
            {
                if (GameManager.RunSimulation && (object)__instance != null && Claims.TryGetValue(__instance.ReferenceId, out List<Entry> kits))
                {
                    Claims.Remove(__instance.ReferenceId);
                    List<long> ids = new List<long>();
                    foreach (Entry entry in kits)
                    {
                        ids.AddRange(entry.Ids);
                    }
                    Plugin.Instance.StartCoroutine(MoveKit(__instance, ids));
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Kit move failed: {ex}");
            }
        }

        /// <summary>true enquanto o kit espera o jogador sair da capsula (somente para o autoteste).</summary>
        public static bool WaitingForExit { get; private set; }

        private static Human SeatedHuman(LanderCapsule capsule)
        {
            return capsule != null && capsule.Slots.Count > 1 ? capsule.Slots[1].Get<Human>() : null;
        }

        private static IEnumerator MoveKit(LanderCapsule capsule, List<long> thingIds)
        {
            // So coloca o lander depois que o jogador sai da capsula: assim ele cai num lugar livre e longe do jogador.
            WaitingForExit = true;
            Human human = SeatedHuman(capsule);
            while (capsule != null && (object)SeatedHuman(capsule) != null)
            {
                if (GameManager.GameState == Assets.Scripts.GridSystem.GameState.None)
                {
                    WaitingForExit = false;
                    yield break;
                }
                yield return new WaitForSeconds(0.25f);
            }
            WaitingForExit = false;
            if (capsule == null)
            {
                yield break;
            }
            yield return new WaitForSeconds(0.5f);

            Vector3 landing = capsule.Transform.position;
            Vector3? player = (object)human != null && human != null ? human.Transform.position : (Vector3?)null;
            float radius = Mathf.Max(Clearance, Plugin.KitRadius.Value);
            List<Vector3> taken = new List<Vector3>();
            int moved = 0;
            foreach (long id in thingIds)
            {
                DynamicThing thing = Thing.Find(id) as DynamicThing;
                if (thing == null || thing.ParentSlot != null)
                {
                    continue;
                }
                Vector3 safe = PickSpot(landing, player, radius, taken);
                taken.Add(safe);
                Plugin.Log.LogInfo($"Kit item {thing.DisplayName}: capsule ({landing.x:0.0}, {landing.z:0.0}), player {(player.HasValue ? $"({player.Value.x:0.0}, {player.Value.z:0.0})" : "-")}, spot ({safe.x:0.0}, {safe.z:0.0}).");
                Plugin.Instance.StartCoroutine(FallFromSky(thing, safe, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f), moved * 1.5f));
                moved++;
            }
            Plugin.Log.LogInfo($"Player left the capsule: starting kit falling within {radius:0} m of ({landing.x:0}, {landing.z:0}): {moved} thing(s).");
        }

        /// <summary>
        /// Escolhe um ponto no chao entre <see cref="Clearance"/> e <paramref name="radius"/> metros da capsula,
        /// o mais longe possivel do jogador e dos outros itens ja colocados.
        /// </summary>
        private static Vector3 PickSpot(Vector3 landing, Vector3? player, float radius, List<Vector3> taken)
        {
            Vector3 best = landing + Vector3.forward * radius;
            float bestScore = float.MinValue;
            float start = UnityEngine.Random.Range(0f, 360f);
            for (int i = 0; i < 12; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, start + i * 30f, 0f) * Vector3.forward;
                float distance = UnityEngine.Random.Range(Clearance, radius);
                Vector3 target = landing + dir * distance;
                Vector3 safe = SpawnPoint.GetSafePoint(new Vector3(target.x, 0f, target.z), Vector3.zero, avoidStructure: true);
                float score = 0f;
                if (player.HasValue)
                {
                    score += Mathf.Min(FlatDistance(safe, player.Value), 30f);
                }
                foreach (Vector3 other in taken)
                {
                    score += Mathf.Min(FlatDistance(safe, other), 15f);
                }
                if (FlatDistance(safe, landing) < Clearance * 0.8f)
                {
                    score -= 100f; // GetSafePoint empurrou para perto demais da capsula
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = safe;
                }
            }
            return best;
        }

        private static float FlatDistance(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

        /// <summary>
        /// Desce o item do ceu ate o ponto no chao, freando no fim (mesma curva da descida da capsula),
        /// e so entao devolve a fisica. Assim nao ha impacto forte nem item atravessando o terreno.
        /// </summary>
        private static IEnumerator FallFromSky(DynamicThing thing, Vector3 ground, Quaternion rotation, float delay)
        {
            if (delay > 0f)
            {
                yield return new WaitForSeconds(delay);
            }
            if (thing == null)
            {
                yield break;
            }
            Rigidbody body = thing.RigidBody;
            bool wasKinematic = body != null && body.isKinematic;
            if (body != null)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.isKinematic = true;
            }
            float height = Mathf.Max(20f, Plugin.KitFallHeight.Value);
            float duration = Mathf.Max(1f, Plugin.KitFallSeconds.Value);
            Vector3 top = ground + Vector3.up * height;
            // MoveToWorld do jogo so serve para itens saindo de um slot; no ar/chao e preciso reposicionar direto.
            thing.SnapTransform(top, rotation);
            float elapsed = 0f;
            while (elapsed < duration)
            {
                if (thing == null)
                {
                    yield break;
                }
                float t = elapsed / duration;
                float eased = 1f - (1f - t) * (1f - t);
                thing.Transform.position = Vector3.Lerp(top, ground, eased);
                elapsed += Time.deltaTime;
                yield return null;
            }
            if (thing == null)
            {
                yield break;
            }
            thing.Transform.position = ground;
            if (body != null)
            {
                body.isKinematic = wasKinematic;
                if (!wasKinematic)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
            }
            Plugin.Log.LogInfo($"Starting kit item {thing.DisplayName} landed at ({ground.x:0}, {ground.z:0}).");
        }
    }
}
