using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.UI;
using TerrainSystem;
using UnityEngine;

namespace LandingFromSpace
{
    /// <summary>
    /// Tela do mapa para quem esta dentro da capsula (host ou cliente com o mod): clique no mapa do mundo
    /// e confirme. Usa a mesma imagem e a mesma conversao de coordenadas do minimapa do jogo.
    /// </summary>
    public static class MapPicker
    {
        private const string InputKey = "LandingFromSpaceMap";

        private static LanderCapsule _capsule;
        private static long _capsuleId;
        private static long _pendingId;
        private static float _pendingUntil;
        private static bool _local;
        private static bool _sent;
        private static Vector2? _selected;
        private static Vector2? _hover;
        private static bool _inputTaken;

        private static GUIStyle _title;
        private static GUIStyle _text;
        private static GUIStyle _marker;
        private static Texture2D _dim;

        public static bool IsOpen => _capsuleId != 0;
        public static long CapsuleId => _capsuleId;

        public static void Open(LanderCapsule capsule)
        {
            _local = true;
            Begin(capsule);
        }

        public static void OpenRemote(long capsuleId)
        {
            _local = false;
            LanderCapsule capsule = Thing.Find<LanderCapsule>(capsuleId);
            if (capsule != null)
            {
                Begin(capsule);
                return;
            }
            // A capsula pode ainda nao ter chegado neste cliente.
            _pendingId = capsuleId;
            _pendingUntil = Time.realtimeSinceStartup + 10f;
        }

        private static void Begin(LanderCapsule capsule)
        {
            _capsule = capsule;
            _capsuleId = capsule.ReferenceId;
            _pendingId = 0;
            _sent = false;
            _selected = null;
            _hover = null;
            TakeInput();
            Plugin.Log.LogInfo($"Choosing landing point for capsule {_capsuleId}.");
        }

        public static void Close(long capsuleId)
        {
            if (_pendingId == capsuleId)
            {
                _pendingId = 0;
            }
            if (_capsuleId != capsuleId)
            {
                return;
            }
            _capsule = null;
            _capsuleId = 0;
            ReleaseInput();
        }

        private static void TakeInput()
        {
            if (_inputTaken)
            {
                return;
            }
            _inputTaken = true;
            try
            {
                KeyManager.SetInputState(InputKey, KeyInputState.Typing);
                InputMouse.SetMouseControl(true);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not free the mouse: {ex.Message}");
            }
        }

        private static void ReleaseInput()
        {
            if (!_inputTaken)
            {
                return;
            }
            _inputTaken = false;
            try
            {
                KeyManager.RemoveInputState(InputKey);
                if (GameManager.GameState == GameState.Running)
                {
                    InputMouse.SetMouseControl(false);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not restore the mouse: {ex.Message}");
            }
        }

        /// <summary>Escolhe o ponto (x, z) ou, sem ponto, o lugar padrao.</summary>
        public static void Choose(Vector2? point)
        {
            if (_capsuleId == 0 || _sent)
            {
                return;
            }
            _sent = true;
            _selected = point;
            long id = _capsuleId;
            if (_local)
            {
                ServerPilot.RequestTarget(id, NetworkManager.LocalClientId, point);
            }
            else
            {
                Net.SendTarget(id, point);
            }
        }

        public static void Tick()
        {
            if (_pendingId != 0)
            {
                LanderCapsule pending = Thing.Find<LanderCapsule>(_pendingId);
                if (pending != null)
                {
                    Begin(pending);
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
                Close(_capsuleId);
                return;
            }
            if (!_sent)
            {
                // O jogo pode prender o mouse de novo (ao assumir o controle do personagem, por exemplo).
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    if (_selected.HasValue)
                    {
                        Choose(_selected);
                    }
                }
            }
            else if (_inputTaken)
            {
                ReleaseInput();
            }
        }

        // ------------------------------------------------------------------ desenho

        private static Texture2D MapTexture()
        {
            try
            {
                return WorldSetting.Current?.Data?.TerrainSettings?.MiniMapTexture?.Texture;
            }
            catch
            {
                return null;
            }
        }

        private static float WorldSize => VoxelConstants.Size;

        private static float WorldOffset => VoxelConstants.Offset;

        private static Vector2 WorldToMap(Rect map, Vector3 world)
        {
            float u = (world.x + WorldOffset) / WorldSize;
            float v = (WorldSize - (world.z + WorldOffset)) / WorldSize;
            return new Vector2(map.x + u * map.width, map.y + v * map.height);
        }

        private static Vector2 MapToWorld(Rect map, Vector2 screen)
        {
            float u = (screen.x - map.x) / map.width;
            float v = (screen.y - map.y) / map.height;
            return new Vector2(u * WorldSize - WorldOffset, WorldSize - v * WorldSize - WorldOffset);
        }

        private static void EnsureStyles()
        {
            if (_title != null)
            {
                return;
            }
            _title = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _title.normal.textColor = Color.white;
            _text = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            _text.normal.textColor = new Color(0.9f, 0.9f, 0.9f);
            _marker = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _dim = new Texture2D(1, 1);
            _dim.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.75f));
            _dim.Apply();
        }

