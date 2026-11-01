using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Shared.Modularity.DependencyInjection;

public sealed class ConventionalRegistrationDiagnostics
{
    public int ScannedAssemblies { get; private set; }

    public int ScannedTypes { get; private set; }

    public int RegisteredDescriptors { get; private set; }

    public int DuplicateCollisionsBlocked { get; private set; }

    public int AmbiguousCollisionsBlocked { get; private set; }

    public long RegistrationDurationMs { get; private set; }

    private readonly HashSet<DescriptorIdentity> _descriptorLedger = new();
    private readonly Dictionary<ServiceIdentifier, ServiceRegistrationRecord> _serviceLedger = new();

    public void AddScannedAssembly()
    {
        ScannedAssemblies++;
    }

    public void AddScannedTypes(int count)
    {
        ScannedTypes += count;
    }

    public void AddRegisteredDescriptor()
    {
        RegisteredDescriptors++;
    }

    public void AddDuplicateCollision()
    {
        DuplicateCollisionsBlocked++;
    }

    public void AddAmbiguousCollision()
    {
        AmbiguousCollisionsBlocked++;
    }

    public void SetRegistrationDuration(long durationMs)
    {
        RegistrationDurationMs = durationMs;
    }

    public void SeedExistingDescriptor(ServiceDescriptor descriptor)
    {
        var serviceIdentifier = CreateServiceIdentifier(descriptor);
        var implementationIdentity = ResolveImplementationIdentity(descriptor);
        var descriptorIdentity = new DescriptorIdentity(serviceIdentifier, implementationIdentity, descriptor.Lifetime);

        _descriptorLedger.Add(descriptorIdentity);

        if (!_serviceLedger.ContainsKey(serviceIdentifier))
        {
            _serviceLedger[serviceIdentifier] = new ServiceRegistrationRecord(
                implementationIdentity,
                descriptor.Lifetime,
                descriptor.NormalizedImplementationType()?.Assembly ?? implementationIdentity.Assembly,
                descriptor.NormalizedImplementationType()?.FullName ?? implementationIdentity.FullName ?? implementationIdentity.Name);
        }
    }

    public bool ContainsExactDescriptor(ServiceIdentifier serviceIdentifier, Type implementationIdentity, ServiceLifetime lifetime)
    {
        return _descriptorLedger.Contains(new DescriptorIdentity(serviceIdentifier, implementationIdentity, lifetime));
    }

    public bool TryGetServiceRegistration(ServiceIdentifier serviceIdentifier, out ServiceRegistrationRecord record)
    {
        return _serviceLedger.TryGetValue(serviceIdentifier, out record);
    }

    public void RecordDescriptor(ServiceIdentifier serviceIdentifier, Type implementationIdentity, ServiceLifetime lifetime, Assembly sourceAssembly, string sourceTypeName)
    {
        _descriptorLedger.Add(new DescriptorIdentity(serviceIdentifier, implementationIdentity, lifetime));
        _serviceLedger[serviceIdentifier] = new ServiceRegistrationRecord(implementationIdentity, lifetime, sourceAssembly, sourceTypeName);
    }

    public void RemoveService(ServiceIdentifier serviceIdentifier)
    {
        _descriptorLedger.RemoveWhere(x => x.ServiceIdentifier.Equals(serviceIdentifier));
        _serviceLedger.Remove(serviceIdentifier);
    }

    private static ServiceIdentifier CreateServiceIdentifier(ServiceDescriptor descriptor)
    {
        return descriptor.ServiceKey == null
            ? new ServiceIdentifier(descriptor.ServiceType)
            : new ServiceIdentifier(descriptor.ServiceKey, descriptor.ServiceType);
    }

    private static Type ResolveImplementationIdentity(ServiceDescriptor descriptor)
    {
        var implementationType = descriptor.NormalizedImplementationType();
        if (implementationType != null)
        {
            return implementationType;
        }

        var implementationInstance = descriptor.NormalizedImplementationInstance();
        if (implementationInstance != null)
        {
            return implementationInstance.GetType();
        }

        return descriptor.ServiceType;
    }

    private readonly record struct DescriptorIdentity(
        ServiceIdentifier ServiceIdentifier,
        Type ImplementationIdentity,
        ServiceLifetime Lifetime);
}

public readonly record struct ServiceRegistrationRecord(
    Type ImplementationIdentity,
    ServiceLifetime Lifetime,
    Assembly SourceAssembly,
    string SourceTypeName);
