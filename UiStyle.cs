using System.Collections.Generic;
using UnityEngine;

namespace LandingFromSpace
{
    /// <summary>
    /// Visual das telas do mod no padrao do HUD do jogo: paineis escuros translucidos com linhas finas, destaque
    /// laranja, titulos na fonte larga do jogo (conthrax, a dos nomes de lugar) e texto em Exo 2.
    /// </summary>
    public static class UiStyle
    {
        public static readonly Color Dim = new Color(0f, 0f, 0f, 0.55f);
        public static readonly Color Panel = new Color(0.06f, 0.08f, 0.10f, 0.90f);
        public static readonly Color PanelHeader = new Color(0.10f, 0.12f, 0.15f, 0.95f);
        public static readonly Color Line = new Color(1f, 1f, 1f, 0.12f);
        public static readonly Color Accent = new Color(0.96f, 0.60f, 0.16f, 1f);
        public static readonly Color AccentDark = new Color(0.12f, 0.07f, 0.02f, 1f);
        public static readonly Color Text = new Color(0.92f, 0.94f, 0.96f, 1f);
        public static readonly Color Muted = new Color(0.60f, 0.65f, 0.70f, 1f);
        public static readonly Color Lava = new Color(1f, 0.32f, 0.10f, 1f);
        public static readonly Color Start = new Color(0.55f, 0.82f, 1f, 1f);
        public static readonly Color Lander = new Color(0.45f, 0.90f, 0.45f, 1f);
        public static readonly Color Recent = new Color(0.90f, 0.92f, 0.95f, 1f);

        private static Font _title;
        private static Font _body;
        private static bool _fontsSearched;
        private static Texture2D _white;
        private static Texture2D _circle;
        private static Texture2D _ring;
        private static readonly Dictionary<string, GUIStyle> Styles = new Dictionary<string, GUIStyle>();

        private static void FindFonts()
        {
            if (_fontsSearched)
            {
                return;
            }
            _fontsSearched = true;
            foreach (Font font in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (font == null)
                {
                    continue;
                }
                if (_title == null && font.name.StartsWith("conthrax", System.StringComparison.OrdinalIgnoreCase))
                {
                    _title = font;
                }
                else if (_body == null && font.name.StartsWith("Exo2", System.StringComparison.OrdinalIgnoreCase))
                {
                    _body = font;
                }
            }
        }

        public static Texture2D White
        {
            get
            {
                if (_white == null)
                {
                    _white = new Texture2D(1, 1) { name = "LandingFromSpaceWhite" };
                    _white.SetPixel(0, 0, Color.white);
                    _white.Apply();
                }
                return _white;
            }
        }

        /// <summary>Circulo cheio (pontos no mapa).</summary>
        public static Texture2D Circle => _circle != null ? _circle : (_circle = MakeCircle(false));

        /// <summary>Anel (alvo no mapa).</summary>
        public static Texture2D Ring => _ring != null ? _ring : (_ring = MakeCircle(true));

