﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using CDR.DataHolder.API.Infrastructure.Extensions;
using CDR.DataHolder.IdentityServer.Extensions;
using CDR.DataHolder.IdentityServer.Interfaces;
using CDR.DataHolder.IdentityServer.Logging;
using CDR.DataHolder.IdentityServer.Models;
using IdentityModel;
using IdentityServer4;
using IdentityServer4.Configuration;
using IdentityServer4.Extensions;
using IdentityServer4.Models;
using IdentityServer4.Services;
using IdentityServer4.Stores;
using IdentityServer4.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using static CDR.DataHolder.IdentityServer.CdsConstants;
using static CDR.DataHolder.IdentityServer.Validation.Messages.ClientDetailsMessages;

namespace CDR.DataHolder.IdentityServer.Validation
{
    // Copied from Identity Server 4 AuthorizeRequestValidator, modified for minimal additional CTS validations
    public class PushedAuthorizationRequestValidator : IPushedAuthorizationRequestValidator
    {
        private readonly IConfiguration _config;
        private readonly IdentityServerOptions _options;
        private readonly IClientStore _clients;
        private readonly ICustomAuthorizeRequestValidator _customValidator;
        private readonly IRedirectUriValidator _uriValidator;
        private readonly IUserSession _userSession;
        private readonly CustomJwtRequestValidator _customJwtRequestValidator;
        private readonly IJwtRequestUriHttpClient _jwtRequestUriHttpClient;
        private readonly IPersistedGrantStore _persistedGrantStore;
        private readonly ILogger _logger;
        private readonly ITokenReplayCache _tokenCache;
        private readonly IHttpContextAccessor _httpContextAccessor;

        private readonly ResponseTypeEqualityComparer
            _responseTypeEqualityComparer = new ResponseTypeEqualityComparer();

