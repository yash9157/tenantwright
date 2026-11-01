using System.Threading;
using System.Threading.Tasks;

namespace Identity.Application.Contracts.Users;

public interface IInternalUserProvisioningAppService
{
    Task<ProvisionFirstUserResultDto> ProvisionFirstUserAsync(
        ProvisionFirstUserInput input,
        CancellationToken cancellationToken = default);
}
