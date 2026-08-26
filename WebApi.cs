/*==============================================================================
(C) Copyright 2024 John J Kauflin, All rights reserved.
--------------------------------------------------------------------------------
DESCRIPTION:  Azure API Functions for the Static Web App (SWA) - this should
              replace all of the old PHP code, and be the "backend" services
              for the web apps
--------------------------------------------------------------------------------
Modification History
2024-06-30 JJK  Initial version (moving logic from PHP to here to update data
                in MediaInfo entities in Cosmos DB NoSQL
2024-07-28 JJK  Resolved JSON parse and DEBUG issues and got the update working
2024-08-27 JJK  Added GetPropertyList2 function for getting property list for
                public web page dues lookup
2024-08-28 JJK  Added GetHoaRec2 function for getting data for dues statement
2024-11-09 JJK  Converted functions to run as dotnet-isolated in .net8.0, 
                logger (connected to App Insights), and added configuration 
                to get environment variables for the Cosmos DB connection str
2024-11-11 JJK  Modified to check user role from function context for auth
2024-11-19 JJK  Moved DB functions into a common DB class (just like the old web)
2025-08-03 JJK  Added GetSalesList and UpdateSales functions to get sales data
                and update the sales record WelcomeSent flag
2025-08-08 JJK  Added new owner update function
2025-08-27 JJK  Added CreateDuesNoticeEmails to create communication records
                and EventGrid events (which are handled by an Azure Function)
2025-09-18 JJK  Added HandlePayment function to process payment posting
----------------------------------------------------------------------------------
2026-07-25 JJK  Removed AspNetCore (and Mvc for IActionResult) references and
                converted all functions to return HttpResponseData instead of 
                IActionResult for dotnet-isolated .net10
2026-07-28 JJK  Modified to use Newtonsoft.Json.Serialization with camelCase 
                for JSON serialization to match the previous PHP API output
                (and have the first letter of the JSON property names be lower case)
2026-08-03 JJK  Modified the AuthorizationCheck class to use JWT token from 
                Authorization header instead of x-ms-client-principal header 
                (as part of migrating Azure Function to .NET 10 isolated worker model).  
                Authorization check is now done by validating the JWT token 
                and checking for the required role in the "roles" claim 
                (on the Azure Entra ID).  The API Function is a registered 
                application in Azure Entra ID and the roles are defined in the 
                app registration.  
                The client application must request an access token for the 
                API Function and include it in the Authorization header of the 
                request (not SWA Easy Auth, but a real access token from Azure Entra ID).
2026-08-26 JJK  Modified the SendDuesNoticeEmails to re-add a check for TEST
                email sent for a particular Parcel Id
================================================================================*/
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.WebUtilities;  // Needed for MultipartReader
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using PaypalServerSdk.Standard;
using PaypalServerSdk.Standard.Controllers;
using PaypalServerSdk.Standard.Http.Response;
using PaypalServerSdk.Standard.Models;

using grhaWebFunctions.Model;

namespace grhaWebFunctions
{
    public class WebApi
    {
        private readonly ILogger<WebApi> log;
        private readonly Microsoft.Extensions.Configuration.IConfiguration config;

        private readonly AuthorizationCheck authCheck;
        private readonly string userAdminRole;
        private readonly CommonUtil util;
        private readonly HoaDbCommon hoaDbCommon;
        private readonly PaypalServerSdkClient paypalClient;

        public WebApi(ILogger<WebApi> logger, Microsoft.Extensions.Configuration.IConfiguration configuration, PaypalServerSdkClient inPaypalClient)
        {
            log = logger;
            config = configuration;
            authCheck = new AuthorizationCheck(log);
            userAdminRole = "hoadbadmin";   // add to config ???
            util = new CommonUtil(log);
            paypalClient = inPaypalClient;
            hoaDbCommon = new HoaDbCommon(log, config);
        }


        [Function("GetPropertyList")]
        public async Task<HttpResponseData> GetPropertyList(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            List<HoaProperty> hoaPropertyList = new List<HoaProperty>();

            try
            {
                string userName = "";
                if (!authCheck.UserAuthorizedForRole(req, userAdminRole, out userName))
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.Unauthorized, "Unauthorized call - User does not have the correct Admin role");
                }

                //log.LogInformation($">>> User is authorized - userName: {userName}");

