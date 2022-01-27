
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CDR.DataHolder.Resource.API.Business.Services {

    public struct PrivateArrangement {
        [JsonProperty("account_ids")]
        public string[] AccountIDs;  
    }

    public class AcpManagementService {
        private readonly IConfiguration _config;
        private readonly ILogger<AcpManagementService> _logger;

        public AcpManagementService(
            IConfiguration config,
            ILogger<AcpManagementService> logger
        ) {
            _config = config;
            _logger = logger;
        }

        public async Task<PrivateArrangement> IntrospecArrangement(string arrangementID) {
            var httpClient = await getAuthenticatedClient();
            var acpWorkspace = _config["AcpCDRWorkspace"];
            var arrangementIntrospectionEndpoint = $"{_config["AcpManagementBaseURI"]}/servers/{acpWorkspace}/cdr/arrangements/{arrangementID}";             
            var response = await httpClient.GetAsync(arrangementIntrospectionEndpoint);

            if (response.IsSuccessStatusCode) {
                var body = await response.Content.ReadAsStringAsync();
                var arrangement = JsonConvert.DeserializeObject<PrivateArrangement>(body);
                return arrangement;
            }

            throw new HttpRequestException("failed to retrieve cdr arrangement");
        }

        private async Task<HttpClient> getAuthenticatedClient() {
            var handler = new HttpClientHandler();  
            var httpClient = new HttpClient(handler);
            handler.ServerCertificateCustomValidationCallback = (a, b, c, d) => true;

            var tokenEndpoint = _config["AcpManagementBaseURI"] + "/oauth2/token";
            var clientID = _config["AcpManagementClientID"];
            var clientSecret = _config["AcpManagementClientSecret"];

            var basicAuth = Encoding.ASCII.GetBytes($"{clientID}:{clientSecret}");
            var b64BasicAuth = Convert.ToBase64String(basicAuth);
            
            var formFields = new List<KeyValuePair<string, string>>();
            formFields.Add(new KeyValuePair<string, string>("grant_type", "client_credentials"));
            formFields.Add(new KeyValuePair<string, string>("client_id", clientID));
            formFields.Add(new KeyValuePair<string, string>("client_secret", clientSecret));

            var response = await httpClient.PostAsync(tokenEndpoint, new FormUrlEncodedContent(formFields));

            if (response.IsSuccessStatusCode) {
                var body = await response.Content.ReadAsStringAsync();
                var json = JsonConvert.DeserializeObject<JObject>(body);
                var token = json["access_token"].ToString();

                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return httpClient;
            }

            throw new HttpRequestException("failed to authenticate client");
        } 
    }
}