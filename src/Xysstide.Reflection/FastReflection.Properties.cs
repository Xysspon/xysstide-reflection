using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using Xysstide.Reflection.Util.Extensions;

namespace Xysstide.Reflection;

partial class FastReflection
{
	// OPTIMIZATION: Store functions by "type.TypeHandle.Value" instead of just "type" due to better GetHashCode() usage
	private static readonly ConcurrentDictionary<nint, ConcurrentDictionary<string, Func<object?, object?>>> getPropertyFuncs = new();
	private static readonly ConcurrentDictionary<nint, ConcurrentDictionary<string, Action<object?, object?>>> setPropertyFuncs = new();
	private static readonly ConcurrentDictionary<nint, ConcurrentDictionary<string, PropertyInfo>> cachedPropertyInfos = new();

	private static class PropertyGenericDelegates<T>
	{
		// ReSharper disable once StaticMemberInGenericType
		public static bool RetrieveUnloadingRegistered { get; set; }

		// ReSharper disable once StaticMemberInGenericType
		public static bool AssignUnloadingRegistered { get; set; }

		public delegate object? RetrieveDelegate(T   instance);
		public delegate void AssignDelegate(ref T instance, object? value);

		public static ConcurrentDictionary<string, RetrieveDelegate> GetPropertyFuncs { get; } = new();

		public static readonly ConcurrentDictionary<string, AssignDelegate> SetPropertyFuncs = new();
	}

	public static object? RetrieveProperty(this Type type, string propertyName, object? instance) {
		var func = BuildRetrievePropertyDelegate(type, propertyName);
		return func(instance);
	}

	public static object? RetrieveProperty<T>(T instance, string propertyName) {
		var func = BuildGenericRetrievePropertyDelegate<T>(propertyName);
		return func(instance);
	}

	public static T? RetrieveProperty<T>(this Type type, string propertyName, object? instance) {
		try {
			return (T?) type.RetrieveProperty(propertyName, instance);
		} catch (AmbiguousMatchException) {
			throw;
		} catch (InvalidCastException) {
			throw new InvalidCastException($"Could not cast property \"{type.GetSimplifiedGenericTypeName()}.{propertyName}\" to type \"{typeof(T).GetSimplifiedGenericTypeName()}\"");
		}
	}

	public static F? RetrieveProperty<T, F>(T instance, string propertyName) {
		try {
			return (F?) RetrieveProperty(instance, propertyName);
		} catch (AmbiguousMatchException) {
			throw;
		} catch (InvalidCastException) {
			throw new InvalidCastException($"Could not cast property \"{typeof(T).GetSimplifiedGenericTypeName()}.{propertyName}\" to type \"{typeof(F).GetSimplifiedGenericTypeName()}\"");
		}
	}

	public static object? RetrieveStaticProperty(this Type type, string propertyName) => RetrieveProperty(type, propertyName, null);

	public static T? RetrieveStaticProperty<T>(this Type type, string propertyName) => RetrieveProperty<T>(type, propertyName, null);

	public static void AssignProperty(this Type type, string propertyName, object? instance, object? value) {
		var func = BuildAssignPropertyDelegate(type, propertyName);
		func(instance, value);
	}

	public static void AssignProperty<T>(ref T instance, string propertyName, object? value) {
		var func = BuildGenericAssignPropertyDelegate<T>(propertyName);
		func(ref instance, value);
	}

	public static void AssignStaticProperty(this Type type, string propertyName, object? value) => AssignProperty(type, propertyName, null, value);

	private static PropertyInfo GetProperty(Type type, string propertyName) {
		nint handle = type.TypeHandle.Value;

		if (cachedPropertyInfos.TryGetValue(handle, out var propertyDict) && propertyDict.TryGetValue(propertyName, out var propertyInfo))
			return propertyInfo;

		propertyInfo = type.GetProperty(propertyName, AllFlags);
		if (propertyInfo is null) throw new ArgumentException($"Could not find property \"{propertyName}\" in type \"{type.GetSimplifiedGenericTypeName()}\"");

		if (!cachedPropertyInfos.TryGetValue(handle, out propertyDict))
			cachedPropertyInfos[handle] = propertyDict = new ConcurrentDictionary<string, PropertyInfo>();
		
		return propertyDict[propertyName] = propertyInfo;
	}

