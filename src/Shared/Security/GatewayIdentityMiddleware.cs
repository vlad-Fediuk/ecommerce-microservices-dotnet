using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace ECommerce.Shared.Security;

public class GatewayIdentityMiddleware
{
    private readonly RequestDelegate _next;

    public GatewayIdentityMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            if (!context.User.TryGetUserId(out _))
            {
                if (context.Request.Headers.TryGetValue("X-User-Id", out var headerId) &&
                    Guid.TryParse(headerId.FirstOrDefault(), out var parsedHeaderId))
                {
                    EnrichExistingIdentity(context, parsedHeaderId);
                }
                else
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity());
                }
            }
            else
            {
                EnrichRolesAndPermissionsFromHeaders(context);
            }
        }

        await _next(context);
    }

    private static void EnrichExistingIdentity(HttpContext context, Guid userId)
    {
        if (context.User.Identity is ClaimsIdentity identity)
        {
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
            EnrichRolesAndPermissionsFromHeaders(context);
        }
    }

    private static void EnrichRolesAndPermissionsFromHeaders(HttpContext context)
    {
        if (context.User.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        var existingRoles = context.User.GetRoles();
        if (context.Request.Headers.TryGetValue("X-User-Roles", out var rolesHeader))
        {
            var headerRoles = rolesHeader.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var role in headerRoles)
            {
                if (!existingRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
                {
                    identity.AddClaim(new Claim(ClaimTypes.Role, role));
                }
            }
        }

        if (context.Request.Headers.TryGetValue("X-User-Permissions", out var permsHeader))
        {
            var existingPerms = context.User.FindAll(Permissions.ClaimType).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var headerPerms = permsHeader.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var perm in headerPerms)
            {
                if (!existingPerms.Contains(perm))
                {
                    identity.AddClaim(new Claim(Permissions.ClaimType, perm));
                }
            }
        }
    }
}
