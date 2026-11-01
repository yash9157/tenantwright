namespace Shared.Domain;

public interface IHasExtraProperties
{
    ExtraPropertyDictionary ExtraProperties { get; }
}