        public PushedAuthorizationRequestValidator(
            IConfiguration config,
            IdentityServerOptions options,
            IClientStore clients,
            ICustomAuthorizeRequestValidator customValidator,
            IRedirectUriValidator uriValidator,
            IPersistedGrantStore persistedGrantStore,
            IUserSession userSession,
            CustomJwtRequestValidator customJwtRequestValidator,
            IJwtRequestUriHttpClient jwtRequestUriHttpClient,
            ILogger<CustomAuthorizeRequestValidator> logger,
            ITokenReplayCache tokenCache,
            IHttpContextAccessor httpContextAccessor)
        {
            _config = config;
            _options = options;
            _persistedGrantStore = persistedGrantStore;
            _clients = clients;
            _customValidator = customValidator;
            _uriValidator = uriValidator;
            _customJwtRequestValidator = customJwtRequestValidator;
            _userSession = userSession;
            _jwtRequestUriHttpClient = jwtRequestUriHttpClient;
            _logger = logger;
            _tokenCache = tokenCache;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task<AuthorizeRequestValidationResult> ValidateAsync(NameValueCollection parameters, ClaimsPrincipal subject = null)
        {
            _logger.LogDebug("Start authorize request protocol validation");

            var request = new ValidatedAuthorizeRequest
            {
                Options = _options,
                Subject = subject ?? Principal.Anonymous,
            };

            request.Raw = parameters ?? throw new ArgumentNullException(nameof(parameters));

            // load client_id
            var loadClientResult = await LoadClientAsync(request);
            if (loadClientResult.IsError)
            {
                return loadClientResult;
            }

            // look for JWT in request
            var jwtRequestResult = await ReadJwtRequestAsync(request);
            if (jwtRequestResult.IsError)
            {
                return jwtRequestResult;
            }

            // validate client_id and redirect_uri
            var clientResult = await ValidateClientAsync(request);
            if (clientResult.IsError)
            {
                return clientResult;
            }

            // state, response_type, response_mode
            var mandatoryResult = ValidateCoreParameters(request);
            if (mandatoryResult.IsError)
            {
                return mandatoryResult;
            }

            // scope, scope restrictions and plausability
            var scopeResult = await ValidateScopeAsync(request);
            if (scopeResult.IsError)
            {
                return scopeResult;
            }

            // nonce, prompt, acr_values, login_hint etc.
            var optionalResult = await ValidateOptionalParametersAsync(request);
            if (optionalResult.IsError)
            {
                return optionalResult;
            }

            // custom validator
            _logger.LogDebug("Calling into custom validator: {type}", _customValidator.GetType().FullName);
            var context = new CustomAuthorizeRequestValidationContext
            {
                Result = new AuthorizeRequestValidationResult(request),
            };
            await _customValidator.ValidateAsync(context);

            // client authentication validation
            var clientAuthenticationResult = await ValidateClientAuthenticationAsync(request);
            if (clientAuthenticationResult.IsError)
            {
                return clientAuthenticationResult;
            }

            // CDR Arrangement Id
            var cdrArrangementIdResult = await ValidateCdrArrangementId(request);
            if (cdrArrangementIdResult.IsError)
            {
                return cdrArrangementIdResult;
            }

            var customResult = context.Result;
            if (customResult.IsError)
            {
                LogError("Error in custom validation", customResult.Error, request);
                return Invalid(request, customResult.Error, customResult.ErrorDescription);
            }

            _logger.LogTrace("Authorize request protocol validation successful");

            return Valid(request);
        }

        private async Task<AuthorizeRequestValidationResult> LoadClientAsync(ValidatedAuthorizeRequest request)
        {
            //////////////////////////////////////////////////////////
            // client_id must be present
            /////////////////////////////////////////////////////////
            var clientId = request.Raw.Get(OidcConstants.AuthorizeRequest.ClientId);
            if (clientId.IsMissing() || clientId.Length > _options.InputLengthRestrictions.ClientId)
            {
                LogError("client_id is missing or too long", request);
                return Invalid(request, description: "Invalid client_id");
            }

            request.ClientId = clientId;

            //////////////////////////////////////////////////////////
            // check for valid client
            //////////////////////////////////////////////////////////
            var client = await _clients.FindEnabledClientByIdAsync(request.ClientId);
            if (client == null)
            {
                LogError("Unknown client or not enabled", request.ClientId, request);
                return Invalid(request, description: "Invalid client_id");
            }

            request.SetClient(client);

            return Valid(request);
        }

        private async Task<AuthorizeRequestValidationResult> ReadJwtRequestAsync(ValidatedAuthorizeRequest request)
        {
            //////////////////////////////////////////////////////////
            // look for optional request params
            /////////////////////////////////////////////////////////
            var jwtRequest = request.Raw.Get(OidcConstants.AuthorizeRequest.Request);

            // Customized for CTS to always validate if request uri is present (it isnt allowed)

            var jwtRequestUri = request.Raw.Get(OidcConstants.AuthorizeRequest.RequestUri);
            if (jwtRequest.IsPresent() && jwtRequestUri.IsPresent())
            {
                LogError("Both request and request_uri are present", request);
                return Invalid(request, description: "Only one request parameter is allowed");
            }

            if (jwtRequestUri.IsPresent())
            {
                // 512 is from the spec
                if (jwtRequestUri.Length > 512)
                {
                    LogError("request_uri is too long", request);
                    return Invalid(request, description: "request_uri is too long");
                }

                var jwt = await _jwtRequestUriHttpClient.GetJwtAsync(jwtRequestUri, request.Client);
                if (jwt.IsMissing())
                {
                    LogError("no value returned from request_uri", request);
                    return Invalid(request, description: "no value returned from request_uri");
                }

                jwtRequest = jwt;
            }

            // This code for validate request JWT is customised for CTS

            //////////////////////////////////////////////////////////
            // validate request JWT
            /////////////////////////////////////////////////////////
            if (jwtRequest.IsPresent())
            {
                // check length restrictions
                if (jwtRequest.Length >= _options.InputLengthRestrictions.Jwt)
                {
                    LogError("request value is too long", request);
                    return Invalid(request, description: "Invalid request value");
                }

                // validate the request JWT for this client
                var jwtRequestValidationResult = await _customJwtRequestValidator.ValidateAsync(request.Client, jwtRequest);
                if (jwtRequestValidationResult.IsError)
                {
                    LogError("request JWT validation failure", request);
                    return Invalid(request, error: PushedAuthorizationServiceErrorCodes.RequestJwtFailedValidation, description: "Request JWT failed validation");
                }

                // validate client_id match
                if (jwtRequestValidationResult.Payload.TryGetValue(OidcConstants.AuthorizeRequest.ClientId, out var payloadClientId)) // validates client id matches
                {
                    if (payloadClientId != request.Client.ClientId)
                    {
                        LogError("client_id in JWT payload does not match client_id in request", request);
                        return Invalid(request, error: PushedAuthorizationServiceErrorCodes.UnauthorizedClient, description: "Client_id in request JWT does not match request value");
                    }
                }
                else
                {
                    LogError("client_id missing from JWT payload", request);
                    return Invalid(request, error: PushedAuthorizationServiceErrorCodes.UnauthorizedClient, description: "client_id missing from request JWT");
                }

                // validate response_type match
                var responseType = request.Raw.Get(OidcConstants.AuthorizeRequest.ResponseType);
                if (responseType != null)
                {
                    if (jwtRequestValidationResult.Payload.TryGetValue(OidcConstants.AuthorizeRequest.ResponseType, out var payloadResponseType))
                    {
                        if (payloadResponseType != responseType)
                        {
                            LogError("response_type in JWT payload does not match response_type in request", request);
                            return Invalid(request, description: "Invalid JWT request");
                        }
                    }
                }

                //Validate grant_type for supported grant type Client Credential 
                var responseGrantType = request.Raw.Get(CdsConstants.GrantTypes.GrantType);
                if (!string.IsNullOrEmpty(responseGrantType))
                {
                    if (!string.Equals(responseGrantType, CdsConstants.GrantTypes.ClientCredentials, StringComparison.InvariantCultureIgnoreCase))
                    {
                        LogError("grant_type in request is invalid", request);
                        return Invalid(request, description: "Invalid grant_type");
                    }                    
                }

                // merge jwt payload values into original request parameters
                foreach (var key in jwtRequestValidationResult.Payload.Keys)
                {
                    var value = jwtRequestValidationResult.Payload[key];
                    request.Raw.Set(key, value);
                }

                request.RequestObjectValues = jwtRequestValidationResult.Payload;
            }
            else
            {
                return Invalid(request, description: "Invalid JWT request");
            }

            return Valid(request);
        }

        private async Task<AuthorizeRequestValidationResult> ValidateClientAsync(ValidatedAuthorizeRequest request)
        {
            //////////////////////////////////////////////////////////
            // redirect_uri must be present, and a valid uri
            //////////////////////////////////////////////////////////
            var redirectUri = request.Raw.Get(OidcConstants.AuthorizeRequest.RedirectUri);

            if (redirectUri.IsMissing() || redirectUri.Length > _options.InputLengthRestrictions.RedirectUri)
            {
                LogError("redirect_uri is missing or too long", request);
                return Invalid(request, description: "Invalid redirect_uri");
            }

            if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var _))
            {
                LogError("malformed redirect_uri", redirectUri, request);
                return Invalid(request, description: "Invalid redirect_uri");
            }

