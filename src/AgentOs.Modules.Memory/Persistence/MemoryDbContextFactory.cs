// Design-time factory so `dotnet ef migrations` can build the context outside the host (model shape only).

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentOs.Modules.Memory.Persistence;

public sealed class MemoryDbContextFactory : IDesignTimeDbContextFactory<MemoryDbContext>
{
    public MemoryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseNpgsql("Host=localhost;Database=agentos;Username=postgres;Password=postgres",
                npg => npg.MigrationsHistoryTable("__EFMigrationsHistory", schema: "memory"))
            .Options;
        return new MemoryDbContext(options);
    }
}
