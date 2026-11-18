using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace AgentTouchFix
{
    /// <summary>
    /// The IL edit itself, kept apart from the reflection and logging in <see cref="SamplerFix"/> so CI
    /// can run it on a synthetic body. Harmony is only a compile-time dependency here: this file builds
    /// <see cref="CodeInstruction"/> values, it does not detour anything.
    ///
    /// What it rewrites, inside <c>ImGuiVulkanRenderer.SamplerFor(string)</c>:
    ///
    /// <code>
    ///   ldarg.0                        ldarg.0
    ///   ldfld _texSamplerMipped   -->  ldarg.1
    ///                                  call object Pick(object, string)
    ///                                  unbox.any VkSampler
    /// </code>
    ///
    /// A postfix cannot do this: the return type is a Vulkan handle struct that this mod cannot reference
    /// at compile time, and Harmony's <c>ref __result</c> has to match the type exactly. Swapping the
    /// field reference keeps the stack shape identical, needs no type reference in C#, and leaves the
    /// official-UI branch (<c>ldfld _texSampler</c>) untouched - which is also the branch that is already
    /// correct, so the only behavioural delta is the mipped one.
    /// </summary>
    internal static class SamplerIl
    {
        /// <summary>
        /// Appends the rewritten body to <paramref name="output"/> and returns the number of call sites
        /// that were replaced. Zero means the pattern is not there: the caller must then unpatch.
        /// </summary>
        internal static int Rewrite(List<CodeInstruction> output,
                                    IEnumerable<CodeInstruction> instructions,
                                    Type owner,
                                    string mippedField,
                                    Type samplerType,
                                    MethodBase original,
                                    MethodInfo pick)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (instructions == null) throw new ArgumentNullException(nameof(instructions));
            if (samplerType == null) throw new ArgumentNullException(nameof(samplerType));
            if (pick == null) throw new ArgumentNullException(nameof(pick));

            bool isStatic = original != null && original.IsStatic;
            CodeInstruction loadPath = new CodeInstruction(isStatic ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1);
            OpCode unbox = samplerType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass;
            int sites = 0;

            foreach (CodeInstruction ins in instructions)
            {
                FieldInfo field = ins.operand as FieldInfo;
                bool isMippedLoad = (ins.opcode == OpCodes.Ldfld || ins.opcode == OpCodes.Ldflda)
                                    && field != null
                                    && field.Name == mippedField
                                    && (owner == null || field.DeclaringType == owner);

                // ldflda hands out an address, not a value: replacing it with a boxed struct would not
                // even be verifiable, so that shape is left alone (and reported as "not patched").
                if (!isMippedLoad || ins.opcode == OpCodes.Ldflda)
                {
                    output.Add(ins);
                    continue;
                }

                // The replacement is three instructions, so labels and exception-block starts move to the
                // first of them; a branch that used to land on the ldfld must still land on the new sequence.
                var load = new CodeInstruction(loadPath.opcode);
                load.labels.AddRange(ins.labels);
                load.blocks.AddRange(ins.blocks);
                output.Add(load);
                output.Add(new CodeInstruction(OpCodes.Call, pick));
                output.Add(new CodeInstruction(unbox, samplerType));
                sites++;
            }

            return sites;
        }
    }
}
