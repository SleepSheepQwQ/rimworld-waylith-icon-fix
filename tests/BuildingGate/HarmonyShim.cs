using System;
using System.Collections.Generic;
using System.Reflection;

namespace HarmonyLib
{
    /// <summary>
    /// Harmony 2.3.3's runtime IL rewrite currently fails under this Termux .NET 10 runtime
    /// ("LocalBuilder is abstract"). The production code only needs Harmony's registration contract
    /// for this gate, so this shim records patches by target MethodInfo; the game-shaped Widgets
    /// methods invoke the registered prefixes in their real order. The test therefore still executes
    /// IconFieldFix.Install(), its reflection and its Prefix/PrefixDef bodies.
    /// </summary>
    internal static class TestPatchRegistry
    {
        // MethodInfo object identity is not stable across every reflection path on this runtime.
        // Key by the metadata signature instead, as Harmony itself resolves a patch target.
        private static readonly Dictionary<string, List<Patch>> Prefixes =
            new Dictionary<string, List<Patch>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<Patch>> Postfixes =
            new Dictionary<string, List<Patch>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, MethodInfo> TargetsByKey =
            new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        private static readonly object Gate = new object();

        internal static void Add(MethodInfo target, MethodInfo method, string owner, bool prefix)
        {
            string key = Key(target);
            lock (Gate)
            {
                Dictionary<string, List<Patch>> table = prefix ? Prefixes : Postfixes;
                List<Patch> list;
                if (!table.TryGetValue(key, out list))
                {
                    list = new List<Patch>();
                    table[key] = list;
                }
                TargetsByKey[key] = target;
                list.Add(new Patch(owner, method));
            }
        }

        internal static Patches Get(MethodInfo target)
        {
            string key = Key(target);
            lock (Gate)
            {
                List<Patch> pre, post;
                Prefixes.TryGetValue(key, out pre);
                Postfixes.TryGetValue(key, out post);
                return new Patches(pre, post);
            }
        }

        internal static void InvokePrefixes(MethodInfo target, object[] args)
        {
            string key = Key(target);
            List<Patch> list;
            lock (Gate)
            {
                if (!Prefixes.TryGetValue(key, out list)) return;
                list = new List<Patch>(list);
            }
            foreach (Patch patch in list)
                patch.PatchMethod.Invoke(null, new object[] { args });
        }

        internal static string Targets()
        {
            lock (Gate)
            {
                var names = new List<string>();
                foreach (string key in Prefixes.Keys)
                    names.Add(key);
                return string.Join(";", names);
            }
        }

        internal static int CountPrefixTargets(string typeName, string methodName, Type first)
        {
            lock (Gate)
            {
                int count = 0;
                foreach (var item in Prefixes)
                {
                    MethodInfo target;
                    if (!TargetsByKey.TryGetValue(item.Key, out target)) continue;
                    if (target.DeclaringType.FullName != typeName || target.Name != methodName) continue;
                    ParameterInfo[] ps = target.GetParameters();
                    if (ps.Length > 0 && ps[0].ParameterType == first) count++;
                }
                return count;
            }
        }

        private static string Key(MethodInfo method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            return method.DeclaringType.FullName + "::" + method.Name + "(" +
                   string.Join(",", Array.ConvertAll(method.GetParameters(),
                       p => p.ParameterType.AssemblyQualifiedName)) + ")";
        }
    }

    public sealed class Patch
    {
        internal Patch(string owner, MethodInfo method)
        {
            this.owner = owner;
            PatchMethod = method;
        }

        public string owner { get; private set; }
        public MethodInfo PatchMethod { get; private set; }
    }

    public sealed class Patches
    {
        internal Patches(List<Patch> prefixes, List<Patch> postfixes)
        {
            Prefixes = new List<Patch>(prefixes ?? new List<Patch>());
            Postfixes = new List<Patch>(postfixes ?? new List<Patch>());
        }

        public List<Patch> Prefixes { get; private set; }
        public List<Patch> Postfixes { get; private set; }
    }

    public sealed class Harmony
    {
        private readonly string _owner;

        public Harmony(string id)
        {
            _owner = id;
        }

        public MethodInfo Patch(MethodInfo original,
                                HarmonyMethod prefix = null,
                                HarmonyMethod postfix = null,
                                HarmonyMethod transpiler = null,
                                HarmonyMethod finalizer = null)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));
            if (prefix != null)
                TestPatchRegistry.Add(original, prefix.PatchMethod, _owner, true);
            if (postfix != null)
                TestPatchRegistry.Add(original, postfix.PatchMethod, _owner, false);
            if (transpiler != null)
                throw new NotSupportedException("transpilers are not needed by the building gate");
            if (finalizer != null)
                throw new NotSupportedException("finalizers are not needed by the building gate");
            return original;
        }

        public static Patches GetPatchInfo(MethodInfo original)
        {
            return TestPatchRegistry.Get(original);
        }
    }

    public sealed class HarmonyMethod
    {
        public HarmonyMethod(MethodInfo method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            PatchMethod = method;
        }

        public MethodInfo PatchMethod { get; private set; }
        public int priority { get; set; }
        public string[] before { get; set; }
        public string[] after { get; set; }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class HarmonyPriorityAttribute : Attribute
    {
        public HarmonyPriorityAttribute(int priority) { info = priority; }
        public int info { get; private set; }
    }

    public static class Priority
    {
        public const int First = 800;
        public const int Last = 0;
        public const int Low = 200;
        public const int Normal = 400;
        public const int High = 600;
    }
}
