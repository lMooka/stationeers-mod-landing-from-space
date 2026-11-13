using System;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using UnityEngine;

namespace LandingFromSpace
{
    /// <summary>
    /// Efeito de entrada na atmosfera embaixo da capsula (so para quem tem o mod), montado com pecas do proprio jogo:
    ///  - a esfera de plasma que ja vem na capsula (EntryEffects/Sphere), cujo shader nao existe mais no jogo;
    ///    ela recebe o shader aditivo das chamas dos propulsores, em laranja;
    ///  - a luz de queima da capsula (EntryEffects/BurnLight), que o jogo deixa desligada;
    ///  - uma copia do fogo do motor de foguete (StructureRocketEngine/FireParticles), virada para cima no fundo
    ///    da capsula, como a chama que sobe pela lateral numa reentrada.
    /// A forca vem da velocidade de queda (Shift aumenta) e de um pico no inicio da descida guiada.
    /// </summary>
    public static class ReentryFx
    {
        private sealed class State
        {
            public LanderCapsule Capsule;
            public Transform Root;
            public Renderer Plasma;
            public Material PlasmaMaterial;
            public Vector3 PlasmaScale;
            public Light BurnLight;
            public ParticleSystem Fire;
            public ParticleSystem Rim;
            public Renderer Sheath;
            public Material SheathMaterial;
            public float FireRate;
            public float FireSize;
            public Vector3 LastPosition;
            public bool HasLast;
            public float FallSpeed;
            public float Intensity;
            public float BurstStart = -1f;
        }

        private static readonly Dictionary<LanderCapsule, State> States = new Dictionary<LanderCapsule, State>();
        private static readonly List<LanderCapsule> Dead = new List<LanderCapsule>();
        private static ParticleSystem _fireTemplate;
        private static bool _templateSearched;

        /// <summary>Somente para o autoteste.</summary>
        public static float LastIntensity;

        private static readonly Color PlasmaColor = new Color(1f, 0.45f, 0.12f, 1f);

        /// <summary>Altura acima do chao em que o efeito some (o pouso final do jogo fica sem ele).</summary>
        private const float FadeOutHeight = 120f;
        private const float FadeRange = 60f;
        private const float BurstSeconds = 10f;

        // Ajustes visuais (conferidos por captura de tela).
        // Onda de choque: faixas finas nascendo na borda do escudo de calor e escorrendo para tras, rente ao casco.
        private const float RingHeight = -0.75f;
        private const float RingRadius = 1.4f;
        private const float FireRate = 90f;
        private const float RimRate = 140f;
        // Parabola da onda de choque (perfil y = BowCurve * r^2, vertice abaixo do escudo de calor).
        // Cada particula sai do centro com velocidade radial constante e aceleracao constante para cima da capsula;
        // esse movimento desenha exatamente a parabola: y = (aceleracao / (2 * v^2)) * r^2.
        private const float BowVertex = -1.1f;
        private const float BowCurve = 0.486f;     // altura 1,16x a meia largura, como na referencia
        private const float BowRadius = 2.4f;      // meia largura onde a parabola termina
        private const float FireSpeedMin = 5.6f;   // velocidade radial
        private const float FireSpeedMax = 6.4f;
        private const float FireLifetimeMin = BowRadius / FireSpeedMax * 0.85f;
        private const float FireLifetimeMax = BowRadius / FireSpeedMin;
        private const float FireSizeMin = 0.07f;
        private const float FireSizeMax = 0.15f;
        private const float BowSpeed = 6f;         // velocidade radial media usada para a aceleracao
        private static float BowLift => 2f * BowCurve * BowSpeed * BowSpeed;
        private const float StreakLength = 3f;
        private const float PlasmaBrightness = 0.12f;
        private static readonly Vector3 PlasmaShape = new Vector3(0.9f, 0.55f, 0.9f);
        private static readonly Vector3 PlasmaOffset = new Vector3(0f, -0.5f, 0f);

        /// <summary>O fogo do motor de foguete do jogo, tambem usado na chama do lander.</summary>
        public static ParticleSystem FireTemplateForLander() => FireTemplate();

