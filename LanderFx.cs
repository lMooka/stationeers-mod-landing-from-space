using System;
using System.Collections;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using UnityEngine;
using UnityEngine.Audio;

namespace LandingFromSpace
{
    /// <summary>
    /// Efeitos da descida do lander (para quem tem o mod): chama de retrofoguete por baixo, luz e som de motor.
    /// No fim o combustivel acaba: a chama engasga, o som falha, o motor desliga e o lander cai os ultimos metros
    /// solto, com o som de impacto. Tudo com pecas do proprio jogo (fogo do motor de foguete, sons da capsula).
    /// O host roda junto com a queda; os clientes recebem a mensagem "K" do host e rodam o mesmo efeito.
    /// </summary>
    public static class LanderFx
    {
        /// <summary>Segundos antes do fim da queda em que o combustivel comeca a acabar.</summary>
        public const float SputterSeconds = 1.6f;

        private static readonly Dictionary<string, AudioClip> Clips = new Dictionary<string, AudioClip>();
        private static AudioMixerGroup _external;
        private static bool _searched;

        /// <summary>Somente para o autoteste.</summary>
        public static int Played;
        public static bool LastBurning;
        public static bool LastCutOff;
        public static bool LastImpact;

        private static void FindAssets()
        {
            if (_searched)
            {
                return;
            }
            _searched = true;
            foreach (AudioClip clip in Resources.FindObjectsOfTypeAll<AudioClip>())
            {
                if (clip != null && !Clips.ContainsKey(clip.name))
                {
                    Clips[clip.name] = clip;
                }
            }
            foreach (AudioMixerGroup group in Resources.FindObjectsOfTypeAll<AudioMixerGroup>())
            {
                if (group != null && group.name == "External")
                {
                    _external = group;
                    break;
                }
            }
        }

        private static AudioClip Clip(string name)
        {
            FindAssets();
            Clips.TryGetValue(name, out AudioClip clip);
            return clip;
        }

        private static AudioSource NewSource(GameObject host, bool loop)
        {
            AudioSource source = host.AddComponent<AudioSource>();
            source.outputAudioMixerGroup = _external;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 15f;
            source.maxDistance = 450f;
            source.dopplerLevel = 0f;
            source.loop = loop;
            source.playOnAwake = false;
            return source;
        }

        /// <summary>
        /// Comeca o efeito num lander que vai descer por <paramref name="duration"/> segundos depois de
        /// <paramref name="delay"/>. So o lander tem retrofoguete; destrocos e caixas caem sem efeito.
        /// </summary>
        public static void Play(DynamicThing thing, float delay, float duration)
        {
            if (thing == null || !(thing is Lander) || GameManager.IsBatchMode || !Plugin.EntryEffect.Value)
            {
                return;
            }
            Plugin.Instance.StartCoroutine(Run(thing, delay, duration));
        }

        private static IEnumerator Run(DynamicThing thing, float delay, float duration)
        {
            if (delay > 0f)
            {
                yield return new WaitForSeconds(delay);
            }
            if (thing == null)
            {
                yield break;
            }
            Played++;
            FindAssets();

            // Pecas do efeito, presas ao lander.
            GameObject root = new GameObject("LandingFromSpaceLanderFx");
            root.transform.SetParent(thing.Transform, false);
            float radius = Kit.Footprint(thing, out Vector3 pivot);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;

            // Propulsores no topo, nos 4 pods (2 bicos cada), com a chama dos propulsores da nave dos comerciantes.
            List<Nozzle> nozzles = MakeNozzles(root.transform);
            Light light = MakeLight(root.transform, radius);
            AudioSource engine = NewSource(root, loop: true);
            engine.clip = Clip("CapsuleDistantEngine");
            AudioSource oneShots = NewSource(root, loop: false);

            float start = Time.time;
            float sputterAt = Mathf.Max(0f, duration - SputterSeconds);
            bool cut = false;
            if (engine.clip != null)
            {
                engine.volume = 0f;
                engine.time = UnityEngine.Random.Range(0f, Mathf.Max(0f, engine.clip.length - 1f));
                engine.Play();
            }
            LastBurning = true;
            LastCutOff = false;
            LastImpact = false;

            float lastY = thing.Transform.position.y;
            float fallSpeed = 0f;
            while (thing != null)
            {
                float t = Time.time - start;
                float power;
                if (t < sputterAt)
                {
                    // Queima normal: liga rapido e fica forte.
                    power = Mathf.Clamp01(t / 0.6f);
                }
                else if (t < duration)
                {
                    // Combustivel acabando: engasga cada vez mais (falhas aleatorias, cada vez mais longas).
                    float k = (t - sputterAt) / SputterSeconds;
                    float noise = Mathf.PerlinNoise(t * 9f, 0.37f);
                    power = noise > 0.25f + 0.6f * k ? Mathf.Lerp(1f, 0.45f, k) : 0.05f;
                }
                else
                {
                    if (!cut)
                    {
                        cut = true;
                        LastCutOff = true;
                        LastBurning = false;
                        AudioClip stop = Clip("CapsuleInternalEngineStop");
                        if (stop != null)
                        {
                            oneShots.PlayOneShot(stop, 0.9f);
                        }
                    }
                    power = 0f;
                }

                SetPower(nozzles, light, engine, power);

                // Depois do corte, espera o lander bater no chao para o som de impacto.
                float y = thing.Transform.position.y;
                float dt = Mathf.Max(1e-4f, Time.deltaTime);
                float speed = (lastY - y) / dt;
                lastY = y;
                if (cut)
                {
                    if (speed > 2f)
                    {
                        fallSpeed = Mathf.Max(fallSpeed, speed);
                    }
                    if (fallSpeed > 2f && speed < 0.5f || t > duration + 4f)
                    {
                        AudioClip impact = Clip("CapsuleImpactGround");
                        if (impact != null && fallSpeed > 2f)
                        {
                            oneShots.PlayOneShot(impact, 1f);
                            LastImpact = true;
                        }
                        break;
                    }
                }
                yield return null;
            }

            // Deixa os sons terminarem e limpa.
            SetPower(nozzles, light, null, 0f);
            yield return new WaitForSeconds(3.5f);
            if (root != null)
            {
                UnityEngine.Object.Destroy(root);
            }
        }