            //////////////////////////////////////////////////////////
            // check if client protocol type is oidc
            //////////////////////////////////////////////////////////
            if (request.Client.ProtocolType != IdentityServerConstants.ProtocolTypes.OpenIdConnect)
            {
                LogError("Invalid protocol type for OIDC authorize endpoint", request.Client.ProtocolType, request);
                return Invalid(request, OidcConstants.AuthorizeErrors.UnauthorizedClient, description: "Invalid protocol");
            }

            //////////////////////////////////////////////////////////
            // check if redirect_uri is valid
            //////////////////////////////////////////////////////////
            if (await _uriValidator.IsRedirectUriValidAsync(redirectUri, request.Client) == false)
            {
                LogError("Invalid redirect_uri", redirectUri, request);
                return Invalid(request, OidcConstants.AuthorizeErrors.UnauthorizedClient, "Invalid redirect_uri");
            }

            request.RedirectUri = redirectUri;

            return Valid(request);
        }

        private AuthorizeRequestValidationResult ValidateCoreParameters(ValidatedAuthorizeRequest request)
        {
            //////////////////////////////////////////////////////////
            // check state
            //////////////////////////////////////////////////////////
            var state = request.Raw.Get(OidcConstants.AuthorizeRequest.State);
            if (state.IsPresent())
            {
                request.State = state;
            }

            //////////////////////////////////////////////////////////
            // response_type must be present and supported
            //////////////////////////////////////////////////////////
            var responseType = request.Raw.Get(OidcConstants.AuthorizeRequest.ResponseType);
            if (responseType.IsMissing())
            {
                LogError("Missing response_type", request);
                return Invalid(request, OidcConstants.AuthorizeErrors.UnsupportedResponseType, "Missing response_type");
            }

            // The responseType may come in in an unconventional order.
            // Use an IEqualityComparer that doesn't care about the order of multiple values.
            // Per https://tools.ietf.org/html/rfc6749#section-3.1.1 -
            // 'Extension response types MAY contain a space-delimited (%x20) list of
            // values, where the order of values does not matter (e.g., response
            // type "a b" is the same as "b a").'
            // http://openid.net/specs/oauth-v2-multiple-response-types-1_0-03.html#terminology -
            // 'If a response type contains one of more space characters (%20), it is compared
            // as a space-delimited list of values in which the order of values does not matter.'
            if (!SupportedResponseTypes.Contains(responseType, _responseTypeEqualityComparer))
            {
                LogError("Response type not supported", responseType, request);
                return Invalid(request, OidcConstants.AuthorizeErrors.UnsupportedResponseType, "Response type not supported");
            }

            // Even though the responseType may have come in in an unconventional order,
            // we still need the request's ResponseType property to be set to the
            // conventional, supported response type.
            request.ResponseType = SupportedResponseTypes.First(
                supportedResponseType => _responseTypeEqualityComparer.Equals(supportedResponseType, responseType));

            //////////////////////////////////////////////////////////
            // match response_type to grant type
            //////////////////////////////////////////////////////////
            request.GrantType = ResponseTypeToGrantTypeMapping[request.ResponseType];

            // set default response mode for flow; this is needed for any client error processing below
            request.ResponseMode = AllowedResponseModesForGrantType[request.GrantType].First();

            //////////////////////////////////////////////////////////
            // check if flow is allowed at authorize endpoint
            //////////////////////////////////////////////////////////
            if (!AllowedGrantTypesForAuthorizeEndpoint.Contains(request.GrantType))
            {
                LogError("Invalid grant type", request.GrantType, request);
                return Invalid(request, description: "Invalid response_type");
            }

            //////////////////////////////////////////////////////////
            // check if PKCE is required and validate parameters
            //////////////////////////////////////////////////////////
            if (request.GrantType == GrantType.AuthorizationCode || request.GrantType == GrantType.Hybrid)
            {
                _logger.LogDebug("Checking for PKCE parameters");

                /////////////////////////////////////////////////////////////////////////////
                // validate code_challenge and code_challenge_method
                /////////////////////////////////////////////////////////////////////////////
                var proofKeyResult = ValidatePkceParameters(request);

                if (proofKeyResult.IsError)
                {
                    return proofKeyResult;
                }
            }

            //////////////////////////////////////////////////////////
            // check response_mode parameter and set response_mode
            //////////////////////////////////////////////////////////

            // check if response_mode parameter is present and valid
            var responseMode = request.Raw.Get(OidcConstants.AuthorizeRequest.ResponseMode);
            if (responseMode.IsPresent())
            {
                if (SupportedResponseModes.Contains(responseMode))
                {
                    if (AllowedResponseModesForGrantType[request.GrantType].Contains(responseMode))
                    {
                        request.ResponseMode = responseMode;
                    }
                    else
                    {
                        LogError("Invalid response_mode for flow", responseMode, request);
                        return Invalid(request, OidcConstants.AuthorizeErrors.UnsupportedResponseType, description: "Invalid response_mode");
                    }
                }
                else
                {
                    LogError("Unsupported response_mode", responseMode, request);
                    return Invalid(request, OidcConstants.AuthorizeErrors.UnsupportedResponseType, description: "Invalid response_mode");
                }
            }


            //////////////////////////////////////////////////////////
            // check if grant type is allowed for client
            //////////////////////////////////////////////////////////
            if (!request.Client.AllowedGrantTypes.Contains(request.GrantType))
            {
                LogError("Invalid grant type for client", request.GrantType, request);
                return Invalid(request, OidcConstants.AuthorizeErrors.UnauthorizedClient, "Invalid grant type for client");
            }

            //////////////////////////////////////////////////////////
            // check if response type contains an access token,
            // and if client is allowed to request access token via browser
            //////////////////////////////////////////////////////////
            var responseTypes = responseType.FromSpaceSeparatedString();
            if (responseTypes.Contains(OidcConstants.ResponseTypes.Token))
            {
                if (!request.Client.AllowAccessTokensViaBrowser)
                {
                    LogError("Client requested access token - but client is not configured to receive access tokens via browser", request);
                    return Invalid(request, description: "Client not configured to receive access tokens via browser");
                }
            }

            return Valid(request);
        }

