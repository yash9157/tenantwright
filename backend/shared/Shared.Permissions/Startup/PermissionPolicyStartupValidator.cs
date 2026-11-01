using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Permissions.Application.Contracts.Definitions;
using Shared.Permissions.Definitions;

namespace Shared.Permissions.Startup;

/// <summary>
/// Verifies that every explicit authorization policy used by an HTTP host resolves
/// either to a host-defined policy or to a permission in the shared catalog.
/// </summary>
public sealed class PermissionPolicyStartupValidator : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHostEnvironment _environment;
    private readonly IOptions<AuthorizationOptions> _authorizationOptions;
    private readonly SharedPermissionDefinitionIndex _permissionDefinitions;
    private readonly PermissionDefinitionProviderLoadDiagnostics _loadDiagnostics;
    private readonly ILogger<PermissionPolicyStartupValidator> _logger;

    public PermissionPolicyStartupValidator(
        IServiceProvider serviceProvider,
        IHostEnvironment environment,
        IOptions<AuthorizationOptions> authorizationOptions,
        SharedPermissionDefinitionIndex permissionDefinitions,
        PermissionDefinitionProviderLoadDiagnostics loadDiagnostics,
        ILogger<PermissionPolicyStartupValidator> logger)
    {
        _serviceProvider = serviceProvider;
        _environment = environment;
        _authorizationOptions = authorizationOptions;
        _permissionDefinitions = permissionDefinitions;
        _loadDiagnostics = loadDiagnostics;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var validationFailures = new List<string>();
        validationFailures.AddRange(_loadDiagnostics.Failures);

        foreach (var policyName in GetDeclaredPolicyNames(validationFailures))
        {
            var isHostPolicy = _authorizationOptions.Value.GetPolicy(policyName) is not null;
            var isCatalogPermission = _permissionDefinitions.TryGet(policyName, out _);

            if (isHostPolicy && isCatalogPermission)
            {
                validationFailures.Add(
                    $"Authorization policy '{policyName}' shadows a permission with the same catalog name.");
                continue;
            }

            if (isHostPolicy || isCatalogPermission)
            {
                continue;
            }

            validationFailures.Add(
                $"Authorization policy '{policyName}' is neither a registered host policy nor a permission in the catalog.");
        }

        if (validationFailures.Count == 0)
        {
            return Task.CompletedTask;
        }

        foreach (var failure in validationFailures)
        {
            _logger.LogError("Permission catalog startup validation failed: {ValidationFailure}", failure);
        }

        throw new InvalidOperationException(
            "Permission catalog startup validation failed. See the preceding error logs for the invalid policy names or definition-provider load failures.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private HashSet<string> GetDeclaredPolicyNames(ICollection<string> validationFailures)
    {
        var policyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in GetCandidateTypes(validationFailures))
        {
            AddPolicyNames(type.GetCustomAttributes<AuthorizeAttribute>(inherit: true), policyNames);

            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                AddPolicyNames(method.GetCustomAttributes<AuthorizeAttribute>(inherit: true), policyNames);
            }
        }

        return policyNames;
    }

    private HashSet<Type> GetCandidateTypes(ICollection<string> validationFailures)
    {
        var types = new HashSet<Type>();
        var assemblies = new HashSet<Assembly>();
        var loadedApplicationAssembly = false;

        if (!string.IsNullOrWhiteSpace(_environment.ApplicationName))
        {
            try
            {
                assemblies.Add(Assembly.Load(new AssemblyName(_environment.ApplicationName)));
                loadedApplicationAssembly = true;
            }
            catch (Exception exception)
            {
                validationFailures.Add(
                    $"Could not load host assembly '{_environment.ApplicationName}' while validating authorization policies: {exception.GetType().Name}.");
            }
        }

        if (!loadedApplicationAssembly && Assembly.GetEntryAssembly() is { } entryAssembly)
        {
            assemblies.Add(entryAssembly);
        }

        var applicationPartManager = _serviceProvider.GetService<ApplicationPartManager>();
        if (applicationPartManager is not null)
        {
            foreach (var typeProvider in applicationPartManager.ApplicationParts.OfType<IApplicationPartTypeProvider>())
            {
                foreach (var type in typeProvider.Types)
                {
                    types.Add(type.AsType());
                }
            }
        }

        foreach (var assembly in assemblies)
        {
            foreach (var type in GetTypes(assembly, validationFailures))
            {
                types.Add(type);
            }
        }

        return types;
    }

    private static IEnumerable<Type> GetTypes(Assembly assembly, ICollection<string> validationFailures)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            foreach (var loaderException in exception.LoaderExceptions.Where(loaderException => loaderException is not null))
            {
                validationFailures.Add(
                    $"Could not load one or more types from '{assembly.GetName().Name}' while validating authorization policies: {loaderException!.GetType().Name}.");
            }

            return exception.Types.OfType<Type>();
        }
        catch (Exception exception)
        {
            validationFailures.Add(
                $"Could not inspect '{assembly.GetName().Name}' while validating authorization policies: {exception.GetType().Name}.");
            return Array.Empty<Type>();
        }
    }

    private static void AddPolicyNames(
        IEnumerable<AuthorizeAttribute> attributes,
        HashSet<string> policyNames)
    {
        foreach (var attribute in attributes)
        {
            if (!string.IsNullOrWhiteSpace(attribute.Policy))
            {
                policyNames.Add(attribute.Policy.Trim());
            }
        }
    }
}

public sealed class PermissionDefinitionProviderLoadDiagnostics
{
    private readonly List<string> _failures = [];
    private readonly Lock _sync = new();

    public IReadOnlyCollection<string> Failures
    {
        get
        {
            lock (_sync)
            {
                return _failures.ToArray();
            }
        }
    }

    public void Record(Assembly assembly, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(exception);

        lock (_sync)
        {
            if (exception is ReflectionTypeLoadException typeLoadException)
            {
                _failures.AddRange(typeLoadException.LoaderExceptions
                    .Where(loaderException => loaderException is not null)
                    .Select(loaderException =>
                        $"Could not load a permission definition provider from '{assembly.GetName().Name}': {loaderException!.GetType().Name}."));
                return;
            }

            _failures.Add(
                $"Could not load permission definition providers from '{assembly.GetName().Name}': {exception.GetType().Name}.");
        }
    }
}
