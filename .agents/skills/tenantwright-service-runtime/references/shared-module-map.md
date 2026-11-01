# Shared Module Discovery

`backend/shared/Shared.*` is the current inventory. Discover ownership from public APIs, registration, tests, and consumers rather than copying the directory list into code changes.

```powershell
Get-ChildItem shared -Directory -Filter 'Shared.*' | Sort-Object Name
Get-ChildItem shared/<Owner> -Recurse -Filter '*.csproj'
git grep -n -E "public (class|interface)|ConfigureServices|Add[A-Z]" -- shared/<Owner>
git grep -n "Shared.<Owner>.csproj" -- app services gateway shared tests
```

Ask:

1. Is the behavior infrastructure rather than service policy?
2. Which existing shared project owns the primitive?
3. Which real callers need the change?
4. Does the API remain provider-neutral and service-neutral?
5. Which options, registrations, packages, and transitive references change?
6. Which consuming host proves composition works?

Use the narrowest owner. Do not add a shared abstraction merely to shorten one service or to hide a cross-service implementation dependency.