        private static ParticleSystem FireTemplate()
        {
            if (_templateSearched)
            {
                return _fireTemplate;
            }
            _templateSearched = true;
            foreach (ParticleSystem ps in Resources.FindObjectsOfTypeAll<ParticleSystem>())
            {
                if (ps != null && ps.name == "FireParticles" && ps.transform.root != null && ps.transform.root.name.EndsWith("StructureRocketEngine", StringComparison.Ordinal))
                {
                    _fireTemplate = ps;
                    break;
                }
            }
            if (_fireTemplate == null)
            {
                Plugin.Log.LogWarning("Rocket engine fire not found; the entry effect uses only the plasma and the light.");
            }
            return _fireTemplate;
        }

        private static State Build(LanderCapsule capsule, EntryEffects effects)
        {
            State state = new State { Capsule = capsule, Root = effects.transform };
            Transform sphere = effects.transform.Find("Sphere");
            if (sphere != null)
            {
                Renderer renderer = sphere.GetComponent<Renderer>();
                Shader additive = Shader.Find("Legacy Shaders/Particles/Additive");
                if (renderer != null && additive != null)
                {
                    state.PlasmaMaterial = new Material(additive);
                    Texture texture = renderer.sharedMaterial != null && renderer.sharedMaterial.HasProperty("_MainTex") ? renderer.sharedMaterial.mainTexture : null;
                    if (texture != null)
                    {
                        state.PlasmaMaterial.mainTexture = texture;
                    }
                    state.PlasmaMaterial.SetColor("_TintColor", Color.clear);
                    renderer.sharedMaterial = state.PlasmaMaterial;
                    state.Plasma = renderer;
                    // Escudo de calor: achatado e um pouco abaixo do centro, envolvendo o fundo da capsula.
                    state.PlasmaScale = sphere.localScale;
                    sphere.localScale = Vector3.Scale(sphere.localScale, PlasmaShape);
                    sphere.localPosition += PlasmaOffset;
                    Plugin.Log.LogInfo($"Entry plasma: size {renderer.bounds.size}, texture {(texture != null ? texture.name : "-")}.");
                }
            }
            Transform light = effects.transform.Find("BurnLight");
            if (light != null)
            {
                state.BurnLight = light.GetComponent<Light>();
            }
            BuildSheath(state, capsule);
            ParticleSystem template = FireTemplate();
            if (template != null)
            {
                GameObject fire = UnityEngine.Object.Instantiate(template.gameObject, capsule.Transform);
                fire.name = "LandingFromSpaceEntryFire";
                fire.SetActive(true);
                // Anel na borda do fundo da capsula. O circulo do emissor fica no plano XY do objeto; girando 90 graus
                // em X ele passa a ficar na horizontal da capsula, e as chamas saem para os lados, como o plasma
                // batendo no metal do fundo e escapando pelas bordas.
                fire.transform.localPosition = new Vector3(0f, BowVertex, 0f);
                fire.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                fire.transform.localScale = Vector3.one;
                state.Fire = fire.GetComponent<ParticleSystem>();
                foreach (ParticleSystem child in fire.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (child != state.Fire)
                    {
                        child.gameObject.SetActive(false);
                    }
                }
                ParticleSystem.MainModule main = state.Fire.main;
                // Local: as chamas acompanham a capsula e nao ficam para tras (acima dela) na queda.
                main.simulationSpace = ParticleSystemSimulationSpace.Local;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                main.startLifetime = new ParticleSystem.MinMaxCurve(FireLifetimeMin, FireLifetimeMax);
                main.startSpeed = new ParticleSystem.MinMaxCurve(FireSpeedMin, FireSpeedMax);
                main.startSize = new ParticleSystem.MinMaxCurve(FireSizeMin, FireSizeMax);
                main.gravityModifier = 0f;
                ParticleSystem.ShapeModule shape = state.Fire.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Circle;
                // Saem do vertice (centro, embaixo do escudo), em todas as direcoes da horizontal.
                shape.radius = 0.05f;
                shape.radiusThickness = 1f;
                shape.arc = 360f;
                shape.randomDirectionAmount = 0.05f;
                // Faixas esticadas na direcao do movimento da particula, em vez das bolhas do motor.
                ParticleSystemRenderer render = state.Fire.GetComponent<ParticleSystemRenderer>();
                if (render != null)
                {
                    render.renderMode = ParticleSystemRenderMode.Stretch;
                    render.velocityScale = 0.08f;
                    render.lengthScale = StreakLength;
                    render.cameraVelocityScale = 0f;
                }
                ParticleSystem.TextureSheetAnimationModule sheet = state.Fire.textureSheetAnimation;
                if (sheet.enabled && sheet.numTilesX * sheet.numTilesY > 1)
                {
                    sheet.startFrame = new ParticleSystem.MinMaxCurve(0f, 1f);
                }
                // Branco incandescente na borda, laranja ao escorrer, vermelho sumindo.
                ParticleSystem.ColorOverLifetimeModule color = state.Fire.colorOverLifetime;
                color.enabled = true;
                Gradient gradient = new Gradient();
                gradient.SetKeys(
                    new[] { new GradientColorKey(new Color(1f, 0.95f, 0.85f), 0f), new GradientColorKey(new Color(1f, 0.55f, 0.15f), 0.35f), new GradientColorKey(new Color(0.8f, 0.15f, 0.05f), 1f) },
                    new[] { new GradientAlphaKey(0.5f, 0f), new GradientAlphaKey(0.35f, 0.4f), new GradientAlphaKey(0f, 1f) });
                color.color = new ParticleSystem.MinMaxGradient(gradient);
                ParticleSystem.SizeOverLifetimeModule sizeLife = state.Fire.sizeOverLifetime;
                sizeLife.enabled = true;
                sizeLife.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.4f));
                main.startColor = Color.white;
                // Aceleracao constante para cima da capsula (no objeto girado, "para cima" e -Z): curva em parabola.
                ParticleSystem.VelocityOverLifetimeModule velocity = state.Fire.velocityOverLifetime;
                velocity.enabled = false;
                ParticleSystem.ForceOverLifetimeModule force = state.Fire.forceOverLifetime;
                force.enabled = true;
                force.space = ParticleSystemSimulationSpace.Local;
                force.x = new ParticleSystem.MinMaxCurve(0f);
                force.y = new ParticleSystem.MinMaxCurve(0f);
                force.z = new ParticleSystem.MinMaxCurve(-BowLift);
                ParticleSystem.LimitVelocityOverLifetimeModule limit = state.Fire.limitVelocityOverLifetime;
                limit.enabled = false;
                ParticleSystem.NoiseModule noise = state.Fire.noise;
                noise.enabled = false;
                ParticleSystem.EmissionModule emission = state.Fire.emission;
                emission.enabled = true;
                state.FireRate = FireRate;
                state.FireSize = 1f;
                state.Fire.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

