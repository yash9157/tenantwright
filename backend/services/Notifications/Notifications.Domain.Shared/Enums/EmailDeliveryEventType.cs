namespace Notifications.Domain.Shared.Enums;

/// <summary>
/// Category of an asynchronous email delivery-feedback event recorded from a provider webhook.
/// Phase 1 records facts only; opens/clicks are intentionally excluded.
/// </summary>
public enum EmailDeliveryEventType
{
    Delivered = 1,
    Bounce = 2,
    Complaint = 3
}
