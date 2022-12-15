using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using Xysstide.Reflection.Util.Extensions;

namespace Xysstide.Reflection;

partial class FastReflection {
    // OPTIMIZATION: Store functions by "type.TypeHandle.Value" instead of just "type" due to better GetHashCode() usage
	private static readonly ConcurrentDictionary<nint, ConcurrentDictionary<string, Func<object?, object?>>> getFieldFuncs = new();
	private static readonly ConcurrentDictionary<nint, ConcurrentDictionary<string, Action<object?, object?>>> setFieldFuncs = new();
	private static readonly ConcurrentDictionary<nint, ConcurrentDictionary<string, FieldInfo>> cachedFieldInfos = new();

	private static class FieldGenericDelegates<T> {
		public static bool retrieveUnloadingRegistered, assignUnloadingRegistered;
			
		public delegate object? RetrieveDelegate(T instance);
		public delegate void AssignDelegate(ref T instance, object? value);
			
		public static readonly ConcurrentDictionary<string, RetrieveDelegate> getFieldFuncs = new();
		public static readonly ConcurrentDictionary<string, AssignDelegate> setFieldFuncs = new();
	}

	public static object? RetrieveField(this Type type, string fieldName, object? instance) {
		Func<object?, object?> func = BuildRetrieveFieldDelegate(type, fieldName);

		return func(instance);
	}

	public static object? RetrieveField<T>(T instance, string fieldName) {
		FieldGenericDelegates<T>.RetrieveDelegate func = BuildGenericRetrieveFieldDelegate<T>(fieldName);

		return func(instance);
	}

	public static T? RetrieveField<T>(this Type type, string fieldName, object? instance) {
		try {
			return (T)type.RetrieveField(fieldName, instance)!;
		} catch (AmbiguousMatchException) {
			throw;
		} catch (InvalidCastException) {
			throw new InvalidCastException($"Could not cast field \"{type.GetSimplifiedGenericTypeName()}.{fieldName}\" to type \"{typeof(T).GetSimplifiedGenericTypeName()}\"");
		}
	}

	public static F? RetrieveField<T, F>(T instance, string fieldName) {
		try {
			return (F)RetrieveField(instance, fieldName)!;
		} catch (AmbiguousMatchException) {
			throw;
		} catch (InvalidCastException) {
			throw new InvalidCastException($"Could not cast field \"{typeof(T).GetSimplifiedGenericTypeName()}.{fieldName}\" to type \"{typeof(F).GetSimplifiedGenericTypeName()}\"");
		}
	}

	public static object? RetrieveStaticField(this Type type, string fieldName) => RetrieveField(type, fieldName, null);

	public static T? RetrieveStaticField<T>(this Type type, string fieldName) => RetrieveField<T>(type, fieldName, null);

	public static void AssignField(this Type type, string fieldName, object? instance, object? value) {
		Action<object?, object?> func = BuildAssignFieldDelegate(type, fieldName);

		func(instance, value);
	}

	public static void AssignField<T>(ref T instance, string fieldName, object? value) {
		FieldGenericDelegates<T>.AssignDelegate func = BuildGenericAssignFieldDelegate<T>(fieldName);

		func(ref instance, value);
	}

	public static void AssignStaticField(this Type type, string fieldName, object? value) => AssignField(type, fieldName, null, value);

	private static FieldInfo GetField(Type type, string fieldName) {
		nint handle = type.TypeHandle.Value;

		if (cachedFieldInfos.TryGetValue(handle, out var fieldDict) && fieldDict.TryGetValue(fieldName, out FieldInfo? fieldInfo))
		return fieldInfo;

		fieldInfo = type.GetField(fieldName, AllFlags)!;

		if (fieldInfo is null)
			throw new ArgumentException($"Could not find field \"{fieldName}\" in type \"{type.GetSimplifiedGenericTypeName()}\"");

		if (!cachedFieldInfos.TryGetValue(handle, out fieldDict))
			cachedFieldInfos[handle] = fieldDict = new();

		fieldDict[fieldName] = fieldInfo;

		return fieldInfo;
	}

