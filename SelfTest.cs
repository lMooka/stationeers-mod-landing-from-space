#if SELFTEST
// Autoteste de desenvolvimento. So entra na DLL quando compilada com -p:SelfTest=true e so roda se existir
// o arquivo %TEMP%\landingfromspace_selftest.flag. Abre um mundo novo descartavel, espera a capsula ficar parada
// esperando a escolha, escolhe um ponto do mapa e confere onde a capsula e o lander pousaram.
using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using System.IO;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using UnityEngine;

namespace LandingFromSpace
{
    public static class SelfTest
    {
        private static readonly bool Active = File.Exists(Path.Combine(Path.GetTempPath(), "landingfromspace_selftest.flag"));
        private static float _nextStatus;
        private static bool _started;
        private static bool _running;
        private static int _pass;
        private static int _fail;

        public static void Tick()
        {
            if (!Active || _running)
            {
                return;
            }
            if (Time.unscaledTime >= _nextStatus)
            {
                _nextStatus = Time.unscaledTime + 10f;
                Plugin.Log.LogInfo($"SELFTEST status state={GameManager.GameState} init={GameManager.IsInitialized} sessions={ServerPilot.Sessions.Count} t={Time.unscaledTime:0}");
            }
            if (!_started && GameManager.IsInitialized && GameManager.GameState == GameState.None && Time.unscaledTime > 25f)
            {
                _started = true;
                StartWorld();
                return;
            }
            if (_started && GameManager.GameState == GameState.Running && ServerPilot.Sessions.Count > 0)
            {
                _running = true;
                Plugin.Instance.StartCoroutine(RunAll());
            }
        }

        private static async void StartWorld()
        {
            try
            {
                WorldSetting world = WorldSetting.AllWorldSettings.FirstOrDefault(w => !w.IsTutorial && !w.IsDeprecated && w.Id.IndexOf("Lunar", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? WorldSetting.AllWorldSettings.FirstOrDefault(w => !w.IsTutorial && !w.IsDeprecated);
                DifficultySetting difficulty = DifficultySetting.Find("Normal") ?? DifficultySetting.AllSettings.FirstOrDefault();
                StartConditionData start = world.Data.StartConditionDatas.FirstOrDefault(s => s.IsDefault) ?? world.Data.StartConditionDatas.FirstOrDefault();
                // O mundo so guarda uma referencia (Id); os spawns de verdade estao no registro de dados.
                if (start != null && DataCollection.TryGet<StartConditionData>(start.Id, out StartConditionData full) && full != null)
                {
                    start = full;
                }
                Plugin.Log.LogInfo($"SELFTEST iniciando mundo={world.Id} dificuldade={difficulty?.Id} inicio={start?.Id} spawns=[{string.Join(", ", start?.Spawns.Select(s => s.Id + ":" + s.EventType) ?? Enumerable.Empty<string>())}]");
                WorldSetting.SetCurrent(world, start);
                DifficultySetting.SetCurrent(difficulty);
                Assets.Scripts.UI.ImGuiLoadingScreen.WorldName = world.Id;
                await World.StartNewWorld(world.Id);
                Plugin.Log.LogInfo("SELFTEST StartNewWorld retornou");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"SELFTEST falha ao iniciar mundo: {ex}");
            }
        }

        private static void Check(string name, bool ok, string detail = "")
        {
            if (ok) _pass++; else _fail++;
            Plugin.Log.LogInfo($"SELFTEST {(ok ? "PASS" : "FAIL")} {name} {detail}");
        }

        private static IEnumerator RunAll()
        {
            IEnumerator body = Run();
            while (true)
            {
                object current;
                try
                {
                    if (!body.MoveNext())
                    {
                        break;
                    }
                    current = body.Current;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"SELFTEST EXCEPTION: {ex}");
                    _fail++;
                    break;
                }
                yield return current;
            }
            Plugin.Log.LogInfo($"SELFTEST DONE pass={_pass} fail={_fail}");
            yield return new WaitForSecondsRealtime(2f);
            Application.Quit();
        }

        private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

