using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace AutoTOT
{
    /// <summary>
    /// Defensive shield for a base-game crash on the multiplayer mission-load path.
    ///
    /// <para>
    /// <c>SeaPower.PlottingTableSerializer.RecreateWorldUsingTemp</c> calls
    /// <c>DefaultWorldInitialization.Initialize</c>, which re-runs the DOTS bootstrap and
    /// enumerates every assembly in the AppDomain. For each one it calls a
    /// <c>Unity.Entities.TypeManager.IsAssemblyReferencing*</c> filter, which in turn calls
    /// <see cref="Assembly.GetName"/>. If any loaded assembly has an invalid culture string in
    /// its native name, <c>AssemblyName.FillName</c> → <c>CultureInfo..ctor</c> throws
    /// ("Parameter name: name"), which aborts the LoadMission coroutine and the mission never
    /// loads.
    /// </para>
    /// <para>
    /// At normal game boot DOTS initializes before any plugin is present, so that first scan is
    /// clean; only the multiplayer <c>RecreateWorldUsingTemp</c> re-scan runs with mods (and
    /// their MonoMod/Harmony dynamic assemblies) loaded, which is why the crash is
    /// multiplayer-only and environment-specific.
    /// </para>
    /// <para>
    /// Installation is robust against load order and DOTS version drift. The exact filter
    /// method differs between Entities versions — <c>IsAssemblyReferencingEntities(Assembly)</c>
    /// on older builds, plus
    /// <c>IsAssemblyReferencingEntitiesOrUnityEngine(Assembly, out bool, out bool)</c> on the
    /// Unity 6 build — and <c>Unity.Entities.dll</c> may not even be loaded into the AppDomain
    /// yet when this mod initializes (in which case a fixed Harmony target cannot resolve).
    /// Therefore we discover every <c>IsAssemblyReferencing*(Assembly, ...)</c> method on
    /// <c>TypeManager</c>, force-load the assembly if possible, or otherwise defer installation
    /// to the moment it loads, and shield each method with a finalizer that swallows the throw.
    /// An assembly the scan cannot even name cannot be one it needs ECS types from, so letting
    /// the call return with its outputs left at their defaults ("does not reference entities")
    /// is correct.
    /// </para>
    /// </summary>
    internal static class DotsScanHardening
    {
        private const string TypeManagerFullName = "Unity.Entities.TypeManager";
        private const string EntitiesAssemblyName = "Unity.Entities";
        private const string ScanMethodPrefix = "IsAssemblyReferencing";

        // Signatures of throws we've already reported, so the log isn't spammed
        // (the DOTS scan may visit the bad assembly many times across re-inits).
        private static readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);

        private static bool _installed;
        private static bool _hooked;

        /// <summary>
        /// Install the shield. Called from <see cref="Bootstrap.Init"/> after Harmony.PatchAll.
        /// Never throws: on any failure a warning is logged and the mod continues unshielded.
        /// </summary>
        internal static void Install(Harmony harmony)
        {
            try
            {
                Type typeManager = AccessTools.TypeByName(TypeManagerFullName);

                if (typeManager == null)
                {
                    // Unity.Entities isn't in the AppDomain yet. Try to pull it in now (Unity
                    // probes the Managed folder); if that doesn't work, defer installation to
                    // the moment the assembly loads — still before any DOTS code can run.
                    try
                    {
                        Assembly.Load(EntitiesAssemblyName);
                        typeManager = AccessTools.TypeByName(TypeManagerFullName);
                    }
                    catch
                    {
                        // fall through to the deferred path
                    }
                }

                if (typeManager != null)
                {
                    PatchScanMethods(harmony, typeManager);
                    return;
                }

                if (_hooked) return;
                _hooked = true;
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                Bootstrap.Log.LogInfo(
                    "[AutoTOT] DOTS scan hardening: Unity.Entities not loaded yet — the shield will install automatically when it loads.");
            }
            catch (Exception e)
            {
                Bootstrap.Log.LogWarning($"[AutoTOT] DOTS scan hardening install failed — shield disabled, mod continues:\n{e}");
            }
        }

        /// <summary>
        /// Deferred install: fires when Unity.Entities.dll finally loads. Must never throw —
        /// an exception here would leak into whatever game code triggered the load.
        /// </summary>
        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                string name = null;
                try
                {
                    name = args?.LoadedAssembly?.GetName()?.Name;
                }
                catch
                {
                    // An assembly whose very name throws is exactly what we shield against;
                    // it cannot be Unity.Entities.
                }
                if (name != EntitiesAssemblyName) return;

                AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                _hooked = false;

                Type typeManager = AccessTools.TypeByName(TypeManagerFullName);
                if (typeManager != null)
                    PatchScanMethods(Bootstrap.Harmony, typeManager);
            }
            catch (Exception e)
            {
                Bootstrap.Log.LogWarning($"[AutoTOT] DOTS scan hardening deferred install failed:\n{e}");
            }
        }

        /// <summary>
        /// Find and patch every static <c>TypeManager.IsAssemblyReferencing*(Assembly, ...)</c>
        /// scan filter. The finalizer-only patch works for all known shapes (bool return,
        /// void with out-bools) because a swallowed throw leaves the caller-side outputs at
        /// their defaults, which always means "not referencing entities".
        /// </summary>
        private static void PatchScanMethods(Harmony harmony, Type typeManager)
        {
            if (_installed) return;

            int patched = 0;
            foreach (MethodInfo method in typeManager.GetMethods(
                         BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!method.Name.StartsWith(ScanMethodPrefix, StringComparison.Ordinal)) continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 0 || parameters[0].ParameterType != typeof(Assembly)) continue;

                try
                {
                    harmony.Patch(method,
                        finalizer: new HarmonyMethod(typeof(DotsScanHardening), nameof(ShieldFinalizer)));
                    patched++;
                    Bootstrap.Log.LogInfo(
                        $"[AutoTOT] DOTS scan hardening target resolved: {method.DeclaringType?.FullName}.{Format(method)}");
                }
                catch (Exception e)
                {
                    Bootstrap.Log.LogWarning($"[AutoTOT] DOTS scan hardening failed to patch {method.Name}: {e.Message}");
                }
            }

            if (patched > 0)
            {
                _installed = true;
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] DOTS scan hardening active: {patched} scan method(s) shielded — multiplayer mission-load crash shield enabled.");
            }
            else
            {
                Bootstrap.Log.LogWarning(
                    "[AutoTOT] DOTS scan hardening target NOT found (TypeManager has no IsAssemblyReferencing*(Assembly, ...) methods) — DOTS may have changed; shield disabled, mod continues.");
            }
        }

        private static string Format(MethodInfo method)
        {
            var names = new List<string>();
            foreach (ParameterInfo p in method.GetParameters())
                names.Add(p.ParameterType.IsByRef
                    ? (p.ParameterType.GetElementType()?.Name ?? "?") + "&"
                    : p.ParameterType.Name);
            return $"{method.Name}({string.Join(", ", names)})";
        }

        /// <summary>
        /// Catch-all: if a scan filter throws (the invalid-culture GetName() case), swallow the
        /// exception and report it once. Returning null from a finalizer suppresses the
        /// exception; the caller then sees the method's result/outputs at their defaults, which
        /// for every known filter variant means "does not reference entities" — so the
        /// unnameable assembly is skipped and mission load continues.
        /// </summary>
        private static Exception ShieldFinalizer(Exception __exception)
        {
            if (__exception == null)
                return null;

            ReportOnce(__exception);
            return null;
        }

        private static void ReportOnce(Exception ex)
        {
            string id = $"{ex.GetType().FullName}: {ex.Message}";
            bool isNew;
            lock (_reported)
                isNew = _reported.Add(id);

            if (isNew)
            {
                Bootstrap.Log.LogWarning(
                    $"[AutoTOT] DOTS assembly scan would have crashed on an unnameable assembly; " +
                    $"treating it as non-ECS so mission load can continue. Underlying: {id}");
            }
        }

        /// <summary>Best-effort identifier for an assembly whose GetName()/FullName may itself throw.</summary>
        internal static string SafeIdentify(Assembly assembly)
        {
            if (assembly == null) return "<null>";
            try { return assembly.FullName; } catch { }
            try { return assembly.Location; } catch { }
            try { return assembly.ManifestModule?.Name ?? "<unknown>"; } catch { }
            return "<unidentifiable>";
        }
    }
}