                // Get the content string from the HTTP request body
                string searchStr = await new StreamReader(req.Body).ReadToEndAsync();
                hoaPropertyList = await hoaDbCommon.GetPropertyList(searchStr);
            }
            catch (Exception ex)
            {
                log.LogError($"Exception, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, $"Exception, message = {ex.Message}");
            }

            return await util.CreateJsonResponse(req, HttpStatusCode.OK, hoaPropertyList);
        }


        //==============================================================================================================
        // Main details lookup service to get data from all the containers for a specific property and populate
        // the HoaRec object.  It also calculates the total Dues due with interest, and gets emails and sales info
        //==============================================================================================================
        [Function("GetHoaRec")]
        public async Task<HttpResponseData> GetHoaRec(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            HoaRec hoaRec = new HoaRec();

            try
            {
                string userName = "";
                if (!authCheck.UserAuthorizedForRole(req, userAdminRole, out userName))
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.Unauthorized, "Unauthorized call - User does not have the correct Admin role");
                }

                //log.LogInformation(">>> User is authorized ");

                // Get the content string from the HTTP request body
                string content = await new StreamReader(req.Body).ReadToEndAsync();
                // Deserialize the JSON string into a generic JSON object
                JObject jObject = JObject.Parse(content);

                // Construct the query from the query parameters
                string parcelId = "";
                string ownerId = "";
                string fy = "";
                string saleDate = "";

