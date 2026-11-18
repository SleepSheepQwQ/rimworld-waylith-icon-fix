using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Xunit;

namespace AgentTouchFix.Tests
{
    /// <summary>
    /// The transpiler, run against a synthetic body that has the same shape as
    /// <c>SamplerFor(string)</c>. There is no way to detour the real renderer on a CI runner (no game, no
    /// Vulkan), so what is verified here is the edit itself: the mipped load becomes a call, the already
    /// correct load is untouched, the stack shape is preserved, and anything unexpected leaves the body
    /// alone instead of producing broken IL.
    /// </summary>
    public class SamplerIlTests
    {
        private struct FakeSampler
        {
#pragma warning disable 0649 // stands in for a Vulkan handle: never assigned in C#, only through IL
            public int Handle;
#pragma warning restore 0649
        }

        private sealed class FakeRenderer
        {
#pragma warning disable 0169, 0649 // only ever read through the IL this test builds
            private FakeSampler _texSampler;
            private FakeSampler _texSamplerMipped;
#pragma warning restore 0169, 0649

            public FakeSampler SamplerFor(string sourcePath)
            {
                if (IsOfficialUiTexture(sourcePath)) return _texSampler;
                return _texSamplerMipped;
            }
        }

        private static bool IsOfficialUiTexture(string path) => path != null;

        private static object Pick(object renderer, string sourcePath) => "non-mipped";

        private static readonly MethodInfo PickMethod =
            typeof(SamplerIlTests).GetMethod(nameof(Pick), BindingFlags.Static | BindingFlags.NonPublic);

        private static readonly MethodInfo SamplerForMethod =
            typeof(FakeRenderer).GetMethod(nameof(FakeRenderer.SamplerFor));

        private static FieldInfo Field(string name) =>
            typeof(FakeRenderer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>The shape the C# above compiles to, minus the branch offsets.</summary>
        private static List<CodeInstruction> MippedThenPlain()
        {
            return new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call, typeof(SamplerIlTests).GetMethod(
                    nameof(IsOfficialUiTexture), BindingFlags.Static | BindingFlags.NonPublic)),
                new CodeInstruction(OpCodes.Brfalse_S, default(Label)),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, Field("_texSampler")),
                new CodeInstruction(OpCodes.Ret),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, Field("_texSamplerMipped")),
                new CodeInstruction(OpCodes.Ret),
            };
        }

        [Fact]
        public void TheMippedLoadBecomesACallIntoPick()
        {
            List<CodeInstruction> body = MippedThenPlain();
            var output = new List<CodeInstruction>();

            int sites = SamplerIl.Rewrite(output, body, typeof(FakeRenderer), "_texSamplerMipped",
                                          typeof(FakeSampler), SamplerForMethod, PickMethod);

            Assert.Equal(1, sites);
            Assert.Equal(body.Count + 2, output.Count); // one instruction in, three out

            int at = output.FindIndex(i => i.opcode == OpCodes.Call && Equals(i.operand, PickMethod));
            Assert.True(at > 0, "no call into Pick in the rewritten body");

            // this is already on the stack; the replacement pushes sourcePath (arg1 of SamplerFor)
            Assert.Equal(OpCodes.Ldarg_1, output[at - 1].opcode);
            Assert.Equal(OpCodes.Unbox_Any, output[at + 1].opcode);
            Assert.Equal(typeof(FakeSampler), output[at + 1].operand);
            // stack shape is preserved: exactly one sampler value where the field load used to leave one
            Assert.Equal(OpCodes.Ret, output[at + 2].opcode);

            // no mipped field load survives
            Assert.DoesNotContain(output, i => i.operand is FieldInfo f && f.Name == "_texSamplerMipped");
        }

        [Fact]
        public void TheOfficialUiBranchIsLeftExactlyAsItWas()
        {
            List<CodeInstruction> body = MippedThenPlain();
            var output = new List<CodeInstruction>();

            SamplerIl.Rewrite(output, body, typeof(FakeRenderer), "_texSamplerMipped",
                              typeof(FakeSampler), SamplerForMethod, PickMethod);

            // the _texSampler load is still the same field, untouched
            Assert.Contains(output, i => Equals(i.operand, Field("_texSampler")));
            Assert.Equal(body[0], output[0]);
            Assert.Equal(body.Count(i => i.opcode == OpCodes.Ret), output.Count(i => i.opcode == OpCodes.Ret));
        }

        [Fact]
        public void LabelsMoveToTheFirstReplacementInstruction()
        {
            List<CodeInstruction> body = MippedThenPlain();
            var label = default(Label);
            int mipped = body.FindIndex(i => i.operand is FieldInfo f && f.Name == "_texSamplerMipped");
            body[mipped].labels.Add(label);

            var output = new List<CodeInstruction>();
            SamplerIl.Rewrite(output, body, typeof(FakeRenderer), "_texSamplerMipped",
                              typeof(FakeSampler), SamplerForMethod, PickMethod);

            int call = output.FindIndex(i => i.opcode == OpCodes.Call && Equals(i.operand, PickMethod));
            Assert.Contains(label, output[call - 1].labels);
            Assert.Empty(output[call].labels);
        }

        [Fact]
        public void ABodyWithoutTheMippedLoadIsReportedAndCopiedUnchanged()
        {
            var body = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ret),
            };
            var output = new List<CodeInstruction>();

            int sites = SamplerIl.Rewrite(output, body, typeof(FakeRenderer), "_texSamplerMipped",
                                          typeof(FakeSampler), SamplerForMethod, PickMethod);

            Assert.Equal(0, sites); // the caller unpatches on this
            Assert.Equal(body, output);
        }

        [Fact]
        public void AnotherRectifiersSamplerFieldIsNotTouched()
        {
            // a field of the same name on a different class must not be rewritten
            var body = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, Field("_texSamplerMipped")),
                new CodeInstruction(OpCodes.Ret),
            };
            var output = new List<CodeInstruction>();

            int sites = SamplerIl.Rewrite(output, body, typeof(string), "_texSamplerMipped",
                                          typeof(FakeSampler), SamplerForMethod, PickMethod);

            Assert.Equal(0, sites);
            Assert.Equal(body, output);
        }

        [Fact]
        public void AStaticTargetLoadsThePathFromArgZero()
        {
            MethodInfo staticLike = typeof(SamplerIlTests).GetMethod(
                nameof(StaticLike), BindingFlags.Static | BindingFlags.NonPublic);

            var output = new List<CodeInstruction>();
            SamplerIl.Rewrite(output, new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, Field("_texSamplerMipped")),
                new CodeInstruction(OpCodes.Ret),
            }, typeof(FakeRenderer), "_texSamplerMipped", typeof(FakeSampler), staticLike, PickMethod);

            int call = output.FindIndex(i => i.opcode == OpCodes.Call && Equals(i.operand, PickMethod));
            Assert.Equal(OpCodes.Ldarg_0, output[call - 1].opcode);
        }

        private static void StaticLike(string sourcePath) { }

        [Fact]
        public void AReferenceTypedResultIsCastInsteadOfUnboxed()
        {
            var output = new List<CodeInstruction>();
            SamplerIl.Rewrite(output, MippedThenPlain(), typeof(FakeRenderer), "_texSamplerMipped",
                              typeof(string), SamplerForMethod, PickMethod);

            Assert.Contains(output, i => i.opcode == OpCodes.Castclass && Equals(i.operand, typeof(string)));
            Assert.DoesNotContain(output, i => i.opcode == OpCodes.Unbox_Any);
        }
    }
}