        private static void SetPower(List<Nozzle> nozzles, Light light, AudioSource engine, float power)
        {
            foreach (Nozzle nozzle in nozzles)
            {
                nozzle.Apply(power);
            }
            if (light != null)
            {
                light.intensity = 3.5f * power;
                light.enabled = power > 0.02f;
            }
            if (engine != null && engine.clip != null)
            {
                engine.volume = Mathf.MoveTowards(engine.volume, 0.85f * power, Time.deltaTime * 6f);
                engine.pitch = Mathf.Lerp(0.8f, 1f, power);
                if (power <= 0f && engine.volume <= 0.01f && engine.isPlaying)
                {
                    engine.Stop();
                }
            }
        }

        /// <summary>Altura do fundo do modelo em relacao ao pivo, pela malha.</summary>
        private static float BottomOffset(Thing thing)
        {
            float min = 0f;
            bool any = false;
            foreach (MeshFilter filter in thing.GetComponentsInChildren<MeshFilter>())
            {
                if (filter == null || filter.sharedMesh == null || filter.GetComponentInParent<Thing>() != thing)
                {
                    continue;
                }
                Bounds b = filter.sharedMesh.bounds;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                    float y = thing.Transform.InverseTransformPoint(filter.transform.TransformPoint(corner)).y;
                    if (!any || y < min)
                    {
                        min = y;
                        any = true;
                    }
                }
            }
            return any ? min : -1f;
        }

        /// <summary>
        /// Bicos dos propulsores do lander: 4 pods nas quinas chanfradas do topo (eixos +X, +Z, -X, -Z; as faces
        /// planas ficam nas diagonais), cada um com um par de bicos, um acima do outro, apontando para fora.
        /// Cada bico recebe uma copia da chama de malha do propulsor da nave dos comerciantes.
        /// </summary>
        private const float CornerRadius = 1.56f;
        private const float NozzleHeight = 4.9f;
        private const float PairSpacing = 0.25f;

        private static readonly Vector3[] NozzlePoints = BuildNozzlePoints();

        /// <summary>Um par de bicos lado a lado no alto de cada quina diagonal (45, 135, 225 e 315 graus).</summary>
        private static Vector3[] BuildNozzlePoints()
        {
            List<Vector3> points = new List<Vector3>();
            for (int i = 0; i < 4; i++)
            {
                Vector3 corner = Quaternion.Euler(0f, 45f + i * 90f, 0f) * Vector3.forward;
                Vector3 side = Vector3.Cross(Vector3.up, corner);
                foreach (float s in new[] { -0.5f, 0.5f })
                {
                    points.Add(corner * CornerRadius + side * (s * PairSpacing) + Vector3.up * NozzleHeight);
                }
            }
            return points.ToArray();
        }

        /// <summary>Somente para o autoteste: posicoes atuais dos bicos.</summary>
        public static Vector3[] DebugNozzlePoints => NozzlePoints;

        /// <summary>Comprimento da chama com potencia maxima e largura do bico, em metros.</summary>
        private const float FlameLength = 2.6f;
        private const float FlameWidth = 0.26f;

        /// <summary>Quanto a chama desce alem de sair para fora (0 = horizontal, 1 = 45 graus para baixo).</summary>
        private const float FlameDrop = 1.38f; // tan(54 graus): a chama sai 54 graus abaixo da horizontal

        private static GameObject _thrusterTemplate;
        private static bool _thrusterSearched;

        private static GameObject ThrusterTemplate()
        {
            if (_thrusterSearched)
            {
                return _thrusterTemplate;
            }
            _thrusterSearched = true;
            GameObject fallback = null;
            foreach (Thruster thruster in Resources.FindObjectsOfTypeAll<Thruster>())
            {
                if (thruster == null || thruster.GetComponent<MeshFilter>() == null)
                {
                    continue;
                }
                fallback ??= thruster.gameObject;
                if (thruster.name == "ThrusterMain_L" && thruster.transform.root.name == "LargeShuttle")
                {
                    _thrusterTemplate = thruster.gameObject;
                    break;
                }
            }
            _thrusterTemplate ??= fallback;
            if (_thrusterTemplate == null)
            {
                Plugin.Log.LogWarning("Trader shuttle thruster not found; the lander falls without flames.");
            }
            return _thrusterTemplate;
        }

