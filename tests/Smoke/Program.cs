using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

/// <summary>
/// Runs the mod's real static entry point outside the game and checks the drawprobe.log contract.
/// Exits non-zero (failing the CI job) if the probe would have been silent or incomplete.
/// </summary>
internal static class Program
{
    private static readonly List<string> Fails = new List<string>();

    private static int Main()
    {
        string dir = AppContext.BaseDirectory;
        string dll = Path.Combine(dir, "AgentTouchFix.dll");
        Console.WriteLine("smoke: base dir  = " + dir);
        Console.WriteLine("smoke: mod dll   = " + dll);
        if (!File.Exists(dll))
        {
            Console.WriteLine("SMOKE FAIL: AgentTouchFix.dll not in the harness output");
            return 1;
        }

        Assembly asm;
        try { asm = Assembly.LoadFrom(dll); }
        catch (Exception e)
        {
            Console.WriteLine("SMOKE FAIL: load threw " + e);
            return 1;
        }
        Console.WriteLine("smoke: mod version = " + asm.GetName().Version);

        Type boot = asm.GetType("AgentTouchFix.Bootstrap", false);
        if (boot == null)
        {
            Console.WriteLine("SMOKE FAIL: AgentTouchFix.Bootstrap not found in the assembly");
            return 1;
        }

        // The real entry point: the static constructor invoked by [StaticConstructorOnStartup].
        try { RuntimeHelpers.RunClassConstructor(boot.TypeHandle); }
        catch (Exception e) { Console.WriteLine("SMOKE FAIL: entry point threw:\n" + e); }

        string logPath = Path.Combine(dir, "drawprobe.log");
        if (!File.Exists(logPath))
        {
            Console.WriteLine("SMOKE FAIL: the probe wrote no log at " + logPath);
            Console.WriteLine("  --- files in the harness output ---");
            string[] all = Directory.GetFiles(dir);
            for (int i = 0; i < all.Length; i++) Console.WriteLine("  " + Path.GetFileName(all[i]));
            return 1;
        }

        string[] lines = File.ReadAllLines(logPath);
        string text = string.Join("\n", lines);
        string first = lines.Length > 0 ? lines[0].TrimEnd() : "";
        Console.WriteLine("smoke: probe log = " + logPath + " (" + lines.Length + " lines)");
        int show = Math.Min(lines.Length, 55);
        for (int i = 0; i < show; i++) Console.WriteLine("  | " + lines[i]);
        if (lines.Length > show) Console.WriteLine("  | ... " + (lines.Length - show) + " more lines");

        Check(first.StartsWith("BUILD "), "first log line starts with BUILD");
        Check(Regex.IsMatch(first, @"^BUILD \d+\.\d+\.\d+-\d+"), "first line is BUILD <version>-<timestamp>, got: " + first);
        Check(first.Contains(" log="), "first line states its own log path");
        Check(first.Contains("drawprobe.log"), "first line names drawprobe.log");
        Check(first.Contains(Path.GetFileName(dir)) || first.Contains("/"), "first line path is absolute");

        Check(text.Contains("PATH read-with=run-as"), "first lines carry the run-as read command");
        Check(text.Contains("INIT-PRE"), "INIT-PRE line written before Init");
        Check(text.Contains("INIT-POST"), "INIT-POST line written after Init");
        Check(text.Contains("INIT-BEGIN"), "probe INIT-BEGIN");
        Check(text.Contains("INIT-END status=ok"), "probe INIT-END status=ok");
        Check(!text.Contains("status=FAILED"), "probe init did not report a failure");

        Check(text.Contains("SHADERDB"), "ShaderDatabase member dump attempted");
        Check(text.Contains("SHADERDB NOT FOUND") || text.Contains("SHADERDB type="), "ShaderDatabase dump produced a verdict");
        Check(!text.Contains("SHADERDB dump FAILED"), "ShaderDatabase dump did not throw");

        Check(text.Contains("DRAWAPI chosen DrawTexture="), "DrawTexture resolution logged");
        Check(text.Contains("DRAWAPI chosen DrawTextureWithTexCoords="), "DrawTextureWithTexCoords resolution logged");
        Check(text.Contains("DRAWAPI chosen material-draw="), "material draw resolution logged");

        Check(text.Contains("HOOKS "), "hook installation report written");
        Check(text.Contains("ENV typemap"), "type map dumped");
        Check(text.Contains("ENV assemblies="), "assembly inventory dumped");

        Console.WriteLine();
        if (Fails.Count == 0)
        {
            Console.WriteLine("SMOKE OK (" + lines.Length + " log lines, contract satisfied)");
            return 0;
        }
        for (int i = 0; i < Fails.Count; i++) Console.WriteLine("SMOKE FAIL: " + Fails[i]);
        return 1;
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
        if (!ok) Fails.Add(what);
    }
}
