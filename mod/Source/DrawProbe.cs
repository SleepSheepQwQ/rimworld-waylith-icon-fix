using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// One-shot diagnostic for the empty equipment box (the weapon attack gizmo beside Draft).
    /// Records, for small icons only: texture identity/state, GPU residency, material + mask,
    /// GUI.color, rect/scale, and the outcome of three direct draw attempts. Never changes behaviour
    /// beyond drawing into the same rect, and stops logging after MaxEntries.
    /// </summary>
    internal static class DrawProbe
    {
        private const int MaxEntries = 60;
        private static readonly object _lock = new object();
        private static readonly HashSet<string> _seen = new HashSet<string>();
        private static int _entries;
        private static StreamWriter _w;
        private static bool _broken;

        private static MethodInfo _ensureResident, _drawTex, _drawTexCoords;
        private static object _unitRect, _stretchMode;

        internal static void Init()
        {
            try
            {
                Type tex2d = FindType("Waylith.Rendering.Texture2D");
                if (tex2d != null)
                    _ensureResident = tex2d.GetMethod("EnsureResident", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

                Type gui = FindType("UnityEngine.GUI");
                Type rectT = FindType("UnityEngine.Rect");
                if (gui != null && rectT != null)
                {
                    _unitRect = Activator.CreateInstance(rectT, new object[] { 0f, 0f, 1f, 1f });
                    foreach (var m in gui.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        var ps = m.GetParameters();
                        if (m.Name == "DrawTexture" && ps.Length == 3 && ps[1].ParameterType.Name == "Texture") _drawTex = m;
                        if (m.Name == "DrawTextureWithTexCoords" && ps.Length == 3 && ps[2].ParameterType == rectT) _drawTexCoords = m;
                    }
                }

                var h = new Harmony("sleepsheep.waylith.agenttouchfix.drawprobe2");
                Type widgets = FindType("Verse.Widgets");
                MethodInfo pre = typeof(DrawProbe).GetMethod("ThingIconPre", BindingFlags.Static | BindingFlags.NonPublic);
                int n = 0;
                foreach (var m in widgets.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "ThingIcon") continue;
                    var ps = m.GetParameters();
                    if (ps.Length < 2 || ps[1].ParameterType.Name != "Thing") continue;
                    h.Patch(m, new HarmonyMethod(pre)); n++;
                }
                // record every texture resolve attempt at the backend level
                Type backend = FindType("RimWorldWaylith.Imgui.ImGuiGuiBackend");
                MethodInfo tr = backend?.GetMethod("TryResolveTexture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (tr != null)
                    h.Patch(tr, postfix: new HarmonyMethod(typeof(DrawProbe).GetMethod("ResolvePost", BindingFlags.Static | BindingFlags.NonPublic)));
                Refl.Info("drawprobe2: installed (" + n + " ThingIcon overloads, resolve=" + (tr != null) + ")");
            }
            catch (Exception e) { Refl.Warn("drawprobe2: install failed: " + e.Message); _broken = true; }
        }

        private static void Line(string s)
        {
            lock (_lock)
            {
                try
                {
                    if (_w == null)
                    {
                        string dir = Path.Combine(GenPath(), "");
                        Directory.CreateDirectory(dir);
                        _w = new StreamWriter(Path.Combine(dir, "drawprobe.log"), true) { AutoFlush = true };
                    }
                    _w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + s);
                }
                catch { _broken = true; }
            }
        }

        private static string GenPath()
        {
            try
            {
                Type gfp = FindType("Verse.GenFilePaths");
                object folder = Get(gfp, "SaveDataFolderPath") ?? Get(gfp, "FolderUnderSaveData");
                if (folder is string s && s.Length > 0) return s;
            }
            catch { }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe2");
        }

        private static void ResolvePost(object __instance, object image, object __result)
        {
            if (_entries >= MaxEntries) return;
            try
            {
                bool ok = __result is bool b && b;
                _entries++;
                Line("[RESOLVE] ok=" + ok + " tex=" + Name(image));
            }
            catch { }
        }

        private static bool ThingIconPre(object[] __args)
        {
            if (_broken || _entries >= MaxEntries) return true;
            try
            {
                object rect = __args[0], thing = __args[1];
                object h = Get(rect, "height");
                float hh = h is float f ? f : (h is int i ? i : 0f);
                if (hh <= 0f || hh > 64f) return true;               // only the small equipment box

                object graphic = Get(thing, "Graphic");
                object mat = graphic == null ? null : (Get(graphic, "MatSingle") ?? Get(graphic, "MatSouth"));
                object tex = mat == null ? null : Get(mat, "mainTexture");
                string key = Name(thing) + "|" + Name(tex) + "|" + (int)hh;
                lock (_lock) { if (_seen.Contains(key)) return true; _seen.Add(key); _entries++; }

                bool? resident = null;
                try
                {
                    if (_ensureResident != null && tex != null)
                    {
                        object r = _ensureResident.IsStatic ? _ensureResident.Invoke(null, new[] { tex })
                                                           : _ensureResident.Invoke(tex, null);
                        resident = r is bool rb ? rb : (bool?)null;
                    }
                }
                catch (Exception e) { Line("[ENSURE] threw " + e.GetType().Name + ": " + e.Message); }

                Line("[ICON] thing=" + Name(thing) + " rectH=" + hh + " scale=" + __args[__args.Length - 2]
                     + " tex=" + Name(tex)
                     + " texW=" + Get(tex, "width") + "x" + Get(tex, "height")
                     + " mip=" + Get(tex, "mipmapCount")
                     + " fmt=" + Get(tex, "format")
                     + " filter=" + Get(tex, "filterMode")
                     + " resident=" + (resident.HasValue ? resident.ToString() : "?")
                     + " mat=" + Name(mat) + " shader=" + Name(Get(mat, "shader"))
                     + " mask=" + Name(Get(mat, "maskTex"))
                     + " guiColor=" + Get(FindType("UnityEngine.GUI"), "color"));

                string a = TryDraw("DrawTexture", _drawTex, new object[] { rect, tex, EnumOrNull() });
                string b2 = TryDraw("DrawTexCoords", _drawTexCoords, new object[] { rect, tex, _unitRect });
                Line("[DRAW] " + a + " | " + b2);
                return true;                                          // let the engine draw as usual
            }
            catch (Exception e)
            {
                if (_entries < 3) Line("[ICON] probe failed: " + e.GetType().Name + " " + e.Message);
                return true;
            }
        }

        private static object EnumOrNull()
        {
            try
            {
                Type sm = FindType("UnityEngine.ScaleMode");
                return sm == null ? null : Enum.Parse(sm, "StretchToFill");
            }
            catch { return null; }
        }

        private static string TryDraw(string tag, MethodInfo m, object[] args)
        {
            if (m == null) return tag + "=no-method";
            try { m.Invoke(null, args); return tag + "=ok"; }
            catch (Exception e) { return tag + "=throw:" + e.GetType().Name; }
        }

        private static string Name(object o)
        {
            if (o == null) return "null";
            foreach (var n in new[] { "name", "defName", "label" })
            {
                object v = Get(o, n);
                if (v is string s && s.Length > 0) return s;
            }
            return o.GetType().Name;
        }

        private static Type FindType(string name)
        {
            Type t = Type.GetType(name);
            if (t != null) return t;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = a.GetType(name); if (t != null) return t; } catch { }
            }
            return null;
        }

        private static object Get(object o, string name)
        {
            if (o == null) return null;
            for (Type c = o.GetType(); c != null; c = c.BaseType)
            {
                PropertyInfo pi = c.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (pi != null) { try { return pi.GetValue(o, null); } catch { } }
                FieldInfo fi = c.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (fi != null) { try { return fi.GetValue(o); } catch { } }
            }
            return null;
        }
    }
}
