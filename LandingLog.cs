using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Serialization;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Serialization;
using UnityEngine;

namespace LandingFromSpace
{
    /// <summary>Um ponto marcado no mapa: pouso recente ou lander.</summary>
    public sealed class LandingSite
    {
        [XmlAttribute] public float X;
        [XmlAttribute] public float Z;
        [XmlAttribute] public string Player = "";
        /// <summary>SteamID do jogador (0 em registros antigos, que valem pelo nome).</summary>
        [XmlAttribute] public ulong PlayerId;
        [XmlAttribute] public string When = "";

        /// <summary>O ponto e desse jogador: pelo SteamID ou, em registros sem ele, pelo nome.</summary>
        public bool IsOf(ulong id, string name)
        {
            return PlayerId != 0 ? PlayerId == id : string.Equals(Player, name ?? "", StringComparison.OrdinalIgnoreCase);
        }

        public Vector2 Point => new Vector2(X, Z);
    }

    /// <summary>Jogador marcado pelo comando "respawn" para receber o kit inicial de novo.</summary>
    public sealed class LandingPending
    {
        [XmlAttribute] public ulong Id;
        [XmlAttribute] public string Name = "";
    }

    [XmlRoot("LandingFromSpace")]
    public sealed class LandingFile
    {
        [XmlArray("RecentSites"), XmlArrayItem("Site")]
        public List<LandingSite> Recent = new List<LandingSite>();

        [XmlArray("Landers"), XmlArrayItem("Lander")]
        public List<LandingSite> Landers = new List<LandingSite>();

        [XmlArray("PendingLanders"), XmlArrayItem("Player")]
        public List<LandingPending> Pending = new List<LandingPending>();
    }

    /// <summary>
    /// Host: os ultimos pontos de pouso (MaxRecent por jogador) e onde cada lander caiu (para sempre), por mundo, no arquivo
    /// saves/&lt;NomeDaEstacao&gt;/LandingFromSpace.xml. Mundo novo ainda nao salvo: fica pendente ate o primeiro save.
    /// Cada jogador so ve os proprios pontos: clientes com o mod recebem so os seus junto com o pedido de escolha do ponto.
    /// </summary>
    public static class LandingLog
    {
        public const int MaxRecent = 5;
        private const string FileName = "LandingFromSpace.xml";

        /// <summary>Pousos mais perto que isso de um ja registrado substituem o antigo.</summary>
        private const float SameSiteDistance = 15f;

        private static LandingFile _data = new LandingFile();
        private static bool _loaded;
        private static string _loadedName;
        private static bool _dirty;
        private static float _nextTick;

        /// <summary>Copia recebida do host (clientes).</summary>
        private static LandingFile _remote;

        /// <summary>Dados para a tela do mapa: os do jogador local no host, ou a copia recebida.</summary>
        public static LandingFile Current
        {
            get
            {
                if (!GameManager.RunSimulation)
                {
                    return _remote ?? new LandingFile();
                }
                Assets.Scripts.Objects.Entities.Human local = Assets.Scripts.Objects.Entities.Human.LocalHuman;
                ulong id = (object)local != null ? local.OwnerClientId : 0;
                string name = (object)local != null ? local.DisplayName : null;
                if (_view == null || _viewVersion != _version || _viewId != id || _viewName != name)
                {
                    _view = For(id, name);
                    _viewVersion = _version;
                    _viewId = id;
                    _viewName = name;
                }
                return _view;
            }
        }

        private static LandingFile _view;
        private static int _version;
        private static int _viewVersion = -1;
        private static ulong _viewId;
        private static string _viewName;

        /// <summary>So os pontos desse jogador.</summary>
        public static LandingFile For(ulong id, string name)
        {
            return new LandingFile
            {
                Recent = _data.Recent.FindAll(s => s.IsOf(id, name)),
                Landers = _data.Landers.FindAll(s => s.IsOf(id, name))
            };
        }

        // ------------------------------------------------------------------ host

        public static void Tick()
        {
            if (GameManager.GameState == GameState.None)
            {
                if (_loaded || _remote != null)
                {
                    _data = new LandingFile();
                    _version++;
                    _loaded = false;
                    _loadedName = null;
                    _dirty = false;
                    _remote = null;
                }
                return;
            }
            if (!GameManager.RunSimulation || GameManager.GameState != GameState.Running || Time.unscaledTime < _nextTick)
            {
                return;
            }
            _nextTick = Time.unscaledTime + 1f;
            string name = CurrentName();
            if (!_loaded)
            {
                _loaded = true;
                _loadedName = name;
                _data = Load(name);
                _version++;
                return;
            }
            if (name != _loadedName)
            {
                // Primeiro save de um mundo novo ou "salvar como": leva os registros junto.
                _loadedName = name;
                _dirty |= _data.Recent.Count > 0 || _data.Landers.Count > 0 || _data.Pending.Count > 0;
            }
            if (_dirty)
            {
                TrySave();
            }
        }

        public static void RecordLanding(Vector3 point, ulong playerId, string player)
        {
            if (!GameManager.RunSimulation)
            {
                return;
            }
            _data.Recent.RemoveAll(s => s.IsOf(playerId, player) && Vector2.Distance(s.Point, new Vector2(point.x, point.z)) < SameSiteDistance);
            _data.Recent.Insert(0, Site(point, playerId, player));
            // Ate MaxRecent por jogador: os mais antigos desse jogador saem.
            int kept = 0;
            _data.Recent.RemoveAll(s => s.IsOf(playerId, player) && ++kept > MaxRecent);
            _dirty = true;
            _version++;
            Plugin.Log.LogInfo($"Landing site recorded: ({point.x:0}, {point.z:0}) by {player}.");
        }

