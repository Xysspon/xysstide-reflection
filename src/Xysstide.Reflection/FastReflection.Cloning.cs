using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Xysstide.Reflection.Util;
using Xysstide.Reflection.Util.Extensions;

namespace Xysstide.Reflection;

partial class FastReflection
{
    private static readonly Dictionary<Type, Func<object?, object?>> clone_funcs              = new();
    private static readonly Dictionary<Type, FieldInfo[]>            cached_field_info_arrays = new();

    [ThreadStatic, Obsolete($"Use ${nameof(DETECTED_OBJECT_INSTANCES)} instead, this is used for thread-static lazy-initialization.")]
    private static HashSet<object>? detected_object_instances;

#pragma warning disable CS0618 // Okay here because this property is responsible for lazy-initialization.
    internal static HashSet<object> DETECTED_OBJECT_INSTANCES => detected_object_instances ??= new(ReferenceEqualityComparer.Instance);
#pragma warning restore CS0618

    [ThreadStatic]
    private static int nestingLevel;

    // TODO: Use shortened reflection where possible.

    /*private static readonly MethodInfo deep_clone =
        typeof(FastReflection).GetMethod(nameof(DeepClone), UNIVERSAL_FLAGS, new [] { typeof(Type), typeof(object) })!;*/

    /// <summary>
    ///     Performs a recursive deep cloning algorithm to clone <paramref name="instance"/>
    /// </summary>
    /// <param name="type">The type of the instance to clone</param>
    /// <param name="instance">The instance to clone</param>
    /// <returns>The cloned instance</returns>
    /// <remarks>
    ///     <b>NOTE:</b>  This algorithm has a few caveats that should be kept in mind: <br />
    ///     <list type="number">
    ///         <item>
    ///         <   see langword="null"/> values in members are copied directly.
    ///         </item>
    ///         <item>
    ///             Any <see langword="unmanaged"/> members (meaning, types whose members do not contain any references) are copied directly.
    ///         </item>
    ///         <item>
    ///             <see cref="Array"/> members will have their lengths preserved and each array element is deep cloned individually.
    ///         </item>
    ///         <item>
    ///             <see cref="string"/> members are copied directly.
    ///         </item>
    ///         <item>
    ///             Pointer and <see langword="ref struct"/> members are set to <see langword="default"/>.
    ///         </item>
    ///         <item>
    ///             Members which cause recursion (e.g. obj1.field → obj2, obj2.field → obj1) may or may not be copied directly. <br />
    ///             Such members should be checked manually after deep cloning.
    ///         </item>
    ///     </list>
    /// </remarks>
    /// <exception cref="ArgumentException"/>
    [return: NotNullIfNotNull("instance")]
    public static object? DeepClone(this Type type, object? instance) {
        if (type.IsAbstract || (type.IsAbstract && type.IsSealed) || type.IsInterface || type.IsGenericTypeDefinition)
            throw new ArgumentException($"Type \"{type.GetSimplifiedGenericTypeName()}\" cannot be used with this method.");

        if (instance is null) return null;

        if (instance.GetType().IsClass) DETECTED_OBJECT_INSTANCES.Add(instance);

        var func = buildDeepCloneDelegate(type);

        nestingLevel++;
        object obj = func(instance)!;
        nestingLevel--;

        // Only clear the dictionary if this DeepClone method wasn't called within another DeepClone method
        if (nestingLevel == 0) DETECTED_OBJECT_INSTANCES.Clear();

        return obj;
    }

    /// <inheritdoc cref="DeepClone(Type, object?)"/>
    [return: NotNullIfNotNull("instance")]
    public static T? DeepClone<T>(T? instance) {
        return (T?)typeof(T).DeepClone(instance);
    }

    /*private static readonly MethodInfo get_uninitialized_object =
        typeof(RuntimeHelpers).GetMethod("GetUninitializedObject", BindingFlags.Public | BindingFlags.Static)!;*/

    // private static readonly MethodInfo get_type = typeof(object).GetMethod("GetType", BindingFlags.Public | BindingFlags.Instance)!;

