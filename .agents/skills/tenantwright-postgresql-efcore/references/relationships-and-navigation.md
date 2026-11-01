# Relationships and Navigation

## Model Ownership First

- Aggregate-owned children may use root-to-child navigations when domain behavior loads and changes them as one consistency boundary.
- Independently owned aggregates normally reference identities, not object graphs.
- A navigation exists for domain ownership and lifecycle, not merely to enable `Include`.
- Configure both ends, foreign keys, requiredness, delete behavior, and tenant-qualified relationships explicitly when convention could hide intent.

## Review Checklist

1. Identify aggregate root, dependent, lifecycle owner, and allowed delete behavior.
2. Ensure cross-tenant relationships are impossible through tenant-qualified keys or equivalent constraints.
3. Avoid cascades that erase audit evidence or independently owned records.
4. Check required relationships for valid creation order and optional relationships for meaningful null semantics.
5. Review generated migration and snapshot for unintended shadow properties or duplicate foreign keys.
6. Test loading, mutation, deletion, concurrency, and isolation using the real owning DbContext.

## Query Shape

Use projections for read models. Use `Include` only when a complete tracked graph is required by domain behavior. For multiple collections, consider split-query tradeoffs and bound result size. Do not create partially populated domain entities as read DTOs.

From `backend/`:

```powershell
rg -n "HasOne|HasMany|WithOne|WithMany|OwnsOne|OwnsMany|Include\(" services app shared tests -g "*.cs"
```