	private static Func<object?, object?> BuildRetrieveFieldDelegate(Type type, string fieldName) {

		nint handle = type.TypeHandle.Value;

		if (getFieldFuncs.TryGetValue(handle, out var funcDictionary) && funcDictionary.TryGetValue(fieldName, out var func))
			return func;

		FieldInfo fieldInfo = GetField(type, fieldName);

		string name = $"{typeof(FastReflection).FullName}.BuildRetrieveFieldDelegate<{type.GetSimplifiedGenericTypeName()}>.get_{fieldName}";
		DynamicMethod method = new(name, typeof(object), new[] { typeof(object) }, type, skipVisibility: true);

		ILGenerator il = method.GetILGenerator();

		Label afterNullCheck = il.DefineLabel();

		if (!fieldInfo.IsStatic) {
			il.Emit(Ldarg_0);
			il.Emit(Brtrue, afterNullCheck);
			il.Emit(Ldstr, "Cannot load an instance field from a null reference");
			il.Emit(Ldstr, "instance");
			il.Emit(Newobj, ArgumentException_ctor_string_string);
			il.Emit(Throw);

			il.MarkLabel(afterNullCheck);

			il.Emit(Ldarg_0);
			il.Emit(Unbox_Any, type);
			il.Emit(Ldfld, fieldInfo);
		} else {
			il.Emit(Ldarg_0);
			il.Emit(Brfalse, afterNullCheck);
			il.Emit(Ldstr, "Cannot load a static field from an object instance");
			il.Emit(Ldstr, "instance");
			il.Emit(Newobj, ArgumentException_ctor_string_string);
			il.Emit(Throw);

			il.MarkLabel(afterNullCheck);

			il.Emit(Ldsfld, fieldInfo);
		}

		if (fieldInfo.FieldType.IsValueType)
			il.Emit(Box, fieldInfo.FieldType);

		il.Emit(Ret);

		if (!getFieldFuncs.TryGetValue(handle, out funcDictionary)) {
			getFieldFuncs[handle] = funcDictionary = new();

			// Capture the parameter into a local
			nint h = handle;

			// TODO: Determine what to do with this?
			// ALCReflectionUnloader.OnUnload(type.Assembly, () => getFieldFuncs.Remove(h, out _));
		}

		funcDictionary[fieldName] = func = method.CreateDelegate<Func<object?, object>>();

		return func;
	}

	private static Action<object?, object?> BuildAssignFieldDelegate(Type type, string fieldName) {
		nint handle = type.TypeHandle.Value;

		if (setFieldFuncs.TryGetValue(handle, out var funcDictionary) && funcDictionary.TryGetValue(fieldName, out var func))
			return func;

		FieldInfo fieldInfo = GetField(type, fieldName);

		string name = $"{typeof(FastReflection).FullName}.BuildAssignFieldDelegate<{type.GetSimplifiedGenericTypeName()}>.set_{fieldName}";
		DynamicMethod method = new(name, null, new[] { typeof(object), typeof(object) }, type, skipVisibility: true);

		ILGenerator il = method.GetILGenerator();

		Label afterNullCheck = il.DefineLabel();

		if (!fieldInfo.IsStatic) {
			il.Emit(Ldarg_0);
			il.Emit(Brtrue, afterNullCheck);
			il.Emit(Ldstr, "Cannot assign a value to an instance field from a null reference");
			il.Emit(Ldstr, "instance");
			il.Emit(Newobj, ArgumentException_ctor_string_string);
			il.Emit(Throw);

			il.MarkLabel(afterNullCheck);

			if (type.IsValueType) {
				// Exit prematurely since assigning an object to a copy would be pointless anyway
				il.Emit(Ldstr, $"Cannot modify a field in a copied value type instance.  Use \"{nameof(FastReflection)}.AssignField<T>(ref T, string, object)\" if you want to assign fields in a value type instance.");
				il.Emit(Newobj, ArgumentException_ctor_string);
				il.Emit(Throw);
				goto endOfDelegate;
			}

			il.Emit(Ldarg_0);
			il.Emit(Unbox_Any, type);
		} else {
			il.Emit(Ldarg_0);
			il.Emit(Brfalse, afterNullCheck);
			il.Emit(Ldstr, "Cannot assign a value to a static field from an object instance");
			il.Emit(Ldstr, "instance");
			il.Emit(Newobj, ArgumentException_ctor_string_string);
			il.Emit(Throw);

			il.MarkLabel(afterNullCheck);
		}

		il.Emit(Ldarg_1);
		il.Emit(Unbox_Any, fieldInfo.FieldType);

		if (!fieldInfo.IsStatic)
			il.Emit(Stfld, fieldInfo);
		else
			il.Emit(Stsfld, fieldInfo);

		endOfDelegate:

		il.Emit(Ret);

		if (!setFieldFuncs.TryGetValue(handle, out funcDictionary)) {
			setFieldFuncs[handle] = funcDictionary = new();

			// Capture the parameter into a local
			nint h = handle;

			// TODO: Determine what to do with this?
			// ALCReflectionUnloader.OnUnload(type.Assembly, () => setFieldFuncs.Remove(h, out _));
		}

		return funcDictionary[fieldName] = method.CreateDelegate<Action<object?, object?>>();
	}

