using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// Odyssey weapon mods draw no icon on the instance while the same def draws fine in the build
    /// menu (Gun_Revolver + a weapon mod: empty tile in every place a Thing is shown). The def-level
    /// repairs in <see cref="IconFieldFix"/> do not help there, because ThingIcon takes the texture
    /// from the *instance* graphic:
    ///
    ///     Widgets.ThingIcon(Rect, Thing, ...)                                   // Widgets.cs:481
    ///       -> Widgets.GetIconFor(Thing, size, rot, stackOfOne, out ...)        // Widgets.cs:640
    ///            result = thing.Graphic.ExtractInnerGraphicFor(thing).MatAt(rot).mainTexture
    ///       -> if (result == null || result == BadTex) return;                  // Widgets.cs:492
    ///
    /// For a modified weapon that instance material comes out of
    /// Thing.DefaultGraphic -> GraphicData.GraphicColoredFor(this), i.e. it is built per thing from
    /// the def's graphicData at the first draw - and on this port such a material either has no
    /// _MainTex property or hands out a texture that is not resident, so mainTexture is null and the
    /// tile is skipped. The def's uiIcon / graphic are unaffected, which is exactly the symptom.
    ///
    /// Assumptions, because this was written without a game assembly (CI has none) and without a
    /// findings file (none existed under scratch/shared when this landed):
    ///   A1. The two members above keep those names and shapes; both are located by parameter *type
    ///       name* (Rect/Thing, Thing first) and nothing is compiled against the game.
    ///   A2. The failure is the instance material/texture, not the def: a modified weapon's def icon
    ///       is the vanilla icon, which draws.
    ///   A3. "Not resident" is observable as a flag on the texture (isResident/resident/IsResident,
    ///       whatever this port names it) or as width/height == 0. Every lookup may fail: an unknown
    ///       member reads as "assume usable", never as "broken", so an unexpected rename degrades to
    ///       2.5.0 behaviour (null mainTexture is still caught by identity).
    ///
    /// What it does:
    ///   - a prefix on ThingIcon(Rect, Thing, ...) classifies the instance graphic once per Thing
    ///     (reference identity, bounded) and asks <see cref="IconFallback.Choose"/> which source the
    ///     icon should come from;
    ///   - a postfix on GetIconFor(Thing, ...) puts that texture in place of the unusable one and
    ///     clears the material argument, because the material without a usable _MainTex is what made
    ///     the tile blank (GenUI.DrawTextureWithMaterial -> Graphics.DrawTexture through it paints
    ///     nothing; measured for this port's CutoutComplexUI materials in 2.5.0).
    ///
    /// No draw call is added, removed or reordered: ThingIcon still calls GetIconFor and still hands
    /// the result to ThingIconWorker. Only the two values it carries are replaced, and only when they
    /// are unusable. The 2.5.0 repairs are untouched - a def icon this fallback uses is the same
    /// field IconFieldFix repairs at the same point.
    ///
    /// Logging: one line per (defName, source) for the life of the process, exactly
    /// "icon fallback: &lt;defName&gt; via &lt;source&gt;". The "none" case is a diagnostic capped at
    /// a handful of lines, because such a Thing is retried on every draw by design.
    /// </summary>
    internal static class InstanceIconFallback
    {
        internal const string HarmonyId = "sleepsheep.waylith.glyphfix.iconfallback";

        // Bounded caches, same policy as IconFieldFix: at the cap the set is dropped and rebuilt
        // lazily instead of growing (a def list is a few thousand entries at most).
        private const int OkCap = 4096;
        private const int FallCap = 2048;
        private const int LogCap = 256;
        private const int NoneLogCap = 5;

        /// <summary>A Thing whose instance graphic was resolved to a def-level source.</summary>
        private sealed class Entry
        {
            internal IconSource Source = IconSource.None;
            internal object Tex;      // null while unresolved -> retried on the next draw
        }

        private static readonly HashSet<object> _ok = new HashSet<object>(RefCmp.Instance);
        private static readonly Dictionary<object, Entry> _fall = new Dictionary<object, Entry>(RefCmp.Instance);
        private static readonly HashSet<string> _logged = new HashSet<string>();
        private static readonly Dictionary<Type, MethodInfo> _matAt = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<Type, MethodInfo> _hasProp = new Dictionary<Type, MethodInfo>();

        private static MethodInfo _innerFor;
        private static object _badTex;
        private static int _badTexTried;
        private static int _matIndex = -1;      // index of the out Material in GetIconFor
        private static int _hooked;             // >0 once the PlayDataLoader hooks are in
        private static int _warned;
        private static int _slowPath;
        private static int _subs;
        private static int _noneLogs;

        // One draw at a time: the ThingIcon prefix fills these, the GetIconFor postfix of the call it
        // makes consumes them. Keyed by the Thing reference, so a slot left behind by a draw that
        // never reached GetIconFor (Blueprint/Frame early return) can never be applied elsewhere.
        private static object _pendingThing;
        private static object _pendingTex;

        internal static void Install()
        {
            InstallInvalidation();
            try
            {
                Type widgets = Refl.T("Verse.Widgets");
                if (widgets == null) { Refl.Warn("iconfallback: Verse.Widgets not found"); return; }

                MethodInfo pre = typeof(InstanceIconFallback).GetMethod("Prefix",
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo post = typeof(InstanceIconFallback).GetMethod("Postfix",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (pre == null || post == null) { Refl.Warn("iconfallback: patch methods missing"); return; }

                int icons = 0, getters = 0;
                foreach (MethodInfo m in widgets.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length < 2 || ps[0].ParameterType.Name != "Rect") continue;

                    if (m.Name == "ThingIcon" && ps[1].ParameterType.Name == "Thing")
                    {
                        try
                        {
                            new Harmony(HarmonyId).Patch(m, new HarmonyMethod(pre));
                            icons++;
                        }
                        catch (Exception e) { Refl.Warn("iconfallback: could not patch ThingIcon: " + e.Message); }
                    }
                }

                foreach (MethodInfo m in widgets.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length < 3 || m.Name != "GetIconFor" || ps[0].ParameterType.Name != "Thing") continue;

                    int mi = -1;
                    for (int i = 1; i < ps.Length; i++)
                        if (ps[i].ParameterType.Name == "Material") { mi = i; break; }
                    if (mi < 0) continue;

                    try
                    {
                        new Harmony(HarmonyId).Patch(m, postfix: new HarmonyMethod(post));
                        _matIndex = mi;
                        getters++;
                    }
                    catch (Exception e) { Refl.Warn("iconfallback: could not patch GetIconFor: " + e.Message); }
                }

                Refl.Info("iconfallback: patched " + icons + " ThingIcon(Rect,Thing,..) / "
                          + getters + " GetIconFor(Thing,..) (material arg #" + _matIndex + ")");
                if (icons == 0 || getters == 0)
                    Refl.Warn("iconfallback: incomplete install - instance icons keep 2.5.0 behaviour");
            }
            catch (Exception e) { Refl.Warn("iconfallback: install failed: " + e.Message); }
        }

        // ------------------------------------------------------------------ draw path

        /// <summary>
        /// Classify the instance graphic and remember which def-level texture the icon should use.
        /// Cheap once a Thing is known: one hash lookup, or one dictionary lookup plus two field
        /// reads for a Thing that needs the fallback. Always returns true - the original draw runs
        /// either way, this prefix only decides what the postfix will substitute.
        /// </summary>
        [HarmonyPriority(Priority.Low)]   // the other draw fixes get the first word on ThingIcon
        private static bool Prefix(object[] __args)
        {
            ClearPending();
            try
            {
                if (__args == null || __args.Length < 2) return true;
                object thing = __args[1];
                if (thing == null) return true;

                if (_ok.Contains(thing)) return true;          // steady state: instance graphic is fine

                Entry e;
                if (_fall.TryGetValue(thing, out e))
                {
                    if (e.Tex != null) { Pend(thing, e.Tex); return true; }
                    // Source == None from an earlier frame: fall through and look again, the instance
                    // graphic may still be loading. A failure is never cached as resolved.
                }
                else
                {
                    e = new Entry();
                    if (_fall.Count >= FallCap) _fall.Clear();
                    _fall[thing] = e;
                }

                _slowPath++;

                object def = Get(thing, "def");
                if (def == null) return true;

                // The three candidates, each reduced to null unless it can actually be drawn.
                object instanceTex = DrawableOrNull(InstanceMainTexture(thing, def));
                object defIcon = DrawableOrNull(Get(def, "uiIcon"));
                object defGraphicTex = DrawableOrNull(DefGraphicTexture(def));

                IconSource src = IconFallback.Choose(instanceTex, defIcon, defGraphicTex, BadTex());

                if (src == IconSource.Instance)
                {
                    e.Source = src;
                    e.Tex = null;
                    _fall.Remove(thing);
                    Remember(thing);          // nothing to do for this Thing ever again
                    return true;
                }

                if (!IconFallback.Cacheable(src))
                {
                    LogNone(def);             // capped diagnostic; the next draw retries
                    return true;
                }

                object tex = src == IconSource.DefUiIcon ? defIcon : defGraphicTex;
                e.Source = src;
                e.Tex = tex;
                Pend(thing, tex);
                LogOnce(def, src);
            }
            catch (Exception ex) { WarnOnce(ex.Message); }
            return true;
        }

        /// <summary>
        /// Replace an unusable instance texture with the def-level one the prefix picked, and drop
        /// the material argument: a material without a usable _MainTex is what made the tile blank,
        /// so drawing through it again would repaint nothing. The call itself is untouched.
        /// </summary>
        private static void Postfix(ref object __result, object[] __args)
        {
            try
            {
                object thing = _pendingThing;
                object tex = _pendingTex;
                ClearPending();

                if (thing == null || tex == null) return;
                if (__args == null || __args.Length == 0 || !ReferenceEquals(__args[0], thing)) return;

                // The instance came out drawable after all (a texture finished loading between the
                // prefix and here): leave the game's own result alone.
                if (DrawableOrNull(__result) != null) return;

                __result = tex;
                if (_matIndex > 0 && _matIndex < __args.Length) __args[_matIndex] = null;

                int n = Interlocked.Increment(ref _subs);
                if (n == 1 || n % 500 == 0) Refl.Info(IconFallback.Summary(n));
            }
            catch (Exception e) { WarnOnce(e.Message); }
        }

        // ------------------------------------------------------------------ game state, by name

        /// <summary>
        /// mainTexture of the instance graphic - null when the material is missing, when its shader
        /// has no _MainTex, or when the texture that comes back cannot be sampled.
        /// </summary>
        private static object InstanceMainTexture(object thing, object def)
        {
            object graphic = Get(thing, "Graphic");
            if (graphic == null) return null;

            object inner = InnerFor(graphic, thing);
            object mat = MatAt(inner ?? graphic, Get(def, "defaultPlacingRot"));
            if (mat == null) return null;

            bool hasMainTex = HasMainTex(mat);
            object tex = hasMainTex ? Get(mat, "mainTexture") : null;
            return IconFallback.MaterialUsable(true, hasMainTex, Resident(tex)) ? tex : null;
        }

        /// <summary>mainTexture of def.graphic at the def's default placing rotation - the last resort.</summary>
        private static object DefGraphicTexture(object def)
        {
            object graphic = Get(def, "graphic");
            if (graphic == null) return null;
            object mat = MatAt(graphic, Get(def, "defaultPlacingRot"));
            return mat == null ? null : Get(mat, "mainTexture");
        }

        /// <summary>Non-null, not BadTex and resident - or null. Feeds both the pure decision and the guard.</summary>
        private static object DrawableOrNull(object tex)
        {
            if (tex == null) return null;
            return Resident(tex) ? tex : null;
        }

        /// <summary>
        /// Can the renderer sample this texture? An explicit residency flag wins when the port has
        /// one; otherwise a zero dimension means there is nothing to sample. An unknown member reads
        /// as usable, never as broken.
        /// </summary>
        private static bool Resident(object tex)
        {
            if (tex == null) return false;
            object bad = BadTex();
            if (bad != null && ReferenceEquals(tex, bad)) return false;

            object flag = Get(tex, "isResident");
            if (flag == null) flag = Get(tex, "resident");
            if (flag == null) flag = Get(tex, "IsResident");
            bool? known = flag as bool?;
            if (known.HasValue) return known.Value;

            int w = IntOf(Get(tex, "width"));
            int h = IntOf(Get(tex, "height"));
            if (w == 0 || h == 0) return false;
            return true;
        }

        /// <summary>Verse.BaseContent.BadTex, resolved once. Null when this build does not have it.</summary>
        private static object BadTex()
        {
            if (_badTex != null || _badTexTried != 0) return _badTex;
            Interlocked.Exchange(ref _badTexTried, 1);
            try
            {
                Type t = Refl.T("Verse.BaseContent");
                if (t == null) return null;
                FieldInfo f = t.GetField("BadTex", BindingFlags.Public | BindingFlags.Static)
                              ?? t.GetField("BadTex", BindingFlags.NonPublic | BindingFlags.Static);
                if (f != null) _badTex = f.GetValue(null);
            }
            catch { }
            return _badTex;
        }

        /// <summary>Does this material carry a _MainTex at all? Unknown counts as yes.</summary>
        private static bool HasMainTex(object mat)
        {
            if (mat == null) return false;
            try
            {
                Type t = mat.GetType();
                MethodInfo m;
                if (!_hasProp.TryGetValue(t, out m))
                {
                    m = t.GetMethod("HasProperty", new[] { typeof(string) });
                    if (_hasProp.Count >= 64) _hasProp.Clear();
                    _hasProp[t] = m;
                }
                if (m != null) return (bool)m.Invoke(mat, new object[] { "_MainTex" });

                MethodInfo g = t.GetMethod("GetTexture", new[] { typeof(string) });
                if (g != null) return g.Invoke(mat, new object[] { "_MainTex" }) != null;
            }
            catch { }
            return true;
        }

        /// <summary>Graphic.ExtractInnerGraphicFor(thing) - Verse.GraphicUtility, static extension.</summary>
        private static object InnerFor(object graphic, object thing)
        {
            try
            {
                MethodInfo m = _innerFor;
                if (m == null)
                {
                    Type t = Refl.T("Verse.GraphicUtility");
                    if (t == null) return graphic;
                    foreach (MethodInfo c in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        if (c.Name == "ExtractInnerGraphicFor" && c.GetParameters().Length == 3) { m = c; break; }
                    _innerFor = m;
                    if (m == null) return graphic;
                }
                return m.Invoke(null, new object[] { graphic, thing, null }) ?? graphic;
            }
            catch { return graphic; }
        }

        /// <summary>Graphic.MatAt(Rot4[, Thing]) - the rotation-aware material, same call Widgets makes.</summary>
        private static object MatAt(object graphic, object rot)
        {
            if (graphic == null) return null;
            try
            {
                Type t = graphic.GetType();
                MethodInfo m;
                if (!_matAt.TryGetValue(t, out m))
                {
                    int best = int.MaxValue;
                    foreach (MethodInfo c in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                          BindingFlags.Instance))
                    {
                        if (c.Name != "MatAt") continue;
                        ParameterInfo[] ps = c.GetParameters();
                        if (ps.Length == 0 || ps.Length > 2) continue;
                        if (ps[0].ParameterType.Name != "Rot4") continue;
                        if (ps.Length < best) { best = ps.Length; m = c; }
                    }
                    if (_matAt.Count >= 256) _matAt.Clear();
                    _matAt[t] = m;
                }
                if (m == null) return null;
                return m.GetParameters().Length == 1
                    ? m.Invoke(graphic, new object[] { rot })
                    : m.Invoke(graphic, new object[] { rot, null });   // Widgets passes no Thing either
            }
            catch { return null; }
        }

        /// <summary>Property or field by name, walking the base chain. Null on any failure.</summary>
        private static object Get(object o, string name)
        {
            if (o == null) return null;
            try
            {
                for (Type c = o.GetType(); c != null; c = c.BaseType)
                {
                    PropertyInfo p = c.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic |
                                                         BindingFlags.Instance);
                    if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
                    FieldInfo f = c.GetField(name, BindingFlags.Public | BindingFlags.NonPublic |
                                                   BindingFlags.Instance);
                    if (f != null) return f.GetValue(o);
                }
            }
            catch { }
            return null;
        }

        private static int IntOf(object v) { return v is int i ? i : -1; }

        // ------------------------------------------------------------------ bookkeeping

        private static void Pend(object thing, object tex)
        {
            _pendingThing = thing;
            _pendingTex = tex;
        }

        private static void ClearPending()
        {
            _pendingThing = null;
            _pendingTex = null;
        }

        private static void Remember(object thing)
        {
            if (_ok.Count >= OkCap) _ok.Clear();
            _ok.Add(thing);
        }

        /// <summary>One line per (defName, source), ever. Never called for an unresolved Thing.</summary>
        private static void LogOnce(object def, IconSource src)
        {
            if (_logged.Count >= LogCap) return;
            string line = IconFallback.Line(Get(def, "defName") as string, src);
            if (!_logged.Add(line)) return;
            Refl.Info(line);
        }

        /// <summary>The "nothing is drawable" diagnostic: a handful of lines, never one per frame.</summary>
        private static void LogNone(object def)
        {
            if (Interlocked.Increment(ref _noneLogs) > NoneLogCap) return;
            Refl.Info(IconFallback.Line(Get(def, "defName") as string, IconSource.None));
        }

        private static void WarnOnce(string m)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0) Refl.Warn("iconfallback: " + m);
        }

        // ------------------------------------------------------------------ invalidation

        /// <summary>
        /// A def reload (language switch, dev-mode hot reload) rebuilds the defs and their uiIcon;
        /// every texture this cache holds belongs to the previous generation. Hooked by method name
        /// on Verse.PlayDataLoader, same two entry points IconFieldFix uses, own Harmony id so the two
        /// caches are independent.
        /// </summary>
        internal static void InstallInvalidation()
        {
            if (_hooked > 0) return;
            try
            {
                Type t = Refl.T("Verse.PlayDataLoader");
                if (t == null) { Refl.Warn("iconfallback: Verse.PlayDataLoader not found, no invalidation"); return; }

                MethodInfo postfix = typeof(InstanceIconFallback).GetMethod("PostfixDefsReloaded",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (postfix == null) return;

                int n = 0;
                foreach (string name in new[] { "ClearAllPlayData", "HotReloadDefs" })
                {
                    MethodInfo m = Refl.M(t, name, 0) ?? Refl.M(t, name);
                    if (m == null) continue;
                    try
                    {
                        new Harmony(HarmonyId).Patch(m, postfix: new HarmonyMethod(postfix));
                        n++;
                    }
                    catch (Exception e) { Refl.Warn("iconfallback: could not hook " + name + ": " + e.Message); }
                }
                _hooked = n;
                Refl.Info("iconfallback: cache invalidation on " + n + " PlayDataLoader method(s)");
            }
            catch (Exception e) { Refl.Warn("iconfallback: invalidation install failed: " + e.Message); }
        }

        private static void PostfixDefsReloaded()
        {
            try
            {
                _ok.Clear();
                _fall.Clear();
                _logged.Clear();
                ClearPending();
                Refl.Info("iconfallback: cache cleared on def reload");
            }
            catch { }
        }

        /// <summary>Reference identity: game types may override Equals/GetHashCode.</summary>
        private sealed class RefCmp : IEqualityComparer<object>
        {
            internal static readonly RefCmp Instance = new RefCmp();
            bool IEqualityComparer<object>.Equals(object a, object b) { return ReferenceEquals(a, b); }
            int IEqualityComparer<object>.GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); }
        }
    }
}
