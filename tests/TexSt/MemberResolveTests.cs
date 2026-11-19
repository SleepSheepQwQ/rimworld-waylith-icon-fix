using System;
using Xunit;

namespace AgentTouchFix.Tests
{
    /// <summary>
    /// <see cref="Member"/> against local stand-ins shaped like the members the device log complained
    /// about. No game assembly is involved, which is the point: 2.6.1 shipped a lookup that failed on
    /// a public field inherited from a base type (Verse.BuildableDef.uiIcon) and on a static, get-only
    /// property (RimWorld.Faction.OfPlayer), and nothing executed either case before the device did.
    /// </summary>
    public class MemberResolveTests
    {
        // ---- stand-ins -------------------------------------------------------------------------

        private class FieldOnly
        {
            public string uiIcon = "field";
        }

        private class PropertyOnly
        {
            private string _v = "property";
            public string uiIcon { get { return _v; } set { _v = value; } }
        }

        private class ReadOnlyProperty
        {
            public string uiIcon { get { return "get-only"; } }
        }

        private class ReadOnlyField
        {
            public readonly string uiIcon = "init-only";
        }

        // the realistic "both kinds exist" case: a build adds a property that hides a base field
        private class BaseWithField
        {
            public string uiIcon = "base-field";
        }

        private class PropertyHidingBaseField : BaseWithField
        {
            public new string uiIcon { get; set; } = "derived-property";
        }

        // the ThingDef/BuildableDef shape: the member lives on a base type, and it is private there
        private class PrivateFieldBase
        {
            private string uiIcon = "private-base";
            public void SetIcon(string v) { uiIcon = v; }
            public string GetIcon() { return uiIcon; }
        }

        private class DerivedFromPrivateBase : PrivateFieldBase
        {
        }

        private class NoSuchMember
        {
            public string other = "x";
        }

        private class StaticField
        {
            public static string OfPlayer = "static-field";
        }

        private class StaticProperty
        {
            public static string OfPlayer { get { return "static-property"; } }
        }

        private class InstanceProperty
        {
            public string OfPlayer { get { return "instance-property"; } }
        }

        // ---- field / property / neither ----------------------------------------------------------

        [Fact]
        public void APlainFieldIsFoundAndWritable()
        {
            Member m = Member.Resolve(typeof(FieldOnly), "uiIcon", needWrite: true, preferField: true);
            Assert.NotNull(m);
            Assert.Equal("field", m.Kind);
            Assert.True(m.CanWrite);
            Assert.False(m.IsStatic);

            var target = new FieldOnly();
            Assert.True(m.Set(target, "written"));
            Assert.Equal("written", m.Get(target));
            Assert.Equal("written", target.uiIcon);
        }

        [Fact]
        public void APropertyIsTheFallbackWhenNoFieldHasTheName()
        {
            Member m = Member.Resolve(typeof(PropertyOnly), "uiIcon", needWrite: true, preferField: true);
            Assert.NotNull(m);
            Assert.Equal("property", m.Kind);
            Assert.True(m.CanWrite);

            var target = new PropertyOnly();
            Assert.True(m.Set(target, "written"));
            Assert.Equal("written", m.Get(target));
        }

        [Fact]
        public void FieldBeatsPropertyWhenFieldIsPreferred()
        {
            Member m = Member.Resolve(typeof(PropertyHidingBaseField), "uiIcon", needWrite: false, preferField: true);
            Assert.NotNull(m);
            Assert.Equal("field", m.Kind);
            Assert.Equal("AgentTouchFix.Tests.BaseWithField", m.DeclaringTypeName);
            Assert.Equal("base-field", m.Get(new PropertyHidingBaseField()));
        }

        [Fact]
        public void PropertyBeatsFieldWhenPropertyIsPreferred()
        {
            // the OfPlayer ordering: the game's API is a property even if a field of the same name exists
            Member m = Member.Resolve(typeof(PropertyHidingBaseField), "uiIcon", needWrite: false, preferField: false);
            Assert.NotNull(m);
            Assert.Equal("property", m.Kind);
            Assert.Equal("derived-property", m.Get(new PropertyHidingBaseField()));
        }

