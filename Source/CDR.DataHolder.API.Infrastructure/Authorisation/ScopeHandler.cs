﻿using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace CDR.DataHolder.API.Infrastructure.Authorisation
{
    public class ScopeHandler : AuthorizationHandler<ScopeRequirement>
    {
        private const string SCOPE_CLAIM_NAME = "http://schemas.microsoft.com/identity/claims/scope";
    
        private readonly ILogger<ScopeHandler> _logger;

        public ScopeHandler(ILogger<ScopeHandler> logger)
        {
            _logger = logger;
        }

        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ScopeRequirement requirement)
        {
          
            // Check that authentication was successful before doing anything else
            if (!context.User.Identity.IsAuthenticated)
            {
                return Task.CompletedTask;
            }

            // If user does not have the scope claim, get out of here
            if (!context.User.HasClaim(c => c.Type == SCOPE_CLAIM_NAME && c.Issuer == requirement.Issuer))
            {
                _logger.LogError($"Unauthorized request. Access token is missing 'scope' claim for issuer '{requirement.Issuer}'.");
                return Task.CompletedTask;
            }

            // Find the matching scope value.
            var hasScope = context.User.Claims.Any(c => c.Type == SCOPE_CLAIM_NAME && c.Issuer == requirement.Issuer && c.Value == requirement.Scope);

            // Succeed if the scope array contains the required scope
            if (hasScope)
            {
                context.Succeed(requirement);
            }
            else
            {
                _logger.LogError($"Unauthorized request. Access token does not contain scope '{requirement.Scope}' for issuer '{requirement.Issuer}'.");
            }

            return Task.CompletedTask;
        }
    }
}
