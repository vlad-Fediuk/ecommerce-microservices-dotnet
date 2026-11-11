using ECommerce.Shared.Protos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderingService.Data;
using Xunit;

namespace Ordering.IntegrationTests.Infrastructure;

public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _dbName = $"OrderingTestDb_{Guid.NewGuid():N}";

    public FakeCatalogClient Catalog { get; } = new();

    public TestTokenProvider Tokens => new(Services.GetRequiredService<IConfiguration>());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            services.AddDbContext<OrderingDbContext>(options =>
                options.UseInMemoryDatabase(_dbName));

            services.RemoveAll<CatalogGrpcService.CatalogGrpcServiceClient>();
            services.AddSingleton<CatalogGrpcService.CatalogGrpcServiceClient>(Catalog);
        });
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        await dbContext.Database.EnsureCreatedAsync();
    }

    public async Task WithDbContextAsync(Func<OrderingDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<OrderingDbContext>());
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;
}
