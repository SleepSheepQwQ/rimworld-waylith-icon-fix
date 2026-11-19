using System;
using System.Reflection;

namespace AgentTouchFix
{
    /// <summary>
    /// One named value on a game type, whichever way the shipped assembly exposes it: a field or a
    /// property, instance or static, readable and - when asked for - writable.
    ///
    /// 2.6.1 looked up `uiIcon` with <c>Type.GetField(name, Public|NonPublic|Instance)</c> and hard
    /// coded the answer into a <see cref="FieldInfo"/>; on the device that returned null and the whole
    /// draw-time repair logged "uiIcon field not found on ThingDef" (the member is a public instance
    /// field on the base type - docs/REFLECTION-FACTS.md). The lookup is therefore done once, here,
    /// with four independent strategies and the ordering decision delegated to
    /// <see cref="MemberChoice"/>:
    ///
    ///   1. per level of the hierarchy: GetField / GetProperty with every binding flag;
    ///   2. a GetMembers sweep on the requested type, matching by name (some runtimes answer the
    ///      sweep for a member whose signature the typed lookup refuses to load);
    ///   3. "usable" folds in the caller's requirement: a value that must be written only counts when
    ///      it is writable (a field that is not InitOnly/Literal, a property with a setter);
    ///   4. field first, or property first, per <see cref="MemberChoice.Choose"/>.
    ///
    /// No game type is named here, so unit tests drive it against local stand-in classes - which is
    /// exactly the regression that shipped: a truth table nobody had executed.
    /// </summary>
    internal sealed class Member
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static |
                                         BindingFlags.FlattenHierarchy;

        private readonly FieldInfo _field;
        private readonly PropertyInfo _prop;

        private Member(FieldInfo field, PropertyInfo prop)
        {
            _field = field;
            _prop = prop;

            Type valueType = null;
            bool isStatic = false;
            bool canWrite = false;

            if (field != null)
            {
                valueType = SafeFieldType(field);
                isStatic = field.IsStatic;
                canWrite = !field.IsInitOnly && !field.IsLiteral;
            }
            else if (prop != null)
            {
                valueType = SafePropertyType(prop);
                MethodInfo getter = prop.GetGetMethod(true);
                MethodInfo setter = prop.GetSetMethod(true);
                isStatic = (getter ?? setter) != null && (getter ?? setter).IsStatic;
                canWrite = setter != null;
            }

            ValueType = valueType;
            IsStatic = isStatic;
            CanWrite = canWrite;
            DeclaringTypeName = field != null ? NameOf(field.DeclaringType)
                              : prop != null ? NameOf(prop.DeclaringType)
                              : "?";
        }

        /// <summary>Null when the name was not found in a usable shape.</summary>
        internal static Member Resolve(Type type, string name, bool needWrite, bool preferField)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;

            FieldInfo field = null;
            PropertyInfo prop = null;

            try
            {
                for (Type c = type; c != null; c = SafeBase(c))
                {
                    if (field == null) field = FirstField(c, name);
                    if (prop == null) prop = FirstProperty(c, name);
                    if (field != null && prop != null) break;
                }
            }
            catch { }

            if (field == null) field = FieldBySweep(type, name);
            if (prop == null) prop = PropertyBySweep(type, name);

            int kind = MemberChoice.Choose(FieldUsable(field, needWrite), PropertyUsable(prop, needWrite), preferField);
            if (kind == MemberChoice.Field) return new Member(field, null);
            if (kind == MemberChoice.Property) return new Member(null, prop);
            return null;
        }

        /// <summary>The member's declared value type, or null when it could not be loaded.</summary>
        internal Type ValueType { get; }

        /// <summary>True for a static field or a static property: read/write pass a null instance.</summary>
        internal bool IsStatic { get; }

        /// <summary>False for a read-only property or an InitOnly/Literal field.</summary>
        internal bool CanWrite { get; }

        /// <summary>"field" or "property" - the word the startup log prints.</summary>
        internal string Kind
        {
            get { return _field != null ? "field" : _prop != null ? "property" : "none"; }
        }

        /// <summary>Where the member is declared, so the log names the type that actually has it.</summary>
        internal string DeclaringTypeName { get; }

        internal object Get(object instance)
        {
            try
            {
                object target = IsStatic ? null : instance;
                if (_field != null) return _field.GetValue(target);
                if (_prop != null && _prop.GetGetMethod(true) != null) return _prop.GetValue(target, null);
            }
            catch { }
            return null;
        }

        /// <summary>False when the value cannot be written; never throws.</summary>
        internal bool Set(object instance, object value)
        {
            if (!CanWrite) return false;
            try
            {
                object target = IsStatic ? null : instance;
                if (_field != null) { _field.SetValue(target, value); return true; }
                MethodInfo setter = _prop == null ? null : _prop.GetSetMethod(true);
                if (setter != null) { _prop.SetValue(target, value, null); return true; }
            }
            catch { }
            return false;
        }

        // ------------------------------------------------------------------ search

        private static bool FieldUsable(FieldInfo f, bool needWrite)
        {
            if (f == null) return false;
            if (!needWrite) return true;
            return !f.IsInitOnly && !f.IsLiteral;
        }

        private static bool PropertyUsable(PropertyInfo p, bool needWrite)
        {
            if (p == null || p.GetGetMethod(true) == null) return false;
            if (p.GetIndexParameters().Length != 0) return false;
            if (!needWrite) return true;
            return p.GetSetMethod(true) != null;
        }

        private static FieldInfo FirstField(Type t, string name)
        {
            try
            {
                FieldInfo f = t.GetField(name, Any);
                if (f != null) return f;
                // Private/protected statics of a base class are not covered by FlattenHierarchy on
                // every runtime: ask each visibility separately before giving up on this level.
                return t.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
                    ?? t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch { return null; }
        }

        private static PropertyInfo FirstProperty(Type t, string name)
        {
            try
            {
                PropertyInfo p = t.GetProperty(name, Any);
                if (p != null && p.GetIndexParameters().Length == 0) return p;
                p = t.GetProperty(name, BindingFlags.NonPublic | BindingFlags.Static)
                 ?? t.GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.GetIndexParameters().Length == 0) return p;
            }
            catch { }
            return null;
        }

        private static FieldInfo FieldBySweep(Type t, string name)
        {
            try
            {
                foreach (MemberInfo m in t.GetMembers(Any))
                {
                    if (!(m is FieldInfo f) || f.Name != name) continue;
                    return f;
                }
            }
            catch { }
            return null;
        }

        private static PropertyInfo PropertyBySweep(Type t, string name)
        {
            try
            {
                foreach (MemberInfo m in t.GetMembers(Any))
                {
                    if (!(m is PropertyInfo p) || p.Name != name) continue;
                    if (p.GetIndexParameters().Length != 0) continue;
                    return p;
                }
            }
            catch { }
            return null;
        }

        private static Type SafeBase(Type t)
        {
            try { return t.BaseType; } catch { return null; }
        }

        private static Type SafeFieldType(FieldInfo f)
        {
            try { return f.FieldType; } catch { return null; }
        }

        private static Type SafePropertyType(PropertyInfo p)
        {
            try { return p.PropertyType; } catch { return null; }
        }

        private static string NameOf(Type t)
        {
            if (t == null) return "?";
            try
            {
                string ns = t.Namespace;
                return string.IsNullOrEmpty(ns) ? t.Name : ns + "." + t.Name;
            }
            catch { return "?"; }
        }
    }
}
