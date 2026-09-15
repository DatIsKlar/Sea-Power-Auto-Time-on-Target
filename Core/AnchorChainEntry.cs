using System;
using AnchorChain;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>AnchorChain entry point ([ACPlugin]); all initialization lives in <see cref="Bootstrap"/>.</summary>
    // Attribute arguments must be constants; Bootstrap.Version keeps the version in one place.
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
