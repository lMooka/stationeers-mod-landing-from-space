using System;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace LandingFromSpace
{
    /// <summary>
    /// Landing From Space: quem tem o mod escolhe no mapa do mundo onde a propria capsula de pouso vai cair.
    /// No primeiro pouso de um mundo novo, o lander (e o resto do kit inicial) vai para o mesmo lugar.
    /// Quem nao tem o mod segue o comportamento normal do jogo.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "mooka.stationeers.landingfromspace";
        public const string PluginName = "LandingFromSpace";
        public const string PluginVersion = "1.0.0";

        public static ManualLogSource Log;
        public static Plugin Instance;

        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> MoveStartingKit;
        public static ConfigEntry<float> KitRadius;
        public static ConfigEntry<float> KitFallHeight;
        public static ConfigEntry<float> KitFallSeconds;
        public static ConfigEntry<float> HoverHeight;
        public static ConfigEntry<float> StartHeight;
        public static ConfigEntry<float> DescentSpeed;
        public static ConfigEntry<float> FastDescentSpeed;
        public static ConfigEntry<float> SteerSpeed;
        public static ConfigEntry<float> MaxTiltAngle;
        public static ConfigEntry<float> SteerResponsiveness;
        public static ConfigEntry<float> ClientReplySeconds;
        public static ConfigEntry<float> MouseSensitivity;
        public static ConfigEntry<float> CameraDistance;
        public static ConfigEntry<float> CameraPitch;
        public static ConfigEntry<bool> EntryEffect;

        /// <summary>Marca, no processo inteiro, qual copia do mod esta ativa (cada copia carregada tem estaticos proprios).</summary>
        private const string OwnerKey = PluginGuid + ".owner";

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            // A copia da Workshop e a local podem ser carregadas juntas. Duas copias ativas abrem o mapa de novo no fim
            // da descida (os ganchos de uma nao sabem o que a outra fez), entao so a primeira funciona.
            string location = typeof(Plugin).Assembly.Location;
            if (AppDomain.CurrentDomain.GetData(OwnerKey) is string owner && owner != location)
            {
                Log.LogError($"Another copy of {PluginName} is already loaded from '{owner}'. This copy ('{location}') does nothing. Enable only one of them (Workshop or local) in the mod list.");
                enabled = false;
                return;
            }
            AppDomain.CurrentDomain.SetData(OwnerKey, location);
            Log.LogInfo($"{PluginName} {PluginVersion} active from '{location}'.");

            Enabled = Config.Bind("General", "Enabled", true,
                "Host: lets players that have the mod choose on the world map where their landing capsule comes down. Client: accept the choice when the host offers it.");
            MoveStartingKit = Config.Bind("General", "MoveStartingKit", true,
                "Host: on a new world, the lander (and the rest of the starting kit) lands where the first player with the mod lands.");
            KitRadius = Config.Bind("General", "KitRadius", 15f,
                "Host: the starting kit appears when that player leaves the capsule, at a random spot this far from the capsule (at least 9 m), away from the player. In meters.");
            KitFallHeight = Config.Bind("General", "KitFallHeight", 200f, "Host: the starting kit comes down from this height above the ground, in meters.");
            KitFallSeconds = Config.Bind("General", "KitFallSeconds", 8f, "Host: how long the starting kit takes to come down, in seconds.");
            HoverHeight = Config.Bind("Descent", "FinalApproachHeight", 100f,
                "Host: height above the ground where the guided descent ends and the game's own final landing (10 s, with fire and impact) takes over, in meters.");
            StartHeight = Config.Bind("Descent", "StartHeight", 500f,
                "Host: height above the chosen point where the guided descent starts, in meters.");
            DescentSpeed = Config.Bind("Descent", "DescentSpeed", 5f, "Host: falling speed during the guided descent, in m/s.");
            FastDescentSpeed = Config.Bind("Descent", "FastDescentSpeed", 25f, "Host: falling speed while the player holds Shift, in m/s.");
            SteerSpeed = Config.Bind("Descent", "SteerSpeed", 15f, "Host: sideways speed when the player steers with WASD, in m/s.");
            MaxTiltAngle = Config.Bind("Descent", "MaxTiltAngle", 35f,
                "Host: how far the capsule leans toward the direction it is moving at full sideways speed, in degrees (0 turns it off, at most 60).");
            SteerResponsiveness = Config.Bind("Descent", "SteerResponsiveness", 2.5f,
                "Host: how quickly the capsule speeds up, slows down and leans when the player steers. Higher is snappier, lower is floatier.");
            MouseSensitivity = Config.Bind("Camera", "MouseSensitivity", 3f, "Client: mouse sensitivity of the outside camera during the guided descent.");
            CameraDistance = Config.Bind("Camera", "CameraDistance", 15.4f, "Client: distance from the outside camera to the capsule.");
            EntryEffect = Config.Bind("Camera", "EntryEffect", true,
                "Client: show the atmospheric entry effect (plasma glow and fire under the capsule) during the guided descent.");
            CameraPitch = Config.Bind("Camera", "CameraPitch", 65f,
                "Client: starting angle of the outside camera below the horizon, in degrees (0 is the horizon, 89 looks straight down). The mouse adjusts it within that range.");
            ClientReplySeconds = Config.Bind("Network", "ClientReplySeconds", 3f,
                "Host: how long a joining player's capsule waits for the player's game to answer that it has the mod. Players without the mod descend after this delay.");

            try
            {
                Harmony harmony = new Harmony(PluginGuid);
                Patch(harmony, AccessTools.Method(typeof(LanderCapsule), "BeginDescent"), prefix: nameof(ServerPilot.BeginDescentPrefix), owner: typeof(ServerPilot));
                Patch(harmony, AccessTools.Method(typeof(LanderCapsule), "TerminateDescent"), postfix: nameof(Kit.TerminateDescentPostfix), owner: typeof(Kit));
                Patch(harmony, AccessTools.Method(typeof(LanderCapsule), nameof(LanderCapsule.SerializeSave)), postfix: nameof(ServerPilot.SerializeSavePostfix), owner: typeof(ServerPilot));
                Patch(harmony, AccessTools.Method(typeof(EntryEffects), nameof(EntryEffects.SetIntensity)), postfix: nameof(ThrusterFx.SetIntensityPostfix), owner: typeof(ThrusterFx));
                Patch(harmony, AccessTools.Method(typeof(WorldSetting), nameof(WorldSetting.SpawnOnNewWorld)), prefix: nameof(Kit.SpawnOnNewWorldPrefix), owner: typeof(Kit));
                Patch(harmony, AccessTools.Method(typeof(SpawnData), nameof(SpawnData.Execute), new[] { typeof(Thing), typeof(Assets.Scripts.Objects.Entities.Human) }),
                    prefix: nameof(Kit.SpawnExecutePrefix), postfix: nameof(Kit.SpawnExecutePostfix), owner: typeof(Kit));
                Patch(harmony, AccessTools.Method(typeof(NetworkMessages.Handshake), nameof(NetworkMessages.Handshake.Process)), prefix: nameof(Net.HandshakeProcessPrefix), owner: typeof(Net));
                Patch(harmony, AccessTools.Method(typeof(NetworkServer), nameof(NetworkServer.Handshake)), postfix: nameof(Net.ServerHandshakePostfix), owner: typeof(Net));
                Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
            }
            catch (Exception ex)
            {
                Log.LogError($"Could not patch the game; the mod does nothing: {ex}");
            }
        }

        private static void Patch(Harmony harmony, MethodBase target, string prefix = null, string postfix = null, Type owner = null)
        {
            if (target == null)
            {
                throw new MissingMethodException($"target method for {prefix ?? postfix} not found in this game version");
            }
            harmony.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(owner, prefix),
                postfix: postfix == null ? null : new HarmonyMethod(owner, postfix));
        }

        private void Update()
        {
            ServerPilot.Tick();
            MapPicker.Tick();
            SteerPilot.Tick();
            ClientSync.Tick();
#if SELFTEST
            SelfTest.Tick();
#endif
        }

        private void LateUpdate()
        {
            SteerPilot.LateTick();
            ThrusterFx.LateTick();
        }

        private void OnGUI()
        {
            MapPicker.DrawHud();
            SteerPilot.DrawHud();
        }
    }
}