    private static readonly MethodInfo clone_object =
        typeof(FastReflection).GetMethod(nameof(cloneObject), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo default_obj =
        typeof(FastReflection).GetMethod(nameof(defaultObj), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo uninitialized_object =
        typeof(FastReflection).GetMethod(nameof(uninitializedObject), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo unsafe_init =
        typeof(FastReflection).GetMethod(nameof(unsafeInit), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo activator_object =
        typeof(FastReflection).GetMethod(nameof(activatorObject), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo clone_array =
        typeof(FastReflection).GetMethod(nameof(cloneArray), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo object_defined_already =
        typeof(FastReflection).GetMethod(nameof(objectDefinedAlready), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static Func<object?, object?> buildDeepCloneDelegate(Type type) {
        if (clone_funcs.TryGetValue(type, out var func)) return func;

        string name   = $"{typeof(FastReflection).FullName}.BuildDeepCloneDelegate<{type.GetSimplifiedGenericTypeName()}>.DeepClone";
        var    method = new DynamicMethod(name, typeof(object), new[] { typeof(object) }, typeof(FastReflection).Module, skipVisibility: true);
        var    il     = method.GetILGenerator();

        // Optimization:  If the type is a struct and every single member (and their members) are unmanaged,
        // then this object is also unmanaged and can just be copied directly.
        // This check will also cover the primitive types.  Nice!
        bool hasRefs = buildTypeHasRefsDelegate(type).Invoke();

        if (!hasRefs) {
            il.Emit(Ldarg_0);
            il.Emit(Ret);
            goto skipCloneLocalReturn;
        }

        // Bug/Optimization Fix:  If the type is an array, then execute the CloneArray helper
        if (typeof(Array).IsAssignableFrom(type)) {
            il.Emit(Ldarg_0);
            il.Emit(Unbox_Any, type);
            il.Emit(Call,      clone_array.MakeGenericMethod(type.GetElementType()!));
            il.Emit(Box,       type);
            il.Emit(Ret);
            goto skipCloneLocalReturn;
        }

        // Bug/Optimization Fix:  If the type is a string, then just return the string.  Copying the fields doesn't suffice
        if (type == typeof(string)) {
            il.Emit(Ldarg_0);
            il.Emit(Ret);
            goto skipCloneLocalReturn;
        }

        // Optimization:  If the argument is null, then the clone will also be null
        var afterNullCheck = il.DefineLabel();
        il.Emit(Ldarg_0);
        il.Emit(Ldnull);
        il.Emit(Bne_Un, afterNullCheck);
        il.Emit(Ldnull);
        il.Emit(Ret);
        il.MarkLabel(afterNullCheck);

        // Bug/Optimization Fix:  If an object was already cloned, just return that object in order to prevent infinite recursion
        var afterRecursionCheck = il.DefineLabel();
        il.Emit(Ldarg_0);
        il.Emit(Call,    object_defined_already);
        il.Emit(Brfalse, afterRecursionCheck);
        il.Emit(Ldarg_0);
        il.Emit(Ret);
        il.MarkLabel(afterRecursionCheck);

        var obj           = il.DeclareLocal(type);
        var clone         = il.DeclareLocal(type);
        var uninitSuccess = il.DeclareLocal(typeof(bool));

        // Copy the argument to the local
        il.Emit(Ldarg_0);
        il.Emit(Unbox_Any, type);
        il.Emit(Stloc,     obj);

        // Initialize the clone
        // If initializing the clone fails via "UninitializedObject<T>", then bail
        var skipBailDueToInvalidType = il.DefineLabel();
        il.Emit(Ldloca, uninitSuccess);
        il.Emit(Call,   uninitialized_object.MakeGenericMethod(type));
        il.Emit(Stloc,  clone);
        il.Emit(Ldloc,  uninitSuccess);
        il.Emit(Brtrue, skipBailDueToInvalidType);
        il.Emit(Ldloc,  clone);
        il.Emit(Ret);
        il.MarkLabel(skipBailDueToInvalidType);

        // Type has at least one reference type somewhere...  everything needs to be cloned manually
        if (!cached_field_info_arrays.TryGetValue(type, out var fields))
            cached_field_info_arrays[type] = fields = type.GetFields(UNIVERSAL_FLAGS & (~BindingFlags.Static)).FastWhere(f => !f.IsStatic && !f.IsLiteral);

        // Do the mario
        var temp = il.DeclareLocal(typeof(object));

        foreach (var field in fields) {
            il.Emit(type.IsValueType ? Ldloca : Ldloc, obj);
            il.Emit(Ldfld,                             field);
            il.Emit(Box,                               field.FieldType);

            il.Emit(Call, clone_object);

            il.Emit(Stloc, temp);

            il.Emit(type.IsValueType ? Ldloca : Ldloc, clone);
            il.Emit(Ldloc,                             temp);

            if (field.FieldType.IsValueType) {
                var afterFieldNullCheck = il.DefineLabel();
                var fieldAssignment     = il.DefineLabel();

                il.Emit(Isinst,  field.FieldType);
                il.Emit(Brfalse, afterFieldNullCheck);

                il.Emit(Ldloc,     temp);
                il.Emit(Unbox_Any, field.FieldType);
                il.Emit(Br,        fieldAssignment);
                il.MarkLabel(afterFieldNullCheck);

                // Return value was null.  Use "DefaultObj<T>()" to get a default object per type
                il.Emit(Call, default_obj.MakeGenericMethod(field.FieldType));
                il.MarkLabel(fieldAssignment);
            } else {
                il.Emit(Isinst, field.FieldType);
            }

            il.Emit(Stfld, field);
        }

        // If no fields are present, then an uninitialized object is the same as a cloned object, so this should be fine
        il.Emit(Ldloc, clone);
        il.Emit(Box,   type);
        il.Emit(Ret);

    skipCloneLocalReturn:
        return clone_funcs[type] = /*func =*/ method.CreateDelegate<Func<object?, object?>>();
    }

    private static readonly Dictionary<Type, Func<bool>> type_has_refs = new();

    private static Func<bool> buildTypeHasRefsDelegate(Type type) {
        if (type_has_refs.TryGetValue(type, out var func)) return func;

        string name   = $"{typeof(FastReflection).FullName}.BuildTypeIsUnmanagedDelegate<{type.GetSimplifiedGenericTypeName()}>";
        var    method = new DynamicMethod(name, typeof(bool), null, typeof(FastReflection).Module, skipVisibility: true);
        var    il     = method.GetILGenerator();

        il.Emit(Ldsfld,
                typeof(TypeInfo<>).MakeGenericType(type).GetField(nameof(TypeInfo<object>.IS_REF_OR_HAS_REFS), BindingFlags.Public | BindingFlags.Static)!);
        il.Emit(Ret);

        return type_has_refs[type] = /*func =*/ method.CreateDelegate<Func<bool>>();
    }

    private static bool objectDefinedAlready(object? obj) {
        if (obj is null || !buildTypeHasRefsDelegate(obj.GetType()).Invoke()) return false;

        return DETECTED_OBJECT_INSTANCES.Contains(obj);
    }

    private static object? cloneObject(object? obj) {
        return obj?.GetType().DeepClone(obj);
    }

    private static T?[]? cloneArray<T>(T[]? array) {
        if (array is null) return null;

        T?[] ret = new T[array.Length];

        for (int i = 0; i < array.Length; i++) ret[i] = (T?)cloneObject(array[i]);

        return ret;
    }

    private static T? defaultObj<T>() {
        return default;
    }

    private static T? uninitializedObject<T>(out bool success) {
        // Types are manually specified here based on the CLR source code
        // https://github.com/dotnet/runtime/blob/4cbe6f99d23e04c56a89251d49de1b0f14000427/src/coreclr/vm/reflectioninvocation.cpp#L1617
        // https://github.com/dotnet/runtime/blob/4cbe6f99d23e04c56a89251d49de1b0f14000427/src/coreclr/vm/reflectioninvocation.cpp#L1865
        var check = typeof(T);
        success = true;

        // Don't allow void
        if (check == typeof(void)) throw new InvalidGenericTypeException("void");

        // Don't allow generic variables (e.g., the 'T' from List<T>) or open generic types (List<>)
        if (check.IsGenericTypeParameter || check.IsGenericTypeDefinition) throw new InvalidGenericTypeException(check);

        // Don't allow arrays, pointers, byrefs, or function pointers
        if (check.IsPointer) {
            success = false;
            return default;
        }

        // Don't allow ref structs
        if (check.IsByRefLike) {
            success = false;
            return default;
        }

        // Don't allow abstract classes or interface types
        if (check.IsAbstract) {
            success = false;
            return default;
        }

        if (check.IsByRef) return unsafeInit<T>();

        // Don't allow creating instances of delegates
        if (typeof(Delegate).IsAssignableFrom(check)) return unsafeInit<T>();

        // Don't allow string or string-like (variable length) types.
        if (typeHasComponentSize<T>()) return (T?)activatorObject(typeof(T));

        // The CLR source includes the following check, but "__Canon" has no meaning in C# land so it's probably safe to ignore
        /*
        // Don't allow generics instantiated over __Canon
        if (pMT->IsSharedByGenericInstantiations())
        {
            COMPlusThrow(kNotSupportedException, W("NotSupported_Type"));
        }
        */

        // Yet another check from the CLR source that wouldn't make sense in C# land
        /*
        // Also do not allow allocation of uninitialized RCWs (COM objects).
        if (pMT->IsComObjectType())
            COMPlusThrow(kNotSupportedException, W("NotSupported_ManagedActivation"));
        */

        // All of the checks have passed.  Make the object
        return getUninitializedObject<T>();
    }

    // Prevent calling the static ctor too early
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool typeHasComponentSize<T>() {
        // For some reason, just checking HasComponentSize won't work for strings... so they need to be manually specified
        return UnsafeHelper<T>.HAS_COMPONENT_SIZE || typeof(T) == typeof(string) || typeof(Array).IsAssignableFrom(typeof(T));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static T? getUninitializedObject<T>() {
        return (T?)RuntimeHelpers.GetUninitializedObject(typeof(T));
    }

    private static T unsafeInit<T>() {
        Unsafe.SkipInit(out T value);
        return value;
    }

    private static object? activatorObject(Type type) {
        if (type == typeof(string)) return string.Empty;

        if (type.IsValueType || type.GetConstructor(Type.EmptyTypes) is not null) return Activator.CreateInstance(type);

        return buildActivatorDelegate(type).Invoke();
    }

    private static readonly Dictionary<Type, Func<object>> activator_object_delegate = new();

    private static Func<object> buildActivatorDelegate(Type type) {
        if (activator_object_delegate.TryGetValue(type, out var func)) return func;

        string name   = $"{typeof(FastReflection).FullName}.BuildActivatorDelegate<{type.GetSimplifiedGenericTypeName()}>";
        var    method = new DynamicMethod(name, typeof(object), null, typeof(FastReflection).Module, skipVisibility: true);
        var    il     = method.GetILGenerator();

        il.Emit(Call, unsafe_init.MakeGenericMethod(type));
        il.Emit(Box,  type);
        il.Emit(Ret);

        return activator_object_delegate[type] = /*func =*/ method.CreateDelegate<Func<object>>();
    }

    private static class TypeInfo<T>
    {
        public static readonly bool IS_REF_OR_HAS_REFS = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
    }

    // The following lines contain a collection of methods/data/etc. to make "UninitializedObject<T>()" work in line with the internal CLR code
    private static readonly MethodInfo RuntimeHelpers_GetMethodTable =
        typeof(RuntimeHelpers).GetMethod("GetMethodTable", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly Type       Type_MethodTable       = Type.GetType("System.Runtime.CompilerServices.MethodTable")!;
    private static readonly MethodInfo Type_GetTypeFromHandle = typeof(Type).GetMethod("GetTypeFromHandle", BindingFlags.Public | BindingFlags.Static)!;
    private static readonly MethodInfo GC_KeepAlive           = typeof(GC).GetMethod("KeepAlive", BindingFlags.Public           | BindingFlags.Static)!;

    private static readonly MethodInfo FastReflection_RetrieveField_T = typeof(FastReflection).GetMethod(
        nameof(RetrieveField),
        1,
        BindingFlags.Public | BindingFlags.Static,
        null,
        new [] { typeof(Type), typeof(string), typeof(object) },
        null)!;

    private static readonly MethodInfo FastReflection_RetrieveField_uint = FastReflection_RetrieveField_T.MakeGenericMethod(typeof(uint));

    private static class UnsafeHelper<T>
    {
        private const uint enum_flag_HasComponentSize = 0x80000000;

        // ReSharper disable once StaticMemberInGenericType
        public static readonly bool HAS_COMPONENT_SIZE = BuildHasComponentSizeDelegate().Invoke();

        private static Func<bool> BuildHasComponentSizeDelegate() {
            string name =
                $"{typeof(FastReflection).FullName}/{nameof(UnsafeHelper<T>)}<{typeof(T).GetSimplifiedGenericTypeName()}>.BuildHasComponentSizeDelegate";
            DynamicMethod method = new(name, typeof(bool), null, typeof(FastReflection).Module, skipVisibility: true);

            var il  = method.GetILGenerator();
            var obj = il.DeclareLocal(typeof(object));

            // Load the type and field name for later use in FastReflection.RetrieveField
            il.Emit(Ldtoken, Type_MethodTable);
            il.Emit(Call,    Type_GetTypeFromHandle);

            il.Emit(Ldstr, "Flags");

            // Making a default object should be fine since this delegate is only used once
            il.Emit(Ldtoken, typeof(T));
            il.Emit(Call,    Type_GetTypeFromHandle);
            il.Emit(Call,    activator_object);
            il.Emit(Stloc,   obj);
            il.Emit(Ldloc,   obj);
            // Get the method table
            il.Emit(Call,  RuntimeHelpers_GetMethodTable);
            il.Emit(Ldobj, Type_MethodTable);
            il.Emit(Box,   Type_MethodTable);

            // Extract the field
            il.Emit(Call, FastReflection_RetrieveField_uint);

            // Unbox the value and perform the arithmetic:
            // (Flags & enum_flag_HasComponentSize) != 0
            il.Emit(Ldc_I4, unchecked ((int)enum_flag_HasComponentSize));
            il.Emit(And);

            // Compare the result to 0 and make the delegate return true if they aren't equal
            il.Emit(Ldc_I4_0);
            il.Emit(Cgt_Un);

            // Ensure that the object that the method table was retrieved from can't be destroyed too early
            il.Emit(Ldloc, obj);
            il.Emit(Call,  GC_KeepAlive);

            il.Emit(Ret);

            return method.CreateDelegate<Func<bool>>();
        }
    }
}
