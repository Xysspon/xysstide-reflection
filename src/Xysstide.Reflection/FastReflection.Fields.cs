using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using Xysstide.Reflection.Util.Extensions;

namespace Xysstide.Reflection;

partial class FastReflection
{
    // OPTIMIZATION: Store functions by "type.TypeHandle.Value" instead of just "type" due to better GetHashCode() usage
    private static readonly ConcurrentDictionary<nint, ConcurrentDictionary<string, Func<object?, object?>>>   get_field_funcs    = new();
    private static readonly ConcurrentDictionary<nint, ConcurrentDictionary<string, Action<object?, object?>>> set_field_funcs    = new();
    private static readonly ConcurrentDictionary<nint, ConcurrentDictionary<string, FieldInfo>>                cached_field_infos = new();

    private static class FieldGenericDelegates<T>
    {
        // ReSharper disable once StaticMemberInGenericType
        public static bool RetrieveUnloadingRegistered { get; set; }

        // ReSharper disable once StaticMemberInGenericType
        public static bool AssignUnloadingRegistered { get; set; }

        public delegate object? RetrieveDelegate(T   instance);
        public delegate void    AssignDelegate(ref T instance, object? value);

        public static ConcurrentDictionary<string, RetrieveDelegate> GetFieldFuncs { get; } = new();

        public static readonly ConcurrentDictionary<string, AssignDelegate> SetFieldFuncs = new();
    }

    public static object? RetrieveField(this Type type, string fieldName, object? instance) {
        var func = buildRetrieveFieldDelegate(type, fieldName);
        return func(instance);
    }

    public static object? RetrieveField<T>(T instance, string fieldName) {
        var func = buildGenericRetrieveFieldDelegate<T>(fieldName);
        return func(instance);
    }

    public static T? RetrieveField<T>(this Type type, string fieldName, object? instance) {
        try {
            return (T?) type.RetrieveField(fieldName, instance);
        }
        // ReSharper disable once RedundantCatchClause
        catch (AmbiguousMatchException) {
            throw;
        }
        catch (InvalidCastException) {
            throw new InvalidCastException(
                $"Could not cast field \"{type.GetSimplifiedGenericTypeName()}.{fieldName}\" to type \"{typeof(T).GetSimplifiedGenericTypeName()}\""
            );
        }
    }

    public static F? RetrieveField<T, F>(T instance, string fieldName) {
        try {
            return (F?) RetrieveField(instance, fieldName);
        }
        // ReSharper disable once RedundantCatchClause
        catch (AmbiguousMatchException) {
            throw;
        }
        catch (InvalidCastException) {
            throw new InvalidCastException(
                $"Could not cast field \"{typeof(T).GetSimplifiedGenericTypeName()}.{fieldName}\" to type \"{typeof(F).GetSimplifiedGenericTypeName()}\""
            );
        }
    }

    public static object? RetrieveStaticField(this Type type, string fieldName) => RetrieveField(type, fieldName, null);

    public static T? RetrieveStaticField<T>(this Type type, string fieldName) => RetrieveField<T>(type, fieldName, null);

    public static void AssignField(this Type type, string fieldName, object? instance, object? value) {
        var func = buildAssignFieldDelegate(type, fieldName);
        func(instance, value);
    }

    public static void AssignField<T>(ref T instance, string fieldName, object? value) {
        var func = buildGenericAssignFieldDelegate<T>(fieldName);
        func(ref instance, value);
    }

    public static void AssignStaticField(this Type type, string fieldName, object? value) => AssignField(type, fieldName, null, value);

    private static FieldInfo getField(Type type, string fieldName) {
        nint handle = type.TypeHandle.Value;

        if (cached_field_infos.TryGetValue(handle, out var fieldDict) && fieldDict.TryGetValue(fieldName, out var fieldInfo)) return fieldInfo;

        fieldInfo = type.GetField(fieldName, UNIVERSAL_FLAGS);
        if (fieldInfo is null) throw new ArgumentException($"Could not find field \"{fieldName}\" in type \"{type.GetSimplifiedGenericTypeName()}\"");

        if (!cached_field_infos.TryGetValue(handle, out fieldDict)) cached_field_infos[handle] = fieldDict = new ConcurrentDictionary<string, FieldInfo>();
        return fieldDict[fieldName] = fieldInfo;
    }

