using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Verse;

namespace AgentTouchFix
{
    /// <summary>
    /// Minimal runtime plumbing: a file-only log sink and a type resolver.
    ///
    /// Everything the mod reports goes to files/game/agenttouchfix.log. Verse.Log is deliberately not
    /// used for routine output - Log.Warning/Log.Error open the in-game "there were errors" dialog and
    /// Log.Message spams the console on this port.
    /// </summary>
    internal static class Refl
    {
        internal const string P = "[AgentTouchFix] ";

        internal static readonly string LogPath =
            "/data/data/com.ports1de.waylith/files/game/agenttouchfix.log";

        private const long MaxLogBytes = 1024L * 1024L;

        private static readonly object _lock = new object();
        private static FileStream _fs;
        private static StreamWriter _w;
        private static long _written;
        private static bool _console;
        private static int _errLogged;

        internal static void Info(string m) { Write("INFO", m); }
        internal static void Warn(string m) { Write("WARN", m); }

        /// <summary>Exactly one in-game error per process, so a real failure is visible but never spammy.</summary>
        internal static void ErrOnce(string m)
        {
            Write("ERROR", m);
            if (Interlocked.Exchange(ref _errLogged, 1) == 0)
            {
                try { Log.Error(P + m); } catch { }
            }
        }

        private static void Write(string level, string m)
        {
            try
            {
                lock (_lock)
                {
                    if (_w == null) Open();
                    string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + " " + m;
                    if (_w == null)
                    {
                        // The device log path does not exist off-device (CI, desktop harness). Silence is
                        // right on the phone, but a run that cannot write its log must still be able to
                        // say why a patch did not fire, so fall back to the process stderr.
                        if (_console) Console.Error.WriteLine(P + line);
                        return;
                    }
                    _w.WriteLine(line);
                    _w.Flush();
                    _written += line.Length + 2;
                    if (_written > MaxLogBytes) { Close(); }
                }
            }
            catch { }
        }

        private static void Open()
        {
            try
            {
                string dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _w = new StreamWriter(_fs, new UTF8Encoding(false));
                _written = _fs.Length;
            }
            catch
            {
                _w = null;
                _console = true;
            }
        }

        private static void Close()
        {
            try { _w?.Flush(); _w?.Dispose(); } catch { }
            _w = null;
            _fs = null;
            _written = 0;
        }

        /// <summary>
        /// Resolve a type by full name across the assemblies the game has loaded. Used instead of
        /// compile-time references so the mod carries no dependency on the port's internals.
        /// </summary>
        internal static Type T(string fullName)
        {
            try
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = a.GetType(fullName, false);
                    if (t != null) return t;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Find a method by name, optionally requiring a parameter count.</summary>
        internal static MethodInfo M(Type t, string name, int argc = -1)
        {
            if (t == null) return null;
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                  BindingFlags.Instance | BindingFlags.Static))
            {
                if (m.Name != name) continue;
                if (argc >= 0 && m.GetParameters().Length != argc) continue;
                return m;
            }
            return null;
        }
    }
}