	private static FieldGenericDelegates<T>.RetrieveDelegate BuildGenericRetrieveFieldDelegate<T>(string fieldName) {
		Type type = typeof(T);

		if (FieldGenericDelegates<T>.getFieldFuncs.TryGetValue(fieldName, out var func))
			return func;

		FieldInfo fieldInfo = GetField(type, fieldName);

		string name = $"{typeof(FastReflection).FullName}.BuildGenericRetrieveFieldDelegate<{type.GetSimplifiedGenericTypeName()}>.get_{fieldName}";
		DynamicMethod method = new(name, typeof(object), new[] { type.MakeByRefType() }, type, skipVisibility: true);

		ILGenerator il = method.GetILGenerator();

		il.Emit(Ldarg_0);
		il.Emit(Ldfld, fieldInfo);

		if (fieldInfo.FieldType.IsValueType)
			il.Emit(Box, fieldInfo.FieldType);

		il.Emit(Ret);

		if (!FieldGenericDelegates<T>.retrieveUnloadingRegistered) {
			FieldGenericDelegates<T>.retrieveUnloadingRegistered = true;

			// TODO: Determine what to do with this?
			// ALCReflectionUnloader.OnUnload(type.Assembly, FieldGenericDelegates<T>.getFieldFuncs.Clear);
		}

		return FieldGenericDelegates<T>.getFieldFuncs[fieldName] = method.CreateDelegate<FieldGenericDelegates<T>.RetrieveDelegate>();
	}

	private static FieldGenericDelegates<T>.AssignDelegate BuildGenericAssignFieldDelegate<T>(string fieldName) {
		Type type = typeof(T);

		if (FieldGenericDelegates<T>.setFieldFuncs.TryGetValue(fieldName, out var func))
			return func;

		FieldInfo fieldInfo = GetField(type, fieldName);

		string name = $"{typeof(FastReflection).FullName}.BuildGenericAssignFieldDelegate<{type.GetSimplifiedGenericTypeName()}>.set_{fieldName}";
		DynamicMethod method = new(name, null, new[] { type.MakeByRefType(), typeof(object) }, type, skipVisibility: true);

		ILGenerator il = method.GetILGenerator();

		il.Emit(Ldarg_0);
		il.Emit(Ldarg_1);
		il.Emit(Unbox_Any, fieldInfo.FieldType);
		il.Emit(Stfld, fieldInfo);
		il.Emit(Ret);

		if (!FieldGenericDelegates<T>.assignUnloadingRegistered) {
			FieldGenericDelegates<T>.assignUnloadingRegistered = true;

			// TODO: Determine what to do with this?
			// ALCReflectionUnloader.OnUnload(type.Assembly, FieldGenericDelegates<T>.setFieldFuncs.Clear);
		}

		return FieldGenericDelegates<T>.setFieldFuncs[fieldName] = method.CreateDelegate<FieldGenericDelegates<T>.AssignDelegate>();
	}
}
