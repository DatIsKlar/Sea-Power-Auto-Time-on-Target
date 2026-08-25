using System;
using AnchorChain;
using UnityEngine;

namespace AutoTOT
{
    [ACPlugin("com.seapowermods.autotot", "Auto Time-on-Target", "0.1.0", null, null)]
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