                // Borda incandescente parada no aro do escudo de calor (a "onda de choque").
                GameObject rim = UnityEngine.Object.Instantiate(fire, capsule.Transform);
                rim.name = "LandingFromSpaceEntryRim";
                rim.transform.localPosition = new Vector3(0f, RingHeight + 0.05f, 0f);
                rim.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                rim.transform.localScale = Vector3.one;
                state.Rim = rim.GetComponent<ParticleSystem>();
                ParticleSystem.MainModule rimMain = state.Rim.main;
                rimMain.simulationSpace = ParticleSystemSimulationSpace.Local;
                rimMain.startLifetime = new ParticleSystem.MinMaxCurve(0.08f, 0.16f);
                rimMain.startSpeed = new ParticleSystem.MinMaxCurve(0f, 0.2f);
                rimMain.startSize = new ParticleSystem.MinMaxCurve(0.18f, 0.32f);
                ParticleSystem.VelocityOverLifetimeModule rimVelocity = state.Rim.velocityOverLifetime;
                rimVelocity.enabled = false;
                ParticleSystemRenderer rimRender = state.Rim.GetComponent<ParticleSystemRenderer>();
                if (rimRender != null)
                {
                    rimRender.renderMode = ParticleSystemRenderMode.Billboard;
                }
                ParticleSystem.ColorOverLifetimeModule rimColor = state.Rim.colorOverLifetime;
                rimColor.enabled = true;
                Gradient rimGradient = new Gradient();
                rimGradient.SetKeys(
                    new[] { new GradientColorKey(new Color(1f, 0.97f, 0.9f), 0f), new GradientColorKey(new Color(1f, 0.6f, 0.2f), 1f) },
                    new[] { new GradientAlphaKey(0.45f, 0f), new GradientAlphaKey(0f, 1f) });
                rimColor.color = new ParticleSystem.MinMaxGradient(rimGradient);
                state.Rim.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
            return state;
        }