        private AuthorizeRequestValidationResult ValidatePkceParameters(ValidatedAuthorizeRequest request)
        {
            var fail = Invalid(request);

            var codeChallenge = request.Raw.Get(OidcConstants.AuthorizeRequest.CodeChallenge);
            if (codeChallenge.IsMissing())
            {
                if (request.Client.RequirePkce)
                {
                    LogError("code_challenge is missing", request);
                    fail.ErrorDescription = "code challenge required";
                }
                else
                {
                    _logger.LogDebug("No PKCE used.");
                    return Valid(request);
                }

                return fail;
            }

            if (codeChallenge.Length < _options.InputLengthRestrictions.CodeChallengeMinLength ||
                codeChallenge.Length > _options.InputLengthRestrictions.CodeChallengeMaxLength)
            {
                LogError("code_challenge is either too short or too long", request);
                fail.ErrorDescription = "Invalid code_challenge";
                return fail;
            }

            request.CodeChallenge = codeChallenge;

            var codeChallengeMethod = request.Raw.Get(OidcConstants.AuthorizeRequest.CodeChallengeMethod);
            if (codeChallengeMethod.IsMissing())
            {
                _logger.LogDebug("Missing code_challenge_method, defaulting to plain");
                codeChallengeMethod = OidcConstants.CodeChallengeMethods.Plain;
            }

            if (!SupportedCodeChallengeMethods.Contains(codeChallengeMethod))
            {
                LogError("Unsupported code_challenge_method", codeChallengeMethod, request);
                fail.ErrorDescription = "Transform algorithm not supported";
                return fail;
            }

            // check if plain method is allowed
            if (codeChallengeMethod == OidcConstants.CodeChallengeMethods.Plain)
            {
                if (!request.Client.AllowPlainTextPkce)
                {
                    LogError("code_challenge_method of plain is not allowed", request);
                    fail.ErrorDescription = "Transform algorithm not supported";
                    return fail;
                }
            }

            request.CodeChallengeMethod = codeChallengeMethod;

            return Valid(request);
        }