	private static Func<object?, object?> BuildRetrievePropertyDelegate(Type type, string propertyName) {
		nint handle = type.TypeHandle.Value;

		if (getPropertyFuncs.TryGetValue(handle, out var funcDictionary) && funcDictionary.TryGetValue(propertyName, out var func))
			return func;

		var propertyInfo = GetProperty(type, propertyName);
		string name = $"{typeof(FastReflection).FullName}.BuildRetrievePropertyDelegate<{type.GetSimplifiedGenericTypeName()}>.get_{propertyName}";
		var method = new DynamicMethod(name, typeof(object), new[] { typeof(object) }, type, skipVisibility: true);
		var il = method.GetILGenerator();
		var afterNullCheck = il.DefineLabel();

		if (propertyInfo.GetMethod is null) throw new ArgumentException($"Property \"{propertyName}\" does not have a getter!");

		if (!propertyInfo.GetMethod.IsStatic) {
			il.Emit(Ldarg_0);
			il.Emit(Brtrue, afterNullCheck);
			il.Emit(Ldstr, "Cannot load an instance property from a null reference");
			il.Emit(Ldstr, "instance");
			il.Emit(Newobj, ArgumentException_ctor_string_string);
			il.Emit(Throw);

			il.MarkLabel(afterNullCheck);

			il.Emit(Ldarg_0);
			il.Emit(Unbox_Any, type);
			il.Emit(Callvirt, propertyInfo.GetMethod);
		} else {
			il.Emit(Ldarg_0);
			il.Emit(Brfalse, afterNullCheck);
			il.Emit(Ldstr, "Cannot load a static property from an object instance");
			il.Emit(Ldstr, "instance");
			il.Emit(Newobj, ArgumentException_ctor_string_string);
			il.Emit(Throw);

			il.MarkLabel(afterNullCheck);

			il.Emit(Call, propertyInfo.GetMethod);
		}

		if (propertyInfo.PropertyType.IsValueType) il.Emit(Box, propertyInfo.PropertyType);

		il.Emit(Ret);

		if (!getPropertyFuncs.TryGetValue(handle, out funcDictionary)) {
			getPropertyFuncs[handle] = funcDictionary = new ConcurrentDictionary<string, Func<object?, object?>>();

			// Capture the parameter into a local
			nint h = handle;
			
			// TODO: Determine what to do with this?
			// ALCReflectionUnloader.OnUnload(type.Assembly, () => getPropertyFuncs.Remove(h, out _));
		}

		return funcDictionary[propertyName] = method.CreateDelegate<Func<object?, object>>();
	}

	private static Action<object?, object?> BuildAssignPropertyDelegate(Type type, string propertyName) {
		nint handle = type.TypeHandle.Value;

		if (setPropertyFuncs.TryGetValue(handle, out var funcDictionary) && funcDictionary.TryGetValue(propertyName, out var func))
			return func;

		var propertyInfo = GetProperty(type, propertyName);
		string name = $"{typeof(FastReflection).FullName}.BuildAssignPropertyDelegate<{type.GetSimplifiedGenericTypeName()}>.set_{propertyName}";
		var method = new DynamicMethod(name, null, new[] { typeof(object), typeof(object) }, type, skipVisibility: true);
		var il = method.GetILGenerator();
		var afterNullCheck = il.DefineLabel();

		if (propertyInfo.SetMethod is null)
			throw new ArgumentException($"Property \"{propertyName}\" does not have a setter!");

		if (!propertyInfo.SetMethod.IsStatic) {
			il.Emit(Ldarg_0);
			il.Emit(Brtrue, afterNullCheck);
			il.Emit(Ldstr, "Cannot assign a value to an instance property from a null reference");
			il.Emit(Ldstr, "instance");
			il.Emit(Newobj, ArgumentException_ctor_string_string);
			il.Emit(Throw);

			il.MarkLabel(afterNullCheck);

			if (type.IsValueType) {
				// Exit prematurely since assigning an object to a copy would be pointless anyway
				il.Emit(Ldstr, $"Cannot modify a property in a copied value type instance.  Use \"{nameof(FastReflection)}.AssignProperty<T>(ref T, string, object)\" if you want to assign properties in a value type instance.");
				il.Emit(Newobj, ArgumentException_ctor_string);
				il.Emit(Throw);
				goto endOfDelegate;
			}

			il.Emit(Ldarg_0);
			il.Emit(Unbox_Any, type);
		} else {
			il.Emit(Ldarg_0);
			il.Emit(Brfalse, afterNullCheck);
			il.Emit(Ldstr, "Cannot assign a value to a static property from an object instance");
			il.Emit(Ldstr, "instance");
			il.Emit(Newobj, ArgumentException_ctor_string_string);
			il.Emit(Throw);

			il.MarkLabel(afterNullCheck);
		}

		il.Emit(Ldarg_1);
		il.Emit(Unbox_Any, propertyInfo.PropertyType);

		il.Emit(!propertyInfo.SetMethod.IsStatic ? Callvirt : Call, propertyInfo.SetMethod);

	endOfDelegate:
		il.Emit(Ret);

		if (!setPropertyFuncs.TryGetValue(handle, out funcDictionary)) {
			setPropertyFuncs[handle] = funcDictionary = new ConcurrentDictionary<string, Action<object?, object?>>();

			// Capture the parameter into a local
			nint h = handle;
			
			// TODO: Determine what to do with this?
			// ALCReflectionUnloader.OnUnload(type.Assembly, () => setPropertyFuncs.Remove(h, out _));
		}

		return funcDictionary[propertyName] = method.CreateDelegate<Action<object?, object?>>();
	}

