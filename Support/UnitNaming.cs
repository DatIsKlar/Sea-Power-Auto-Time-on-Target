using SeaPower;

namespace AutoTOT
{
    /// <summary>
    /// The one way this mod turns a game object into a name for a log line or a panel row: it never throws,
    /// and gives up the informative name last.
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
