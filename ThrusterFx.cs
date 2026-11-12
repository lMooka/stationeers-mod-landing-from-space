using System;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using HarmonyLib;
using UnityEngine;

namespace LandingFromSpace
{
    /// <summary>
    /// Lado visual (qualquer jogo com o mod): cada um dos propulsores da capsula recebe uma chama propria.
    /// Quando a capsula esta inclinada, os propulsores do lado oposto ao topo ficam mais fortes (sao eles que
    /// sustentam a inclinacao) e os do outro lado mais fracos. A capsula em pe fica com a chama original do jogo.
    /// O tamanho geral continua vindo do jogo (ControlledDescentLerp), que o host sincroniza para todos.
    /// </summary>
    public static class ThrusterFx
    {
        private static readonly AccessTools.FieldRef<EntryEffects, Transform[]> ThrustersRef =
            AccessTools.FieldRefAccess<EntryEffects, Transform[]>("_thrusterEffects");

        private sealed class Tracked
        {
            public LanderCapsule Capsule;
            public float Lerp;
        }

        private static readonly Dictionary<EntryEffects, Tracked> Effects = new Dictionary<EntryEffects, Tracked>();
        private static readonly List<EntryEffects> Dead = new List<EntryEffects>();

        /// <summary>Diferenca maxima entre os lados: 0,8 = o lado forte com 1,8x e o fraco com 0,2x.</summary>
        private const float Gain = 0.8f;

        /// <summary>Inclinacao em que a diferenca entre os lados chega no maximo.</summary>
        private const float FullTiltDegrees = 30f;

        /// <summary>Somente para o autoteste: ultimo fator aplicado a cada propulsor.</summary>
        public static readonly Dictionary<Transform, float> LastFactor = new Dictionary<Transform, float>();

        public static void SetIntensityPostfix(EntryEffects __instance, float lerpFactor)
        {
            try
            {
                if (__instance == null)
                {
                    return;
                }
                if (!Effects.TryGetValue(__instance, out Tracked tracked))
                {
                    tracked = new Tracked { Capsule = __instance.GetComponentInParent<LanderCapsule>() };
                    Effects[__instance] = tracked;
                }
                tracked.Lerp = lerpFactor;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Thruster tracking failed: {ex.Message}");
            }
        }

        private static float EaseOutQuart(float x) => 1f - (1f - x) * (1f - x) * (1f - x) * (1f - x);

        public static void LateTick()
        {
            if (Effects.Count == 0)
            {
                return;
            }
            Dead.Clear();
            foreach (KeyValuePair<EntryEffects, Tracked> pair in Effects)
            {
                EntryEffects effects = pair.Key;
                LanderCapsule capsule = pair.Value.Capsule;
                if (effects == null || capsule == null)
                {
                    Dead.Add(effects);
                    continue;
                }
                try
                {
                    ReentryFx.Update(effects, capsule);
                    Apply(effects, capsule, pair.Value.Lerp);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"Thruster effect failed: {ex.Message}");
                    Dead.Add(effects);
                }
            }
            foreach (EntryEffects effects in Dead)
            {
                Effects.Remove(effects);
            }
            ReentryFx.Cleanup();
        }

        private static void Apply(EntryEffects effects, LanderCapsule capsule, float lerp)
        {
            Transform[] thrusters = ThrustersRef(effects);
            if (thrusters == null || thrusters.Length == 0)
            {
                return;
            }
            Transform body = capsule.Transform;
            Vector3 up = body.up;
            Vector3 lean = new Vector3(up.x, 0f, up.z);
            float tilt = Vector3.Angle(up, Vector3.up);
            if (tilt < 0.5f || lean.sqrMagnitude < 1e-6f)
            {
                // Em pe: deixa a chama do jogo como esta.
                return;
            }
            if (!thrusters[0].gameObject.activeInHierarchy)
            {
                return;
            }
            Vector3 strongSide = -lean.normalized;
            float strength = Gain * Mathf.Clamp01(tilt / FullTiltDegrees);
            float height = EaseOutQuart(Mathf.Clamp01(lerp)) * 0.7f;
            foreach (Transform thruster in thrusters)
            {
                if (thruster == null)
                {
                    continue;
                }
                // Posicao do propulsor em volta do eixo da capsula.
                Vector3 offset = thruster.position - body.position;
                offset -= Vector3.Dot(offset, up) * up;
                offset.y = 0f;
                float side = offset.sqrMagnitude > 1e-4f ? Vector3.Dot(offset.normalized, strongSide) : 0f;
                float factor = Mathf.Max(0.05f, 1f + strength * side);
                LastFactor[thruster] = factor;
                thruster.localScale = new Vector3(0.3f, height * factor * UnityEngine.Random.Range(0.95f, 1.05f), 0.6f);
            }
        }
    }
}
