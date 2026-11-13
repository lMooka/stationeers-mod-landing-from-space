using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Serialization;
using HarmonyLib;
using UnityEngine;

namespace LandingFromSpace
{
    /// <summary>
    /// Lado do host (ou singleplayer): segura a capsula antes da descida enquanto o jogador escolhe no mapa.
    /// Quando chega o ponto, leva a capsula para o alto desse ponto, espera o chao existir ali e solta a
    /// descida normal do jogo.
    /// </summary>
    public static class ServerPilot
    {
        public enum Phase { Choosing, WaitingTerrain, Steering }

        public sealed class Session
        {
            public LanderCapsule Capsule;
            public long CapsuleId;
            public Human Human;
            public ulong ClientId;
            public bool Local;
            public Phase Phase;
            public Vector3 Target;
            public float TerrainDeadline;
            public float StartedAt;
            public float ChosenAt;

            // Descida guiada
            public Vector2 Dir;
            public bool Fast;
            public float LastInput;
            public float GroundY;
            public float NextGroundSample;
            public Vector2 Velocity;
            public float Thrust;
            public float FallSpeed;
            public Quaternion Upright = Quaternion.identity;
        }

        /// <summary>Quanto tempo sem receber direcao do cliente ate parar de desviar a capsula.</summary>
        private const float InputTimeout = 1f;

        public static readonly Dictionary<long, Session> Sessions = new Dictionary<long, Session>();
        private static readonly HashSet<long> Deciding = new HashSet<long>();
        private static readonly HashSet<long> Bypass = new HashSet<long>();

        private static readonly MethodInfo BeginDescentMethod = AccessTools.Method(typeof(LanderCapsule), "BeginDescent");
        private static readonly MethodInfo EstimateSurfaceMethod = AccessTools.Method(typeof(SpawnPoint), "EstimateTerrainSurface");

        private static GameState _lastState = GameState.None;

        /// <summary>Tempo maximo esperando o terreno do destino carregar antes de descer mesmo assim.</summary>
        private const float TerrainWaitSeconds = 20f;

        public static Session Find(long capsuleId)
        {
            Sessions.TryGetValue(capsuleId, out Session session);
            return session;
        }

        // ------------------------------------------------------------------ patches

        /// <summary>A capsula acabou de entrar em modo de descida: decide se o jogador vai escolher o local.</summary>
        public static bool BeginDescentPrefix(LanderCapsule __instance)
        {
            try
            {
                if (!GameManager.RunSimulation || !Plugin.Enabled.Value || (object)__instance == null)
                {
                    return true;
                }
                long id = __instance.ReferenceId;
                if (Bypass.Remove(id))
                {
                    return true;
                }
                if (Sessions.ContainsKey(id) || !Deciding.Add(id))
                {
                    return false;
                }
                Plugin.Instance.StartCoroutine(Decide(__instance));
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"BeginDescent hook failed, capsule descends normally: {ex}");
                return true;
            }
        }

