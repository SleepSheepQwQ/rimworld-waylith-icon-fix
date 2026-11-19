using System;
using System.Globalization;

namespace AgentTouchFix
{
    /// <summary>
    /// Pure half of the "testspawn" diagnostic: the log lines, the <c>same</c> predicate and the
    /// line budget. No game types and no Unity types, so CI exercises every branch here instead of on
    /// a device.
    ///
    /// The question it answers: for one thing, what would the draw path actually use, and is that
    /// texture the very object the def's <c>uiIcon</c> points at? A blank icon on one instance of a
    /// def and a good icon on another is an instance-level split, and these columns are what tells
    /// the two apart in a single log line.
    ///
    /// The module is read-only by contract: nothing here - and nothing in <see cref="TestSpawn"/> -
    /// creates, deletes, moves or edits anything in the world. See the guard step in
    /// .github/workflows/build.yml.
    /// </summary>
    internal static class TestSpawnDecision
    {
        /// <summary>Log lines one load may write. Twenty is enough to compare instances and small
        /// enough that the log stays readable.</summary>
        internal const int LineCap = 20;

        /// <summary>Longest value copied into a log line. Longer values are cut and marked with '~' so
        /// one thing stays one line.</summary>
        internal const int MaxCell = 200;

        /// <summary>The one line written when the loaded save has no player pawn at all. Spelled out
        /// here so the log format cannot drift away from what a reader greps for.</summary>
        internal const string NoPlayerPawns = "no-player-pawns";

        // ------------------------------------------------------------------ budget

        /// <summary>May another line be written? The cap is a hard limit on output, not a hint.</summary>
        internal static bool HasRoom(int written, int cap)
        {
            return written < cap;
        }

        // ------------------------------------------------------------------ same

        /// <summary>
        /// Is the instance's main texture the very same object as def.uiIcon?
        ///
        /// Two absences answer false even though ReferenceEquals(null, null) is true: printing "same"
        /// for a thing with no texture at all would read as a healthy match. A BadTex placeholder
        /// compares by identity like any other object, and the line already names it as BadTex.
        /// </summary>
        internal static bool Same(bool texNull, bool iconNull, bool referenceEqual)
        {
            if (texNull || iconNull) return false;
            return referenceEqual;
        }

        // ------------------------------------------------------------------ log lines

        /// <summary>
        /// One thing, one line:
        /// <c>test: thing def=&lt;defName&gt; id=&lt;id&gt; graphic=&lt;type&gt; tex=&lt;name&gt;
        /// size=WxH uiIcon=&lt;name|BadTex&gt; same=&lt;true|false&gt; subgraphics=&lt;n|-&gt;</c>
        ///
        /// <paramref name="subGraphics"/> is the variant count only when the graphic really is a
        /// <c>Graphic_Collection</c>; every other graphic passes -1 and prints "-", because a count
        /// that does not exist must not read as zero variants.
        /// </summary>
        internal static string ThingLine(string defName, int id, string graphicType, string texName,
                                         int width, int height, string uiIcon, bool same, int subGraphics)
        {
            return "test: thing"
                 + " def=" + Cell(defName)
                 + " id=" + Int(id)
                 + " graphic=" + Cell(graphicType)
                 + " tex=" + Cell(texName)
                 + " size=" + Size(width, height)
                 + " uiIcon=" + Cell(uiIcon)
                 + " same=" + (same ? "true" : "false")
                 + " subgraphics=" + SubGraphicsText(subGraphics);
        }

        /// <summary>The variant count column: the count, or "-" when the graphic has no variants.</summary>
        internal static string SubGraphicsText(int count)
        {
            return count < 0 ? "-" : Int(count);
        }

        /// <summary><c>test: skip &lt;reason&gt;</c> - the only line a failure may produce, and never a
        /// reason for the load to stop. <c>SkipLine(NoPlayerPawns)</c> is exactly
        /// <c>test: skip no-player-pawns</c>.</summary>
        internal static string SkipLine(string reason)
        {
            return "test: skip " + Cell(reason);
        }

        // ------------------------------------------------------------------ field rendering

        /// <summary>Text of a value as it appears after "key=". Null, empty and whitespace-only print as
        /// "null"; control characters (a label may carry "\n") collapse to spaces; the result is
        /// trimmed and capped at <see cref="MaxCell"/> characters with a trailing '~'.</summary>
        internal static string Cell(string value)
        {
            if (value == null || value.Length == 0) return "null";

            char[] buf = null;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c < ' ' || c == '\u007f')
                {
                    if (buf == null) buf = value.ToCharArray();
                    buf[i] = ' ';
                }
            }
            string s = (buf == null ? value : new string(buf)).Trim();
            if (s.Length == 0) return "null";

            if (s.Length > MaxCell) s = s.Substring(0, MaxCell) + "~";
            return s;
        }

        /// <summary>"WxH", or "null" when there is no texture to measure.</summary>
        internal static string Size(int width, int height)
        {
            if (width <= 0 || height <= 0) return "null";
            return Int(width) + "x" + Int(height);
        }

        private static string Int(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
