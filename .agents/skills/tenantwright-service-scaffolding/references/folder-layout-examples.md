# Folder Layout

Keep project roots for true root artifacts such as `.csproj`, module classes, `Program.cs`, settings, and Dockerfiles. Organize implementation by feature, then by responsibility only when it improves navigation. Namespaces must equal the project base namespace plus folder path.

## Application

```text
ServiceName.Application/
  ServiceNameApplicationModule.cs
  FeatureName/
    Commands/
      CreateItem/
    Queries/
      GetItem/
    Services/
    Validators/
```

## Application.Contracts

```text
ServiceName.Application.Contracts/
  ServiceNameApplicationContractsModule.cs
  FeatureName/
    Dtos/
    Requests/
    Responses/
    Abstractions/
```

## Domain

```text
ServiceName.Domain/
  ServiceNameDomainModule.cs
  FeatureName/
    Aggregates/
    Entities/
    ValueObjects/
    Events/
    Rules/
```

## Domain.Shared

```text
ServiceName.Domain.Shared/
  ServiceNameDomainSharedModule.cs
  FeatureName/
    Enums/
    Constants/
    ErrorCodes/
    PermissionNames/
```

## EntityFrameworkCore

```text
ServiceName.EntityFrameworkCore/
  ServiceNameEntityFrameworkCoreModule.cs
  DbContexts/
    ServiceNameDbContext.cs
  FeatureName/
    Configurations/
  Migrations/
```

Example: `services/ServiceName/ServiceName.Application/FeatureName/Queries/GetItem/GetItemQueryHandler.cs` uses `namespace ServiceName.Application.FeatureName.Queries.GetItem;`.

Do not create empty taxonomy folders, flatten unrelated responsibilities into one directory, or skip folder segments in namespaces. File count alone is not a reason for another architectural layer.
