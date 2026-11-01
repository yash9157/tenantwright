# Naming and Assertions

## Test Names

Name tests for behavior and condition, using the established local style. A useful name identifies the action, scenario, and expected outcome without repeating implementation details.

Examples:

```csharp
EvaluateAsync_ExpiredGrant_ReturnsDenied
CreateAsync_WrongTenant_ThrowsAuthorizationException
HandleAsync_DuplicateMessage_DoesNotRepeatSideEffects
```

## Arrange, Act, Assert

Keep setup focused. Extract fixture construction when it obscures intent, but avoid a test DSL that hides security or tenant state. One test may contain several assertions when they describe one observable outcome.

Assert:

- returned value or exception type and stable error code;
- durable state and absence of forbidden state;
- emitted event or message when publication is contractual;
- tenant/provider/audit metadata when security-sensitive;
- no sensitive values in errors or logs when relevant.

Avoid assertions on localized prose, private methods, incidental call order, full serialized snapshots for unstable payloads, or only mock invocations when a result is observable.

## Async and Exceptions

Await all work. Pass cancellation tokens through test calls. Use bounded timeouts for eventual behavior and preserve the original assertion failure. Assert the narrowest meaningful exception and stable machine-readable details rather than brittle stack traces.
