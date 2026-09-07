using SeaPower;

namespace AutoTOT
{
    /// <summary>
    /// The one way this mod turns a game object into a name for a log line or a panel row.
    ///
    /// Three near-copies of this used to exist (in Coordinator, LaunchDiagnostics and Hud) and they
    /// disagreed in both directions: two fell back to the Unity object name when
    /// <c>getUIDAndName</c> threw and one returned "?", and they returned "-" or "?" or threw for a
    /// null. The panel copy was the one with no null guard, and it labels ship rows, so this is not
    /// a logging-only helper. The union below is what every caller wanted: never throw, and give up
    /// the informative name last rather than first.
    /// </summary>
    internal static class UnitNaming
    {
        /// <summary>
        /// The object's UID-and-name, its Unity object name if the game's accessor throws (a
        /// destroyed object mid-frame is the usual cause), and "?" if there is nothing to say.
        /// </summary>
        internal static string SafeName(ObjectBase o)
        {
            if (o == null) return "?";
            try { return o.getUIDAndName(); }
            catch
            {
                try { return o.name; } catch { return "?"; }
            }
        }
    }
}
