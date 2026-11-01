namespace Auth.Pages.Account.Validation;

public interface IAuthPasswordPolicyProvider
{
    AuthPasswordPolicyDescriptor GetPolicy();
}
