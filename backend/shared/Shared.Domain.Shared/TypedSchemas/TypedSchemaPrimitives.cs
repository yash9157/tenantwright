namespace Shared.Domain.Shared.TypedSchemas;

/// <summary>
/// Primitive value kinds supported by governed tenant-defined fields. This list is deliberately
/// closed: persisted definitions cannot select executable code, HTML, routes, or UI components.
/// </summary>
public enum TypedFieldKind
{
    ShortText = 1,
    LongText = 2,
    // Integer and Decimal are the stable persisted field-kind vocabulary, not CLR type references.
#pragma warning disable CA1720
    Integer = 3,
    Decimal = 4,
#pragma warning restore CA1720
    Boolean = 5,
    Date = 6,
    DateTime = 7,
    SingleChoice = 8,
    MultipleChoice = 9,
    Identifier = 10,
    Email = 11,
    Phone = 12,
    CountryCode = 13,
    AdministrativeAreaCode = 14
}

public enum TypedRuleOperator
{
    Required = 1,
    MinimumLength = 2,
    MaximumLength = 3,
    MinimumValue = 4,
    MaximumValue = 5,
    Equals = 6,
    NotEquals = 7,
    In = 8,
    NotIn = 9,
    Present = 10,
    Absent = 11
}

public enum TypedRuleCombinator
{
    All = 1,
    Any = 2
}

public enum FieldSensitivity
{
    Standard = 1,
    Sensitive = 2,
    Restricted = 3
}

public enum FieldAccessClass
{
    TenantAdministrator = 1,
    ComplianceOfficer = 3,
    SystemOnly = 5
}

public enum FieldRetentionClass
{
    Configuration = 1,
    OperationalEvidence = 2,
    RegulatoryEvidence = 3,
    TransientSetup = 4
}

public static class TypedSchemaPolicy
{
    public const int MaximumFieldsPerDefinition = 256;
    public const int MaximumOptionsPerField = 500;
    public const int MaximumRulesPerField = 32;
    public const int MaximumRuleGroups = 64;
    public const int MaximumRuleDepth = 4;
    public const int MaximumTextLength = 8_000;

    public static void RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(parameterName);
    }

    public static void RequireCompatible(TypedFieldKind fieldKind, TypedRuleOperator ruleOperator)
    {
        RequireDefined(fieldKind, nameof(fieldKind));
        RequireDefined(ruleOperator, nameof(ruleOperator));

        var compatible = ruleOperator switch
        {
            TypedRuleOperator.Required or TypedRuleOperator.Present or TypedRuleOperator.Absent => true,
            TypedRuleOperator.MinimumLength or TypedRuleOperator.MaximumLength =>
                fieldKind is TypedFieldKind.ShortText or TypedFieldKind.LongText or
                    TypedFieldKind.Email or TypedFieldKind.Phone or TypedFieldKind.Identifier,
            TypedRuleOperator.MinimumValue or TypedRuleOperator.MaximumValue =>
                fieldKind is TypedFieldKind.Integer or TypedFieldKind.Decimal or
                    TypedFieldKind.Date or TypedFieldKind.DateTime,
            TypedRuleOperator.Equals or TypedRuleOperator.NotEquals => true,
            TypedRuleOperator.In or TypedRuleOperator.NotIn =>
                fieldKind is TypedFieldKind.SingleChoice or TypedFieldKind.MultipleChoice or
                    TypedFieldKind.CountryCode or TypedFieldKind.AdministrativeAreaCode,
            _ => false
        };

        if (!compatible)
            throw new ArgumentException($"Operator {ruleOperator} is not valid for field kind {fieldKind}.", nameof(ruleOperator));
    }
}
