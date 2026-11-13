#if SELFTEST
// Autoteste de desenvolvimento. So entra na DLL quando compilada com -p:SelfTest=true e so roda se existir
// o arquivo %TEMP%\landingfromspace_selftest.flag. Abre um mundo novo descartavel, espera a capsula ficar parada
// esperando a escolha, escolhe um ponto do mapa e confere onde a capsula e o kit inicial pousaram.
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
        private static readonly string FlagPath = Path.Combine(Path.GetTempPath(), "landingfromspace_selftest.flag");
        private static readonly bool Active = File.Exists(FlagPath);

        /// <summary>Parte do arquivo de sinal do selftest.ps1 ("Mundo|Condicao"), ou null para a padrao.</summary>
        private static string Requested(int index)
        {
            try
            {
                string[] parts = File.ReadAllText(FlagPath).Trim().Split('|');
                string text = index < parts.Length ? parts[index].Trim() : "";
                return text.Length == 0 || text == "1" ? null : text;
            }
            catch
            {
                return null;
            }
        }
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
                string wanted = Requested(0) ?? "Vulcan2";
                WorldSetting world = WorldSetting.AllWorldSettings.FirstOrDefault(w => !w.IsTutorial && !w.IsDeprecated && string.Equals(w.Id, wanted, StringComparison.OrdinalIgnoreCase))
                    ?? WorldSetting.AllWorldSettings.FirstOrDefault(w => !w.IsTutorial && !w.IsDeprecated && w.Id.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? WorldSetting.AllWorldSettings.FirstOrDefault(w => !w.IsTutorial && !w.IsDeprecated);
                DifficultySetting difficulty = DifficultySetting.Find("Normal") ?? DifficultySetting.AllSettings.FirstOrDefault();
                string requested = Requested(1);
                StartConditionData start = (requested != null ? world.Data.StartConditionDatas.FirstOrDefault(s => s.Id == requested) : null)
                    ?? world.Data.StartConditionDatas.FirstOrDefault(s => s.IsDefault) ?? world.Data.StartConditionDatas.FirstOrDefault();
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

        /// <summary>Itens soltos do kit reservado (lander, caixa do brutal, tanques...).</summary>
        private static List<DynamicThing> KitRoots()
        {
            List<DynamicThing> roots = new List<DynamicThing>();
            foreach (long id in Kit.Things)
            {
                if (Thing.Find(id) is DynamicThing thing && thing.ParentSlot == null)
                {
                    roots.Add(thing);
                }
            }
            return roots;
        }

        private static string Describe(IEnumerable<DynamicThing> things)
        {
            return string.Join(" ", things.Select(t => $"{t.DisplayName}@({t.Transform.position.x:0},{t.Transform.position.y:0},{t.Transform.position.z:0})"));
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

        /// <summary>Lista as fontes do jogo (para a interface do mod seguir o visual do jogo).</summary>
        private static void DumpFonts()
        {
            try
            {
                foreach (Font font in Resources.FindObjectsOfTypeAll<Font>())
                {
                    if (font != null)
                    {
                        Plugin.Log.LogInfo($"SELFTEST FONT legacy {font.name} dynamic={font.dynamic} size={font.fontSize}");
                    }
                }
                Type tmp = AccessTools.TypeByName("TMPro.TMP_FontAsset");
                if (tmp != null)
                {
                    foreach (UnityEngine.Object asset in Resources.FindObjectsOfTypeAll(tmp))
                    {
                        object source = AccessTools.Property(tmp, "sourceFontFile")?.GetValue(asset, null);
                        Plugin.Log.LogInfo($"SELFTEST FONT tmp {asset.name} source={(source as UnityEngine.Object)?.name ?? "-"}");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"SELFTEST font dump failed: {ex.Message}");
            }
        }

        private static IEnumerator Run()
        {
            DumpFonts();
            ServerPilot.Session session = ServerPilot.Sessions.Values.First();
            LanderCapsule capsule = session.Capsule;
            DumpEffects(capsule);
            Check("capsula do jogador local espera a escolha", session.Local && capsule != null && session.Phase == ServerPilot.Phase.Choosing);
            Check("capsula parada (cinematica) e travada", capsule.RigidBody.isKinematic && capsule.IsLocked);
            Check($"kit inicial rastreado e reservado para esta capsula ({Kit.LastSources})", Kit.IsClaimed && Kit.ClaimedBy == capsule.ReferenceId,
                $"itens={Kit.Things.Count}");
            List<DynamicThing> kit = KitRoots();
            Dictionary<long, Vector3> kitStart = kit.ToDictionary(t => t.ReferenceId, t => t.Transform.position);
            Check("kit tem itens soltos para levar", kit.Count > 0, $"[{Describe(kit)}]");

            yield return new WaitForSeconds(3f);
            Check("tela do mapa aberta", MapPicker.IsOpen && MapPicker.CapsuleId == capsule.ReferenceId);
            if (WorldSetting.Current != null && WorldSetting.Current.HasLava)
            {
                Check("camada de lava pronta ao abrir o mapa (imagem embutida)", LavaMap.Ready && LavaMap.Overlay != null, $"progresso={LavaMap.Progress * 100f:0}%");
                float lavaDeadline = Time.realtimeSinceStartup + 40f;
                while (!LavaMap.Ready && Time.realtimeSinceStartup < lavaDeadline)
                {
                    yield return null;
                }
                Vector3 c0 = capsule.Transform.position;
                LandingLog.RecordLanding(new Vector3(c0.x - 600f, 0f, c0.z + 300f), "Earlier");
                LandingLog.RecordLanding(new Vector3(c0.x + 200f, 0f, c0.z - 500f), "Earlier");
                LandingLog.RecordLander(new Vector3(c0.x - 590f, 0f, c0.z + 310f), "Earlier");
                MapPicker.TestSelect(new Vector2(c0.x + 350f, c0.z + 250f));
                yield return new WaitForEndOfFrame();
                Shot("0_mapa_lava");
            }
            Texture2D texture = WorldSetting.Current?.Data?.TerrainSettings?.MiniMapTexture?.Texture;
            Check("mundo tem imagem do mapa", texture != null, $"tamanho={(texture != null ? texture.width + "x" + texture.height : "-")} mundo={TerrainSystem.VoxelConstants.Size}");
            Check("capsula continua parada enquanto escolhe", capsule.RigidBody.isKinematic && !capsule.IsOpen);

            Vector3 start = capsule.Transform.position;
            Vector2 target = new Vector2(start.x + 350f, start.z + 250f);
            MapPicker.Choose(target);
            yield return null;
            Check("escolha leva a capsula para o alto do destino (ou do chao seguro mais proximo)", Flat(capsule.Transform.position, session.Target) < 2f,
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
            float fy0 = capsule.Transform.position.y;
            yield return new WaitForSeconds(0.4f);
            float early = (fy0 - capsule.Transform.position.y) / 0.4f;
            Check("Shift acelera a queda aos poucos", early < (Plugin.DescentSpeed.Value + Plugin.FastDescentSpeed.Value) / 2f, $"velocidade logo depois={early:0.0} m/s");
            yield return new WaitForSeconds(2.6f);
            float fy1 = capsule.Transform.position.y;
            yield return new WaitForSeconds(0.4f);
            float later = (fy1 - capsule.Transform.position.y) / 0.4f;
            Check("Shift chega perto da velocidade maxima", later > Plugin.FastDescentSpeed.Value * 0.8f, $"velocidade depois de 3 s={later:0.0} m/s");
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
            Vector3 dropPoint = ServerPilot.LastDropPoint;
            Check("capsula pousou onde a descida guiada terminou (ou no chao seguro mais proximo)", Flat(landed, dropPoint) < 12f,
                $"alvo=({target.x:0}, {target.y:0}) entregue=({dropPoint.x:0}, {dropPoint.z:0}) pouso={landed}");
            Check("capsula nao pousou na lava", !ServerPilot.IsLava(landed), $"pouso={landed}");
            LandingSite newest = LandingLog.Current.Recent.FirstOrDefault();
            Check("pouso registrado como o mais recente", newest != null && Vector2.Distance(newest.Point, new Vector2(dropPoint.x, dropPoint.z)) < 1f
                && LandingLog.Current.Recent.Count <= LandingLog.MaxRecent, $"recentes={LandingLog.Current.Recent.Count}");
            float capsuleRadius = Kit.Footprint(capsule, out _);
            Check("capsula esta no chao", Mathf.Abs(landed.y - landedGround) < 8f, $"y={landed.y:0.0} chao={landedGround:0.0}");

            kit = KitRoots();
            Check("kit espera o jogador sair da capsula", kit.Count > 0 && Kit.WaitingForExit
                && kit.All(t => kitStart.TryGetValue(t.ReferenceId, out Vector3 p0) && Flat(t.Transform.position, p0) < 5f), $"[{Describe(kit)}]");

            Human pilot = capsule.Slots[1].Get<Human>();
            bool exited = pilot != null && pilot.ExitAnything();
            yield return new WaitForSeconds(0.2f);
            Vector3 exitPosition = pilot != null ? pilot.Transform.position : Vector3.zero;
            yield return new WaitForSeconds(2.3f);
            Check("jogador saiu da capsula", exited && capsule.Slots[1].Get<Human>() == null);
            kit = KitRoots();
            Check("kit vem caindo do ceu", kit.Any(t => t.Transform.position.y - ServerPilot.GroundHeight(t.Transform.position) > 30f
                && Flat(t.Transform.position, landed) < Plugin.KitRadius.Value + 6f), $"[{Describe(kit)}]");
            // Cada item comeca a cair 1,5 s depois do anterior.
            yield return new WaitForSeconds(Plugin.KitFallSeconds.Value + 3f + 1.5f * Mathf.Max(0, kit.Count - 1));

            kit = KitRoots();
            // Posicoes no pouso de cada item (a fisica pode mover depois, ex.: tanque rolando numa encosta).
            Dictionary<long, Vector3> at = kit.ToDictionary(t => t.ReferenceId, t => Kit.Landed.TryGetValue(t.ReferenceId, out Vector3 l) ? l : t.Transform.position);
            float drift = kit.Select(t => Flat(t.Transform.position, at[t.ReferenceId])).DefaultIfEmpty(0f).Max();
            Plugin.Log.LogInfo($"SELFTEST kit: maior deslocamento pela fisica depois do pouso={drift:0.0} m");
            DynamicThing mainItem = kit.FirstOrDefault(t => t is Lander) ?? kit.FirstOrDefault();
            Check("posicao do lander registrada", mainItem != null && LandingLog.Current.Landers.Any(l => Vector2.Distance(l.Point, new Vector2(at[mainItem.ReferenceId].x, at[mainItem.ReferenceId].z)) < 6f),
                $"landers=[{string.Join(" ", LandingLog.Current.Landers.Select(l => $"({l.X:0},{l.Z:0})"))}]");
            Plugin.Log.LogInfo($"SELFTEST kit depois da queda: [{Describe(kit)}] capsula=({landed.x:0},{landed.z:0}) jogador={(pilot != null ? pilot.Transform.position.ToString() : "-")}");
            // Arranjo original do jogo: tamanho e menor distancia entre itens.
            Vector3 startCenter = Vector3.zero;
            foreach (Vector3 p0 in kitStart.Values) startCenter += p0;
            startCenter /= Mathf.Max(1, kitStart.Count);
            float layoutRadius = kitStart.Values.Select(p0 => Flat(p0, startCenter)).DefaultIfEmpty(0f).Max();
            float nearestCapsule = kit.Select(t => Flat(at[t.ReferenceId], landed)).DefaultIfEmpty(0f).Min();
            float farthestCapsule = kit.Select(t => Flat(at[t.ReferenceId], landed)).DefaultIfEmpty(0f).Max();
            bool lavaWorld = WorldSetting.Current != null && WorldSetting.Current.HasLava;
            Check("todos os itens do kit perto da capsula", kit.Count > 0 && !Kit.WaitingForExit
                && nearestCapsule >= (lavaWorld ? 1.5f : 6f) && farthestCapsule <= Plugin.KitRadius.Value + 2f * layoutRadius + 6f,
                $"mais perto={nearestCapsule:0.0} mais longe={farthestCapsule:0.0} raio do arranjo={layoutRadius:0.0}");
            if (WorldSetting.Current != null && WorldSetting.Current.HasLava)
            {
                float gap = kit.Select(t =>
                {
                    float r = Kit.Footprint(t, out Vector3 pivot);
                    return Flat(at[t.ReferenceId] + pivot, landed) - capsuleRadius - r;
                }).DefaultIfEmpty(99f).Min();
                Check("mundo com lava: item mais proximo a ate 3 m da borda da capsula", gap <= 3f, $"folga={gap:0.0} m raio capsula={capsuleRadius:0.0}");
            }
            Check("todos os itens do kit no chao", kit.All(t => Mathf.Abs(t.Transform.position.y - ServerPilot.GroundHeight(t.Transform.position)) < 8f));
            if (pilot != null)
            {
                Check("itens do kit pousam longe do jogador", kit.All(t => Flat(at[t.ReferenceId], exitPosition) > 4f), $"jogador ao sair={exitPosition}");
            }
            if (kit.Count > 1)
            {
                // O arranjo foi so deslocado: as distancias entre os itens devem ficar parecidas com as originais.
                float worst = 0f;
                for (int i = 0; i < kit.Count; i++)
                    for (int j = i + 1; j < kit.Count; j++)
                    {
                        if (!kitStart.TryGetValue(kit[i].ReferenceId, out Vector3 a0) || !kitStart.TryGetValue(kit[j].ReferenceId, out Vector3 b0)) continue;
                        worst = Mathf.Max(worst, Mathf.Abs(Flat(at[kit[i].ReferenceId], at[kit[j].ReferenceId]) - Flat(a0, b0)));
                    }
                Check("kit mantem o arranjo original", worst < 3f, $"maior diferenca de distancia entre itens={worst:0.0} m");
            }
        }
    }
}
#endif
