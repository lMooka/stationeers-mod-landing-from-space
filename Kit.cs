using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>Somente para o autoteste: onde cada item do kit pousou (antes da fisica mexer nele).</summary>
        public static readonly Dictionary<long, Vector3> Landed = new Dictionary<long, Vector3>();

        /// <summary>Saiu do mundo: os kits pendentes pertencem a ele e nao valem para o proximo.</summary>
        public static void Reset()
        {
            _worldKit = null;
            PlayerKits.Clear();
            Claims.Clear();
            Landed.Clear();
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
                if (NetworkManager.IsClient || _suppressTracking)
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
        /// Comando "respawn": o jogador acabou de renascer e estava marcado. Cria de novo os itens que a condicao de
        /// inicio da fora da capsula (lander, caixa do brutal, tanques...) como kit desse jogador; a capsula dele leva
        /// o kit (cai do ceu ao lado quando ele sair). Sem o mod no cliente, o kit fica onde o jogo colocou.
        /// </summary>
        public static void CreateCharacterPostfix(Human __result, ulong clientId, string steamName, bool isRespawn)
        {
            try
            {
                if (!GameManager.RunSimulation || !isRespawn || (object)__result == null)
                {
                    return;
                }
                if (!LandingLog.TakePending(clientId, steamName))
                {
                    return;
                }
                StartConditionData start = WorldSetting.Current?.StartConditionData;
                if (start?.Spawns == null)
                {
                    Plugin.Log.LogWarning($"No start condition to give {steamName} a new lander.");
                    return;
                }
                Entry entry = new Entry { Source = "respawn " + steamName };
                foreach (SpawnData spawn in start.Spawns)
                {
                    if (spawn == null || (spawn.EventType != SpawnEvent.NewWorld && spawn.EventType != SpawnEvent.NewPlayer)
                        || spawn.Id.IndexOf("Capsule", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }
                    HashSet<long> before = new HashSet<long>(ThingSpawnData.PreSpawnedThingIds);
                    _suppressTracking = true;
                    try
                    {
                        spawn.Execute(null, __result);
                    }
                    finally
                    {
                        _suppressTracking = false;
                    }
                    foreach (long id in ThingSpawnData.PreSpawnedThingIds)
                    {
                        if (!before.Contains(id) && !(Thing.Find(id) is LanderCapsule) && !entry.Ids.Contains(id))
                        {
                            entry.Ids.Add(id);
                        }
                    }
                }
                if (entry.Ids.Count == 0)
                {
                    Plugin.Log.LogWarning($"The start condition gave nothing to respawn for {steamName}.");
                    return;
                }
                PlayerKits[__result.ReferenceId] = entry;
                Plugin.Log.LogInfo($"New starting kit for {steamName} on respawn: {entry.Ids.Count} thing(s).");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Could not give {steamName} a new lander: {ex}");
            }
        }

        /// <summary>Enquanto o comando "respawn" cria um kit, os ganchos de rastreio nao mexem nele.</summary>
        private static bool _suppressTracking;

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

            List<DynamicThing> things = new List<DynamicThing>();
            foreach (long id in thingIds)
            {
                if (Thing.Find(id) is DynamicThing thing && thing.ParentSlot == null)
                {
                    things.Add(thing);
                }
            }
            if (things.Count == 0)
            {
                yield break;
            }

            // Mantem o arranjo que o jogo criou (ex.: os destrocos espalhados do modo brutal) e so desloca o conjunto
            // inteiro para o lado da capsula, longe do jogador.
            Vector3 centroid = Vector3.zero;
            foreach (DynamicThing thing in things)
            {
                centroid += thing.Transform.position;
            }
            centroid /= things.Count;
            List<Vector3> offsets = new List<Vector3>();
            float layoutRadius = 0f;
            foreach (DynamicThing thing in things)
            {
                Vector3 offset = thing.Transform.position - centroid;
                offset.y = 0f;
                offsets.Add(offset);
                layoutRadius = Mathf.Max(layoutRadius, offset.magnitude);
            }
            // Em mundos com lava (Vulcano) o kit fica colado na capsula: a borda de cada item a no maximo
            // TightGap metros da borda da capsula, onde o chao ja e seguro (a capsula acabou de pousar ali).
            bool tight = WorldSetting.Current != null && WorldSetting.Current.HasLava;
            float capsuleRadius = Footprint(capsule, out _);
            List<float> radii = new List<float>();
            List<Vector3> pivots = new List<Vector3>();
            foreach (DynamicThing thing in things)
            {
                radii.Add(Footprint(thing, out Vector3 pivot));
                pivots.Add(pivot);
            }
            float anchor = 0f;
            if (tight)
            {
                float scale = layoutRadius > TightLayoutRadius ? TightLayoutRadius / layoutRadius : 1f;
                for (int i = 0; i < offsets.Count; i++)
                {
                    offsets[i] *= scale;
                }
                layoutRadius *= scale;
                // Distancia do centro do arranjo ate a capsula para o item mais proximo ficar a TightGap da borda.
                anchor = capsuleRadius + TightGap + layoutRadius + radii.Max();
                if (things.Count == 1)
                {
                    anchor = capsuleRadius + TightGap + radii[0];
                }
            }
            Vector3 center = tight ? PickTightCenter(landing, player, offsets, anchor) : PickCenter(landing, player, offsets, layoutRadius);

            // Item principal do kit, que fica registrado para sempre no mapa: o lander, ou o maior item (caixa do brutal).
            int main = things.FindIndex(t => t is Lander);
            if (main < 0)
            {
                main = radii.IndexOf(radii.Max());
            }
            string owner = (object)human != null && human != null ? human.DisplayName : "";

            for (int i = 0; i < things.Count; i++)
            {
                DynamicThing thing = things[i];
                // O centro visual do item fica no ponto escolhido (o pivo do modelo nem sempre e o centro).
                Vector3 target = center + offsets[i] - pivots[i];
                Vector3 safe = SpawnPoint.GetSafePoint(new Vector3(target.x, 0f, target.z), Vector3.zero, avoidStructure: true);
                Vector3 fromCapsule = new Vector3(safe.x + pivots[i].x - landing.x, 0f, safe.z + pivots[i].z - landing.z);
                float minimum = tight ? capsuleRadius + radii[i] + 0.3f : Clearance * 0.8f;
                if (fromCapsule.magnitude < minimum)
                {
                    // GetSafePoint (ou o proprio arranjo) ficou perto demais da capsula: afasta.
                    Vector3 away = fromCapsule.sqrMagnitude > 0.01f ? fromCapsule.normalized : (center - landing).normalized;
                    away.y = 0f;
                    Vector3 pushed = landing + away.normalized * (tight ? capsuleRadius + radii[i] + TightGap * 0.5f : Clearance) - pivots[i];
                    safe = SpawnPoint.GetSafePoint(new Vector3(pushed.x, 0f, pushed.z), Vector3.zero, avoidStructure: true);
                }
                Plugin.Log.LogInfo($"Kit item {thing.DisplayName}: capsule ({landing.x:0.0}, {landing.z:0.0}), player {(player.HasValue ? $"({player.Value.x:0.0}, {player.Value.z:0.0})" : "-")}, spot ({safe.x:0.0}, {safe.z:0.0}).");
                // Com varios itens, caem quase juntos, como uma chuva de destrocos.
                float delay = things.Count == 1 ? 0f : UnityEngine.Random.Range(0f, 1.2f);
                Plugin.Instance.StartCoroutine(FallFromSky(thing, safe, thing.Transform.rotation, delay));
                // Chama, luz e som do retrofoguete (aqui e nos clientes com o mod).
                float fallSeconds = Mathf.Max(1f, Plugin.KitFallSeconds.Value);
                LanderFx.Play(thing, delay, fallSeconds);
                Net.SendToModdedClients($"K|{thing.ReferenceId}|{delay.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}|{fallSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
                if (i == main)
                {
                    LandingLog.RecordLander(safe + pivots[i], owner);
                }
            }
            Plugin.Log.LogInfo($"Player left the capsule: starting kit falling around ({center.x:0}, {center.z:0}), {FlatDistance(center, landing):0} m from the capsule at ({landing.x:0}, {landing.z:0}): {things.Count} thing(s), layout radius {layoutRadius:0} m.");
        }

        /// <summary>
        /// Escolhe onde fica o centro do arranjo do kit: entre <see cref="Clearance"/> e KitRadius metros alem da
        /// borda do arranjo, na direcao que deixa os itens mais longe do jogador e sem nenhum em cima da capsula.
        /// </summary>
        private static Vector3 PickCenter(Vector3 landing, Vector3? player, List<Vector3> offsets, float layoutRadius)
        {
            float radius = Mathf.Max(Clearance, Plugin.KitRadius.Value);
            Vector3 best = landing + Vector3.forward * (radius + layoutRadius);
            float bestScore = float.MinValue;
            float start = UnityEngine.Random.Range(0f, 360f);
            for (int i = 0; i < 12; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, start + i * 30f, 0f) * Vector3.forward;
                Vector3 center = landing + dir * (UnityEngine.Random.Range(Clearance, radius) + layoutRadius);
                float nearestPlayer = float.MaxValue;
                float nearestCapsule = float.MaxValue;
                foreach (Vector3 offset in offsets)
                {
                    Vector3 item = center + offset;
                    nearestCapsule = Mathf.Min(nearestCapsule, FlatDistance(item, landing));
                    if (player.HasValue)
                    {
                        nearestPlayer = Mathf.Min(nearestPlayer, FlatDistance(item, player.Value));
                    }
                }
                float score = player.HasValue ? Mathf.Min(nearestPlayer, 30f) : 0f;
                if (nearestCapsule < Clearance * 0.8f)
                {
                    score -= 100f;
                }
                // Prefere chao plano: numa encosta os itens (tanques, principalmente) saem rolando.
                score -= SlopePenalty * HeightRange(center, layoutRadius + 3f);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = center;
                }
            }
            return best;
        }

        /// <summary>Mundos com lava: folga maxima entre a borda da capsula e a borda do item.</summary>
        private const float TightGap = 1.5f;

        /// <summary>Mundos com lava: raio maximo do arranjo de varios itens (os destrocos do brutal ficam juntos).</summary>
        private const float TightLayoutRadius = 4f;

        /// <summary>
        /// Raio horizontal do item e o deslocamento do centro dele em relacao ao pivo, medidos pela malha 3D.
        /// Colisores nao servem: o jogo desliga os colisores de objetos longe do jogador (o lander nasce longe).
        /// Ignora os efeitos da capsula (EntryEffects e os do mod) e os objetos guardados dentro (jogador, itens).
        /// </summary>
        public static float Footprint(Thing thing, out Vector3 pivot)
        {
            pivot = Vector3.zero;
            if (thing == null)
            {
                return 1f;
            }
            Bounds? bounds = null;
            foreach (MeshFilter filter in thing.GetComponentsInChildren<MeshFilter>())
            {
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null || filter.GetComponentInParent<Thing>() != thing || IsEffect(filter.transform, thing.Transform))
                {
                    continue;
                }
                Bounds local = mesh.bounds;
                Matrix4x4 toWorld = filter.transform.localToWorldMatrix;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 point = local.center + Vector3.Scale(local.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                    Vector3 world = toWorld.MultiplyPoint3x4(point);
                    if (bounds.HasValue)
                    {
                        Bounds b = bounds.Value;
                        b.Encapsulate(world);
                        bounds = b;
                    }
                    else
                    {
                        bounds = new Bounds(world, Vector3.zero);
                    }
                }
            }
            if (!bounds.HasValue)
            {
                return 1f;
            }
            Vector3 offset = bounds.Value.center - thing.Transform.position;
            pivot = new Vector3(offset.x, 0f, offset.z);
            return Mathf.Max(0.3f, Mathf.Max(bounds.Value.extents.x, bounds.Value.extents.z));
        }

        private static bool IsEffect(Transform t, Transform root)
        {
            for (; t != null && t != root; t = t.parent)
            {
                if (t.name == "EntryEffects" || t.name.StartsWith("LandingFromSpace", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Mundos com lava: o arranjo fica a <paramref name="anchor"/> metros da capsula, no lado sem lava, mais
        /// plano e mais longe do jogador.
        /// </summary>
        private static Vector3 PickTightCenter(Vector3 landing, Vector3? player, List<Vector3> offsets, float anchor)
        {
            Vector3 best = landing + Vector3.forward * anchor;
            float bestScore = float.MinValue;
            float start = UnityEngine.Random.Range(0f, 360f);
            for (int i = 0; i < 16; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, start + i * 22.5f, 0f) * Vector3.forward;
                Vector3 center = landing + dir * anchor;
                float score = 0f;
                float nearestPlayer = float.MaxValue;
                foreach (Vector3 offset in offsets)
                {
                    Vector3 item = center + offset;
                    Vector3 ground = new Vector3(item.x, ServerPilot.GroundHeight(item), item.z);
                    if (WorldSetting.Current.IsUnderLava(ground))
                    {
                        score -= 1000f;
                    }
                    if (player.HasValue)
                    {
                        nearestPlayer = Mathf.Min(nearestPlayer, FlatDistance(item, player.Value));
                    }
                }
                if (player.HasValue)
                {
                    score += Mathf.Min(nearestPlayer, 10f);
                }
                score -= SlopePenalty * HeightRange(center, Mathf.Max(2f, anchor * 0.3f));
                if (score > bestScore)
                {
                    bestScore = score;
                    best = center;
                }
            }
            return best;
        }

        /// <summary>Pontos por metro de desnivel na area do kit ao escolher o lado da capsula.</summary>
        private const float SlopePenalty = 2f;

        /// <summary>Diferenca entre o ponto mais alto e o mais baixo do terreno num circulo em volta de <paramref name="center"/>.</summary>
        private static float HeightRange(Vector3 center, float radius)
        {
            float min = float.MaxValue;
            float max = float.MinValue;
            for (int i = 0; i <= 8; i++)
            {
                Vector3 point = i == 0 ? center : center + Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward * radius;
                float height = ServerPilot.GroundHeight(point);
                min = Mathf.Min(min, height);
                max = Mathf.Max(max, height);
            }
            return max - min;
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
            Landed[thing.ReferenceId] = ground;
            Plugin.Log.LogInfo($"Starting kit item {thing.DisplayName} landed at ({ground.x:0}, {ground.z:0}).");
        }
    }
}
