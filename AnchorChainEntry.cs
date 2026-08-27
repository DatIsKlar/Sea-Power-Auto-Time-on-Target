using System;
using AnchorChain;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// AnchorChain entry point. The AnchorChain chainloader discovers this class via
    /// [ACPlugin] and calls <see cref="TriggerEntryPoint"/> when the mod is enabled;
    /// all real initialization lives in <see cref="Bootstrap"/> (which first waits for
    /// the game's mod-menu state to become readable).
    /// </summary>
    // Attribute arguments must be compile-time constants; Bootstrap.Version is one.
    // Keep in sync with AutoTOT.csproj <Version>.
    [ACPlugin(Bootstrap.Guid, "Auto Time-on-Target", Bootstrap.Version, null, null)]
    public class AnchorChainEntry : IAnchorChainMod
    {
        public void TriggerEntryPoint()
        {
            try
            {
                Bootstrap.InitIfEnabled();
            }
            catch (Exception e)
            {
                Debug.LogError($"[AutoTOT] failed to initialize: {e}");
            }
        }
    }
}
