using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using BepInEx;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using TerrainSystem;
using UnityEngine;

namespace LandingFromSpace
{
    /// <summary>
    /// Camada de lava para a tela do mapa: uma textura 128x128 em que cada ponto com lava (altura da lava do jogo
    /// acima do chao) fica vermelho. E calculada aos poucos, alguns milissegundos por quadro, e guardada por mundo.
    /// </summary>
    public static class LavaMap
    {
        private const int Size = 128;
        private const float FrameBudgetMs = 6f;

        private static readonly Color32 LavaColor = new Color32(255, 70, 20, 150);
        private static readonly Color32 Clear = new Color32(0, 0, 0, 0);

        public static Texture2D Overlay { get; private set; }
        public static float Progress { get; private set; }
        public static bool Ready { get; private set; }
        private static string _worldId;
        private static bool _building;

        /// <summary>Comeca a calcular a camada do mundo atual, se ele tiver lava e ela ainda nao existir.</summary>
        public static void EnsureStarted()
        {
            WorldSetting world = WorldSetting.Current;
            if (world == null || !world.HasLava)
            {
                return;
            }
            if (_building || (Overlay != null && _worldId == world.Id))
            {
                return;
            }
            // 1) imagem pronta embutida no mod (mundos do jogo); 2) calculo salvo em disco; 3) calcula agora e salva.
            Texture2D baked = LoadEmbedded(world.Id) ?? LoadCached(world.Id);
            if (baked != null)
            {
                Overlay = baked;
                _worldId = world.Id;
                Progress = 1f;
                Ready = true;
                return;
            }
            Plugin.Instance.StartCoroutine(Build(world.Id));
        }

        /// <summary>Pasta do cache: BepInEx/cache/LandingFromSpace.</summary>
        private static string CacheDir => Path.Combine(Paths.CachePath, "LandingFromSpace");

        /// <summary>Arquivo do cache do mundo; muda com a versao do jogo, porque uma atualizacao pode mudar o mapa.</summary>
        public static string CacheFile(string worldId)
        {
            string version = GameManager.GetGameVersion() ?? "unknown";
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                version = version.Replace(c, '_');
            }
            return Path.Combine(CacheDir, $"lava_{worldId}_{version}.png");
        }

        private static Texture2D FromPng(byte[] png, string name)
        {
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            if (!texture.LoadImage(png) || texture.width != Size || texture.height != Size)
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }
            return texture;
        }

        private static Texture2D LoadEmbedded(string worldId)
        {
            try
            {
                Assembly assembly = typeof(LavaMap).Assembly;
                string resource = $"LandingFromSpace.Lava.lava_{worldId}.png";
                using (Stream stream = assembly.GetManifestResourceStream(resource))
                {
                    if (stream == null)
                    {
                        return null;
                    }
                    using (MemoryStream memory = new MemoryStream())
                    {
                        stream.CopyTo(memory);
                        Texture2D texture = FromPng(memory.ToArray(), "LandingFromSpaceLava");
                        if (texture != null)
                        {
                            Plugin.Log.LogInfo($"Lava map for {worldId}: built in.");
                        }
                        return texture;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Built-in lava map failed: {ex.Message}");
                return null;
            }
        }

        private static Texture2D LoadCached(string worldId)
        {
            try
            {
                string file = CacheFile(worldId);
                if (!File.Exists(file))
                {
                    return null;
                }
                Texture2D texture = FromPng(File.ReadAllBytes(file), "LandingFromSpaceLava");
                if (texture != null)
                {
                    Plugin.Log.LogInfo($"Lava map for {worldId}: loaded from {file}.");
                }
                return texture;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Cached lava map failed: {ex.Message}");
                return null;
            }
        }

        private static void SaveCache(string worldId, Texture2D texture)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                string file = CacheFile(worldId);
                File.WriteAllBytes(file, texture.EncodeToPNG());
                Plugin.Log.LogInfo($"Lava map for {worldId} saved to {file}.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not save the lava map: {ex.Message}");
            }
        }

        /// <summary>O ponto do mundo (x, z) esta marcado como lava na camada (false enquanto nao calculado).</summary>
        public static bool IsMarked(Vector2 world)
        {
            if (Overlay == null)
            {
                return false;
            }
            int i = Mathf.FloorToInt((world.x + VoxelConstants.Offset) / VoxelConstants.Size * Size);
            int j = Mathf.FloorToInt((world.y + VoxelConstants.Offset) / VoxelConstants.Size * Size);
            if (i < 0 || j < 0 || i >= Size || j >= Size)
            {
                return false;
            }
            return Overlay.GetPixel(i, j).a > 0.1f;
        }

        private static IEnumerator Build(string worldId)
        {
            _building = true;
            Ready = false;
            Progress = 0f;
            _worldId = worldId;
            Texture2D texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                name = "LandingFromSpaceLava",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            Color32[] pixels = new Color32[Size * Size];
            texture.SetPixels32(pixels);
            texture.Apply();
            Overlay = texture;

            Stopwatch watch = new Stopwatch();
            Stopwatch total = Stopwatch.StartNew();
            int frames = 1;
            float cell = (float)VoxelConstants.Size / Size;
            int lava = 0;
            int nextApply = 8;
            for (int j = 0; j < Size; j++)
            {
                watch.Restart();
                for (int i = 0; i < Size; i++)
                {
                    if (GameManager.GameState == GameState.None || WorldSetting.Current == null || WorldSetting.Current.Id != worldId)
                    {
                        _building = false;
                        Overlay = null;
                        yield break;
                    }
                    Vector3 point = new Vector3((i + 0.5f) * cell - VoxelConstants.Offset, 0f, (j + 0.5f) * cell - VoxelConstants.Offset);
                    bool isLava = ServerPilot.IsLava(point);
                    pixels[j * Size + i] = isLava ? LavaColor : Clear;
                    lava += isLava ? 1 : 0;
                    if (watch.Elapsed.TotalMilliseconds > FrameBudgetMs)
                    {
                        frames++;
                        yield return null;
                        watch.Restart();
                    }
                }
                Progress = (j + 1f) / Size;
                if (j + 1 >= nextApply || j == Size - 1)
                {
                    // Mostra a camada aparecendo aos poucos no mapa.
                    texture.SetPixels32(pixels);
                    texture.Apply();
                    nextApply = j + 1 + 8;
                }
            }
            Ready = true;
            _building = false;
            Plugin.Log.LogInfo($"Lava map for {worldId}: {lava} of {Size * Size} cells with lava, computed in {total.Elapsed.TotalSeconds:0.0} s ({frames} frames).");
            SaveCache(worldId, texture);
        }
    }
}
