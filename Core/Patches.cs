using System;
using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Intercepts player-issued group missile attacks and defers each ship's launch
    /// so the whole salvo arrives on target together (Time-on-Target).
    /// </summary>
    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.InsertEngageTask))]
    internal static class InsertEngageTask_Patch
    {
        // Set while the coordinator issues the real, deferred launch, so we don't
        // intercept our own call recursively.
        [System.ThreadStatic] internal static bool Bypass;

        // Fault messages already logged (capped), so a repeating fault is reported once.
        private const int MaxFaultMessages = 32;
        private static readonly HashSet<string> _faultsLogged = new HashSet<string>();

        // Prefix: return false to skip the immediate launch and defer it instead.
        private static bool Prefix(
            ObjectBase __instance,
            string ammoId,
            ObjectBase targetObject,
            Vector3 targetPosition,
            int shotsToFire,
            int priority,
            bool autoAttack,
            bool markAsReturned,
            bool isFormationAttack,
            ref EngageTask __result)
        {
            if (Bypass) return true;

            try
            {
                if (!Coordinator.TryIntercept(
                        __instance, ammoId, targetObject,
                        autoAttack, isFormationAttack, shotsToFire, priority))
                {
                    return true; // not a coordinated player group missile attack ; run normally
                }

                // Deferred: hand back an unqueued EngageTask so the caller's logging does not throw. The
                // coordinator issues the real task later.
                __result = new EngageTask(ammoId, targetObject, __instance, shotsToFire, priority, autoAttack, isFormationAttack);
                return false;
            }
            catch (Exception e)
            {
                // Never re-throw: this can run inside GameMain.Update's per-object loop, which has no
                // try/catch, so a throw would stop every later object updating. What happens to the order
                // depends on how far TryIntercept got.
                bool held = Coordinator.InterceptQueued;
                LogFault(e, __instance, targetObject, ammoId, shotsToFire, isFormationAttack, autoAttack, held);
                if (!held)
                {
                    // Nothing held: the game fires the order itself. Drop the empty batch this call may have opened.
                    Coordinator.DiscardEmptyBatch(targetObject);
                    return true;
                }

                // Already held and released later by the coordinator; letting the game run it too would fire it twice.
                try { __result = new EngageTask(ammoId, targetObject, __instance, shotsToFire, priority, autoAttack, isFormationAttack); }
                catch { }
                return false;
            }
        }

        private static void LogFault(Exception e, ObjectBase unit, ObjectBase target, string ammoId,
                                     int shots, bool formation, bool auto, bool held)
        {
            try
            {
                string key = e.GetType().Name + ": " + e.Message;
                if (_faultsLogged.Count >= MaxFaultMessages || !_faultsLogged.Add(key)) return;
                Bootstrap.Log.LogError(
                    $"[AutoTOT] InsertEngageTask prefix threw for " +
                    $"{Safe(() => unit?.getUIDAndName())} -> {Safe(() => target?.getUIDAndName())} " +
                    $"(ammo={ammoId}, shots={shots}, formation={formation}, auto={auto}). " +
                    (held
                        ? "The order was already held, so AutoTOT still releases it. "
                        : "The order was handed back to the game uncoordinated. ") +
                    $"Reported once per message:\n{e}");
            }
            catch { }
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? "null"; } catch { return "?"; }
        }
    }
}