                JToken? jToken;
                if (jObject.TryGetValue("parcelId", out jToken))
                {
                    parcelId = jToken.ToString();
                    if (parcelId.Equals(""))
                    {
                        return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "GetHoaRec failed because parcelId was blank");
                    }
                }
                else
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "GetHoaRec failed because parcelId was NOT FOUND");
                }
                if (jObject.TryGetValue("ownerId", out jToken))
                {
                    ownerId = jToken.ToString();
                }
                if (jObject.TryGetValue("fy", out jToken))
                {
                    fy = jToken.ToString();
                }
                if (jObject.TryGetValue("saleDate", out jToken))
                {
                    saleDate = jToken.ToString();
                }

                hoaRec = await hoaDbCommon.GetHoaRecDB(parcelId, ownerId, fy, saleDate);
            }
            catch (Exception ex)
            {
                log.LogError($"Exception, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, $"Exception, message = {ex.Message}");
            }

            return await util.CreateJsonResponse(req, HttpStatusCode.OK, hoaRec);
        }


        //==============================================================================================================
        // Function to return a list of full HoaRec objects with filtering options (for Reports and Mailing Lists)
        //==============================================================================================================
        [Function("GetHoaRecList")]
        public async Task<HttpResponseData> GetHoaRecList(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            List<HoaRec> hoaRecList = new List<HoaRec>();
            bool duesOwed = false;
            bool skipEmail = false;
            //bool salesWelcome = false;
            bool currYearPaid = false;
            bool currYearUnpaid = false;
            bool testEmail = false;

            try
            {
                string userName = "";
                if (!authCheck.UserAuthorizedForRole(req, userAdminRole, out userName))
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.Unauthorized, "Unauthorized call - User does not have the correct Admin role");
                }

                //log.LogInformation(">>> User is authorized ");

                // Get the content string from the HTTP request body
                string content = await new StreamReader(req.Body).ReadToEndAsync();
                // Deserialize the JSON string into a generic JSON object
                JObject jObject = JObject.Parse(content);

                // Construct the query from the query parameters
                string reportName = "";
                //string mailingListName = "";
                //bool logDuesLetterSend = false;
                //bool logWelcomeLetters = false;

                JToken? jToken;
                if (jObject.TryGetValue("reportName", out jToken))
                {
                    reportName = jToken.ToString();
                    if (reportName.Equals(""))
                    {
                        return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "GetHoaRecList failed because reportName was blank");
                    }
                }
                else
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "GetHoaRecList failed because reportName was NOT FOUND");
                }

                /*
                if (jObject.TryGetValue("mailingListName", out jToken))
                {
                    mailingListName = jToken.ToString();
                }
                if (jObject.TryGetValue("logDuesLetterSend", out jToken))
                {
                    logDuesLetterSend = jToken.Type == JTokenType.Boolean ? jToken.Value<bool>() : false;
                }
                if (jObject.TryGetValue("logWelcomeLetters", out jToken))
                {
                    logWelcomeLetters = jToken.Type == JTokenType.Boolean ? jToken.Value<bool>() : false;
                }

                if (reportName.Equals("PaidDuesReport"))
                {
                    currYearPaid = true;
                }
                if (reportName.Equals("UnpaidDuesReport"))
                {
                    currYearUnpaid = true;
                }
                if (mailingListName.Equals("WelcomeLetters"))
                {
                    salesWelcome = true;
                }
                */
                if (reportName.StartsWith("Duesletter") || reportName.Equals("UnpaidDuesRankingReport"))
                {
                    duesOwed = true;
                }
                if (reportName.StartsWith("Duesletter1"))
                {
                    skipEmail = true;
                }

                hoaRecList = await hoaDbCommon.GetHoaRecListDB(duesOwed, skipEmail, currYearPaid, currYearUnpaid, testEmail);
            }
            catch (Exception ex)
            {
                log.LogError($"Exception, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, $"Exception, message = {ex.Message}");
            }

            return await util.CreateJsonResponse(req, HttpStatusCode.OK, hoaRecList);
        }


        [Function("GetSalesList")]
        public async Task<HttpResponseData> GetSalesList(
                [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            List<hoa_sales> hoaSalesList = new List<hoa_sales>();

            try
            {
                string userName = "";
                if (!authCheck.UserAuthorizedForRole(req, userAdminRole, out userName))
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.Unauthorized, "Unauthorized call - User does not have the correct Admin role");
                }

                //log.LogInformation(">>> User is authorized ");

                // Get the content string from the HTTP request body
                /*
                string content = await new StreamReader(req.Body).ReadToEndAsync();
                // Deserialize the JSON string into a generic JSON object
                JObject jObject = JObject.Parse(content);

                // Construct the query from the query parameters
                string reportName = "";

                JToken? jToken;
                if (jObject.TryGetValue("reportName", out jToken))
                {
                    reportName = jToken.ToString().Trim();
                    if (reportName.Equals(""))
                    {
                        return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "Query failed because reportName was blank");
                    }
                } else {
                    return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "Query failed because reportName was NOT FOUND");
                }
                */
                hoaSalesList = await hoaDbCommon.GetSalesListDb();
            }
            catch (Exception ex)
            {
                log.LogError($"Exception, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, $"Exception, message = {ex.Message}");
            }

            return await util.CreateJsonResponse(req, HttpStatusCode.OK, hoaSalesList);
        }


        [Function("GetConfigList")]
        public async Task<HttpResponseData> GetConfigList(
                [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            List<hoa_config> hoaConfigList = new List<hoa_config>();

            try
            {
                string userName = "";
                if (!authCheck.UserAuthorizedForRole(req, userAdminRole, out userName))
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.Unauthorized, "Unauthorized call - User does not have the correct Admin role");
                }

                //log.LogInformation(">>> User is authorized ");

                hoaConfigList = await hoaDbCommon.GetConfigListDB();
            }
            catch (Exception ex)
            {
                log.LogError($"Exception, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, $"Exception, message = {ex.Message}");
            }

            return await util.CreateJsonResponse(req, HttpStatusCode.OK, hoaConfigList);
        }

        [Function("UpdateConfig")]
        public async Task<HttpResponseData> UpdateConfig(
                [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            hoa_config hoaConfig;

            try
            {
                string userName = "";
                if (!authCheck.UserAuthorizedForRole(req, userAdminRole, out userName))
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.Unauthorized, "Unauthorized call - User does not have the correct Admin role");
                }

                //log.LogInformation(">>> User is authorized ");

                // Get the content string from the HTTP request body
                string content = await new StreamReader(req.Body).ReadToEndAsync();
                // Deserialize the JSON string into a generic JSON object
                JObject jObject = JObject.Parse(content);

                // Construct the query from the query parameters
                string configName = "";
                string configDesc = "";
                string configValue = "";

                JToken? jToken;
                if (jObject.TryGetValue("configName", out jToken))
                {
                    configName = jToken.ToString().Trim();
                    if (configName.Equals(""))
                    {
                        return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "Query failed because configName was blank");
                    }
                }
                else
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "Query failed because configName was NOT FOUND");
                }

                if (jObject.TryGetValue("configDesc", out jToken))
                {
                    configDesc = jToken.ToString().Trim();
                    if (configDesc.Equals(""))
                    {
                        return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "Query failed because configDesc was blank");
                    }
                }
                else
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "Query failed because configDesc was NOT FOUND");
                }

                if (jObject.TryGetValue("configValue", out jToken))
                {
                    configValue = jToken.ToString().Trim();
                }

                hoaConfig = await hoaDbCommon.UpdateConfigDB(userName, configName, configDesc, configValue);
            }
            catch (Exception ex)
            {
                log.LogError($"Exception, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, $"Exception, message = {ex.Message}");
            }

            return await util.CreateJsonResponse(req, HttpStatusCode.OK, hoaConfig);
        }

        [Function("GetPaidDuesCountList")]
        public async Task<HttpResponseData> GetPaidDuesCountList(
                [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            List<PaidDuesCount> duesCountList = new List<PaidDuesCount>();

            try
            {
                string userName = "";
                if (!authCheck.UserAuthorizedForRole(req, userAdminRole, out userName))
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.Unauthorized, "Unauthorized call - User does not have the correct Admin role");
                }

                //log.LogInformation(">>> User is authorized ");

                duesCountList = await hoaDbCommon.GetPaidDuesCountListDb();
            }
            catch (Exception ex)
            {
                log.LogError($"Exception, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, $"Exception, message = {ex.Message}");
            }

            return await util.CreateJsonResponse(req, HttpStatusCode.OK, duesCountList);
        }


        [Function("UpdateSales")]
        public async Task<HttpResponseData> UpdateSales(
                [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            string returnMessage = "";
            try
            {
                string userName = "";
                if (!authCheck.UserAuthorizedForRole(req, userAdminRole, out userName))
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.Unauthorized, "Unauthorized call - User does not have the correct Admin role");
                }

                // Get the content string from the HTTP request body
                string content = await new StreamReader(req.Body).ReadToEndAsync();
                // Deserialize the JSON string into a generic JSON object
                JObject jObject = JObject.Parse(content);

                // Construct the query from the query parameters
                string parid = "";
                string saledt = "";
                string processedFlag = "";
                string welcomeSent = "";

                JToken? jToken;
                if (jObject.TryGetValue("parid", out jToken))
                {
                    parid = jToken.ToString().Trim();
                    if (parid.Equals(""))
                    {
                        return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "Query failed because parid was blank");
                    }
                }
                else
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "Query failed because parid was NOT FOUND");
                }

                if (jObject.TryGetValue("saledt", out jToken))
                {
                    saledt = jToken.ToString().Trim();
                    if (saledt.Equals(""))
                    {
                        return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "Query failed because saledt was blank");
                    }
                }
                else
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "Query failed because saledt was NOT FOUND");
                }

                if (jObject.TryGetValue("processedFlag", out jToken))
                {
                    processedFlag = jToken.ToString().Trim();
                }

                if (jObject.TryGetValue("welcomeSent", out jToken))
                {
                    welcomeSent = jToken.ToString().Trim();
                }

                await hoaDbCommon.UpdateSalesDB(userName, parid, saledt, processedFlag, welcomeSent);
                returnMessage = "Sales record was updated";
            }
            catch (Exception ex)
            {
                log.LogError($"Exception in UpdateSales, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "Error in update of Sales record - check log");
            }

            return await util.CreateJsonResponse(req, HttpStatusCode.OK, returnMessage);
        }


        [Function("GetCommunications")]
        public async Task<HttpResponseData> GetCommunications(
                [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            List<hoa_communications> hoaCommunicationsList = new List<hoa_communications>();

            try
            {
                string userName = "";
                if (!authCheck.UserAuthorizedForRole(req, userAdminRole, out userName))
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.Unauthorized, "Unauthorized call - User does not have the correct Admin role");
                }

                //log.LogInformation(">>> User is authorized ");

                // Get the content string from the HTTP request body
                string content = await new StreamReader(req.Body).ReadToEndAsync();
                // Deserialize the JSON string into a generic JSON object
                JObject jObject = JObject.Parse(content);

                // Construct the query from the query parameters
                string parcelId = "";
                string sentStatus = "";

                JToken? jToken;
                if (jObject.TryGetValue("parcelId", out jToken))
                {
                    parcelId = jToken.ToString();
                    if (parcelId.Equals(""))
                    {
                        return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "GetHoaRec failed because parcelId was blank");
                    }
                }
                else
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "GetHoaRec failed because parcelId was NOT FOUND");
                }

                // Set the sentStatus to filter on if it was passed in
                if (jObject.TryGetValue("sentStatus", out jToken))
                {
                    sentStatus = jToken.ToString();
                }

                hoaCommunicationsList = await hoaDbCommon.GetCommunicationsDB(parcelId, sentStatus);
            }
            catch (Exception ex)
            {
                log.LogError($"Exception, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, $"Exception, message = {ex.Message}");
            }

            return await util.CreateJsonResponse(req, HttpStatusCode.OK, hoaCommunicationsList);
        }

        [Function("CreateDuesNoticeEmails")]
        public async Task<HttpResponseData> CreateDuesNoticeEmails(
                [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            List<hoa_communications> hoaCommunicationsList = new List<hoa_communications>();
            string returnMessage = "";

            try
            {
                string userName = "";
                if (!authCheck.UserAuthorizedForRole(req, userAdminRole, out userName))
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.Unauthorized, "Unauthorized call - User does not have the correct Admin role");
                }

                //log.LogInformation(">>> User is authorized ");

                int cnt = await hoaDbCommon.CreateDuesEmailsListDB(userName);
                returnMessage = $"Dues Notice Emails list created, count = {cnt}";
            }
            catch (Exception ex)
            {
                log.LogError($"Exception, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, $"Exception, message = {ex.Message}");
            }

            return await util.CreateJsonResponse(req, HttpStatusCode.OK, returnMessage);
        }

        [Function("SendDuesNoticeEmails")]
        public async Task<HttpResponseData> SendDuesNoticeEmails(
                [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            List<hoa_communications> hoaCommunicationsList = new List<hoa_communications>();
            string returnMessage = "";

            try
            {
                string userName = "";
                if (!authCheck.UserAuthorizedForRole(req, userAdminRole, out userName))
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.Unauthorized, "Unauthorized call - User does not have the correct Admin role");
                }

                string content = await new StreamReader(req.Body).ReadToEndAsync();
                JObject jObject = JObject.Parse(content);
                string testParcelId = "";
                JToken? jToken;
                if (jObject.TryGetValue("testParcelId", out jToken))
                {
                    testParcelId = jToken.ToString().Trim();
                }

                int cnt = await hoaDbCommon.SendDuesNoticeEmailsDB(userName, testParcelId);
                returnMessage = $"Dues Notice Emails queued for send, count = {cnt}";
            }
            catch (Exception ex)
            {
                log.LogError($"Exception, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, $"Exception, message = {ex.Message}");
            }

            return await util.CreateJsonResponse(req, HttpStatusCode.OK, returnMessage);
        }

        /*
        using Newtonsoft.Json.Linq;
        string json = "{\"Name\":\"John\",\"Age\":30}";
        JObject obj = JObject.Parse(json);
        Console.WriteLine($"Name: {obj["Name"]}, Age: {obj["Age"]}"); // Use index-based access
        */

        [Function("UpdateProperty")]
        public async Task<HttpResponseData> UpdateProperty(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            string returnMessage = "";
            try
            {
                string userName = "";
                if (!authCheck.UserAuthorizedForRole(req, userAdminRole, out userName))
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.Unauthorized, "Unauthorized call - User does not have the correct Admin role");
                }
                //log.LogInformation($">>> User is authorized - userName: {userName}");

                // Get content from the Request BODY
                var boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(req.Headers.GetValues("Content-Type").FirstOrDefault()).Boundary).Value;
                var reader = new MultipartReader(boundary, req.Body);
                var section = await reader.ReadNextSectionAsync();

                var formFields = new Dictionary<string, string>();
                var files = new List<(string fieldName, string fileName, byte[] content)>();

                while (section != null)
                {
                    var contentDisposition = section.GetContentDispositionHeader();
                    if (contentDisposition != null)
                    {
                        if (contentDisposition.IsFileDisposition())
                        {
                            using var memoryStream = new MemoryStream();
                            await section.Body.CopyToAsync(memoryStream);
                            files.Add((contentDisposition.Name.Value, contentDisposition.FileName.Value, memoryStream.ToArray()));
                        }
                        else if (contentDisposition.IsFormDisposition())
                        {
                            using var streamReader = new StreamReader(section.Body);
                            formFields[contentDisposition.Name.Value] = await streamReader.ReadToEndAsync();
                        }
                    }

                    section = await reader.ReadNextSectionAsync();
                }

                /*
                foreach (var field in formFields)
                {
                    log.LogWarning($"Field {field.Key}: {field.Value}");
                }
                */
                await hoaDbCommon.UpdatePropertyDB(userName, formFields);

                returnMessage = "Property was updated";
            }
            catch (Exception ex)
            {
                log.LogError($"Exception in UpdateProperty, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "Error in update of Property - check log");
            }

            return await util.CreateJsonResponse(req, HttpStatusCode.OK, returnMessage);
        }


        [Function("UpdateOwner")]
        public async Task<HttpResponseData> UpdateOwner(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            hoa_owners ownerRec = new hoa_owners();
            //string returnMessage = "";
            try
            {
                string userName = "";
                if (!authCheck.UserAuthorizedForRole(req, userAdminRole, out userName))
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.Unauthorized, "Unauthorized call - User does not have the correct Admin role");
                }
                //log.LogInformation($">>> User is authorized - userName: {userName}");

                // Get content from the Request BODY
                var boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(req.Headers.GetValues("Content-Type").FirstOrDefault()).Boundary).Value;
                var reader = new MultipartReader(boundary, req.Body);
                var section = await reader.ReadNextSectionAsync();

                var formFields = new Dictionary<string, string>();
                var files = new List<(string fieldName, string fileName, byte[] content)>();

                while (section != null)
                {
                    var contentDisposition = section.GetContentDispositionHeader();
                    if (contentDisposition != null)
                    {
                        if (contentDisposition.IsFileDisposition())
                        {
                            using var memoryStream = new MemoryStream();
                            await section.Body.CopyToAsync(memoryStream);
                            files.Add((contentDisposition.Name.Value, contentDisposition.FileName.Value, memoryStream.ToArray()));
                        }
                        else if (contentDisposition.IsFormDisposition())
                        {
                            using var streamReader = new StreamReader(section.Body);
                            formFields[contentDisposition.Name.Value] = await streamReader.ReadToEndAsync();
                        }
                    }

                    section = await reader.ReadNextSectionAsync();
                }

                string ownerId = formFields["OwnerID"].Trim();
                if (ownerId.Equals("*** CREATE NEW OWNER (on Save) ***"))
                {
                    ownerRec = await hoaDbCommon.NewOwnerDB(userName, formFields);
                }
                else
                {
                    ownerRec = await hoaDbCommon.UpdateOwnerDB(userName, formFields);
                }
            }
            catch (Exception ex)
            {
                log.LogError($"Exception in UpdateProperty, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "Error in update of Owner - check log");
            }

            return await util.CreateJsonResponse(req, HttpStatusCode.OK, ownerRec);
        }

        [Function("UpdateAssessment")]
        public async Task<HttpResponseData> UpdateAssessment(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            hoa_assessments assessmentRec = new hoa_assessments();
            //string returnMessage = "";
            try
            {
                string userName = "";
                if (!authCheck.UserAuthorizedForRole(req, userAdminRole, out userName))
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.Unauthorized, "Unauthorized call - User does not have the correct Admin role");
                }
                //log.LogInformation($">>> User is authorized - userName: {userName}");

                // Get content from the Request BODY
                var boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(req.Headers.GetValues("Content-Type").FirstOrDefault()).Boundary).Value;
                var reader = new MultipartReader(boundary, req.Body);
                var section = await reader.ReadNextSectionAsync();

                var formFields = new Dictionary<string, string>();
                var files = new List<(string fieldName, string fileName, byte[] content)>();

                while (section != null)
                {
                    var contentDisposition = section.GetContentDispositionHeader();
                    if (contentDisposition != null)
                    {
                        if (contentDisposition.IsFileDisposition())
                        {
                            using var memoryStream = new MemoryStream();
                            await section.Body.CopyToAsync(memoryStream);
                            files.Add((contentDisposition.Name.Value, contentDisposition.FileName.Value, memoryStream.ToArray()));
                        }
                        else if (contentDisposition.IsFormDisposition())
                        {
                            using var streamReader = new StreamReader(section.Body);
                            formFields[contentDisposition.Name.Value] = await streamReader.ReadToEndAsync();
                        }
                    }

                    section = await reader.ReadNextSectionAsync();
                }

                assessmentRec = await hoaDbCommon.UpdateAssessmentDB(userName, formFields);
            }
            catch (Exception ex)
            {
                log.LogError($"Exception in UpdateProperty, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "Error in update of Property - check log");
            }

            return await util.CreateJsonResponse(req, HttpStatusCode.OK, assessmentRec);
        }


        // Bulk add assessments for all properties for a given FiscalYear and DuesAmt
        [Function("AddAssessments")]
        public async Task<HttpResponseData> AddAssessments(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            string resultMessage = "";
            try
            {
                string userName = "";
                if (!authCheck.UserAuthorizedForRole(req, userAdminRole, out userName))
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.Unauthorized, "Unauthorized call - User does not have the correct Admin role");
                }

                string content = await new StreamReader(req.Body).ReadToEndAsync();
                JObject jObject = JObject.Parse(content);

                string fiscalYear = "";
                string duesAmt = "";
                JToken? jToken;
                if (jObject.TryGetValue("FiscalYear", out jToken))
                {
                    fiscalYear = jToken.ToString().Trim();
                }
                if (jObject.TryGetValue("DuesAmt", out jToken))
                {
                    duesAmt = jToken.ToString().Trim();
                }
                if (string.IsNullOrEmpty(fiscalYear) || string.IsNullOrEmpty(duesAmt))
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "FiscalYear and DuesAmt are required");
                }

                int fy = int.Parse(fiscalYear);
                decimal amt = decimal.Parse(duesAmt);
                int count = await hoaDbCommon.AddAssessmentsBulk(userName, fy, amt);
                resultMessage = $"Added {count} assessments for Fiscal Year {fy} with DuesAmt {amt:C}";
            }
            catch (Exception ex)
            {
                log.LogError($"Exception in AddAssessments, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, $"Error in AddAssessments - {ex.Message}");
            }
            return await util.CreateJsonResponse(req, HttpStatusCode.OK, resultMessage);
        }

        // Sales Upload endpoint
        [Function("SalesUpload")]
        public async Task<HttpResponseData> SalesUpload(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            string resultMessage = "";
            try
            {
                string userName = "";
                if (!authCheck.UserAuthorizedForRole(req, userAdminRole, out userName))
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.Unauthorized, "Unauthorized call - User does not have the correct Admin role");
                }

                var boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(req.Headers.GetValues("Content-Type").FirstOrDefault()).Boundary).Value;
                var reader = new MultipartReader(boundary, req.Body);
                var section = await reader.ReadNextSectionAsync();

                Stream fileStream = null;
                string fileName = null;
                var formFields = new Dictionary<string, string>();

                while (section != null)
                {
                    var contentDisposition = section.GetContentDispositionHeader();
                    if (contentDisposition != null)
                    {
                        if (contentDisposition.IsFileDisposition())
                        {
                            fileName = contentDisposition.FileName.Value;
                            fileStream = new MemoryStream();
                            await section.Body.CopyToAsync(fileStream);
                            fileStream.Position = 0;
                        }
                        else if (contentDisposition.IsFormDisposition())
                        {
                            using var streamReader = new StreamReader(section.Body);
                            formFields[contentDisposition.Name.Value] = await streamReader.ReadToEndAsync();
                        }
                    }
                    section = await reader.ReadNextSectionAsync();
                }

                if (fileStream == null)
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, $"No file uploaded.");
                }

                string result = await hoaDbCommon.ProcessSalesUploadDB(userName, fileStream, fileName);
                resultMessage = result;
            }
            catch (Exception ex)
            {
                log.LogError($"Exception in SalesUpload, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, $"Error in SalesUpload - {ex.Message}");
            }
            return await util.CreateJsonResponse(req, HttpStatusCode.OK, resultMessage);
        }

        //-------------------------------------------------------------------------------------------------------------
        // Handle Payment endpoint - called from client after PayPal payment is approved
        // This will verify the payment with PayPal and then call hoaDbCommon to record the payment in hoa_payments
        // 2025-09-21 JJK - Comments from original PHP version
        /*==============================================================================
        * (C) Copyright 2016,2020,2021 John J Kauflin, All rights reserved.
        *----------------------------------------------------------------------------
        * DESCRIPTION: Handle notification from payment merchant - insert a payment
        * 				transaction record, update paid flags, and send an email to
        * 				the payer.  This service is called from the client after
        *              it has created the order and gotten approval from Paypal
        *----------------------------------------------------------------------------
        * Modification History
        * 2016-04-26 JJK 	Initial version starting with paypal_ipn.php
        * 2016-05-02 JJK   Modified to update assessment to paid
        * 2016-05-11 JJK	Modified to insert payment transaction record
        * 2016-05-14 JJK   Moved updates to updHoaPayment
        * 2016-08-26 JJK   Changed from sandbox to live production
        * 2020-08-05 JJK   Modified to include hoaDbCommon and call function there
        *                  to do the update the HOA database
        * 2020-09-08 JJK   Added email to notify of problems (INVALID) for the 
        *                  Access Denied issue
        * 2020-09-19 JJK   Corrected email issue by including autoload.php
        * 2020-12-31 JJK   New version (not using IPN), using PHP SDK for Paypal API
        * 2020-01-03 JJK   Modified to go Live with production settings
        * 2021-02-13 JJK   Modified CustomId to be FY,ParcelId
        * 2021-09-04 JJK   Added logging to check email function
        *============================================================================*/
        [Function("HandlePayment")]
        public async Task<HttpResponseData> HandlePayment(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            string resultMessage;
            try
            {
                string orderID = await new StreamReader(req.Body).ReadToEndAsync();
                if (string.IsNullOrEmpty(orderID))
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "HandlePayment failed because orderID was blank");
                }
                var ordersController = paypalClient.OrdersController;
                CaptureOrderInput ordersCaptureInput = new CaptureOrderInput { Id = orderID };
                ApiResponse<Order> result = await ordersController.CaptureOrderAsync(ordersCaptureInput);

                // Error out if the order is NOT completed/approved
                if (result.Data.Status != OrderStatus.Completed ||
                    result.Data.PurchaseUnits[0].Payments.Captures[0].Status != CaptureStatus.Completed)
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "Payment not completed or invalid order");
                }

                // Get the values from the response
                string parcelId = result.Data.PurchaseUnits[0].ReferenceId;
                string[] customId = result.Data.PurchaseUnits[0].Payments.Captures[0].CustomId.Split(',');
                string fiscalYear = customId[0];
                string parcelId2 = customId[1];
                if (!parcelId.Equals(parcelId2))
                {
                    return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "ParcelId in ReferenceId does not match CustomId");
                }
                string transactionId = result.Data.PurchaseUnits[0].Payments.Captures[0].Id;
                decimal totalAmount = decimal.Parse(result.Data.PurchaseUnits[0].Payments.Captures[0].Amount.MValue);
                decimal paymentAmt = decimal.Parse(result.Data.PurchaseUnits[0].Payments.Captures[0].SellerReceivableBreakdown.GrossAmount.MValue);
                decimal paymentFee = decimal.Parse(result.Data.PurchaseUnits[0].Payments.Captures[0].SellerReceivableBreakdown.PaypalFee.MValue);
                string paymentDate = result.Data.PurchaseUnits[0].Payments.Captures[0].CreateTime;
                string payerEmail = result.Data.Payer.EmailAddress;
                string payerName = result.Data.Payer.Name.GivenName + ' ' + result.Data.Payer.Name.Surname;

                // Call HoaDbCommon to update hoa_payments
                await hoaDbCommon.RecordPayment(parcelId, fiscalYear, transactionId, totalAmount, paymentAmt, paymentFee, paymentDate, payerEmail, payerName);

                resultMessage = $"Thank you, {result.Data.Payer.Name.GivenName}.  {fiscalYear} Dues for parcel {parcelId} have been marked as PAID, and you will receive a confirmation email";
            }
            catch (Exception ex)
            {
                log.LogError($"Exception in HandlePayment, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, $"Error in HandlePayment - {ex.Message}");
            }
            return await util.CreateJsonResponse(req, HttpStatusCode.OK, resultMessage);
        }


    } // public static class WebApi
}