	private static PropertyGenericDelegates<T>.RetrieveDelegate BuildGenericRetrievePropertyDelegate<T>(string propertyName) {
		var type = typeof(T);

		if (PropertyGenericDelegates<T>.GetPropertyFuncs.TryGetValue(propertyName, out PropertyGenericDelegates<T>.RetrieveDelegate? func))
			return func;

		var propertyInfo = GetProperty(type, propertyName);
		string name = $"{typeof(FastReflection).FullName}.BuildGenericRetrievePropertyDelegate<{type.GetSimplifiedGenericTypeName()}>.get_{propertyName}";
		var method = new DynamicMethod(name, typeof(object), new[] { type.MakeByRefType() }, type, skipVisibility: true);
		var il = method.GetILGenerator();

		if (propertyInfo.GetMethod is null) throw new ArgumentException($"Property \"{propertyName}\" does not have a getter!");

		il.Emit(Ldarg_0);
		il.Emit(Callvirt, propertyInfo.GetMethod);

		if (propertyInfo.PropertyType.IsValueType) il.Emit(Box, propertyInfo.PropertyType);

		il.Emit(Ret);

		if (!PropertyGenericDelegates<T>.RetrieveUnloadingRegistered) {
			PropertyGenericDelegates<T>.RetrieveUnloadingRegistered = true;

			// TODO: Determine what to do with this?
			// ALCReflectionUnloader.OnUnload(type.Assembly, PropertyGenericDelegates<T>.getPropertyFuncs.Clear);
		}

		return PropertyGenericDelegates<T>.GetPropertyFuncs[propertyName] = method.CreateDelegate<PropertyGenericDelegates<T>.RetrieveDelegate>();
	}

	private static PropertyGenericDelegates<T>.AssignDelegate BuildGenericAssignPropertyDelegate<T>(string propertyName) {
		var type = typeof(T);

		if (PropertyGenericDelegates<T>.SetPropertyFuncs.TryGetValue(propertyName, out PropertyGenericDelegates<T>.AssignDelegate? func))
			return func;

		var propertyInfo = GetProperty(type, propertyName);
		string name = $"{typeof(FastReflection).FullName}.BuildGenericAssignPropertyDelegate<{type.GetSimplifiedGenericTypeName()}>.set_{propertyName}";
		var method = new DynamicMethod(name, null, new[] { type.MakeByRefType(), typeof(object) }, type, skipVisibility: true);
		var il = method.GetILGenerator();

		if (propertyInfo.SetMethod is null) throw new ArgumentException($"Property \"{propertyName}\" does not have a setter!");

		il.Emit(Ldarg_0);
		il.Emit(Ldarg_1);
		il.Emit(Unbox_Any, propertyInfo.PropertyType);
		il.Emit(Callvirt, propertyInfo.SetMethod);
		il.Emit(Ret);

		if (!PropertyGenericDelegates<T>.AssignUnloadingRegistered) {
			PropertyGenericDelegates<T>.AssignUnloadingRegistered = true;

			// TODO: Determine what to do with this?
			// ALCReflectionUnloader.OnUnload(type.Assembly, PropertyGenericDelegates<T>.setPropertyFuncs.Clear);
		}

		return PropertyGenericDelegates<T>.SetPropertyFuncs[propertyName] = method.CreateDelegate<PropertyGenericDelegates<T>.AssignDelegate>();
	}
}
