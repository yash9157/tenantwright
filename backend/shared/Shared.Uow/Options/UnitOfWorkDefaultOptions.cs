using System.Data;

namespace Shared.Uow.Options;

public class UnitOfWorkDefaultOptions
{
    public const string SectionName = "UnitOfWork:Default";

    public UnitOfWorkTransactionBehavior TransactionBehavior { get; set; } = UnitOfWorkTransactionBehavior.Auto;

    public bool? IsTransactional { get; set; }

    public IsolationLevel? IsolationLevel { get; set; }

    public int? Timeout { get; set; }

    public bool CalculateIsTransactional(bool autoValue)
    {
        if (IsTransactional.HasValue)
        {
            return IsTransactional.Value;
        }

        return TransactionBehavior switch
        {
            UnitOfWorkTransactionBehavior.Enabled => true,
            UnitOfWorkTransactionBehavior.Disabled => false,
            UnitOfWorkTransactionBehavior.Auto => autoValue,
            _ => throw new InvalidOperationException($"Unsupported transaction behavior value: {TransactionBehavior}.")
        };
    }

    public UnitOfWorkOptions Normalize(UnitOfWorkOptions options)
    {
        if (options.IsolationLevel == null)
        {
            options.IsolationLevel = IsolationLevel;
        }

        if (options.Timeout == null)
        {
            options.Timeout = Timeout;
        }

        if (options.Timeout.HasValue && options.Timeout <= 0)
        {
            throw new InvalidOperationException("UnitOfWork timeout must be greater than zero.");
        }

        return options;
    }
}
