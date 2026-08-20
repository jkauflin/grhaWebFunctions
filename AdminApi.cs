/*==============================================================================
(C) Copyright 2025 John J Kauflin, All rights reserved.
--------------------------------------------------------------------------------
DESCRIPTION:  Azure API Functions for the Static Web App (SWA) - to support
                the Admin operations
--------------------------------------------------------------------------------
Modification History
2025-04-12 JJK  Initial version
2025-04-13 JJK  Completed the Board of Trustees maintenance functions
2025-04-22 JJK  Re-thinking error handling for api calls from javascript fetch
2025-05-07 JJK  Adding function for handling file uploads
2025-10-04 JJK  Adding WebsiteMessage to Trustee
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

using grhaWebFunctions.Model;

namespace grhaWebFunctions
{
    public class AdminApi
    {
        private readonly ILogger<AdminApi> log;
        private readonly IConfiguration config;

        private readonly AuthorizationCheck authCheck;
        private readonly string userAdminRole;
        private readonly CommonUtil util;
        private readonly HoaDbCommon hoaDbCommon;

        public AdminApi(ILogger<AdminApi> logger, IConfiguration configuration)
        {
            log = logger;
            config = configuration;
            authCheck = new AuthorizationCheck(log);
            userAdminRole = "grhaadmin";   // add to config ???
            util = new CommonUtil(log);
            hoaDbCommon = new HoaDbCommon(log,config);
        }

        [Function("GetTrustee")]
        public async Task<HttpResponseData> GetTrustee(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            var trustee = new Trustee{id = "01"};
            try {
                string userName = "";
                if (!authCheck.UserAuthorizedForRole(req,userAdminRole,out userName)) {
                    //log.LogWarning($">>> User is NOT authorized - userName: {userName}");
                    return await util.CreateErrorResponse(req, HttpStatusCode.Unauthorized, "Unauthorized call - User does not have the correct Admin role");
                }
                //log.LogInformation($">>> User is authorized - userName: {userName}");

                // Get the content string from the HTTP request body
                string trusteeId = await new StreamReader(req.Body).ReadToEndAsync();

                trustee = await hoaDbCommon.GetTrusteeById(trusteeId);
                //log.LogWarning($"trustee.Name: {trustee.Name}");
            }
            catch (Exception ex) {
                log.LogError($"Exception in DB get of Board of Trustees, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "Error in get of Trustee data - check log");
            }
            
            return await util.CreateJsonResponse(req, HttpStatusCode.OK, trustee);
        }

        [Function("UpdateTrustee")]
        public async Task<HttpResponseData> UpdateTrustee(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            //var trustee = new Trustee{id = "01"};
            try {
                string userName = "";
                if (!authCheck.UserAuthorizedForRole(req,userAdminRole,out userName)) {
                    return await util.CreateErrorResponse(req, HttpStatusCode.Unauthorized, "Unauthorized call - User does not have the correct Admin role");
                }
                //log.LogInformation($">>> User is authorized - userName: {userName}");

                // Get the content string from the HTTP request body
                string content = await new StreamReader(req.Body).ReadToEndAsync();
                // Deserialize the JSON string into a generic JSON object
                JObject jObject = JObject.Parse(content);
                var trustee = jObject.ToObject<Trustee>();
                if (trustee == null) {
                    return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "Update failed - object was NULL");
                } 
                await hoaDbCommon.UpdTrustee(trustee);
            }
            catch (Exception ex) {
                log.LogError($"Exception in DB update to Board of Trustees, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "Error in update of Trustee data - check log");
            }
            
            return await util.CreateJsonResponse(req, HttpStatusCode.OK, "Update was successful");
        }


        [Function("UploadDoc")]
        public async Task<HttpResponseData> UploadDoc(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            string returnMessage = "No files uploaded";
            try {
                string userName = "";
                if (!authCheck.UserAuthorizedForRole(req,userAdminRole,out userName)) {
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

                // Example usage
                /*
                foreach (var field in formFields)
                {
                    log.LogInformation($"Field {field.Key}: {field.Value}");
                }

                foreach (var file in files)
                {
                    log.LogInformation($"File {file.fileName} from field {file.fieldName}, Size: {file.content.Length} bytes");

                    //byte[] fileBytes = ...; // Your byte array
                    //string filePath = "/Projects/"+file.fileName; // Specify the file path
                    //File.WriteAllBytes(filePath, file.content);
                }
                */

                int mediaTypeId = 4;
                string docCategory = formFields["DocCategory"];
                string docMonth = formFields["DocMonth"];
                string dateString = docMonth+"-01";
                DateTime mediaDateTime = DateTime.Parse(dateString);
                string docName;
                string docTitle;
                if (files[0].fieldName.Equals("DocFile")) {
                    docName = files[0].fileName;
                    docTitle = files[0].fileName;
                    if (docCategory.Equals("Quail Call newsletters")) {
                        docName = docMonth+"-GRHA-QuailCall.pdf";
                        docTitle = docMonth+"-GRHA-QuailCall";
                    }

                    await hoaDbCommon.UploadFileToDatabase(mediaTypeId, docName, mediaDateTime, files[0].content, docCategory, docTitle);
                    returnMessage = "Upload was successful";
                } 
            }
            catch (Exception ex) {
                log.LogError($"Exception in Doc File upload, message: {ex.Message} {ex.StackTrace}");
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "Error Doc File upload - check log");
            }
            
            return await util.CreateJsonResponse(req, HttpStatusCode.OK, returnMessage);
        }


        [Function("UploadPhotos")]
        public async Task<HttpResponseData> UploadPhotos(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestData req)
        {
            string returnMessage = "No files uploaded";
            try {
                string userName = "";
                if (!authCheck.UserAuthorizedForRole(req,userAdminRole,out userName)) {
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

                int mediaTypeId = 1;
                string eventCategory = formFields["EventCategory"];
                string eventMonth = formFields["EventMonth"];
                string dateString = eventMonth+"-01";
                DateTime mediaDateTime = DateTime.Parse(dateString);
                string newFileName;
                string title = "";
                int cnt = 0;
                foreach (var file in files)
                {
                    cnt++;
                    //log.LogWarning($"File {file.fileName} from field {file.fieldName}, Size: {file.content.Length} bytes");
                    newFileName = mediaDateTime.ToString("yyyy-MM ") + file.fileName;

                    if (cnt == 1) {
                        title = formFields["PhotoTitle1"].Trim();
                    } else if (cnt == 2) {
                        title = formFields["PhotoTitle2"].Trim();
                    } else if (cnt == 3) {
                        title = formFields["PhotoTitle3"].Trim();
                    }

                    await hoaDbCommon.UploadFileToDatabase(mediaTypeId, newFileName, mediaDateTime, files[cnt-1].content, eventCategory, title);
                    returnMessage = "Upload was successful";
                }
            }
            catch (Exception ex) {
                string errorMsg = "- check log";
                log.LogError($"Exception in Photos upload, message: {ex.Message} {ex.StackTrace}");
                if (ex.Message != null) {
                    if (ex.Message.Contains("maximum request body size")) {
                        errorMsg = "- Uploaded files exceed maximum allowed size of 50 MB";
                    } else if (ex.Message.Contains("The format of the file name")) {
                        errorMsg = "- Invalid characters in file name";
                    } else if (ex.Message.Contains("Image cannot be loaded")) {
                        errorMsg = "- JPEG file is corrupt or invalid format";
                    }
                }
                return await util.CreateErrorResponse(req, HttpStatusCode.BadRequest, "Error in upload of Photos " + errorMsg);

            }
            
            return await util.CreateJsonResponse(req, HttpStatusCode.OK, returnMessage);
        }

    } // public static class AdminApi
} // namespace grhaWebFunctions