        private static void Marker(Rect map, Vector3 world, string text, Color color)
        {
            Vector2 p = WorldToMap(map, world);
            if (!map.Contains(p))
            {
                return;
            }
            Rect r = new Rect(p.x - 30f, p.y - 12f, 60f, 24f);
            Color old = _marker.normal.textColor;
            _marker.normal.textColor = Color.black;
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, _marker);
            _marker.normal.textColor = color;
            GUI.Label(r, text, _marker);
            _marker.normal.textColor = old;
        }

        public static void DrawHud()
        {
            if (_capsuleId == 0 || _capsule == null)
            {
                return;
            }
            EnsureStyles();
            GUI.depth = -1000;

            if (_sent)
            {
                string where = _selected.HasValue ? $" ({_selected.Value.x:0}, {_selected.Value.y:0})" : string.Empty;
                float w = Mathf.Min(520f, Screen.width - 32f);
                Rect box = new Rect((Screen.width - w) / 2f, 24f, w, 40f);
                GUI.DrawTexture(box, _dim);
                GUI.Label(box, "Descending to the chosen point" + where + "...", _text);
                return;
            }

            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), _dim);
            float size = Mathf.Max(200f, Mathf.Min(Screen.height - 190f, Screen.width - 64f, 800f));
            Rect map = new Rect((Screen.width - size) / 2f, 70f, size, size);
            GUI.Label(new Rect(0f, 20f, Screen.width, 40f), "Choose where your capsule lands", _title);

            Texture2D texture = MapTexture();
            if (texture == null)
            {
                GUI.Label(new Rect(map.x, map.y + size / 2f - 30f, size, 60f), "This world has no map image. The capsule will land at the default spot.", _text);
            }
            else
            {
                GUI.DrawTexture(map, texture, ScaleMode.StretchToFill);

                try
                {
                    foreach (StartLocationData entry in WorldSetting.Current.Data.StartLocationDatas)
                    {
                        StartLocationData start = DataCollection.Get<StartLocationData>(entry.Id);
                        if (start != null && !(start is RoundRobinStartLocationData))
                        {
                            Marker(map, start.WorldPosition(), "S", Color.yellow);
                        }
                    }
                }
                catch
                {
                    // Sem pontos de inicio no mapa: segue sem eles.
                }
                Marker(map, _capsule.Transform.position, "●", new Color(1f, 0.35f, 0.25f));

                Event e = Event.current;
                _hover = map.Contains(e.mousePosition) ? MapToWorld(map, e.mousePosition) : (Vector2?)null;
                if (e.type == EventType.MouseDown && e.button == 0 && _hover.HasValue)
                {
                    _selected = _hover;
                    e.Use();
                }
                else if (e.type == EventType.MouseDown && e.button == 1)
                {
                    _selected = null;
                    e.Use();
                }
                if (_selected.HasValue)
                {
                    Marker(map, new Vector3(_selected.Value.x, 0f, _selected.Value.y), "✕", new Color(0.3f, 1f, 0.4f));
                }
            }

            string info = _hover.HasValue
                ? $"Cursor: ({_hover.Value.x:0}, {_hover.Value.y:0})"
                : "Click the map to choose a point. Right click clears it.";
            if (_selected.HasValue)
            {
                info += $"    Chosen: ({_selected.Value.x:0}, {_selected.Value.y:0})";
            }
            GUI.Label(new Rect(map.x, map.yMax + 6f, size, 24f), info, _text);
            GUI.Label(new Rect(map.x, map.yMax + 30f, size, 22f), "● your capsule    S world start points    ✕ chosen point", _text);

            float buttonY = map.yMax + 58f;
            GUI.enabled = _selected.HasValue;
            if (GUI.Button(new Rect(Screen.width / 2f - 210f, buttonY, 200f, 40f), "Land here (Enter)"))
            {
                Choose(_selected);
            }
            GUI.enabled = true;
            if (GUI.Button(new Rect(Screen.width / 2f + 10f, buttonY, 200f, 40f), "Default spot"))
            {
                Choose(null);
            }
        }
    }
}
