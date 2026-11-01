using System.Linq.Expressions;
using System.Threading;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Shared.EntityFrameworkCore.GlobalFilters;

namespace Shared.EntityFrameworkCore;

#pragma warning disable EF1001
public class SharedEntityQueryProvider : EntityQueryProvider
{
    protected SharedEfCoreCurrentDbContext SharedEfCoreCurrentDbContext { get; }
    protected ICurrentDbContext CurrentDbContext { get; }

    public SharedEntityQueryProvider(
        IQueryCompiler queryCompiler,
        SharedEfCoreCurrentDbContext sharedEfCoreCurrentDbContext,
        ICurrentDbContext currentDbContext)
        : base(queryCompiler)
    {
        SharedEfCoreCurrentDbContext = sharedEfCoreCurrentDbContext;
        CurrentDbContext = currentDbContext;
    }

    public override object Execute(Expression expression)
    {
        using (SharedEfCoreCurrentDbContext.Use(CurrentDbContext.Context as ISharedEfCoreDbFunctionContext))
        {
            return base.Execute(expression);
        }
    }

    public override TResult Execute<TResult>(Expression expression)
    {
        using (SharedEfCoreCurrentDbContext.Use(CurrentDbContext.Context as ISharedEfCoreDbFunctionContext))
        {
            return base.Execute<TResult>(expression);
        }
    }

    public override TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = new CancellationToken())
    {
        using (SharedEfCoreCurrentDbContext.Use(CurrentDbContext.Context as ISharedEfCoreDbFunctionContext))
        {
            return base.ExecuteAsync<TResult>(expression, cancellationToken);
        }
    }
}
#pragma warning restore EF1001
