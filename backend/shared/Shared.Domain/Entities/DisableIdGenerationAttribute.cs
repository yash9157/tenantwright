using System;

namespace Shared.Domain.Entities;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public class DisableIdGenerationAttribute : Attribute
{
}
