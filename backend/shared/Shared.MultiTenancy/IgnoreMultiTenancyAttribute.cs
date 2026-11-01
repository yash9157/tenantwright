using System;

namespace Shared.MultiTenancy;

[AttributeUsage(AttributeTargets.All)]
public class IgnoreMultiTenancyAttribute : Attribute
{
}