using System;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using JetBrains.Annotations;

namespace Xysstide.Reflection.Benchmarks.BindingFlagBenchmarks;

[UsedImplicitly]
internal class NonPublicClass
{
    [UsedImplicitly]
    private int privateField;

    [UsedImplicitly]
    private int PrivateProperty { get; }

    [UsedImplicitly]
    private void privateMethod() { }
}

[UsedImplicitly]
public class PublicClass
{
    [UsedImplicitly]
    public int publicField;

    [UsedImplicitly]
    public int PublicProperty { get; }

    [UsedImplicitly]
    public void PublicMethod() { }
}

public class ExactVsGeneral
{
    private const BindingFlags type_members_non_public_flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags type_members_public_flags     = BindingFlags.Instance | BindingFlags.Public;
    private const BindingFlags general_flags                 = BindingFlags.Public   | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private Type getNonPublicType() {
        return typeof(NonPublicClass);
    }

    private Type getPublicType() {
        return typeof(PublicClass);
    }

    [Benchmark]
    public FieldInfo Exact_Field_NonPublic() {
        return getNonPublicType().GetField("privateField", type_members_non_public_flags)!;
    }

    [Benchmark]
    public PropertyInfo Exact_Property_NonPublic() {
        return getNonPublicType().GetProperty("privateProperty", type_members_non_public_flags)!;
    }

    [Benchmark]
    public MethodInfo Exact_Method_NonPublic() {
        return getNonPublicType().GetMethod("privateMethod", type_members_non_public_flags)!;
    }

    [Benchmark]
    public FieldInfo Exact_Field_Public() {
        return getPublicType().GetField("publicField", type_members_public_flags)!;
    }

    [Benchmark]
    public PropertyInfo Exact_Property_Public() {
        return getPublicType().GetProperty("publicProperty", type_members_public_flags)!;
    }

    [Benchmark]
    public MethodInfo Exact_Method_Public() {
        return getPublicType().GetMethod("publicMethod", type_members_public_flags)!;
    }

    [Benchmark]
    public FieldInfo General_Field_NonPublic() {
        return getNonPublicType().GetField("privateField", general_flags)!;
    }

    [Benchmark]
    public PropertyInfo General_Property_NonPublic() {
        return getNonPublicType().GetProperty("privateProperty", general_flags)!;
    }

    [Benchmark]
    public MethodInfo General_Method_NonPublic() {
        return getNonPublicType().GetMethod("privateMethod", general_flags)!;
    }

    [Benchmark]
    public FieldInfo General_Field_Public() {
        return getPublicType().GetField("publicField", general_flags)!;
    }

    [Benchmark]
    public PropertyInfo General_Property_Public() {
        return getPublicType().GetProperty("publicProperty", general_flags)!;
    }

    [Benchmark]
    public MethodInfo General_Method_Public() {
        return getPublicType().GetMethod("publicMethod", general_flags)!;
    }
}
