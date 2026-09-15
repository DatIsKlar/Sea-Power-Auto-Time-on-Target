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

        // Fault messages already logged, so a fault that repeats on every order is reported once.
        // Capped: the set only has to stop a flood, not remember a whole session.
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

                // Deferred. Hand back a valid (but un-queued) EngageTask so the caller's
                // logging (which reads engageTask._uid) doesn't NRE. It is never added to
                // the ship's task list; the real task is created later by the Coordinator.
                __result = new EngageTask(ammoId, targetObject, __instance, shotsToFire, priority, autoAttack, isFormationAttack);
                return false;
            }
            catch (Exception e)
            {
                // Never re-throw. InsertEngageTask is also reached from inside the game's per-object
                // update, and GameMain.Update walks its objects with no try/catch, so an exception
                // escaping here would stop every object after this one from updating for as long as
                // the fault repeats: radars, AI and engage handling included. What happens to the
                // order depends on how far TryIntercept got before it threw.
                bool held = Coordinator.InterceptQueued;
                LogFault(e, __instance, targetObject, ammoId, shotsToFire, isFormationAttack, autoAttack, held);
                if (!held)
                {
                    // Nothing was held, so the game fires the order itself, as it would without the
                    // mod. A batch this call opened for the target is still empty and is dropped.
                    Coordinator.DiscardEmptyBatch(targetObject);
                    return true;
                }

                // The order is already held and the coordinator will release it. Letting the game
                // run it as well would fire it twice.
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
