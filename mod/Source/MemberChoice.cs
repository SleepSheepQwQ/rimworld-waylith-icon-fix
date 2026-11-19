using System;

namespace AgentTouchFix
{
    /// <summary>
    /// Pure selection logic for the three reflection lookups that regressed on the device - no game
    /// types, no <see cref="System.Reflection"/> types, so CI unit-tests every branch here:
    ///
    ///   1. <see cref="Choose"/>       - a named value: field or property, prefer field or prefer property
    ///   2. <see cref="GetIconFor"/>   - which Verse.Widgets.GetIconFor overloads to postfix
    ///   3. <see cref="Standable"/>    - static Verse.GenGrid.Standable(IntVec3, Map) or the instance form
    ///
    /// The device log of 2.6.1 showed all three failing for the same reason: the lookup assumed one
    /// shape (an instance field, a parameter called "material" on exactly one overload, an instance
    /// method on IntVec3) and silently produced nothing when the shipped assembly had another.
    /// Deciding the shape here, from booleans the caller measured, keeps that decision testable.
    /// </summary>
    internal static class MemberChoice
    {
        // ------------------------------------------------------------------ a named value

        /// <summary>Nothing carries the name, or nothing usable does.</summary>
        internal const int None = 0;

        /// <summary>The name is a field (the shipped assembly's usual shape for def data).</summary>
        internal const int Field = 1;

        /// <summary>The name is a property (RimWorld.Faction.OfPlayer is one).</summary>
        internal const int Property = 2;

        /// <summary>
        /// Pick the member to use. "Usable" already folds in the caller's requirement - a value that
        /// has to be written only counts when it can be written - so this is purely the ordering:
        /// a field when one is usable, otherwise the property, and when neither is usable nothing.
        ///
        /// <paramref name="preferField"/> exists because the two callers want opposite defaults and
        /// both are right for their type: def data on this port is public fields (uiIcon), while the
        /// game's own API for the player faction is a static property (OfPlayer).
        /// </summary>
        internal static int Choose(bool fieldUsable, bool propertyUsable, bool preferField)
        {
            if (preferField)
            {
                if (fieldUsable) return Field;
                if (propertyUsable) return Property;
                return None;
            }

            if (propertyUsable) return Property;
            if (fieldUsable) return Field;
            return None;
        }

        /// <summary>The word the log uses: "field", "property" or "none".</summary>
        internal static string Name(int kind)
        {
            if (kind == Field) return "field";
            if (kind == Property) return "property";
            return "none";
        }

        // ------------------------------------------------------------------ GetIconFor overloads

        /// <summary>Not one of the two entry points the fallback belongs on.</summary>
        internal const int Reject = 0;

        /// <summary>GetIconFor(Thing, Vector2, Rot4?, bool, out float, out float, out Vector2, out Color, out Material).</summary>
        internal const int ThingOverload = 1;

        /// <summary>GetIconFor(ThingDef, out Material, ThingDef, ThingStyleDef, int?).</summary>
        internal const int DefOverload = 2;

        /// <summary>
        /// Which postfix, if any, an overload gets.
        ///
        /// The two that matter are identified by shape, not by name: the first parameter says whose
        /// icon it is (Thing or ThingDef) and a by-ref Material parameter is what lets the postfix
        /// replace the material that just failed. A ThingDef overload without one is the 4-argument
        /// convenience form that forwards to the 5-argument one - patching it too would run the
        /// fallback twice for one draw, so it is rejected.
        ///
        /// The Thing overload also has to hand out a by-ref Color: the postfix sets it to white so
        /// Widgets takes its plain path, and there is no second postfix shape for a Thing overload
        /// without one.
        /// </summary>
        internal static int GetIconFor(bool firstIsThing, bool firstIsThingDef, bool hasByRefMaterial,
                                       bool hasByRefColor)
        {
            if (!hasByRefMaterial) return Reject;
            if (firstIsThing) return hasByRefColor ? ThingOverload : Reject;
            if (firstIsThingDef) return DefOverload;
            return Reject;
        }

        // ------------------------------------------------------------------ Standable

        /// <summary>No Standable method at all: every cell is treated as usable (fail-open).</summary>
        internal const int StandableNone = 0;

        /// <summary>Verse.GenGrid.Standable(IntVec3, Map) - a static method, two parameters.</summary>
        internal const int StandableStatic = 1;

        /// <summary>IntVec3.Standable(Map) - an instance method on the cell struct.</summary>
        internal const int StandableInstance = 2;

        /// <summary>
        /// The shipped assembly has the static form (Verse.GenGrid.Standable(IntVec3 c, Map map));
        /// the instance form is kept as a fallback for a build that moves it back onto IntVec3.
        /// Static wins when both exist: it is the one the game's own spawn code calls.
        /// </summary>
        internal static int Standable(bool staticFound, bool instanceFound)
        {
            if (staticFound) return StandableStatic;
            if (instanceFound) return StandableInstance;
            return StandableNone;
        }

        internal static string StandableName(int kind)
        {
            if (kind == StandableStatic) return "static";
            if (kind == StandableInstance) return "instance";
            return "none";
        }
    }
}
