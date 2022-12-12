using System.Reflection;

namespace Xysstide.Reflection.Util;

/// <summary>
///     Constant and read-only values used to aid with reflection.
/// </summary>
public static class ReflectionConstants
{
    /// <summary>
    ///     <see cref="BindingFlags"/> that can access public, non-public, instance, and static members.
    /// </summary>
    public const BindingFlags UNIVERSAL_FLAGS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
}