        private async Task<AuthorizeRequestValidationResult> ValidateScopeAsync(ValidatedAuthorizeRequest request)
        {
            //////////////////////////////////////////////////////////
            // scope must be present
            //////////////////////////////////////////////////////////
            var scope = request.Raw.Get(OidcConstants.AuthorizeRequest.Scope);
            if (scope.IsMissing())
            {
                LogError("scope is missing", request);
                return Invalid(request, description: "Invalid scope");
            }

            if (scope.Length > _options.InputLengthRestrictions.Scope)
            {
                LogError("scopes too long.", request);
                return Invalid(request, description: "Invalid scope");
            }

            request.RequestedScopes = scope.FromSpaceSeparatedString().Distinct().ToList();

            if (request.RequestedScopes.Contains(IdentityServerConstants.StandardScopes.OpenId))
            {
                request.IsOpenIdRequest = true;
            }

            //////////////////////////////////////////////////////////
            // check scope vs response_type plausability
            //////////////////////////////////////////////////////////
            var requirement = ResponseTypeToScopeRequirement[request.ResponseType];
            if (requirement == ScopeRequirement.Identity ||
                requirement == ScopeRequirement.IdentityOnly)
            {
                if (request.IsOpenIdRequest == false)
                {
                    LogError("response_type requires the openid scope", request);
                    return Invalid(request, description: "Missing openid scope");
                }
            }

            //////////////////////////////////////////////////////////
            // check if scopes are valid/supported and check for resource scopes
            //////////////////////////////////////////////////////////
            if (request.RequestedScopes.AreScopesValid() == false)
            {
                return Invalid(request, OidcConstants.AuthorizeErrors.InvalidScope, "Invalid scope");
            }

            if (request.RequestedScopes.ContainsOpenIdScopes() && !request.IsOpenIdRequest)
            {
                LogError("Identity related scope requests, but no openid scope", request);
                return Invalid(request, OidcConstants.AuthorizeErrors.InvalidScope, "Identity scopes requested, but openid scope is missing");
            }

            if (request.RequestedScopes.ContainsApiResourceScopes())
            {
                request.IsApiResourceRequest = true;
            }

            //////////////////////////////////////////////////////////
            // check scopes and scope restrictions
            //////////////////////////////////////////////////////////
            if (request.RequestedScopes.AreScopesAllowed(request.Client.AllowedScopes) == false)
            {
                return Invalid(request, OidcConstants.AuthorizeErrors.UnauthorizedClient, description: "Invalid scope for client");
            }

            // TODO: fix this
            //request.ValidatedResources.ParsedScopes = _scopeValidator;

            //////////////////////////////////////////////////////////
            // check id vs resource scopes and response types plausability
            //////////////////////////////////////////////////////////
            if (!request.RequestedScopes.IsResponseTypeValid(request.ResponseType))
            {
                return Invalid(request, OidcConstants.AuthorizeErrors.InvalidScope, "Invalid scope for response type");
            }

            return Valid(request);
        }

