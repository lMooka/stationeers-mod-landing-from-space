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
    /// Kit inicial de um mundo novo (lander, caixa do modo brutal, tanques...): tudo o que a condicao de inicio
    /// cria no evento NewWorld. Se a primeira capsula do mundo for pilotada, o kit vai para onde ela pousar.
    /// Se o primeiro jogador nao tiver o mod, o kit fica onde o jogo colocou.
    /// </summary>
    public static class Kit
    {
        private enum KitState { None, Pending, Claimed, Done }

        private static KitState _state = KitState.None;
        private static readonly List<long> ThingIds = new List<long>();
        private static Vector3 _center;
        private static long _claimedBy;
        private static HashSet<long> _before;

        /// <summary>Distancia minima entre a capsula e qualquer item do kit depois de mudar de lugar.</summary>
        private const float Clearance = 9f;

        public static bool IsPending => _state == KitState.Pending;
        public static bool IsClaimed => _state == KitState.Claimed;
        public static bool IsDone => _state == KitState.Done;
        public static long ClaimedBy => _claimedBy;
        public static IReadOnlyList<long> Things => ThingIds;
        public static Vector3 Center => _center;

        /// <summary>Saiu do mundo: o kit pendente pertence a ele e nao vale para o proximo.</summary>
        public static void Reset()
        {
            if (_state == KitState.None && ThingIds.Count == 0)
            {
                return;
            }
            _state = KitState.None;
            ThingIds.Clear();
            _claimedBy = 0;
            _before = null;
        }

        private static bool IsStartConditionNewWorld(SpawnData spawn, Thing parent)
        {
            if ((object)parent != null || spawn == null || spawn.EventType != SpawnEvent.NewWorld)
            {
                return false;
            }
            return Contains(_spawningWorld?.StartConditionData, spawn) || Contains(WorldSetting.Current?.StartConditionData, spawn);
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

        public static void SpawnExecutePrefix(SpawnData __instance, Thing parent)
        {
            try
            {
                // Nao exige RunSimulation: durante a criacao do mundo ela pode ainda nao valer; so o host cria mundo novo.
                if (!NetworkManager.IsClient && IsStartConditionNewWorld(__instance, parent))
                {
                    if (_state != KitState.Pending)
                    {
                        // Mundo novo nesta sessao: comeca um kit novo.
                        ThingIds.Clear();
                        _claimedBy = 0;
                        _state = KitState.Pending;
                        _center = WorldSetting.Current.StartLocationData.Get().WorldPosition();
                    }
                    _before = new HashSet<long>(ThingSpawnData.PreSpawnedThingIds);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Kit tracking failed: {ex.Message}");
                _before = null;
            }
        }

        public static void SpawnExecutePostfix(SpawnData __instance, Thing parent)
        {
            try
            {
                if (_before == null || !IsStartConditionNewWorld(__instance, parent))
                {
                    return;
                }
                foreach (long id in ThingSpawnData.PreSpawnedThingIds)
                {
                    if (!_before.Contains(id) && !ThingIds.Contains(id))
                    {
                        ThingIds.Add(id);
                    }
                }
                _before = null;
                Plugin.Log.LogInfo($"Starting kit '{__instance.Id}': {ThingIds.Count} thing(s) tracked so far.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Kit tracking failed: {ex.Message}");
            }
        }

        /// <summary>A primeira capsula do mundo decide para onde o kit vai. Retorna true se esta capsula leva o kit.</summary>
        public static bool OnCapsuleDecided(LanderCapsule capsule, bool steered)
        {
            if (_state != KitState.Pending)
            {
                return false;
            }
            if (!steered || !Plugin.MoveStartingKit.Value || ThingIds.Count == 0)
            {
                _state = KitState.Done;
                return false;
            }
            _state = KitState.Claimed;
            _claimedBy = capsule.ReferenceId;
            return true;
        }

        public static void TerminateDescentPostfix(LanderCapsule __instance)
        {
            try
            {
                if (_state == KitState.Claimed && GameManager.RunSimulation && (object)__instance != null && __instance.ReferenceId == _claimedBy)
                {
                    _state = KitState.Done;
                    Plugin.Instance.StartCoroutine(MoveKit(__instance));
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

        private static IEnumerator MoveKit(LanderCapsule capsule)
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
            foreach (long id in ThingIds)
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
