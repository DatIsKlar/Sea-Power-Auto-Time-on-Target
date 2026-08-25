using System;
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
                    return true; // not a coordinated player group missile attack — run normally
                }

                // Deferred. Hand back a valid (but un-queued) EngageTask so the caller's
                // logging (which reads engageTask._uid) doesn't NRE. It is never added to
                // the ship's task list; the real task is created later by the Coordinator.
                __result = new EngageTask(ammoId, targetObject, __instance, shotsToFire, priority, autoAttack, isFormationAttack);
                return false;
            }
            catch (Exception e)
            {
                // Log with the order context that pinpoints which launch broke, then
                // re-throw so behaviour is unchanged (the game sees the same exception
                // it would have seen without this mod's logging).
                Bootstrap.Log.LogError(
                    $"[AutoTOT] InsertEngageTask prefix threw for " +
                    $"{Safe(() => __instance?.getUIDAndName())} -> {Safe(() => targetObject?.getUIDAndName())} " +
                    $"(ammo={ammoId}, shots={shotsToFire}, formation={isFormationAttack}, auto={autoAttack}):\n{e}");
                throw;
            }
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? "null"; } catch { return "?"; }
        }
    }
}
