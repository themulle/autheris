namespace Autheris.Tests.Integration.Modules;

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Replaces module-owned service registrations with a cross-<see cref="AssemblyLoadContext"/> bridge when
/// integration tests supply a test double typed against the host <see cref="AssemblyLoadContext.Default"/>
/// copy of a module interface while the running endpoint resolves the copy inside the module's isolated
/// <see cref="AssemblyLoadContext"/>.
/// </summary>
/// <remarks>
/// <see cref="System.Reflection.DispatchProxy"/> emits types into a shared dynamic assembly bound to
/// <see cref="AssemblyLoadContext.Default"/>; when the test assembly also references the module assembly,
/// <see cref="System.Reflection.DispatchProxy"/> resolves the interface from <see cref="AssemblyLoadContext.Default"/>
/// and fails with <see cref="InvalidCastException"/> against the module ALC copy. Emitting the proxy inside
/// a child <see cref="AssemblyLoadContext"/> pinned to the target module assembly avoids this collision
/// across any number of isolated module load contexts in the same process.
/// </remarks>
internal static class ModuleTestServiceBridge
{
    public static IServiceCollection ReplaceModuleService<TService>(this IServiceCollection services, TService implementation)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(implementation);

        var targetName = typeof(TService).FullName
            ?? throw new InvalidOperationException($"Service type '{typeof(TService)}' has no FullName.");

        var matchingDescriptors = services
            .Where(d => string.Equals(d.ServiceType.FullName, targetName, StringComparison.Ordinal))
            .ToList();

        var serviceTypes = matchingDescriptors
            .Select(d => d.ServiceType)
            .Distinct()
            .ToList();

        foreach (var descriptor in matchingDescriptors)
        {
            services.Remove(descriptor);
        }

        if (serviceTypes.Count == 0)
        {
            serviceTypes.Add(typeof(TService));
        }

        foreach (var serviceType in serviceTypes)
        {
            if (serviceType == typeof(TService))
            {
                services.AddSingleton(serviceType, implementation);
            }
            else
            {
                var proxy = CrossAlcServiceProxy<TService>.CreateProxy(serviceType, implementation);
                services.AddSingleton(serviceType, proxy);
            }
        }

