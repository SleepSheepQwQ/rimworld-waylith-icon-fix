using System;

namespace Verse
{
    /// <summary>Compile-time stand-in for Verse.Log (the game provides the real one at runtime).</summary>
    public static class Log
    {
        public static void Message(string text) { }
        public static void Warning(string text) { }
        public static void Error(string text) { }
    }

    /// <summary>Compile-time stand-in for Verse.StaticConstructorOnStartup.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class StaticConstructorOnStartup : Attribute
    {
    }
}