        private static Texture2D MakeCircle(bool ring)
        {
            const int size = 64;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "LandingFromSpaceCircle", filterMode = FilterMode.Bilinear };
            Color[] pixels = new Color[size * size];
            float center = (size - 1) / 2f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - center) * (x - center) + (y - center) * (y - center));
                    float outer = Mathf.Clamp01(center - d + 0.5f);
                    float alpha = ring ? outer * Mathf.Clamp01(d - (center - 6f) + 0.5f) : outer;
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        /// <summary>Estilo de texto; titulo = fonte larga do jogo.</summary>
        public static GUIStyle Label(int size, Color color, TextAnchor anchor = TextAnchor.MiddleLeft, bool title = false, bool wrap = false)
        {
            FindFonts();
            string key = $"{size}|{color}|{anchor}|{title}|{wrap}";
            if (!Styles.TryGetValue(key, out GUIStyle style))
            {
                style = new GUIStyle(GUI.skin.label)
                {
                    font = title ? (_title ?? _body) : _body,
                    fontSize = size,
                    alignment = anchor,
                    wordWrap = wrap,
                    clipping = TextClipping.Overflow,
                    padding = new RectOffset(0, 0, 0, 0),
                    margin = new RectOffset(0, 0, 0, 0),
                    richText = false
                };
                style.normal.textColor = color;
                Styles[key] = style;
            }
            return style;
        }

        public static void Fill(Rect rect, Color color)
        {
            Color old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, White);
            GUI.color = old;
        }

        public static void Frame(Rect rect, Color color, float thickness = 1f)
        {
            Fill(new Rect(rect.x, rect.y, rect.width, thickness), color);
            Fill(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            Fill(new Rect(rect.x, rect.y, thickness, rect.height), color);
            Fill(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        /// <summary>Cantoneiras de destaque nos quatro cantos.</summary>
        public static void Corners(Rect rect, Color color, float length = 14f, float thickness = 2f)
        {
            Fill(new Rect(rect.x, rect.y, length, thickness), color);
            Fill(new Rect(rect.x, rect.y, thickness, length), color);
            Fill(new Rect(rect.xMax - length, rect.y, length, thickness), color);
            Fill(new Rect(rect.xMax - thickness, rect.y, thickness, length), color);
            Fill(new Rect(rect.x, rect.yMax - thickness, length, thickness), color);
            Fill(new Rect(rect.x, rect.yMax - length, thickness, length), color);
            Fill(new Rect(rect.xMax - length, rect.yMax - thickness, length, thickness), color);
            Fill(new Rect(rect.xMax - thickness, rect.yMax - length, thickness, length), color);
        }

        /// <summary>Painel do HUD: fundo escuro, borda fina e uma linha laranja no topo.</summary>
        public static void PanelBox(Rect rect, bool accentTop = true)
        {
            Fill(rect, Panel);
            Frame(rect, Line);
            if (accentTop)
            {
                Fill(new Rect(rect.x, rect.y, rect.width, 2f), Accent);
            }
        }

        public static void Dot(Vector2 center, float size, Color color, bool ring = false)
        {
            Color old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(center.x - size / 2f, center.y - size / 2f, size, size), ring ? Ring : Circle);
            GUI.color = old;
        }

        /// <summary>Tecla desenhada como no jogo: caixa com borda e o nome dentro.</summary>
        public static float KeyChip(float x, float y, string key, float height = 22f)
        {
            GUIStyle style = Label(12, Text, TextAnchor.MiddleCenter);
            float width = Mathf.Max(height, style.CalcSize(new GUIContent(key)).x + 12f);
            Rect rect = new Rect(x, y, width, height);
            Fill(rect, new Color(1f, 1f, 1f, 0.08f));
            Frame(rect, new Color(1f, 1f, 1f, 0.35f));
            GUI.Label(rect, key, style);
            return width;
        }

        /// <summary>Botao no padrao do jogo. Principal = laranja.</summary>
        public static bool Button(Rect rect, string text, bool primary, bool enabled = true)
        {
            bool hover = enabled && rect.Contains(Event.current.mousePosition);
            Color fill;
            Color textColor;
            if (!enabled)
            {
                fill = new Color(1f, 1f, 1f, 0.04f);
                textColor = new Color(Muted.r, Muted.g, Muted.b, 0.5f);
            }
            else if (primary)
            {
                fill = hover ? new Color(1f, 0.70f, 0.30f, 1f) : Accent;
                textColor = AccentDark;
            }
            else
            {
                fill = hover ? new Color(1f, 1f, 1f, 0.16f) : new Color(1f, 1f, 1f, 0.07f);
                textColor = Text;
            }
            Fill(rect, fill);
            Frame(rect, primary && enabled ? new Color(1f, 0.8f, 0.5f, 0.6f) : Line);
            GUI.Label(rect, text, Label(14, textColor, TextAnchor.MiddleCenter, title: true));
            return enabled && GUI.Button(rect, GUIContent.none, GUIStyle.none);
        }
    }
}
