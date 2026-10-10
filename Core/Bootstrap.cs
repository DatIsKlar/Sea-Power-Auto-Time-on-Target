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
    /// Entry logic invoked by AnchorChain: waits for the mod menu state, then installs the patches and
    /// starts the per-frame pump.
    /// </summary>
    public static class Bootstrap
    {
        public const string Guid = "com.seapowermods.autotot";

        // Keep in sync with AutoTOT.csproj <Version>; AnchorChainEntry's [ACPlugin] references this.
        internal const string Version = "0.2.1";

        internal static ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("AutoTOT");
        public static Harmony Harmony { get; private set; }

        private static bool _initialized;

        // Read by the on-screen HUD.
        internal static bool ShowIndicator = true;
        internal static KeyCode ToggleModifier = KeyCode.LeftAlt;
        internal static KeyCode ToggleKey = KeyCode.T;   // arm/disarm auto mode
        internal static KeyCode PanelKey = KeyCode.G;    // open/close the planner
        internal static KeyCode StrikeArmKey = KeyCode.H; // arm/disarm multi-target strike collection
        internal static KeyCode FireStrikeKey = KeyCode.F; // fire the staged strike without opening the panel
        internal static float UiScale = 0f;              // 0 = auto (scale to screen height)
        internal static float UiScaleMultiplier = 1f;    // fine-tune on top of UiScale

        // Config entries read after startup. Enabled, AutoModeOnStart and EstimatorThreads are read once in LoadConfig.
        private static ConfigFile _config;
        private static ConfigEntry<bool> _cfgShowIndicator;
        private static ConfigEntry<KeyCode> _cfgToggleModifier;
        private static ConfigEntry<KeyCode> _cfgToggleKey;
        private static ConfigEntry<KeyCode> _cfgPanelKey;
        private static ConfigEntry<KeyCode> _cfgStrikeArmKey;
        private static ConfigEntry<KeyCode> _cfgFireStrikeKey;
        private static ConfigEntry<float> _cfgUiScale;
        private static ConfigEntry<float> _cfgUiScaleMultiplier;
        private static ConfigEntry<float> _cfgDebounce;
        private static ConfigEntry<float> _cfgMaxWindow;
        private static ConfigEntry<bool> _cfgVerbose;
        private static ConfigEntry<bool> _cfgTraceFlightModel;
        private static ConfigEntry<bool> _cfgProfiling;
        private static ConfigEntry<bool> _cfgVerifySolve;

        public static void InitIfEnabled()
        {
            bool? enabled = ModMenuEnabled();
            if (enabled == false)
            {
                Log.LogInfo("AutoTOT is present but not enabled in the Mods menu, so it is standing down.");
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

        // Writing the entry persists it and fires SettingChanged, which re-applies the config.
        internal static void SetUiScaleMultiplier(float v)
        {
            v = Mathf.Clamp(v, Hud.MinUiScaleMultiplier, Hud.MaxUiScaleMultiplier);
            if (_cfgUiScaleMultiplier != null) _cfgUiScaleMultiplier.Value = v;
            else UiScaleMultiplier = v;
        }

        /// <summary>
        /// One-time startup. <see cref="_initialized"/> is set only on success; a failure disables the
        /// coordinator, so a broken load behaves like an absent mod.
        /// </summary>
        private static void Init()
        {
            if (_initialized || _initializing) return;
            _initializing = true;
            try
            {
                InitCore();
                _initialized = true;
            }
            catch (Exception e)
            {
                Coordinator.Enabled = false;
                Log.LogError($"[AutoTOT] initialization failed, so the mod is standing down:\n{e}");
            }
            finally
            {
                _initializing = false;
            }
        }

        private static bool _initializing;

        private static void InitCore()
        {
            LoadConfig();

            // Relay uncaught Unity exceptions whose stack involves AutoTOT to the BepInEx log.
            Application.logMessageReceived -= OnUnityLog; // guard against double-subscribe
            Application.logMessageReceived += OnUnityLog;

            Harmony = new Harmony(Guid);

            // Null means InsertEngageTask's signature changed in a game update, and PatchAll would fail.
            var patchTarget = AccessTools.Method(typeof(ObjectBase), nameof(ObjectBase.InsertEngageTask));
            if (patchTarget == null)
                Log.LogError("[AutoTOT] patch target ObjectBase.InsertEngageTask NOT found. The game version may be incompatible, and patching will likely fail.");
            else
                Log.LogInfo($"[AutoTOT] patch target resolved: {patchTarget.DeclaringType?.FullName}.{patchTarget.Name}");

            try
            {
                Harmony.PatchAll(typeof(Bootstrap).Assembly);
            }
            catch (Exception e)
            {
                // Rethrown into Init, which reports it and leaves the mod disabled.
                Log.LogError($"[AutoTOT] Harmony PatchAll failed, so the mod will not function:\n{e}");
                throw;
            }

            // Installed explicitly, not by PatchAll: the target differs between Entities versions, and
            // Unity.Entities may not be loaded yet.
            DotsScanHardening.Install(Harmony);

            // Name any assembly whose GetName() throws: one such assembly aborts the DOTS world re-init
            // during multiplayer mission load.
            LogAssembliesThatFailGetName();

            GameObject pump = new GameObject("AutoTOTPump");
            UnityEngine.Object.DontDestroyOnLoad(pump);
            pump.AddComponent<Pump>();
            pump.AddComponent<Hud>();
#if LIVETEST
            pump.AddComponent<LiveTestRunner>();   // inert unless BepInEx/AutoTOT-livetest/plan.ini exists
#endif

            Log.LogInfo($"Auto Time-on-Target v{Version} loaded (Enabled={Coordinator.Enabled}, Armed={Coordinator.Active}, Unity={Application.unityVersion}, flight model {FlightModelCommit()}).");
        }

        // Calls GetName() on every loaded assembly, as the DOTS TypeManager does, and reports the ones that
        // throw. Never throws itself.
        /// <summary>The SeaPowerFlightModel commit compiled into this DLL, stamped by AutoTOT.csproj.</summary>
        private static string FlightModelCommit()
        {
            foreach (AssemblyMetadataAttribute a in typeof(Bootstrap).Assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false))
                if (a.Key == "FlightModelCommit") return a.Value;
            return "unknown";
        }

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
                            $"this is the kind that crashes the DOTS multiplayer world re-init. " +
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

            ConfigEntry<bool> cfgEnabled = _config.Bind("General", "Enabled", true,
                "Master switch for the mod. If off, the mod does nothing and the on-screen indicator " +
                "is hidden. Read once at startup: changing it mid-session takes effect on the next " +
                "game start.");
            ConfigEntry<bool> cfgDefaultOn = _config.Bind("General", "AutoModeOnStart", false,
                "Whether the automatic 'coordinate normal group orders' mode is on when the game starts. Read once at startup; the hotkey toggle then carries across missions. The planner panel works regardless of this.");
            _cfgShowIndicator = _config.Bind("Interface", "ShowIndicator", true,
                "Show the on-screen ARMED/off indicator (also click it to toggle).");
            _cfgToggleModifier = _config.Bind("Interface", "ToggleModifier", KeyCode.LeftAlt,
                "Optional modifier held with ToggleKey to arm/disarm coordination. Set to None for a single-key toggle.");
            _cfgToggleKey = _config.Bind("Interface", "ToggleKey", KeyCode.T,
                "Key (with ToggleModifier) that toggles the auto-coordinate-normal-orders mode.");
            _cfgPanelKey = _config.Bind("Interface", "OpenPanelKey", KeyCode.G,
                "Key (with ToggleModifier) that opens/closes the Time-on-Target planner panel.");
            _cfgStrikeArmKey = _config.Bind("Interface", "StrikeArmKey", KeyCode.H,
                "Key (with ToggleModifier) that arms or disarms multi-target strike collection. " +
                "While armed, every missile order you issue is held, whatever its target, until you " +
                "fire the strike from the planner panel; all of it then lands at the same moment.");
            _cfgFireStrikeKey = _config.Bind("Interface", "FireStrikeKey", KeyCode.F,
                "Key (with ToggleModifier) that fires the staged strike. It works with the panel " +
                "hidden, so a strike built earlier can be launched without opening anything.");
            _cfgUiScale = _config.Bind("Interface", "UIScale", 0f,
                new ConfigDescription(
                    "Scale factor for the planner panel and its text. 0 = auto (scales with screen height, so 4K screens get a larger panel). Otherwise an explicit multiplier, e.g. 1.5 or 2.0.",
                    new AcceptableValueRange<float>(0f, Hud.MaxUiScale)));
            _cfgUiScaleMultiplier = _config.Bind("Interface", "UIScaleMultiplier", 1.0f,
                new ConfigDescription(
                    "Fine-tune multiplier applied on top of UIScale. 1.0 = no change; below 1.0 makes the panel smaller, above 1.0 larger. Lets you shrink the auto scale without giving up auto-adaptation to your resolution.",
                    new AcceptableValueRange<float>(Hud.MinUiScaleMultiplier, Hud.MaxUiScaleMultiplier)));
            _cfgDebounce = _config.Bind("Timing", "GroupWindowSeconds", Coordinator.DefaultDebounceSeconds,
                new ConfigDescription(
                    "After the last missile order at a target, wait this many real seconds (with no new orders) before locking in the coordinated launch. Larger = easier to group several manual orders from one ship; smaller = snappier single attacks.",
                    new AcceptableValueRange<float>(0.05f, 5.0f)));
            _cfgMaxWindow = _config.Bind("Timing", "MaxCollectSeconds", Coordinator.DefaultMaxWindowSeconds,
                new ConfigDescription(
                    "Hard cap (real seconds) on how long one target keeps collecting orders before it must lock in.",
                    new AcceptableValueRange<float>(0.25f, 20.0f)));
            _cfgVerbose = _config.Bind("Debug", "VerboseLogging", false,
                "Log every queued and released launch.");
            _cfgTraceFlightModel = _config.Bind("Debug", "TraceFlightModel", false,
                "Research tool, off by default. Logs the flight-model internals behind every " +
                "estimate: the integrator's step trace, per-missile telemetry, " +
                "launch geometry and the stage transitions, plus the estimate-vs-actual gap. Use it " +
                "when a flight time looks wrong. It is roughly ten times the volume of ordinary " +
                "verbose logging and runs extra sims per round, so leave it off for normal play " +
                "and for coordination debugging, which VerboseLogging already covers.");
            _cfgProfiling = _config.Bind("Debug", "Profiling", false,
                "Log per-frame timing every 60 frames to diagnose performance issues.");
            ConfigEntry<int> cfgEstimatorThreads = _config.Bind("Performance", "EstimatorThreads", -1,
                "Worker threads for flight-time simulation. 0 runs everything on the main thread, " +
                "as before. -1 picks a count from the CPU: a quarter of the logical cores, at " +
                "least 1 and at most 4. Raise it only if the profiling line shows the queue " +
                "backing up; the workers run below normal priority so the game always wins a " +
                "contended core.");

            _cfgVerifySolve = _config.Bind("Debug", "VerifySolve", false,
                "Re-run every threaded flight simulation on the main thread and warn if the two " +
                "answers differ. Proves the threaded path computes exactly what the main thread " +
                "would. Doubles simulation work, so use it to check correctness, never to measure " +
                "performance.");

            // Started once: restarting a pool mid-mission would strand queued work. A hand-set count is
            // clamped to the core count.
            int threads = cfgEstimatorThreads.Value;
            if (threads < 0) threads = Mathf.Clamp(SystemInfo.processorCount / 4, 1, 4);
            else threads = Mathf.Min(threads, Mathf.Max(1, SystemInfo.processorCount));
            FlightTime.StartWorkers(threads);

            Coordinator.Enabled = cfgEnabled.Value;   // master switch, startup-only (see ApplyConfig)
            Coordinator.Active = cfgDefaultOn.Value;  // runtime toggle's starting state

            ApplyConfig();

            // One file-level subscription; ApplyConfig re-reads every live setting. Enabled, AutoModeOnStart
            // and EstimatorThreads are deliberately read only at startup.
            _config.SettingChanged += (_, __) => ApplyConfig();
        }

        // Relays LogType.Exception with AutoTOT in the stack only. ManualLogSource does not route back
        // through Debug.Log, so this cannot loop.
        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception) return;
            if (stackTrace == null || !stackTrace.Contains("AutoTOT")) return;
            Log.LogError($"[AutoTOT] Unity exception: {condition}\n{stackTrace}");
        }

        private static void ApplyConfig()
        {
            // Enabled is read only at startup: switching it live could cut off work already scheduled.
            Coordinator.DebounceSeconds = _cfgDebounce.Value;
            Coordinator.MaxWindowSeconds = _cfgMaxWindow.Value;
            Coordinator.VerboseLog = _cfgVerbose.Value;
            Coordinator.TraceFlightModel = _cfgTraceFlightModel.Value;
            Coordinator.ProfilingEnabled = _cfgProfiling.Value;
            FlightTime.VerifySolve = _cfgVerifySolve.Value;
            ShowIndicator = _cfgShowIndicator.Value;
            ToggleModifier = _cfgToggleModifier.Value;
            ToggleKey = _cfgToggleKey.Value;
            PanelKey = _cfgPanelKey.Value;
            StrikeArmKey = _cfgStrikeArmKey.Value;
            FireStrikeKey = _cfgFireStrikeKey.Value;
            UiScale = _cfgUiScale.Value;
            UiScaleMultiplier = _cfgUiScaleMultiplier.Value;
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
                    Log.LogInfo("AutoTOT not enabled in the Mods menu, so it is standing down.");
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
            // A menu or pause briefly nulls _mainGameViewModel while the mission keeps running. Resetting
            // then would drop missiles in flight, so reset only after the view model has been gone this long.
            private const float ExitDebounceSeconds = 0.5f;

            private bool _wasInMission;
            private bool _resetPending;   // mission absent, debounce not yet elapsed, reset not yet done
            private float _absentSince;   // Time.unscaledTime when the mission first went absent

            // A repeating tick error is counted and restated on this real-time cadence, not logged every frame.
            private const float ErrorRelogSeconds = 30f;
            private string _lastErrorMsg;
            private int _errorRepeats;
            private float _lastErrorLogReal;

            // A mission clock that jumps back with no reset means a new mission is running on the old state (one
            // loaded without the view model going absent long enough), so the reset runs then.
            private const float ClockBackwardsToleranceSeconds = 1f;
            private float _lastSimNow = -1f;

            private static void ResetSafely(string what, Action reset)
            {
                try { reset(); }
                catch (Exception e)
                {
                    Log.LogError($"[AutoTOT] mission reset of the {what} failed, so some of its state " +
                                 $"from the last mission may remain:\n{e}");
                }
            }

            private void Update()
            {
                bool inMission = Globals._mainGameViewModel != null;

                if (inMission)
                {
                    _resetPending = false;   // mission (re)appeared ; cancel any pending exit reset
                }
                else
                {
                    // Just went absent: start the debounce. Reset only after it elapses, exactly once.
                    if (_wasInMission)
                    {
                        _resetPending = true;
                        _absentSince = Time.unscaledTime;
                    }
                    if (_resetPending && (Time.unscaledTime - _absentSince) >= ExitDebounceSeconds)
                    {
                        // Each part guarded and the pending flag cleared regardless, so a failing reset is
                        // not retried every frame.
                        ResetSafely("coordinator", Coordinator.Reset);
                        _resetPending = false;
                        _lastErrorMsg = null;
                        _errorRepeats = 0;
                        _lastSimNow = -1f;
                    }
                }
                _wasInMission = inMission;

                if (!inMission) return;
                // The master switch is read at startup only; with it off nothing is collected or scheduled, so
                // the per-frame launch scan and the stages behind it have nothing to do.
                if (!Coordinator.Enabled) return;

                float simNow = GameClock.SimNow();
                if (_lastSimNow >= 0f && simNow < _lastSimNow - ClockBackwardsToleranceSeconds)
                {
                    // Every held order and anchor refers to the previous mission's units and clock, so carrying
                    // them over would fire on stale state; the reset the exit debounce missed runs here.
                    Log.LogWarning(
                        $"[AutoTOT] mission clock went back from {_lastSimNow:0.0}s to {simNow:0.0}s " +
                        $"with no mission reset in between, so a new mission is running: resetting the " +
                        $"coordinator now. Please send this log if it happened mid-mission.");
                    ResetSafely("coordinator", Coordinator.Reset);
                    _lastErrorMsg = null;
                    _errorRepeats = 0;
                }
                _lastSimNow = simNow;

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
                        _errorRepeats = 0;
                        _lastErrorLogReal = Time.unscaledTime;
                        Log.LogError($"[AutoTOT] coordinator tick error:\n{e}");
                    }
                    else
                    {
                        _errorRepeats++;
                        if (Time.unscaledTime - _lastErrorLogReal >= ErrorRelogSeconds)
                        {
                            Log.LogError(
                                $"[AutoTOT] coordinator tick error repeated {_errorRepeats} time(s) " +
                                $"in the last {Time.unscaledTime - _lastErrorLogReal:0}s: {msg}");
                            _errorRepeats = 0;
                            _lastErrorLogReal = Time.unscaledTime;
                        }
                    }
                }
            }
        }
    }
}
