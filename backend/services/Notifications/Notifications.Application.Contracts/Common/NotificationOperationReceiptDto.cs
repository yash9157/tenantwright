namespace Notifications.Application.Contracts.Common;

public sealed class NotificationOperationReceiptDto
{
    public Guid ReceiptId { get; set; }
    public string Operation { get; set; } = string.Empty;
    public long ResultingRevision { get; set; }
    public int AffectedCount { get; set; }
    public DateTime AppliedAtUtc { get; set; }
    public bool IsReplay { get; set; }
}

public sealed class NotificationMutationResultDto
{
    public long Revision { get; set; }
    public NotificationOperationReceiptDto Receipt { get; set; } = new();
}