        return services;
    }

    private sealed class ProxyAssemblyLoadContext : AssemblyLoadContext
    {
        private readonly Assembly _moduleAssembly;
        private readonly AssemblyLoadContext? _sourceAlc;
        private readonly string? _moduleSimpleName;

        public ProxyAssemblyLoadContext(Assembly moduleAssembly)
            : base(name: $"CrossAlcProxy:{moduleAssembly.GetName().Name}", isCollectible: false)
        {
            _moduleAssembly = moduleAssembly;
            _sourceAlc = GetLoadContext(moduleAssembly);
            _moduleSimpleName = moduleAssembly.GetName().Name;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (!string.IsNullOrEmpty(_moduleSimpleName) &&
                string.Equals(assemblyName.Name, _moduleSimpleName, StringComparison.OrdinalIgnoreCase))
            {
                return _moduleAssembly;
            }

            if (_sourceAlc is not null && _sourceAlc != Default)
            {
                foreach (var asm in _sourceAlc.Assemblies)
                {
                    if (string.Equals(asm.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        return asm;
                    }
                }
            }

            return null;
        }
    }

    private static class CrossAlcServiceProxy<TService>
        where TService : class
    {
        private sealed record ProxyMetadata(Type ProxyType, MethodInfo[] InterfaceMethods);

        private static readonly ConditionalWeakTable<Assembly, ProxyAssemblyLoadContext> AlcByModuleAssembly = new();
        private static readonly ConcurrentDictionary<Type, Lazy<ProxyMetadata>> ProxyCache = new();
        private static readonly MethodInfo DispatcherInvokeMethod =
            typeof(Func<int, object?[], object?>).GetMethod(nameof(Func<int, object?[], object?>.Invoke))!;

        internal static object CreateProxy(Type alcServiceType, TService implementation)
        {
            if (!alcServiceType.IsInterface)
            {
                throw new ArgumentException($"Cross-ALC bridge requires an interface type, got '{alcServiceType}'.", nameof(alcServiceType));
            }

            var metadata = ProxyCache.GetOrAdd(
                alcServiceType,
                static type => new Lazy<ProxyMetadata>(() => BuildProxyType(type))).Value;

            Func<int, object?[], object?> dispatcher = (methodIndex, callArgs) =>
                InvokeTarget(implementation, metadata.InterfaceMethods[methodIndex], callArgs);

            return Activator.CreateInstance(metadata.ProxyType, dispatcher)!;
        }

        private static ProxyMetadata BuildProxyType(Type alcServiceType)
        {
            var interfaceMethods = new[] { alcServiceType }
                .Concat(alcServiceType.GetInterfaces())
                .SelectMany(static iface => iface.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                .Distinct()
                .ToArray();

            var proxyAlc = AlcByModuleAssembly.GetValue(
                alcServiceType.Assembly,
                static asm => new ProxyAssemblyLoadContext(asm));

            using (proxyAlc.EnterContextualReflection())
            {
                var asmName = new AssemblyName($"CrossAlcProxy_{Guid.NewGuid():N}");
                var asmBuilder = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.Run);
                var modBuilder = asmBuilder.DefineDynamicModule("Main");
                var typeBuilder = modBuilder.DefineType(
                    $"CrossAlcProxy_{alcServiceType.Name}_{Guid.NewGuid():N}",
                    TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
                    typeof(object),
                    [alcServiceType]);

                var dispatcherField = typeBuilder.DefineField(
                    "_dispatcher",
                    typeof(Func<int, object?[], object?>),
                    FieldAttributes.Private | FieldAttributes.InitOnly);

                var ctorBuilder = typeBuilder.DefineConstructor(
                    MethodAttributes.Public,
                    CallingConventions.Standard,
                    [typeof(Func<int, object?[], object?>)]);
                var ctorIl = ctorBuilder.GetILGenerator();
                ctorIl.Emit(OpCodes.Ldarg_0);
                ctorIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
                ctorIl.Emit(OpCodes.Ldarg_0);
                ctorIl.Emit(OpCodes.Ldarg_1);
                ctorIl.Emit(OpCodes.Stfld, dispatcherField);
                ctorIl.Emit(OpCodes.Ret);

                for (int methodIndex = 0; methodIndex < interfaceMethods.Length; methodIndex++)
                {
                    EmitMethod(typeBuilder, dispatcherField, interfaceMethods[methodIndex], methodIndex);
                }

                var proxyType = typeBuilder.CreateType()!;
                return new ProxyMetadata(proxyType, interfaceMethods);
            }
        }

        private static void EmitMethod(TypeBuilder typeBuilder, FieldBuilder dispatcherField, MethodInfo method, int methodIndex)
        {
            if (method.IsGenericMethodDefinition)
            {
                throw new NotSupportedException($"Generic interface methods are not supported by CrossAlcServiceProxy: '{method}'.");
            }

            var parameters = method.GetParameters();
            var paramTypes = parameters.Select(static p => p.ParameterType).ToArray();
            var methodBuilder = typeBuilder.DefineMethod(
                method.Name,
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                method.ReturnType,
                paramTypes);

            var il = methodBuilder.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, dispatcherField);
            il.Emit(OpCodes.Ldc_I4, methodIndex);
            il.Emit(OpCodes.Ldc_I4, parameters.Length);
            il.Emit(OpCodes.Newarr, typeof(object));

            for (int pIndex = 0; pIndex < parameters.Length; pIndex++)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, pIndex);
                il.Emit(OpCodes.Ldarg, pIndex + 1);
                var pType = parameters[pIndex].ParameterType;
                if (pType.IsValueType)
                {
                    il.Emit(OpCodes.Box, pType);
                }

                il.Emit(OpCodes.Stelem_Ref);
            }

            il.Emit(OpCodes.Callvirt, DispatcherInvokeMethod);

            if (method.ReturnType == typeof(void))
            {
                il.Emit(OpCodes.Pop);
            }
            else if (method.ReturnType.IsValueType)
            {
                il.Emit(OpCodes.Unbox_Any, method.ReturnType);
            }
            else
            {
                il.Emit(OpCodes.Castclass, method.ReturnType);
            }

            il.Emit(OpCodes.Ret);
            typeBuilder.DefineMethodOverride(methodBuilder, method);
        }

        private static object? InvokeTarget(TService implementation, MethodInfo targetMethod, object?[] args)
        {
            var targetMethodParams = targetMethod.GetParameters();
            var innerMethod = typeof(TService).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Single(m =>
                    string.Equals(m.Name, targetMethod.Name, StringComparison.Ordinal) &&
                    ParametersMatch(m.GetParameters(), targetMethodParams));

            var innerParams = innerMethod.GetParameters();
            var convertedArgs = new object?[innerParams.Length];
            for (int i = 0; i < innerParams.Length; i++)
            {
                convertedArgs[i] = ConvertValue(args[i], innerParams[i].ParameterType);
            }

            object? result;
            try
            {
                result = innerMethod.Invoke(implementation, convertedArgs);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }

            if (targetMethod.ReturnType == typeof(void) || result is null)
            {
                return null;
            }

            if (targetMethod.ReturnType.IsInstanceOfType(result))
            {
                return result;
            }

            if (targetMethod.ReturnType.IsGenericType &&
                targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>) &&
                result is Task defaultTask)
            {
                var alcResultType = targetMethod.ReturnType.GetGenericArguments()[0];
                return WrapTaskMethod
                    .MakeGenericMethod(alcResultType)
                    .Invoke(null, [defaultTask]);
            }

            return ConvertValue(result, targetMethod.ReturnType);
        }

        private static readonly MethodInfo WrapTaskMethod = typeof(CrossAlcServiceProxy<TService>)
            .GetMethod(nameof(WrapTaskAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

        private static async Task<TAlcResult> WrapTaskAsync<TAlcResult>(Task defaultTask)
        {
            await defaultTask.ConfigureAwait(false);
            var rawResult = defaultTask.GetType()
                .GetProperty(nameof(Task<object>.Result), BindingFlags.Public | BindingFlags.Instance)!
                .GetValue(defaultTask);

            return (TAlcResult)ConvertValue(rawResult, typeof(TAlcResult))!;
        }

        private static bool ParametersMatch(ParameterInfo[] left, ParameterInfo[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (!string.Equals(left[i].ParameterType.FullName, right[i].ParameterType.FullName, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static object? ConvertValue(object? value, Type targetType)
        {
            if (value is null)
            {
                return null;
            }

            var nonNullableTarget = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (nonNullableTarget.IsInstanceOfType(value))
            {
                return value;
            }

            if (TryConvertList(value, nonNullableTarget, out var convertedList))
            {
                return convertedList;
            }

            if (TryConvertByConstructor(value, nonNullableTarget, out var constructed))
            {
                return constructed;
            }

            var json = JsonSerializer.Serialize(value, value.GetType());
            return JsonSerializer.Deserialize(json, nonNullableTarget);
        }

        private static bool TryConvertList(object value, Type targetType, out object? convertedList)
        {
            convertedList = null;
            if (value is not IEnumerable enumerable || value is string)
            {
                return false;
            }

            Type? targetElementType = null;
            if (targetType.IsArray)
            {
                targetElementType = targetType.GetElementType();
            }
            else if (targetType.IsGenericType)
            {
                var def = targetType.GetGenericTypeDefinition();
                if (def == typeof(IReadOnlyList<>) ||
                    def == typeof(IList<>) ||
                    def == typeof(IReadOnlyCollection<>) ||
                    def == typeof(ICollection<>) ||
                    def == typeof(IEnumerable<>) ||
                    def == typeof(List<>))
                {
                    targetElementType = targetType.GetGenericArguments()[0];
                }
            }

            if (targetElementType is null)
            {
                return false;
            }

            var listType = typeof(List<>).MakeGenericType(targetElementType);
            var list = (IList)Activator.CreateInstance(listType)!;
            foreach (var item in enumerable)
            {
                list.Add(ConvertValue(item, targetElementType));
            }

            convertedList = targetType.IsArray ? ArrayFromList(list, targetElementType) : list;
            return true;
        }

        private static Array ArrayFromList(IList list, Type elementType)
        {
            var array = Array.CreateInstance(elementType, list.Count);
            list.CopyTo(array, 0);
            return array;
        }

        private static bool TryConvertByConstructor(object value, Type targetType, out object? instance)
        {
            instance = null;
            var sourceType = value.GetType();
            var sourceProps = sourceType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead)
                .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var ctor in targetType.GetConstructors(BindingFlags.Public | BindingFlags.Instance).OrderByDescending(c => c.GetParameters().Length))
            {
                var parameters = ctor.GetParameters();
                if (parameters.Length == 0 || !parameters.All(p => p.Name is not null && sourceProps.ContainsKey(p.Name)))
                {
                    continue;
                }

                var ctorArgs = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    var prop = sourceProps[parameters[i].Name!];
                    ctorArgs[i] = ConvertValue(prop.GetValue(value), parameters[i].ParameterType);
                }

                instance = ctor.Invoke(ctorArgs);
                return true;
            }

            return false;
        }
    }
}
