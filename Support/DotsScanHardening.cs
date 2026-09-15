using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace AutoTOT
{
    /// <summary>
    /// Shield for a base-game crash on the multiplayer mission-load path: the DOTS assembly scan throws on an
    /// unnameable assembly, and Harmony finalizers swallow that throw. See docs/ARCHITECTURE.md "DOTS scan hardening".
    /// </summary>
    internal static class DotsScanHardening
    {
        private const string TypeManagerFullName = "Unity.Entities.TypeManager";
        private const string EntitiesAssemblyName = "Unity.Entities";
        private const string ScanMethodPrefix = "IsAssemblyReferencing";

        // Signatures already reported, since the DOTS scan may visit the bad assembly many times across re-inits.
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
                    // Unity.Entities is not loaded yet: try to load it now (Unity probes the Managed folder),
                    // else install when the assembly loads, still before any DOTS code runs.
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
        /// Deferred install when Unity.Entities.dll loads. Must never throw: the exception would leak into
        /// the game code that triggered the load.
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
                    // An assembly whose name throws is what this shields against; it cannot be Unity.Entities.
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
        /// Find and patch every static <c>TypeManager.IsAssemblyReferencing*(Assembly, ...)</c> scan filter.
        /// A finalizer-only patch covers every known shape (bool return, void with out-bools): a swallowed
        /// throw leaves the outputs at their defaults, which mean "not referencing entities".
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
        /// Suppresses the failure this shield exists for: <c>Assembly.GetName()</c> on an assembly whose name
        /// carries an invalid culture, thrown out of a DOTS scan filter during mission load. Returning null
        /// suppresses it, the caller sees the defaults ("does not reference entities"), and load continues.
        /// Anything else is reported and re-thrown, so an unrelated DOTS fault stays visible and the game
        /// behaves as it would without the mod.
        /// </summary>
        private static Exception ShieldFinalizer(Exception __exception)
        {
            if (__exception == null)
                return null;

            ReportOnce(__exception);
            return IsAssemblyNameFailure(__exception) ? null : __exception;
        }

        /// <summary>
        /// True for the invalid-assembly-name failure at any depth (the throw arrives wrapped when the filter
        /// is called through reflection). Matched on exception type and faulting parameter name, because the
        /// message text depends on culture.
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
