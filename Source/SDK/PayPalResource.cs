using System;
using System.Collections.Generic;
using System.Collections;
using System.Net;
using Newtonsoft.Json;
using PayPal.Log;
using System.Text;
using PayPal.Api;
using System.IO;

namespace PayPal
{
    /// <summary>
    /// Abstract class that handles configuring an HTTP request prior to making an API call.
    /// </summary>
    internal abstract class PayPalResource
    {
        /// <summary>
        /// Logs output statements, errors, debug info to a text file    
        /// </summary>
        private static Logger logger = Logger.GetLogger(typeof(PayPalResource));

        private static ArrayList retryCodes = new ArrayList(new HttpStatusCode[] 
                                                { HttpStatusCode.GatewayTimeout,
                                                  HttpStatusCode.RequestTimeout,
                                                  HttpStatusCode.InternalServerError,
                                                  HttpStatusCode.ServiceUnavailable,
                                                });

        /// <summary>
        /// Configures and executes REST call: Supports JSON
        /// </summary>
        /// <param name="apiContext">APIContext object</param>
        /// <param name="httpMethod">HttpMethod type</param>
        /// <param name="resource">URI path of the resource</param>
        /// <param name="payload">JSON request payload</param>
        /// <returns>Response object or null otherwise for void API calls</returns>
        /// <exception cref="PayPal.HttpException">Thrown if there was an error sending the request.</exception>
        /// <exception cref="PayPal.PaymentsException">Thrown if an HttpException was raised and contains a Payments API error object.</exception>
        /// <exception cref="PayPal.PayPalException">Thrown for any other issues encountered. See inner exception for further details.</exception>
        public static object ConfigureAndExecute(APIContext apiContext, HttpMethod httpMethod, string resource, string payload = "")
        {
            return ConfigureAndExecute<object>(apiContext, httpMethod, resource, payload);
        }

        /// <summary>
        /// Configures and executes REST call: Supports JSON
        /// </summary>
        /// <typeparam name="T">Generic Type parameter for response object</typeparam>
        /// <param name="apiContext">APIContext object</param>
        /// <param name="httpMethod">HttpMethod type</param>
        /// <param name="resource">URI path of the resource</param>
        /// <param name="payload">JSON request payload</param>
        /// <returns>Response object or null otherwise for void API calls</returns>
        /// <exception cref="PayPal.HttpException">Thrown if there was an error sending the request.</exception>
        /// <exception cref="PayPal.PaymentsException">Thrown if an HttpException was raised and contains a Payments API error object.</exception>
        /// <exception cref="PayPal.PayPalException">Thrown for any other issues encountered. See inner exception for further details.</exception>
        public static T ConfigureAndExecute<T>(APIContext apiContext, HttpMethod httpMethod, string resource, string payload = "")
        {
            // Verify the state of the APIContext object.
            if (apiContext == null)
            {
                throw new PayPalException("APIContext object is null");
            }

            try
            {
                var config = apiContext.GetConfigWithDefaults();
                var headersMap = GetHeaderMap(apiContext);
                var endpoint = GetEndpoint(config);

                // Create the URI where the HTTP request will be sent.
                Uri uniformResourceIdentifier = null;
                var baseUri = new Uri(endpoint);
                if (!Uri.TryCreate(baseUri, resource, out uniformResourceIdentifier))
                {
                    throw new PayPalException("Cannot create URL; baseURI=" + baseUri.ToString() + ", resourcePath=" + resource);
                }

                // Create the HttpRequest object that will be used to send the HTTP request.
                var connMngr = ConnectionManager.Instance;
                connMngr.GetConnection(config, uniformResourceIdentifier.ToString());
                var httpRequest = connMngr.GetConnection(config, uniformResourceIdentifier.ToString());
                httpRequest.Method = httpMethod.ToString();

                // Set custom content type (default to [application/json])
                if (headersMap != null && headersMap.ContainsKey(BaseConstants.ContentTypeHeader))
                {
                    httpRequest.ContentType = headersMap[BaseConstants.ContentTypeHeader].Trim();
                    headersMap.Remove(BaseConstants.ContentTypeHeader);
                }
                else
                {
                    httpRequest.ContentType = BaseConstants.ContentTypeHeaderJson;
                }

                // Set User-Agent HTTP header
                if (headersMap.ContainsKey(BaseConstants.UserAgentHeader))
                {
                    // aganzha
                    //iso-8859-1
                    var iso8851 = Encoding.GetEncoding("iso-8859-1", new EncoderReplacementFallback(string.Empty), new DecoderExceptionFallback());
                    var bytes = Encoding.Convert(Encoding.UTF8, iso8851, Encoding.UTF8.GetBytes(headersMap[BaseConstants.UserAgentHeader]));
                    httpRequest.UserAgent = iso8851.GetString(bytes);
                    headersMap.Remove(BaseConstants.UserAgentHeader);
                }

                // Set Custom HTTP headers
                foreach (KeyValuePair<string, string> entry in headersMap)
                {
                    httpRequest.Headers.Add(entry.Key, entry.Value);
                }

                // Log the headers
                foreach (string headerName in httpRequest.Headers)
                {
                    logger.DebugFormat(headerName + ":" + httpRequest.Headers[headerName]);
                }

                // Execute call
                var connectionHttp = new HttpConnection(config);
                var response = connectionHttp.Execute(payload, httpRequest);

                if (typeof(T).Name.Equals("Object"))
                {
                    return default(T);
                }
                else if (typeof(T).Name.Equals("String"))
                {
                    return (T)Convert.ChangeType(response, typeof(T));
                }

                return JsonFormatter.ConvertFromJson<T>(response);
            }
            catch (HttpException ex)
            {
                //  Check to see if we have a Payments API error.
                if (ex.StatusCode == HttpStatusCode.BadRequest)
                {
                    PaymentsException paymentsEx;
                    if (ex.TryConvertTo<PaymentsException>(out paymentsEx))
                    {
                        throw paymentsEx;
                    }
                }
                throw;
            }
            catch (PayPalException)
            {
                // If we get a PayPalException, just rethrow to preserve the stack trace.
                throw;
            }
            catch (System.Exception ex)
            {
                throw new PayPalException(ex.Message, ex);
            }
        }