        private async Task<AuthorizeRequestValidationResult> ValidateCdrArrangementId(ValidatedAuthorizeRequest request)
        {
            var jwtToken = new JwtSecurityTokenHandler().ReadJwtToken(request.Raw.Get(OidcConstants.AuthorizeRequest.Request));
            var claims = jwtToken.Claims.FirstOrDefault(x => x.Type.Trim() == AuthorizeRequest.Claims)?.Value;

            AuthorizeClaims authorizeClaims;

            try
            {
                authorizeClaims = JsonConvert.DeserializeObject<AuthorizeClaims>(claims);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Claims {Claims} in Request JWT could not deserialize.", claims);
                return Invalid(request, description: "Invalid JWT request");
            }

            if (string.IsNullOrWhiteSpace(authorizeClaims.CdrArrangementId))
            {
                return Valid(request);
            }
            else
            {
                var cdrArrangementGrant = await _persistedGrantStore.GetAsync(authorizeClaims.CdrArrangementId);

                if (cdrArrangementGrant != null && cdrArrangementGrant.ClientId == request.ClientId)
                {
                    return Valid(request);
                }
                else
                {
                    LogError($"Client sent CDR Arrangement Id Claim that was not found in grant store for their sub value or doesnt have a matching refresh token: {authorizeClaims.CdrArrangementId}", request);
                    return Invalid(request, error: PushedAuthorizationServiceErrorCodes.InvalidCdrArrangementId, description: "");
                }
            }
        }