    private static Func<object?, object?> buildRetrieveFieldDelegate(Type type, string fieldName) {
        nint handle = type.TypeHandle.Value;

        if (get_field_funcs.TryGetValue(handle, out var funcDictionary) && funcDictionary.TryGetValue(fieldName, out var func)) return func;

        var    fieldInfo      = getField(type, fieldName);
        string name           = $"{typeof(FastReflection).FullName}.buildRetrieveFieldDelegate<{type.GetSimplifiedGenericTypeName()}>.get_{fieldName}";
        var    method         = new DynamicMethod(name, typeof(object), new[] { typeof(object) }, type, skipVisibility: true);
        var    il             = method.GetILGenerator();
        var    afterNullCheck = il.DefineLabel();

        if (!fieldInfo.IsStatic) {
            il.Emit(Ldarg_0);
            il.Emit(Brtrue, afterNullCheck);
            il.Emit(Ldstr,  "Cannot load an instance field from a null reference");
            il.Emit(Ldstr,  "instance");
            il.Emit(Newobj, arg_ex_ctor_string_string);
            il.Emit(Throw);

            il.MarkLabel(afterNullCheck);

            il.Emit(Ldarg_0);
            il.Emit(Unbox_Any, type);
            il.Emit(Ldfld,     fieldInfo);
        } else {
            il.Emit(Ldarg_0);
            il.Emit(Brfalse, afterNullCheck);
            il.Emit(Ldstr,   "Cannot load a static field from an object instance");
            il.Emit(Ldstr,   "instance");
            il.Emit(Newobj,  arg_ex_ctor_string_string);
            il.Emit(Throw);

            il.MarkLabel(afterNullCheck);

            il.Emit(Ldsfld, fieldInfo);
        }

        if (fieldInfo.FieldType.IsValueType) il.Emit(Box, fieldInfo.FieldType);

        il.Emit(Ret);

        if (!get_field_funcs.TryGetValue(handle, out funcDictionary)) {
            get_field_funcs[handle] = funcDictionary = new();

            // Capture the parameter into a local
            // TODO: Determine what to do with this?
            /*nint h = handle;
            ALCReflectionUnloader.OnUnload(type.Assembly, () => getFieldFuncs.Remove(h, out _));*/
        }

        return funcDictionary[fieldName] = /*func =*/ method.CreateDelegate<Func<object?, object>>();
    }

    private static Action<object?, object?> buildAssignFieldDelegate(Type type, string fieldName) {
        nint handle = type.TypeHandle.Value;

        if (set_field_funcs.TryGetValue(handle, out var funcDictionary) && funcDictionary.TryGetValue(fieldName, out var func)) return func;

        var    fieldInfo      = getField(type, fieldName);
        string name           = $"{typeof(FastReflection).FullName}.buildAssignFieldDelegate<{type.GetSimplifiedGenericTypeName()}>.set_{fieldName}";
        var    method         = new DynamicMethod(name, null, new[] { typeof(object), typeof(object) }, type, skipVisibility: true);
        var    il             = method.GetILGenerator();
        var    afterNullCheck = il.DefineLabel();

        if (!fieldInfo.IsStatic) {
            il.Emit(Ldarg_0);
            il.Emit(Brtrue, afterNullCheck);
            il.Emit(Ldstr,  "Cannot assign a value to an instance field from a null reference");
            il.Emit(Ldstr,  "instance");
            il.Emit(Newobj, arg_ex_ctor_string_string);
            il.Emit(Throw);

            il.MarkLabel(afterNullCheck);

            if (type.IsValueType) {
                // Exit prematurely since assigning an object to a copy would be pointless anyway
                il.Emit(Ldstr,
                        $"Cannot modify a field in a copied value type instance.  Use \"{nameof(FastReflection)}.AssignField<T>(ref T, string, object)\" if you want to assign fields in a value type instance."
                );
                il.Emit(Newobj, arg_ex_ctor_string);
                il.Emit(Throw);
                goto endOfDelegate;
            }

            il.Emit(Ldarg_0);
            il.Emit(Unbox_Any, type);
        } else {
            il.Emit(Ldarg_0);
            il.Emit(Brfalse, afterNullCheck);
            il.Emit(Ldstr,   "Cannot assign a value to a static field from an object instance");
            il.Emit(Ldstr,   "instance");
            il.Emit(Newobj,  arg_ex_ctor_string_string);
            il.Emit(Throw);

            il.MarkLabel(afterNullCheck);
        }

        il.Emit(Ldarg_1);
        il.Emit(Unbox_Any, fieldInfo.FieldType);

        il.Emit(!fieldInfo.IsStatic ? Stfld : Stsfld, fieldInfo);

    endOfDelegate:
        il.Emit(Ret);

        if (!set_field_funcs.TryGetValue(handle, out funcDictionary)) {
            set_field_funcs[handle] = funcDictionary = new();

            // Capture the parameter into a local
            // TODO: Determine what to do with this?
            /*nint h = handle;
            ALCReflectionUnloader.OnUnload(type.Assembly, () => setFieldFuncs.Remove(h, out _));*/
        }

        return funcDictionary[fieldName] = /*func =*/ method.CreateDelegate<Action<object?, object?>>();
    }

