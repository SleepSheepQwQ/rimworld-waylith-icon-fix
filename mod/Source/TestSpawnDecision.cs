using System;
using System.Globalization;

namespace AgentTouchFix
{
    /// <summary>
    /// Pure half of the "testspawn" diagnostic: the two log lines, the <c>same</c> predicate, and the
    /// placement decision (this cell, else an adjacent cell, else give up). No game types and no Unity
    /// types, so CI exercises every branch here instead of on a device.
    ///
    /// The question this answers: a weapon's *instance* graphic can be blank while the same def draws
    /// fine on another instance. The diagnostic therefore copies the weapon to the ground - a brand new
    /// instance made from the same def/stuff - and prints the original and the copy side by side, so
    /// two instances of one def can be compared in a single log.
    /// </summary>
    internal static class TestSpawnDecision
    {
        /// <summary>Origin lines (and, in lockstep, spawn lines) one load may write. Twenty pairs is
        /// enough to compare instances and small enough that the log stays readable.</summary>
        internal const int LineCap = 20;

        /// <summary>Longest value copied into a log line. Longer values are cut and marked with '~' so
        /// one thing stays one line.</summary>
        internal const int MaxCell = 200;

        /// <summary>Where the copy can go.</summary>
        internal enum Placement
        {
            /// <summary>Nowhere: the cell is unusable and no neighbour is usable either.</summary>
            None,

            /// <summary>The pawn's own cell.</summary>
            Here,

            /// <summary>An adjacent cell; the caller logs the substitution.</summary>
            Adjacent,
        }

        // ------------------------------------------------------------------ placement

        /// <summary>
        /// Cell first, then a neighbour: the copy belongs where the pawn stands, because that is what
        /// makes the two instances easy to compare in game. Only a cell that cannot hold a thing sends
        /// the copy sideways, and only when a neighbour can take it - otherwise nothing is spawned at
        /// all rather than a thing dropped somewhere arbitrary.
        /// </summary>
        internal static Placement Choose(bool cellUsable, bool neighbourUsable)
        {
            if (cellUsable) return Placement.Here;
            if (neighbourUsable) return Placement.Adjacent;
            return Placement.None;
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
        /// The original weapon:
        /// <c>test: origin def=&lt;defName&gt; id=&lt;id&gt; graphic=&lt;type&gt; tex=&lt;name&gt;
        /// size=WxH uiIcon=&lt;name|BadTex&gt; same=&lt;true|false&gt;</c>
        /// </summary>
        internal static string OriginLine(string defName, int id, string graphicType, string texName,
                                          int width, int height, string uiIcon, bool same)
        {
            return Line("test: origin ", defName, id, graphicType, texName, width, height, uiIcon, same);
        }

        /// <summary>
        /// The copy that was just made and dropped. Same fields, one extra space after "spawn" so the
        /// two lines align in the log and in a grep.
        /// </summary>
        internal static string SpawnLine(string defName, int id, string graphicType, string texName,
                                         int width, int height, string uiIcon, bool same)
        {
            return Line("test: spawn  ", defName, id, graphicType, texName, width, height, uiIcon, same);
        }

        private static string Line(string prefix, string defName, int id, string graphicType, string texName,
                                   int width, int height, string uiIcon, bool same)
        {
            return prefix
                 + "def=" + Cell(defName)
                 + " id=" + Int(id)
                 + " graphic=" + Cell(graphicType)
                 + " tex=" + Cell(texName)
                 + " size=" + Size(width, height)
                 + " uiIcon=" + Cell(uiIcon)
                 + " same=" + (same ? "true" : "false");
        }

        /// <summary><c>test: skip &lt;reason&gt;</c> - the only line a failure may produce, and never a
        /// reason for the load to stop.</summary>
        internal static string SkipLine(string reason)
        {
            return "test: skip " + Cell(reason);
        }

        /// <summary><c>test: fallback &lt;label&gt; from=&lt;x,z&gt; to=&lt;x,z&gt;</c> - the copy could
        /// not stand where the pawn does, so it went next door.</summary>
        internal static string FallbackLine(string label, string from, string to)
        {
            return "test: fallback " + Cell(label) + " from=" + Cell(from) + " to=" + Cell(to);
        }

        // ------------------------------------------------------------------ the fixture sample

        /// <summary>The unique revolver the report is about - present in Odyssey, absent from the
        /// save that could not reproduce the bug.</summary>
        internal const string FixtureDefName = "Gun_Revolver_Unique";

        /// <summary>Random-variant graphics are the ones whose variants carry their own masks.</summary>
        internal const string RandomGraphicPrefix = "Graphic_Random";

        /// <summary>
        /// Is this def a usable sample? It has to have both properties that make a tile draw blank in
        /// this port: a graphic that picks a random variant (so several sub-graphics exist and the
        /// count is worth printing) and a mask that the content index actually has. A def without a
        /// mask would be expected to draw fine and would prove nothing.
        /// </summary>
        internal static bool FixtureCandidate(string graphicClass, bool maskFound)
        {
            if (!maskFound || string.IsNullOrEmpty(graphicClass)) return false;
            return graphicClass.StartsWith(RandomGraphicPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// <c>test: fixture def=&lt;defName&gt; id=&lt;id&gt; graphic=&lt;type&gt; tex=&lt;name&gt;
        /// size=WxH uiIcon=&lt;name|BadTex&gt; same=&lt;bool&gt; graphicClass=&lt;type&gt;
        /// texPath=&lt;path&gt; subgraphics=&lt;n|-&gt;</c>
        ///
        /// The first eight fields match the origin/spawn lines, so the same grep finds all three; the
        /// last three answer "how many variants does this def really have" (5 vs 10) without a second
        /// log line. subgraphics is "-" when the graphic is not a Graphic_Collection.
        /// </summary>
        internal static string FixtureLine(string defName, int id, string graphicType, string texName,
                                           int width, int height, string uiIcon, bool same,
                                           string graphicClass, string texPath, int subGraphics)
        {
            return "test: fixture"
                 + " def=" + Cell(defName)
                 + " id=" + Int(id)
                 + " graphic=" + Cell(graphicType)
                 + " tex=" + Cell(texName)
                 + " size=" + Size(width, height)
                 + " uiIcon=" + Cell(uiIcon)
                 + " same=" + (same ? "true" : "false")
                 + " graphicClass=" + Cell(graphicClass)
                 + " texPath=" + Cell(texPath)
                 + " subgraphics=" + (subGraphics < 0 ? "-" : Int(subGraphics));
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
