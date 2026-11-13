using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using UnityEngine;

namespace LandingFromSpace
{
    /// <summary>
    /// Cliente: enquanto uma capsula esta em descida, ela fica sem fisica local e so segue a posicao do servidor.
    ///
    /// O servidor move a capsula sem fisica (cinematica) e manda posicao com velocidade zero; essa marca nao vai para
    /// os clientes. No cliente a capsula continuava com gravidade: caia entre uma atualizacao e outra e era puxada de
    /// volta a cada pacote, piscando ("capsula fantasma"). O jogo ja tem o modo certo para isso (IsForceKinematic):
    /// o objeto fica cinematico e desliza ate a ultima posicao recebida.
    /// </summary>
    public static class ClientSync
    {
        private static readonly List<LanderCapsule> Capsules = new List<LanderCapsule>();
        private static readonly HashSet<LanderCapsule> Forced = new HashSet<LanderCapsule>();
        private static float _nextScan;

        /// <summary>Somente para diagnostico: quantas capsulas estao seguindo o servidor agora.</summary>
        public static int ForcedCount => Forced.Count;

        private static readonly Dictionary<DynamicThing, float> Following = new Dictionary<DynamicThing, float>();
        private static readonly List<DynamicThing> FollowDone = new List<DynamicThing>();

        /// <summary>Cliente: o item (lander caindo do ceu) segue so o servidor pelos proximos segundos.</summary>
        public static void FollowServer(DynamicThing thing, float seconds)
        {
            if (thing == null || GameManager.RunSimulation)
            {
                return;
            }
            Following[thing] = Time.time + seconds + 0.5f;
        }

        private static void TickFollowing()
        {
            if (Following.Count == 0)
            {
                return;
            }
            FollowDone.Clear();
            foreach (KeyValuePair<DynamicThing, float> pair in Following)
            {
                DynamicThing thing = pair.Key;
                if (thing == null)
                {
                    FollowDone.Add(thing);
                    continue;
                }
                Rigidbody body = thing.RigidBody;
                if (Time.time < pair.Value)
                {
                    if (body != null)
                    {
                        body.isKinematic = true;
                    }
                    thing.IsForceKinematic = true;
                }
                else
                {
                    // Combustivel acabou no servidor: a fisica normal volta para a queda final e o pouso.
                    thing.IsForceKinematic = false;
                    if (body != null)
                    {
                        body.isKinematic = false;
                    }
                    FollowDone.Add(thing);
                }
            }
            foreach (DynamicThing thing in FollowDone)
            {
                Following.Remove(thing);
            }
        }

        public static void Tick()
        {
            TickFollowing();
            if (GameManager.RunSimulation || GameManager.GameState == GameState.None)
            {
                // Host e singleplayer simulam a capsula de verdade.
                if (Forced.Count > 0 || Capsules.Count > 0)
                {
                    Forced.Clear();
                    Capsules.Clear();
                }
                return;
            }
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 0.5f;
                Capsules.Clear();
                Capsules.AddRange(Object.FindObjectsOfType<LanderCapsule>());
            }
            foreach (LanderCapsule capsule in Capsules)
            {
                if (capsule == null)
                {
                    continue;
                }
                bool descending = capsule.IsDescending && !capsule.IsOpen && capsule.ParentSlot == null;
                Rigidbody body = capsule.RigidBody;
                if (body == null)
                {
                    continue;
                }
                if (descending)
                {
                    if (Forced.Add(capsule))
                    {
                        body.velocity = Vector3.zero;
                        body.angularVelocity = Vector3.zero;
                    }
                    body.isKinematic = true;
                    capsule.IsForceKinematic = true;
                }
                else if (Forced.Remove(capsule))
                {
                    // Pousou: devolve a fisica normal do cliente.
                    capsule.IsForceKinematic = false;
                    body.isKinematic = false;
                }
            }
            Forced.RemoveWhere(c => c == null);
        }
    }
}