    private static FieldGenericDelegates<T>.RetrieveDelegate buildGenericRetrieveFieldDelegate<T>(string fieldName) {
        var type = typeof(T);

        if (FieldGenericDelegates<T>.GetFieldFuncs.TryGetValue(fieldName, out FieldGenericDelegates<T>.RetrieveDelegate? func)) return func;

        var    fieldInfo = getField(type, fieldName);
        string name      = $"{typeof(FastReflection).FullName}.buildGenericRetrieveFieldDelegate<{type.GetSimplifiedGenericTypeName()}>.get_{fieldName}";
        var    method    = new DynamicMethod(name, typeof(object), new[] { type.MakeByRefType() }, type, skipVisibility: true);
        var    il        = method.GetILGenerator();

        il.Emit(Ldarg_0);
        il.Emit(Ldfld, fieldInfo);

        if (fieldInfo.FieldType.IsValueType) il.Emit(Box, fieldInfo.FieldType);

        il.Emit(Ret);

        if (!FieldGenericDelegates<T>.RetrieveUnloadingRegistered) {
            FieldGenericDelegates<T>.RetrieveUnloadingRegistered = true;

            // TODO: Determine what to do with this?
            //ALCReflectionUnloader.OnUnload(type.Assembly, FieldGenericDelegates<T>.getFieldFuncs.Clear);
        }

        return FieldGenericDelegates<T>.GetFieldFuncs[fieldName] = /*func =*/ method.CreateDelegate<FieldGenericDelegates<T>.RetrieveDelegate>();
    }

    private static FieldGenericDelegates<T>.AssignDelegate buildGenericAssignFieldDelegate<T>(string fieldName) {
        var type = typeof(T);

        if (FieldGenericDelegates<T>.SetFieldFuncs.TryGetValue(fieldName, out FieldGenericDelegates<T>.AssignDelegate? func)) return func;

        var    fieldInfo = getField(type, fieldName);
        string name      = $"{typeof(FastReflection).FullName}.buildGenericAssignFieldDelegate<{type.GetSimplifiedGenericTypeName()}>.set_{fieldName}";
        var    method    = new DynamicMethod(name, null, new[] { type.MakeByRefType(), typeof(object) }, type, skipVisibility: true);
        var    il        = method.GetILGenerator();

        il.Emit(Ldarg_0);
        il.Emit(Ldarg_1);
        il.Emit(Unbox_Any, fieldInfo.FieldType);
        il.Emit(Stfld,     fieldInfo);
        il.Emit(Ret);

        if (!FieldGenericDelegates<T>.AssignUnloadingRegistered) {
            FieldGenericDelegates<T>.AssignUnloadingRegistered = true;

            // TODO: Determine what to do with this?
            // ALCReflectionUnloader.OnUnload(type.Assembly, FieldGenericDelegates<T>.setFieldFuncs.Clear);
        }

        return FieldGenericDelegates<T>.SetFieldFuncs[fieldName] = /*func =*/ method.CreateDelegate<FieldGenericDelegates<T>.AssignDelegate>();
    }
}
