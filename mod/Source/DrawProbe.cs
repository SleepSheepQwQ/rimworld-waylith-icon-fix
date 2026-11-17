using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// Evidence-first diagnostic probe for the empty equipment-bullet gizmo.
    ///
    /// Design rules, learned the hard way:
    ///  * It never assumes a game member exists. Every field/property/method is looked up by
    ///    reflection, null-checked, and a MISS line is written when the lookup fails.
    ///  * It never assumes a draw hook exists either: all ThingIcon overloads, all Gizmo.draw-ish
    ///    overrides, every *ResolveTexture-ish method and one per-frame method are patched; each patch
    ///    target, each failure and each miss is logged.
    ///  * The log path is resolved by probing candidates for writability, and line 1 states the
    ///    absolute path plus the exact "run-as <pkg> cat <path>" command, so the file can always be
    ///    found again from an adb/termux shell.
    ///  * Any exception anywhere is written with its full stack (Exception.ToString()) and never
    ///    swallowed silently into a bool.
    /// </summary>
    internal static class DrawProbe
    {
        internal const string HarmonyId = "sleepsheep.waylith.agenttouchfix.probe2";

        private const string LogName = "drawprobe.log";
        private const long MaxBytes = 4L * 1024L * 1024L;
        private const int MaxDetailPerHook = 40;
        private const int MaxPatches = 60;
        private const int HeartbeatEvery = 300;
        private const string LegacyGameDir = "/data/data/com.ports1de.waylith/files/game/";

        // ---------------------------------------------------------------- state

        private static readonly object _lk = new object();      // log sink only
        private static readonly object _cnt = new object();     // counters only (never held while logging)
        private static readonly DateTime _t0 = DateTime.Now;

        private static StreamWriter _w;
        private static string _path = "(unresolved)";
        private static long _bytes;
        private static bool _capped;

        private static Harmony _h;
        private static List<Type> _types;

        private static int _iconCalls, _gizmoCalls, _frames, _resolveCalls, _errs;

        private static readonly Dictionary<string, int> _hookHits = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _detail = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _rects = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _buckets = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _things = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _textures = new Dictionary<string, int>();
        private static readonly HashSet<string> _misses = new HashSet<string>();

        // resolved draw api (all optional, all logged)
        private static Type _guiType, _rectType, _scaleType;
        private static MethodInfo _mDrawTex, _mDrawTexCoords, _mDrawTexMat;
        private static object _scaleMode, _unitRect;

        // ---------------------------------------------------------------- build marker / identity

        internal static string LogPath { get { return _path; } }

        internal static string BuildTag
        {
            get
            {
                string stamp = "unknown";
                try
                {
                    object[] attrs = typeof(DrawProbe).Assembly.GetCustomAttributes(
                        typeof(AssemblyMetadataAttribute), false);
                    for (int i = 0; i < attrs.Length; i++)
                    {
                        AssemblyMetadataAttribute a = attrs[i] as AssemblyMetadataAttribute;
                        if (a != null && a.Key == "BuildStamp" && !string.IsNullOrEmpty(a.Value)) stamp = a.Value;
                    }
                }
                catch { }
                return Version3() + "-" + stamp;
            }
        }

        private static string Version3()
        {
            try
            {
                Version v = typeof(DrawProbe).Assembly.GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
            catch { return "0.0.0"; }
        }

        private static string Pid()
        {
            try { return Environment.ProcessId.ToString(CultureInfo.InvariantCulture); }
            catch { return "?"; }
        }

        private static string ExePath()
        {
            try { return Environment.ProcessPath ?? "?"; }
            catch { return "?"; }
        }

        /// <summary>First argv entry of this process - that is the package name to hand to run-as.</summary>
        private static string ProcName()
        {
            try
            {
                string raw = File.ReadAllText("/proc/self/cmdline");
                int z = raw.IndexOf('\0');
                if (z >= 0) raw = raw.Substring(0, z);
                raw = raw.Trim();
                if (raw.Length > 0) return raw;
            }
            catch (Exception e) { return "unreadable(" + e.GetType().Name + ")"; }
            return "?";
        }

        private static string PkgGuess()
        {
            string p = ProcName();
            if (p.IndexOf('/') >= 0) p = p.Substring(p.LastIndexOf('/') + 1);
            if (p.Length == 0 || p == "?") return "<package>";
            return p;
        }

        private static string AsmPath()
        {
            try { return typeof(DrawProbe).Assembly.Location ?? "?"; }
            catch { return "?"; }
        }

        // ---------------------------------------------------------------- log sink

        /// <summary>Resolve the log path and write the build marker. Safe to call more than once.</summary>
        internal static void Start()
        {
            lock (_lk)
            {
                if (_w == null) OpenLocked();
            }
        }

        internal static void Note(string s)
        {
            if (s == null) s = "(null)";
            lock (_lk)
            {
                try
                {
                    if (_capped) return;
                    if (_w == null) OpenLocked();
                    if (_w == null) return;
                    RawLocked(s);
                    if (_bytes >= MaxBytes)
                    {
                        RawLocked("CAP size limit " + MaxBytes + " reached, detail lines dropped from here");
                        _capped = true;
                        try { _w.Flush(); _w.Dispose(); } catch { }
                        _w = null;
                    }
                }
                catch { }
            }
        }

        /// <summary>Log to the probe file and leave a breadcrumb in the mod's long-lived log.</summary>
        internal static void NoteBoth(string s)
        {
            Note(s);
            try { Refl.Info("probe: " + s); } catch { }
        }

        /// <summary>Header line: written verbatim, so the first line of the file is exactly "BUILD ...".</summary>
        /// <summary>Log a message only the first time this key is seen (keeps per-frame paths bounded).</summary>
        private static void NoteOnce(string key, string msg)
        {
            bool first = false;
            lock (_cnt)
            {
                if (!_misses.Contains(key)) { _misses.Add(key); first = true; }
            }
            if (first) Note(msg);
        }

        private static void RawHeadLocked(string s)
        {
            _w.WriteLine(s);
            _w.Flush();
            _bytes += s.Length + 2;
        }

        private static void RawLocked(string s)
        {
            string line = "[" + (DateTime.Now - _t0).TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)
                          + "s t" + Thread.CurrentThread.ManagedThreadId + "] " + s;
            _w.WriteLine(line);
            _w.Flush();
            _bytes += line.Length + 2;
        }

        private static List<string> Candidates()
        {
            List<string> list = new List<string>();
            try
            {
                string sf = SaveFolder();
                if (!string.IsNullOrEmpty(sf)) list.Add(Path.Combine(sf, LogName));
            }
            catch { }
            try
            {
                string bd = AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrEmpty(bd)) list.Add(Path.Combine(bd, LogName));
            }
            catch { }
            try { list.Add(Path.Combine(LegacyGameDir, LogName)); } catch { }
            try { list.Add(Path.Combine(Path.GetTempPath(), LogName)); } catch { }
            return list;
        }

        private static void OpenLocked()
        {
            List<string> cands = Candidates();
            string[] status = new string[cands.Count];
            for (int i = 0; i < cands.Count; i++)
            {
                if (_w != null) break;
                string cand = cands[i];
                try
                {
                    string dir = Path.GetDirectoryName(cand);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    FileStream fs = new FileStream(cand, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    long len = fs.Length;
                    bool okWrite = false;
                    try { fs.WriteByte(0x0A); fs.Flush(); okWrite = true; }
                    catch { }
                    try { fs.SetLength(len); fs.Seek(len, SeekOrigin.Begin); }
                    catch { }
                    if (!okWrite)
                    {
                        try { fs.Dispose(); } catch { }
                        status[i] = "FAILED (write test)";
                        continue;
                    }
                    StreamWriter sw = new StreamWriter(fs, new UTF8Encoding(false));
                    sw.AutoFlush = true;
                    _w = sw;
                    _path = cand;
                    _bytes = len;
                    status[i] = "USED";
                }
                catch (Exception e)
                {
                    status[i] = "FAILED " + e.GetType().Name + ": " + e.Message;
                }
            }

            if (_w == null)
            {
                _path = "(no writable log path)";
                return;
            }

            // No timestamp prefix on this line: it must start with "BUILD " so it can be grepped for.
            RawHeadLocked("BUILD " + BuildTag + " log=" + _path
                          + " runtime=" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
            RawLocked("PATH abs=" + _path + " bytes-after-header=" + _bytes + " pid=" + Pid()
                      + " proc=" + ProcName() + " exe=" + ExePath() + " dll=" + AsmPath());
            RawLocked("PATH read-with=run-as " + PkgGuess() + " cat " + _path);
            Type gfp = FindTypeBySimpleName("GenFilePaths");
            RawLocked("PATH GenFilePaths=" + (gfp == null ? "NOT FOUND" : gfp.FullName)
                      + " saveFolder=" + (SaveFolder() ?? "null"));
            for (int k = 0; k < cands.Count; k++)
                RawLocked("PATH candidate[" + k + "] " + (status[k] ?? "UNTRIED") + " :: " + cands[k]);
        }

        /// <summary>Save folder through GenFilePaths, by whatever member name this port actually has.</summary>
        private static string SaveFolder()
        {
            Type gfp = FindTypeBySimpleName("GenFilePaths");
            if (gfp == null) return null;
            string[] names = { "SaveDataFolderPath", "SaveFolderPath", "SaveDataFolder", "FolderUnderSaveData" };
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    PropertyInfo p = gfp.GetProperty(names[i],
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (p == null) continue;
                    string s = p.GetValue(null, null) as string;
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                catch { }
            }
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    FieldInfo f = gfp.GetField(names[i],
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (f == null) continue;
                    string s = f.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                catch { }
            }
            try
            {
                MethodInfo m = gfp.GetMethod("FolderUnderSaveData",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null,
                    new Type[] { typeof(string) }, null);
                if (m != null)
                {
                    string s = m.Invoke(null, new object[] { "AgentTouchFix" }) as string;
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch { }
            return null;
        }

        // ---------------------------------------------------------------- type plumbing

        private static List<Type> Types()
        {
            lock (_cnt)
            {
                if (_types == null) _types = ScanTypes();
                return _types;
            }
        }

        private static void RefreshTypes()
        {
            List<Type> fresh = ScanTypes();
            lock (_cnt) { _types = fresh; }
        }

        private static List<Type> ScanTypes()
        {
            List<Type> list = new List<Type>();
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                Type[] ts = null;
                try { ts = asms[i].GetTypes(); }
                catch (ReflectionTypeLoadException e) { ts = e.Types; }
                catch { }
                if (ts == null) continue;
                for (int j = 0; j < ts.Length; j++) if (ts[j] != null) list.Add(ts[j]);
            }
            return list;
        }

        private static Type FindTypeBySimpleName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            List<Type> ts = Types();
            for (int i = 0; i < ts.Count; i++) if (ts[i].Name == name) return ts[i];
            string dotted = "." + name;
            for (int i = 0; i < ts.Count; i++)
            {
                string f = ts[i].FullName;
                if (f != null && f.EndsWith(dotted, StringComparison.Ordinal)) return ts[i];
            }
            return null;
        }

        private static List<Type> FindTypesBySimpleName(string name)
        {
            List<Type> found = new List<Type>();
            List<Type> ts = Types();
            for (int i = 0; i < ts.Count; i++) if (ts[i].Name == name) found.Add(ts[i]);
            return found;
        }

        private static List<MethodInfo> MethodsNamed(string[] names)
        {
            List<MethodInfo> found = new List<MethodInfo>();
            List<Type> ts = Types();
            for (int i = 0; i < ts.Count; i++)
            {
                MethodInfo[] ms;
                try
                {
                    ms = ts[i].GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Static | BindingFlags.Instance |
                                          BindingFlags.DeclaredOnly);
                }
                catch { continue; }
                for (int j = 0; j < ms.Length; j++)
                {
                    for (int k = 0; k < names.Length; k++)
                    {
                        if (ms[j].Name != names[k]) continue;
                        if (ms[j].IsAbstract) break;
                        if (ms[j].ContainsGenericParameters) break;
                        found.Add(ms[j]);
                        break;
                    }
                }
            }
            return found;
        }

        private static ParameterInfo[] SafeParams(MethodInfo m)
        {
            if (m == null) return new ParameterInfo[0];
            try { return m.GetParameters(); }
            catch { return new ParameterInfo[0]; }
        }

        private static string Sig(MethodInfo m)
        {
            if (m == null) return "(no overload)";
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(m.DeclaringType == null ? "?" : m.DeclaringType.FullName);
                sb.Append('.').Append(m.Name).Append('(');
                ParameterInfo[] ps = m.GetParameters();
                for (int i = 0; i < ps.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(ps[i].ParameterType.Name).Append(' ').Append(ps[i].Name);
                }
                sb.Append(") : ").Append(m.ReturnType.Name);
                return sb.ToString();
            }
            catch (Exception e) { return "(signature threw " + e.GetType().Name + ")"; }
        }

        // ---------------------------------------------------------------- member access (never blind)

        private static void Miss(Type t, string member)
        {
            if (t == null) return;
            string key = t.FullName + "#" + member;
            bool first = false;
            lock (_cnt)
            {
                if (!_misses.Contains(key)) { _misses.Add(key); first = true; }
            }
            if (first) Note("MISS " + t.Name + "." + member + " (no such member on " + t.FullName + ")");
        }

        private static object Get(object o, string name)
        {
            if (o == null || string.IsNullOrEmpty(name)) return null;
            for (Type c = o.GetType(); c != null; c = c.BaseType)
            {
                try
                {
                    PropertyInfo p = c.GetProperty(name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                        BindingFlags.Static | BindingFlags.DeclaredOnly);
                    if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
                }
                catch { }
                try
                {
                    FieldInfo f = c.GetField(name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                        BindingFlags.Static | BindingFlags.DeclaredOnly);
                    if (f != null) return f.GetValue(o);
                }
                catch { }
            }
            Miss(o.GetType(), name);
            return null;
        }

        private static object StaticGet(Type t, string name)
        {
            if (t == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(null, null);
            }
            catch { }
            try
            {
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f != null) return f.GetValue(null);
            }
            catch { }
            Miss(t, name);
            return null;
        }

        private static string TypeName(object o)
        {
            if (o == null) return "null";
            try { return o.GetType().FullName ?? o.GetType().Name; }
            catch { return "?"; }
        }

        private static string Describe(object v)
        {
            if (v == null) return "null";
            try
            {
                string s = v as string;
                if (s == null) s = v.ToString();
                if (s == null) s = "";
                if (s.Length > 160) s = s.Substring(0, 160) + "...";
                string tn;
                try { tn = v.GetType().Name; } catch { tn = "?"; }
                return tn + "(" + s + ")";
            }
            catch (Exception e) { return "(ToString threw " + e.GetType().Name + ")"; }
        }

        private static string Name(object o)
        {
            if (o == null) return "null";
            string[] keys = { "name", "defName", "label", "path", "texPath" };
            for (int i = 0; i < keys.Length; i++)
            {
                string s = Get(o, keys[i]) as string;
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return "<" + TypeName(o) + ">";
        }

        private static string V(object o, string member)
        {
            if (o == null) return "n/a";
            object v = Get(o, member);
            return v == null ? "null" : Describe(v);
        }

        private static float Num(object v)
        {
            if (v == null) return float.NaN;
            try
            {
                if (v is float) return (float)v;
                if (v is int) return (float)(int)v;
                if (v is double) return (float)(double)v;
                if (v is long) return (float)(long)v;
                if (v is short) return (float)(short)v;
                if (v is byte) return (float)(byte)v;
                if (v is decimal) return (float)(decimal)v;
            }
            catch { }
            return float.NaN;
        }

        private static string F(float f)
        {
            return ((int)Math.Round(f)).ToString(CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- init

        internal static void Init()
        {
            NoteBoth("INIT-BEGIN probe init (tag " + BuildTag + ")");
            try
            {
                RefreshTypes();
                DumpEnvironment();
                DumpShaderDatabase();
                ResolveDrawApi();
                InstallHooks();
                Note("INIT-END status=ok log=" + _path);
                NoteBoth("INIT-END ok=1 tag=" + BuildTag);
            }
            catch (Exception e)
            {
                Note("INIT-END status=FAILED " + e.ToString());
                NoteBoth("INIT-END ok=0 " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void DumpEnvironment()
        {
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            Note("ENV os=" + SafeOs() + " clr=" + SafeClr() + " baseDir=" + AppDomain.CurrentDomain.BaseDirectory);
            Note("ENV assemblies=" + asms.Length + " types=" + Types().Count);
            for (int i = 0; i < asms.Length; i++)
            {
                string loc = "?";
                int n = -1;
                try { loc = asms[i].Location; } catch { }
                try { n = asms[i].GetTypes().Length; } catch { }
                Note("ENV asm[" + i + "] " + asms[i].GetName().Name + " v" + asms[i].GetName().Version
                     + " types=" + n + " loc=" + loc);
            }

            // one-shot map of everything that could be involved in drawing an icon
            string[] keys = { "Gizmo", "Icon", "Texture", "Shader", "Gui", "Command", "Graphic", "Material", "DrawProbe" };
            List<string> hits = new List<string>();
            List<Type> ts = Types();
            for (int i = 0; i < ts.Count; i++)
            {
                string n = ts[i].FullName ?? ts[i].Name;
                if (n == null) continue;
                for (int k = 0; k < keys.Length; k++)
                {
                    if (n.IndexOf(keys[k], StringComparison.OrdinalIgnoreCase) >= 0) { hits.Add(n); break; }
                }
            }
            Note("ENV typemap matched=" + hits.Count + " (listing first 300)");
            for (int i = 0; i < hits.Count && i < 300; i++) Note("ENV type " + hits[i]);
        }

        private static string SafeOs()
        {
            try { return Environment.OSVersion.ToString(); } catch { return "?"; }
        }

        private static string SafeClr()
        {
            try { return Environment.Version.ToString(); } catch { return "?"; }
        }

        /// <summary>
        /// Print every member of every ShaderDatabase-like type. No member name is assumed anywhere:
        /// the point is to find out what this port actually has instead of guessing a shader name.
        /// </summary>
        private static void DumpShaderDatabase()
        {
            try
            {
                List<Type> found = FindTypesBySimpleName("ShaderDatabase");
                if (found.Count == 0)
                {
                    List<string> shaderish = new List<string>();
                    List<Type> ts = Types();
                    for (int i = 0; i < ts.Count && shaderish.Count < 60; i++)
                    {
                        string n = ts[i].FullName ?? "";
                        if (n.IndexOf("Shader", StringComparison.OrdinalIgnoreCase) >= 0) shaderish.Add(n);
                    }
                    Note("SHADERDB NOT FOUND among " + ts.Count + " types in "
                         + AppDomain.CurrentDomain.GetAssemblies().Length + " assemblies");
                    Note("SHADERDB shader-ish types=" + shaderish.Count + " " + string.Join(" ; ", shaderish.ToArray()));
                    return;
                }

                for (int i = 0; i < found.Count; i++)
                {
                    Type t = found[i];
                    Note("SHADERDB type=" + t.FullName + " asm=" + t.Assembly.GetName().Name
                         + " abstract=" + t.IsAbstract + " static=" + (t.IsAbstract && t.IsSealed));
                    List<string> grayish = new List<string>();

                    FieldInfo[] fs = null;
                    try
                    {
                        fs = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                                         BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    }
                    catch (Exception e) { Note("SHADERDB   fields threw " + e.GetType().Name + ": " + e.Message); }
                    int nf = fs == null ? 0 : fs.Length;
                    Note("SHADERDB   FieldCount=" + nf);
                    for (int k = 0; k < nf; k++)
                    {
                        FieldInfo f = fs[k];
                        string val;
                        if (f.IsStatic)
                        {
                            try { val = Describe(f.GetValue(null)); }
                            catch (Exception e) { val = "(get threw " + e.GetType().Name + ")"; }
                        }
                        else val = "(instance field, not read)";
                        Note("SHADERDB   field " + (f.IsStatic ? "static " : "instance ") + f.FieldType.Name
                             + " " + f.Name + " = " + val);
                        if (f.Name.IndexOf("gray", StringComparison.OrdinalIgnoreCase) >= 0
                            || f.Name.IndexOf("grey", StringComparison.OrdinalIgnoreCase) >= 0)
                            grayish.Add(f.Name + "=" + val);
                    }

                    PropertyInfo[] ps = null;
                    try
                    {
                        ps = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                                             BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    }
                    catch (Exception e) { Note("SHADERDB   props threw " + e.GetType().Name + ": " + e.Message); }
                    int np = ps == null ? 0 : ps.Length;
                    Note("SHADERDB   PropertyCount=" + np);
                    for (int k = 0; k < np; k++)
                    {
                        PropertyInfo p = ps[k];
                        string val = "(not read)";
                        MethodInfo getter = p.GetGetMethod(true);
                        if (p.GetIndexParameters().Length == 0 && getter != null)
                        {
                            if (getter.IsStatic)
                            {
                                try { val = Describe(p.GetValue(null, null)); }
                                catch (Exception e) { val = "(get threw " + e.GetType().Name + ")"; }
                            }
                            else val = "(instance property, not read)";
                        }
                        Note("SHADERDB   prop " + p.PropertyType.Name + " " + p.Name + " = " + val);
                    }

                    MethodInfo[] ms = null;
                    try
                    {
                        ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                                          BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    }
                    catch (Exception e) { Note("SHADERDB   methods threw " + e.GetType().Name + ": " + e.Message); }
                    int nm = ms == null ? 0 : ms.Length;
                    Note("SHADERDB   MethodCount=" + nm);
                    for (int k = 0; k < nm; k++) Note("SHADERDB   method " + Sig(ms[k]));

                    Note("SHADERDB grayscale-like members=" + (grayish.Count == 0
                        ? "(none present in this build)" : string.Join(" ; ", grayish.ToArray())));
                }
            }
            catch (Exception e)
            {
                Note("SHADERDB dump FAILED, full stack follows:\n" + e.ToString());
            }
        }

        // ---------------------------------------------------------------- draw api resolution

        private static void ResolveDrawApi()
        {
            _guiType = FindTypeBySimpleName("GUI");
            _rectType = FindTypeBySimpleName("Rect");
            _scaleType = FindTypeBySimpleName("ScaleMode");
            Note("DRAWAPI GUI=" + (_guiType == null ? "NOT FOUND" : _guiType.FullName)
                 + " Rect=" + (_rectType == null ? "NOT FOUND" : _rectType.FullName)
                 + " ScaleMode=" + (_scaleType == null ? "NOT FOUND" : _scaleType.FullName));

            string[] wanted = { "DrawTexture", "DrawTextureWithTexCoords" };
            List<MethodInfo> all = MethodsNamed(wanted);
            Note("DRAWAPI candidate methods=" + all.Count + " (listing first 60)");
            for (int i = 0; i < all.Count && i < 60; i++) Note("DRAWAPI   " + Sig(all[i]));

            if (_scaleType != null)
            {
                try { _scaleMode = Enum.Parse(_scaleType, "StretchToFill"); }
                catch (Exception e) { Note("DRAWAPI ScaleMode.StretchToFill unavailable: " + e.GetType().Name + ": " + e.Message); }
            }
            if (_rectType != null) _unitRect = MakeRect(_rectType, 0f, 0f, 1f, 1f);
            else Note("DRAWAPI no Rect type: TexCoords attempt will be skipped");

            // pass 1: exact 3-parameter forms
            for (int i = 0; i < all.Count; i++)
            {
                MethodInfo m = all[i];
                ParameterInfo[] ps = SafeParams(m);
                if (m.Name == "DrawTexture")
                {
                    if (ps.Length == 3 && NameIs(ps[0], "Rect") && NameIs(ps[1], "Texture")
                        && _scaleMode != null && _mDrawTex == null) _mDrawTex = m;
                    if (NameIs(ps[0], "Rect") && HasMaterial(ps) && _mDrawTexMat == null) _mDrawTexMat = m;
                }
                else if (m.Name == "DrawTextureWithTexCoords" && ps.Length == 3
                         && NameIs(ps[0], "Rect") && NameIs(ps[1], "Texture") && _mDrawTexCoords == null)
                {
                    _mDrawTexCoords = m;
                }
            }
            // pass 2: 2-parameter DrawTexture as a fallback
            if (_mDrawTex == null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    MethodInfo m = all[i];
                    ParameterInfo[] ps = SafeParams(m);
                    if (m.Name == "DrawTexture" && ps.Length == 2 && NameIs(ps[0], "Rect") && NameIs(ps[1], "Texture"))
                    {
                        _mDrawTex = m;
                        break;
                    }
                }
            }

            Note("DRAWAPI chosen DrawTexture=" + Sig(_mDrawTex));
            Note("DRAWAPI chosen DrawTextureWithTexCoords=" + Sig(_mDrawTexCoords));
            Note("DRAWAPI chosen material-draw=" + Sig(_mDrawTexMat));
            Note("DRAWAPI scaleMode=" + Describe(_scaleMode) + " unitRect=" + Describe(_unitRect));
        }

        private static object MakeRect(Type rectT, float x, float y, float w, float h)
        {
            try
            {
                ConstructorInfo ci = rectT.GetConstructor(new Type[] { typeof(float), typeof(float), typeof(float), typeof(float) });
                if (ci != null) return ci.Invoke(new object[] { x, y, w, h });
            }
            catch (Exception e) { Note("DRAWAPI Rect(float,float,float,float) threw " + e.GetType().Name + ": " + e.Message); }
            try
            {
                object r = Activator.CreateInstance(rectT);
                SetMember(r, "x", x);
                SetMember(r, "y", y);
                SetMember(r, "width", w);
                SetMember(r, "height", h);
                Note("DRAWAPI Rect built by default ctor + members: " + Describe(r));
                return r;
            }
            catch (Exception e) { Note("DRAWAPI Rect default ctor threw " + e.GetType().Name + ": " + e.Message); }
            return null;
        }

        private static void SetMember(object o, string name, float v)
        {
            if (o == null) return;
            for (Type c = o.GetType(); c != null; c = c.BaseType)
            {
                try
                {
                    PropertyInfo p = c.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic |
                                                         BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (p != null && p.GetIndexParameters().Length == 0 && p.GetSetMethod(true) != null)
                    {
                        p.SetValue(o, v, null);
                        return;
                    }
                }
                catch { }
                try
                {
                    FieldInfo f = c.GetField(name, BindingFlags.Public | BindingFlags.NonPublic |
                                                   BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (f != null) { f.SetValue(o, v); return; }
                }
                catch { }
            }
            Miss(o.GetType(), name);
        }

        private static bool NameIs(ParameterInfo p, string name)
        {
            try { return p.ParameterType.Name.IndexOf(name, StringComparison.Ordinal) >= 0; }
            catch { return false; }
        }

        private static bool HasMaterial(ParameterInfo[] ps)
        {
            for (int i = 0; i < ps.Length; i++) if (NameIs(ps[i], "Material")) return true;
            return false;
        }

        // ---------------------------------------------------------------- hooks

        private static void InstallHooks()
        {
            _h = new Harmony(HarmonyId);
            List<string> report = new List<string>();

            PatchThingIcon(report);
            PatchGizmoDraw(report);
            PatchGizmoGrid(report);
            PatchHeartbeat(report);
            PatchResolve(report);

            Note("HOOKS " + string.Join(" | ", report.ToArray()));
            // Always emit one summary, even with zero hits: a histogram section that is present and
            // says "0" is evidence; a missing section is a silent failure.
            DumpSummary("init");
        }

        private static void PatchThingIcon(List<string> report)
        {
            MethodInfo pre = typeof(DrawProbe).GetMethod("PrefixThingIcon",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (pre == null) { report.Add("ThingIcon: patch method missing (reflection bug)"); return; }

            List<MethodInfo> ms = MethodsNamed(new string[] { "ThingIcon" });
            int patched = 0, failed = 0;
            for (int i = 0; i < ms.Count; i++)
            {
                if (patched + failed >= MaxPatches) break;
                try
                {
                    _h.Patch(ms[i], new HarmonyMethod(pre));
                    patched++;
                    Note("HOOK ThingIcon patched " + Sig(ms[i]));
                }
                catch (Exception e)
                {
                    failed++;
                    Note("HOOK ThingIcon FAILED " + Sig(ms[i]) + " :: " + e.GetType().Name + ": " + e.Message);
                }
            }
            report.Add("ThingIcon found=" + ms.Count + " patched=" + patched + " failed=" + failed);
            if (ms.Count == 0) Note("HOOK ThingIcon: no method of that name exists in any loaded assembly");

            // extra evidence: what else looks like an icon entry point
            List<MethodInfo> iconish = MethodsNamed(new string[] { "DrawIcon", "GetIconFor", "IconFor", "DrawIconFor" });
            Note("HOOK iconish methods=" + iconish.Count);
            for (int i = 0; i < iconish.Count && i < 30; i++) Note("HOOK   iconish " + Sig(iconish[i]));
        }

        private static void PatchGizmoDraw(List<string> report)
        {
            MethodInfo post = typeof(DrawProbe).GetMethod("PostfixGizmo",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (post == null) { report.Add("Gizmo: postfix method missing"); return; }

            Type gbase = FindTypeBySimpleName("Gizmo");
            if (gbase == null)
            {
                report.Add("Gizmo: base type NOT FOUND");
                List<MethodInfo> draws = MethodsNamed(new string[] { "DrawGizmo", "DoGizmo", "DrawGizmos" });
                Note("HOOK Gizmo type absent; DrawGizmo-ish methods=" + draws.Count);
                for (int i = 0; i < draws.Count && i < 30; i++) Note("HOOK   gizmoish " + Sig(draws[i]));
                return;
            }

            string[] verbs = { "Draw", "DrawGUI", "OnGUI", "DrawIcon" };
            int patched = 0, failed = 0, types = 0;
            List<Type> ts = Types();
            for (int i = 0; i < ts.Count; i++)
            {
                Type t = ts[i];
                if (t == null || !t.IsClass || t == gbase) continue;
                bool derived;
                try { derived = gbase.IsAssignableFrom(t); }
                catch { continue; }
                if (!derived) continue;
                types++;
                MethodInfo[] ms;
                try
                {
                    ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                      BindingFlags.Instance | BindingFlags.DeclaredOnly);
                }
                catch { continue; }
                for (int j = 0; j < ms.Length; j++)
                {
                    if (patched + failed >= MaxPatches) break;
                    bool match = false;
                    for (int k = 0; k < verbs.Length; k++) if (ms[j].Name == verbs[k]) { match = true; break; }
                    if (!match) continue;
                    if (ms[j].IsAbstract || ms[j].ContainsGenericParameters) continue;
                    try
                    {
                        _h.Patch(ms[j], null, new HarmonyMethod(post));
                        patched++;
                        Note("HOOK GizmoDerived patched " + Sig(ms[j]));
                    }
                    catch (Exception e)
                    {
                        failed++;
                        Note("HOOK GizmoDerived FAILED " + Sig(ms[j]) + " :: " + e.GetType().Name + ": " + e.Message);
                    }
                }
            }
            report.Add("Gizmo base=" + gbase.FullName + " subtypes=" + types + " patched=" + patched + " failed=" + failed);
        }

        private static void PatchGizmoGrid(List<string> report)
        {
            MethodInfo post = typeof(DrawProbe).GetMethod("PostfixGizmo",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (post == null) return;
            List<MethodInfo> ms = MethodsNamed(new string[] { "DrawGizmoGrid", "DoGizmoGrid", "DrawGizmos" });
            int patched = 0, failed = 0;
            for (int i = 0; i < ms.Count; i++)
            {
                try
                {
                    _h.Patch(ms[i], null, new HarmonyMethod(post));
                    patched++;
                    Note("HOOK GizmoGrid patched " + Sig(ms[i]));
                }
                catch (Exception e)
                {
                    failed++;
                    Note("HOOK GizmoGrid FAILED " + Sig(ms[i]) + " :: " + e.GetType().Name + ": " + e.Message);
                }
            }
            report.Add("GizmoGrid found=" + ms.Count + " patched=" + patched + " failed=" + failed);
        }

        private static void PatchHeartbeat(List<string> report)
        {
            MethodInfo post = typeof(DrawProbe).GetMethod("PostfixHeartbeat",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (post == null) { report.Add("heartbeat: postfix method missing"); return; }

            string[] typeNames = { "Root_Play", "Root", "UIRoot_Play", "UIRoot", "Root_Entry" };
            string[] verbs = { "Update", "DoUpdate", "Tick", "DoTick" };
            int patched = 0;
            List<string> tried = new List<string>();
            for (int i = 0; i < typeNames.Length && patched < 3; i++)
            {
                Type t = FindTypeBySimpleName(typeNames[i]);
                if (t == null) { tried.Add(typeNames[i] + "=absent"); continue; }
                tried.Add(typeNames[i] + "=present");
                for (int k = 0; k < verbs.Length && patched < 3; k++)
                {
                    MethodInfo m = null;
                    try
                    {
                        m = t.GetMethod(verbs[k], BindingFlags.Public | BindingFlags.NonPublic |
                                                   BindingFlags.Static | BindingFlags.Instance, null,
                                        Type.EmptyTypes, null);
                    }
                    catch { }
                    if (m == null) continue;
                    try
                    {
                        _h.Patch(m, null, new HarmonyMethod(post));
                        patched++;
                        Note("HOOK heartbeat patched " + Sig(m));
                    }
                    catch (Exception e)
                    {
                        Note("HOOK heartbeat FAILED " + Sig(m) + " :: " + e.GetType().Name + ": " + e.Message);
                    }
                }
            }
            report.Add("heartbeat patched=" + patched + " tried=" + string.Join(",", tried.ToArray()));
        }

        private static void PatchResolve(List<string> report)
        {
            MethodInfo post = typeof(DrawProbe).GetMethod("PostfixResolve",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (post == null) return;
            List<MethodInfo> ms = MethodsNamed(new string[] { "TryResolveTexture", "ResolveTexture", "LoadTexture" });
            int patched = 0, failed = 0;
            for (int i = 0; i < ms.Count && patched < 12; i++)
            {
                ParameterInfo[] ps = SafeParams(ms[i]);
                if (ps.Length < 1 || ps.Length > 3) continue;
                try
                {
                    _h.Patch(ms[i], null, new HarmonyMethod(post));
                    patched++;
                    Note("HOOK resolve patched " + Sig(ms[i]));
                }
                catch (Exception e)
                {
                    failed++;
                    Note("HOOK resolve FAILED " + Sig(ms[i]) + " :: " + e.GetType().Name + ": " + e.Message);
                }
            }
            report.Add("resolve found=" + ms.Count + " patched=" + patched + " failed=" + failed);
        }

        // ---------------------------------------------------------------- hook bodies

        private static bool PrefixThingIcon(object[] __args)
        {
            try
            {
                int n = Interlocked.Increment(ref _iconCalls);
                Hit("ThingIcon");
                if (n == 1) Note("ICON first call, argCount=" + (__args == null ? -1 : __args.Length));
                RecordArgs("ThingIcon", __args, null);
                if (n % 100 == 0) DumpSummary("icon-calls=" + n);
            }
            catch (Exception e)
            {
                int n = Interlocked.Increment(ref _errs);
                if (n <= 3) Note("ICON prefix threw #" + n + ", full stack:\n" + e.ToString());
                else NoteOnce("iconerr", "ICON prefix keeps throwing; the first 3 stacks are above");
            }
            return true;   // never skip the original
        }

        private static void PostfixGizmo(object __instance, object[] __args)
        {
            try
            {
                Interlocked.Increment(ref _gizmoCalls);
                string key = __instance == null ? "static" : __instance.GetType().Name;
                Hit("gizmo:" + key);
                RecordArgs("gizmo:" + key, __args, __instance);
            }
            catch (Exception e)
            {
                int n = Interlocked.Increment(ref _errs);
                if (n <= 3) Note("GIZMO postfix threw #" + n + ", full stack:\n" + e.ToString());
                else NoteOnce("gizmoerr", "GIZMO postfix keeps throwing; the first 3 stacks are above");
            }
        }

        private static void PostfixHeartbeat()
        {
            try
            {
                int f = Interlocked.Increment(ref _frames);
                Hit("heartbeat");
                if (f % HeartbeatEvery == 0)
                {
                    Note("HEARTBEAT frames=" + f + " uptime="
                         + (DateTime.Now - _t0).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)
                         + "s iconCalls=" + Volatile.Read(ref _iconCalls)
                         + " gizmoCalls=" + Volatile.Read(ref _gizmoCalls));
                    DumpSummary("heartbeat@" + f);
                }
            }
            catch (Exception e)
            {
                Note("HEARTBEAT threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void PostfixResolve(object __instance, object[] __args)
        {
            try
            {
                int n = Interlocked.Increment(ref _resolveCalls);
                Hit("resolve");
                if (!TakeDetail("resolve")) return;
                string a = "no args";
                if (__args != null && __args.Length > 0)
                {
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < __args.Length; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(Describe(__args[i]));
                    }
                    a = sb.ToString();
                }
                Note("RESOLVE#" + n + " args[" + a + "] on " + (__instance == null ? "static" : __instance.GetType().Name));
            }
            catch (Exception e)
            {
                Note("RESOLVE postfix threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ---------------------------------------------------------------- counters

        private static void Hit(string hook)
        {
            lock (_cnt)
            {
                int n;
                _hookHits.TryGetValue(hook, out n);
                _hookHits[hook] = n + 1;
            }
        }

        private static bool TakeDetail(string hook)
        {
            int n;
            bool cap = false;
            lock (_cnt)
            {
                _detail.TryGetValue(hook, out n);
                n++;
                _detail[hook] = n;
                if (n == MaxDetailPerHook + 1) cap = true;
            }
            if (cap) Note("DETAIL-CAP " + hook + " reached " + MaxDetailPerHook + "; further calls are counted only");
            return n <= MaxDetailPerHook;
        }

        private static void RecordRect(float w, float h)
        {
            string key = F(w) + "x" + F(h);
            string b = Bucket(h);
            lock (_cnt)
            {
                int n;
                _rects.TryGetValue(key, out n);
                _rects[key] = n + 1;
                _buckets.TryGetValue(b, out n);
                _buckets[b] = n + 1;
            }
        }

        private static void RecordName(Dictionary<string, int> d, string key)
        {
            if (string.IsNullOrEmpty(key)) key = "(empty)";
            lock (_cnt)
            {
                int n;
                d.TryGetValue(key, out n);
                d[key] = n + 1;
            }
        }

        private static string Bucket(float h)
        {
            if (h < 8f) return "<8";
            if (h < 16f) return "8-15";
            if (h < 24f) return "16-23";
            if (h < 32f) return "24-31";
            if (h < 48f) return "32-47";
            if (h < 64f) return "48-63";
            if (h < 96f) return "64-95";
            if (h < 128f) return "96-127";
            return ">=128";
        }

        private static void DumpSummary(string why)
        {
            if (!TakeDetail("summary")) return;
            int icon, giz, frames;
            Dictionary<string, int> hooks, rects, buckets, things, texs;
            lock (_cnt)
            {
                icon = _iconCalls;
                giz = _gizmoCalls;
                frames = _frames;
                hooks = new Dictionary<string, int>(_hookHits);
                rects = new Dictionary<string, int>(_rects);
                buckets = new Dictionary<string, int>(_buckets);
                things = new Dictionary<string, int>(_things);
                texs = new Dictionary<string, int>(_textures);
            }

            List<string> lines = new List<string>();
            lines.Add("SUMMARY why=" + why + " iconCalls=" + icon + " gizmoCalls=" + giz
                      + " frames=" + frames + " distinctRects=" + rects.Count);

            List<KeyValuePair<string, int>> hl = Sorted(hooks);
            for (int i = 0; i < hl.Count; i++) lines.Add("SUMMARY   hook " + hl[i].Key + " = " + hl[i].Value);

            int total = 0;
            List<KeyValuePair<string, int>> rl = Sorted(rects);
            for (int i = 0; i < rl.Count; i++) total += rl[i].Value;
            lines.Add("SUMMARY   rect sizes (top 15 of " + rl.Count + ", total " + total + "):");
            for (int i = 0; i < rl.Count && i < 15; i++)
                lines.Add("SUMMARY     " + Pad(rl[i].Key, 14) + " " + Pad(rl[i].Value.ToString(CultureInfo.InvariantCulture), 6)
                          + Bar(rl[i].Value, total) + " " + Pct(rl[i].Value, total));

            string[] order = { "<8", "8-15", "16-23", "24-31", "32-47", "48-63", "64-95", "96-127", ">=128" };
            lines.Add("SUMMARY   rect-height histogram (text):");
            for (int i = 0; i < order.Length; i++)
            {
                int c;
                buckets.TryGetValue(order[i], out c);
                if (c == 0 && total == 0) continue;
                lines.Add("SUMMARY     h " + Pad(order[i], 8) + " " + Pad(c.ToString(CultureInfo.InvariantCulture), 6)
                          + Bar(c, total) + " " + Pct(c, total));
            }

            lines.Add("SUMMARY   distinctThings=" + things.Count + " " + JoinTop(things, 12));
            lines.Add("SUMMARY   distinctTextures=" + texs.Count + " " + JoinTop(texs, 12));

            for (int i = 0; i < lines.Count; i++) Note(lines[i]);
        }

        private static List<KeyValuePair<string, int>> Sorted(Dictionary<string, int> d)
        {
            List<KeyValuePair<string, int>> l = new List<KeyValuePair<string, int>>(d);
            l.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                int c = b.Value.CompareTo(a.Value);
                if (c != 0) return c;
                return string.CompareOrdinal(a.Key, b.Key);
            });
            return l;
        }

        private static string JoinTop(Dictionary<string, int> d, int n)
        {
            List<KeyValuePair<string, int>> l = Sorted(d);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < l.Count && i < n; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(l[i].Key).Append('=').Append(l[i].Value);
            }
            return sb.Length == 0 ? "(none)" : sb.ToString();
        }

        private static string Pad(string s, int w)
        {
            if (s == null) s = "";
            if (s.Length >= w) return s;
            return s + new string(' ', w - s.Length);
        }

        private static string Bar(int c, int total)
        {
            if (total <= 0) return "|";
            int n = (int)Math.Round(40.0 * c / total);
            if (n < 1 && c > 0) n = 1;
            return "|" + new string('#', n);
        }

        private static string Pct(int c, int total)
        {
            if (total <= 0) return "(0.0%)";
            return "(" + (100.0 * c / total).ToString("0.0", CultureInfo.InvariantCulture) + "%)";
        }

        // ---------------------------------------------------------------- per-call evidence

        /// <summary>Look for an icon on an object without assuming any single known name; every probe is
        /// null-checked and the first hit (or the reason there is none) is reported.</summary>
        private static string HuntIcon(object o)
        {
            if (o == null) return "no-instance";
            string[] names = { "icon", "Icon", "tex", "Tex", "graphic", "Graphic" };
            for (int i = 0; i < names.Length; i++)
            {
                object v = Get(o, names[i]);
                if (v == null) continue;
                string tn = v.GetType().Name;
                if (tn.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) >= 0) return names[i] + "=" + Name(v);
                return names[i] + ":" + tn + "(not a texture)";
            }
            object f = FindTextureIn(o);
            if (f != null) return "field-scan=" + Name(f);
            return "none(no texture-typed member holds a value)";
        }

        private static string DescribeArgs(object[] args)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < args.Length && i < 4; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Describe(args[i]));
            }
            if (args.Length > 4) sb.Append(", ...");
            string s = sb.ToString();
            return s.Length > 240 ? s.Substring(0, 240) : s;
        }

        private static void RecordArgs(string hook, object[] args, object instance)
        {
            if (args == null || args.Length == 0)
            {
                NoteOnce("argn:" + hook, "ARG " + hook + ": no arguments; instance=" + TypeName(instance)
                                         + " icon=" + HuntIcon(instance));
                return;
            }

            float w = 0f, h = 0f;
            object rect = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == null) continue;
                string tn;
                try { tn = args[i].GetType().Name; } catch { continue; }
                if (tn.IndexOf("Rect", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (SizeOf(args[i], out w, out h)) { rect = args[i]; break; }
            }
            if (rect == null)
            {
                NoteOnce("argr:" + hook, "ARG " + hook + ": no Rect-like argument in " + args.Length
                                         + " args; instance=" + TypeName(instance)
                                         + " args=" + DescribeArgs(args) + " icon=" + HuntIcon(instance));
                return;
            }
            RecordRect(w, h);

            if (!TakeDetail(hook)) return;

            object subject = args.Length > 1 ? args[1] : null;
            object def = Get(subject, "def") ?? subject;

            object graphic = Get(def, "graphic");
            if (graphic == null) graphic = Get(subject, "Graphic");
            if (graphic == null) graphic = Get(def, "GraphicData");

            object mat = Get(graphic, "MatSingle");
            if (mat == null) mat = Get(graphic, "MatSouth");
            if (mat == null) mat = Get(graphic, "mat");
            if (mat == null) mat = Get(def, "uiIconMaterial");
            if (mat == null && instance != null) mat = Get(instance, "iconMat");

            string texFrom = "none";
            object tex = null;
            if (mat != null)
            {
                tex = Get(mat, "mainTexture");
                if (tex == null) tex = Get(mat, "mainTex");
                if (tex != null) texFrom = "material.mainTexture";
            }
            if (tex == null)
            {
                tex = Get(def, "uiIcon");
                if (tex != null) texFrom = "def.uiIcon";
            }
            if (tex == null && graphic != null)
            {
                tex = FindTextureIn(graphic);
                if (tex != null) texFrom = "graphic.*Texture*";
            }
            if (tex == null && instance != null)
            {
                tex = FindTextureIn(instance);
                if (tex != null) texFrom = "instance.*Texture*";
            }

            string guiColor;
            if (_guiType == null) guiColor = "GUI type NOT FOUND";
            else
            {
                object c = StaticGet(_guiType, "color");
                guiColor = c == null ? "color member null/missing" : Describe(c);
            }

            Note("ICON#" + Volatile.Read(ref _iconCalls) + " hook=" + hook
                 + " rect=" + F(w) + "x" + F(h)
                 + " thing=" + Name(subject) + " thingType=" + TypeName(subject)
                 + " def=" + Name(def)
                 + " graphic=" + Name(graphic)
                 + " mat=" + Name(mat)
                 + " shader=" + Name(Get(mat, "shader"))
                 + " mask=" + Name(Get(mat, "maskTex"))
                 + " tex=" + Name(tex) + " texFrom=" + texFrom
                 + " texW=" + V(tex, "width") + " texH=" + V(tex, "height")
                 + " mipmapCount=" + V(tex, "mipmapCount")
                 + " format=" + V(tex, "format")
                 + " filterMode=" + V(tex, "filterMode")
                 + " wrapMode=" + V(tex, "wrapMode")
                 + " anisoLevel=" + V(tex, "anisoLevel")
                 + " guiColor=" + guiColor
                 + " instanceIcon=" + HuntIcon(instance));

            RecordName(_things, Name(subject));
            RecordName(_textures, Name(tex) + (tex == null ? "" : " [" + V(tex, "width") + "x" + V(tex, "height") + "]"));

            string a = Attempt("DrawTexture", _mDrawTex, rect, tex, mat);
            string b = Attempt("DrawTextureWithTexCoords", _mDrawTexCoords, rect, tex, mat);
            string c2 = Attempt("DrawTexture+Material", _mDrawTexMat, rect, tex, mat);
            Note("DRAW " + a);
            Note("DRAW " + b + " texCoords=" + Describe(_unitRect));
            Note("DRAW " + c2 + " material=" + Name(mat) + " materialIsNull=" + (mat == null));
        }

        private static bool SizeOf(object rect, out float w, out float h)
        {
            w = 0f;
            h = 0f;
            float fw = Num(Get(rect, "width"));
            float fh = Num(Get(rect, "height"));
            if (!float.IsNaN(fw) && !float.IsNaN(fh)) { w = fw; h = fh; return true; }

            float x0 = Num(Get(rect, "xMin"));
            float x1 = Num(Get(rect, "xMax"));
            float y0 = Num(Get(rect, "yMin"));
            float y1 = Num(Get(rect, "yMax"));
            if (!float.IsNaN(x0) && !float.IsNaN(x1) && !float.IsNaN(y0) && !float.IsNaN(y1))
            {
                w = x1 - x0;
                h = y1 - y0;
                return true;
            }
            return false;
        }

        /// <summary>Depth-1 hunt for any Texture-typed member, so an unexpected object layout still yields a
        /// texture instead of a silent "null".</summary>
        private static object FindTextureIn(object o)
        {
            if (o == null) return null;
            Type t;
            try { t = o.GetType(); } catch { return null; }
            for (Type c = t; c != null; c = c.BaseType)
            {
                PropertyInfo[] ps;
                try
                {
                    ps = c.GetProperties(BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.DeclaredOnly);
                }
                catch { ps = null; }
                if (ps != null)
                {
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (ps[i].GetIndexParameters().Length != 0) continue;
                        if (ps[i].PropertyType.Name.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        try
                        {
                            object v = ps[i].GetValue(o, null);
                            if (v != null) return v;
                        }
                        catch { }
                    }
                }
                FieldInfo[] fs;
                try
                {
                    fs = c.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.DeclaredOnly);
                }
                catch { fs = null; }
                if (fs != null)
                {
                    for (int i = 0; i < fs.Length; i++)
                    {
                        if (fs[i].FieldType.Name.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        try
                        {
                            object v = fs[i].GetValue(o);
                            if (v != null) return v;
                        }
                        catch { }
                    }
                }
            }
            return null;
        }

        private static object[] ArgsFor(MethodInfo m, object rect, object tex, object mat)
        {
            ParameterInfo[] ps = SafeParams(m);
            object[] a = new object[ps.Length];
            int rects = 0;
            for (int i = 0; i < ps.Length; i++)
            {
                string tn;
                try { tn = ps[i].ParameterType.Name; } catch { tn = ""; }
                if (tn == "Rect")
                {
                    rects++;
                    a[i] = rects == 1 ? rect : _unitRect;   // 2nd Rect is a texCoords rect
                }
                else if (tn.IndexOf("Material", StringComparison.Ordinal) >= 0) a[i] = mat;
                else if (tn.IndexOf("Texture", StringComparison.Ordinal) >= 0) a[i] = tex;
                else if (tn == "ScaleMode") a[i] = _scaleMode;
                else if (ps[i].HasDefaultValue) a[i] = ps[i].DefaultValue;
                else if (ps[i].ParameterType.IsEnum) { try { a[i] = Activator.CreateInstance(ps[i].ParameterType); } catch { a[i] = null; } }
                else if (ps[i].ParameterType.IsValueType) { try { a[i] = Activator.CreateInstance(ps[i].ParameterType); } catch { a[i] = null; } }
                else a[i] = null;
            }
            return a;
        }

        private static string Attempt(string tag, MethodInfo m, object rect, object tex, object mat)
        {
            if (m == null) return tag + "=SKIP (no matching overload was found for this port)";
            if (!m.IsStatic) return tag + "=SKIP (resolved overload is an instance method: " + Sig(m) + ")";
            if (rect == null) return tag + "=SKIP (no Rect instance available)";
            if (tex == null) return tag + "=SKIP (texture is null, nothing to draw)";
            object[] args = ArgsFor(m, rect, tex, mat);
            try
            {
                object ret = m.Invoke(null, args);
                return tag + "=OK sig=" + Sig(m) + " ret=" + Describe(ret);
            }
            catch (TargetInvocationException e)
            {
                Exception ie = e.InnerException ?? e;
                return tag + "=THROW " + ie.GetType().Name + ": " + ie.Message + " @ " + StackHead(ie);
            }
            catch (Exception e)
            {
                return tag + "=THROW " + e.GetType().Name + ": " + e.Message + " @ " + StackHead(e);
            }
        }

        private static string StackHead(Exception e)
        {
            try
            {
                string st = e.StackTrace;
                if (string.IsNullOrEmpty(st)) return "(no stack)";
                st = st.Replace("\r", "");
                int nl = st.IndexOf('\n');
                if (nl > 0) st = st.Substring(0, nl);
                return st.Trim();
            }
            catch { return "(stack unavailable)"; }
        }
    }
}