        private static DynamicThing FindLander()
        {
            foreach (long id in Kit.Things)
            {
                if (Thing.Find(id) is Lander lander && lander.ParentSlot == null)
                {
                    return lander;
                }
            }
            return null;
        }

        private static void Shot(string name)
        {
            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "landingfromspace_" + name + ".png");
                ScreenCapture.CaptureScreenshot(path);
                Plugin.Log.LogInfo($"SELFTEST SHOT {path}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"SELFTEST shot failed: {ex.Message}");
            }
        }

        private static void CheckThrusters(LanderCapsule capsule)
        {
            Transform strong = null, weak = null;
            float strongX = float.MinValue, weakX = float.MaxValue;
            foreach (KeyValuePair<Transform, float> pair in ThrusterFx.LastFactor)
            {
                if (pair.Key == null || !pair.Key.IsChildOf(capsule.Transform))
                {
                    continue;
                }
                float x = pair.Key.position.x - capsule.Transform.position.x;
                if (x > strongX) { strongX = x; strong = pair.Key; }
                if (x < weakX) { weakX = x; weak = pair.Key; }
            }
            string all = string.Join(" ", ThrusterFx.LastFactor.Where(p => p.Key != null).Select(p => $"{p.Key.name}@{p.Key.position.x - capsule.Transform.position.x:0.0}:{p.Value:0.00}/{p.Key.localScale.y:0.00}"));
            Check("propulsores do lado do movimento ficam mais fortes", strong != null && weak != null && strong != weak
                && ThrusterFx.LastFactor[strong] > 1.2f && ThrusterFx.LastFactor[weak] < 0.8f && strong.localScale.y > weak.localScale.y,
                $"[{all}]");
        }

