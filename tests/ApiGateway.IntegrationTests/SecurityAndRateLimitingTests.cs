using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ApiGateway.IntegrationTests.Infrastructure;
using ECommerce.Shared.Security;
using FluentAssertions;
using Xunit;

namespace ApiGateway.IntegrationTests;

public class SecurityAndRateLimitingTests : IClassFixture<GatewayTestFactory>
{
    private readonly GatewayTestFactory _factory;

    public SecurityAndRateLimitingTests(GatewayTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ЗапитБезТокена_Повертає401ProblemDetails_ІНеВикликаєСервіси()
    {
        _factory.Reset();
        var itemId = Guid.NewGuid();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.168.1.101");

        var response = await client.GetAsync($"/api/v1/screens/item-details/{itemId}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.CatalogStub.CallCount.Should().Be(0);
        _factory.OrderingStub.CallCount.Should().Be(0);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(401);
        problem.GetProperty("title").GetString().Should().Be("Не авторизовано");
        problem.GetProperty("detail").GetString().Should().Be("Токен автентифікації відсутній або недійсний.");
    }

    [Fact]
    public async Task ЗапитБезТокенаАлеЗПідробленимиЗаголовками_Повертає401Unauthorized()
    {
        _factory.Reset();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.168.1.105");
        client.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-User-Roles", RoleNames.Admin);
        client.DefaultRequestHeaders.Add("X-User-Permissions", Permissions.System.ManageCache);

        var response = await client.GetAsync("/api/v1/cache/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ЗапитЗНеваліднимТокеном_Повертає401ProblemDetails()
    {
        _factory.Reset();
        var itemId = Guid.NewGuid();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.168.1.102");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid.jwt.token");

        var response = await client.GetAsync($"/api/v1/screens/item-details/{itemId}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.CatalogStub.CallCount.Should().Be(0);
        _factory.OrderingStub.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ДоступДоУправлінняКешем_ЗвичайномуКористувачуЗаборонено_Повертає403ProblemDetails()
    {
        _factory.Reset();
        var userClient = _factory.CreateAuthorizedClient(Guid.NewGuid(), RoleNames.User);

        var metricsResponse = await userClient.GetAsync("/api/v1/cache/metrics");
        var invalidateResponse = await userClient.PostAsync("/api/v1/cache/invalidate", null);

        metricsResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        invalidateResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var problem = await metricsResponse.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(403);
        problem.GetProperty("title").GetString().Should().Be("Доступ заборонено");
    }

    [Fact]
    public async Task Cors_ЗДозволеногоДжерела_ПовертаєЗаголовкиДоступуТаExposedHeaders()
    {
        _factory.Reset();
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", "http://localhost:3000");

        var response = await client.SendAsync(request);

        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins).Should().BeTrue();
        origins.Should().Contain("http://localhost:3000");

        response.Headers.TryGetValues("Access-Control-Expose-Headers", out var exposed).Should().BeTrue();
        var exposedCombined = string.Join(",", exposed!);
        exposedCombined.Should().Contain("Location");
        exposedCombined.Should().Contain("Idempotency-Replayed");
        exposedCombined.Should().Contain("Retry-After");
    }

    [Fact]
    public async Task ПеревищенняЛімітуЗапитів_Повертає429ТаЗаголовокRetryAfter()
    {
        _factory.Reset();
        var client = _factory.CreateAuthorizedClient(Guid.NewGuid(), RoleNames.Admin);
        client.DefaultRequestHeaders.Remove("X-Forwarded-For");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.168.1.100");

        HttpResponseMessage lastResponse = null!;

        for (var i = 0; i < 40; i++)
        {
            lastResponse = await client.GetAsync("/api/v1/cache/metrics");
            if (lastResponse.StatusCode == HttpStatusCode.TooManyRequests)
            {
                break;
            }
        }

        lastResponse.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        lastResponse.Headers.RetryAfter.Should().NotBeNull();

        var problem = await lastResponse.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(429);
        problem.GetProperty("title").GetString().Should().Be("Забагато запитів");
        problem.GetProperty("detail").GetString().Should().Be("Перевищено ліміт запитів. Спробуйте пізніше.");
    }
}
