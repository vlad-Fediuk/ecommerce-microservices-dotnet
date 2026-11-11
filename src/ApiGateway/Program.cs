using System.Diagnostics;
using System.Security.Claims;
using System.Threading.RateLimiting;
using ApiGateway.Aggregators;
using ApiGateway.Clients;
using ApiGateway.Middleware;
using ApiGateway.Services;
using ECommerce.Shared.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Ocelot.DependencyInjection;
using Ocelot.Middleware;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("ocelot.json", optional: false, reloadOnChange: true);
builder.Configuration.AddJsonFile($"ocelot.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? new[] { "http://localhost:3000", "http://localhost:5173", "http://localhost:4200", "http://localhost:8080" };

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyMethod()
              .WithHeaders("Authorization", "Content-Type", "Accept", "Idempotency-Key", "X-Correlation-Id")
              .WithExposedHeaders("Location", "Idempotency-Replayed", "Retry-After", "X-Correlation-Id")
              .AllowCredentials();
    });
});

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ICacheMetricsService, CacheMetricsService>();

var catalogBaseUrl = builder.Configuration["Services:Catalog"] ?? "http://localhost:5002";
var orderingBaseUrl = builder.Configuration["Services:Ordering"] ?? "http://localhost:5003";
var authBaseUrl = builder.Configuration["Services:Auth"] ?? "http://localhost:5001";

builder.Services.AddHttpClient<ICatalogClient, CatalogClient>(client =>
{
    client.BaseAddress = new Uri(catalogBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(2);
});

builder.Services.AddHttpClient<IOrderingClient, OrderingClient>(client =>
{
    client.BaseAddress = new Uri(orderingBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(2);
});

builder.Services.AddHttpClient<IAuthClient, AuthClient>(client =>
{
    client.BaseAddress = new Uri(authBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(2);
});

builder.Services.AddScoped<IItemDetailsScreenAggregator, ItemDetailsScreenAggregator>();

builder.Services.AddControllers();

builder.Services.AddJwtAuth(builder.Configuration);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.Headers.RetryAfter = "60";
        var problem = new ProblemDetails
        {
            Type = "https://tools.ietf.org/html/rfc6585#section-4",
            Title = "Забагато запитів",
            Status = StatusCodes.Status429TooManyRequests,
            Detail = "Перевищено ліміт запитів. Спробуйте пізніше.",
            Instance = context.HttpContext.Request.Path
        };
        await context.HttpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
    };

    options.AddPolicy("auth", httpContext =>
    {
        var ip = httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault()
                 ?? httpContext.Connection.RemoteIpAddress?.ToString()
                 ?? "unknown_client";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });

    options.AddPolicy("general", httpContext =>
    {
        var partitionKey = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                           ?? httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault()
                           ?? httpContext.Connection.RemoteIpAddress?.ToString()
                           ?? "unknown_client";

        return RateLimitPartition.GetSlidingWindowLimiter(partitionKey, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0
        });
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "API Gateway",
        Version = "v1",
        Description = "Єдина точка входу до системи (API Gateway): складений ендпоінт, кеш, rate limiting, PBAC автентифікація."
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using Bearer scheme. Example: \"Authorization: Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT"
    });

    options.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecuritySchemeReference("Bearer", null, null),
            new List<string>()
        }
    });
});

builder.Services.AddOcelot(builder.Configuration);

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<GatewayProblemDetailsMiddleware>();

app.UseRouting();
app.UseCors();

app.UseRateLimiter();

app.UseAuthentication();
app.UseMiddleware<TokenValidationAndEnrichmentMiddleware>();
app.UseAuthorization();
app.UseMiddleware<CacheInvalidationMiddleware>();

app.Use(async (context, next) =>
{
    var sw = Stopwatch.StartNew();
    await next();
    sw.Stop();

    var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? string.Empty;
    var path = context.Request.Path.Value ?? string.Empty;
    var dest = path.StartsWith("/health") ? "Internal (/health)" :
               path.StartsWith("/api/v1/screens") ? "Internal (Composed Screen)" :
               path.StartsWith("/api/v1/cache") ? "Internal (Cache Management)" :
               path.StartsWith("/api/v1/auth") || path.StartsWith("/api/v1/users") ? "AuthService (5001)" :
               path.StartsWith("/api/v1/catalog") ? "CatalogService (5002)" :
               path.StartsWith("/api/orders") || path.StartsWith("/api/v1/orders") ? "OrderingService (5003)" :
               "Gateway";

    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("[{CorrelationId}] {Method} {Path} -> {Destination} -> {StatusCode} ({ElapsedMs} ms)",
        correlationId, context.Request.Method, context.Request.Path, dest, context.Response.StatusCode, sw.ElapsedMilliseconds);
});

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "API Gateway (Composed & Management)");
    c.SwaggerEndpoint("/swagger/auth/v1/swagger.json", "Auth Service");
    c.SwaggerEndpoint("/swagger/catalog/v1/swagger.json", "Catalog Service");
    c.SwaggerEndpoint("/swagger/orders/v1/swagger.json", "Ordering Service");
});

app.UseEndpoints(endpoints =>
{
    endpoints.MapMethods("/", new[] { "GET", "HEAD" }, () => Results.Ok(new { status = "Healthy", service = "ApiGateway" }));
    endpoints.MapControllers();
});

await app.UseOcelot();

app.Run();

public partial class Program;