        private static string TPath(Transform t, Transform root)
        {
            string path = t.name;
            while (t != root && t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }

        /// <summary>Lista os efeitos da capsula e efeitos de fogo/calor do jogo, para escolher o efeito de reentrada.</summary>
        private static void DumpEffects(LanderCapsule capsule)
        {
            try
            {
                foreach (Transform t in capsule.GetComponentsInChildren<Transform>(true))
                {
                    string comps = string.Join(",", t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name));
                    string mats = "";
                    Renderer r = t.GetComponent<Renderer>();
                    if (r != null)
                    {
                        mats = " mats=" + string.Join(",", r.sharedMaterials.Where(m => m != null).Select(m => m.name + "<" + (m.shader != null ? m.shader.name : "?") + ">"));
                    }
                    Plugin.Log.LogInfo($"SELFTEST DUMP capsule {TPath(t, capsule.Transform)} active={t.gameObject.activeSelf} [{comps}]{mats}");
                }
                EntryEffects fx = capsule.GetComponentInChildren<EntryEffects>(true);
                if (fx != null)
                {
                    Traverse tr = Traverse.Create(fx);
                    Material m = tr.Field("_material").GetValue<Material>();
                    Light l = tr.Field("_light").GetValue<Light>();
                    GameObject[] linked = tr.Field("_linkedGameObjects").GetValue<GameObject[]>();
                    Transform[] thr = tr.Field("_thrusterEffects").GetValue<Transform[]>();
                    string props = "";
                    if (m != null && m.shader != null)
                    {
                        props = string.Join(",", Enumerable.Range(0, m.shader.GetPropertyCount()).Select(i => m.shader.GetPropertyName(i)));
                    }
                    Plugin.Log.LogInfo($"SELFTEST DUMP entry material={m?.name}<{m?.shader?.name}> props=[{props}] intensity={(m != null && m.HasProperty("_Intensity") ? m.GetFloat("_Intensity").ToString("0.00") : "-")}");
                    Plugin.Log.LogInfo($"SELFTEST DUMP entry light={(l != null ? TPath(l.transform, capsule.Transform) + " i=" + l.intensity.ToString("0.0") + " range=" + l.range.ToString("0") : "-")}");
                    Plugin.Log.LogInfo($"SELFTEST DUMP entry linked=[{string.Join(", ", (linked ?? new GameObject[0]).Where(g => g != null).Select(g => TPath(g.transform, capsule.Transform)))}] thrusters=[{string.Join(", ", (thr ?? new Transform[0]).Where(x => x != null).Select(x => TPath(x, capsule.Transform)))}]");
                    foreach (Renderer rr in Resources.FindObjectsOfTypeAll<Renderer>())
                    {
                        if (rr != null && m != null && rr.sharedMaterials.Contains(m))
                        {
                            Plugin.Log.LogInfo($"SELFTEST DUMP entry material used by {TPath(rr.transform, rr.transform.root)} scene={rr.gameObject.scene.name}");
                        }
                    }
                }
                string[] keys = { "entry", "reentry", "heat", "plasma", "fire", "flame", "burn", "thrust", "exhaust", "meteor", "trail", "atmos", "spark", "glow" };
                HashSet<string> seen = new HashSet<string>();
                foreach (ParticleSystem ps in Resources.FindObjectsOfTypeAll<ParticleSystem>())
                {
                    if (ps == null) continue;
                    string path = TPath(ps.transform, ps.transform.root);
                    string low = path.ToLowerInvariant();
                    if (keys.Any(k => low.Contains(k)) && seen.Add(path))
                    {
                        Plugin.Log.LogInfo($"SELFTEST DUMP particles {path} scene={(ps.gameObject.scene.IsValid() ? ps.gameObject.scene.name : "asset")}");
                    }
                }
                foreach (Material mat in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (mat == null) continue;
                    string low = (mat.name + " " + (mat.shader != null ? mat.shader.name : "")).ToLowerInvariant();
                    if (keys.Any(k => low.Contains(k)) && seen.Add("mat:" + mat.name))
                    {
                        Plugin.Log.LogInfo($"SELFTEST DUMP material {mat.name}<{mat.shader?.name}>");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"SELFTEST DUMP failed: {ex}");
            }
        }

        private static IEnumerator Run()
        {
            ServerPilot.Session session = ServerPilot.Sessions.Values.First();
            LanderCapsule capsule = session.Capsule;
            DumpEffects(capsule);
            Check("capsula do jogador local espera a escolha", session.Local && capsule != null && session.Phase == ServerPilot.Phase.Choosing);
            Check("capsula parada (cinematica) e travada", capsule.RigidBody.isKinematic && capsule.IsLocked);
            Check("kit inicial rastreado e reservado para esta capsula", Kit.IsClaimed && Kit.ClaimedBy == capsule.ReferenceId,
                $"itens={Kit.Things.Count}");
            DynamicThing lander = FindLander();
            Check("lander faz parte do kit", lander != null);
            Vector3 landerStart = lander != null ? lander.Transform.position : Vector3.zero;

            yield return new WaitForSeconds(3f);
            Check("tela do mapa aberta", MapPicker.IsOpen && MapPicker.CapsuleId == capsule.ReferenceId);
            Texture2D texture = WorldSetting.Current?.Data?.TerrainSettings?.MiniMapTexture?.Texture;
            Check("mundo tem imagem do mapa", texture != null, $"tamanho={(texture != null ? texture.width + "x" + texture.height : "-")} mundo={TerrainSystem.VoxelConstants.Size}");
            Check("capsula continua parada enquanto escolhe", capsule.RigidBody.isKinematic && !capsule.IsOpen);

            Vector3 start = capsule.Transform.position;
            Vector2 target = new Vector2(start.x + 350f, start.z + 250f);
            MapPicker.Choose(target);
            yield return null;
            Check("escolha leva a capsula para o alto do destino", Flat(capsule.Transform.position, new Vector3(target.x, 0f, target.y)) < 2f,
                $"pos={capsule.Transform.position}");

            float deadline = Time.time + 30f;
            while (session.Phase != ServerPilot.Phase.Steering && ServerPilot.Sessions.ContainsKey(capsule.ReferenceId) && Time.time < deadline)
            {
                yield return new WaitForSeconds(0.25f);
            }
            Check("descida guiada comecou depois do terreno carregar", session.Phase == ServerPilot.Phase.Steering, $"t={30f - (deadline - Time.time):0.0}s");
            yield return null;
            Check("tela do mapa fechou e a camera de fora assumiu", !MapPicker.IsOpen && SteerPilot.Active);
            yield return new WaitForSeconds(1.5f);
            Shot("1_inicio_descida");
            Check("efeito de reentrada forte no inicio da descida", ReentryFx.LastIntensity > 0.5f, $"intensidade={ReentryFx.LastIntensity:0.00}");
            float startHeight = capsule.Transform.position.y - ServerPilot.GroundHeight(capsule.Transform.position) + 1.5f * Plugin.DescentSpeed.Value;
            Check("descida guiada comeca alta", startHeight > ServerPilot.StartHeight - 30f, $"altura={startHeight:0}");

            float y0 = capsule.Transform.position.y;
            yield return new WaitForSeconds(2f);
            float rate = (y0 - capsule.Transform.position.y) / 2f;
            Check("queda lenta", rate > 0.5f && rate < Plugin.DescentSpeed.Value * 1.6f, $"velocidade={rate:0.0} m/s");

            float idleThrust = capsule.ControlledDescentLerp;
            float idleEntry = ReentryFx.LastIntensity;
            Vector3 before = capsule.Transform.position;
            SteerPilot.SimulatedDir = new Vector2(1f, 0f);
            yield return new WaitForSeconds(3f);
            float tilt = Vector3.Angle(capsule.Transform.up, Vector3.up);
            Vector3 lean = capsule.Transform.up;
            float steerThrust = capsule.ControlledDescentLerp;
            Check("chama dos propulsores cresce ao desviar", steerThrust > idleThrust + 0.2f && steerThrust < 1f,
                $"parada={idleThrust:0.00} desviando={steerThrust:0.00}");
            Check("capsula inclina com o fundo na direcao do movimento", tilt > 10f && tilt <= Plugin.MaxTiltAngle.Value + 1f && lean.x < -0.1f,
                $"inclinacao={tilt:0.0} topo={lean}");
            yield return new WaitForEndOfFrame();
            CheckThrusters(capsule);
            Shot("2_desviando");
            SteerPilot.SimulatedDir = Vector2.zero;
            yield return null;
            Vector3 after = capsule.Transform.position;
            Check("WASD desvia a capsula", after.x - before.x > Plugin.SteerSpeed.Value * 2f && Mathf.Abs(after.z - before.z) < 5f,
                $"de={before} para={after}");
            yield return new WaitForSeconds(3f);
            float settled = Vector3.Angle(capsule.Transform.up, Vector3.up);
            Check("capsula volta a ficar em pe ao parar de mover", settled < 3f, $"inclinacao={settled:0.0}");

            // Acelera a queda para o teste nao demorar.
            SteerPilot.SimulatedFast = true;
            yield return new WaitForSeconds(2f);
            Check("chama diminui ao acelerar a queda", capsule.ControlledDescentLerp < idleThrust - 0.1f,
                $"parada={idleThrust:0.00} queda rapida={capsule.ControlledDescentLerp:0.00}");
            yield return new WaitForSeconds(1.5f);
            Check("efeito de reentrada volta na queda rapida", ReentryFx.LastIntensity > 0.4f, $"intensidade={ReentryFx.LastIntensity:0.00}");
            Shot("3_queda_rapida");

            // Fotos para a capa: capsula inclinada, efeito no maximo, camera de lado e perto, sem a faixa de ajuda.
            SteerPilot.SimulatedDir = new Vector2(1f, 0f);
            SteerPilot.HideHud = true;
            SteerPilot.TestDistance = 9f;
            yield return new WaitForSeconds(1.2f);
            float[][] poses = { new[] { 12f, 0f }, new[] { 18f, 330f }, new[] { 8f, 30f }, new[] { 25f, 300f } };
            for (int i = 0; i < poses.Length; i++)
            {
                SteerPilot.TestPitch = poses[i][0];
                SteerPilot.TestYaw = poses[i][1];
                yield return new WaitForSeconds(0.35f);
                Shot($"cover_{i}");
            }
            SteerPilot.TestPitch = null;
            SteerPilot.TestYaw = null;
            SteerPilot.TestDistance = null;
            SteerPilot.HideHud = false;
            SteerPilot.SimulatedDir = Vector2.zero;
            Vector3 handover = capsule.Transform.position;
            deadline = Time.time + 60f;
            while (ServerPilot.Sessions.ContainsKey(capsule.ReferenceId) && Time.time < deadline)
            {
                handover = capsule.Transform.position;
                yield return null;
            }
            SteerPilot.SimulatedDir = null;
            SteerPilot.SimulatedFast = false;
            Check("efeito de reentrada some perto do chao", ReentryFx.LastIntensity < 0.1f, $"intensidade={ReentryFx.LastIntensity:0.00}");
            Check("descida guiada entrega para o pouso final do jogo", !ServerPilot.Sessions.ContainsKey(capsule.ReferenceId) && !SteerPilot.Active);
            Check("capsula entregue em pe", Vector3.Angle(capsule.Transform.up, Vector3.up) < 2f, $"inclinacao={Vector3.Angle(capsule.Transform.up, Vector3.up):0.0}");
            target = new Vector2(handover.x, handover.z);

            deadline = Time.time + 40f;
            while (capsule != null && !capsule.IsOpen && Time.time < deadline)
            {
                yield return new WaitForSeconds(0.5f);
            }
            Check("capsula pousou e abriu", capsule != null && capsule.IsOpen);
            yield return new WaitForSeconds(3f);
            Vector3 landed = capsule.Transform.position;
            float landedGround = ServerPilot.GroundHeight(landed);
            Check("capsula pousou no ponto escolhido", Flat(landed, new Vector3(target.x, 0f, target.y)) < 12f, $"alvo=({target.x:0}, {target.y:0}) pouso={landed}");
            Check("capsula esta no chao", Mathf.Abs(landed.y - landedGround) < 8f, $"y={landed.y:0.0} chao={landedGround:0.0}");

            lander = FindLander();
            Vector3 stillThere = lander != null ? lander.Transform.position : Vector3.zero;
            Check("lander espera o jogador sair da capsula", lander != null && Kit.WaitingForExit && Flat(stillThere, landerStart) < 5f,
                $"lander={stillThere}");

            Human pilot = capsule.Slots[1].Get<Human>();
            bool exited = pilot != null && pilot.ExitAnything();
            yield return new WaitForSeconds(0.2f);
            Vector3 exitPosition = pilot != null ? pilot.Transform.position : Vector3.zero;
            yield return new WaitForSeconds(2.3f);
            Check("jogador saiu da capsula", exited && capsule.Slots[1].Get<Human>() == null);
            lander = FindLander();
            Vector3 falling = lander != null ? lander.Transform.position : Vector3.zero;
            Check("lander vem caindo do ceu", lander != null && falling.y - ServerPilot.GroundHeight(falling) > 30f && Flat(falling, landed) < Plugin.KitRadius.Value + 6f,
                $"lander={falling}");
            yield return new WaitForSeconds(Plugin.KitFallSeconds.Value + 3f);

            lander = FindLander();
            Vector3 landerNow = lander != null ? lander.Transform.position : Vector3.zero;
            float distance = Flat(landerNow, landed);
            Check("lander apareceu no raio em volta da capsula", lander != null && !Kit.WaitingForExit && distance >= 8f && distance <= Plugin.KitRadius.Value + 6f,
                $"distancia={distance:0.0} lander={landerNow}");
            Check("lander esta no chao", lander != null && Mathf.Abs(landerNow.y - ServerPilot.GroundHeight(landerNow)) < 8f, $"y={landerNow.y:0.0}");
            if (pilot != null && lander != null)
            {
                Check("lander longe do jogador", Flat(landerNow, pilot.Transform.position) > 5f, $"jogador={pilot.Transform.position} jogador ao sair={exitPosition}");
            }
        }
    }
}
#endif