        private async Task<AuthorizeRequestValidationResult> ValidateOptionalParametersAsync(ValidatedAuthorizeRequest request)
        {
            //////////////////////////////////////////////////////////
            // check nonce
            //////////////////////////////////////////////////////////
            var nonce = request.Raw.Get(OidcConstants.AuthorizeRequest.Nonce);
            if (nonce.IsPresent())
            {
                if (nonce.Length > _options.InputLengthRestrictions.Nonce)
                {
                    LogError("Nonce too long", request);
                    return Invalid(request, description: "Invalid nonce");
                }

                request.Nonce = nonce;
            }
            else
            {
                if (request.GrantType == GrantType.Implicit ||
                    request.GrantType == GrantType.Hybrid)
                {
                    // only openid requests require nonce
                    if (request.IsOpenIdRequest)
                    {
                        LogError("Nonce required for implicit and hybrid flow with openid scope", request);
                        return Invalid(request, description: "Invalid nonce");
                    }
                }
            }


            //////////////////////////////////////////////////////////
            // check prompt
            //////////////////////////////////////////////////////////
            var prompt = request.Raw.Get(OidcConstants.AuthorizeRequest.Prompt);
            if (prompt.IsPresent())
            {
                if (SupportedPromptModes.Contains(prompt))
                {
                    request.PromptModes = new List<string>(new string[] { prompt });
                }
                else
                {
                    _logger.LogDebug("Unsupported prompt mode - ignored: " + prompt);
                }
            }

            //////////////////////////////////////////////////////////
            // check ui locales
            //////////////////////////////////////////////////////////
            var uilocales = request.Raw.Get(OidcConstants.AuthorizeRequest.UiLocales);
            if (uilocales.IsPresent())
            {
                if (uilocales.Length > _options.InputLengthRestrictions.UiLocale)
                {
                    LogError("UI locale too long", request);
                    return Invalid(request, description: "Invalid ui_locales");
                }

                request.UiLocales = uilocales;
            }

            //////////////////////////////////////////////////////////
            // check display
            //////////////////////////////////////////////////////////
            // Display check removed

            //////////////////////////////////////////////////////////
            // check max_age
            //////////////////////////////////////////////////////////
            var maxAge = request.Raw.Get(OidcConstants.AuthorizeRequest.MaxAge);
            if (maxAge.IsPresent())
            {
                if (int.TryParse(maxAge, out var seconds))
                {
                    if (seconds >= 0)
                    {
                        request.MaxAge = seconds;
                    }
                    else
                    {
                        LogError("Invalid max_age.", request);
                        return Invalid(request, description: "Invalid max_age");
                    }
                }
                else
                {
                    LogError("Invalid max_age.", request);
                    return Invalid(request, description: "Invalid max_age");
                }
            }

            //////////////////////////////////////////////////////////
            // check login_hint
            //////////////////////////////////////////////////////////
            var loginHint = request.Raw.Get(OidcConstants.AuthorizeRequest.LoginHint);
            if (loginHint.IsPresent())
            {
                if (loginHint.Length > _options.InputLengthRestrictions.LoginHint)
                {
                    LogError("Login hint too long", request);
                    return Invalid(request, description: "Invalid login_hint");
                }

                request.LoginHint = loginHint;
            }

            //////////////////////////////////////////////////////////
            // check acr_values
            //////////////////////////////////////////////////////////
            var acrValues = request.Raw.Get(OidcConstants.AuthorizeRequest.AcrValues);
            if (acrValues.IsPresent())
            {
                if (acrValues.Length > _options.InputLengthRestrictions.AcrValues)
                {
                    LogError("Acr values too long", request);
                    return Invalid(request, description: "Invalid acr_values");
                }

                request.AuthenticationContextReferenceClasses = acrValues.FromSpaceSeparatedString().Distinct().ToList();
            }

            //////////////////////////////////////////////////////////
            // check custom acr_values: idp
            //////////////////////////////////////////////////////////
            var idp = request.GetIdP();
            if (idp.IsPresent())
            {
                // if idp is present but client does not allow it, strip it from the request message
                if (request.Client.IdentityProviderRestrictions != null && request.Client.IdentityProviderRestrictions.Any())
                {
                    if (!request.Client.IdentityProviderRestrictions.Contains(idp))
                    {
                        _logger.LogWarning("idp requested ({idp}) is not in client restriction list.", idp);
                        request.RemoveIdP();
                    }
                }
            }

            //////////////////////////////////////////////////////////
            // check session cookie
            //////////////////////////////////////////////////////////
            if (_options.Endpoints.EnableCheckSessionEndpoint &&
                request.Subject.IsAuthenticated())
            {
                var sessionId = await _userSession.GetSessionIdAsync();
                if (sessionId.IsPresent())
                {
                    request.SessionId = sessionId;
                }
                else
                {
                    LogError("Check session endpoint enabled, but SessionId is missing", request);
                }
            }

            return Valid(request);
        }

        private async Task<AuthorizeRequestValidationResult> ValidateClientAuthenticationAsync(ValidatedAuthorizeRequest request)
        {
            //////////////////////////////////////////////////////////
            // check for client assertion type
            //////////////////////////////////////////////////////////
            var clientAssertionType = request.Raw.Get(PushedAuthorizationRequest.ClientAssertionType);
            if (clientAssertionType.IsMissing() || clientAssertionType != ClientAssertionTypes.JwtBearer)
            {
                LogError("client_assertion_type is missing or invalid", request);
                return Invalid(request, description: "Invalid client_assertion_type");
            }

            //////////////////////////////////////////////////////////
            // check for client assertion
            //////////////////////////////////////////////////////////
            var clientAssertion = request.Raw.Get(PushedAuthorizationRequest.ClientAssertion);
            if (clientAssertion.IsMissing() || clientAssertion.Length > _options.InputLengthRestrictions.Jwt)
            {
                LogError("client_assertion is missing or too long", request);
                return Invalid(request, description: "Invalid client_assertion");
            }

            var client = await _clients.FindEnabledClientByIdAsync(request.ClientId);

            var trustedKeys = client.ClientSecrets.Where(s => s.Type == SecretTypes.JsonWebKey)
                    .Select(s => new Microsoft.IdentityModel.Tokens.JsonWebKey(s.Value))
                    .ToArray();

            if (!BeValidClientAssertion(clientAssertion, trustedKeys, client.ClientId))
            {
                LogError("client_assertion is invalid", request);
                return Invalid(request, description: "Invalid client_assertion");
            }

            return Valid(request);
        }

