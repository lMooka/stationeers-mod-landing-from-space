using System;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Inventory;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using UnityEngine;

namespace LandingFromSpace
{
    /// <summary>
    /// Descida guiada, do lado de quem esta dentro da capsula (host ou cliente com o mod): camera de fora,
    /// WASD desvia a capsula e Shift acelera a queda. A posicao e sempre decidida pelo host.
    /// </summary>
    public static class SteerPilot
    {
        private static LanderCapsule _capsule;
        private static long _capsuleId;
        private static long _pendingId;
        private static float _pendingUntil;
        private static bool _local;

        private static Transform _rig;
        private static float _yaw;
        private static float _pitch = DefaultPitch;

        /// <summary>Inclinacao da camera em graus abaixo do horizonte: sempre olhando para o chao.</summary>
        private const float MinPitch = 0f;
        private const float MaxPitch = 89f;
        private static float DefaultPitch => Mathf.Clamp(Plugin.CameraPitch?.Value ?? 65f, MinPitch, MaxPitch);

        private static Vector2 _sentDir;
        private static bool _sentFast;
        private static float _nextSend;

        private static float _height;
        private static float _nextHeight;

        private static GUIStyle _style;
        private static Texture2D _dim;

        public static bool Active => _capsule != null;
        public static long CapsuleId => _capsuleId;

        /// <summary>Somente para o autoteste (fotos da capa): camera fixa e sem a faixa de ajuda.</summary>
        public static float? TestPitch;
        public static float? TestYaw;
        public static float? TestDistance;
        public static bool HideHud;

        /// <summary>Somente para o autoteste: entrada simulada no lugar do teclado.</summary>
        public static Vector2? SimulatedDir;
        public static bool SimulatedFast;

        public static void Begin(LanderCapsule capsule, bool local)
        {
            _local = local;
            _capsule = capsule;
            _capsuleId = capsule.ReferenceId;
            _pendingId = 0;
            _yaw = capsule.Transform.eulerAngles.y;
            _pitch = DefaultPitch;
            _nextHeight = 0f;
            _sentDir = Vector2.zero;
            _sentFast = false;
            if (_rig == null)
            {
                GameObject go = new GameObject("LandingFromSpaceCameraRig");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _rig = go.transform;
            }
            PlaceRig();
            Plugin.Log.LogInfo($"Guided descent of capsule {_capsuleId}.");
        }

        public static void BeginRemote(long capsuleId)
        {
            MapPicker.Close(capsuleId);
            LanderCapsule capsule = Thing.Find<LanderCapsule>(capsuleId);
            if (capsule != null)
            {
                Begin(capsule, local: false);
                return;
            }
            _pendingId = capsuleId;
            _pendingUntil = Time.realtimeSinceStartup + 10f;
        }

        public static void End(long capsuleId)
        {
            if (_pendingId == capsuleId)
            {
                _pendingId = 0;
            }
            if (_capsuleId != capsuleId)
            {
                return;
            }
            LanderCapsule capsule = _capsule;
            _capsule = null;
            _capsuleId = 0;
            RestoreCamera(capsule);
        }

