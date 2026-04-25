using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Il2CppInterop.Common;
using Il2CppInterop.Common.Attributes;
using Il2CppInterop.Runtime.CatHelpers;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Runtime;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Runtime;

public static unsafe class IL2CPP
{
    private static readonly Dictionary<string, IntPtr> ourImagesMap = new();

    static IL2CPP()
    {
        ExportApi.LoadOrFetchExportList();
        InitExportPtrs();

        var domain = il2cpp_domain_get();
        if (domain == IntPtr.Zero)
        {
            Logger.Instance.LogError("No il2cpp domain found; sad!");
            return;
        }

        uint assembliesCount = 0;
        var assemblies = il2cpp_domain_get_assemblies(domain, ref assembliesCount);
        for (var i = 0; i < assembliesCount; i++)
        {
            var image = il2cpp_assembly_get_image(assemblies[i]);
            var name = il2cpp_image_get_name_(image)!;
            ourImagesMap[name] = image;
        }
    }

    internal static IntPtr GetIl2CppImage(string name)
    {
        if (ourImagesMap.ContainsKey(name)) return ourImagesMap[name];
        return IntPtr.Zero;
    }

    internal static IntPtr[] GetIl2CppImages()
    {
        return ourImagesMap.Values.ToArray();
    }

    public static IntPtr GetIl2CppClass(string assemblyName, string namespaze, string className)
    {
        if (!ourImagesMap.TryGetValue(assemblyName, out var image))
        {
            Logger.Instance.LogError("Assembly {AssemblyName} is not registered in il2cpp", assemblyName);
            return IntPtr.Zero;
        }

        var clazz = il2cpp_class_from_name(image, namespaze, className);
        return clazz;
    }

    public static IntPtr GetIl2CppField(IntPtr clazz, string fieldName)
    {
        if (clazz == IntPtr.Zero) return IntPtr.Zero;

        var field = il2cpp_class_get_field_from_name(clazz, fieldName);
        if (field == IntPtr.Zero)
            Logger.Instance.LogError(
                "Field {FieldName} was not found on class {ClassName}", fieldName, il2cpp_class_get_name_(clazz));
        return field;
    }

    public static IntPtr GetIl2CppMethodByToken(IntPtr clazz, int token)
    {
        if (clazz == IntPtr.Zero)
            return NativeStructUtils.GetMethodInfoForMissingMethod(token.ToString());

        var iter = IntPtr.Zero;
        IntPtr method;
        while ((method = il2cpp_class_get_methods(clazz, ref iter)) != IntPtr.Zero)
            if (il2cpp_method_get_token(method) == token)
                return method;

        var className = il2cpp_class_get_name_(clazz);
        Logger.Instance.LogTrace("Unable to find method {ClassName}::{Token}", className, token);

        return NativeStructUtils.GetMethodInfoForMissingMethod(className + "::" + token);
    }

    public static IntPtr GetIl2CppMethod(IntPtr clazz, bool isGeneric, string methodName, string returnTypeName,
        params string[] argTypes)
    {
        if (clazz == IntPtr.Zero)
            return NativeStructUtils.GetMethodInfoForMissingMethod(methodName + "(" + string.Join(", ", argTypes) +
                                                                   ")");

        returnTypeName = Regex.Replace(returnTypeName, "\\`\\d+", "").Replace('/', '.').Replace('+', '.');
        for (var index = 0; index < argTypes.Length; index++)
        {
            var argType = argTypes[index];
            argTypes[index] = Regex.Replace(argType, "\\`\\d+", "").Replace('/', '.').Replace('+', '.');
        }

        var methodsSeen = 0;
        var lastMethod = IntPtr.Zero;
        var iter = IntPtr.Zero;
        IntPtr method;
        while ((method = il2cpp_class_get_methods(clazz, ref iter)) != IntPtr.Zero)
        {
            if (il2cpp_method_get_name_(method) != methodName)
                continue;

            if (il2cpp_method_get_param_count(method) != argTypes.Length)
                continue;

            if (il2cpp_method_is_generic(method) != isGeneric)
                continue;

            var returnType = il2cpp_method_get_return_type(method);
            var returnTypeNameActual = il2cpp_type_get_name_(returnType);
            if (returnTypeNameActual != returnTypeName)
                continue;

            methodsSeen++;
            lastMethod = method;

            var badType = false;
            for (var i = 0; i < argTypes.Length; i++)
            {
                var paramType = il2cpp_method_get_param(method, (uint)i);
                var typeName = il2cpp_type_get_name_(paramType);
                if (typeName != argTypes[i])
                {
                    badType = true;
                    break;
                }
            }

            if (badType) continue;

            return method;
        }

        var className = il2cpp_class_get_name_(clazz);

        if (methodsSeen == 1)
        {
            Logger.Instance.LogTrace(
                "Method {ClassName}::{MethodName} was stubbed with a random matching method of the same name", className, methodName);
            Logger.Instance.LogTrace(
                "Stubby return type/target: {LastMethod} / {ReturnTypeName}", il2cpp_type_get_name_(il2cpp_method_get_return_type(lastMethod)), returnTypeName);
            Logger.Instance.LogTrace("Stubby parameter types/targets follow:");
            for (var i = 0; i < argTypes.Length; i++)
            {
                var paramType = il2cpp_method_get_param(lastMethod, (uint)i);
                var typeName = il2cpp_type_get_name_(paramType);
                Logger.Instance.LogTrace("    {TypeName} / {ArgType}", typeName, argTypes[i]);
            }

            return lastMethod;
        }

        Logger.Instance.LogTrace("Unable to find method {ClassName}::{MethodName}; signature follows", className, methodName);
        Logger.Instance.LogTrace("    return {ReturnTypeName}", returnTypeName);
        foreach (var argType in argTypes)
            Logger.Instance.LogTrace("    {ArgType}", argType);
        Logger.Instance.LogTrace("Available methods of this name follow:");
        iter = IntPtr.Zero;
        while ((method = il2cpp_class_get_methods(clazz, ref iter)) != IntPtr.Zero)
        {
            if (il2cpp_method_get_name_(method) != methodName)
                continue;

            var nParams = il2cpp_method_get_param_count(method);
            Logger.Instance.LogTrace("Method starts");
            Logger.Instance.LogTrace(
                "     return {MethodTypeName}", il2cpp_type_get_name_(il2cpp_method_get_return_type(method)));
            for (var i = 0; i < nParams; i++)
            {
                var paramType = il2cpp_method_get_param(method, (uint)i);
                var typeName = il2cpp_type_get_name_(paramType);
                Logger.Instance.LogTrace("    {TypeName}", typeName);
            }

            return method;
        }

        return NativeStructUtils.GetMethodInfoForMissingMethod(className + "::" + methodName + "(" +
                                                               string.Join(", ", argTypes) + ")");
    }

    public static string? Il2CppStringToManaged(IntPtr il2CppString)
    {
        if (il2CppString == IntPtr.Zero) return null;

        var length = il2cpp_string_length(il2CppString);
        var chars = il2cpp_string_chars(il2CppString);

        return new string(chars, 0, length);
    }

    public static IntPtr ManagedStringToIl2Cpp(string? str)
    {
        if (str == null) return IntPtr.Zero;

        fixed (char* chars = str)
        {
            return il2cpp_string_new_utf16(chars, str.Length);
        }
    }

    public static IntPtr Il2CppObjectBaseToPtr(Il2CppObjectBase obj)
    {
        return obj?.Pointer ?? IntPtr.Zero;
    }

    public static IntPtr Il2CppObjectBaseToPtrNotNull(Il2CppObjectBase obj)
    {
        return obj?.Pointer ?? throw new NullReferenceException();
    }

    public static IntPtr GetIl2CppNestedType(IntPtr enclosingType, string nestedTypeName)
    {
        if (enclosingType == IntPtr.Zero) return IntPtr.Zero;

        var iter = IntPtr.Zero;
        IntPtr nestedTypePtr;
        if (il2cpp_class_is_inflated(enclosingType))
        {
            Logger.Instance.LogTrace("Original class was inflated, falling back to reflection");

            return RuntimeReflectionHelper.GetNestedTypeViaReflection(enclosingType, nestedTypeName);
        }

        while ((nestedTypePtr = il2cpp_class_get_nested_types(enclosingType, ref iter)) != IntPtr.Zero)
            if (il2cpp_class_get_name_(nestedTypePtr) == nestedTypeName)
                return nestedTypePtr;

        Logger.Instance.LogError(
            "Nested type {NestedTypeName} on {EnclosingTypeName} not found!", nestedTypeName, il2cpp_class_get_name_(enclosingType));

        return IntPtr.Zero;
    }

    public static void ThrowIfNull(object arg)
    {
        if (arg == null)
            throw new NullReferenceException();
    }

    public static T ResolveICall<T>(string signature) where T : Delegate
    {
        var icallPtr = il2cpp_resolve_icall(signature);
        if (icallPtr == IntPtr.Zero)
        {
            Logger.Instance.LogTrace("ICall {Signature} not resolved", signature);
            return GenerateDelegateForMissingICall<T>(signature);
        }

        return Marshal.GetDelegateForFunctionPointer<T>(icallPtr);
    }

    private static T GenerateDelegateForMissingICall<T>(string signature) where T : Delegate
    {
        var invoke = typeof(T).GetMethod("Invoke")!;

        var trampoline = new DynamicMethod("(missing icall delegate) " + typeof(T).FullName,
            invoke.ReturnType, invoke.GetParameters().Select(it => it.ParameterType).ToArray(), typeof(IL2CPP), true);
        var bodyBuilder = trampoline.GetILGenerator();

        bodyBuilder.Emit(OpCodes.Ldstr, $"ICall with signature {signature} was not resolved");
        bodyBuilder.Emit(OpCodes.Newobj, typeof(Exception).GetConstructor(new[] { typeof(string) })!);
        bodyBuilder.Emit(OpCodes.Throw);

        return (T)trampoline.CreateDelegate(typeof(T));
    }

    public static T? PointerToValueGeneric<T>(IntPtr objectPointer, bool isFieldPointer, bool valueTypeWouldBeBoxed)
    {
        if (isFieldPointer)
        {
            if (il2cpp_class_is_valuetype(Il2CppClassPointerStore<T>.NativeClassPtr))
                objectPointer = il2cpp_value_box(Il2CppClassPointerStore<T>.NativeClassPtr, objectPointer);
            else
                objectPointer = *(IntPtr*)objectPointer;
        }

        if (!valueTypeWouldBeBoxed && il2cpp_class_is_valuetype(Il2CppClassPointerStore<T>.NativeClassPtr))
            objectPointer = il2cpp_value_box(Il2CppClassPointerStore<T>.NativeClassPtr, objectPointer);

        if (typeof(T) == typeof(string))
            return (T)(object)Il2CppStringToManaged(objectPointer);

        if (objectPointer == IntPtr.Zero)
            return default;

        if (typeof(T).IsValueType)
            return Il2CppObjectBase.UnboxUnsafe<T>(objectPointer);

        return Il2CppObjectPool.Get<T>(objectPointer);
    }

    public static string RenderTypeName<T>(bool addRefMarker = false)
    {
        return RenderTypeName(typeof(T), addRefMarker);
    }

    public static string RenderTypeName(Type t, bool addRefMarker = false)
    {
        if (addRefMarker) return RenderTypeName(t) + "&";
        if (t.IsArray) return RenderTypeName(t.GetElementType()) + "[]";
        if (t.IsByRef) return RenderTypeName(t.GetElementType()) + "&";
        if (t.IsPointer) return RenderTypeName(t.GetElementType()) + "*";
        if (t.IsGenericParameter) return t.Name;

        if (t.IsGenericType)
        {
            if (t.TypeHasIl2CppArrayBase())
                return RenderTypeName(t.GetGenericArguments()[0]) + "[]";

            var builder = new StringBuilder();
            builder.Append(t.GetGenericTypeDefinition().FullNameObfuscated().TrimIl2CppPrefix());
            builder.Append('<');
            var genericArguments = t.GetGenericArguments();
            for (var i = 0; i < genericArguments.Length; i++)
            {
                if (i != 0) builder.Append(',');
                builder.Append(RenderTypeName(genericArguments[i]));
            }

            builder.Append('>');
            return builder.ToString();
        }

        if (t == typeof(Il2CppStringArray))
            return "System.String[]";

        return t.FullNameObfuscated().TrimIl2CppPrefix();
    }

    private static string FullNameObfuscated(this Type t)
    {
        var obfuscatedNameAnnotations = t.GetCustomAttribute<ObfuscatedNameAttribute>();
        if (obfuscatedNameAnnotations == null) return t.FullName;
        return obfuscatedNameAnnotations.ObfuscatedName;
    }

    private static string TrimIl2CppPrefix(this string s)
    {
        return s.StartsWith("Il2Cpp") ? s.Substring("Il2Cpp".Length) : s;
    }

    private static bool TypeHasIl2CppArrayBase(this Type type)
    {
        if (type == null) return false;
        if (type.IsConstructedGenericType) type = type.GetGenericTypeDefinition();
        if (type == typeof(Il2CppArrayBase<>)) return true;
        return TypeHasIl2CppArrayBase(type.BaseType);
    }