        /// <summary>Se o mundo for salvo com a capsula no ar, ela fica salva no chao.</summary>
        public static void SerializeSavePostfix(LanderCapsule __instance, ThingSaveData __result)
        {
            try
            {
                if (__result != null && Sessions.ContainsKey(__instance.ReferenceId))
                {
                    __result.WorldPosition = SafeGroundPoint(__instance.Transform.position);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Save fix failed: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ decisao

        private static IEnumerator Decide(LanderCapsule capsule)
        {
            long id = capsule.ReferenceId;
            try
            {
                while (GameManager.GameState != GameState.Running)
                {
                    if (GameManager.GameState == GameState.None || capsule == null)
                    {
                        yield break;
                    }
                    yield return null;
                }

                // O jogador entra no assento logo depois da capsula ser criada.
                Human human = null;
                float until = Time.realtimeSinceStartup + 2f;
                while (capsule != null && Time.realtimeSinceStartup < until)
                {
                    human = capsule.Slots.Count > 1 ? capsule.Slots[1].Get<Human>() : null;
                    if ((object)human != null)
                    {
                        break;
                    }
                    yield return null;
                }
                if (capsule == null)
                {
                    yield break;
                }
                if ((object)human == null)
                {
                    Resume(capsule, null);
                    yield break;
                }

                bool local = !GameManager.IsBatchMode && human.OwnerClientId == NetworkManager.LocalClientId;
                Client client = null;
                if (!local && NetworkManager.IsServer)
                {
                    client = Client.Find(human.OwnerClientId);
                    if (client != null && !Net.ModdedClients.Contains(human.OwnerClientId))
                    {
                        Net.SayHello(client);
                        float wait = Time.realtimeSinceStartup + Mathf.Max(0f, Plugin.ClientReplySeconds.Value);
                        while (capsule != null && Time.realtimeSinceStartup < wait && !Net.ModdedClients.Contains(human.OwnerClientId))
                        {
                            yield return null;
                        }
                        if (capsule == null)
                        {
                            yield break;
                        }
                    }
                }
                bool modded = local || (client != null && Net.ModdedClients.Contains(human.OwnerClientId));
                bool carriesKit = Kit.OnCapsuleDecided(capsule, human, modded);
                if (!modded)
                {
                    Plugin.Log.LogInfo($"Capsule of {human.DisplayName}: player has no mod, normal descent.");
                    Resume(capsule, null);
                    yield break;
                }

                StartSession(capsule, human, local, client);
                Plugin.Log.LogInfo($"Capsule of {human.DisplayName}: waiting for a landing point{(carriesKit ? " (carries the starting kit)" : string.Empty)}.");
            }
            finally
            {
                Deciding.Remove(id);
            }
        }

        private static void StartSession(LanderCapsule capsule, Human human, bool local, Client client)
        {
            Session session = new Session
            {
                Capsule = capsule,
                CapsuleId = capsule.ReferenceId,
                Human = human,
                ClientId = human.OwnerClientId,
                Local = local,
                Phase = Phase.Choosing,
                StartedAt = Time.time
            };
            capsule.RigidBody.isKinematic = true;
            OnServer.Interact(capsule.InteractLock, 1);
            Sessions[session.CapsuleId] = session;

            if (local)
            {
                MapPicker.Open(capsule);
            }
            else
            {
                // Os pousos recentes e os landers vao antes, para o mapa do cliente ja abrir com eles.
                Net.SendToClient(client, $"M|{LandingLog.Serialize()}");
                Net.SendToClient(client, $"P|{session.CapsuleId}");
            }
        }

        /// <summary>Solta a descida original do jogo, opcionalmente a partir de outro ponto no chao.</summary>
        private static void Resume(LanderCapsule capsule, Vector3? groundPoint)
        {
            if (capsule == null)
            {
                return;
            }
            if (groundPoint.HasValue)
            {
                // A descida final do jogo sempre comeca KinematicStartHeight (100 m) acima do ponto no chao. Depois da
                // descida guiada ela comeca da altura em que a capsula ja esta, sem pular para cima.
                float above = capsule.Transform.position.y - groundPoint.Value.y;
                capsule.KinematicStartHeight = Mathf.Clamp(above, 5f, 100f);
                capsule.Transform.position = groundPoint.Value;
            }
            Bypass.Add(capsule.ReferenceId);
            try
            {
                BeginDescentMethod.Invoke(capsule, null);
            }
            catch (Exception ex)
            {
                Bypass.Remove(capsule.ReferenceId);
                Plugin.Log.LogError($"Could not start the descent: {ex}");
                capsule.RigidBody.isKinematic = false;
            }
        }

        // ------------------------------------------------------------------ pedidos do jogador

        /// <summary>O jogador escolheu um ponto (x, z) no mapa. Sem ponto: cai onde a capsula ja esta.</summary>
        public static void RequestTarget(long capsuleId, ulong fromClient, Vector2? point)
        {
            Session session = Find(capsuleId);
            if (session == null || session.ClientId != fromClient || session.Phase != Phase.Choosing || session.Capsule == null)
            {
                return;
            }
            Vector3 current = session.Capsule.Transform.position;
            Vector2 target = point ?? new Vector2(current.x, current.z);
            if (float.IsNaN(target.x) || float.IsNaN(target.y) || float.IsInfinity(target.x) || float.IsInfinity(target.y))
            {
                target = new Vector2(current.x, current.z);
            }
            target = ClampToWorld(target);
            // Ponto em cima da lava: a descida guiada ja comeca sobre o chao seguro mais proximo.
            Vector3 dry = AwayFromLava(new Vector3(target.x, 0f, target.y));
            target = new Vector2(dry.x, dry.z);
            session.Target = new Vector3(target.x, 0f, target.y);
            session.Phase = Phase.WaitingTerrain;
            session.TerrainDeadline = Time.time + TerrainWaitSeconds;
            session.ChosenAt = Time.time;

            // Leva a capsula para o alto do destino: o jogador sentado faz o terreno de la carregar.
            float ground = GroundHeight(session.Target);
            session.GroundY = ground;
            session.Capsule.Transform.position = new Vector3(target.x, ground + StartHeight, target.y);
            Plugin.Log.LogInfo($"Capsule {capsuleId}: landing point ({target.x:0}, {target.y:0}) chosen, waiting for the terrain there.");
        }

        /// <summary>Altura (acima do chao) em que a descida guiada comeca. Nunca abaixo da altura de entrega.</summary>
        public static float StartHeight => Mathf.Max(Plugin.StartHeight.Value, Plugin.HoverHeight.Value + 10f);

        /// <summary>Direcao horizontal (mundo) pedida pelo jogador durante a descida guiada.</summary>
        public static void SetInput(long capsuleId, ulong fromClient, Vector2 dir, bool fast)
        {
            Session session = Find(capsuleId);
            if (session == null || session.ClientId != fromClient || session.Phase != Phase.Steering)
            {
                return;
            }
            if (float.IsNaN(dir.x) || float.IsNaN(dir.y) || float.IsInfinity(dir.x) || float.IsInfinity(dir.y))
            {
                dir = Vector2.zero;
            }
            if (dir.sqrMagnitude > 1f)
            {
                dir.Normalize();
            }
            session.Dir = dir;
            session.Fast = fast;
            session.LastInput = Time.time;
        }

        private static void BeginSteering(Session session)
        {
            session.Phase = Phase.Steering;
            session.LastInput = Time.time;
            session.NextGroundSample = 0f;
            session.Velocity = Vector2.zero;
            session.Thrust = ThrustIdle;
            session.FallSpeed = Plugin.DescentSpeed.Value;
            session.Upright = Quaternion.Euler(0f, session.Capsule.Transform.eulerAngles.y, 0f);
            // Liga o fogo dos motores e o tremor da entrada, como na descida original.
            OnServer.Interact(session.Capsule.InteractActivate, 1);
            if (session.Local)
            {
                MapPicker.Close(session.CapsuleId);
                SteerPilot.Begin(session.Capsule, local: true);
            }
            else
            {
                Net.SendToClient(Client.Find(session.ClientId), $"S|{session.CapsuleId}");
            }
            Plugin.Log.LogInfo($"Capsule {session.CapsuleId}: guided descent started.");
        }

        /// <summary>Move a capsula durante a descida guiada. Retorna true quando chegou na altura de entrega.</summary>
        private static bool Steer(Session session, float dt)
        {
            if (!session.Local && Time.time - session.LastInput > InputTimeout)
            {
                session.Dir = Vector2.zero;
            }
            Transform transform = session.Capsule.Transform;
            Vector3 position = transform.position;
            float steer = Mathf.Max(0.1f, Plugin.SteerSpeed.Value);

            // Velocidade lateral suavizada: a capsula acelera e freia em vez de mudar de direcao na hora.
            float response = 1f - Mathf.Exp(-Mathf.Max(0.1f, Plugin.SteerResponsiveness.Value) * dt);
            session.Velocity = Vector2.Lerp(session.Velocity, session.Dir * steer, response);
            position.x += session.Velocity.x * dt;
            position.z += session.Velocity.y * dt;
            Vector2 clamped = ClampToWorld(new Vector2(position.x, position.z));
            position.x = clamped.x;
            position.z = clamped.y;
            if (Time.time >= session.NextGroundSample)
            {
                session.NextGroundSample = Time.time + 0.25f;
                session.GroundY = GroundHeight(position);
            }
            // Shift muda a velocidade de queda aos poucos, nos dois sentidos (acelera e freia gradualmente).
            float wanted = session.Fast ? Plugin.FastDescentSpeed.Value : Plugin.DescentSpeed.Value;
            float ease = 1f - Mathf.Exp(-Mathf.Max(0.05f, Plugin.FallResponsiveness.Value) * dt);
            session.FallSpeed = Mathf.Lerp(session.FallSpeed, wanted, ease);
            float fall = session.FallSpeed;
            float floor = session.GroundY + Plugin.HoverHeight.Value;
            position.y = Mathf.Max(position.y - fall * dt, floor);
            transform.position = position;
            transform.rotation = Tilt(session, transform.rotation, position.y - floor, steer, dt);
            UpdateThrusters(session, steer, dt);
            return position.y <= floor + 0.5f;
        }

        // Tamanho da chama dos propulsores (ControlledDescentLerp do jogo, 0 a 1; em 1 o jogo apaga o efeito).
        private const float ThrustIdle = 0.35f;
        private const float ThrustSteer = 0.55f;
        private const float ThrustFastFall = 0.12f;
        private const float ThrustMax = 0.95f;

        /// <summary>
        /// A chama cresce quando o jogador desvia a capsula e diminui quando ele acelera a queda (Shift),
        /// sempre suave. O valor vai do host para todos os jogadores pelo proprio jogo, com ou sem o mod.
        /// </summary>
        private static void UpdateThrusters(Session session, float steer, float dt)
        {
            float steering = Mathf.Clamp01(session.Velocity.magnitude / steer);
            float pushing = Mathf.Clamp01(session.Dir.magnitude);
            float target = session.Fast ? ThrustFastFall : ThrustIdle;
            target += ThrustSteer * Mathf.Max(steering, pushing * 0.6f);
            target = Mathf.Clamp(target, 0.05f, ThrustMax);
            float response = 1f - Mathf.Exp(-Mathf.Max(0.1f, Plugin.SteerResponsiveness.Value) * 2f * dt);
            session.Thrust = Mathf.Lerp(session.Thrust, target, response);
            session.Capsule.ControlledDescentLerp = session.Thrust;
        }

        /// <summary>Altura (acima da entrega) em que a capsula comeca a se endireitar para o pouso final.</summary>
        private const float LevelOutHeight = 40f;

        /// <summary>
        /// Inclina a capsula com o fundo na direcao em que ela se move, proporcional a velocidade lateral, ate
        /// MaxTiltAngle. Perto do pouso final ela volta a ficar em pe, porque a descida do jogo espera isso.
        /// </summary>
        private static Quaternion Tilt(Session session, Quaternion current, float heightAboveHandover, float steer, float dt)
        {
            float speed = session.Velocity.magnitude;
            float level = Mathf.Clamp01(heightAboveHandover / LevelOutHeight);
            float angle = Mathf.Clamp(Plugin.MaxTiltAngle.Value, 0f, 60f) * Mathf.Clamp01(speed / steer) * level;
            Quaternion target = session.Upright;
            if (angle > 0.01f && speed > 0.01f)
            {
                Vector3 moving = new Vector3(session.Velocity.x, 0f, session.Velocity.y) / speed;
                Vector3 axis = Vector3.Cross(Vector3.up, moving);
                // Angulo negativo: o fundo (os propulsores) aponta para onde a capsula vai, o topo fica para tras.
                target = Quaternion.AngleAxis(-angle, axis) * session.Upright;
            }
            float smooth = 1f - Mathf.Exp(-Mathf.Max(0.1f, Plugin.SteerResponsiveness.Value) * 1.5f * dt);
            return Quaternion.Slerp(current, target, smooth);
        }

        private static Vector2 ClampToWorld(Vector2 point)
        {
            float half = TerrainSystem.VoxelConstants.Size / 2f - 16f;
            if (half <= 0f)
            {
                return point;
            }
            return new Vector2(Mathf.Clamp(point.x, -half, half), Mathf.Clamp(point.y, -half, half));
        }

        private static void Drop(Session session, string reason)
        {
            Sessions.Remove(session.CapsuleId);
            if (session.Local)
            {
                MapPicker.Close(session.CapsuleId);
                SteerPilot.End(session.CapsuleId);
            }
            else
            {
                Net.SendToClient(Client.Find(session.ClientId), $"E|{session.CapsuleId}");
            }
            LanderCapsule capsule = session.Capsule;
            if (capsule == null)
            {
                return;
            }
            if (session.Phase == Phase.Steering)
            {
                // A descida final do jogo e vertical: entrega a capsula em pe.
                capsule.Transform.rotation = session.Upright;
            }
            Vector3 ground = SafeGroundPoint(capsule.Transform.position);
            LastDropPoint = ground;
            if (session.Phase == Phase.Steering && session.Human != null && session.Human.ParentSlot?.Parent == capsule)
            {
                LandingLog.RecordLanding(ground, session.Human.DisplayName);
            }
            Plugin.Log.LogInfo($"Capsule {session.CapsuleId} descends at ({ground.x:0}, {ground.z:0}) [{reason}].");
            Resume(capsule, ground);
        }

        /// <summary>
        /// O terreno do destino esta pronto: ja existe colisor ali, ou o jogo terminou de gerar o terreno pedido
        /// (o mesmo sinal que ele espera antes de entregar o controle do personagem).
        /// </summary>
        private static bool TerrainReady(Session session)
        {
            Vector3 target = session.Target;
            Vector3 from = new Vector3(target.x, GroundHeight(target) + 60f, target.z);
            if (Physics.Raycast(from, Vector3.down, out RaycastHit hit, 200f, 1 << (int)Layers.Terrain) && hit.collider != null)
            {
                return true;
            }
            bool settled = Time.time - session.ChosenAt > 2f;
            return settled && TerrainSystem.Lods.LodManager.Instance != null && !TerrainSystem.Lods.LodManager.Instance.AwaitingTerrainGeneration;
        }

        public static void Tick()
        {
            GameState state = GameManager.GameState;
            bool left = state == GameState.None && _lastState != GameState.None;
            _lastState = state;
            if (left)
            {
                Sessions.Clear();
                Kit.Reset();
                Net.ModdedClients.Clear();
                Net.HostHasMod = false;
                return;
            }
            if (Sessions.Count == 0)
            {
                return;
            }
            List<Session> toDrop = null;
            List<Session> toSteer = null;
            List<long> gone = null;
            float dt = Time.deltaTime;
            foreach (Session session in Sessions.Values)
            {
                LanderCapsule capsule = session.Capsule;
                if (capsule == null)
                {
                    (gone ??= new List<long>()).Add(session.CapsuleId);
                    continue;
                }
                bool humanGone = session.Human == null || session.Human.ParentSlot?.Parent != capsule;
                bool clientGone = !session.Local && NetworkManager.IsServer && Client.Find(session.ClientId) == null;
                if (humanGone || clientGone)
                {
                    (toDrop ??= new List<Session>()).Add(session);
                    continue;
                }
                if (session.Phase == Phase.WaitingTerrain && (TerrainReady(session) || Time.time > session.TerrainDeadline))
                {
                    (toSteer ??= new List<Session>()).Add(session);
                }
                else if (session.Phase == Phase.Steering && Steer(session, dt))
                {
                    (toDrop ??= new List<Session>()).Add(session);
                }
            }
            if (toSteer != null)
            {
                foreach (Session session in toSteer)
                {
                    BeginSteering(session);
                }
            }
            if (gone != null)
            {
                foreach (long id in gone)
                {
                    Sessions.Remove(id);
                }
            }
            if (toDrop != null)
            {
                foreach (Session session in toDrop)
                {
                    bool seated = session.Human != null && session.Human.ParentSlot?.Parent == session.Capsule;
                    Drop(session, seated ? "guided descent reached the final approach" : "player left the capsule or the game");
                }
            }
        }

        /// <summary>Altura do terreno em (x, z), lida da densidade do voxel (nao precisa de colisores carregados).</summary>
        public static float GroundHeight(Vector3 position)
        {
            try
            {
                if (EstimateSurfaceMethod != null)
                {
                    return ((Vector3)EstimateSurfaceMethod.Invoke(null, new object[] { position })).y;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Terrain height failed: {ex.Message}");
            }
            return SpawnPoint.GetSafePoint(position).y - 4f;
        }

        /// <summary>
        /// Mesmo calculo que o jogo usa para escolher onde a capsula nasce, mas antes tira o ponto de cima da lava
        /// (a busca do jogo so anda alguns metros e nao sai de um lago de lava grande).
        /// </summary>
        public static Vector3 SafeGroundPoint(Vector3 position)
        {
            Vector3 dry = AwayFromLava(position);
            return SpawnPoint.GetSafePoint(new Vector3(dry.x, 0f, dry.z), Vector3.zero, avoidStructure: true);
        }

        /// <summary>Somente para o autoteste: ultimo ponto no chao onde uma capsula foi entregue ao pouso final.</summary>
        public static Vector3 LastDropPoint;

        /// <summary>Margem em volta do ponto que tambem precisa estar sem lava (a capsula pode escorregar depois do pouso).</summary>
        private const float LavaMargin = 15f;

        /// <summary>Desnivel maximo dentro da margem: numa encosta a capsula escorrega para a lava.</summary>
        private const float MaxLavaSlope = 5f;

        /// <summary>O chao em (x, z) esta coberto de lava.</summary>
        public static bool IsLava(Vector3 position)
        {
            WorldSetting world = WorldSetting.Current;
            if (world == null || !world.HasLava)
            {
                return false;
            }
            float ground = GroundHeight(position);
            return world.GetLavaHeight(new Vector3(position.x, ground, position.z)) > ground - 0.2f;
        }

        /// <summary>O ponto e a volta dele (LavaMargin) estao sem lava, num chao quase plano.</summary>
        private static bool IsDry(Vector3 position)
        {
            WorldSetting world = WorldSetting.Current;
            float min = float.MaxValue;
            float max = float.MinValue;
            for (int ring = 0; ring <= 2; ring++)
            {
                int count = ring == 0 ? 1 : 8;
                for (int i = 0; i < count; i++)
                {
                    Vector3 point = position + Quaternion.Euler(0f, i * 45f + ring * 22.5f, 0f) * Vector3.forward * (LavaMargin * ring / 2f);
                    float ground = GroundHeight(point);
                    if (world.GetLavaHeight(new Vector3(point.x, ground, point.z)) > ground - 0.2f)
                    {
                        return false;
                    }
                    min = Mathf.Min(min, ground);
                    max = Mathf.Max(max, ground);
                    if (max - min > MaxLavaSlope)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>O ponto sem lava mais proximo (procura em aneis de 8 m, ate 600 m). Sem lava no mundo: o proprio ponto.</summary>
        public static Vector3 AwayFromLava(Vector3 position)
        {
            WorldSetting world = WorldSetting.Current;
            if (world == null || !world.HasLava || IsDry(position))
            {
                return position;
            }
            for (float ring = 8f; ring <= 600f; ring += 8f)
            {
                int steps = Mathf.Clamp(Mathf.CeilToInt(ring * 2f * Mathf.PI / 8f), 8, 96);
                float start = UnityEngine.Random.Range(0f, 360f);
                for (int i = 0; i < steps; i++)
                {
                    Vector3 candidate = position + Quaternion.Euler(0f, start + i * 360f / steps, 0f) * Vector3.forward * ring;
                    Vector2 clamped = ClampToWorld(new Vector2(candidate.x, candidate.z));
                    candidate = new Vector3(clamped.x, 0f, clamped.y);
                    if (IsDry(candidate))
                    {
                        Plugin.Log.LogInfo($"Landing point ({position.x:0}, {position.z:0}) is on lava; moved {ring:0} m to ({candidate.x:0}, {candidate.z:0}).");
                        return candidate;
                    }
                }
            }
            Plugin.Log.LogWarning($"No ground without lava found near ({position.x:0}, {position.z:0}).");
            return position;
        }
    }
}