        [Fact]
        public void NeitherKindResolvesToNothing()
        {
            Assert.Null(Member.Resolve(typeof(NoSuchMember), "uiIcon", needWrite: false, preferField: true));
            Assert.Null(Member.Resolve(typeof(NoSuchMember), "uiIcon", needWrite: true, preferField: false));
            Assert.Null(Member.Resolve(null, "uiIcon", needWrite: false, preferField: true));
            Assert.Null(Member.Resolve(typeof(FieldOnly), "", needWrite: false, preferField: true));
        }

        // ---- writability ---------------------------------------------------------------------------

        [Fact]
        public void AGetOnlyPropertyIsRefusedWhenTheValueMustBeWritten()
        {
            Assert.Null(Member.Resolve(typeof(ReadOnlyProperty), "uiIcon", needWrite: true, preferField: true));

            // but it is still readable, so a read-only caller gets it (fail-open reading)
            Member read = Member.Resolve(typeof(ReadOnlyProperty), "uiIcon", needWrite: false, preferField: true);
            Assert.NotNull(read);
            Assert.Equal("property", read.Kind);
            Assert.False(read.CanWrite);
            Assert.Equal("get-only", read.Get(new ReadOnlyProperty()));
            Assert.False(read.Set(new ReadOnlyProperty(), "y"));   // never throws
        }

        [Fact]
        public void AnInitOnlyFieldIsRefusedWhenTheValueMustBeWritten()
        {
            Assert.Null(Member.Resolve(typeof(ReadOnlyField), "uiIcon", needWrite: true, preferField: true));

            Member read = Member.Resolve(typeof(ReadOnlyField), "uiIcon", needWrite: false, preferField: true);
            Assert.NotNull(read);
            Assert.Equal("field", read.Kind);
            Assert.False(read.CanWrite);
            Assert.Equal("init-only", read.Get(new ReadOnlyField()));
        }

        // ---- inheritance ---------------------------------------------------------------------------

        [Fact]
        public void AFieldDeclaredOnABaseTypeIsFound()
        {
            // Verse.BuildableDef.uiIcon seen through the runtime type Verse.ThingDef
            Member m = Member.Resolve(typeof(DerivedFromPrivateBase), "uiIcon", needWrite: true, preferField: true);
            Assert.NotNull(m);
            Assert.Equal("field", m.Kind);
            Assert.Equal("AgentTouchFix.Tests.PrivateFieldBase", m.DeclaringTypeName);
            Assert.Equal("private-base", m.Get(new DerivedFromPrivateBase()));
        }

        // ---- static --------------------------------------------------------------------------------

        [Fact]
        public void AStaticPropertyIsReadWithANullInstance()
        {
            // RimWorld.Faction.OfPlayer: static, get-only, no instance to pass
            Member m = Member.Resolve(typeof(StaticProperty), "OfPlayer", needWrite: false, preferField: false);
            Assert.NotNull(m);
            Assert.Equal("property", m.Kind);
            Assert.True(m.IsStatic);
            Assert.Equal("static-property", m.Get(null));
        }

        [Fact]
        public void AStaticFieldIsAcceptedToo()
        {
            Member m = Member.Resolve(typeof(StaticField), "OfPlayer", needWrite: true, preferField: false);
            Assert.NotNull(m);
            Assert.Equal("field", m.Kind);
            Assert.True(m.IsStatic);
            Assert.True(m.Set(null, "written"));
            Assert.Equal("written", m.Get(null));
            Assert.Equal("written", StaticField.OfPlayer);
        }

        [Fact]
        public void AStaticPropertyWithNoFieldIsStillFoundWhenFieldIsPreferred()
        {
            // uiIcon-style ordering must not turn "property only" into "nothing"
            Member m = Member.Resolve(typeof(StaticProperty), "OfPlayer", needWrite: false, preferField: true);
            Assert.NotNull(m);
            Assert.Equal("property", m.Kind);
        }

        [Fact]
        public void AnInstancePropertyIsNotReportedAsStatic()
        {
            Member m = Member.Resolve(typeof(InstanceProperty), "OfPlayer", needWrite: false, preferField: false);
            Assert.NotNull(m);
            Assert.False(m.IsStatic);
            Assert.Equal("instance-property", m.Get(new InstanceProperty()));
            Assert.Null(m.Get(null));   // an instance member read without an instance: no value, no throw
        }

        [Fact]
        public void TheValueTypeIsCarriedSoCallersCanWalkIntoIt()
        {
            Member m = Member.Resolve(typeof(FieldOnly), "uiIcon", needWrite: false, preferField: true);
            Assert.Equal(typeof(string), m.ValueType);
        }
    }
}