        private static void RestoreCamera(LanderCapsule capsule)
        {
            try
            {
                Human human = InventoryManager.ParentHuman;
                if ((object)human != null && capsule != null && human.ParentSlot?.Parent == capsule)
                {
                    human.CameraRig = capsule.GetCameraPoint(human);
                    if (CameraController.Instance != null)
                    {
                        CameraController.Instance.ControlMode = CameraControlMode.MouseLook;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Camera restore failed: {ex.Message}");
            }
        }

        private static bool InputAllowed => KeyManager.InputState == KeyInputState.Game && !ConsoleWindow.IsOpen;

        public static void Tick()
        {
            if (_pendingId != 0)
            {
                LanderCapsule pending = Thing.Find<LanderCapsule>(_pendingId);
                if (pending != null)
                {
                    Begin(pending, local: false);
                }
                else if (Time.realtimeSinceStartup > _pendingUntil || GameManager.GameState == GameState.None)
                {
                    _pendingId = 0;
                }
            }
            if (_capsuleId == 0)
            {
                return;
            }
            if (_capsule == null || GameManager.GameState == GameState.None || _capsule.IsOpen)
            {
                End(_capsuleId);
                return;
            }

            // Mantem a camera de fora: o jogo reaponta a camera para dentro da capsula quando o jogador assume o controle.
            Human human = InventoryManager.ParentHuman;
            if ((object)human != null && human.ParentSlot?.Parent == _capsule)
            {
                human.CameraRig = _rig;
                if (CameraController.Instance != null && CameraController.Instance.ControlMode != CameraControlMode.Default)
                {
                    CameraController.Instance.ControlMode = CameraControlMode.Default;
                }
            }

            if (Time.unscaledTime >= _nextHeight)
            {
                _nextHeight = Time.unscaledTime + 0.25f;
                Vector3 at = _capsule.Transform.position;
                _height = at.y - ServerPilot.GroundHeight(at);
            }

            Vector2 dir = Vector2.zero;
            bool fast = false;
            if (InputAllowed)
            {
                float sensitivity = Plugin.MouseSensitivity.Value;
                // Mesmo eixo que o jogo usa para olhar (o Input.GetAxis antigo devolve zero aqui).
                float lookX = 0f;
                float lookY = 0f;
                try
                {
                    InputManager input = InputManager.Instance;
                    if (input != null)
                    {
                        lookX = input.GetAxis("LookX");
                        lookY = input.GetAxis("LookY");
                    }
                }
                catch
                {
                    // Sem o gerenciador de entrada: a camera fica parada, o resto continua.
                }
                bool invert = Assets.Scripts.Serialization.Settings.CurrentData != null && Assets.Scripts.Serialization.Settings.CurrentData.InvertMouse;
                _yaw += lookX * sensitivity;
                // Mouse para cima olha para cima (diminui a inclinacao para baixo), como no jogo.
                _pitch = Mathf.Clamp(_pitch - lookY * sensitivity * (invert ? -1f : 1f), MinPitch, MaxPitch);
                // A tela do mapa soltou o mouse; durante a descida ele volta a ficar preso no jogo.
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                float forward = (Input.GetKey(KeyMap.Forward) ? 1f : 0f) - (Input.GetKey(KeyMap.Backward) ? 1f : 0f);
                float right = (Input.GetKey(KeyMap.Right) ? 1f : 0f) - (Input.GetKey(KeyMap.Left) ? 1f : 0f);
                Vector3 world = Quaternion.Euler(0f, _yaw, 0f) * new Vector3(right, 0f, forward);
                dir = new Vector2(world.x, world.z);
                if (dir.sqrMagnitude > 1f)
                {
                    dir.Normalize();
                }
                fast = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            }
            if (SimulatedDir.HasValue)
            {
                dir = SimulatedDir.Value;
                fast = SimulatedFast;
            }

            if (_local)
            {
                ServerPilot.SetInput(_capsuleId, NetworkManager.LocalClientId, dir, fast);
            }
            else if ((dir - _sentDir).sqrMagnitude > 0.0025f || fast != _sentFast || ((dir != Vector2.zero || fast) && Time.unscaledTime >= _nextSend))
            {
                Net.SendInput(_capsuleId, dir, fast);
                _sentDir = dir;
                _sentFast = fast;
                _nextSend = Time.unscaledTime + 0.25f;
            }
        }

        public static void LateTick()
        {
            if (_capsule != null)
            {
                PlaceRig();
            }
        }

        private static void PlaceRig()
        {
            if (_rig == null || _capsule == null)
            {
                return;
            }
            Quaternion rotation = Quaternion.Euler(TestPitch ?? _pitch, TestYaw ?? _yaw, 0f);
            Vector3 center = _capsule.Transform.position;
            _rig.SetPositionAndRotation(center - rotation * Vector3.forward * (TestDistance ?? Plugin.CameraDistance.Value), rotation);
        }

        public static void DrawHud()
        {
            if (_capsule == null || HideHud)
            {
                return;
            }
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = 17, alignment = TextAnchor.MiddleCenter, wordWrap = true };
                _style.normal.textColor = Color.white;
                _dim = new Texture2D(1, 1);
                _dim.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.6f));
                _dim.Apply();
            }
            Vector3 p = _capsule.Transform.position;
            float height = _height;
            string text = $"Guided descent  ·  WASD: steer  ·  Shift: fall faster  ·  Mouse: look\n" +
                          $"Height {Mathf.Max(0f, height):0} m  ·  ({p.x:0}, {p.z:0})";
            float width = Mathf.Min(720f, Screen.width - 32f);
            Rect box = new Rect((Screen.width - width) / 2f, 24f, width, 64f);
            GUI.DrawTexture(box, _dim);
            GUI.Label(box, text, _style);
        }
    }
}
