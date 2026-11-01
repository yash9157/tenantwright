using System;
namespace Identity.Domain.Shared.Account;

[Flags]
public enum UserInfoSegments
{
    None = 0,
    Profile = 1,
    Authorization = 2
}
