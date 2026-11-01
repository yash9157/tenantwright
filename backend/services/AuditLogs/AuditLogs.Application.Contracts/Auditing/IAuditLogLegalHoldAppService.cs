namespace AuditLogs.Application.Contracts.Auditing;

public interface IAuditLogLegalHoldAppService
{
    Task<AuditLogLegalHoldDto> CreateAsync(CreateAuditLogLegalHoldInput input);

    Task<AuditLogLegalHoldDto> ReleaseAsync(long id, ReleaseAuditLogLegalHoldInput input);

    Task<PagedResultDto<AuditLogLegalHoldDto>> GetListAsync(GetAuditLogLegalHoldsInput input);
}
