namespace Autheris.Tests.Integration.Modules;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Replaces module-owned service registrations with a <see cref="DispatchProxy"/> bridge when integration tests
/// supply a test double typed against the host <see cref="System.Runtime.Loader.AssemblyLoadContext.Default"/>
/// copy of a module interface while the running endpoint resolves the copy inside the module's own
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>.
/// </summary>
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1852:Seal internal types", Justification = "DispatchProxy requires an unsealed proxy type.")]
    public class CrossAlcServiceProxy<TService> : DispatchProxy
        where TService : class
    {
        private TService _inner = null!;

        internal static object CreateProxy(Type alcServiceType, TService implementation)
        {
            var createMethod = typeof(DispatchProxy)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == nameof(DispatchProxy.Create) && m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 0)
                .MakeGenericMethod(alcServiceType, typeof(CrossAlcServiceProxy<TService>));

            var proxy = (CrossAlcServiceProxy<TService>)createMethod.Invoke(null, null)!;
            proxy._inner = implementation;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            var targetMethodParams = targetMethod.GetParameters();
            var innerMethod = typeof(TService).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Single(m =>
                    string.Equals(m.Name, targetMethod.Name, StringComparison.Ordinal) &&
                    ParametersMatch(m.GetParameters(), targetMethodParams));

            var innerParams = innerMethod.GetParameters();
            var convertedArgs = new object?[innerParams.Length];
            for (int i = 0; i < innerParams.Length; i++)
            {
                convertedArgs[i] = ConvertValue(args?[i], innerParams[i].ParameterType);
            }

            object? result;
            try
            {
                result = innerMethod.Invoke(_inner, convertedArgs);
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
