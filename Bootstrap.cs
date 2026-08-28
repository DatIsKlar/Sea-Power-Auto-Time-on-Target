using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Entry logic invoked by AnchorChain. Waits until the mod menu state is readable,
    /// respects whether this mod is enabled, then installs the Harmony patch and starts
    /// the per-frame coordination pump.
    /// </summary>
    public static class Bootstrap
    {
        public const string Guid = "com.seapowermods.autotot";

        // Mod version. Keep in sync with AutoTOT.csproj <Version> and the [ACPlugin]
        // attribute in AnchorChainEntry.cs (which references this constant).
        internal const string Version = "0.1.1";

        internal static ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("AutoTOT");
        public static Harmony Harmony { get; private set; }

        private static bool _initialized;

        // Read by the on-screen HUD.
        internal static bool ShowIndicator = true;
        internal static KeyCode ToggleModifier = KeyCode.LeftAlt;
        internal static KeyCode ToggleKey = KeyCode.T;   // arm/disarm auto mode
        internal static KeyCode PanelKey = KeyCode.G;    // open/close the planner

        // --- Config ---
        private static ConfigFile _config;
        private static ConfigEntry<bool> _cfgEnabled;
        private static ConfigEntry<bool> _cfgDefaultOn;
        private static ConfigEntry<bool> _cfgShowIndicator;
        private static ConfigEntry<KeyCode> _cfgToggleModifier;
        private static ConfigEntry<KeyCode> _cfgToggleKey;
        private static ConfigEntry<KeyCode> _cfgPanelKey;
        private static ConfigEntry<float> _cfgDebounce;
        private static ConfigEntry<float> _cfgMaxWindow;
        private static ConfigEntry<bool> _cfgVerbose;
        private static ConfigEntry<bool> _cfgProfiling;

        public static void InitIfEnabled()
        {
            bool? enabled = ModMenuEnabled();
            if (enabled == false)
            {
                Log.LogInfo("AutoTOT is present but not enabled in the Mods menu — standing down.");
                return;
            }
            if (enabled == true)
            {
                Init();
                return;
            }

            // State not readable yet: wait on a gate object.
            GameObject gate = new GameObject("AutoTOTModGate");
            UnityEngine.Object.DontDestroyOnLoad(gate);
            gate.AddComponent<ModMenuGate>();
        }

        private static bool? ModMenuEnabled()
        {
            try
            {
                if (!Singleton<FileManager>.InstanceExists(false))
                    return null;

                var directories = Singleton<FileManager>.Instance.Directories;
                if (directories == null)
                    return null;

                string myDir = Path.GetFullPath(
                    Path.GetDirectoryName(typeof(Bootstrap).Assembly.Location) ?? "");
                if (myDir.Length == 0)
                    return true; // can't tell where we live; don't block

                foreach (SearchDirectory sd in directories)
                {
                    string dir = sd?.DirectoryInfo?.FullName;
                    if (string.IsNullOrEmpty(dir)) continue;
                    if (string.Equals(Path.GetFullPath(dir).TrimEnd('/', '\\'),
                                      myDir.TrimEnd('/', '\\'),
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        return sd.IsChecked;
                    }
                }
                // Our folder isn't a listed search directory (e.g. run from BepInEx/plugins).
                return true;
            }
            catch (Exception e)
            {
                Log.LogWarning($"Could not read mod menu state: {e.Message}");
                return null;
            }
        }

        private static void Init()
        {
            if (_initialized) return;
            _initialized = true;

            LoadConfig();

            // Forward uncaught Unity/game-side exceptions into the BepInEx log so a
            // crash triggered while this mod is active is captured with a full stack,
            // even when it fires inside the game's own code rather than ours.
            Application.logMessageReceived -= OnUnityLog; // guard against double-subscribe
            Application.logMessageReceived += OnUnityLog;

            Harmony = new Harmony(Guid);

            // A null target here means the game's InsertEngageTask signature changed
            // (e.g. after a game update) — PatchAll would then fail. Log it either way.
            var patchTarget = AccessTools.Method(typeof(ObjectBase), nameof(ObjectBase.InsertEngageTask));
            if (patchTarget == null)
                Log.LogError("[AutoTOT] patch target ObjectBase.InsertEngageTask NOT found — the game version may be incompatible; patching will likely fail.");
            else
                Log.LogInfo($"[AutoTOT] patch target resolved: {patchTarget.DeclaringType?.FullName}.{patchTarget.Name}");

            try
            {
                Harmony.PatchAll(typeof(Bootstrap).Assembly);
            }
            catch (Exception e)
            {
                Log.LogError($"[AutoTOT] Harmony PatchAll failed — mod will not function:\n{e}");
                throw;
            }

            // Shield the DOTS assembly scan (multiplayer mission-load crash). Done explicitly
            // rather than via PatchAll: the exact target differs between Entities versions and
            // Unity.Entities.dll may not be loaded yet at this point — Install handles both.
            DotsScanHardening.Install(Harmony);

            // Diagnostic for the multiplayer mission-load crash: DOTS re-init
            // (PlottingTableSerializer.RecreateWorldUsingTemp) enumerates every AppDomain
            // assembly and calls GetName() on each; one bad-culture assembly makes that throw
            // and aborts mission load. Name any such assembly up front so the real culprit is
            // identifiable even before the DOTS scan runs. DotsScanHardening then shields the
            // actual scan so load survives regardless.
            LogAssembliesThatFailGetName();

            GameObject pump = new GameObject("AutoTOTPump");
            UnityEngine.Object.DontDestroyOnLoad(pump);
            pump.AddComponent<Pump>();
            pump.AddComponent<Hud>();

            Log.LogInfo($"Auto Time-on-Target v{Version} loaded (Enabled={Coordinator.Enabled}, Armed={Coordinator.Active}, Unity={Application.unityVersion}).");
        }

        // One-shot sweep mirroring what Unity's DOTS TypeManager does during multiplayer
        // world re-init: call GetName() on every loaded assembly and report the ones that
        // throw (the "Parameter name: name" / invalid-culture case). Purely diagnostic —
        // never throws itself.
        private static void LogAssembliesThatFailGetName()
        {
            try
            {
                int bad = 0;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        // The exact call DOTS makes; this is what throws on a bad-culture name.
                        var _ = asm.GetName();
                    }
                    catch (Exception ge)
                    {
                        bad++;
                        Log.LogWarning(
                            $"[AutoTOT] Assembly with unreadable name ({DotsScanHardening.SafeIdentify(asm)}) " +
                            $"— this is the kind that crashes the DOTS multiplayer world re-init. " +
                            $"{ge.GetType().Name}: {ge.Message}");
                    }
                }
                if (bad == 0)
                    Log.LogInfo("[AutoTOT] AppDomain assembly-name sweep: all assemblies name cleanly at load time.");
                else
                    Log.LogWarning($"[AutoTOT] AppDomain assembly-name sweep: {bad} assembly(ies) fail GetName(); DOTS scan hardening will shield mission load.");
            }
            catch (Exception e)
            {
                Log.LogWarning($"[AutoTOT] assembly-name sweep skipped: {e.Message}");
            }
        }

        private static void LoadConfig()
        {
            string path = Path.Combine(Paths.ConfigPath, Guid + ".cfg");
            _config = new ConfigFile(path, true);

            _cfgEnabled = _config.Bind("General", "Enabled", true,
                "Master switch for the mod. If off, the mod does nothing and the on-screen indicator is hidden.");
            _cfgDefaultOn = _config.Bind("General", "AutoModeOnStart", false,
                "Whether the automatic 'coordinate normal group orders' mode is on at mission start. The planner panel works regardless of this.");
            _cfgShowIndicator = _config.Bind("Interface", "ShowIndicator", true,
                "Show the on-screen ARMED/off indicator (also click it to toggle).");
            _cfgToggleModifier = _config.Bind("Interface", "ToggleModifier", KeyCode.LeftAlt,
                "Optional modifier held with ToggleKey to arm/disarm coordination. Set to None for a single-key toggle.");
            _cfgToggleKey = _config.Bind("Interface", "ToggleKey", KeyCode.T,
                "Key (with ToggleModifier) that toggles the auto-coordinate-normal-orders mode.");
            _cfgPanelKey = _config.Bind("Interface", "OpenPanelKey", KeyCode.G,
                "Key (with ToggleModifier) that opens/closes the Time-on-Target planner panel.");
            _cfgDebounce = _config.Bind("Timing", "GroupWindowSeconds", 0.75f,
                new ConfigDescription(
                    "After the last missile order at a target, wait this many real seconds (with no new orders) before locking in the coordinated launch. Larger = easier to group several manual orders from one ship; smaller = snappier single attacks.",
                    new AcceptableValueRange<float>(0.05f, 5.0f)));
            _cfgMaxWindow = _config.Bind("Timing", "MaxCollectSeconds", 6.0f,
                new ConfigDescription(
                    "Hard cap (real seconds) on how long one target keeps collecting orders before it must lock in.",
                    new AcceptableValueRange<float>(0.25f, 20.0f)));
            _cfgVerbose = _config.Bind("Debug", "VerboseLogging", false,
                "Log every queued and released launch.");
            _cfgProfiling = _config.Bind("Debug", "Profiling", false,
                "Log per-frame timing every 60 frames to diagnose performance issues.");

            Coordinator.Active = _cfgDefaultOn.Value; // runtime toggle's starting state

            ApplyConfig();
            _cfgEnabled.SettingChanged += (_, __) => ApplyConfig();
            _cfgShowIndicator.SettingChanged += (_, __) => ApplyConfig();
            _cfgToggleModifier.SettingChanged += (_, __) => ApplyConfig();
            _cfgToggleKey.SettingChanged += (_, __) => ApplyConfig();
            _cfgPanelKey.SettingChanged += (_, __) => ApplyConfig();
            _cfgDebounce.SettingChanged += (_, __) => ApplyConfig();
            _cfgMaxWindow.SettingChanged += (_, __) => ApplyConfig();
            _cfgVerbose.SettingChanged += (_, __) => ApplyConfig();
            _cfgProfiling.SettingChanged += (_, __) => ApplyConfig();
        }

        // Forwards uncaught Unity exceptions to the BepInEx log. Only LogType.Exception
        // is relayed (real crashes / uncaught throws) to avoid drowning the log in the
        // game's routine LogType.Error output. ManualLogSource does not route back
        // through Debug.Log, so this cannot feed back on itself.
        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception) return;
            if (stackTrace == null || !stackTrace.Contains("AutoTOT")) return;
            Log.LogError($"[AutoTOT] Unity exception: {condition}\n{stackTrace}");
        }

        private static void ApplyConfig()
        {
            Coordinator.Enabled = _cfgEnabled.Value;
            Coordinator.DebounceSeconds = _cfgDebounce.Value;
            Coordinator.MaxWindowSeconds = _cfgMaxWindow.Value;
            Coordinator.VerboseLog = _cfgVerbose.Value;
            Coordinator.ProfilingEnabled = _cfgProfiling.Value;
            ShowIndicator = _cfgShowIndicator.Value;
            ToggleModifier = _cfgToggleModifier.Value;
            ToggleKey = _cfgToggleKey.Value;
            PanelKey = _cfgPanelKey.Value;
        }

        /// <summary>Waits for the mod menu state to become readable, then loads or stands down.</summary>
        private sealed class ModMenuGate : MonoBehaviour
        {
            // Give up waiting for the mod-menu state after this long and load anyway.
            private const float GateDeadlineSeconds = 120f;

            private float _deadline;

            private void Awake() => _deadline = Time.realtimeSinceStartup + GateDeadlineSeconds;

            private void Update()
            {
                bool? enabled = ModMenuEnabled();
                if (!enabled.HasValue && Time.realtimeSinceStartup < _deadline)
                    return;

                if (enabled == false)
                {
                    Log.LogInfo("AutoTOT not enabled in the Mods menu — standing down.");
                }
                else
                {
                    if (!enabled.HasValue)
                        Log.LogWarning("Mod menu state still unreadable at deadline; loading anyway.");
                    Init();
                }
                UnityEngine.Object.Destroy(gameObject);
            }
        }

        /// <summary>Drives the coordinator once per frame. Detects mission transitions for state reset.</summary>
        private sealed class Pump : MonoBehaviour
        {
            private bool _wasInMission;
            private string _lastErrorMsg;

            private void Update()
            {
                bool inMission = Globals._mainGameViewModel != null;

                if (_wasInMission && !inMission)
                    Coordinator.Reset();
                _wasInMission = inMission;

                if (!inMission) return;

                try
                {
                    Coordinator.Tick();
                }
                catch (Exception e)
                {
                    string msg = e.InnerException?.Message ?? e.Message;
                    if (msg != _lastErrorMsg)
                    {
                        _lastErrorMsg = msg;
                        Log.LogError($"[AutoTOT] coordinator tick error:\n{e}");
                    }
                }
            }
        }
    }
}