        /// <summary>
        /// Gets a collection of headers to be used in an HTTP request.
        /// </summary>
        /// <param name="apiContext">APIContext object containing information needed to construct the headers map.</param>
        /// <returns>A collection of headers.</returns>
        public static Dictionary<string, string> GetHeaderMap(APIContext apiContext)
        {
            var headers = new Dictionary<string, string>();

            /*
		     * The implementation is PayPal specific. The Authorization header is
		     * formed for OAuth or Basic, for OAuth system the authorization token
		     * passed as a parameter is used in creation of HTTP header, for Basic
		     * Authorization the ClientID and ClientSecret passed as parameters are
		     * used after a Base64 encoding.
		     */
            if (!string.IsNullOrEmpty(apiContext.AccessToken))
            {
                headers.Add(BaseConstants.AuthorizationHeader, apiContext.AccessToken);
            }
            else 
            {
                var config = apiContext.GetConfigWithDefaults();
                var clientId = config.ContainsKey(BaseConstants.ClientId) ? config[BaseConstants.ClientId] : null;
                var clientSecret = config.ContainsKey(BaseConstants.ClientSecret) ? config[BaseConstants.ClientSecret] : null;
                if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
                {
                    headers.Add(BaseConstants.AuthorizationHeader, "Basic " + EncodeToBase64(clientId, clientSecret));
                }
            }

            /*
             * Appends request Id which is used by PayPal API service for
		     * Idempotency
             */
            var requestId = apiContext.MaskRequestId ? null : apiContext.RequestId;
            if (!string.IsNullOrEmpty(requestId))
            {
                headers.Add(BaseConstants.PayPalRequestIdHeader, requestId);
            }

            // Add User-Agent header for tracking in PayPal system
            var userAgentMap = UserAgentHeader.GetHeader();
            if (userAgentMap != null && userAgentMap.Count > 0)
            {
                foreach (KeyValuePair<string, string> entry in userAgentMap)
                {
                    headers.Add(entry.Key, entry.Value);
                }
            }

            // Add any custom headers
            if (apiContext.HTTPHeaders != null && apiContext.HTTPHeaders.Count > 0)
            {
                foreach (var header in apiContext.HTTPHeaders)
                {
                    headers.Add(header.Key, header.Value);
                }
            }
            return headers;
        }

        /// <summary>
        /// Gets the endpoint to be used when making an HTTP call to the REST API.
        /// </summary>
        /// <returns>The endpoint to be used when making an HTTP call to the REST API.</returns>
        public static string GetEndpoint(Dictionary<string, string> config)
        {
            string endpoint = null;

            // Try and load the endpoint from the config.
            if (config.ContainsKey(BaseConstants.EndpointConfig))
            {
                endpoint = config[BaseConstants.EndpointConfig];
            }
            else if (config.ContainsKey(BaseConstants.ApplicationModeConfig))
            {
                switch (config[BaseConstants.ApplicationModeConfig])
                {
                    case BaseConstants.LiveMode:
                        endpoint = BaseConstants.RESTLiveEndpoint;
                        break;
                    case BaseConstants.SandboxMode:
                        endpoint = BaseConstants.RESTSandboxEndpoint;
                        break;
                }
            }

            // If no endpoint is defined, then default to sandbox.
            if (string.IsNullOrEmpty(endpoint))
            {
                endpoint = BaseConstants.RESTSandboxEndpoint;
            }

            if (!endpoint.EndsWith("/"))
            {
                endpoint += "/";
            }

            return endpoint;
        }

        private static string EncodeToBase64(string clientID, string clientSecret)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(clientID + ":" + clientSecret);
                return Convert.ToBase64String(bytes);
            }
            catch (System.Exception ex)
            {
                throw new PayPalException(ex.Message, ex);
            }
        }
    }
}
