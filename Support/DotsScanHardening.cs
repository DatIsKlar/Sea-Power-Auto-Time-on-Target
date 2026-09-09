using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace AutoTOT
{
    /// <summary>
    /// Defensive shield for a base-game crash on the multiplayer mission-load path.
    /// The DOTS assembly scan throws on an unnameable assembly; this swallows the throw
    /// via Harmony finalizers. See docs/ARCHITECTURE.md "DOTS scan hardening".
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
                    // the moment the assembly loads ; still before any DOTS code can run.
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
                    "[AutoTOT] DOTS scan hardening: Unity.Entities not loaded yet. The shield will install automatically when it loads.");
            }
            catch (Exception e)
            {
                Bootstrap.Log.LogWarning($"[AutoTOT] DOTS scan hardening install failed. Shield disabled, mod continues:\n{e}");
            }
        }

        /// <summary>
        /// Deferred install: fires when Unity.Entities.dll finally loads. Must never throw ;
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
                    $"[AutoTOT] DOTS scan hardening active: {patched} scan method(s) shielded, multiplayer mission-load crash shield enabled.");
            }
            else
            {
                Bootstrap.Log.LogWarning(
                    "[AutoTOT] DOTS scan hardening target NOT found (TypeManager has no IsAssemblyReferencing*(Assembly, ...) methods). DOTS may have changed. Shield disabled, mod continues.");
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
        /// The failure this shield exists for: <c>Assembly.GetName()</c> on an assembly whose name
        /// carries an invalid culture, which throws out of a DOTS scan filter and takes mission load
        /// with it. Returning null from a finalizer suppresses the exception; the caller then sees
        /// the method's outputs at their defaults, which for every known filter variant means "does
        /// not reference entities", so the unnameable assembly is skipped and load continues.
        ///
        /// NARROW, deliberately. This used to swallow every exception from a patched scan method,
        /// which would have hidden an unrelated DOTS fault inside the mod's own shield and left
        /// nobody able to see it. Anything that is not the documented failure is reported and
        /// RE-THROWN, so the game behaves exactly as it would without the mod.
        /// </summary>
        private static Exception ShieldFinalizer(Exception __exception)
        {
            if (__exception == null)
                return null;

            ReportOnce(__exception);
            return IsAssemblyNameFailure(__exception) ? null : __exception;
        }

        /// <summary>
        /// True for the invalid-assembly-name failure, at any depth: the throw arrives wrapped when
        /// the scan filter is itself called through reflection. Matched on the exception type plus
        /// the name of the parameter it faults on, because the message text is culture-dependent and
        /// cannot be compared against a fixed string.
        /// </summary>
        private static bool IsAssemblyNameFailure(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (e is CultureNotFoundException) return true;
                if (e is FileLoadException || e is BadImageFormatException) return true;
                if (e is ArgumentException a &&
                    string.Equals(a.ParamName, "name", StringComparison.Ordinal)) return true;
            }
            return false;
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
