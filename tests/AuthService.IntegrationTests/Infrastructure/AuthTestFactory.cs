using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using AuthService.Data;
using AuthService.Models;
using ECommerce.Shared.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace AuthService.IntegrationTests.Infrastructure;

public class AuthTestFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _dbName = $"AuthTestDb_{Guid.NewGuid():N}";
    public const string TestJwtKey = "SuperSecretKeyForJwtAuthenticationMustBeAtLeast32BytesLong!";
    public const string TestIssuer = "ECommerceApi";
    public const string TestAudience = "ECommerceApi";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = TestJwtKey,
                ["Jwt:Issuer"] = TestIssuer,
                ["Jwt:Audience"] = TestAudience
            });
        });

        builder.ConfigureServices(services =>
        {
            services.AddDbContext<AuthDbContext>(options =>
                options.UseInMemoryDatabase(_dbName));
        });
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await db.Database.EnsureCreatedAsync();

        var existingRoles = await db.Roles.Select(r => r.Name).ToListAsync();
        var nextRoleId = (await db.Roles.MaxAsync(r => (int?)r.Id) ?? 0) + 1;
        foreach (var roleName in RoleNames.All)
        {
            if (!existingRoles.Contains(roleName, StringComparer.OrdinalIgnoreCase))
            {
                db.Roles.Add(new Role { Id = nextRoleId++, Name = roleName });
            }
        }
        await db.SaveChangesAsync();

        var admin = await db.Users.FirstOrDefaultAsync(u => u.Email == "admin@ecommerce.com");
        if (admin != null)
        {
            admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword("Admin123!");
            await db.SaveChangesAsync();
        }
    }

    public HttpClient CreateAuthorizedClient(Guid userId, params string[] roles)
    {
        var client = CreateClient();
        var token = GenerateToken(userId, roles);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public string GenerateToken(Guid userId, params string[] roles)
    {
        var effectiveRoles = roles.Length > 0 ? roles : new[] { RoleNames.User };
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new("sub", userId.ToString())
        };

        foreach (var role in effectiveRoles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        foreach (var permission in RolePermissions.GetPermissionsForRoles(effectiveRoles))
        {
            claims.Add(new Claim(Permissions.ClaimType, permission));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: TestIssuer,
            audience: TestAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task WithDbContextAsync(Func<AuthDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;
}
