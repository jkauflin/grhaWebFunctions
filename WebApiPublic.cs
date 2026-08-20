/*==============================================================================
(C) Copyright 2024 John J Kauflin, All rights reserved.
--------------------------------------------------------------------------------
DESCRIPTION:  Azure API Functions for the Static Web App (SWA) - this should
              replace all of the old PHP code, and be the "backend" services
              for the web apps
--------------------------------------------------------------------------------
Modification History
2024-11-18 JJK  Initial version (moving GetPropertyList2 and GetHoaRec2 into
                this public api class to get data for the public facing
                website
----------------------------------------------------------------------------------
2026-07-25 JJK  Removed AspNetCore (and Mvc for IActionResult) references and
                converted all functions to return HttpResponseData instead of 
                IActionResult for dotnet-isolated .net10
2026-07-28 JJK  Modified to use Newtonsoft.Json.Serialization with camelCase 
                for JSON serialization to match the previous PHP API output
                (and have the first letter of the JSON property names be lower case)
================================================================================*/
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

using grhaWebFunctions.Model;

namespace grhaWebFunctions
{
    public class WebApiPublic
    {
        private readonly ILogger<WebApi> log;
        private readonly IConfiguration config;
        private readonly CommonUtil util;
        private readonly HoaDbCommon hoaDbCommon;

        public WebApiPublic(ILogger<WebApi> logger, IConfiguration configuration)
        {
            log = logger;
            config = configuration;
            util = new CommonUtil(log);
            hoaDbCommon = new HoaDbCommon(log, config);
        }

        // Public access for website Dues page
        [Function("GetPropertyList2")]
        public async Task<HttpResponseData> GetPropertyList2(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            List<HoaProperty2> hoaProperty2List = new List<HoaProperty2>();
            try
            {
                // Get the content string from the HTTP request body
                string searchAddress = await new StreamReader(req.Body).ReadToEndAsync();
                hoaProperty2List = await hoaDbCommon.GetPropertyList2(searchAddress);
            }
            catch (Exception ex)
            {
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, $"Exception, message = {ex.Message}");
            }

            return await util.CreateJsonResponse(req, HttpStatusCode.OK, hoaProperty2List);
        }


        //==============================================================================================================
        // Main details lookup service to get data from all the containers for a specific property and populate
        // the HoaRec object.  It also calculates the total Dues due with interest, and gets emails and sales info
        //==============================================================================================================
        [Function("GetHoaRec2")]
        public async Task<HttpResponseData> GetHoaRec2(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            HoaRec2 hoaRec2 = new HoaRec2();
            try
            {
                // Get the content string from the HTTP request body
                string parcelId = await new StreamReader(req.Body).ReadToEndAsync();
                hoaRec2 = await hoaDbCommon.GetHoaRec2DB(parcelId);
            }
            catch (Exception ex)
            {
                log.LogError($"Exception, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, $"Exception, message = {ex.Message}");
            }

            return await util.CreateJsonResponse(req, HttpStatusCode.OK, hoaRec2);
        }

        // Public access for website Dues page
        [Function("GetTrusteeList")]
        public async Task<HttpResponseData> GetTrusteeList(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)] HttpRequestData req)
        {
            List<Trustee> trusteeList = new List<Trustee>();

            try
            {
                trusteeList = await hoaDbCommon.GetTrusteeListDB();
            }
            catch (Exception ex)
            {
                log.LogError($"Exception, message: {ex.Message} {ex.StackTrace}");
                // Just return empty list on exception (to let the caller do retry or show error)
                //return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, $"Exception, message = {ex.Message}");
            }

            return await util.CreateJsonResponse(req, HttpStatusCode.OK, trusteeList);
        }

        // Public API for media info queries
        [Function("GetMediaInfo")]
        public async Task<HttpResponseData> GetMediaInfo(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            try
            {
                string body = await new StreamReader(req.Body).ReadToEndAsync();
                // paramData is a JSON object with filter params
                var paramData = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                var mediaList = await hoaDbCommon.GetMediaInfoDB(paramData);
                return await util.CreateJsonResponse(req, HttpStatusCode.OK, mediaList);
            }
            catch (Exception ex)
            {
                log.LogError($"Exception in GetMediaInfo: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, $"Exception, message = {ex.Message}");
            }
        }

    } // public static class WebApi
} // namespace grhaWebFunctions