        public static void RecordLander(Vector3 point, ulong playerId, string player)
        {
            if (!GameManager.RunSimulation)
            {
                return;
            }
            _data.Landers.Add(Site(point, playerId, player));
            _dirty = true;
            _version++;
            Plugin.Log.LogInfo($"Lander position recorded: ({point.x:0}, {point.z:0}) for {player}.");
        }

        /// <summary>Jogadores marcados para receber o kit inicial no proximo respawn.</summary>
        public static List<LandingPending> Pending => _data.Pending;

        public static void MarkPending(ulong id, string name)
        {
            _data.Pending.RemoveAll(p => (id != 0 && p.Id == id) || string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            _data.Pending.Add(new LandingPending { Id = id, Name = name ?? "" });
            _dirty = true;
            Plugin.Log.LogInfo($"{name} ({id}) marked for a new starting lander on the next respawn.");
        }

        public static bool CancelPending(string query)
        {
            int removed = _data.Pending.RemoveAll(p => p.Id.ToString() == query || (p.Name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            _dirty |= removed > 0;
            return removed > 0;
        }

        /// <summary>Se o jogador estava marcado, tira a marca e retorna true (pelo SteamID ou, sem ele, pelo nome).</summary>
        public static bool TakePending(ulong id, string name)
        {
            int removed = _data.Pending.RemoveAll(p => IsMatch(p, id, name));
            _dirty |= removed > 0;
            return removed > 0;
        }

        public static bool IsPending(ulong id, string name)
        {
            return _data.Pending.Exists(p => IsMatch(p, id, name));
        }

        private static bool IsMatch(LandingPending p, ulong id, string name)
        {
            return (p.Id != 0 && p.Id == id) || (p.Id == 0 && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static LandingSite Site(Vector3 point, ulong playerId, string player)
        {
            return new LandingSite
            {
                PlayerId = playerId,
                X = Mathf.Round(point.x * 10f) / 10f,
                Z = Mathf.Round(point.z * 10f) / 10f,
                Player = player ?? "",
                When = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            };
        }

        private static string CurrentName()
        {
            try
            {
                XmlSaveLoad instance = XmlSaveLoad.Instance;
                string name = (object)instance == null ? null : instance.CurrentStationName;
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch
            {
                return null;
            }
        }

        private static string DirectoryFor(string name) => Path.Combine(StationSaveUtils.GetSavePathSavesSubDir().FullName, name);

        /// <summary>Somente para o autoteste: arquivo do mundo atual (pode ainda nao existir).</summary>
        public static string CurrentFile => _loadedName == null ? null : Path.Combine(DirectoryFor(_loadedName), FileName);

        private static LandingFile Load(string name)
        {
            if (name == null)
            {
                return new LandingFile();
            }
            try
            {
                string path = Path.Combine(DirectoryFor(name), FileName);
                if (!File.Exists(path))
                {
                    return new LandingFile();
                }
                XmlSerializer serializer = new XmlSerializer(typeof(LandingFile));
                using (FileStream stream = File.OpenRead(path))
                {
                    LandingFile file = (LandingFile)serializer.Deserialize(stream) ?? new LandingFile();
                    file.Recent ??= new List<LandingSite>();
                    file.Landers ??= new List<LandingSite>();
                    file.Pending ??= new List<LandingPending>();
                    Plugin.Log.LogInfo($"{file.Recent.Count} recent landing site(s) and {file.Landers.Count} lander(s) loaded from {path}.");
                    return file;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to load landing sites of '{name}': {ex.Message}");
                return new LandingFile();
            }
        }

        private static void TrySave()
        {
            string name = _loadedName;
            if (name == null)
            {
                return;
            }
            try
            {
                string dir = DirectoryFor(name);
                if (!Directory.Exists(dir))
                {
                    return; // mundo novo ainda nao salvo
                }
                string path = Path.Combine(dir, FileName);
                string temp = path + ".tmp";
                XmlSerializer serializer = new XmlSerializer(typeof(LandingFile));
                using (FileStream stream = File.Create(temp))
                {
                    serializer.Serialize(stream, _data);
                }
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(temp, path);
                _dirty = false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not save landing sites: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ rede

        private static string Clean(string text) => (text ?? "").Replace(";", " ").Replace(",", " ").Replace("|", " ");

        /// <summary>Host: texto para enviar a um cliente, so com os pontos dele ("r,x,z,nome;l,x,z,nome;...").</summary>
        public static string Serialize(ulong playerId, string player)
        {
            LandingFile own = For(playerId, player);
            StringBuilder builder = new StringBuilder();
            foreach (LandingSite site in own.Recent)
            {
                builder.Append("r,").Append(site.X.ToString("0.#", CultureInfo.InvariantCulture)).Append(',').Append(site.Z.ToString("0.#", CultureInfo.InvariantCulture)).Append(',').Append(Clean(site.Player)).Append(';');
            }
            foreach (LandingSite site in own.Landers)
            {
                builder.Append("l,").Append(site.X.ToString("0.#", CultureInfo.InvariantCulture)).Append(',').Append(site.Z.ToString("0.#", CultureInfo.InvariantCulture)).Append(',').Append(Clean(site.Player)).Append(';');
            }
            return builder.ToString();
        }

        /// <summary>Cliente: guarda a copia recebida do host.</summary>
        public static void ApplyRemote(string payload)
        {
            LandingFile file = new LandingFile();
            foreach (string entry in (payload ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = entry.Split(',');
                if (parts.Length < 3)
                {
                    continue;
                }
                if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    continue;
                }
                LandingSite site = new LandingSite { X = x, Z = z, Player = parts.Length > 3 ? parts[3] : "" };
                (parts[0] == "l" ? file.Landers : file.Recent).Add(site);
            }
            _remote = file;
        }
    }
}