        private bool BeValidClientAssertion(string clientAssertion, Microsoft.IdentityModel.Tokens.JsonWebKey[] keys, string clientId)
        {
            var aud = _config["ParUri"];

            // Get absolute path
            var tokenValidationParameters = new TokenValidationParameters
            {
                IssuerSigningKeys = keys,
                ValidateIssuerSigningKey = true,
                ValidIssuer = clientId,
                ValidateIssuer = true,
                ValidAudience = aud,
                ValidateAudience = true,
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ValidateLifetime = true,
                ValidateTokenReplay = true,
                TokenReplayCache = _tokenCache,
            };

            JwtSecurityToken jwtToken;
            try
            {
                var handler = new JwtSecurityTokenHandler();
                handler.ValidateToken(clientAssertion, tokenValidationParameters, out var token);
                jwtToken = token as JwtSecurityToken;
            }
            catch (Exception exception)
            {
                var customMessage = exception switch
                {
                    SecurityTokenExpiredException _ => TokenExpired,
                    SecurityTokenInvalidAudienceException _ => InvalidAud,
                    SecurityTokenInvalidLifetimeException _ => InvalidNbf,
                    SecurityTokenInvalidSignatureException _ => InvalidSignature,
                    SecurityTokenNoExpirationException _ => ExpIsMissing,
                    SecurityTokenNotYetValidException _ => InvalidValidFrom,
                    SecurityTokenReplayDetectedException _ => TokenReplayed,
                    SecurityTokenInvalidIssuerException _ => InvalidIss,
                    Exception _ => ClientAssertionParseError,
                };

                _logger.LogError(exception, customMessage);
                return false;
            }

            if (jwtToken == null)
            {
                return false;
            }

            if (jwtToken.Id.IsMissing())
            {
                _logger.LogError(JtiIsMissing);
                return false;
            }

            if (_tokenCache.TryFind(jwtToken.Id))
            {
                _logger.LogError(JtiAlreadyUsed);
                return false;
            }

            _tokenCache.TryAdd(jwtToken.Id, jwtToken.ValidTo);

            if (jwtToken.Subject.IsMissing())
            {
                _logger.LogError(SubIsMissing);
                return false;
            }

            if (jwtToken.Subject.Length > _options.InputLengthRestrictions.ClientId)
            {
                _logger.LogError(SubTooLong);
                return false;
            }

            if (jwtToken.Subject != jwtToken.Issuer || jwtToken.Subject != clientId)
            {
                _logger.LogError(InvalidSub);
                return false;
            }

            return true;
        }

        private AuthorizeRequestValidationResult Invalid(ValidatedAuthorizeRequest request, string error = OidcConstants.AuthorizeErrors.InvalidRequest, string description = null)
        {
            return new AuthorizeRequestValidationResult(request, error, description);
        }

        private AuthorizeRequestValidationResult Valid(ValidatedAuthorizeRequest request)
        {
            return new AuthorizeRequestValidationResult(request);
        }

        private void LogError(string message, ValidatedAuthorizeRequest request)
        {
            var requestDetails = new AuthorizationRequestValidationLog(request);
            _logger.LogError(message + "\n{@requestDetails}", requestDetails);
        }

        private void LogError(string message, string detail, ValidatedAuthorizeRequest request)
        {
            var requestDetails = new AuthorizationRequestValidationLog(request);
            _logger.LogError(message + ": {detail}\n{@requestDetails}", detail, requestDetails);
        }
    }
}
