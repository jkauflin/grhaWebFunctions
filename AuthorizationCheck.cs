/*==============================================================================
(C) Copyright 2024,2026 John J Kauflin, All rights reserved.
--------------------------------------------------------------------------------
DESCRIPTION:  Functions to parse a request context and look for authentication
              and authorization information in the x-ms-client-principal 
              header

--------------------------------------------------------------------------------
Modification History
2024-11-11 JJK  Initial version (check user role from function context for auth)
2026-08-02 JJK  Updated to use JWT token from Authorization header instead of 
                x-ms-client-principal header (as part of migrating Azure 
                Function to .NET 10 isolated worker model).  Authorization check 
                is now done by validating the JWT token and checking for the 
                required role in the "roles" claim (on the Azure Entra ID).
                The API Function is a registered application in Azure Entra ID 
                and the roles are defined in the app registration.  
                The client application must request an access token for the 
                API Function and include it in the Authorization header of the 
                request (not SWA Easy Auth, but a real access token from 
                Azure Entra ID).
================================================================================*/

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

public class AuthorizationCheck
{
    private readonly ILogger log;
    private readonly string? issuer;
    private readonly string? audience;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? openIdConfigManager;

    public AuthorizationCheck(ILogger logger, IConfiguration? configuration = null)
    {
        log = logger;

        var tenantId = configuration?["AUTH_TENANT_ID"] ?? Environment.GetEnvironmentVariable("AUTH_TENANT_ID");
        audience = configuration?["AUTH_AUDIENCE"] ?? Environment.GetEnvironmentVariable("AUTH_AUDIENCE");
        issuer = configuration?["AUTH_ISSUER"] ?? Environment.GetEnvironmentVariable("AUTH_ISSUER");

        if (string.IsNullOrWhiteSpace(issuer) && !string.IsNullOrWhiteSpace(tenantId))
        {
            issuer = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        }

        var metadataAddress = !string.IsNullOrWhiteSpace(tenantId)
            ? $"https://login.microsoftonline.com/{tenantId}/v2.0/.well-known/openid-configuration"
            : null;

        if (!string.IsNullOrWhiteSpace(metadataAddress))
        {
            openIdConfigManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever());
        }
    }

    public bool UserAuthorizedForRole(Microsoft.Azure.Functions.Worker.Http.HttpRequestData req, string userRoleToCheck, out string userName)
    {
        userName = string.Empty;

        try
        {
            var claimsPrincipal = ExtractPrincipal(req);
            if (claimsPrincipal == null)
            {
                log.LogWarning("No valid bearer token was supplied for role check. ");
                return false;
            }

            userName = GetUserName(claimsPrincipal);

            /*
            var claimValues = claimsPrincipal.Claims
                .Select(c => $"{c.Type}={c.Value}")
                .ToArray();
            log.LogWarning(">>> claimsPrincipal has {ClaimCount} claims: {Claims}", claimValues.Length, string.Join("; ", claimValues));
            */

            var userAuthorized = claimsPrincipal.IsInRole(userRoleToCheck);
            if (!userAuthorized)
            {
                log.LogWarning($"User is authenticated but missing the required role. roleToCheck={userRoleToCheck}, userName={userName}");
            }

            return userAuthorized;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Exception in UserAuthorizedForRole");
            return false;
        }
    }

    private ClaimsPrincipal? ExtractPrincipal(Microsoft.Azure.Functions.Worker.Http.HttpRequestData req)
    {
        if (!TryGetBearerToken(req, out var token))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(audience) || string.IsNullOrWhiteSpace(issuer) || openIdConfigManager is null)
        {
            log.LogWarning("Authorization validation is not configured. Set AUTH_TENANT_ID, AUTH_AUDIENCE, and AUTH_ISSUER.");
            return null;
        }

        try
        {
            var handler = new JwtSecurityTokenHandler();
            handler.MapInboundClaims = false; // don't silently rewrite claim names — read raw JWT claim names
            handler.InboundClaimTypeMap.Clear(); // don't map claim names to Microsoft-specific names

            var config = openIdConfigManager.GetConfigurationAsync().GetAwaiter().GetResult();
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudiences = new[] { audience },
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = config.SigningKeys,
                ClockSkew = TimeSpan.FromMinutes(2),
                //RoleClaimType = ClaimTypes.Role
                RoleClaimType = "roles"   // matches the raw Azure AD v2 claim name now that mapping is off
            };

            return handler.ValidateToken(token, validationParameters, out _);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Bearer token validation failed.");
            return null;
        }
    }

    //private static bool TryGetBearerToken(Microsoft.Azure.Functions.Worker.Http.HttpRequestData req, out string token)
    private bool TryGetBearerToken(Microsoft.Azure.Functions.Worker.Http.HttpRequestData req, out string token)
    {
        token = string.Empty;

        if (!req.Headers.TryGetValues("Authorization", out var authHeaders))
        {
            log.LogWarning("Authorization header not found.");

            foreach (var h in req.Headers)
            {
                log.LogWarning("{Name} = {Value}",
                    h.Key,
                    string.Join(",", h.Value));
            }

            return false;
        }

        /*
        if (!req.Headers.TryGetValues("Authorization", out var authHeaders))
        {
            return false;
        }
        */

        var bearer = authHeaders.FirstOrDefault();
        //log.LogWarning("Authorization header (Bearer) = {Header}", bearer);
        if (string.IsNullOrWhiteSpace(bearer) || !bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = bearer["Bearer ".Length..];
        /*
        log.LogWarning("Bearer starts with: {Token}",
            bearer.Length > 40 ? bearer.Substring(0,40) : bearer);
        */  
        return !string.IsNullOrWhiteSpace(token);
    }

    private static string GetUserName(ClaimsPrincipal principal)
    {
        return principal.FindFirst("preferred_username")?.Value
            ?? principal.FindFirst(ClaimTypes.Name)?.Value
            ?? principal.FindFirst(ClaimTypes.Upn)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? string.Empty;
    }
}
