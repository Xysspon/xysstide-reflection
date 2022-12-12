using System;
using System.Reflection;

namespace Xysstide.Reflection;

/// <summary>
///     Utilities specializing in faster access of hidden members within types.
/// </summary>
public static partial class FastReflection
{
    private static readonly ConstructorInfo arg_ex_ctor_string        = typeof(ArgumentException).GetConstructor(new [] { typeof(string) })!;
    private static readonly ConstructorInfo arg_ex_ctor_string_string = typeof(ArgumentException).GetConstructor(new [] { typeof(string), typeof(string) })!;
}
