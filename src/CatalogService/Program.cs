using CatalogService.Data;
using CatalogService.Features.Categories;
using CatalogService.Features.Items;
using ECommerce.Shared.Filters;
using ECommerce.Shared.Handlers;
using ECommerce.Shared.Security;
using FluentValidation;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsEnvironment("Testing"))
{
    var restPort = builder.Configuration.GetValue("Kestrel:RestPort", 5002);
    var grpcPort = builder.Configuration.GetValue("Kestrel:GrpcPort", 5102);

    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.ListenAnyIP(restPort, o => o.Protocols = HttpProtocols.Http1AndHttp2);
        kestrel.ListenAnyIP(grpcPort, o => o.Protocols = HttpProtocols.Http2);
    });
}

builder.Services.AddGrpc();

if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddDbContext<CatalogDbContext>((sp, options) =>
    {
        var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        options.UseNpgsql(connectionString);
    });
}

builder.Services.AddScoped<ICatalogRepository, CatalogRepository>();
builder.Services.AddScoped<ICategoryRepository, CategoryRepository>();
builder.Services.AddScoped<ICatalogService, CatalogService.Features.Items.CatalogService>();

builder.Services.AddValidatorsFromAssemblyContaining<Program>();

builder.Services.AddControllers(options =>
{
    options.Filters.Add<ValidationActionFilter>();
});

builder.Services.AddJwtAuth(builder.Configuration);

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Catalog Service API",
        Version = "v1",
        Description = "Microservice for managing product categories and catalog items"
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using Bearer scheme. Example: \"Authorization: Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT"
    });

    c.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecuritySchemeReference("Bearer", null, null),
            new List<string>()
        }
    });
});

var app = builder.Build();

if (!app.Environment.IsEnvironment("Testing"))
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        if (db.Database.IsRelational())
        {
            await db.Database.MigrateAsync();
        }
        else
        {
            await db.Database.EnsureCreatedAsync();
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Database initialization failed on startup.");
    }
}

app.UseExceptionHandler();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Catalog Service API v1");
    c.RoutePrefix = "swagger";
});

app.UseJwtAuth();

app.MapControllers();
app.MapGrpcService<CatalogGrpcServiceImpl>();

app.MapGet("/health", async (CatalogDbContext db) =>
{
    try
    {
        var canConnect = await db.Database.CanConnectAsync();
        return canConnect
            ? Results.Ok(new { service = "CatalogService", status = "Healthy", database = "Connected", port = 5002 })
            : Results.Json(new { service = "CatalogService", status = "Degraded", database = "Unavailable" }, statusCode: 503);
    }
    catch (Exception ex)
    {
        return Results.Json(new { service = "CatalogService", status = "Unhealthy", error = ex.Message }, statusCode: 503);
    }
})
.AllowAnonymous()
.WithName("HealthCheck")
.WithTags("Health");

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.Run();

public partial class Program;
