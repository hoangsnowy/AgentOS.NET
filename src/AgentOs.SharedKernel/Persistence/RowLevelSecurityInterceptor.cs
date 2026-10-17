// Applies RowLevelSecurity to every connection an EF Core context opens (role switch + tenant setting).
// Stateless — one shared instance for every module context.

using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AgentOs.SharedKernel.Persistence;

/// <summary>EF Core connection interceptor for <see cref="RowLevelSecurity"/>.</summary>
public sealed class RowLevelSecurityInterceptor : DbConnectionInterceptor
{
    /// <summary>The shared instance.</summary>
    public static RowLevelSecurityInterceptor Instance { get; } = new();

    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => RowLevelSecurity.ApplyToConnection(connection);

    /// <inheritdoc />
    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        => RowLevelSecurity.ApplyToConnectionAsync(connection, cancellationToken);
}
