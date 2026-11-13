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


        public static bool IsOpen => _capsuleId != 0;

        /// <summary>Somente para o autoteste: marca um alvo como se o jogador tivesse clicado, sem confirmar.</summary>
        public static void TestSelect(Vector2 point) => _selected = point;
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
            LavaMap.EnsureStarted();
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

        private static Vector2 MarkerPoint(Rect map, Vector3 world) => WorldToMap(map, world);

        /// <summary>Barra de progresso fina, no padrao do HUD.</summary>
        private static void ProgressBar(Rect rect, float value, Color color)
        {
            UiStyle.Fill(rect, new Color(1f, 1f, 1f, 0.08f));
            UiStyle.Fill(new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(value), rect.height), color);
        }

        private static float Section(float x, float y, float width, string label, string value, Color valueColor)
        {
            GUI.Label(new Rect(x, y, width, 16f), label, UiStyle.Label(11, UiStyle.Muted));
            GUI.Label(new Rect(x, y + 16f, width, 24f), value, UiStyle.Label(18, valueColor));
            return y + 48f;
        }

        /// <summary>Lander no mapa: quadrado verde com "L".</summary>
        private static void LanderIcon(Vector2 c)
        {
            Rect r = new Rect(c.x - 8f, c.y - 8f, 16f, 16f);
            UiStyle.Fill(new Rect(r.x - 1f, r.y - 1f, r.width + 2f, r.height + 2f), new Color(0f, 0f, 0f, 0.6f));
            UiStyle.Fill(r, UiStyle.Lander);
            GUI.Label(r, "L", UiStyle.Label(11, UiStyle.AccentDark, TextAnchor.MiddleCenter, title: true));
        }

        /// <summary>Pouso recente no mapa: circulo claro numerado (1 = o mais recente).</summary>
        private static void RecentIcon(Vector2 c, int number)
        {
            UiStyle.Dot(c, 18f, new Color(0f, 0f, 0f, 0.6f));
            UiStyle.Dot(c, 15f, UiStyle.Recent);
            GUI.Label(new Rect(c.x - 9f, c.y - 9f, 18f, 18f), number.ToString(), UiStyle.Label(10, UiStyle.AccentDark, TextAnchor.MiddleCenter, title: true));
        }

        /// <summary>Linha da lista: icone, coordenadas e jogador. Clicar marca o ponto como alvo.</summary>
        private static float SiteRow(float x, float y, float width, LandingSite site, System.Action<Vector2> icon)
        {
            Rect row = new Rect(x - 4f, y, width + 8f, 22f);
            bool selected = _selected.HasValue && Vector2.Distance(_selected.Value, site.Point) < 1f;
            if (selected)
            {
                UiStyle.Fill(row, new Color(UiStyle.Accent.r, UiStyle.Accent.g, UiStyle.Accent.b, 0.18f));
            }
            else if (row.Contains(Event.current.mousePosition))
            {
                UiStyle.Fill(row, new Color(1f, 1f, 1f, 0.08f));
            }
            icon(new Vector2(x + 8f, y + 11f));
            GUI.Label(new Rect(x + 24f, y, 110f, 22f), $"{site.X:0}, {site.Z:0}", UiStyle.Label(13, UiStyle.Text));
            GUI.Label(new Rect(x + 130f, y, width - 130f, 22f), site.Player, UiStyle.Label(12, UiStyle.Muted, TextAnchor.MiddleRight));
            if (GUI.Button(row, GUIContent.none, GUIStyle.none))
            {
                _selected = site.Point;
            }
            return y + 24f;
        }

        private static float LegendRow(float x, float y, float width, string text, System.Action<Vector2> icon)
        {
            icon(new Vector2(x + 8f, y + 9f));
            GUI.Label(new Rect(x + 24f, y, width - 24f, 18f), text, UiStyle.Label(13, UiStyle.Text));
            return y + 24f;
        }

        public static void DrawHud()
        {
            if (_capsuleId == 0 || _capsule == null)
            {
                return;
            }
            GUI.depth = -1000;

            if (_sent)
            {
                string where = _selected.HasValue ? $"  {_selected.Value.x:0}, {_selected.Value.y:0}" : string.Empty;
                float w = Mathf.Min(460f, Screen.width - 32f);
                Rect banner = new Rect((Screen.width - w) / 2f, 24f, w, 44f);
                UiStyle.PanelBox(banner);
                GUI.Label(new Rect(banner.x + 16f, banner.y, w - 32f, banner.height), "DESCENDING TO TARGET" + where, UiStyle.Label(14, UiStyle.Text, TextAnchor.MiddleCenter, title: true));
                return;
            }

            UiStyle.Fill(new Rect(0f, 0f, Screen.width, Screen.height), UiStyle.Dim);

            const float pad = 16f;
            const float side = 280f;
            const float header = 52f;
            float size = Mathf.Max(240f, Mathf.Min(Screen.height - header - pad * 3f - 40f, Screen.width - side - pad * 4f, 820f));
            float width = pad + size + pad + side + pad;
            float height = header + size + pad * 2f;
            Rect panel = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
            UiStyle.PanelBox(panel);

            // Cabecalho: titulo na fonte do jogo e instrucao em laranja.
            Rect head = new Rect(panel.x, panel.y + 2f, panel.width, header - 2f);
            UiStyle.Fill(head, UiStyle.PanelHeader);
            UiStyle.Fill(new Rect(panel.x, panel.y + header, panel.width, 1f), UiStyle.Line);
            GUI.Label(new Rect(head.x + pad, head.y, 400f, head.height), "LANDING SITE", UiStyle.Label(22, UiStyle.Text, title: true));
            GUI.Label(new Rect(head.x, head.y, head.width - pad, head.height), "SELECT A POINT ON THE MAP", UiStyle.Label(12, UiStyle.Accent, TextAnchor.MiddleRight, title: true));

            Rect map = new Rect(panel.x + pad, panel.y + header + pad, size, size);
            Rect column = new Rect(map.xMax + pad, map.y, side, size);
            bool hasLava = WorldSetting.Current != null && WorldSetting.Current.HasLava;

            Texture2D texture = MapTexture();
            if (texture == null)
            {
                UiStyle.Fill(map, new Color(0f, 0f, 0f, 0.4f));
                GUI.Label(map, "This world has no map image.\nThe capsule will land at the default spot.", UiStyle.Label(15, UiStyle.Muted, TextAnchor.MiddleCenter, wrap: true));
            }
            else
            {
                GUI.DrawTexture(map, texture, ScaleMode.StretchToFill);
                if (LavaMap.Overlay != null)
                {
                    GUI.DrawTexture(map, LavaMap.Overlay, ScaleMode.StretchToFill, alphaBlend: true);
                }

                try
                {
                    foreach (StartLocationData entry in WorldSetting.Current.Data.StartLocationDatas)
                    {
                        StartLocationData start = DataCollection.Get<StartLocationData>(entry.Id);
                        if (start != null && !(start is RoundRobinStartLocationData))
                        {
                            Vector2 p = MarkerPoint(map, start.WorldPosition());
                            if (map.Contains(p))
                            {
                                UiStyle.Dot(p, 12f, new Color(0f, 0f, 0f, 0.6f));
                                UiStyle.Dot(p, 8f, UiStyle.Start);
                            }
                        }
                    }
                }
                catch
                {
                    // Sem pontos de inicio no mapa: segue sem eles.
                }

                LandingFile sites = LandingLog.Current;
                foreach (LandingSite lander in sites.Landers)
                {
                    Vector2 p = MarkerPoint(map, new Vector3(lander.X, 0f, lander.Z));
                    if (map.Contains(p))
                    {
                        LanderIcon(p);
                    }
                }
                for (int i = sites.Recent.Count - 1; i >= 0; i--)
                {
                    Vector2 p = MarkerPoint(map, new Vector3(sites.Recent[i].X, 0f, sites.Recent[i].Z));
                    if (map.Contains(p))
                    {
                        RecentIcon(p, i + 1);
                    }
                }

                Vector2 capsulePoint = MarkerPoint(map, _capsule.Transform.position);
                if (map.Contains(capsulePoint))
                {
                    UiStyle.Dot(capsulePoint, 20f, new Color(UiStyle.Accent.r, UiStyle.Accent.g, UiStyle.Accent.b, 0.35f));
                    UiStyle.Dot(capsulePoint, 10f, UiStyle.Accent);
                }

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

                if (_hover.HasValue)
                {
                    // Linhas guia finas pelo cursor.
                    Vector2 m = e.mousePosition;
                    UiStyle.Fill(new Rect(map.x, m.y, map.width, 1f), new Color(1f, 1f, 1f, 0.18f));
                    UiStyle.Fill(new Rect(m.x, map.y, 1f, map.height), new Color(1f, 1f, 1f, 0.18f));
                }
                if (_selected.HasValue)
                {
                    // Mira laranja no alvo.
                    Vector2 t = MarkerPoint(map, new Vector3(_selected.Value.x, 0f, _selected.Value.y));
                    Color target = LavaMap.IsMarked(_selected.Value) ? UiStyle.Lava : UiStyle.Accent;
                    UiStyle.Dot(t, 30f, new Color(0f, 0f, 0f, 0.5f), ring: true);
                    UiStyle.Dot(t, 26f, target, ring: true);
                    UiStyle.Fill(new Rect(t.x - 22f, t.y - 1f, 10f, 2f), target);
                    UiStyle.Fill(new Rect(t.x + 12f, t.y - 1f, 10f, 2f), target);
                    UiStyle.Fill(new Rect(t.x - 1f, t.y - 22f, 2f, 10f), target);
                    UiStyle.Fill(new Rect(t.x - 1f, t.y + 12f, 2f, 10f), target);
                }
            }
            UiStyle.Frame(map, UiStyle.Line);
            UiStyle.Corners(map, UiStyle.Accent);

            // Coluna lateral.
            float x = column.x;
            float y = column.y;
            float w2 = column.width;
            bool hoverLava = _hover.HasValue && LavaMap.IsMarked(_hover.Value);
            y = Section(x, y, w2, "CURSOR", _hover.HasValue ? $"{_hover.Value.x:0}, {_hover.Value.y:0}" : "—", UiStyle.Text);
            if (hoverLava)
            {
                Rect chip = new Rect(x + 120f, y - 30f, 52f, 18f);
                UiStyle.Fill(chip, UiStyle.Lava);
                GUI.Label(chip, "LAVA", UiStyle.Label(11, Color.white, TextAnchor.MiddleCenter, title: true));
            }
            y = Section(x, y, w2, "TARGET", _selected.HasValue ? $"{_selected.Value.x:0}, {_selected.Value.y:0}" : "—", _selected.HasValue ? UiStyle.Accent : UiStyle.Muted);
            if (_selected.HasValue && LavaMap.IsMarked(_selected.Value))
            {
                GUI.Label(new Rect(x, y - 6f, w2, 40f), "Lava here. You will land on the nearest safe ground.", UiStyle.Label(12, UiStyle.Lava, wrap: true));
                y += 40f;
            }

            LandingFile known = LandingLog.Current;
            if (known.Recent.Count > 0 || known.Landers.Count > 0)
            {
                y += 4f;
                UiStyle.Fill(new Rect(x, y, w2, 1f), UiStyle.Line);
                y += 10f;
                if (known.Landers.Count > 0)
                {
                    GUI.Label(new Rect(x, y, w2, 16f), "LANDER", UiStyle.Label(11, UiStyle.Muted));
                    y += 18f;
                    foreach (LandingSite lander in known.Landers)
                    {
                        y = SiteRow(x, y, w2, lander, c => LanderIcon(c));
                    }
                }
                if (known.Recent.Count > 0)
                {
                    GUI.Label(new Rect(x, y + 2f, w2, 16f), "RECENT LANDING SITES", UiStyle.Label(11, UiStyle.Muted));
                    y += 20f;
                    for (int i = 0; i < known.Recent.Count; i++)
                    {
                        int number = i + 1;
                        y = SiteRow(x, y, w2, known.Recent[i], c => RecentIcon(c, number));
                    }
                }
            }

            y += 8f;
            UiStyle.Fill(new Rect(x, y, w2, 1f), UiStyle.Line);
            y += 12f;
            GUI.Label(new Rect(x, y, w2, 16f), "LEGEND", UiStyle.Label(11, UiStyle.Muted));
            y += 22f;
            y = LegendRow(x, y, w2, "Your capsule", c => { UiStyle.Dot(c, 16f, new Color(UiStyle.Accent.r, UiStyle.Accent.g, UiStyle.Accent.b, 0.35f)); UiStyle.Dot(c, 8f, UiStyle.Accent); });
            y = LegendRow(x, y, w2, "World start point", c => UiStyle.Dot(c, 8f, UiStyle.Start));
            y = LegendRow(x, y, w2, "Target", c => UiStyle.Dot(c, 14f, UiStyle.Accent, ring: true));
            if (known.Landers.Count > 0)
            {
                y = LegendRow(x, y, w2, "Lander", c => LanderIcon(c));
            }
            if (known.Recent.Count > 0)
            {
                y = LegendRow(x, y, w2, "Recent landing site", c => RecentIcon(c, 1));
            }
            if (hasLava)
            {
                y = LegendRow(x, y, w2, "Lava", c => UiStyle.Fill(new Rect(c.x - 6f, c.y - 6f, 12f, 12f), new Color(UiStyle.Lava.r, UiStyle.Lava.g, UiStyle.Lava.b, 0.75f)));
                if (!LavaMap.Ready)
                {
                    GUI.Label(new Rect(x, y, w2, 16f), $"MAPPING LAVA  {LavaMap.Progress * 100f:0}%", UiStyle.Label(11, UiStyle.Muted));
                    ProgressBar(new Rect(x, y + 18f, w2, 3f), LavaMap.Progress, UiStyle.Lava);
                    y += 30f;
                }
            }

            bool roomForControls = y + 20f + 22f + 28f * 3f < column.yMax - 106f;
            if (roomForControls)
            {
            y += 8f;
            UiStyle.Fill(new Rect(x, y, w2, 1f), UiStyle.Line);
            y += 12f;
            GUI.Label(new Rect(x, y, w2, 16f), "CONTROLS", UiStyle.Label(11, UiStyle.Muted));
            y += 22f;
            float cx = x + UiStyle.KeyChip(x, y, "LMB") + 8f;
            GUI.Label(new Rect(cx, y, w2, 22f), "Choose target", UiStyle.Label(13, UiStyle.Text));
            y += 28f;
            cx = x + UiStyle.KeyChip(x, y, "RMB") + 8f;
            GUI.Label(new Rect(cx, y, w2, 22f), "Clear target", UiStyle.Label(13, UiStyle.Text));
            y += 28f;
            cx = x + UiStyle.KeyChip(x, y, "ENTER") + 8f;
            GUI.Label(new Rect(cx, y, w2, 22f), "Land here", UiStyle.Label(13, UiStyle.Text));
            }

            // Botoes no rodape da coluna.
            Rect land = new Rect(x, column.yMax - 96f, w2, 42f);
            Rect normal = new Rect(x, column.yMax - 44f, w2, 42f);
            if (UiStyle.Button(land, "LAND HERE", primary: true, enabled: _selected.HasValue))
            {
                Choose(_selected);
            }
            if (UiStyle.Button(normal, "DEFAULT SPOT", primary: false))
            {
                Choose(null);
            }
        }
    }
}