    // this is called if there's no actual il2cpp_gc_wbarrier_set_field()
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FieldWriteWbarrierStub(IntPtr obj, IntPtr targetAddress, IntPtr value)
    {
        // ignore obj
        *(IntPtr*)targetAddress = value;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandle(string lpModuleName);


    private static IntPtr _gameAssemblyModule;
    private static IntPtr GetGameAssemblyModule()
    {
        if (_gameAssemblyModule == IntPtr.Zero)
        {
            _gameAssemblyModule = GetModuleHandle("GameAssembly.dll");
        }
        return _gameAssemblyModule;
    }

    private static IntPtr GetExport(string exportCleanName)
    {
        string exportName = ExportApi.GetExportName(exportCleanName);
        if (string.IsNullOrEmpty(exportName)) return IntPtr.Zero;

        IntPtr gameAssemblyModule = GetGameAssemblyModule();
        if (gameAssemblyModule == IntPtr.Zero) return IntPtr.Zero;

        return GetProcAddress(gameAssemblyModule, exportName);
    }

    // IL2CPP Functions

    private static void InitExportPtrs()
    {
        IntPtr ptr;

        ptr = GetExport("il2cpp_init");
        if (ptr != IntPtr.Zero)
            _il2cpp_init = Marshal.GetDelegateForFunctionPointer<il2cpp_init_t>(ptr);

        ptr = GetExport("il2cpp_init_utf16");
        if (ptr != IntPtr.Zero)
            _il2cpp_init_utf16 = Marshal.GetDelegateForFunctionPointer<il2cpp_init_utf16_t>(ptr);

        ptr = GetExport("il2cpp_shutdown");
        if (ptr != IntPtr.Zero)
            _il2cpp_shutdown = Marshal.GetDelegateForFunctionPointer<il2cpp_shutdown_t>(ptr);

        ptr = GetExport("il2cpp_set_data_dir");
        if (ptr != IntPtr.Zero)
            _il2cpp_set_data_dir = Marshal.GetDelegateForFunctionPointer<il2cpp_set_data_dir_t>(ptr);

        ptr = GetExport("il2cpp_set_config_dir");
        if (ptr != IntPtr.Zero)
            _il2cpp_set_config_dir = Marshal.GetDelegateForFunctionPointer<il2cpp_set_config_dir_t>(ptr);

        ptr = GetExport("il2cpp_set_config_dir");
        if (ptr != IntPtr.Zero)
            _il2cpp_set_config_dir = Marshal.GetDelegateForFunctionPointer<il2cpp_set_config_dir_t>(ptr);

        ptr = GetExport("il2cpp_set_data_dir");
        if (ptr != IntPtr.Zero)
            _il2cpp_set_data_dir = Marshal.GetDelegateForFunctionPointer<il2cpp_set_data_dir_t>(ptr);

        ptr = GetExport("il2cpp_set_temp_dir");
        if (ptr != IntPtr.Zero)
            _il2cpp_set_temp_dir = Marshal.GetDelegateForFunctionPointer<il2cpp_set_temp_dir_t>(ptr);

        ptr = GetExport("il2cpp_set_commandline_arguments");
        if (ptr != IntPtr.Zero)
            _il2cpp_set_commandline_arguments = Marshal.GetDelegateForFunctionPointer<il2cpp_set_commandline_arguments_t>(ptr);

        ptr = GetExport("il2cpp_set_commandline_arguments_utf16");
        if (ptr != IntPtr.Zero)
            _il2cpp_set_commandline_arguments_utf16 = Marshal.GetDelegateForFunctionPointer<il2cpp_set_commandline_arguments_utf16_t>(ptr);

        ptr = GetExport("il2cpp_set_config_utf16");
        if (ptr != IntPtr.Zero)
            _il2cpp_set_config_utf16 = Marshal.GetDelegateForFunctionPointer<il2cpp_set_config_utf16_t>(ptr);

        ptr = GetExport("il2cpp_set_config");
        if (ptr != IntPtr.Zero)
            _il2cpp_set_config = Marshal.GetDelegateForFunctionPointer<il2cpp_set_config_t>(ptr);

        ptr = GetExport("il2cpp_set_memory_callbacks");
        if (ptr != IntPtr.Zero)
            _il2cpp_set_memory_callbacks = Marshal.GetDelegateForFunctionPointer<il2cpp_set_memory_callbacks_t>(ptr);

        ptr = GetExport("il2cpp_get_corlib");
        if (ptr != IntPtr.Zero)
            _il2cpp_get_corlib = Marshal.GetDelegateForFunctionPointer<il2cpp_get_corlib_t>(ptr);

        ptr = GetExport("il2cpp_add_internal_call");
        if (ptr != IntPtr.Zero)
            _il2cpp_add_internal_call = Marshal.GetDelegateForFunctionPointer<il2cpp_add_internal_call_t>(ptr);

        ptr = GetExport("il2cpp_resolve_icall");
        if (ptr != IntPtr.Zero)
            _il2cpp_resolve_icall = Marshal.GetDelegateForFunctionPointer<il2cpp_resolve_icall_t>(ptr);

        ptr = GetExport("il2cpp_alloc");
        if (ptr != IntPtr.Zero)
            _il2cpp_alloc = Marshal.GetDelegateForFunctionPointer<il2cpp_alloc_t>(ptr);

        ptr = GetExport("il2cpp_free");
        if (ptr != IntPtr.Zero)
            _il2cpp_free = Marshal.GetDelegateForFunctionPointer<il2cpp_free_t>(ptr);

        ptr = GetExport("il2cpp_array_class_get");
        if (ptr != IntPtr.Zero)
            _il2cpp_array_class_get = Marshal.GetDelegateForFunctionPointer<il2cpp_array_class_get_t>(ptr);

        ptr = GetExport("il2cpp_array_length");
        if (ptr != IntPtr.Zero)
            _il2cpp_array_length = Marshal.GetDelegateForFunctionPointer<il2cpp_array_length_t>(ptr);

        ptr = GetExport("il2cpp_array_get_byte_length");
        if (ptr != IntPtr.Zero)
            _il2cpp_array_get_byte_length = Marshal.GetDelegateForFunctionPointer<il2cpp_array_get_byte_length_t>(ptr);

        ptr = GetExport("il2cpp_array_new");
        if (ptr != IntPtr.Zero)
            _il2cpp_array_new = Marshal.GetDelegateForFunctionPointer<il2cpp_array_new_t>(ptr);

        ptr = GetExport("il2cpp_array_new_specific");
        if (ptr != IntPtr.Zero)
            _il2cpp_array_new_specific = Marshal.GetDelegateForFunctionPointer<il2cpp_array_new_specific_t>(ptr);

        ptr = GetExport("il2cpp_array_new_full");
        if (ptr != IntPtr.Zero)
            _il2cpp_array_new_full = Marshal.GetDelegateForFunctionPointer<il2cpp_array_new_full_t>(ptr);

        ptr = GetExport("il2cpp_bounded_array_class_get");
        if (ptr != IntPtr.Zero)
            _il2cpp_bounded_array_class_get = Marshal.GetDelegateForFunctionPointer<il2cpp_bounded_array_class_get_t>(ptr);

        ptr = GetExport("il2cpp_array_element_size");
        if (ptr != IntPtr.Zero)
            _il2cpp_array_element_size = Marshal.GetDelegateForFunctionPointer<il2cpp_array_element_size_t>(ptr);

        ptr = GetExport("il2cpp_assembly_get_image");
        if (ptr != IntPtr.Zero)
            _il2cpp_assembly_get_image = Marshal.GetDelegateForFunctionPointer<il2cpp_assembly_get_image_t>(ptr);

        ptr = GetExport("il2cpp_class_enum_basetype");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_enum_basetype = Marshal.GetDelegateForFunctionPointer<il2cpp_class_enum_basetype_t>(ptr);

        ptr = GetExport("il2cpp_class_is_generic");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_is_generic = Marshal.GetDelegateForFunctionPointer<il2cpp_class_is_generic_t>(ptr);

        ptr = GetExport("il2cpp_class_is_inflated");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_is_inflated = Marshal.GetDelegateForFunctionPointer<il2cpp_class_is_inflated_t>(ptr);

        ptr = GetExport("il2cpp_class_is_assignable_from");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_is_assignable_from = Marshal.GetDelegateForFunctionPointer<il2cpp_class_is_assignable_from_t>(ptr);

        ptr = GetExport("il2cpp_class_is_subclass_of");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_is_subclass_of = Marshal.GetDelegateForFunctionPointer<il2cpp_class_is_subclass_of_t>(ptr);

        ptr = GetExport("il2cpp_class_has_parent");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_has_parent = Marshal.GetDelegateForFunctionPointer<il2cpp_class_has_parent_t>(ptr);

        ptr = GetExport("il2cpp_class_from_il2cpp_type");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_from_il2cpp_type = Marshal.GetDelegateForFunctionPointer<il2cpp_class_from_il2cpp_type_t>(ptr);

        ptr = GetExport("il2cpp_class_from_name");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_from_name = Marshal.GetDelegateForFunctionPointer<il2cpp_class_from_name_t>(ptr);

        ptr = GetExport("il2cpp_class_from_system_type");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_from_system_type = Marshal.GetDelegateForFunctionPointer<il2cpp_class_from_system_type_t>(ptr);

        ptr = GetExport("il2cpp_class_get_element_class");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_element_class = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_element_class_t>(ptr);

        ptr = GetExport("il2cpp_class_get_events");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_events = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_events_t>(ptr);

        ptr = GetExport("il2cpp_class_get_fields");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_fields = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_fields_t>(ptr);

        ptr = GetExport("il2cpp_class_get_nested_types");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_nested_types = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_nested_types_t>(ptr);

        ptr = GetExport("il2cpp_class_get_interfaces");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_interfaces = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_interfaces_t>(ptr);

        ptr = GetExport("il2cpp_class_get_properties");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_properties = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_properties_t>(ptr);

        ptr = GetExport("il2cpp_class_get_property_from_name");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_property_from_name = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_property_from_name_t>(ptr);

        ptr = GetExport("il2cpp_class_get_field_from_name");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_field_from_name = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_field_from_name_t>(ptr);

        ptr = GetExport("il2cpp_class_get_methods");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_methods = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_methods_t>(ptr);

        ptr = GetExport("il2cpp_class_get_method_from_name");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_method_from_name = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_method_from_name_t>(ptr);

        ptr = GetExport("il2cpp_class_get_name");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_name = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_name_t>(ptr);

        ptr = GetExport("il2cpp_class_get_namespace");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_namespace = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_namespace_t>(ptr);

        ptr = GetExport("il2cpp_class_get_parent");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_parent = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_parent_t>(ptr);

        ptr = GetExport("il2cpp_class_get_declaring_type");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_declaring_type = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_declaring_type_t>(ptr);

        ptr = GetExport("il2cpp_class_instance_size");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_instance_size = Marshal.GetDelegateForFunctionPointer<il2cpp_class_instance_size_t>(ptr);

        ptr = GetExport("il2cpp_class_num_fields");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_num_fields = Marshal.GetDelegateForFunctionPointer<il2cpp_class_num_fields_t>(ptr);

        ptr = GetExport("il2cpp_class_is_valuetype");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_is_valuetype = Marshal.GetDelegateForFunctionPointer<il2cpp_class_is_valuetype_t>(ptr);

        ptr = GetExport("il2cpp_class_value_size");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_value_size = Marshal.GetDelegateForFunctionPointer<il2cpp_class_value_size_t>(ptr);

        ptr = GetExport("il2cpp_class_is_blittable");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_is_blittable = Marshal.GetDelegateForFunctionPointer<il2cpp_class_is_blittable_t>(ptr);

        ptr = GetExport("il2cpp_class_get_flags");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_flags = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_flags_t>(ptr);

        ptr = GetExport("il2cpp_class_is_abstract");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_is_abstract = Marshal.GetDelegateForFunctionPointer<il2cpp_class_is_abstract_t>(ptr);

        ptr = GetExport("il2cpp_class_is_interface");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_is_interface = Marshal.GetDelegateForFunctionPointer<il2cpp_class_is_interface_t>(ptr);

        ptr = GetExport("il2cpp_class_array_element_size");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_array_element_size = Marshal.GetDelegateForFunctionPointer<il2cpp_class_array_element_size_t>(ptr);

        ptr = GetExport("il2cpp_class_from_type");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_from_type = Marshal.GetDelegateForFunctionPointer<il2cpp_class_from_type_t>(ptr);

        ptr = GetExport("il2cpp_class_get_type");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_type = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_type_t>(ptr);

        ptr = GetExport("il2cpp_class_get_type_token");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_type_token = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_type_token_t>(ptr);

        ptr = GetExport("il2cpp_class_has_attribute");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_has_attribute = Marshal.GetDelegateForFunctionPointer<il2cpp_class_has_attribute_t>(ptr);

        ptr = GetExport("il2cpp_class_has_references");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_has_references = Marshal.GetDelegateForFunctionPointer<il2cpp_class_has_references_t>(ptr);

        ptr = GetExport("il2cpp_class_is_enum");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_is_enum = Marshal.GetDelegateForFunctionPointer<il2cpp_class_is_enum_t>(ptr);

        ptr = GetExport("il2cpp_class_get_image");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_image = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_image_t>(ptr);

        ptr = GetExport("il2cpp_class_get_assemblyname");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_assemblyname = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_assemblyname_t>(ptr);

        ptr = GetExport("il2cpp_class_get_rank");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_rank = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_rank_t>(ptr);

        ptr = GetExport("il2cpp_class_get_bitmap_size");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_bitmap_size = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_bitmap_size_t>(ptr);

        ptr = GetExport("il2cpp_class_get_bitmap");
        if (ptr != IntPtr.Zero)
            _il2cpp_class_get_bitmap = Marshal.GetDelegateForFunctionPointer<il2cpp_class_get_bitmap_t>(ptr);

        ptr = GetExport("il2cpp_stats_dump_to_file");
        if (ptr != IntPtr.Zero)
            _il2cpp_stats_dump_to_file = Marshal.GetDelegateForFunctionPointer<il2cpp_stats_dump_to_file_t>(ptr);

        ptr = GetExport("il2cpp_domain_get");
        if (ptr != IntPtr.Zero)
            _il2cpp_domain_get = Marshal.GetDelegateForFunctionPointer<il2cpp_domain_get_t>(ptr);

        ptr = GetExport("il2cpp_domain_assembly_open");
        if (ptr != IntPtr.Zero)
            _il2cpp_domain_assembly_open = Marshal.GetDelegateForFunctionPointer<il2cpp_domain_assembly_open_t>(ptr);

        ptr = GetExport("il2cpp_domain_get_assemblies");
        if (ptr != IntPtr.Zero)
            _il2cpp_domain_get_assemblies = Marshal.GetDelegateForFunctionPointer<il2cpp_domain_get_assemblies_t>(ptr);

        ptr = GetExport("il2cpp_exception_from_name_msg");
        if (ptr != IntPtr.Zero)
            _il2cpp_exception_from_name_msg = Marshal.GetDelegateForFunctionPointer<il2cpp_exception_from_name_msg_t>(ptr);

        ptr = GetExport("il2cpp_get_exception_argument_null");
        if (ptr != IntPtr.Zero)
            _il2cpp_get_exception_argument_null = Marshal.GetDelegateForFunctionPointer<il2cpp_get_exception_argument_null_t>(ptr);

        ptr = GetExport("il2cpp_format_exception");
        if (ptr != IntPtr.Zero)
            _il2cpp_format_exception = Marshal.GetDelegateForFunctionPointer<il2cpp_format_exception_t>(ptr);

        ptr = GetExport("il2cpp_format_stack_trace");
        if (ptr != IntPtr.Zero)
            _il2cpp_format_stack_trace = Marshal.GetDelegateForFunctionPointer<il2cpp_format_stack_trace_t>(ptr);

        ptr = GetExport("il2cpp_unhandled_exception");
        if (ptr != IntPtr.Zero)
            _il2cpp_unhandled_exception = Marshal.GetDelegateForFunctionPointer<il2cpp_unhandled_exception_t>(ptr);

        ptr = GetExport("il2cpp_field_get_flags");
        if (ptr != IntPtr.Zero)
            _il2cpp_field_get_flags = Marshal.GetDelegateForFunctionPointer<il2cpp_field_get_flags_t>(ptr);

        ptr = GetExport("il2cpp_field_get_name");
        if (ptr != IntPtr.Zero)
            _il2cpp_field_get_name = Marshal.GetDelegateForFunctionPointer<il2cpp_field_get_name_t>(ptr);

        ptr = GetExport("il2cpp_field_get_parent");
        if (ptr != IntPtr.Zero)
            _il2cpp_field_get_parent = Marshal.GetDelegateForFunctionPointer<il2cpp_field_get_parent_t>(ptr);

        ptr = GetExport("il2cpp_field_get_offset");
        if (ptr != IntPtr.Zero)
            _il2cpp_field_get_offset = Marshal.GetDelegateForFunctionPointer<il2cpp_field_get_offset_t>(ptr);

        ptr = GetExport("il2cpp_field_get_type");
        if (ptr != IntPtr.Zero)
            _il2cpp_field_get_type = Marshal.GetDelegateForFunctionPointer<il2cpp_field_get_type_t>(ptr);

        ptr = GetExport("il2cpp_field_get_value");
        if (ptr != IntPtr.Zero)
            _il2cpp_field_get_value = Marshal.GetDelegateForFunctionPointer<il2cpp_field_get_value_t>(ptr);

        ptr = GetExport("il2cpp_field_get_value_object");
        if (ptr != IntPtr.Zero)
            _il2cpp_field_get_value_object = Marshal.GetDelegateForFunctionPointer<il2cpp_field_get_value_object_t>(ptr);

        ptr = GetExport("il2cpp_field_has_attribute");
        if (ptr != IntPtr.Zero)
            _il2cpp_field_has_attribute = Marshal.GetDelegateForFunctionPointer<il2cpp_field_has_attribute_t>(ptr);

        ptr = GetExport("il2cpp_field_set_value");
        if (ptr != IntPtr.Zero)
            _il2cpp_field_set_value = Marshal.GetDelegateForFunctionPointer<il2cpp_field_set_value_t>(ptr);

        ptr = GetExport("il2cpp_field_static_get_value");
        if (ptr != IntPtr.Zero)
            _il2cpp_field_static_get_value = Marshal.GetDelegateForFunctionPointer<il2cpp_field_static_get_value_t>(ptr);

        ptr = GetExport("il2cpp_field_static_set_value");
        if (ptr != IntPtr.Zero)
            _il2cpp_field_static_set_value = Marshal.GetDelegateForFunctionPointer<il2cpp_field_static_set_value_t>(ptr);

        ptr = GetExport("il2cpp_field_set_value_object");
        if (ptr != IntPtr.Zero)
            _il2cpp_field_set_value_object = Marshal.GetDelegateForFunctionPointer<il2cpp_field_set_value_object_t>(ptr);

        ptr = GetExport("il2cpp_gc_collect");
        if (ptr != IntPtr.Zero)
            _il2cpp_gc_collect = Marshal.GetDelegateForFunctionPointer<il2cpp_gc_collect_t>(ptr);

        ptr = GetExport("il2cpp_gc_collect_a_little");
        if (ptr != IntPtr.Zero)
            _il2cpp_gc_collect_a_little = Marshal.GetDelegateForFunctionPointer<il2cpp_gc_collect_a_little_t>(ptr);

        ptr = GetExport("il2cpp_gc_disable");
        if (ptr != IntPtr.Zero)
            _il2cpp_gc_disable = Marshal.GetDelegateForFunctionPointer<il2cpp_gc_disable_t>(ptr);

        ptr = GetExport("il2cpp_gc_enable");
        if (ptr != IntPtr.Zero)
            _il2cpp_gc_enable = Marshal.GetDelegateForFunctionPointer<il2cpp_gc_enable_t>(ptr);

        ptr = GetExport("il2cpp_gc_is_disabled");
        if (ptr != IntPtr.Zero)
            _il2cpp_gc_is_disabled = Marshal.GetDelegateForFunctionPointer<il2cpp_gc_is_disabled_t>(ptr);

        ptr = GetExport("il2cpp_gc_get_used_size");
        if (ptr != IntPtr.Zero)
            _il2cpp_gc_get_used_size = Marshal.GetDelegateForFunctionPointer<il2cpp_gc_get_used_size_t>(ptr);

        ptr = GetExport("il2cpp_gc_get_heap_size");
        if (ptr != IntPtr.Zero)
            _il2cpp_gc_get_heap_size = Marshal.GetDelegateForFunctionPointer<il2cpp_gc_get_heap_size_t>(ptr);

        ptr = GetExport("il2cpp_gc_wbarrier_set_field");
        if (ptr != IntPtr.Zero)
            _il2cpp_gc_wbarrier_set_field = Marshal.GetDelegateForFunctionPointer<il2cpp_gc_wbarrier_set_field_t>(ptr);

        ptr = GetExport("il2cpp_gchandle_new");
        if (ptr != IntPtr.Zero)
            _il2cpp_gchandle_new = Marshal.GetDelegateForFunctionPointer<il2cpp_gchandle_new_t>(ptr);

        ptr = GetExport("il2cpp_gchandle_new_weakref");
        if (ptr != IntPtr.Zero)
            _il2cpp_gchandle_new_weakref = Marshal.GetDelegateForFunctionPointer<il2cpp_gchandle_new_weakref_t>(ptr);

        ptr = GetExport("il2cpp_gchandle_get_target");
        if (ptr != IntPtr.Zero)
            _il2cpp_gchandle_get_target = Marshal.GetDelegateForFunctionPointer<il2cpp_gchandle_get_target_t>(ptr);

        ptr = GetExport("il2cpp_gchandle_free");
        if (ptr != IntPtr.Zero)
            _il2cpp_gchandle_free = Marshal.GetDelegateForFunctionPointer<il2cpp_gchandle_free_t>(ptr);

        ptr = GetExport("il2cpp_unity_liveness_calculation_begin");
        if (ptr != IntPtr.Zero)
            _il2cpp_unity_liveness_calculation_begin = Marshal.GetDelegateForFunctionPointer<il2cpp_unity_liveness_calculation_begin_t>(ptr);

        ptr = GetExport("il2cpp_unity_liveness_calculation_end");
        if (ptr != IntPtr.Zero)
            _il2cpp_unity_liveness_calculation_end = Marshal.GetDelegateForFunctionPointer<il2cpp_unity_liveness_calculation_end_t>(ptr);

        ptr = GetExport("il2cpp_unity_liveness_calculation_from_root");
        if (ptr != IntPtr.Zero)
            _il2cpp_unity_liveness_calculation_from_root = Marshal.GetDelegateForFunctionPointer<il2cpp_unity_liveness_calculation_from_root_t>(ptr);

        ptr = GetExport("il2cpp_unity_liveness_calculation_from_statics");
        if (ptr != IntPtr.Zero)
            _il2cpp_unity_liveness_calculation_from_statics = Marshal.GetDelegateForFunctionPointer<il2cpp_unity_liveness_calculation_from_statics_t>(ptr);

        ptr = GetExport("il2cpp_method_get_return_type");
        if (ptr != IntPtr.Zero)
            _il2cpp_method_get_return_type = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_return_type_t>(ptr);

        ptr = GetExport("il2cpp_method_get_declaring_type");
        if (ptr != IntPtr.Zero)
            _il2cpp_method_get_declaring_type = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_declaring_type_t>(ptr);

        ptr = GetExport("il2cpp_method_get_name");
        if (ptr != IntPtr.Zero)
            _il2cpp_method_get_name = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_name_t>(ptr);

        ptr = GetExport("_il2cpp_method_get_from_reflection");
        if (ptr != IntPtr.Zero)
            __il2cpp_method_get_from_reflection = Marshal.GetDelegateForFunctionPointer<_il2cpp_method_get_from_reflection_t>(ptr);

        ptr = GetExport("il2cpp_method_get_object");
        if (ptr != IntPtr.Zero)
            _il2cpp_method_get_object = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_object_t>(ptr);

        ptr = GetExport("il2cpp_method_is_generic");
        if (ptr != IntPtr.Zero)
            _il2cpp_method_is_generic = Marshal.GetDelegateForFunctionPointer<il2cpp_method_is_generic_t>(ptr);

        ptr = GetExport("il2cpp_method_is_inflated");
        if (ptr != IntPtr.Zero)
            _il2cpp_method_is_inflated = Marshal.GetDelegateForFunctionPointer<il2cpp_method_is_inflated_t>(ptr);

        ptr = GetExport("il2cpp_method_is_instance");
        if (ptr != IntPtr.Zero)
            _il2cpp_method_is_instance = Marshal.GetDelegateForFunctionPointer<il2cpp_method_is_instance_t>(ptr);

        ptr = GetExport("il2cpp_method_get_param_count");
        if (ptr != IntPtr.Zero)
            _il2cpp_method_get_param_count = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_param_count_t>(ptr);

        ptr = GetExport("il2cpp_method_get_param");
        if (ptr != IntPtr.Zero)
            _il2cpp_method_get_param = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_param_t>(ptr);

        ptr = GetExport("il2cpp_method_get_class");
        if (ptr != IntPtr.Zero)
            _il2cpp_method_get_class = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_class_t>(ptr);

        ptr = GetExport("il2cpp_method_has_attribute");
        if (ptr != IntPtr.Zero)
            _il2cpp_method_has_attribute = Marshal.GetDelegateForFunctionPointer<il2cpp_method_has_attribute_t>(ptr);

        ptr = GetExport("il2cpp_method_get_flags");
        if (ptr != IntPtr.Zero)
            _il2cpp_method_get_flags = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_flags_t>(ptr);

        ptr = GetExport("il2cpp_method_get_token");
        if (ptr != IntPtr.Zero)
            _il2cpp_method_get_token = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_token_t>(ptr);

        ptr = GetExport("il2cpp_method_get_param_name");
        if (ptr != IntPtr.Zero)
            _il2cpp_method_get_param_name = Marshal.GetDelegateForFunctionPointer<il2cpp_method_get_param_name_t>(ptr);

        ptr = GetExport("il2cpp_profiler_install");
        if (ptr != IntPtr.Zero)
            _il2cpp_profiler_install = Marshal.GetDelegateForFunctionPointer<il2cpp_profiler_install_t>(ptr);

        ptr = GetExport("il2cpp_profiler_install_enter_leave");
        if (ptr != IntPtr.Zero)
            _il2cpp_profiler_install_enter_leave = Marshal.GetDelegateForFunctionPointer<il2cpp_profiler_install_enter_leave_t>(ptr);

        ptr = GetExport("il2cpp_profiler_install_allocation");
        if (ptr != IntPtr.Zero)
            _il2cpp_profiler_install_allocation = Marshal.GetDelegateForFunctionPointer<il2cpp_profiler_install_allocation_t>(ptr);

        ptr = GetExport("il2cpp_profiler_install_gc");
        if (ptr != IntPtr.Zero)
            _il2cpp_profiler_install_gc = Marshal.GetDelegateForFunctionPointer<il2cpp_profiler_install_gc_t>(ptr);

        ptr = GetExport("il2cpp_profiler_install_fileio");
        if (ptr != IntPtr.Zero)
            _il2cpp_profiler_install_fileio = Marshal.GetDelegateForFunctionPointer<il2cpp_profiler_install_fileio_t>(ptr);

        ptr = GetExport("il2cpp_profiler_install_thread");
        if (ptr != IntPtr.Zero)
            _il2cpp_profiler_install_thread = Marshal.GetDelegateForFunctionPointer<il2cpp_profiler_install_thread_t>(ptr);

        ptr = GetExport("il2cpp_property_get_flags");
        if (ptr != IntPtr.Zero)
            _il2cpp_property_get_flags = Marshal.GetDelegateForFunctionPointer<il2cpp_property_get_flags_t>(ptr);

        ptr = GetExport("il2cpp_property_get_get_method");
        if (ptr != IntPtr.Zero)
            _il2cpp_property_get_get_method = Marshal.GetDelegateForFunctionPointer<il2cpp_property_get_get_method_t>(ptr);

        ptr = GetExport("il2cpp_property_get_set_method");
        if (ptr != IntPtr.Zero)
            _il2cpp_property_get_set_method = Marshal.GetDelegateForFunctionPointer<il2cpp_property_get_set_method_t>(ptr);

        ptr = GetExport("il2cpp_property_get_name");
        if (ptr != IntPtr.Zero)
            _il2cpp_property_get_name = Marshal.GetDelegateForFunctionPointer<il2cpp_property_get_name_t>(ptr);

        ptr = GetExport("il2cpp_property_get_parent");
        if (ptr != IntPtr.Zero)
            _il2cpp_property_get_parent = Marshal.GetDelegateForFunctionPointer<il2cpp_property_get_parent_t>(ptr);

        ptr = GetExport("il2cpp_object_get_class");
        if (ptr != IntPtr.Zero)
            _il2cpp_object_get_class = Marshal.GetDelegateForFunctionPointer<il2cpp_object_get_class_t>(ptr);

        ptr = GetExport("il2cpp_object_get_size");
        if (ptr != IntPtr.Zero)
            _il2cpp_object_get_size = Marshal.GetDelegateForFunctionPointer<il2cpp_object_get_size_t>(ptr);

        ptr = GetExport("il2cpp_object_get_virtual_method");
        if (ptr != IntPtr.Zero)
            _il2cpp_object_get_virtual_method = Marshal.GetDelegateForFunctionPointer<il2cpp_object_get_virtual_method_t>(ptr);

        ptr = GetExport("il2cpp_object_new");
        if (ptr != IntPtr.Zero)
            _il2cpp_object_new = Marshal.GetDelegateForFunctionPointer<il2cpp_object_new_t>(ptr);

        ptr = GetExport("il2cpp_object_unbox");
        if (ptr != IntPtr.Zero)
            _il2cpp_object_unbox = Marshal.GetDelegateForFunctionPointer<il2cpp_object_unbox_t>(ptr);

        ptr = GetExport("il2cpp_value_box");
        if (ptr != IntPtr.Zero)
            _il2cpp_value_box = Marshal.GetDelegateForFunctionPointer<il2cpp_value_box_t>(ptr);

        ptr = GetExport("il2cpp_monitor_enter");
        if (ptr != IntPtr.Zero)
            _il2cpp_monitor_enter = Marshal.GetDelegateForFunctionPointer<il2cpp_monitor_enter_t>(ptr);

        ptr = GetExport("il2cpp_monitor_try_enter");
        if (ptr != IntPtr.Zero)
            _il2cpp_monitor_try_enter = Marshal.GetDelegateForFunctionPointer<il2cpp_monitor_try_enter_t>(ptr);

        ptr = GetExport("il2cpp_monitor_exit");
        if (ptr != IntPtr.Zero)
            _il2cpp_monitor_exit = Marshal.GetDelegateForFunctionPointer<il2cpp_monitor_exit_t>(ptr);

        ptr = GetExport("il2cpp_monitor_pulse");
        if (ptr != IntPtr.Zero)
            _il2cpp_monitor_pulse = Marshal.GetDelegateForFunctionPointer<il2cpp_monitor_pulse_t>(ptr);

        ptr = GetExport("il2cpp_monitor_pulse_all");
        if (ptr != IntPtr.Zero)
            _il2cpp_monitor_pulse_all = Marshal.GetDelegateForFunctionPointer<il2cpp_monitor_pulse_all_t>(ptr);

        ptr = GetExport("il2cpp_monitor_wait");
        if (ptr != IntPtr.Zero)
            _il2cpp_monitor_wait = Marshal.GetDelegateForFunctionPointer<il2cpp_monitor_wait_t>(ptr);

        ptr = GetExport("il2cpp_monitor_try_wait");
        if (ptr != IntPtr.Zero)
            _il2cpp_monitor_try_wait = Marshal.GetDelegateForFunctionPointer<il2cpp_monitor_try_wait_t>(ptr);

        ptr = GetExport("il2cpp_runtime_invoke");
        if (ptr != IntPtr.Zero)
            _il2cpp_runtime_invoke = Marshal.GetDelegateForFunctionPointer<il2cpp_runtime_invoke_t>(ptr);

        ptr = GetExport("il2cpp_runtime_invoke_convert_args");
        if (ptr != IntPtr.Zero)
            _il2cpp_runtime_invoke_convert_args = Marshal.GetDelegateForFunctionPointer<il2cpp_runtime_invoke_convert_args_t>(ptr);

        ptr = GetExport("il2cpp_runtime_class_init");
        if (ptr != IntPtr.Zero)
            _il2cpp_runtime_class_init = Marshal.GetDelegateForFunctionPointer<il2cpp_runtime_class_init_t>(ptr);

        ptr = GetExport("il2cpp_runtime_object_init");
        if (ptr != IntPtr.Zero)
            _il2cpp_runtime_object_init = Marshal.GetDelegateForFunctionPointer<il2cpp_runtime_object_init_t>(ptr);

        ptr = GetExport("il2cpp_runtime_object_init_exception");
        if (ptr != IntPtr.Zero)
            _il2cpp_runtime_object_init_exception = Marshal.GetDelegateForFunctionPointer<il2cpp_runtime_object_init_exception_t>(ptr);

        ptr = GetExport("il2cpp_string_length");
        if (ptr != IntPtr.Zero)
            _il2cpp_string_length = Marshal.GetDelegateForFunctionPointer<il2cpp_string_length_t>(ptr);

        ptr = GetExport("il2cpp_string_chars");
        if (ptr != IntPtr.Zero)
            _il2cpp_string_chars = Marshal.GetDelegateForFunctionPointer<il2cpp_string_chars_t>(ptr);

        ptr = GetExport("il2cpp_string_new");
        if (ptr != IntPtr.Zero)
            _il2cpp_string_new = Marshal.GetDelegateForFunctionPointer<il2cpp_string_new_t>(ptr);

        ptr = GetExport("il2cpp_string_new_len");
        if (ptr != IntPtr.Zero)
            _il2cpp_string_new_len = Marshal.GetDelegateForFunctionPointer<il2cpp_string_new_len_t>(ptr);

        ptr = GetExport("il2cpp_string_new_utf16");
        if (ptr != IntPtr.Zero)
            _il2cpp_string_new_utf16 = Marshal.GetDelegateForFunctionPointer<il2cpp_string_new_utf16_t>(ptr);

        ptr = GetExport("il2cpp_string_new_wrapper");
        if (ptr != IntPtr.Zero)
            _il2cpp_string_new_wrapper = Marshal.GetDelegateForFunctionPointer<il2cpp_string_new_wrapper_t>(ptr);

        ptr = GetExport("il2cpp_string_intern");
        if (ptr != IntPtr.Zero)
            _il2cpp_string_intern = Marshal.GetDelegateForFunctionPointer<il2cpp_string_intern_t>(ptr);

        ptr = GetExport("il2cpp_string_is_interned");
        if (ptr != IntPtr.Zero)
            _il2cpp_string_is_interned = Marshal.GetDelegateForFunctionPointer<il2cpp_string_is_interned_t>(ptr);

        ptr = GetExport("il2cpp_thread_current");
        if (ptr != IntPtr.Zero)
            _il2cpp_thread_current = Marshal.GetDelegateForFunctionPointer<il2cpp_thread_current_t>(ptr);

        ptr = GetExport("il2cpp_thread_attach");
        if (ptr != IntPtr.Zero)
            _il2cpp_thread_attach = Marshal.GetDelegateForFunctionPointer<il2cpp_thread_attach_t>(ptr);

        ptr = GetExport("il2cpp_thread_detach");
        if (ptr != IntPtr.Zero)
            _il2cpp_thread_detach = Marshal.GetDelegateForFunctionPointer<il2cpp_thread_detach_t>(ptr);

        ptr = GetExport("il2cpp_thread_get_all_attached_threads");
        if (ptr != IntPtr.Zero)
            _il2cpp_thread_get_all_attached_threads = Marshal.GetDelegateForFunctionPointer<il2cpp_thread_get_all_attached_threads_t>(ptr);

        ptr = GetExport("il2cpp_is_vm_thread");
        if (ptr != IntPtr.Zero)
            _il2cpp_is_vm_thread = Marshal.GetDelegateForFunctionPointer<il2cpp_is_vm_thread_t>(ptr);

        ptr = GetExport("il2cpp_current_thread_walk_frame_stack");
        if (ptr != IntPtr.Zero)
            _il2cpp_current_thread_walk_frame_stack = Marshal.GetDelegateForFunctionPointer<il2cpp_current_thread_walk_frame_stack_t>(ptr);

        ptr = GetExport("il2cpp_thread_walk_frame_stack");
        if (ptr != IntPtr.Zero)
            _il2cpp_thread_walk_frame_stack = Marshal.GetDelegateForFunctionPointer<il2cpp_thread_walk_frame_stack_t>(ptr);

        ptr = GetExport("il2cpp_current_thread_get_top_frame");
        if (ptr != IntPtr.Zero)
            _il2cpp_current_thread_get_top_frame = Marshal.GetDelegateForFunctionPointer<il2cpp_current_thread_get_top_frame_t>(ptr);

        ptr = GetExport("il2cpp_thread_get_top_frame");
        if (ptr != IntPtr.Zero)
            _il2cpp_thread_get_top_frame = Marshal.GetDelegateForFunctionPointer<il2cpp_thread_get_top_frame_t>(ptr);

        ptr = GetExport("il2cpp_current_thread_get_frame_at");
        if (ptr != IntPtr.Zero)
            _il2cpp_current_thread_get_frame_at = Marshal.GetDelegateForFunctionPointer<il2cpp_current_thread_get_frame_at_t>(ptr);

        ptr = GetExport("il2cpp_thread_get_frame_at");
        if (ptr != IntPtr.Zero)
            _il2cpp_thread_get_frame_at = Marshal.GetDelegateForFunctionPointer<il2cpp_thread_get_frame_at_t>(ptr);

        ptr = GetExport("il2cpp_current_thread_get_stack_depth");
        if (ptr != IntPtr.Zero)
            _il2cpp_current_thread_get_stack_depth = Marshal.GetDelegateForFunctionPointer<il2cpp_current_thread_get_stack_depth_t>(ptr);

        ptr = GetExport("il2cpp_thread_get_stack_depth");
        if (ptr != IntPtr.Zero)
            _il2cpp_thread_get_stack_depth = Marshal.GetDelegateForFunctionPointer<il2cpp_thread_get_stack_depth_t>(ptr);

        ptr = GetExport("il2cpp_type_get_object");
        if (ptr != IntPtr.Zero)
            _il2cpp_type_get_object = Marshal.GetDelegateForFunctionPointer<il2cpp_type_get_object_t>(ptr);

        ptr = GetExport("il2cpp_type_get_type");
        if (ptr != IntPtr.Zero)
            _il2cpp_type_get_type = Marshal.GetDelegateForFunctionPointer<il2cpp_type_get_type_t>(ptr);

        ptr = GetExport("il2cpp_type_get_class_or_element_class");
        if (ptr != IntPtr.Zero)
            _il2cpp_type_get_class_or_element_class = Marshal.GetDelegateForFunctionPointer<il2cpp_type_get_class_or_element_class_t>(ptr);

        ptr = GetExport("il2cpp_type_get_name");
        if (ptr != IntPtr.Zero)
            _il2cpp_type_get_name = Marshal.GetDelegateForFunctionPointer<il2cpp_type_get_name_t>(ptr);

        ptr = GetExport("il2cpp_type_is_byref");
        if (ptr != IntPtr.Zero)
            _il2cpp_type_is_byref = Marshal.GetDelegateForFunctionPointer<il2cpp_type_is_byref_t>(ptr);

        ptr = GetExport("il2cpp_type_get_attrs");
        if (ptr != IntPtr.Zero)
            _il2cpp_type_get_attrs = Marshal.GetDelegateForFunctionPointer<il2cpp_type_get_attrs_t>(ptr);

        ptr = GetExport("il2cpp_type_equals");
        if (ptr != IntPtr.Zero)
            _il2cpp_type_equals = Marshal.GetDelegateForFunctionPointer<il2cpp_type_equals_t>(ptr);

        ptr = GetExport("il2cpp_type_get_assembly_qualified_name");
        if (ptr != IntPtr.Zero)
            _il2cpp_type_get_assembly_qualified_name = Marshal.GetDelegateForFunctionPointer<il2cpp_type_get_assembly_qualified_name_t>(ptr);

        ptr = GetExport("il2cpp_image_get_assembly");
        if (ptr != IntPtr.Zero)
            _il2cpp_image_get_assembly = Marshal.GetDelegateForFunctionPointer<il2cpp_image_get_assembly_t>(ptr);

        ptr = GetExport("il2cpp_image_get_name");
        if (ptr != IntPtr.Zero)
            _il2cpp_image_get_name = Marshal.GetDelegateForFunctionPointer<il2cpp_image_get_name_t>(ptr);

        ptr = GetExport("il2cpp_image_get_filename");
        if (ptr != IntPtr.Zero)
            _il2cpp_image_get_filename = Marshal.GetDelegateForFunctionPointer<il2cpp_image_get_filename_t>(ptr);

        ptr = GetExport("il2cpp_image_get_entry_point");
        if (ptr != IntPtr.Zero)
            _il2cpp_image_get_entry_point = Marshal.GetDelegateForFunctionPointer<il2cpp_image_get_entry_point_t>(ptr);

        ptr = GetExport("il2cpp_image_get_class_count");
        if (ptr != IntPtr.Zero)
            _il2cpp_image_get_class_count = Marshal.GetDelegateForFunctionPointer<il2cpp_image_get_class_count_t>(ptr);

        ptr = GetExport("il2cpp_image_get_class");
        if (ptr != IntPtr.Zero)
            _il2cpp_image_get_class = Marshal.GetDelegateForFunctionPointer<il2cpp_image_get_class_t>(ptr);

        ptr = GetExport("il2cpp_capture_memory_snapshot");
        if (ptr != IntPtr.Zero)
            _il2cpp_capture_memory_snapshot = Marshal.GetDelegateForFunctionPointer<il2cpp_capture_memory_snapshot_t>(ptr);

        ptr = GetExport("il2cpp_free_captured_memory_snapshot");
        if (ptr != IntPtr.Zero)
            _il2cpp_free_captured_memory_snapshot = Marshal.GetDelegateForFunctionPointer<il2cpp_free_captured_memory_snapshot_t>(ptr);

        ptr = GetExport("il2cpp_set_find_plugin_callback");
        if (ptr != IntPtr.Zero)
            _il2cpp_set_find_plugin_callback = Marshal.GetDelegateForFunctionPointer<il2cpp_set_find_plugin_callback_t>(ptr);

        ptr = GetExport("il2cpp_register_log_callback");
        if (ptr != IntPtr.Zero)
            _il2cpp_register_log_callback = Marshal.GetDelegateForFunctionPointer<il2cpp_register_log_callback_t>(ptr);

        ptr = GetExport("il2cpp_debugger_set_agent_options");
        if (ptr != IntPtr.Zero)
            _il2cpp_debugger_set_agent_options = Marshal.GetDelegateForFunctionPointer<il2cpp_debugger_set_agent_options_t>(ptr);

        ptr = GetExport("il2cpp_is_debugger_attached");
        if (ptr != IntPtr.Zero)
            _il2cpp_is_debugger_attached = Marshal.GetDelegateForFunctionPointer<il2cpp_is_debugger_attached_t>(ptr);

        ptr = GetExport("il2cpp_unity_install_unitytls_interface");
        if (ptr != IntPtr.Zero)
            _il2cpp_unity_install_unitytls_interface = Marshal.GetDelegateForFunctionPointer<il2cpp_unity_install_unitytls_interface_t>(ptr);

        ptr = GetExport("il2cpp_custom_attrs_from_class");
        if (ptr != IntPtr.Zero)
            _il2cpp_custom_attrs_from_class = Marshal.GetDelegateForFunctionPointer<il2cpp_custom_attrs_from_class_t>(ptr);

        ptr = GetExport("il2cpp_custom_attrs_from_method");
        if (ptr != IntPtr.Zero)
            _il2cpp_custom_attrs_from_method = Marshal.GetDelegateForFunctionPointer<il2cpp_custom_attrs_from_method_t>(ptr);

        ptr = GetExport("il2cpp_custom_attrs_get_attr");
        if (ptr != IntPtr.Zero)
            _il2cpp_custom_attrs_get_attr = Marshal.GetDelegateForFunctionPointer<il2cpp_custom_attrs_get_attr_t>(ptr);

        ptr = GetExport("il2cpp_custom_attrs_has_attr");
        if (ptr != IntPtr.Zero)
            _il2cpp_custom_attrs_has_attr = Marshal.GetDelegateForFunctionPointer<il2cpp_custom_attrs_has_attr_t>(ptr);

        ptr = GetExport("il2cpp_custom_attrs_construct");
        if (ptr != IntPtr.Zero)
            _il2cpp_custom_attrs_construct = Marshal.GetDelegateForFunctionPointer<il2cpp_custom_attrs_construct_t>(ptr);

        ptr = GetExport("il2cpp_custom_attrs_free");
        if (ptr != IntPtr.Zero)
            _il2cpp_custom_attrs_free = Marshal.GetDelegateForFunctionPointer<il2cpp_custom_attrs_free_t>(ptr);
    }


    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_init_t(IntPtr domain_name);
    private static il2cpp_init_t _il2cpp_init;
    public static void il2cpp_init(IntPtr domain_name) => _il2cpp_init(domain_name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_init_utf16_t(IntPtr domain_name);
    private static il2cpp_init_utf16_t _il2cpp_init_utf16;
    public static void il2cpp_init_utf16(IntPtr domain_name) => _il2cpp_init_utf16(domain_name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_shutdown_t();
    private static il2cpp_shutdown_t _il2cpp_shutdown;
    public static void il2cpp_shutdown() => _il2cpp_shutdown();

    public static string? il2cpp_class_get_namespace_(IntPtr klass) => Marshal.PtrToStringUTF8(il2cpp_class_get_namespace(klass));
    public static string? il2cpp_class_get_name_(IntPtr klass) => Marshal.PtrToStringUTF8(il2cpp_class_get_name(klass));
    public static string? il2cpp_class_get_assemblyname_(IntPtr klass) => Marshal.PtrToStringUTF8(il2cpp_class_get_assemblyname(klass));
    public static string? il2cpp_field_get_name_(IntPtr field) => Marshal.PtrToStringUTF8(il2cpp_field_get_name(field));
    public static string? il2cpp_method_get_name_(IntPtr method) => Marshal.PtrToStringUTF8(il2cpp_method_get_name(method));
    public static string? il2cpp_method_get_param_name_(IntPtr method, uint index) => Marshal.PtrToStringUTF8(il2cpp_method_get_param_name(method, index));
    public static string? il2cpp_property_get_name_(IntPtr prop) => Marshal.PtrToStringUTF8(il2cpp_property_get_name(prop));
    public static string? il2cpp_type_get_name_(IntPtr type) => Marshal.PtrToStringUTF8(il2cpp_type_get_name(type));
    public static string? il2cpp_image_get_filename_(IntPtr image) => Marshal.PtrToStringUTF8(il2cpp_image_get_filename(image));
    public static string? il2cpp_image_get_name_(IntPtr image) => Marshal.PtrToStringUTF8(il2cpp_image_get_name(image));


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IntPtr il2cpp_method_get_from_reflection(IntPtr method)
    {
        if (UnityVersionHandler.HasGetMethodFromReflection) return _il2cpp_method_get_from_reflection(method);
        Il2CppReflectionMethod* reflectionMethod = (Il2CppReflectionMethod*)method;
        return (IntPtr)reflectionMethod->method;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_set_config_dir_t(IntPtr config_path);
    private static il2cpp_set_config_dir_t _il2cpp_set_config_dir;
    public static void il2cpp_set_config_dir(IntPtr config_path) => _il2cpp_set_config_dir(config_path);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_set_data_dir_t(IntPtr data_path);
    private static il2cpp_set_data_dir_t _il2cpp_set_data_dir;
    public static void il2cpp_set_data_dir(IntPtr data_path) => _il2cpp_set_data_dir(data_path);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_set_temp_dir_t(IntPtr temp_path);
    private static il2cpp_set_temp_dir_t _il2cpp_set_temp_dir;
    public static void il2cpp_set_temp_dir(IntPtr temp_path) => _il2cpp_set_temp_dir(temp_path);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_set_commandline_arguments_t(int argc, IntPtr argv, IntPtr basedir);
    private static il2cpp_set_commandline_arguments_t _il2cpp_set_commandline_arguments;
    public static void il2cpp_set_commandline_arguments(int argc, IntPtr argv, IntPtr basedir) => _il2cpp_set_commandline_arguments(argc, argv, basedir);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_set_commandline_arguments_utf16_t(int argc, IntPtr argv, IntPtr basedir);
    private static il2cpp_set_commandline_arguments_utf16_t _il2cpp_set_commandline_arguments_utf16;
    public static void il2cpp_set_commandline_arguments_utf16(int argc, IntPtr argv, IntPtr basedir) => _il2cpp_set_commandline_arguments_utf16(argc, argv, basedir);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_set_config_utf16_t(IntPtr executablePath);
    private static il2cpp_set_config_utf16_t _il2cpp_set_config_utf16;
    public static void il2cpp_set_config_utf16(IntPtr executablePath) => _il2cpp_set_config_utf16(executablePath);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_set_config_t(IntPtr executablePath);
    private static il2cpp_set_config_t _il2cpp_set_config;
    public static void il2cpp_set_config(IntPtr executablePath) => _il2cpp_set_config(executablePath);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_set_memory_callbacks_t(IntPtr callbacks);
    private static il2cpp_set_memory_callbacks_t _il2cpp_set_memory_callbacks;
    public static void il2cpp_set_memory_callbacks(IntPtr callbacks) => _il2cpp_set_memory_callbacks(callbacks);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_get_corlib_t();
    private static il2cpp_get_corlib_t _il2cpp_get_corlib;
    public static IntPtr il2cpp_get_corlib() => _il2cpp_get_corlib();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_add_internal_call_t(IntPtr name, IntPtr method);
    private static il2cpp_add_internal_call_t _il2cpp_add_internal_call;
    public static void il2cpp_add_internal_call(IntPtr name, IntPtr method) => _il2cpp_add_internal_call(name, method);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_resolve_icall_t([MarshalAs(UnmanagedType.LPStr)] string name);
    private static il2cpp_resolve_icall_t _il2cpp_resolve_icall;
    public static IntPtr il2cpp_resolve_icall([MarshalAs(UnmanagedType.LPStr)] string name) => _il2cpp_resolve_icall(name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_alloc_t(uint size);
    private static il2cpp_alloc_t _il2cpp_alloc;
    public static IntPtr il2cpp_alloc(uint size) => _il2cpp_alloc(size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_free_t(IntPtr ptr);
    private static il2cpp_free_t _il2cpp_free;
    public static void il2cpp_free(IntPtr ptr) => _il2cpp_free(ptr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_array_class_get_t(IntPtr element_class, uint rank);
    private static il2cpp_array_class_get_t _il2cpp_array_class_get;
    public static IntPtr il2cpp_array_class_get(IntPtr element_class, uint rank) => _il2cpp_array_class_get(element_class, rank);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint il2cpp_array_length_t(IntPtr array);
    private static il2cpp_array_length_t _il2cpp_array_length;
    public static uint il2cpp_array_length(IntPtr array) => _il2cpp_array_length(array);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint il2cpp_array_get_byte_length_t(IntPtr array);
    private static il2cpp_array_get_byte_length_t _il2cpp_array_get_byte_length;
    public static uint il2cpp_array_get_byte_length(IntPtr array) => _il2cpp_array_get_byte_length(array);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_array_new_t(IntPtr elementTypeInfo, ulong length);
    private static il2cpp_array_new_t _il2cpp_array_new;
    public static IntPtr il2cpp_array_new(IntPtr elementTypeInfo, ulong length) => _il2cpp_array_new(elementTypeInfo, length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_array_new_specific_t(IntPtr arrayTypeInfo, ulong length);
    private static il2cpp_array_new_specific_t _il2cpp_array_new_specific;
    public static IntPtr il2cpp_array_new_specific(IntPtr arrayTypeInfo, ulong length) => _il2cpp_array_new_specific(arrayTypeInfo, length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_array_new_full_t(IntPtr array_class, ref ulong lengths, ref ulong lower_bounds);
    private static il2cpp_array_new_full_t _il2cpp_array_new_full;
    public static IntPtr il2cpp_array_new_full(IntPtr array_class, ref ulong lengths, ref ulong lower_bounds) => _il2cpp_array_new_full(array_class, ref lengths, ref lower_bounds);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_bounded_array_class_get_t(IntPtr element_class, uint rank, [MarshalAs(UnmanagedType.I1)] bool bounded);
    private static il2cpp_bounded_array_class_get_t _il2cpp_bounded_array_class_get;
    public static IntPtr il2cpp_bounded_array_class_get(IntPtr element_class, uint rank, [MarshalAs(UnmanagedType.I1)] bool bounded) => _il2cpp_bounded_array_class_get(element_class, rank, bounded);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int il2cpp_array_element_size_t(IntPtr array_class);
    private static il2cpp_array_element_size_t _il2cpp_array_element_size;
    public static int il2cpp_array_element_size(IntPtr array_class) => _il2cpp_array_element_size(array_class);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_assembly_get_image_t(IntPtr assembly);
    private static il2cpp_assembly_get_image_t _il2cpp_assembly_get_image;
    public static IntPtr il2cpp_assembly_get_image(IntPtr assembly) => _il2cpp_assembly_get_image(assembly);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_class_enum_basetype_t(IntPtr klass);
    private static il2cpp_class_enum_basetype_t _il2cpp_class_enum_basetype;
    public static IntPtr il2cpp_class_enum_basetype(IntPtr klass) => _il2cpp_class_enum_basetype(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_class_is_generic_t(IntPtr klass);
    private static il2cpp_class_is_generic_t _il2cpp_class_is_generic;

    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_class_is_generic(IntPtr klass) => _il2cpp_class_is_generic(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_class_is_inflated_t(IntPtr klass);
    private static il2cpp_class_is_inflated_t _il2cpp_class_is_inflated;

    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_class_is_inflated(IntPtr klass) => _il2cpp_class_is_inflated(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_class_is_assignable_from_t(IntPtr klass, IntPtr oklass);
    private static il2cpp_class_is_assignable_from_t _il2cpp_class_is_assignable_from;

    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_class_is_assignable_from(IntPtr klass, IntPtr oklass) => _il2cpp_class_is_assignable_from(klass, oklass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_class_is_subclass_of_t(IntPtr klass, IntPtr klassc, [MarshalAs(UnmanagedType.I1)] bool check_interfaces);
    private static il2cpp_class_is_subclass_of_t _il2cpp_class_is_subclass_of;

    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_class_is_subclass_of(IntPtr klass, IntPtr klassc, [MarshalAs(UnmanagedType.I1)] bool check_interfaces) => _il2cpp_class_is_subclass_of(klass, klassc, check_interfaces);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_class_has_parent_t(IntPtr klass, IntPtr klassc);
    private static il2cpp_class_has_parent_t _il2cpp_class_has_parent;

    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_class_has_parent(IntPtr klass, IntPtr klassc) => _il2cpp_class_has_parent(klass, klassc);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_class_from_il2cpp_type_t(IntPtr type);
    private static il2cpp_class_from_il2cpp_type_t _il2cpp_class_from_il2cpp_type;
    public static IntPtr il2cpp_class_from_il2cpp_type(IntPtr type) => _il2cpp_class_from_il2cpp_type(type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_class_from_name_t(IntPtr image, [MarshalAs(UnmanagedType.LPUTF8Str)] string namespaze, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    private static il2cpp_class_from_name_t _il2cpp_class_from_name;
    public static IntPtr il2cpp_class_from_name(IntPtr image, [MarshalAs(UnmanagedType.LPUTF8Str)] string namespaze, [MarshalAs(UnmanagedType.LPUTF8Str)] string name) => _il2cpp_class_from_name(image, namespaze, name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_class_from_system_type_t(IntPtr type);
    private static il2cpp_class_from_system_type_t _il2cpp_class_from_system_type;
    public static IntPtr il2cpp_class_from_system_type(IntPtr type) => _il2cpp_class_from_system_type(type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_class_get_element_class_t(IntPtr klass);
    private static il2cpp_class_get_element_class_t _il2cpp_class_get_element_class;
    public static IntPtr il2cpp_class_get_element_class(IntPtr klass) => _il2cpp_class_get_element_class(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_class_get_events_t(IntPtr klass, ref IntPtr iter);
    private static il2cpp_class_get_events_t _il2cpp_class_get_events;
    public static IntPtr il2cpp_class_get_events(IntPtr klass, ref IntPtr iter) => _il2cpp_class_get_events(klass, ref iter);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_class_get_fields_t(IntPtr klass, ref IntPtr iter);
    private static il2cpp_class_get_fields_t _il2cpp_class_get_fields;
    public static IntPtr il2cpp_class_get_fields(IntPtr klass, ref IntPtr iter) => _il2cpp_class_get_fields(klass, ref iter);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_class_get_nested_types_t(IntPtr klass, ref IntPtr iter);
    private static il2cpp_class_get_nested_types_t _il2cpp_class_get_nested_types;
    public static IntPtr il2cpp_class_get_nested_types(IntPtr klass, ref IntPtr iter) => _il2cpp_class_get_nested_types(klass, ref iter);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_class_get_interfaces_t(IntPtr klass, ref IntPtr iter);
    private static il2cpp_class_get_interfaces_t _il2cpp_class_get_interfaces;
    public static IntPtr il2cpp_class_get_interfaces(IntPtr klass, ref IntPtr iter) => _il2cpp_class_get_interfaces(klass, ref iter);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_class_get_properties_t(IntPtr klass, ref IntPtr iter);
    private static il2cpp_class_get_properties_t _il2cpp_class_get_properties;
    public static IntPtr il2cpp_class_get_properties(IntPtr klass, ref IntPtr iter) => _il2cpp_class_get_properties(klass, ref iter);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_class_get_property_from_name_t(IntPtr klass, IntPtr name);
    private static il2cpp_class_get_property_from_name_t _il2cpp_class_get_property_from_name;
    public static IntPtr il2cpp_class_get_property_from_name(IntPtr klass, IntPtr name) => _il2cpp_class_get_property_from_name(klass, name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_class_get_field_from_name_t(IntPtr klass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    private static il2cpp_class_get_field_from_name_t _il2cpp_class_get_field_from_name;
    public static IntPtr il2cpp_class_get_field_from_name(IntPtr klass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name) => _il2cpp_class_get_field_from_name(klass, name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_class_get_methods_t(IntPtr klass, ref IntPtr iter);
    private static il2cpp_class_get_methods_t _il2cpp_class_get_methods;
    public static IntPtr il2cpp_class_get_methods(IntPtr klass, ref IntPtr iter) => _il2cpp_class_get_methods(klass, ref iter);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_class_get_method_from_name_t(IntPtr klass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int argsCount);
    private static il2cpp_class_get_method_from_name_t _il2cpp_class_get_method_from_name;
    public static IntPtr il2cpp_class_get_method_from_name(IntPtr klass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int argsCount) => _il2cpp_class_get_method_from_name(klass, name, argsCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate nint il2cpp_class_get_name_t(IntPtr klass);
    private static il2cpp_class_get_name_t _il2cpp_class_get_name;
    public static nint il2cpp_class_get_name(IntPtr klass) => _il2cpp_class_get_name(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate nint il2cpp_class_get_namespace_t(IntPtr klass);
    private static il2cpp_class_get_namespace_t _il2cpp_class_get_namespace;
    public static nint il2cpp_class_get_namespace(IntPtr klass) => _il2cpp_class_get_namespace(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_class_get_parent_t(IntPtr klass);
    private static il2cpp_class_get_parent_t _il2cpp_class_get_parent;
    public static IntPtr il2cpp_class_get_parent(IntPtr klass) => _il2cpp_class_get_parent(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_class_get_declaring_type_t(IntPtr klass);
    private static il2cpp_class_get_declaring_type_t _il2cpp_class_get_declaring_type;
    public static IntPtr il2cpp_class_get_declaring_type(IntPtr klass) => _il2cpp_class_get_declaring_type(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int il2cpp_class_instance_size_t(IntPtr klass);
    private static il2cpp_class_instance_size_t _il2cpp_class_instance_size;
    public static int il2cpp_class_instance_size(IntPtr klass) => _il2cpp_class_instance_size(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint il2cpp_class_num_fields_t(IntPtr enumKlass);
    private static il2cpp_class_num_fields_t _il2cpp_class_num_fields;
    public static uint il2cpp_class_num_fields(IntPtr enumKlass) => _il2cpp_class_num_fields(enumKlass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_class_is_valuetype_t(IntPtr klass);
    private static il2cpp_class_is_valuetype_t _il2cpp_class_is_valuetype;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_class_is_valuetype(IntPtr klass) => _il2cpp_class_is_valuetype(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int il2cpp_class_value_size_t(IntPtr klass, ref uint align);
    private static il2cpp_class_value_size_t _il2cpp_class_value_size;
    [return: MarshalAs(UnmanagedType.I1)]
    public static int il2cpp_class_value_size(IntPtr klass, ref uint align) => _il2cpp_class_value_size(klass, ref align);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_class_is_blittable_t(IntPtr klass);
    private static il2cpp_class_is_blittable_t _il2cpp_class_is_blittable;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_class_is_blittable(IntPtr klass) => _il2cpp_class_is_blittable(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int il2cpp_class_get_flags_t(IntPtr klass);
    private static il2cpp_class_get_flags_t _il2cpp_class_get_flags;
    public static int il2cpp_class_get_flags(IntPtr klass) => _il2cpp_class_get_flags(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_class_is_abstract_t(IntPtr klass);
    private static il2cpp_class_is_abstract_t _il2cpp_class_is_abstract;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_class_is_abstract(IntPtr klass) => _il2cpp_class_is_abstract(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_class_is_interface_t(IntPtr klass);
    private static il2cpp_class_is_interface_t _il2cpp_class_is_interface;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_class_is_interface(IntPtr klass) => _il2cpp_class_is_interface(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int il2cpp_class_array_element_size_t(IntPtr klass);
    private static il2cpp_class_array_element_size_t _il2cpp_class_array_element_size;
    public static int il2cpp_class_array_element_size(IntPtr klass) => _il2cpp_class_array_element_size(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_class_from_type_t(IntPtr type);
    private static il2cpp_class_from_type_t _il2cpp_class_from_type;
    public static IntPtr il2cpp_class_from_type(IntPtr type) => _il2cpp_class_from_type(type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_class_get_type_t(IntPtr klass);
    private static il2cpp_class_get_type_t _il2cpp_class_get_type;
    public static IntPtr il2cpp_class_get_type(IntPtr klass) => _il2cpp_class_get_type(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint il2cpp_class_get_type_token_t(IntPtr klass);
    private static il2cpp_class_get_type_token_t _il2cpp_class_get_type_token;
    public static uint il2cpp_class_get_type_token(IntPtr klass) => _il2cpp_class_get_type_token(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_class_has_attribute_t(IntPtr klass, IntPtr attr_class);
    private static il2cpp_class_has_attribute_t _il2cpp_class_has_attribute;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_class_has_attribute(IntPtr klass, IntPtr attr_class) => _il2cpp_class_has_attribute(klass, attr_class);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_class_has_references_t(IntPtr klass);
    private static il2cpp_class_has_references_t _il2cpp_class_has_references;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_class_has_references(IntPtr klass) => _il2cpp_class_has_references(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_class_is_enum_t(IntPtr klass);
    private static il2cpp_class_is_enum_t _il2cpp_class_is_enum;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_class_is_enum(IntPtr klass) => _il2cpp_class_is_enum(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_class_get_image_t(IntPtr klass);
    private static il2cpp_class_get_image_t _il2cpp_class_get_image;
    public static IntPtr il2cpp_class_get_image(IntPtr klass) => _il2cpp_class_get_image(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate nint il2cpp_class_get_assemblyname_t(IntPtr klass);
    private static il2cpp_class_get_assemblyname_t _il2cpp_class_get_assemblyname;
    public static nint il2cpp_class_get_assemblyname(IntPtr klass) => _il2cpp_class_get_assemblyname(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int il2cpp_class_get_rank_t(IntPtr klass);
    private static il2cpp_class_get_rank_t _il2cpp_class_get_rank;
    public static int il2cpp_class_get_rank(IntPtr klass) => _il2cpp_class_get_rank(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint il2cpp_class_get_bitmap_size_t(IntPtr klass);
    private static il2cpp_class_get_bitmap_size_t _il2cpp_class_get_bitmap_size;
    public static uint il2cpp_class_get_bitmap_size(IntPtr klass) => _il2cpp_class_get_bitmap_size(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_class_get_bitmap_t(IntPtr klass, ref uint bitmap);
    private static il2cpp_class_get_bitmap_t _il2cpp_class_get_bitmap;
    public static void il2cpp_class_get_bitmap(IntPtr klass, ref uint bitmap) => _il2cpp_class_get_bitmap(klass, ref bitmap);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_stats_dump_to_file_t(IntPtr path);
    private static il2cpp_stats_dump_to_file_t _il2cpp_stats_dump_to_file;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_stats_dump_to_file(IntPtr path) => _il2cpp_stats_dump_to_file(path);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_domain_get_t();
    private static il2cpp_domain_get_t _il2cpp_domain_get;
    public static IntPtr il2cpp_domain_get() => _il2cpp_domain_get();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_domain_assembly_open_t(IntPtr domain, IntPtr name);
    private static il2cpp_domain_assembly_open_t _il2cpp_domain_assembly_open;
    public static IntPtr il2cpp_domain_assembly_open(IntPtr domain, IntPtr name) => _il2cpp_domain_assembly_open(domain, name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr* il2cpp_domain_get_assemblies_t(IntPtr domain, ref uint size);
    private static il2cpp_domain_get_assemblies_t _il2cpp_domain_get_assemblies;
    public static IntPtr* il2cpp_domain_get_assemblies(IntPtr domain, ref uint size) => _il2cpp_domain_get_assemblies(domain, ref size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_exception_from_name_msg_t(IntPtr image, IntPtr name_space, IntPtr name, IntPtr msg);
    private static il2cpp_exception_from_name_msg_t _il2cpp_exception_from_name_msg;
    public static IntPtr il2cpp_exception_from_name_msg(IntPtr image, IntPtr name_space, IntPtr name, IntPtr msg) => _il2cpp_exception_from_name_msg(image, name_space, name, msg);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_get_exception_argument_null_t(IntPtr arg);
    private static il2cpp_get_exception_argument_null_t _il2cpp_get_exception_argument_null;
    public static IntPtr il2cpp_get_exception_argument_null(IntPtr arg) => _il2cpp_get_exception_argument_null(arg);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_format_exception_t(IntPtr ex, void* message, int message_size);
    private static il2cpp_format_exception_t _il2cpp_format_exception;
    public static void il2cpp_format_exception(IntPtr ex, void* message, int message_size) => _il2cpp_format_exception(ex, message, message_size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_format_stack_trace_t(IntPtr ex, void* output, int output_size);
    private static il2cpp_format_stack_trace_t _il2cpp_format_stack_trace;
    public static void il2cpp_format_stack_trace(IntPtr ex, void* output, int output_size) => _il2cpp_format_stack_trace(ex, output, output_size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_unhandled_exception_t(IntPtr ex);
    private static il2cpp_unhandled_exception_t _il2cpp_unhandled_exception;
    public static void il2cpp_unhandled_exception(IntPtr ex) => _il2cpp_unhandled_exception(ex);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int il2cpp_field_get_flags_t(IntPtr field);
    private static il2cpp_field_get_flags_t _il2cpp_field_get_flags;
    public static int il2cpp_field_get_flags(IntPtr field) => _il2cpp_field_get_flags(field);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate nint il2cpp_field_get_name_t(IntPtr field);
    private static il2cpp_field_get_name_t _il2cpp_field_get_name;
    public static nint il2cpp_field_get_name(IntPtr field) => _il2cpp_field_get_name(field);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_field_get_parent_t(IntPtr field);
    private static il2cpp_field_get_parent_t _il2cpp_field_get_parent;
    public static IntPtr il2cpp_field_get_parent(IntPtr field) => _il2cpp_field_get_parent(field);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint il2cpp_field_get_offset_t(IntPtr field);
    private static il2cpp_field_get_offset_t _il2cpp_field_get_offset;
    public static uint il2cpp_field_get_offset(IntPtr field) => _il2cpp_field_get_offset(field);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_field_get_type_t(IntPtr field);
    private static il2cpp_field_get_type_t _il2cpp_field_get_type;
    public static IntPtr il2cpp_field_get_type(IntPtr field) => _il2cpp_field_get_type(field);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_field_get_value_t(IntPtr obj, IntPtr field, void* value);
    private static il2cpp_field_get_value_t _il2cpp_field_get_value;
    public static void il2cpp_field_get_value(IntPtr obj, IntPtr field, void* value) => _il2cpp_field_get_value(obj, field, value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_field_get_value_object_t(IntPtr field, IntPtr obj);
    private static il2cpp_field_get_value_object_t _il2cpp_field_get_value_object;
    public static IntPtr il2cpp_field_get_value_object(IntPtr field, IntPtr obj) => _il2cpp_field_get_value_object(field, obj);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_field_has_attribute_t(IntPtr field, IntPtr attr_class);
    private static il2cpp_field_has_attribute_t _il2cpp_field_has_attribute;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_field_has_attribute(IntPtr field, IntPtr attr_class) => _il2cpp_field_has_attribute(field, attr_class);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_field_set_value_t(IntPtr obj, IntPtr field, void* value);
    private static il2cpp_field_set_value_t _il2cpp_field_set_value;
    public static void il2cpp_field_set_value(IntPtr obj, IntPtr field, void* value) => _il2cpp_field_set_value(obj, field, value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_field_static_get_value_t(IntPtr field, void* value);
    private static il2cpp_field_static_get_value_t _il2cpp_field_static_get_value;
    public static void il2cpp_field_static_get_value(IntPtr field, void* value) => _il2cpp_field_static_get_value(field, value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_field_static_set_value_t(IntPtr field, void* value);
    private static il2cpp_field_static_set_value_t _il2cpp_field_static_set_value;
    public static void il2cpp_field_static_set_value(IntPtr field, void* value) => _il2cpp_field_static_set_value(field, value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_field_set_value_object_t(IntPtr instance, IntPtr field, IntPtr value);
    private static il2cpp_field_set_value_object_t _il2cpp_field_set_value_object;
    public static void il2cpp_field_set_value_object(IntPtr instance, IntPtr field, IntPtr value) => _il2cpp_field_set_value_object(instance, field, value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_gc_collect_t(int maxGenerations);
    private static il2cpp_gc_collect_t _il2cpp_gc_collect;
    public static void il2cpp_gc_collect(int maxGenerations) => _il2cpp_gc_collect(maxGenerations);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int il2cpp_gc_collect_a_little_t();
    private static il2cpp_gc_collect_a_little_t _il2cpp_gc_collect_a_little;
    public static int il2cpp_gc_collect_a_little() => _il2cpp_gc_collect_a_little();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_gc_disable_t();
    private static il2cpp_gc_disable_t _il2cpp_gc_disable;
    public static void il2cpp_gc_disable() => _il2cpp_gc_disable();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_gc_enable_t();
    private static il2cpp_gc_enable_t _il2cpp_gc_enable;
    public static void il2cpp_gc_enable() => _il2cpp_gc_enable();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_gc_is_disabled_t();
    private static il2cpp_gc_is_disabled_t _il2cpp_gc_is_disabled;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_gc_is_disabled() => _il2cpp_gc_is_disabled();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate long il2cpp_gc_get_used_size_t();
    private static il2cpp_gc_get_used_size_t _il2cpp_gc_get_used_size;
    public static long il2cpp_gc_get_used_size() => _il2cpp_gc_get_used_size();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate long il2cpp_gc_get_heap_size_t();
    private static il2cpp_gc_get_heap_size_t _il2cpp_gc_get_heap_size;
    public static long il2cpp_gc_get_heap_size() => _il2cpp_gc_get_heap_size();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_gc_wbarrier_set_field_t(IntPtr obj, IntPtr targetAddress, IntPtr gcObj);
    private static il2cpp_gc_wbarrier_set_field_t _il2cpp_gc_wbarrier_set_field;
    public static void il2cpp_gc_wbarrier_set_field(IntPtr obj, IntPtr targetAddress, IntPtr gcObj) => _il2cpp_gc_wbarrier_set_field(obj, targetAddress, gcObj);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate nint il2cpp_gchandle_new_t(IntPtr obj, [MarshalAs(UnmanagedType.I1)] bool pinned);
    private static il2cpp_gchandle_new_t _il2cpp_gchandle_new;
    public static nint il2cpp_gchandle_new(IntPtr obj, [MarshalAs(UnmanagedType.I1)] bool pinned) => _il2cpp_gchandle_new(obj, pinned);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate nint il2cpp_gchandle_new_weakref_t(IntPtr obj, [MarshalAs(UnmanagedType.I1)] bool track_resurrection);
    private static il2cpp_gchandle_new_weakref_t _il2cpp_gchandle_new_weakref;
    public static nint il2cpp_gchandle_new_weakref(IntPtr obj, [MarshalAs(UnmanagedType.I1)] bool track_resurrection) => _il2cpp_gchandle_new_weakref(obj, track_resurrection);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_gchandle_get_target_t(nint gchandle);
    private static il2cpp_gchandle_get_target_t _il2cpp_gchandle_get_target;
    public static IntPtr il2cpp_gchandle_get_target(nint gchandle) => _il2cpp_gchandle_get_target(gchandle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_gchandle_free_t(nint gchandle);
    private static il2cpp_gchandle_free_t _il2cpp_gchandle_free;
    public static void il2cpp_gchandle_free(nint gchandle) => _il2cpp_gchandle_free(gchandle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_unity_liveness_calculation_begin_t(IntPtr filter, int max_object_count, IntPtr callback, IntPtr userdata, IntPtr onWorldStarted, IntPtr onWorldStopped);
    private static il2cpp_unity_liveness_calculation_begin_t _il2cpp_unity_liveness_calculation_begin;
    public static IntPtr il2cpp_unity_liveness_calculation_begin(IntPtr filter, int max_object_count, IntPtr callback, IntPtr userdata, IntPtr onWorldStarted, IntPtr onWorldStopped) => _il2cpp_unity_liveness_calculation_begin(filter, max_object_count, callback, userdata, onWorldStarted, onWorldStopped);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_unity_liveness_calculation_end_t(IntPtr state);
    private static il2cpp_unity_liveness_calculation_end_t _il2cpp_unity_liveness_calculation_end;
    public static void il2cpp_unity_liveness_calculation_end(IntPtr state) => _il2cpp_unity_liveness_calculation_end(state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_unity_liveness_calculation_from_root_t(IntPtr root, IntPtr state);
    private static il2cpp_unity_liveness_calculation_from_root_t _il2cpp_unity_liveness_calculation_from_root;
    public static void il2cpp_unity_liveness_calculation_from_root(IntPtr root, IntPtr state) => _il2cpp_unity_liveness_calculation_from_root(root, state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_unity_liveness_calculation_from_statics_t(IntPtr state);
    private static il2cpp_unity_liveness_calculation_from_statics_t _il2cpp_unity_liveness_calculation_from_statics;
    public static void il2cpp_unity_liveness_calculation_from_statics(IntPtr state) => _il2cpp_unity_liveness_calculation_from_statics(state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_method_get_return_type_t(IntPtr method);
    private static il2cpp_method_get_return_type_t _il2cpp_method_get_return_type;
    public static IntPtr il2cpp_method_get_return_type(IntPtr method) => _il2cpp_method_get_return_type(method);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_method_get_declaring_type_t(IntPtr method);
    private static il2cpp_method_get_declaring_type_t _il2cpp_method_get_declaring_type;
    public static IntPtr il2cpp_method_get_declaring_type(IntPtr method) => _il2cpp_method_get_declaring_type(method);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate nint il2cpp_method_get_name_t(IntPtr method);
    private static il2cpp_method_get_name_t _il2cpp_method_get_name;
    public static nint il2cpp_method_get_name(IntPtr method) => _il2cpp_method_get_name(method);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr _il2cpp_method_get_from_reflection_t(IntPtr method);
    private static _il2cpp_method_get_from_reflection_t __il2cpp_method_get_from_reflection;
    public static IntPtr _il2cpp_method_get_from_reflection(IntPtr method) => __il2cpp_method_get_from_reflection(method);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_method_get_object_t(IntPtr method, IntPtr refclass);
    private static il2cpp_method_get_object_t _il2cpp_method_get_object;
    public static IntPtr il2cpp_method_get_object(IntPtr method, IntPtr refclass) => _il2cpp_method_get_object(method, refclass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_method_is_generic_t(IntPtr method);
    private static il2cpp_method_is_generic_t _il2cpp_method_is_generic;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_method_is_generic(IntPtr method) => _il2cpp_method_is_generic(method);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_method_is_inflated_t(IntPtr method);
    private static il2cpp_method_is_inflated_t _il2cpp_method_is_inflated;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_method_is_inflated(IntPtr method) => _il2cpp_method_is_inflated(method);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_method_is_instance_t(IntPtr method);
    private static il2cpp_method_is_instance_t _il2cpp_method_is_instance;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_method_is_instance(IntPtr method) => _il2cpp_method_is_instance(method);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint il2cpp_method_get_param_count_t(IntPtr method);
    private static il2cpp_method_get_param_count_t _il2cpp_method_get_param_count;
    public static uint il2cpp_method_get_param_count(IntPtr method) => _il2cpp_method_get_param_count(method);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_method_get_param_t(IntPtr method, uint index);
    private static il2cpp_method_get_param_t _il2cpp_method_get_param;
    public static IntPtr il2cpp_method_get_param(IntPtr method, uint index) => _il2cpp_method_get_param(method, index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_method_get_class_t(IntPtr method);
    private static il2cpp_method_get_class_t _il2cpp_method_get_class;
    public static IntPtr il2cpp_method_get_class(IntPtr method) => _il2cpp_method_get_class(method);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_method_has_attribute_t(IntPtr method, IntPtr attr_class);
    private static il2cpp_method_has_attribute_t _il2cpp_method_has_attribute;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_method_has_attribute(IntPtr method, IntPtr attr_class) => _il2cpp_method_has_attribute(method, attr_class);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint il2cpp_method_get_flags_t(IntPtr method, ref uint iflags);
    private static il2cpp_method_get_flags_t _il2cpp_method_get_flags;
    public static uint il2cpp_method_get_flags(IntPtr method, ref uint iflags) => _il2cpp_method_get_flags(method, ref iflags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint il2cpp_method_get_token_t(IntPtr method);
    private static il2cpp_method_get_token_t _il2cpp_method_get_token;
    public static uint il2cpp_method_get_token(IntPtr method) => _il2cpp_method_get_token(method);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate nint il2cpp_method_get_param_name_t(IntPtr method, uint index);
    private static il2cpp_method_get_param_name_t _il2cpp_method_get_param_name;
    public static nint il2cpp_method_get_param_name(IntPtr method, uint index) => _il2cpp_method_get_param_name(method, index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_profiler_install_t(IntPtr prof, IntPtr shutdown_callback);
    private static il2cpp_profiler_install_t _il2cpp_profiler_install;
    public static void il2cpp_profiler_install(IntPtr prof, IntPtr shutdown_callback) => _il2cpp_profiler_install(prof, shutdown_callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_profiler_install_enter_leave_t(IntPtr enter, IntPtr fleave);
    private static il2cpp_profiler_install_enter_leave_t _il2cpp_profiler_install_enter_leave;
    public static void il2cpp_profiler_install_enter_leave(IntPtr enter, IntPtr fleave) => _il2cpp_profiler_install_enter_leave(enter, fleave);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_profiler_install_allocation_t(IntPtr callback);
    private static il2cpp_profiler_install_allocation_t _il2cpp_profiler_install_allocation;
    public static void il2cpp_profiler_install_allocation(IntPtr callback) => _il2cpp_profiler_install_allocation(callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_profiler_install_gc_t(IntPtr callback, IntPtr heap_resize_callback);
    private static il2cpp_profiler_install_gc_t _il2cpp_profiler_install_gc;
    public static void il2cpp_profiler_install_gc(IntPtr callback, IntPtr heap_resize_callback) => _il2cpp_profiler_install_gc(callback, heap_resize_callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_profiler_install_fileio_t(IntPtr callback);
    private static il2cpp_profiler_install_fileio_t _il2cpp_profiler_install_fileio;
    public static void il2cpp_profiler_install_fileio(IntPtr callback) => _il2cpp_profiler_install_fileio(callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_profiler_install_thread_t(IntPtr start, IntPtr end);
    private static il2cpp_profiler_install_thread_t _il2cpp_profiler_install_thread;
    public static void il2cpp_profiler_install_thread(IntPtr start, IntPtr end) => _il2cpp_profiler_install_thread(start, end);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint il2cpp_property_get_flags_t(IntPtr prop);
    private static il2cpp_property_get_flags_t _il2cpp_property_get_flags;
    public static uint il2cpp_property_get_flags(IntPtr prop) => _il2cpp_property_get_flags(prop);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_property_get_get_method_t(IntPtr prop);
    private static il2cpp_property_get_get_method_t _il2cpp_property_get_get_method;
    public static IntPtr il2cpp_property_get_get_method(IntPtr prop) => _il2cpp_property_get_get_method(prop);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_property_get_set_method_t(IntPtr prop);
    private static il2cpp_property_get_set_method_t _il2cpp_property_get_set_method;
    public static IntPtr il2cpp_property_get_set_method(IntPtr prop) => _il2cpp_property_get_set_method(prop);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate nint il2cpp_property_get_name_t(IntPtr prop);
    private static il2cpp_property_get_name_t _il2cpp_property_get_name;
    public static nint il2cpp_property_get_name(IntPtr prop) => _il2cpp_property_get_name(prop);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_property_get_parent_t(IntPtr prop);
    private static il2cpp_property_get_parent_t _il2cpp_property_get_parent;
    public static IntPtr il2cpp_property_get_parent(IntPtr prop) => _il2cpp_property_get_parent(prop);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_object_get_class_t(IntPtr obj);
    private static il2cpp_object_get_class_t _il2cpp_object_get_class;
    public static IntPtr il2cpp_object_get_class(IntPtr obj) => _il2cpp_object_get_class(obj);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint il2cpp_object_get_size_t(IntPtr obj);
    private static il2cpp_object_get_size_t _il2cpp_object_get_size;
    public static uint il2cpp_object_get_size(IntPtr obj) => _il2cpp_object_get_size(obj);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_object_get_virtual_method_t(IntPtr obj, IntPtr method);
    private static il2cpp_object_get_virtual_method_t _il2cpp_object_get_virtual_method;
    public static IntPtr il2cpp_object_get_virtual_method(IntPtr obj, IntPtr method) => _il2cpp_object_get_virtual_method(obj, method);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_object_new_t(IntPtr klass);
    private static il2cpp_object_new_t _il2cpp_object_new;
    public static IntPtr il2cpp_object_new(IntPtr klass) => _il2cpp_object_new(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_object_unbox_t(IntPtr obj);
    private static il2cpp_object_unbox_t _il2cpp_object_unbox;
    public static IntPtr il2cpp_object_unbox(IntPtr obj) => _il2cpp_object_unbox(obj);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_value_box_t(IntPtr klass, IntPtr data);
    private static il2cpp_value_box_t _il2cpp_value_box;
    public static IntPtr il2cpp_value_box(IntPtr klass, IntPtr data) => _il2cpp_value_box(klass, data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_monitor_enter_t(IntPtr obj);
    private static il2cpp_monitor_enter_t _il2cpp_monitor_enter;
    public static void il2cpp_monitor_enter(IntPtr obj) => _il2cpp_monitor_enter(obj);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_monitor_try_enter_t(IntPtr obj, uint timeout);
    private static il2cpp_monitor_try_enter_t _il2cpp_monitor_try_enter;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_monitor_try_enter(IntPtr obj, uint timeout) => _il2cpp_monitor_try_enter(obj, timeout);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_monitor_exit_t(IntPtr obj);
    private static il2cpp_monitor_exit_t _il2cpp_monitor_exit;
    public static void il2cpp_monitor_exit(IntPtr obj) => _il2cpp_monitor_exit(obj);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_monitor_pulse_t(IntPtr obj);
    private static il2cpp_monitor_pulse_t _il2cpp_monitor_pulse;
    public static void il2cpp_monitor_pulse(IntPtr obj) => _il2cpp_monitor_pulse(obj);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_monitor_pulse_all_t(IntPtr obj);
    private static il2cpp_monitor_pulse_all_t _il2cpp_monitor_pulse_all;
    public static void il2cpp_monitor_pulse_all(IntPtr obj) => _il2cpp_monitor_pulse_all(obj);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_monitor_wait_t(IntPtr obj);
    private static il2cpp_monitor_wait_t _il2cpp_monitor_wait;
    public static void il2cpp_monitor_wait(IntPtr obj) => _il2cpp_monitor_wait(obj);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_monitor_try_wait_t(IntPtr obj, uint timeout);
    private static il2cpp_monitor_try_wait_t _il2cpp_monitor_try_wait;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_monitor_try_wait(IntPtr obj, uint timeout) => _il2cpp_monitor_try_wait(obj, timeout);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_runtime_invoke_t(IntPtr method, IntPtr obj, void** param, ref IntPtr exc);
    private static il2cpp_runtime_invoke_t _il2cpp_runtime_invoke;
    public static IntPtr il2cpp_runtime_invoke(IntPtr method, IntPtr obj, void** param, ref IntPtr exc) => _il2cpp_runtime_invoke(method, obj, param, ref exc);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_runtime_invoke_convert_args_t(IntPtr method, IntPtr obj, void** param, int paramCount, ref IntPtr exc);
    private static il2cpp_runtime_invoke_convert_args_t _il2cpp_runtime_invoke_convert_args;
    public static IntPtr il2cpp_runtime_invoke_convert_args(IntPtr method, IntPtr obj, void** param, int paramCount, ref IntPtr exc) => _il2cpp_runtime_invoke_convert_args(method, obj, param, paramCount, ref exc);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_runtime_class_init_t(IntPtr klass);
    private static il2cpp_runtime_class_init_t _il2cpp_runtime_class_init;
    public static void il2cpp_runtime_class_init(IntPtr klass) => _il2cpp_runtime_class_init(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_runtime_object_init_t(IntPtr obj);
    private static il2cpp_runtime_object_init_t _il2cpp_runtime_object_init;
    public static void il2cpp_runtime_object_init(IntPtr obj) => _il2cpp_runtime_object_init(obj);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_runtime_object_init_exception_t(IntPtr obj, ref IntPtr exc);
    private static il2cpp_runtime_object_init_exception_t _il2cpp_runtime_object_init_exception;
    public static void il2cpp_runtime_object_init_exception(IntPtr obj, ref IntPtr exc) => _il2cpp_runtime_object_init_exception(obj, ref exc);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int il2cpp_string_length_t(IntPtr str);
    private static il2cpp_string_length_t _il2cpp_string_length;
    public static int il2cpp_string_length(IntPtr str) => _il2cpp_string_length(str);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate char* il2cpp_string_chars_t(IntPtr str);
    private static il2cpp_string_chars_t _il2cpp_string_chars;
    public static char* il2cpp_string_chars(IntPtr str) => _il2cpp_string_chars(str);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_string_new_t(string str);
    private static il2cpp_string_new_t _il2cpp_string_new;
    public static IntPtr il2cpp_string_new(string str) => _il2cpp_string_new(str);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_string_new_len_t(string str, uint length);
    private static il2cpp_string_new_len_t _il2cpp_string_new_len;
    public static IntPtr il2cpp_string_new_len(string str, uint length) => _il2cpp_string_new_len(str, length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_string_new_utf16_t(char* text, int len);
    private static il2cpp_string_new_utf16_t _il2cpp_string_new_utf16;
    public static IntPtr il2cpp_string_new_utf16(char* text, int len) => _il2cpp_string_new_utf16(text, len);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_string_new_wrapper_t(string str);
    private static il2cpp_string_new_wrapper_t _il2cpp_string_new_wrapper;
    public static IntPtr il2cpp_string_new_wrapper(string str) => _il2cpp_string_new_wrapper(str);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_string_intern_t(string str);
    private static il2cpp_string_intern_t _il2cpp_string_intern;
    public static IntPtr il2cpp_string_intern(string str) => _il2cpp_string_intern(str);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_string_is_interned_t(string str);
    private static il2cpp_string_is_interned_t _il2cpp_string_is_interned;
    public static IntPtr il2cpp_string_is_interned(string str) => _il2cpp_string_is_interned(str);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_thread_current_t();
    private static il2cpp_thread_current_t _il2cpp_thread_current;
    public static IntPtr il2cpp_thread_current() => _il2cpp_thread_current();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_thread_attach_t(IntPtr domain);
    private static il2cpp_thread_attach_t _il2cpp_thread_attach;
    public static IntPtr il2cpp_thread_attach(IntPtr domain) => _il2cpp_thread_attach(domain);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_thread_detach_t(IntPtr thread);
    private static il2cpp_thread_detach_t _il2cpp_thread_detach;
    public static void il2cpp_thread_detach(IntPtr thread) => _il2cpp_thread_detach(thread);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void** il2cpp_thread_get_all_attached_threads_t(ref uint size);
    private static il2cpp_thread_get_all_attached_threads_t _il2cpp_thread_get_all_attached_threads;
    public static void** il2cpp_thread_get_all_attached_threads(ref uint size) => _il2cpp_thread_get_all_attached_threads(ref size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_is_vm_thread_t(IntPtr thread);
    private static il2cpp_is_vm_thread_t _il2cpp_is_vm_thread;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_is_vm_thread(IntPtr thread) => _il2cpp_is_vm_thread(thread);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_current_thread_walk_frame_stack_t(IntPtr func, IntPtr user_data);
    private static il2cpp_current_thread_walk_frame_stack_t _il2cpp_current_thread_walk_frame_stack;
    public static void il2cpp_current_thread_walk_frame_stack(IntPtr func, IntPtr user_data) => _il2cpp_current_thread_walk_frame_stack(func, user_data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_thread_walk_frame_stack_t(IntPtr thread, IntPtr func, IntPtr user_data);
    private static il2cpp_thread_walk_frame_stack_t _il2cpp_thread_walk_frame_stack;
    public static void il2cpp_thread_walk_frame_stack(IntPtr thread, IntPtr func, IntPtr user_data) => _il2cpp_thread_walk_frame_stack(thread, func, user_data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_current_thread_get_top_frame_t(IntPtr frame);
    private static il2cpp_current_thread_get_top_frame_t _il2cpp_current_thread_get_top_frame;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_current_thread_get_top_frame(IntPtr frame) => _il2cpp_current_thread_get_top_frame(frame);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_thread_get_top_frame_t(IntPtr thread, IntPtr frame);
    private static il2cpp_thread_get_top_frame_t _il2cpp_thread_get_top_frame;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_thread_get_top_frame(IntPtr thread, IntPtr frame) => _il2cpp_thread_get_top_frame(thread, frame);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_current_thread_get_frame_at_t(int offset, IntPtr frame);
    private static il2cpp_current_thread_get_frame_at_t _il2cpp_current_thread_get_frame_at;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_current_thread_get_frame_at(int offset, IntPtr frame) => _il2cpp_current_thread_get_frame_at(offset, frame);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_thread_get_frame_at_t(IntPtr thread, int offset, IntPtr frame);
    private static il2cpp_thread_get_frame_at_t _il2cpp_thread_get_frame_at;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_thread_get_frame_at(IntPtr thread, int offset, IntPtr frame) => _il2cpp_thread_get_frame_at(thread, offset, frame);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int il2cpp_current_thread_get_stack_depth_t();
    private static il2cpp_current_thread_get_stack_depth_t _il2cpp_current_thread_get_stack_depth;
    public static int il2cpp_current_thread_get_stack_depth() => _il2cpp_current_thread_get_stack_depth();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int il2cpp_thread_get_stack_depth_t(IntPtr thread);
    private static il2cpp_thread_get_stack_depth_t _il2cpp_thread_get_stack_depth;
    public static int il2cpp_thread_get_stack_depth(IntPtr thread) => _il2cpp_thread_get_stack_depth(thread);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_type_get_object_t(IntPtr type);
    private static il2cpp_type_get_object_t _il2cpp_type_get_object;
    public static IntPtr il2cpp_type_get_object(IntPtr type) => _il2cpp_type_get_object(type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int il2cpp_type_get_type_t(IntPtr type);
    private static il2cpp_type_get_type_t _il2cpp_type_get_type;
    public static int il2cpp_type_get_type(IntPtr type) => _il2cpp_type_get_type(type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_type_get_class_or_element_class_t(IntPtr type);
    private static il2cpp_type_get_class_or_element_class_t _il2cpp_type_get_class_or_element_class;
    public static IntPtr il2cpp_type_get_class_or_element_class(IntPtr type) => _il2cpp_type_get_class_or_element_class(type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate nint il2cpp_type_get_name_t(IntPtr type);
    private static il2cpp_type_get_name_t _il2cpp_type_get_name;
    public static nint il2cpp_type_get_name(IntPtr type) => _il2cpp_type_get_name(type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_type_is_byref_t(IntPtr type);
    private static il2cpp_type_is_byref_t _il2cpp_type_is_byref;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_type_is_byref(IntPtr type) => _il2cpp_type_is_byref(type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint il2cpp_type_get_attrs_t(IntPtr type);
    private static il2cpp_type_get_attrs_t _il2cpp_type_get_attrs;
    public static uint il2cpp_type_get_attrs(IntPtr type) => _il2cpp_type_get_attrs(type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_type_equals_t(IntPtr type, IntPtr otherType);
    private static il2cpp_type_equals_t _il2cpp_type_equals;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_type_equals(IntPtr type, IntPtr otherType) => _il2cpp_type_equals(type, otherType);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_type_get_assembly_qualified_name_t(IntPtr type);
    private static il2cpp_type_get_assembly_qualified_name_t _il2cpp_type_get_assembly_qualified_name;
    public static IntPtr il2cpp_type_get_assembly_qualified_name(IntPtr type) => _il2cpp_type_get_assembly_qualified_name(type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_image_get_assembly_t(IntPtr image);
    private static il2cpp_image_get_assembly_t _il2cpp_image_get_assembly;
    public static IntPtr il2cpp_image_get_assembly(IntPtr image) => _il2cpp_image_get_assembly(image);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate nint il2cpp_image_get_name_t(IntPtr image);
    private static il2cpp_image_get_name_t _il2cpp_image_get_name;
    public static nint il2cpp_image_get_name(IntPtr image) => _il2cpp_image_get_name(image);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate nint il2cpp_image_get_filename_t(IntPtr image);
    private static il2cpp_image_get_filename_t _il2cpp_image_get_filename;
    public static nint il2cpp_image_get_filename(IntPtr image) => _il2cpp_image_get_filename(image);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_image_get_entry_point_t(IntPtr image);
    private static il2cpp_image_get_entry_point_t _il2cpp_image_get_entry_point;
    public static IntPtr il2cpp_image_get_entry_point(IntPtr image) => _il2cpp_image_get_entry_point(image);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint il2cpp_image_get_class_count_t(IntPtr image);
    private static il2cpp_image_get_class_count_t _il2cpp_image_get_class_count;
    public static uint il2cpp_image_get_class_count(IntPtr image) => _il2cpp_image_get_class_count(image);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_image_get_class_t(IntPtr image, uint index);
    private static il2cpp_image_get_class_t _il2cpp_image_get_class;
    public static IntPtr il2cpp_image_get_class(IntPtr image, uint index) => _il2cpp_image_get_class(image, index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_capture_memory_snapshot_t();
    private static il2cpp_capture_memory_snapshot_t _il2cpp_capture_memory_snapshot;
    public static IntPtr il2cpp_capture_memory_snapshot() => _il2cpp_capture_memory_snapshot();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_free_captured_memory_snapshot_t(IntPtr snapshot);
    private static il2cpp_free_captured_memory_snapshot_t _il2cpp_free_captured_memory_snapshot;
    public static void il2cpp_free_captured_memory_snapshot(IntPtr snapshot) => _il2cpp_free_captured_memory_snapshot(snapshot);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_set_find_plugin_callback_t(IntPtr method);
    private static il2cpp_set_find_plugin_callback_t _il2cpp_set_find_plugin_callback;
    public static void il2cpp_set_find_plugin_callback(IntPtr method) => _il2cpp_set_find_plugin_callback(method);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_register_log_callback_t(IntPtr method);
    private static il2cpp_register_log_callback_t _il2cpp_register_log_callback;
    public static void il2cpp_register_log_callback(IntPtr method) => _il2cpp_register_log_callback(method);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_debugger_set_agent_options_t(IntPtr options);
    private static il2cpp_debugger_set_agent_options_t _il2cpp_debugger_set_agent_options;
    public static void il2cpp_debugger_set_agent_options(IntPtr options) => _il2cpp_debugger_set_agent_options(options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_is_debugger_attached_t();
    private static il2cpp_is_debugger_attached_t _il2cpp_is_debugger_attached;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_is_debugger_attached() => _il2cpp_is_debugger_attached();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_unity_install_unitytls_interface_t(void* unitytlsInterfaceStruct);
    private static il2cpp_unity_install_unitytls_interface_t _il2cpp_unity_install_unitytls_interface;
    public static void il2cpp_unity_install_unitytls_interface(void* unitytlsInterfaceStruct) => _il2cpp_unity_install_unitytls_interface(unitytlsInterfaceStruct);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_custom_attrs_from_class_t(IntPtr klass);
    private static il2cpp_custom_attrs_from_class_t _il2cpp_custom_attrs_from_class;
    public static IntPtr il2cpp_custom_attrs_from_class(IntPtr klass) => _il2cpp_custom_attrs_from_class(klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_custom_attrs_from_method_t(IntPtr method);
    private static il2cpp_custom_attrs_from_method_t _il2cpp_custom_attrs_from_method;
    public static IntPtr il2cpp_custom_attrs_from_method(IntPtr method) => _il2cpp_custom_attrs_from_method(method);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_custom_attrs_get_attr_t(IntPtr ainfo, IntPtr attr_klass);
    private static il2cpp_custom_attrs_get_attr_t _il2cpp_custom_attrs_get_attr;
    public static IntPtr il2cpp_custom_attrs_get_attr(IntPtr ainfo, IntPtr attr_klass) => _il2cpp_custom_attrs_get_attr(ainfo, attr_klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate bool il2cpp_custom_attrs_has_attr_t(IntPtr ainfo, IntPtr attr_klass);
    private static il2cpp_custom_attrs_has_attr_t _il2cpp_custom_attrs_has_attr;
    [return: MarshalAs(UnmanagedType.I1)]
    public static bool il2cpp_custom_attrs_has_attr(IntPtr ainfo, IntPtr attr_klass) => _il2cpp_custom_attrs_has_attr(ainfo, attr_klass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr il2cpp_custom_attrs_construct_t(IntPtr cinfo);
    private static il2cpp_custom_attrs_construct_t _il2cpp_custom_attrs_construct;
    public static IntPtr il2cpp_custom_attrs_construct(IntPtr cinfo) => _il2cpp_custom_attrs_construct(cinfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void il2cpp_custom_attrs_free_t(IntPtr ainfo);
    private static il2cpp_custom_attrs_free_t _il2cpp_custom_attrs_free;
    public static void il2cpp_custom_attrs_free(IntPtr ainfo) => _il2cpp_custom_attrs_free(ainfo);
}