        private sealed class Nozzle
        {
            public Transform Flame;
            public Vector3 Point;          // bico, no espaco do lander
            public Quaternion Rotation;    // eixo da malha -> direcao da chama
            public Vector3 Axis;           // eixo da malha em que a chama se estende
            public float AxisBase;         // ponto da malha (no eixo) que fica no bico
            public float AxisScale;        // escala no eixo para FlameLength
            public Vector3 BaseScale;      // escala nas outras direcoes para FlameWidth
            public float Flicker;

            public void Apply(float power)
            {
                if (Flame == null)
                {
                    return;
                }
                bool on = power > 0.02f;
                if (Flame.gameObject.activeSelf != on)
                {
                    Flame.gameObject.SetActive(on);
                }
                if (!on)
                {
                    return;
                }
                Flicker = Mathf.Lerp(Flicker, UnityEngine.Random.Range(0.85f, 1.1f), 0.5f);
                float length = AxisScale * Mathf.Lerp(0.25f, 1f, power) * Flicker;
                Vector3 scale = BaseScale;
                scale += Axis * (length - Vector3.Dot(BaseScale, Axis));
                Flame.localScale = scale;
                Flame.localRotation = Rotation;
                // O ponto da malha que representa o bico fica no bico, qualquer que seja o comprimento.
                Flame.localPosition = Point - Rotation * (Axis * (AxisBase * length));
            }
        }

        private static List<Nozzle> MakeNozzles(Transform parent)
        {
            List<Nozzle> nozzles = new List<Nozzle>();
            GameObject template = ThrusterTemplate();
            MeshFilter filter = template != null ? template.GetComponent<MeshFilter>() : null;
            if (filter == null || filter.sharedMesh == null)
            {
                return nozzles;
            }
            Thruster settings = template.GetComponent<Thruster>();
            Vector3 axis = settings == null ? Vector3.up : settings.Axis == UnityEngine.Animations.Axis.X ? Vector3.right : settings.Axis == UnityEngine.Animations.Axis.Z ? Vector3.forward : Vector3.up;
            Bounds bounds = filter.sharedMesh.bounds;
            float min = Vector3.Dot(bounds.center - bounds.extents, axis);
            float max = Vector3.Dot(bounds.center + bounds.extents, axis);
            // A chama sai do lado da malha mais perto do pivo (o bico) e cresce para o outro lado.
            bool growsPositive = Mathf.Abs(min) <= Mathf.Abs(max);
            float span = Mathf.Max(0.001f, max - min);
            float width = 0.001f;
            foreach (Vector3 other in new[] { Vector3.right, Vector3.up, Vector3.forward })
            {
                if (other != axis)
                {
                    width = Mathf.Max(width, Vector3.Dot(bounds.size, other));
                }
            }
            Vector3 perpendicular = Vector3.one * (FlameWidth / width);
            perpendicular -= axis * Vector3.Dot(perpendicular, axis);

            foreach (Vector3 point in NozzlePoints)
            {
                GameObject go = UnityEngine.Object.Instantiate(template, parent);
                go.name = "LandingFromSpaceLanderThruster";
                foreach (Light copied in go.GetComponentsInChildren<Light>(true))
                {
                    copied.enabled = false;
                }
                foreach (Thruster copied in go.GetComponentsInChildren<Thruster>(true))
                {
                    UnityEngine.Object.Destroy(copied);
                }
                foreach (Collider copied in go.GetComponentsInChildren<Collider>(true))
                {
                    UnityEngine.Object.Destroy(copied);
                }
                // A chama da nave tem uma esfera de brilho filha no bico: fica so a chama (senao parecem 16 bicos).
                foreach (Transform child in go.transform)
                {
                    UnityEngine.Object.Destroy(child.gameObject);
                }
                Vector3 outward = new Vector3(point.x, 0f, point.z).normalized;
                Vector3 direction = (outward + Vector3.down * FlameDrop).normalized;
                Vector3 meshDirection = growsPositive ? axis : -axis;
                nozzles.Add(new Nozzle
                {
                    Flame = go.transform,
                    Point = point,
                    Rotation = Quaternion.FromToRotation(meshDirection, direction),
                    Axis = axis,
                    AxisBase = growsPositive ? min : max,
                    AxisScale = FlameLength / span,
                    BaseScale = perpendicular,
                    Flicker = 1f
                });
                go.SetActive(false);
            }
            return nozzles;
        }

        private static Light MakeLight(Transform parent, float radius)
        {
            GameObject go = new GameObject("LandingFromSpaceLanderLight");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.up * 3.5f;
            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(0.55f, 0.7f, 1f);
            light.range = Mathf.Max(20f, radius * 8f);
            light.intensity = 0f;
            light.enabled = false;
            return light;
        }
    }
}