        public static void Update(EntryEffects effects, LanderCapsule capsule)
        {
            if (!Plugin.EntryEffect.Value)
            {
                return;
            }
            if (!States.TryGetValue(capsule, out State state))
            {
                state = Build(capsule, effects);
                States[capsule] = state;
            }
            float dt = Mathf.Max(1e-4f, Time.deltaTime);
            Vector3 position = capsule.Transform.position;
            if (state.HasLast)
            {
                float fall = (state.LastPosition.y - position.y) / dt;
                state.FallSpeed = Mathf.Lerp(state.FallSpeed, fall, 1f - Mathf.Exp(-4f * dt));
            }
            state.LastPosition = position;
            state.HasLast = true;

            float height = position.y - ServerPilot.GroundHeight(position);
            float altitude = Mathf.Clamp01((height - FadeOutHeight) / FadeRange);
            bool active = SteerPilot.Active && SteerPilot.CapsuleId == capsule.ReferenceId || altitude > 0f && capsule.IsDescending;
            if (active && state.BurstStart < 0f && altitude > 0.5f)
            {
                state.BurstStart = Time.time;
            }
            float burst = state.BurstStart >= 0f ? Mathf.Clamp01(1f - (Time.time - state.BurstStart) / BurstSeconds) : 0f;
            float speed = Mathf.Clamp01((state.FallSpeed - 7f) / 15f);
            float target = active ? Mathf.Max(burst, speed) * altitude : 0f;
            state.Intensity = Mathf.Lerp(state.Intensity, target, 1f - Mathf.Exp(-3f * dt));
            LastIntensity = state.Intensity;
            Render(state);
        }

        private static void Render(State state)
        {
            float k = state.Intensity;
            bool on = k > 0.03f;
            if (state.Sheath != null)
            {
                state.Sheath.gameObject.SetActive(on);
                if (on)
                {
                    Color tint = PlasmaColor * (SheathBrightness * k);
                    tint.a = SheathBrightness * k;
                    state.SheathMaterial.SetColor("_TintColor", tint);
                    // As faixas escorrem para tras (para cima da capsula) e tremulam um pouco.
                    state.SheathMaterial.mainTextureOffset = new Vector2(Time.time * 0.07f, -Time.time * SheathFlow);
                }
            }
            if (state.Plasma != null)
            {
                state.Plasma.gameObject.SetActive(on);
                if (on)
                {
                    Color color = PlasmaColor * (PlasmaBrightness * k);
                    color.a = PlasmaBrightness * k;
                    state.PlasmaMaterial.SetColor("_TintColor", color);
                }
            }
            if (state.BurnLight != null)
            {
                state.BurnLight.gameObject.SetActive(on);
                state.BurnLight.enabled = on;
                if (on)
                {
                    state.BurnLight.color = PlasmaColor;
                    state.BurnLight.intensity = 1.5f * k;
                    state.BurnLight.range = Mathf.Max(state.BurnLight.range, 25f);
                }
            }
            if (state.Fire != null)
            {
                ParticleSystem.EmissionModule emission = state.Fire.emission;
                emission.rateOverTimeMultiplier = state.FireRate * (0.15f + 0.85f * k);
                ParticleSystem.MainModule main = state.Fire.main;
                float size = state.FireSize * (0.5f + 0.5f * k);
                main.startSize = new ParticleSystem.MinMaxCurve(FireSizeMin * size, FireSizeMax * size);
                if (on && !state.Fire.isPlaying)
                {
                    state.Fire.Play(true);
                }
                else if (!on && state.Fire.isEmitting)
                {
                    state.Fire.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }
            if (state.Rim != null)
            {
                ParticleSystem.EmissionModule rimEmission = state.Rim.emission;
                rimEmission.rateOverTimeMultiplier = RimRate * k;
                if (on && !state.Rim.isPlaying)
                {
                    state.Rim.Play(true);
                }
                else if (!on && state.Rim.isEmitting)
                {
                    state.Rim.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }
        }

        // Veu de plasma: a mesma parabola das particulas, girada em volta do eixo da capsula (uma "tigela" de onda de choque).
        private const int SheathSegments = 40;
        private const int SheathRings = 12;
        private const float SheathInnerRadius = 0.5f;
        private const float SheathBrightness = 0.9f;
        private const float SheathFlow = 1.6f;
        private static Texture2D _streaks;

        /// <summary>Textura de faixas verticais suaves (repete nos dois sentidos) para o veu.</summary>
        private static Texture2D StreakTexture()
        {
            if (_streaks != null)
            {
                return _streaks;
            }
            const int width = 64;
            const int height = 128;
            System.Random random = new System.Random(1234);
            float[] columns = new float[width];
            for (int x = 0; x < width; x++)
            {
                columns[x] = (float)random.NextDouble();
            }
            _streaks = new Texture2D(width, height, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, name = "LandingFromSpaceStreaks" };
            Color[] pixels = new Color[width * height];
            for (int x = 0; x < width; x++)
            {
                // Faixa suavizada com as vizinhas, para nao ficar serrilhada.
                float column = (columns[(x + width - 1) % width] + 2f * columns[x] + columns[(x + 1) % width]) / 4f;
                column = Mathf.Pow(column, 2.2f);
                float phase = (float)random.NextDouble() * Mathf.PI * 2f;
                for (int y = 0; y < height; y++)
                {
                    float wave = 0.65f + 0.35f * Mathf.Sin(phase + y / (float)height * Mathf.PI * 2f * 2f);
                    float v = Mathf.Clamp01(column * wave * 1.6f);
                    pixels[y * width + x] = new Color(v, v, v, v);
                }
            }
            _streaks.SetPixels(pixels);
            _streaks.Apply();
            return _streaks;
        }

        private static void BuildSheath(State state, LanderCapsule capsule)
        {
            Shader additive = Shader.Find("Legacy Shaders/Particles/Additive");
            if (additive == null)
            {
                return;
            }
            Vector3[] vertices = new Vector3[(SheathSegments + 1) * SheathRings];
            Vector2[] uvs = new Vector2[vertices.Length];
            Color[] colors = new Color[vertices.Length];
            for (int r = 0; r < SheathRings; r++)
            {
                float t = r / (float)(SheathRings - 1);
                // Mesma parabola das particulas: do fundo, por baixo do escudo, ate a meia largura final.
                float radius = Mathf.Lerp(SheathInnerRadius, BowRadius, t);
                float y = BowVertex + BowCurve * radius * radius;
                // Mais forte perto da borda do escudo (onde a onda de choque encosta) e sumindo para as pontas.
                float edge = Mathf.InverseLerp(SheathInnerRadius, RingRadius, radius);
                float alpha = radius <= RingRadius ? Mathf.Lerp(0.35f, 1f, edge) : Mathf.Pow(1f - Mathf.InverseLerp(RingRadius, BowRadius, radius), 1.6f);
                for (int i = 0; i <= SheathSegments; i++)
                {
                    float a = i / (float)SheathSegments * Mathf.PI * 2f;
                    int index = r * (SheathSegments + 1) + i;
                    vertices[index] = new Vector3(Mathf.Cos(a) * radius, y, Mathf.Sin(a) * radius);
                    uvs[index] = new Vector2(i / (float)SheathSegments * 3f, t);
                    colors[index] = new Color(1f, 1f, 1f, alpha);
                }
            }
            int[] triangles = new int[(SheathRings - 1) * SheathSegments * 6];
            int k = 0;
            for (int r = 0; r < SheathRings - 1; r++)
            {
                for (int i = 0; i < SheathSegments; i++)
                {
                    int a = r * (SheathSegments + 1) + i;
                    int b = a + SheathSegments + 1;
                    triangles[k++] = a; triangles[k++] = b; triangles[k++] = a + 1;
                    triangles[k++] = a + 1; triangles[k++] = b; triangles[k++] = b + 1;
                }
            }
            Mesh mesh = new Mesh { name = "LandingFromSpaceSheath", vertices = vertices, uv = uvs, colors = colors, triangles = triangles };
            mesh.RecalculateBounds();

            GameObject go = new GameObject("LandingFromSpacePlasmaSheath");
            go.transform.SetParent(capsule.Transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            state.SheathMaterial = new Material(additive) { mainTexture = StreakTexture() };
            state.SheathMaterial.mainTextureScale = new Vector2(1f, 1f);
            state.SheathMaterial.SetColor("_TintColor", Color.clear);
            renderer.sharedMaterial = state.SheathMaterial;
            state.Sheath = renderer;
            go.SetActive(false);
        }

        /// <summary>Esquece capsulas destruidas.</summary>
        public static void Cleanup()
        {
            if (States.Count == 0)
            {
                return;
            }
            Dead.Clear();
            foreach (KeyValuePair<LanderCapsule, State> pair in States)
            {
                if (pair.Key == null)
                {
                    Dead.Add(pair.Key);
                }
            }
            foreach (LanderCapsule capsule in Dead)
            {
                States.Remove(capsule);
            }
        }
    }
}
