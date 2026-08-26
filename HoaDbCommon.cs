/*==============================================================================
(C) Copyright 2024 John J Kauflin, All rights reserved.
--------------------------------------------------------------------------------
DESCRIPTION:  Common functions to handle getting data from the data sources.
              Centralize all data source libraries and configuration to this
              utility class.
--------------------------------------------------------------------------------
Modification History
2024-11-19 JJK  Initial versions
2025-04-12 JJK  Added functions to read and update BoardOfTrustee data source
2025-04-13 JJK  *** NEW philosophy - put the error handling (try/catch) in the
                main/calling function, and leave it out of the DB Common - DB
                Common will throw any error, and the caller can log and handle
2025-05-08 JJK  Added function to convert images and upload to 
2025-05-14 JJK  Added calc of DuesDue in the assessments record
2025-05=16 JJK  Working on DuesStatement (and PDF)
2025-05-31 JJK  Added AddPatchField and functions to update hoadb property
2025-06-27 JJK  Added Assessment update
2025-08-03 JJK  Added GetSalesList and UpdateSales functions to get sales data
                and update the sales record WelcomeSent flag
2025-08-08 JJK  Added new owner update function
2025-08-17 JJK  Added functions for reports, and corrected problem with dues
                paid counts because of duplicate assessments from the sql to
                cosmosdb migration (load program has been corrected).
                Modified the assessments update to choose new owners
2025-08-21 JJK  Added function to get and update hoa_config values
2025-09-30 JJK  Added functions to process sales upload, and for recording
                payments
2026-02-27 JJK  Modified to use ParseDate function for consistent date parsing 
                and formatting across the codebase, and updated all date 
                parsing in the code to use this function 
2026-03-17 JJK  Added calculation of online payment processing fee based on 
                total amount due, and added to the GetHoaRec functions.
                Modified RecordPayment to make all unpaid assessment to PAID
                when receiving an online payment
2026-03-20 JJK  Modified CreateDuesEmailsListDB to delete any unsent
                communications records for a parcel before creating new ones
2025-09-22 JJK  Added SendPaymentEmail function
2025-09-30 JJK  Fixed some bugs and modified to return the email Id from ACS
                *** Turned on sending to actual email address (commented out test) ***
================================================================================*/
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Cosmos;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Messaging.EventGrid;
using Azure.Communication.Email;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

using grhaWebFunctions.Model;

namespace grhaWebFunctions
{
public class HoaDbCommon
{
    private readonly ILogger log;
    private readonly IConfiguration config;
    private readonly string? apiCosmosDbConnStr;
    private readonly string? apiStorageConnStr;
    private readonly string databaseId;
    private readonly string? grhaSendEmailEventTopicEndpoint;
    private readonly string? grhaSendEmailEventTopicKey;
    private readonly string? acsEmailConnStr;  // Your ACS Email connection string from the Azure portal
    private readonly string? acsEmailSenderAddress;
    private readonly CommonUtil util;


    public HoaDbCommon(ILogger logger, IConfiguration configuration)
    {
        log = logger;
        config = configuration;
        apiCosmosDbConnStr = config["API_COSMOS_DB_CONN_STR"];
        apiStorageConnStr = config["BLOB_STORAGE_CONN_STR"];
        databaseId = "hoadb";
        grhaSendEmailEventTopicEndpoint = config["GRHA_SENDMAIL_EVENT_TOPIC_ENDPOINT"];
        grhaSendEmailEventTopicKey = config["GRHA_SENDMAIL_EVENT_TOPIC_KEY"];
        acsEmailConnStr = config["ACS_EMAIL_CONN_STR"];
        acsEmailSenderAddress = config["ACS_EMAIL_SENDER_ADDRESS"];
        util = new CommonUtil(log);
    }


    public async Task<string> SendEmailandUpdateRecs(DuesEmailEvent duesEmailEvent)
    {
            string returnMessage = "";

            string containerId = "hoa_communications";
            CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
            Database db = cosmosClient.GetDatabase(databaseId);
            Container container = db.GetContainer(containerId);
            DateTime currDateTime = DateTime.Now;
            string LastChangedTs = currDateTime.ToString("o");

            var hoaRec = await GetHoaRecDB(duesEmailEvent.parcelId);

            string subject = $"{duesEmailEvent.hoaNameShort} Dues Notice";
            string htmlMessageStr = "";
            string title = duesEmailEvent.hoaNameShort + " Member Dues Notice";    // TEST?
            string noticeYear = (hoaRec.assessmentsList[0].FY - 1).ToString();

            htmlMessageStr = $"<b>{duesEmailEvent.hoaName}</b><br>";
            htmlMessageStr += $"{title} for Fiscal Year <b>{hoaRec.assessmentsList[0].FY.ToString()}</b><br>";
            htmlMessageStr += $"<b>For the Period:</b> Oct 1, {noticeYear} thru Sept 30, {hoaRec.assessmentsList[0].FY.ToString()}<br><br>";
            if (hoaRec.assessmentsList[0].Paid != 1) {
                htmlMessageStr += $"<b>Current Dues Amount: </b>{hoaRec.assessmentsList[0].DuesAmt}<br>";
            }
            htmlMessageStr += $"<b>*****Total Outstanding:</b> ${hoaRec.totalDue} (Includes fees, current & past dues)<br>";
            htmlMessageStr += $"<b>Due Date: </b>October 1, {noticeYear}<br>";
            htmlMessageStr += $"<b>Dues must be paid to avoid a lien and lien fees </b><br><br>";

            htmlMessageStr += $"<b>Parcel Id: </b>{duesEmailEvent.parcelId}<br>";
            htmlMessageStr += $"<b>Owner: </b>{hoaRec.property.Mailing_Name}<br>";
            htmlMessageStr += $"<b>Location: </b>{hoaRec.property.Parcel_Location}<br>";
            htmlMessageStr += $"<b>Phone: </b>{hoaRec.ownersList[0].Owner_Phone}<br>";
            htmlMessageStr += $"<b>Email: </b>{hoaRec.ownersList[0].EmailAddr}<br>";
            htmlMessageStr += $"<b>Email2: </b>{hoaRec.ownersList[0].EmailAddr2}<br>";

            htmlMessageStr += $"<h3><a href='{duesEmailEvent.duesUrl}'>Click here to view Dues Statement or PAY online</a></h3>";
            htmlMessageStr += $"*** Online payment is for properties with ONLY current dues outstanding - if there are outstanding past dues or fees on the account, contact Treasurer for online payment options *** <br>";

            htmlMessageStr += $"Send payment checks to:<br>";
            htmlMessageStr += $"<b>{duesEmailEvent.hoaNameShort}</b><br>";
            htmlMessageStr += $"<b>{duesEmailEvent.hoaAddress1}</b><br>";
            htmlMessageStr += $"<b>{duesEmailEvent.hoaAddress2}</b><br>";

            if (!String.IsNullOrEmpty(duesEmailEvent.helpNotes)) {
                htmlMessageStr += $"<br>{duesEmailEvent.helpNotes}<br>";
            }


            // Create the EmailClient
            var emailClient = new EmailClient(acsEmailConnStr);

            // Build the email content
            var emailContent = new EmailContent(title)
            {
                Html = htmlMessageStr
            };

            var emailRecipients = new EmailRecipients(
                to: new List<EmailAddress>
                {
                    new EmailAddress(duesEmailEvent.emailAddr)
                }
            );

            // Create the message
            var emailMessage = new EmailMessage(
                senderAddress: acsEmailSenderAddress, // must be from a verified domain in ACS
                content: emailContent,
                recipients: emailRecipients
            );

            // Send the email and wait until the operation completes
            EmailSendOperation operation = await emailClient.SendAsync(
                WaitUntil.Completed,
                emailMessage
            );

            // Check the result
            EmailSendResult result = operation.Value;
            if (result.Status != EmailSendStatus.Succeeded)
            {
                log.LogError("---------- DUES EMAIL SEND FAILED ------------");
                log.LogError($">>> {duesEmailEvent.parcelId}, id: {duesEmailEvent.id}, email: {duesEmailEvent.emailAddr}");
                log.LogError($"Email send status: {result.Status.ToString()}");
                throw new Exception("Dues email send failed");
            }

            //----------------------------------------------------------------------------------------------------------------
            // Update the status of the Communications record indicating that the email has been SENT
            //----------------------------------------------------------------------------------------------------------------
            // Initialize a list of PatchOperation (and default to setting the mandatory LastChanged fields)
            List<PatchOperation> patchOperations = new List<PatchOperation>
            {
                PatchOperation.Replace("/SentStatus", "Y"),
                PatchOperation.Replace("/LastChangedBy", "SendMail"),
                PatchOperation.Replace("/LastChangedTs", LastChangedTs)
            };

            // Convert the list to an array
            PatchOperation[] patchArray = patchOperations.ToArray();

            ItemResponse<dynamic> response = await container.PatchItemAsync<dynamic>(
                duesEmailEvent.id,
                new PartitionKey(duesEmailEvent.parcelId),
                patchArray
            );

            returnMessage = $"Successfully sent email and updated comm rec, Parcel_ID = {duesEmailEvent.parcelId}, email Id: {operation.Id}";
            return returnMessage;
    }

    public async Task<string> SendPaymentEmail(DuesEmailEvent duesEmailEvent)
    {
            string returnMessage = "";

            string containerId = "hoa_payments";
            CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
            Database db = cosmosClient.GetDatabase(databaseId);
            Container container = db.GetContainer(containerId);
            DateTime currDateTime = DateTime.UtcNow;
            string LastChangedTs = currDateTime.ToString("o");

            // Create the EmailClient
            var emailClient = new EmailClient(acsEmailConnStr);

            // Build the email content
            var emailContent = new EmailContent(duesEmailEvent.mailSubject)
            {
                Html = duesEmailEvent.htmlMessage
            };

            var emailRecipients = new EmailRecipients(
                to: new List<EmailAddress>
                {
                    new EmailAddress(duesEmailEvent.emailAddr)
                }
            );

            // Create the message
            var emailMessage = new EmailMessage(
                senderAddress: acsEmailSenderAddress, // must be from a verified domain in ACS
                content: emailContent,
                recipients: emailRecipients
            );

            // Send the email and wait until the operation completes
            EmailSendOperation operation = await emailClient.SendAsync(
                WaitUntil.Completed,
                emailMessage
            );

            // Check the result
            EmailSendResult result = operation.Value;
            //log.LogWarning($"Email send status: {result.Status.ToString()}, Succeeded = {EmailSendStatus.Succeeded.ToString()}, Id: {operation.Id}");
            if (result.Status != EmailSendStatus.Succeeded)
            {
                log.LogError("---------- PAYMENT EMAIL SEND FAILED ------------");
                log.LogError($">>> {duesEmailEvent.parcelId}, id: {duesEmailEvent.id}, email: {duesEmailEvent.emailAddr}");
                log.LogError($"Email send status: {result.Status.ToString()}");
                throw new Exception("Payment email send failed");
            }

            //----------------------------------------------------------------------------------------------------------------
            // Update the status of the Payment record indicating that the email has been SENT
            //----------------------------------------------------------------------------------------------------------------
            // Initialize a list of PatchOperation (and default to setting the mandatory LastChanged fields)
            List<PatchOperation> patchOperations = new List<PatchOperation>
            {
                PatchOperation.Replace("/paidEmailSent", "Y"),
                PatchOperation.Replace("/LastChangedTs", LastChangedTs)
            };

            // Convert the list to an array
            PatchOperation[] patchArray = patchOperations.ToArray();

            ItemResponse<dynamic> response = await container.PatchItemAsync<dynamic>(
                duesEmailEvent.id,
                new PartitionKey(duesEmailEvent.parcelId),
                patchArray
            );

            returnMessage = $"Successfully sent email and updated payments rec, Parcel_ID: {duesEmailEvent.parcelId}, email Id: {operation.Id}";
            return returnMessage;
    }


    // Common internal function to lookup configuration values
    private async Task<string> getConfigVal(Container container, string configName)
    {
        string configVal = "";
        var queryDefinition = new QueryDefinition("SELECT * FROM c WHERE c.ConfigName = @configName ")
            .WithParameter("@configName", configName);
        var feed = container.GetItemQueryIterator<hoa_config>(queryDefinition);
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            foreach (var item in response)
            {
                configVal = item.ConfigValue ?? "";
            }
        }
        return configVal;
    }

    public async Task<List<HoaProperty>> GetPropertyList(string searchStr)
    {
        // Construct the query from the parameters
        searchStr = searchStr.Trim().ToUpper();
        var queryDefinition = new QueryDefinition("SELECT * FROM c WHERE "
            + "CONTAINS(UPPER(c.Parcel_ID),@searchStr) "
            + "OR CONTAINS(UPPER(c.LotNo),@searchStr) "
            + "OR CONTAINS(UPPER(c.Parcel_Location),@searchStr) "
            + "OR CONTAINS(UPPER(CONCAT(c.Owner_Name1,' ',c.Owner_Name2,' ',c.Mailing_Name)),@searchStr) "
            + "ORDER BY c.id")
        .WithParameter("@searchStr", searchStr);

        //------------------------------------------------------------------------------------------------------------------
        // Query the NoSQL container to get values
        //------------------------------------------------------------------------------------------------------------------
        List<HoaProperty> hoaPropertyList = new List<HoaProperty>();
        HoaProperty hoaProperty = new HoaProperty();

        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container container = db.GetContainer("hoa_properties");

        var feed = container.GetItemQueryIterator<hoa_properties>(queryDefinition);
        int cnt = 0;
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            foreach (var item in response)
            {
                cnt++;
                hoaProperty = new HoaProperty();
                hoaProperty.parcelId = item.Parcel_ID;
                hoaProperty.lotNo = item.LotNo;
                hoaProperty.subDivParcel = item.SubDivParcel;
                hoaProperty.parcelLocation = item.Parcel_Location;
                hoaProperty.ownerName = item.Owner_Name1 + " " + item.Owner_Name2;
                hoaProperty.ownerPhone = item.Owner_Phone;
                hoaPropertyList.Add(hoaProperty);
            }
        }

        return hoaPropertyList;
    }

    public async Task<List<HoaProperty2>> GetPropertyList2(string searchAddress)
    {
        //------------------------------------------------------------------------------------------------------------------
        // Query the NoSQL container to get values
        //------------------------------------------------------------------------------------------------------------------
        string databaseId = "hoadb";
        string containerId = "hoa_properties";

        List<HoaProperty2> hoaProperty2List = new List<HoaProperty2>();

        HoaProperty2 hoaProperty2 = new HoaProperty2();

        //var mySetting = _configuration["MY_ENV_VARIABLE"];
        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container container = db.GetContainer(containerId);

        // Get the existing document from Cosmos DB
        string sql = $"";
        if (searchAddress.Equals(""))
        {
            sql = $"SELECT * FROM c ORDER BY c.id";
        }
        else
        {
            sql = $"SELECT * FROM c WHERE CONTAINS(UPPER(c.Parcel_Location),'{searchAddress.Trim().ToUpper()}') ORDER BY c.id";
        }

        var feed = container.GetItemQueryIterator<hoa_properties2>(sql);
        int cnt = 0;
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            foreach (var item in response)
            {
                cnt++;
                //log.LogInformation($"{cnt}  Name: {mediaPeople.PeopleName}");
                hoaProperty2 = new HoaProperty2();
                hoaProperty2.parcelId = item.Parcel_ID;
                hoaProperty2.lotNo = item.LotNo;
                hoaProperty2.subDivParcel = item.SubDivParcel;
                hoaProperty2.parcelLocation = item.Parcel_Location;
                hoaProperty2List.Add(hoaProperty2);
            }
        }

        return hoaProperty2List;
    }

    //==============================================================================================================
    // Main details lookup service to get data from all the containers for a specific property and populate
    // the HoaRec object.  It also calculates the total Dues due with interest, and gets emails and sales info
    //==============================================================================================================
    public async Task<HoaRec> GetHoaRecDB(string parcelId, string ownerId = "", string fy = "", string saleDate = "")
    {
        //------------------------------------------------------------------------------------------------------------------
        // Query the NoSQL container to get values
        //------------------------------------------------------------------------------------------------------------------
        string containerId = "hoa_properties";
        string sql = $"";

        HoaRec hoaRec = new HoaRec();
        hoaRec.totalDue = 0.00m;
        hoaRec.paymentInstructions = "";
        hoaRec.paymentFee = 0.00m;
        hoaRec.duesEmailAddr = "";

        hoaRec.ownersList = new List<hoa_owners>();
        hoaRec.assessmentsList = new List<hoa_assessments>();
        hoaRec.commList = new List<hoa_communications>();
        hoaRec.salesList = new List<hoa_sales>();
        hoaRec.totalDuesCalcList = new List<TotalDuesCalcRec>();
        hoaRec.emailAddrList = new List<string>();

        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container configContainer = db.GetContainer("hoa_config");

        //----------------------------------- Property --------------------------------------------------------
        containerId = "hoa_properties";
        Container container = db.GetContainer(containerId);
        //sql = $"SELECT * FROM c WHERE c.id = '{parcelId}' ";
        var queryDefinition = new QueryDefinition(
            "SELECT * FROM c WHERE c.id = @parcelId ")
                .WithParameter("@parcelId", parcelId);
        var feed = container.GetItemQueryIterator<hoa_properties>(queryDefinition);
        int cnt = 0;
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            foreach (var item in response)
            {
                cnt++;
                hoaRec.property = item;
            }
        }

        //----------------------------------- Owners ----------------------------------------------------------
        containerId = "hoa_owners";
        Container ownersContainer = db.GetContainer(containerId);

        if (!ownerId.Equals(""))
        {
            queryDefinition = new QueryDefinition(
                "SELECT * FROM c WHERE c.id = @ownerId AND c.Parcel_ID = @parcelId ")
                .WithParameter("@ownerId", ownerId)
                .WithParameter("@parcelId", parcelId);
        }
        else
        {
            queryDefinition = new QueryDefinition(
                "SELECT * FROM c WHERE c.Parcel_ID = @parcelId ORDER BY c.OwnerID DESC ")
                .WithParameter("@parcelId", parcelId);
        }
        var ownersFeed = ownersContainer.GetItemQueryIterator<hoa_owners>(queryDefinition);
        cnt = 0;
        while (ownersFeed.HasMoreResults)
        {
            var response = await ownersFeed.ReadNextAsync();
            foreach (var item in response)
            {
                cnt++;
                hoaRec.ownersList.Add(item);

                if (item.CurrentOwner == 1)
                {
                    // Current Owner fields are already part of the properties record (including property.OwnerID)

                    hoaRec.duesEmailAddr = item.EmailAddr;
                    if (!string.IsNullOrWhiteSpace(item.EmailAddr))
                    {
                        hoaRec.emailAddrList.Add(item.EmailAddr);
                    }
                    if (!string.IsNullOrWhiteSpace(item.EmailAddr2))
                    {
                        hoaRec.emailAddrList.Add(item.EmailAddr2);
                    }
                }
            }
        }

        //----------------------------------- Emails ----------------------------------------------------------
        containerId = "hoa_payments";
        Container paymentsContainer = db.GetContainer(containerId);
        //--------------------------------------------------------------------------------------------------
        // Override email address to use if we get the last email used to make an electronic payment
        // 10/15/2022 JJK Modified to only look for payments within the last year (because of renter issue)
        //--------------------------------------------------------------------------------------------------
        sql = $"SELECT * FROM c WHERE c.OwnerID = {hoaRec.property!.OwnerID} AND c.Parcel_ID = '{parcelId}' AND c.payment_date > DateTimeAdd('yy', -1, GetCurrentDateTime()) ";
        var paymentsFeed = paymentsContainer.GetItemQueryIterator<hoa_payments>(sql);
        cnt = 0;
        while (paymentsFeed.HasMoreResults)
        {
            var response = await paymentsFeed.ReadNextAsync();
            foreach (var item in response)
            {
                cnt++;
                if (!string.IsNullOrWhiteSpace(item.payer_email))
                {
                    // If there is an email from the last electronic payment, for the current Owner,
                    // add it to the email list (if not already in the array)
                    string compareStr = item.payer_email.ToLower();
                    if (Array.IndexOf(hoaRec.emailAddrList.ToArray(), compareStr) < 0)
                    {
                        hoaRec.emailAddrList.Add(compareStr);
                    }
                }
            }
        }

        //----------------------------------- Assessments -----------------------------------------------------
        containerId = "hoa_assessments";
        Container assessmentsContainer = db.GetContainer(containerId);
        if (fy.Equals("") || fy.Equals("LATEST"))
        {
            sql = $"SELECT * FROM c WHERE c.Parcel_ID = '{parcelId}' ORDER BY c.FY DESC ";
        }
        else
        {
            sql = $"SELECT * FROM c WHERE c.Parcel_ID = '{parcelId}' AND c.FY = {fy} ORDER BY c.FY DESC ";
        }
        var assessmentsFeed = assessmentsContainer.GetItemQueryIterator<hoa_assessments>(sql);
        cnt = 0;
        DateTime currDate = DateTime.Now;
        DateTime dateTime;
        DateTime dateDue;
        while (assessmentsFeed.HasMoreResults)
        {
            var response = await assessmentsFeed.ReadNextAsync();
            foreach (var item in response)
            {
                cnt++;
                if (fy.Equals("LATEST") && cnt > 1)
                {
                    continue;
                }

                // Reformat the due date to yyyy-MM-dd for consistency
                item.DateDue = util.ParseDate(item.DateDue, (item.FY - 1).ToString() + "-10-01"); 
                dateDue = DateTime.Parse(item.DateDue);

                if (item.Paid == 1)
                {
                    item.DatePaid = util.ParseDate(item.DatePaid,item.DateDue);
                }

                item.DuesDue = false;
                if (item.Paid != 1 && item.NonCollectible != 1)
                {
                    // check dates (if NOT PAID)
                    if (currDate > dateDue)
                    {
                        item.DuesDue = true;
                    }
                }

                hoaRec.assessmentsList.Add(item);

            } // Assessments loop
        }

        // Pass the assessments to the common function to calculate Total Dues
        bool onlyCurrYearDue;
        decimal totalDueOut;
        hoaRec.totalDuesCalcList = util.CalcTotalDues(hoaRec.assessmentsList, out onlyCurrYearDue, out totalDueOut);
        hoaRec.totalDue = totalDueOut;

        //---------------------------------------------------------------------------------------------------
        // Construct the online payment button and instructions according to what is owed
        //---------------------------------------------------------------------------------------------------
        // Only display payment button if something is owed
        // For now, only set payment button if just the current year dues are owed (no other years or open liens)
        if (hoaRec.totalDue > 0.0m)
        {
            /* Old logic of only showing the online payment button if just the current year dues are owed (no other years or open liens) and a flat fee
            hoaRec.paymentInstructions = await getConfigVal(configContainer, "OfflinePaymentInstructions");
            hoaRec.paymentFee = decimal.Parse(await getConfigVal(configContainer, "paymentFee"));
            if (onlyCurrYearDue)
            {
                hoaRec.paymentInstructions = await getConfigVal(configContainer, "OnlinePaymentInstructions");
            }
            */

            // 2026-03-17 JJK - Calculate the processing fee for electronic payments based on the total amount due
            hoaRec.paymentFee = util.CalcProcessingFee(hoaRec.totalDue);
            hoaRec.paymentInstructions = await getConfigVal(configContainer, "OnlinePaymentInstructions");
        }

        //----------------------------------- Sales -----------------------------------------------------------
        containerId = "hoa_sales";
        Container salesContainer = db.GetContainer(containerId);
        if (saleDate.Equals(""))
        {
            sql = $"SELECT * FROM c WHERE c.id = '{parcelId}' ORDER BY c.CreateTimestamp DESC ";
        }
        else
        {
            sql = $"SELECT * FROM c WHERE c.id = '{parcelId}' AND c.SALEDT = {saleDate} ";
        }
        var salesFeed = salesContainer.GetItemQueryIterator<hoa_sales>(sql);
        cnt = 0;
        while (salesFeed.HasMoreResults)
        {
            var response = await salesFeed.ReadNextAsync();
            foreach (var item in response)
            {
                cnt++;
                hoaRec.salesList.Add(item);
            } // Sales loop
        }

        return hoaRec;
    }


    public async Task<HoaRec2> GetHoaRec2DB(string parcelId)
    {
        //------------------------------------------------------------------------------------------------------------------
        // Query the NoSQL container to get values
        //------------------------------------------------------------------------------------------------------------------
        HoaRec2 hoaRec2 = new HoaRec2();
        hoaRec2.totalDue = 0.00m;
        hoaRec2.paymentInstructions = "";
        hoaRec2.paymentFee = 0.00m;
        hoaRec2.assessmentsList = new List<hoa_assessments>();
        hoaRec2.totalDuesCalcList = new List<TotalDuesCalcRec>();
        string databaseId = "hoadb";
        string containerId = "hoa_properties";
        string sql;

        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container configContainer = db.GetContainer("hoa_config");

        //----------------------------------- Property --------------------------------------------------------
        containerId = "hoa_properties";
        Container container = db.GetContainer(containerId);
        //sql = $"SELECT * FROM c WHERE c.id = '{parcelId}' ";

        var queryDefinition = new QueryDefinition(
            "SELECT * FROM c WHERE c.id = @parcelId ")
                .WithParameter("@parcelId", parcelId);

        var feed = container.GetItemQueryIterator<hoa_properties2>(queryDefinition);
        int cnt = 0;
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            foreach (var item in response)
            {
                cnt++;
                hoaRec2.property = item;
            }
        }

        //----------------------------------- Assessments -----------------------------------------------------
        containerId = "hoa_assessments";
        Container assessmentsContainer = db.GetContainer(containerId);
        sql = $"SELECT * FROM c WHERE c.Parcel_ID = '{parcelId}' ORDER BY c.FY DESC ";
        var assessmentsFeed = assessmentsContainer.GetItemQueryIterator<hoa_assessments>(sql);
        cnt = 0;
        DateTime dateTime;
        DateTime dateDue;
        while (assessmentsFeed.HasMoreResults)
        {
            var response = await assessmentsFeed.ReadNextAsync();
            foreach (var item in response)
            {
                cnt++;

                // Reformat the due date to yyyy-MM-dd for consistency
                item.DateDue = util.ParseDate(item.DateDue, (item.FY - 1).ToString() + "-10-01"); 
                dateDue = DateTime.Parse(item.DateDue);

                if (item.Paid == 1)
                {
                    item.DatePaid = util.ParseDate(item.DatePaid,item.DateDue);
                }

                hoaRec2.assessmentsList.Add(item);

            } // Assessments loop
        }

        // Pass the assessments to the common function to calculate Total Dues
        bool onlyCurrYearDue;
        decimal totalDueOut;
        hoaRec2.totalDuesCalcList = util.CalcTotalDues(hoaRec2.assessmentsList, out onlyCurrYearDue, out totalDueOut);
        hoaRec2.totalDue = totalDueOut;

        //---------------------------------------------------------------------------------------------------
        // Construct the online payment button and instructions according to what is owed
        //---------------------------------------------------------------------------------------------------
        // Only display payment button if something is owed
        // For now, only set payment button if just the current year dues are owed (no other years or open liens)
        if (hoaRec2.totalDue > 0.0m)
        {
            /* Old logic of only showing the online payment button if just the current year dues are owed (no other years or open liens) and a flat fee
            hoaRec2.paymentInstructions = await getConfigVal(configContainer, "OfflinePaymentInstructions");
            hoaRec2.paymentFee = decimal.Parse(await getConfigVal(configContainer, "paymentFee"));
            if (onlyCurrYearDue)
            {
                hoaRec2.paymentInstructions = await getConfigVal(configContainer, "OnlinePaymentInstructions");
            }
            */
            
            // 2026-03-17 JJK - Calculate the processing fee for electronic payments based on the total amount due
            hoaRec2.paymentFee = util.CalcProcessingFee(hoaRec2.totalDue);
            hoaRec2.paymentInstructions = await getConfigVal(configContainer, "OnlinePaymentInstructions");
        }

        return hoaRec2;
    }


    //==============================================================================================================
    //  Function to return an array of full hoaRec objects (with a couple of parameters to filter list)
    //==============================================================================================================
    public async Task<List<HoaRec>> GetHoaRecListDB(
        bool duesOwed = false,
        bool skipEmail = false,
        bool currYearPaid = false,
        bool currYearUnpaid = false,
        bool testEmail = false)
    {
        List<HoaRec> outputList = new List<HoaRec>();
        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container propertiesContainer = db.GetContainer("hoa_properties");
        Container ownersContainer = db.GetContainer("hoa_owners");
        Container assessmentsContainer = db.GetContainer("hoa_assessments");
        //Container salesContainer = db.GetContainer("hoa_sales");
        Container configContainer = db.GetContainer("hoa_config");

        string sql = "";
        string? testEmailParcel = null;
        int fy = 0;

        if (testEmail)
        {
            // Get the test parcel from config
            testEmailParcel = await getConfigVal(configContainer, "duesEmailTestParcel");
            //sql = $"SELECT * FROM c WHERE c.Parcel_ID = '{testEmailParcel}' ORDER BY c.Parcel_ID";
        }

        // Get max FY if needed
        if (currYearPaid || currYearUnpaid)
        {
            var maxFyQuery = new QueryDefinition("SELECT VALUE MAX(c.FY) FROM c");
            var maxFyFeed = assessmentsContainer.GetItemQueryIterator<int>(maxFyQuery);
            while (fy == 0 && maxFyFeed.HasMoreResults)
            {
                var response = await maxFyFeed.ReadNextAsync();
                foreach (var item in response)
                {
                    fy = item;
                }
            }
        }

        // Get all properties
        List<hoa_properties> propList = new List<hoa_properties>();
        var allPropQuery = new QueryDefinition("SELECT * FROM c ORDER BY c.Parcel_ID");
        var allPropFeed = propertiesContainer.GetItemQueryIterator<hoa_properties>(allPropQuery);
        while (allPropFeed.HasMoreResults)
        {
            var response = await allPropFeed.ReadNextAsync();
            foreach (var item in response)
            {
                propList.Add(item);
            }
        }

        // Get all current owners
        List<hoa_owners> ownerList = new List<hoa_owners>();
        var allOwnerQuery = new QueryDefinition("SELECT * FROM c WHERE c.CurrentOwner = 1");
        var allOwnerFeed = ownersContainer.GetItemQueryIterator<hoa_owners>(allOwnerQuery);
        while (allOwnerFeed.HasMoreResults)
        {
            var response = await allOwnerFeed.ReadNextAsync();
            foreach (var item in response)
            {
                ownerList.Add(item);
            }
        }

        // Get all assessments
        List<hoa_assessments> assessmentList = new List<hoa_assessments>();
        var allAssessmentQuery = new QueryDefinition("SELECT * FROM c ORDER BY c.FY DESC ");
        if (currYearPaid)
        {
            allAssessmentQuery = new QueryDefinition("SELECT * FROM c WHERE c.FY = @fy AND c.Paid = 1")
                .WithParameter("@fy", fy);
        }
        if (currYearUnpaid)
        {
            allAssessmentQuery = new QueryDefinition(
                "SELECT * FROM c WHERE c.FY = @fy AND c.Paid = 0 AND (IS_NULL(c.NonCollectible) OR c.NonCollectible != 1)")
                .WithParameter("@fy", fy);
        }
        var allAssessmentFeed = assessmentsContainer.GetItemQueryIterator<hoa_assessments>(allAssessmentQuery);
        while (allAssessmentFeed.HasMoreResults)
        {
            var response = await allAssessmentFeed.ReadNextAsync();
            foreach (var item in response)
            {
                assessmentList.Add(item);
            }
        }

        // Get all config values into a dictionary
        /*
        Dictionary<string, string> configDict = new Dictionary<string, string>();
        var configQuery = new QueryDefinition("SELECT * FROM c");
        var configFeed = configContainer.GetItemQueryIterator<hoa_config>(configQuery);
        while (configFeed.HasMoreResults)
        {
            var response = await configFeed.ReadNextAsync();
            foreach (var item in response)
            {
                if (!string.IsNullOrEmpty(item.ConfigName))
                    configDict[item.ConfigName] = item.ConfigValue ?? string.Empty;
            }
        }
        */

        // Build HoaRec for each property using the in-memory lists and config
        foreach (var prop in propList)
        {
            //var hoaRec = BuildHoaRecFromLists(prop, ownerList, assessmentList, configDict);
            var hoaRec = BuildHoaRecFromLists(prop, ownerList, assessmentList);

            if ((duesOwed || currYearUnpaid) && hoaRec.totalDue < 0.01m)
            {
                continue;
            }
            /* 2025-09-11 JJK - Board decided to send Due Notice postal letters to ALL properties, even if they have an email use preference
                                (because of old or bad emails and the need to make sure everyone gets a notice)
            if (skipEmail && (hoaRec.property.UseEmail == 1))
            {
                continue;
            }
            */

            outputList.Add(hoaRec);
        }

        return outputList;
    }

    // Build an HoaRec using in-memory lists of owners and assessments
    public HoaRec BuildHoaRecFromLists(
        hoa_properties property,
        List<hoa_owners> ownerList,
        List<hoa_assessments> assessmentList)
    //            Dictionary<string, string> configDict)
    {
        HoaRec hoaRec = new HoaRec();
        hoaRec.property = property;
        hoaRec.ownersList = ownerList.Where(o => o.Parcel_ID == property.Parcel_ID).ToList();
        hoaRec.assessmentsList = assessmentList.Where(a => a.Parcel_ID == property.Parcel_ID).ToList();
        hoaRec.totalDuesCalcList = util.CalcTotalDues(hoaRec.assessmentsList, out bool onlyCurrYearDue, out decimal totalDueOut);
        hoaRec.totalDue = totalDueOut;
        // Set config-based fields if present
        /*
        if (configDict != null)
        {
            //if (configDict.TryGetValue("OfflinePaymentInstructions", out var offlinePay))
            //    hoaRec.paymentInstructions = offlinePay;
            //if (configDict.TryGetValue("OnlinePaymentInstructions", out var onlinePay) && onlyCurrYearDue)
            //    hoaRec.paymentInstructions = onlinePay;
            //if (configDict.TryGetValue("paymentFee", out var payFee) && decimal.TryParse(payFee, out var feeVal))
            //    hoaRec.paymentFee = feeVal;
            if (configDict.TryGetValue("duesStatementNotes", out var notes))
                hoaRec.duesStatementNotes = notes;
            if (configDict.TryGetValue("hoaNameShort", out var hoaName))
                hoaRec.hoaNameShort = hoaName;
        }
        */
        return hoaRec;
    }


    public async Task<List<hoa_communications>> GetCommunicationsDB(string parcelId, string sentStatus = "")
    {
        //------------------------------------------------------------------------------------------------------------------
        // Query the NoSQL container to get values
        //------------------------------------------------------------------------------------------------------------------
        string containerId = "hoa_communications";
        //string sql = $"";

        List<hoa_communications> hoaCommunicationsList = new List<hoa_communications>();

        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container container = db.GetContainer(containerId);

        QueryDefinition queryDefinition;
        if (parcelId.Equals("DuesNoticeEmails"))
        {
            if (!sentStatus.Equals(""))
            {
                queryDefinition = new QueryDefinition(
                    "SELECT * FROM c WHERE c.Email = 1 AND c.SentStatus = @sentStatus ORDER BY c.CreateTs DESC ")
                    .WithParameter("@sentStatus", sentStatus);
            }
            else
            {
                queryDefinition = new QueryDefinition(
                    "SELECT TOP 400 * FROM c WHERE c.Email = 1 ORDER BY c.CreateTs DESC ");
                    //"SELECT * FROM c WHERE c.Email = 1 ORDER BY c.CreateTs DESC OFFSET 0 LIMIT 400");
            }
        }
        else
        {
            queryDefinition = new QueryDefinition(
                "SELECT * FROM c WHERE c.Parcel_ID = @parcelId ORDER BY c.CreateTs DESC ")
                .WithParameter("@parcelId", parcelId);
        }

        var feed = container.GetItemQueryIterator<hoa_communications>(queryDefinition);
        int cnt = 0;
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            foreach (var item in response)
            {
                cnt++;
                hoaCommunicationsList.Add(item);
            }
        }

        return hoaCommunicationsList;
    }

    public async Task<int> CreateDuesEmailsListDB(string userName)
    {
        bool duesOwed = true;
        bool skipEmail = false;
        bool currYearPaid = false;
        bool currYearUnpaid = false;
        bool testEmail = false;
        int returnCnt = 0;

        // Get a list of the parcels that have dues owed
        var hoaRecList = await GetHoaRecListDB(duesOwed, skipEmail, currYearPaid, currYearUnpaid, testEmail);
        
        string containerId = "hoa_communications";
        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container container = db.GetContainer(containerId);
        DateTime currDateTime = DateTime.Now;

        // Delete any existing hoa_communications records with Email = 1 and SentStatus = 'N'
        QueryDefinition deleteQuery = new QueryDefinition("SELECT * FROM c WHERE c.Email = 1 AND c.SentStatus = 'N'");
        var deleteFeed = container.GetItemQueryIterator<hoa_communications>(deleteQuery);
        while (deleteFeed.HasMoreResults)
        {
            var response = await deleteFeed.ReadNextAsync();
            foreach (var item in response)
            {
                await container.DeleteItemAsync<hoa_communications>(item.id, new PartitionKey(item.Parcel_ID));
            }
        }

        // Get list of parcels that owe dues and have a valid email address
        //int cnt = 0;
        string commId = "";
        foreach (var hoaRec in hoaRecList)
        {
            hoaRec.emailAddrList = new List<string>();

            // Add the valid emails to the list
            if (!string.IsNullOrWhiteSpace(hoaRec.ownersList[0].EmailAddr))
            {
                if (util.IsValidEmail(hoaRec.ownersList[0].EmailAddr))
                {
                    hoaRec.emailAddrList.Add(hoaRec.ownersList[0].EmailAddr);
                }
            }
            if (!string.IsNullOrWhiteSpace(hoaRec.ownersList[0].EmailAddr2))
            {
                if (util.IsValidEmail(hoaRec.ownersList[0].EmailAddr2))
                {
                    hoaRec.emailAddrList.Add(hoaRec.ownersList[0].EmailAddr2);
                }
            }

            // Skip parcel if there are no valid email addresses
            if (hoaRec.emailAddrList.Count < 1)
            {
                continue;
            }
            // Create a communication record and an email send event for each valid email address for the Owner
            foreach (var emailAddr in hoaRec.emailAddrList)
            {
                returnCnt++;
                //log.LogWarning($"{returnCnt} Parcel = {hoaRec.property.Parcel_ID}, TotalDue = {hoaRec.totalDue}, email = {emailAddr}");
                // >>>>>>>>>>>>>>>>>>>>>>> Limit for testing <<<<<<<<<<<<<<<<<<<<<<<
                /*
                if (returnCnt > 10)
                {
                    return returnCnt;
                }
                */
                commId = Guid.NewGuid().ToString();

                // Create a metadata object from the media file information
                hoa_communications hoa_comm = new hoa_communications
                {
                    id = commId,
                    Parcel_ID = hoaRec.property.Parcel_ID,
                    CommID = 9999,
                    CreateTs = currDateTime,
                    OwnerID = hoaRec.property.OwnerID,
                    CommType = "Dues Notice",
                    CommDesc = "Sent to Owner email - " + hoaRec.property.Parcel_Location,
                    Mailing_Name = hoaRec.property.Mailing_Name,
                    Email = 1,
                    EmailAddr = emailAddr,
                    SentStatus = "N",
                    LastChangedBy = userName,
                    LastChangedTs = currDateTime
                };

                // Insert a new communications doc for the dues email send
                await container.CreateItemAsync(hoa_comm, new PartitionKey(hoa_comm.Parcel_ID));

            }
        }
        return returnCnt;
    }

    public async Task<int> SendDuesNoticeEmailsDB(string userName)
    {
        int returnCnt = 0;

        string containerId = "hoa_communications";
        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container container = db.GetContainer(containerId);
        Container configContainer = db.GetContainer("hoa_config");
        DateTime currDateTime = DateTime.Now;
        string LastChangedTs = currDateTime.ToString("o");

        var eventGridPublisherClient = new EventGridPublisherClient(
            new Uri(grhaSendEmailEventTopicEndpoint),
            new AzureKeyCredential(grhaSendEmailEventTopicKey)
        );

        // Create an object to send data values to the send email event
        DuesEmailEvent duesEmailEvent = new DuesEmailEvent();
        duesEmailEvent.hoaName = await getConfigVal(configContainer, "hoaName");
        duesEmailEvent.hoaNameShort = await getConfigVal(configContainer, "hoaNameShort");
        duesEmailEvent.hoaAddress1 = await getConfigVal(configContainer, "hoaAddress1");
        duesEmailEvent.hoaAddress2 = await getConfigVal(configContainer, "hoaAddress2");
        duesEmailEvent.helpNotes = await getConfigVal(configContainer, "duesNotes");
        duesEmailEvent.duesUrl = await getConfigVal(configContainer, "duesUrl");


        QueryDefinition queryDefinition = new QueryDefinition(
            "SELECT * FROM c WHERE c.Email = 1 AND c.SentStatus = 'N' ");

        var feed = container.GetItemQueryIterator<hoa_communications>(queryDefinition);
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            foreach (var hoa_comm in response)
            {
                returnCnt++;
                duesEmailEvent.id = hoa_comm.id;
                duesEmailEvent.parcelId = hoa_comm.Parcel_ID;
                duesEmailEvent.emailAddr = hoa_comm.EmailAddr;

                // Queue up an event to create and send the dues notice for this email address
                await eventGridPublisherClient.SendEventAsync(
                    new EventGridEvent(
                        subject: "DuesEmailRequest",
                        eventType: "SendMail",
                        dataVersion: "1.0",
                        data: BinaryData.FromObjectAsJson(duesEmailEvent)
                    )
                );
            }
        }

        return returnCnt;
    }


    public async Task<List<hoa_sales>> GetSalesListDb()
    {
        //------------------------------------------------------------------------------------------------------------------
        // Query the NoSQL container to get values
        //------------------------------------------------------------------------------------------------------------------
        string containerId = "hoa_sales";

        List<hoa_sales> hoaSalesList = new List<hoa_sales>();

        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        //Container configContainer = db.GetContainer("hoa_config");
        Container container = db.GetContainer(containerId);
        //var queryDefinition = new QueryDefinition("SELECT * FROM c ORDER BY c.CreateTimestamp DESC OFFSET 0 LIMIT 200 ");
        // "SALEDT": "01-AUG-23"
        // Note: If you can change how SALEDT is stored (ISO yyyy-MM-dd), you can sort directly in the Cosmos query (ORDER BY c.SALEDT DESC).
        var queryDefinition = new QueryDefinition("SELECT TOP 200 * FROM c ORDER BY c.LastChangedTs DESC ");
        var feed = container.GetItemQueryIterator<hoa_sales>(queryDefinition);
        int cnt = 0;
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            foreach (var item in response)
            {
                cnt++;
                hoaSalesList.Add(item);
            }
        }

        // Sort by SALEDT (format "dd-MMM-yy") descending. Falls back to DateTime.MinValue when parse fails.
        hoaSalesList = hoaSalesList
            .OrderByDescending(s =>
                DateTime.TryParseExact(s.SALEDT, "dd-MMM-yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                    ? dt
                    : DateTime.MinValue)
            .ToList();
            
        return hoaSalesList;
    }

    // Get all config values from hoa_config container
    public async Task<List<hoa_config>> GetConfigListDB()
    {
        List<hoa_config> configList = new List<hoa_config>();
        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container configContainer = db.GetContainer("hoa_config");
        var query = new QueryDefinition("SELECT * FROM c ORDER BY c.ConfigName");
        var feed = configContainer.GetItemQueryIterator<hoa_config>(query);
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            foreach (var item in response)
            {
                configList.Add(item);
            }
        }
        return configList;
    }

    // Update or insert a config value in hoa_config container
    public async Task<hoa_config> UpdateConfigDB(string userName, string configName, string configDesc, string configValue)
    {
        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container configContainer = db.GetContainer("hoa_config");
        hoa_config configRec = null;
        // Try to get existing config by ConfigName
        var query = new QueryDefinition("SELECT * FROM c WHERE c.ConfigName = @configName")
            .WithParameter("@configName", configName);
        var feed = configContainer.GetItemQueryIterator<hoa_config>(query);
        string id = null;
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            foreach (var item in response)
            {
                configRec = item;
                id = item.id;
            }
        }
        if (configRec == null)
        {
            // Insert new config
            configRec = new hoa_config
            {
                id = configName,
                ConfigName = configName,
                ConfigDesc = configDesc,
                ConfigValue = configValue
            };
            await configContainer.CreateItemAsync(configRec, new PartitionKey(configRec.id));
        }
        else
        {
            // Update existing config
            configRec.ConfigDesc = configDesc;
            configRec.ConfigValue = configValue;
            await configContainer.ReplaceItemAsync(configRec, configRec.id, new PartitionKey(configRec.id));
        }
        return configRec;
    }


    public async Task<List<PaidDuesCount>> GetPaidDuesCountListDb()
    {
        //------------------------------------------------------------------------------------------------------------------
        // Query the NoSQL container to get values
        //------------------------------------------------------------------------------------------------------------------
        string containerId = "hoa_assessments";

        List<PaidDuesCount> duesCountList = new List<PaidDuesCount>();

        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container container = db.GetContainer(containerId);
        string sql = "SELECT * FROM c WHERE c.FY > 2006 ORDER BY c.FY ";
        var feed = container.GetItemQueryIterator<hoa_assessments>(sql);

        int paidCnt = 0;
        int unPaidCnt = 0;
        int nonCollCnt = 0;
        decimal totalDue = 0.0m;
        decimal nonCollDue = 0.0m;
        int cnt = 0;
        int prevFY = 0;
        bool first = true;

        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            foreach (var item in response)
            {
                cnt++;
                //log.LogWarning($"{cnt} FY: {item.FY}, Parcel_ID: {item.Parcel_ID}, DuesAmt: {item.DuesAmt}, Paid: {item.Paid}");

                int fy = item.FY;
                decimal duesAmt = util.stringToMoney(item.DuesAmt);

                if (first)
                {
                    prevFY = fy;
                    first = false;
                }

                if (fy != prevFY)
                {
                    // Add previous FY bucket
                    PaidDuesCount rec = new PaidDuesCount
                    {
                        fy = prevFY,
                        paidCnt = paidCnt,
                        unpaidCnt = unPaidCnt,
                        nonCollCnt = nonCollCnt,
                        totalDue = totalDue,
                        nonCollDue = nonCollDue
                    };
                    duesCountList.Add(rec);

                    // Reset counters
                    paidCnt = 0;
                    unPaidCnt = 0;
                    nonCollCnt = 0;
                    totalDue = 0.0m;
                    nonCollDue = 0.0m;
                    prevFY = fy;
                }

                if (item.Paid == 1)
                {
                    paidCnt++;
                }
                else
                {
                    if (item.NonCollectible == 1)
                    {
                        nonCollCnt++;
                        nonCollDue += duesAmt;
                    }
                    else
                    {
                        unPaidCnt++;
                        totalDue += duesAmt;
                    }
                }
            }
        }

        // Add last bucket
        if (!first)
        {
            PaidDuesCount rec = new PaidDuesCount
            {
                fy = prevFY,
                paidCnt = paidCnt,
                unpaidCnt = unPaidCnt,
                nonCollCnt = nonCollCnt,
                totalDue = totalDue,
                nonCollDue = nonCollDue
            };
            duesCountList.Add(rec);
        }

        return duesCountList;
    }

    public async Task UpdateSalesDB(string userName, string parid, string saledt, string processedFlag, string welcomeSent)
    {
        DateTime currDateTime = DateTime.Now;
        string LastChangedTs = currDateTime.ToString("o");

        string databaseId = "hoadb";
        string containerId = "hoa_sales";
        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container container = db.GetContainer(containerId);

        // Patch operations for sales record
        List<PatchOperation> patchOperations = new List<PatchOperation>
            {
                PatchOperation.Replace("/LastChangedBy", userName),
                PatchOperation.Replace("/LastChangedTs", LastChangedTs)
            };

        if (!string.IsNullOrWhiteSpace(processedFlag))
        {
            // If processedFlag is not empty, add it to the patch operations
            patchOperations.Add(PatchOperation.Replace("/ProcessedFlag", processedFlag));
        }

        if (!string.IsNullOrWhiteSpace(welcomeSent))
        {
            // If processedFlag is not empty, add it to the patch operations
            patchOperations.Add(PatchOperation.Replace("/WelcomeSent", welcomeSent));
        }

        PatchOperation[] patchArray = patchOperations.ToArray();

        // Compose id for sales record: usually composite key, but here use saledt as id
        string itemId = parid;
        PartitionKey pk = new PartitionKey(saledt);

        ItemResponse<dynamic> response = await container.PatchItemAsync<dynamic>(
            itemId,
            pk,
            patchArray
        );
    }

    public async Task<List<Trustee>> GetTrusteeListDB()
    {
        //------------------------------------------------------------------------------------------------------------------
        // Query the NoSQL container to get values
        //------------------------------------------------------------------------------------------------------------------
        string databaseId = "hoadb";
        string containerId = "BoardOfTrustees";
        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container container = db.GetContainer(containerId);

        List<Trustee> trusteeList = new List<Trustee>();

        var queryDefinition = new QueryDefinition("SELECT * FROM c ORDER BY c.TrusteeId ");
        var feed = container.GetItemQueryIterator<Trustee>(queryDefinition);
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            foreach (var item in response)
            {
                trusteeList.Add(item);
            }
        }

        return trusteeList;
    }

    public async Task<Trustee> GetTrusteeById(string trusteeId)
    {
        //------------------------------------------------------------------------------------------------------------------
        // Query the NoSQL container to get values
        //------------------------------------------------------------------------------------------------------------------
        string databaseId = "hoadb";
        string containerId = "BoardOfTrustees";
        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container container = db.GetContainer(containerId);

        // Get the existing document from Cosmos DB
        int partitionKey = int.Parse(trusteeId); // Partition key of the item
        var trustee = await container.ReadItemAsync<Trustee>(trusteeId, new PartitionKey(partitionKey));

        return trustee;
    } // public async Task<Trustee> GetTrusteeById(string trusteeId)

    public async Task UpdTrustee(Trustee trustee)
    {
        //------------------------------------------------------------------------------------------------------------------
        // Query the NoSQL container to get values
        //------------------------------------------------------------------------------------------------------------------
        string databaseId = "hoadb";
        string containerId = "BoardOfTrustees";
        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container container = db.GetContainer(containerId);

        await container.ReplaceItemAsync(trustee, trustee.id, new PartitionKey(trustee.TrusteeId));

    } // public async Task UpdTrustee(Trustee trustee)


    private async Task UploadFileToStorageAsync(string containerName, string fileName, byte[] fileData, bool storageOverwrite = true, bool fileIsImage = false, int desiredImgSize = 0)
    {
        var blobContainerClient = new BlobContainerClient(apiStorageConnStr, containerName);
        // Create a client with the URI and the name
        var blobClient = blobContainerClient.GetBlobClient(fileName);
        // Makes a call to Azure to see if this URI+name exists
        if (blobClient.Exists() && !storageOverwrite)
        {
            return;
        }

        // Set a default type
        string contentType = "application/pdf";
        MemoryStream memoryStream = new MemoryStream();

        if (fileIsImage)
        {
            // Create an image from the file data
            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(fileData);
            if (image is null)
            {
                throw new Exception("Image is NULL");
            }
            contentType = "image/jpeg";
            /*
            string ext = fi.Extension.ToLower();
            if (ext.Equals(".png"))
            {
                blobHttpHeaders.ContentType = "image/png";
            }
            else if (ext.Equals(".gif"))
            {
                blobHttpHeaders.ContentType = "image/gif";
            }
            */

            // If you pass 0 as any of the values for width and height dimensions then ImageSharp will
            // automatically determine the correct opposite dimensions size to preserve the original aspect ratio.
            //thumbnails just make img.height = 110   (used to use 130)
            int newImgSize = desiredImgSize;
            if (newImgSize > Math.Max(image.Width, image.Height))
            {
                newImgSize = Math.Max(image.Width, image.Height);
            }

            int width = image.Width;
            int height = image.Height;

            if (desiredImgSize < 200)
            {
                width = 0;
                height = newImgSize;
            }
            else
            {
                if (width > height)
                {
                    width = newImgSize;
                    height = 0;
                }
                else
                {
                    width = 0;
                    height = newImgSize;
                }
            }

            image.Mutate(x => x.Resize(width, height));
            image.Save(memoryStream, image.Metadata.DecodedImageFormat);

        }
        else
        {
            memoryStream = new MemoryStream(fileData);
        }

        memoryStream.Position = 0;
        var blobHttpHeaders = new BlobHttpHeaders
        {
            ContentType = contentType
        };

        blobClient.Upload(memoryStream, storageOverwrite);
        await blobClient.SetHttpHeadersAsync(blobHttpHeaders);

        return;
    } // private async Task UploadFileToStorageAsync


    public async Task UploadFileToDatabase(int mediaTypeId, string fileName, DateTime mediaDateTime, byte[] fileData, string category = "", string title = "", string description = "")
    {
        //------------------------------------------------------------------------------------------------------------------
        // Query the NoSQL container to get values
        //------------------------------------------------------------------------------------------------------------------
        string databaseId = "hoadb";
        string containerId = "MediaInfoDoc";
        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container container = db.GetContainer(containerId);

        if (mediaTypeId == 1)
        {
            await UploadFileToStorageAsync("photos", fileName, fileData, true, true, 2000);
            await UploadFileToStorageAsync("thumbs", fileName, fileData, true, true, 110);
        }
        else if (mediaTypeId == 4)
        {
            await UploadFileToStorageAsync("docs", fileName, fileData, true);
        }

        // Create a metadata object from the media file information
        MediaInfo mediaInfo = new MediaInfo
        {
            id = Guid.NewGuid().ToString(),
            MediaTypeId = mediaTypeId,
            Name = fileName,
            MediaDateTime = mediaDateTime,
            MediaDateTimeVal = int.Parse(mediaDateTime.ToString("yyyyMMddHH")),
            CategoryTags = category,
            MenuTags = "",
            AlbumTags = "",
            Title = title,
            Description = description,
            People = "",
            ToBeProcessed = false,
            SearchStr = fileName.ToLower()
        };

        // Check if there is an existing doc entry in Cosmos DB (by media type and Name)
        var queryDefinition = new QueryDefinition(
            "SELECT * FROM c WHERE c.MediaTypeId = @mediaTypeId AND c.Name = @fileName")
            .WithParameter("@mediaTypeId", mediaTypeId)
            .WithParameter("@fileName", fileName);
        var feed = container.GetItemQueryIterator<MediaInfo>(queryDefinition);
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            foreach (var item in response)
            {
                // If you find an existing doc with the same type and name, just get the "id" for the Upsert (so it updates the existing doc)
                mediaInfo.id = item.id;
            }
        }

        // Insert a new doc, or update an existing one
        await container.UpsertItemAsync(mediaInfo, new PartitionKey(mediaInfo.MediaTypeId));

    } // UploadFileToDatabase

    public void AddPatchField(List<PatchOperation> patchOperations, Dictionary<string, string> formFields, string fieldName, string fieldType = "Text", string operationType = "Replace")
    {
        if (patchOperations == null || formFields == null || string.IsNullOrWhiteSpace(fieldName))
            return; // Prevent potential null reference errors

        if (operationType.Equals("Replace", StringComparison.OrdinalIgnoreCase))
        {
            if (fieldType.Equals("Text"))
            {
                if (formFields.ContainsKey(fieldName))
                {
                    string value = formFields[fieldName]?.Trim() ?? string.Empty;
                    patchOperations.Add(PatchOperation.Replace("/" + fieldName, value));
                }
            }
            else if (fieldType.Equals("Int"))
            {
                if (formFields.ContainsKey(fieldName))
                {
                    string value = formFields[fieldName]?.Trim() ?? string.Empty;
                    patchOperations.Add(PatchOperation.Replace("/" + fieldName, int.Parse(value)));
                }
            }
            else if (fieldType.Equals("Money"))
            {
                string value = formFields[fieldName]?.Trim() ?? string.Empty;
                //string input = "$1,234.56";
                if (decimal.TryParse(value, NumberStyles.Currency, CultureInfo.GetCultureInfo("en-US"), out decimal moneyVal))
                {
                    Console.WriteLine($"Parsed currency: {moneyVal}");
                    patchOperations.Add(PatchOperation.Replace("/" + fieldName, moneyVal));
                }
            }
            else if (fieldType.Equals("Bool"))
            {
                int value = 0;
                if (formFields.ContainsKey(fieldName))
                {
                    string checkedValue = formFields[fieldName]?.Trim() ?? string.Empty;
                    if (checkedValue.Equals("on"))
                    {
                        value = 1;
                    }
                }
                patchOperations.Add(PatchOperation.Replace("/" + fieldName, value));
            }
        }
        else if (operationType.Equals("Add", StringComparison.OrdinalIgnoreCase))
        {
            //string value = formFields[fieldName]?.Trim() ?? string.Empty;
            //patchOperations.Add(PatchOperation.Add("/" + fieldName, value));

            if (fieldType.Equals("Text"))
            {
                if (formFields.ContainsKey(fieldName))
                {
                    string value = formFields[fieldName]?.Trim() ?? string.Empty;
                    patchOperations.Add(PatchOperation.Add("/" + fieldName, value));
                }
            }
            else if (fieldType.Equals("Int"))
            {
                if (formFields.ContainsKey(fieldName))
                {
                    string value = formFields[fieldName]?.Trim() ?? string.Empty;
                    patchOperations.Add(PatchOperation.Add("/" + fieldName, int.Parse(value)));
                }
            }
            else if (fieldType.Equals("Bool"))
            {
                int value = 0;
                if (formFields.ContainsKey(fieldName))
                {
                    string checkedValue = formFields[fieldName]?.Trim() ?? string.Empty;
                    if (checkedValue.Equals("on"))
                    {
                        value = 1;
                    }
                }
                patchOperations.Add(PatchOperation.Add("/" + fieldName, value));
            }
        }
        else if (operationType.Equals("Remove", StringComparison.OrdinalIgnoreCase))
        {
            patchOperations.Add(PatchOperation.Remove("/" + fieldName));
        }
    }


    public T GetFieldValue<T>(Dictionary<string, string> formFields, string fieldName, T defaultValue = default)
    {
        if (formFields == null || string.IsNullOrWhiteSpace(fieldName))
            return defaultValue;

        if (formFields.TryGetValue(fieldName, out string rawValue))
        {
            try
            {
                if (typeof(T) == typeof(bool))
                {
                    object boolValue = rawValue.Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
                    return (T)boolValue;
                }
                else
                {
                    return (T)Convert.ChangeType(rawValue.Trim(), typeof(T));
                }
            }
            catch
            {
                // Optionally log the error here
                return defaultValue;
            }
        }

        return defaultValue;
    }

    public int GetFieldValueBool(Dictionary<string, string> formFields, string fieldName)
    {
        int value = 0;
        if (formFields == null || string.IsNullOrWhiteSpace(fieldName))
            return value; // Prevent potential null reference errors

        if (formFields.ContainsKey(fieldName))
        {
            string checkedValue = formFields[fieldName]?.Trim() ?? string.Empty;
            if (checkedValue.Equals("on"))
            {
                value = 1;
            }
        }
        return value;
    }
    public decimal GetFieldValueMoney(Dictionary<string, string> formFields, string fieldName)
    {
        decimal value = 0.00m;
        if (formFields == null || string.IsNullOrWhiteSpace(fieldName))
            return value; // Prevent potential null reference errors

        if (formFields.ContainsKey(fieldName))
        {
            string rawValue = formFields[fieldName]?.Trim() ?? string.Empty;
            //string input = "$1,234.56";
            if (decimal.TryParse(rawValue, NumberStyles.Currency, CultureInfo.GetCultureInfo("en-US"), out decimal moneyVal))
            {
                //Console.WriteLine($"Parsed currency: {moneyVal}");
            }
            value = moneyVal;
        }
        return value;
    }


    public async Task UpdatePropertyDB(string userName, Dictionary<string, string> formFields)
    {
        DateTime currDateTime = DateTime.Now;
        string LastChangedTs = currDateTime.ToString("o");

        //------------------------------------------------------------------------------------------------------------------
        // Query the NoSQL container to get values
        //------------------------------------------------------------------------------------------------------------------
        string databaseId = "hoadb";
        string containerId = "hoa_properties";
        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container container = db.GetContainer(containerId);

        //foreach (var field in formFields)
        //{
        //    log.LogWarning($">>> in DB, Field {field.Key}: {field.Value}");
        //}
        string parcelId = formFields["Parcel_ID"].Trim();

        // Initialize a list of PatchOperation (and default to setting the mandatory LastChanged fields)
        List<PatchOperation> patchOperations = new List<PatchOperation>
            {
                PatchOperation.Replace("/LastChangedBy", userName),
                PatchOperation.Replace("/LastChangedTs", LastChangedTs)
            };

        //AddPatchField(patchOperations, formFields, "UseEmail", "Bool");
        AddPatchField(patchOperations, formFields, "Comments");

        // Convert the list to an array
        PatchOperation[] patchArray = patchOperations.ToArray();

        ItemResponse<dynamic> response = await container.PatchItemAsync<dynamic>(
            parcelId,
            new PartitionKey(parcelId),
            patchArray
        );
    }


    public async Task<hoa_owners> UpdateOwnerDB(string userName, Dictionary<string, string> formFields)
    {
        DateTime currDateTime = DateTime.Now;
        string LastChangedTs = currDateTime.ToString("o");
        hoa_owners ownerRec = null;

        //------------------------------------------------------------------------------------------------------------------
        // Query the NoSQL container to get values
        //------------------------------------------------------------------------------------------------------------------
        string databaseId = "hoadb";
        string containerId = "hoa_owners";
        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container container = db.GetContainer(containerId);

        string parcelId = formFields["Parcel_ID"].Trim();
        string ownerId = formFields["OwnerID"].Trim();

        // Initialize a list of PatchOperation
        List<PatchOperation> patchOperations = new List<PatchOperation>
            {
                PatchOperation.Replace("/LastChangedBy", userName),
                PatchOperation.Replace("/LastChangedTs", LastChangedTs)
            };

        //AddPatchField(patchOperations, formFields, "CurrentOwner", "Bool");
        AddPatchField(patchOperations, formFields, "Owner_Name1");
        AddPatchField(patchOperations, formFields, "Owner_Name2");
        AddPatchField(patchOperations, formFields, "DatePurchased");
        AddPatchField(patchOperations, formFields, "Mailing_Name");
        AddPatchField(patchOperations, formFields, "Owner_Phone");
        AddPatchField(patchOperations, formFields, "EmailAddr");
        AddPatchField(patchOperations, formFields, "EmailAddr2");
        AddPatchField(patchOperations, formFields, "Comments");

        // Convert the list to an array
        PatchOperation[] patchArray = patchOperations.ToArray();

        ItemResponse<dynamic> response = await container.PatchItemAsync<dynamic>(
            ownerId,
            new PartitionKey(parcelId),
            patchArray
        );

        //-----------------------------------------------------------------------------------            
        // 2nd set of updates
        patchOperations = new List<PatchOperation>
            {
                PatchOperation.Replace("/LastChangedBy", userName),
                PatchOperation.Replace("/LastChangedTs", LastChangedTs)
            };

        AddPatchField(patchOperations, formFields, "AlternateMailing", "Bool");
        AddPatchField(patchOperations, formFields, "Alt_Address_Line1");
        AddPatchField(patchOperations, formFields, "Alt_Address_Line2");
        AddPatchField(patchOperations, formFields, "Alt_City");
        AddPatchField(patchOperations, formFields, "Alt_State");
        AddPatchField(patchOperations, formFields, "Alt_Zip");

        patchArray = patchOperations.ToArray();

        response = await container.PatchItemAsync<dynamic>(
            ownerId,
            new PartitionKey(parcelId),
            patchArray
        );

        // Get the updated owner record for the return value (for display in UI)
        containerId = "hoa_owners";
        Container ownersContainer = db.GetContainer(containerId);
        var queryDefinition = new QueryDefinition(
            "SELECT * FROM c WHERE c.id = @ownerId AND c.Parcel_ID = @parcelId ")
            .WithParameter("@ownerId", ownerId)
            .WithParameter("@parcelId", parcelId);
        var ownersFeed = ownersContainer.GetItemQueryIterator<hoa_owners>(queryDefinition);
        while (ownersFeed.HasMoreResults)
        {
            var ownersResponse = await ownersFeed.ReadNextAsync();
            foreach (var item in ownersResponse)
            {
                ownerRec = item;
            }
        }

        // if current owner, update the OWNER fields in the hoa_properties record
        if (ownerRec.CurrentOwner == 1)
        {
            containerId = "hoa_properties";
            container = db.GetContainer(containerId);

            // Initialize a list of PatchOperation (and default to setting the mandatory LastChanged fields)
            patchOperations = new List<PatchOperation>
            {
            };

            AddPatchField(patchOperations, formFields, "Owner_Name1");
            AddPatchField(patchOperations, formFields, "Owner_Name2");
            AddPatchField(patchOperations, formFields, "Mailing_Name");
            AddPatchField(patchOperations, formFields, "Owner_Phone");
            AddPatchField(patchOperations, formFields, "Alt_Address_Line1");

            // Convert the list to an array
            patchArray = patchOperations.ToArray();

            response = await container.PatchItemAsync<dynamic>(
                parcelId,
                new PartitionKey(parcelId),
                patchArray
            );
        }

        return ownerRec;
    }

    public async Task<hoa_owners> NewOwnerDB(string userName, Dictionary<string, string> formFields)
    {
        DateTime currDateTime = DateTime.Now;
        string LastChangedTs = currDateTime.ToString("o");
        hoa_owners ownerRec = null;

        string parcelId = formFields["Parcel_ID"].Trim();
        //string ownerId = formFields["OwnerID"].Trim();

        //------------------------------------------------------------------------------------------------------------------
        // Query the NoSQL container to get values
        //------------------------------------------------------------------------------------------------------------------
        string databaseId = "hoadb";
        string containerId = "hoa_owners";
        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container container = db.GetContainer(containerId);
        // Get the current owner record for this parcel
        var queryDefinition = new QueryDefinition(
            "SELECT * FROM c WHERE c.Parcel_ID = @parcelId AND c.CurrentOwner = 1 ")
            .WithParameter("@parcelId", parcelId);
        var ownersFeed = container.GetItemQueryIterator<hoa_owners>(queryDefinition);
        while (ownersFeed.HasMoreResults)
        {
            var ownersResponse = await ownersFeed.ReadNextAsync();
            foreach (var item in ownersResponse)
            {
                ownerRec = item;
            }
        }
        if (ownerRec == null)
        {
            throw new Exception("Current owner not found for parcel: " + parcelId);
        }

        int maxOwnerID = 0;
        queryDefinition = new QueryDefinition("SELECT TOP 1 * FROM c ORDER BY c.OwnerID DESC ");
        var feed = container.GetItemQueryIterator<hoa_owners>(queryDefinition);
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            foreach (var item in response)
            {
                maxOwnerID = item.OwnerID;
            }
        }

        // Set the current owner "off" on the previous owner record
        // Initialize a list of PatchOperation
        List<PatchOperation> patchOperations = new List<PatchOperation>
            {
                PatchOperation.Replace("/CurrentOwner", 0)
            };
        // Convert the list to an array
        PatchOperation[] patchArray = patchOperations.ToArray();
        ItemResponse<dynamic> response2 = await container.PatchItemAsync<dynamic>(
            ownerRec.OwnerID.ToString(),
            new PartitionKey(parcelId),
            patchArray
        );

        // Overwrite values from the current owner record with values from the form
        ownerRec.OwnerID = maxOwnerID + 1; // Increment to get a new id
        ownerRec.id = ownerRec.OwnerID.ToString(); // Set the id to the new OwnerID
        ownerRec.CurrentOwner = 1;  // Make new owner the current owner
        ownerRec.Owner_Name1 = GetFieldValue<string>(formFields, "Owner_Name1", "");
        ownerRec.Owner_Name2 = GetFieldValue<string>(formFields, "Owner_Name2", "");
        ownerRec.DatePurchased = GetFieldValue<string>(formFields, "DatePurchased", "");
        ownerRec.Mailing_Name = GetFieldValue<string>(formFields, "Mailing_Name", "");
        ownerRec.Owner_Phone = GetFieldValue<string>(formFields, "Owner_Phone", "");
        ownerRec.EmailAddr = GetFieldValue<string>(formFields, "EmailAddr", "");
        ownerRec.EmailAddr2 = GetFieldValue<string>(formFields, "EmailAddr2", "");
        ownerRec.Comments = GetFieldValue<string>(formFields, "Comments", "");
        ownerRec.AlternateMailing = GetFieldValueBool(formFields, "AlternateMailing");
        ownerRec.Alt_Address_Line1 = GetFieldValue<string>(formFields, "Alt_Address_Line1", "");
        ownerRec.Alt_Address_Line2 = GetFieldValue<string>(formFields, "Alt_Address_Line2", "");
        ownerRec.Alt_City = GetFieldValue<string>(formFields, "Alt_City", "");
        ownerRec.Alt_State = GetFieldValue<string>(formFields, "Alt_State", "");
        ownerRec.Alt_Zip = GetFieldValue<string>(formFields, "Alt_Zip", "");
        ownerRec.LastChangedBy = userName;
        ownerRec.LastChangedTs = currDateTime;
        await container.CreateItemAsync(ownerRec, new PartitionKey(parcelId));


        // if current owner, update the OWNER fields in the hoa_properties record
        containerId = "hoa_properties";
        container = db.GetContainer(containerId);
        // Initialize a list of PatchOperation (and default to setting the mandatory LastChanged fields)
        List<PatchOperation> patchOperations3 = new List<PatchOperation>
            {
                PatchOperation.Replace("/OwnerID", ownerRec.OwnerID)
            };
        AddPatchField(patchOperations3, formFields, "Owner_Name1");
        AddPatchField(patchOperations3, formFields, "Owner_Name2");
        AddPatchField(patchOperations3, formFields, "Mailing_Name");
        AddPatchField(patchOperations3, formFields, "Owner_Phone");
        AddPatchField(patchOperations3, formFields, "Alt_Address_Line1");
        // Convert the list to an array
        PatchOperation[] patchArray3 = patchOperations3.ToArray();
        ItemResponse<dynamic> response3 = await container.PatchItemAsync<dynamic>(
            parcelId,
            new PartitionKey(parcelId),
            patchArray3
        );


        // Update any ProcessedFlag not set to "Y" in the sales records for this parcel (assuming a new owner means the sales record is now processed)
        containerId = "hoa_sales";
        container = db.GetContainer(containerId);
        var queryDefinition4 = new QueryDefinition(
            "SELECT * FROM c WHERE c.PARID = @parcelId AND c.ProcessedFlag != @processedFlag ")
            .WithParameter("@parcelId", parcelId)
            .WithParameter("@processedFlag", "Y");
        var salesFeed = container.GetItemQueryIterator<hoa_sales>(queryDefinition4);
        while (salesFeed.HasMoreResults)
        {
            var salesResponse = await salesFeed.ReadNextAsync();
            foreach (var item in salesResponse)
            {
                item.ProcessedFlag = "Y";
                await container.ReplaceItemAsync(item, item.id, new PartitionKey(item.SALEDT));
            }
        }

        return ownerRec;
    }

    public async Task<hoa_assessments> UpdateAssessmentDB(string userName, Dictionary<string, string> formFields)
    {
        DateTime currDateTime = DateTime.Now;
        string LastChangedTs = currDateTime.ToString("o");
        hoa_assessments assessmentRec = new hoa_assessments();

        //------------------------------------------------------------------------------------------------------------------
        // Query the NoSQL container to get values
        //------------------------------------------------------------------------------------------------------------------
        string databaseId = "hoadb";
        string containerId = "hoa_assessments";
        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container container = db.GetContainer(containerId);

        string parcelId = formFields["Parcel_ID"].Trim();
        string assessmentId = formFields["AssessmentId"].Trim();

        assessmentRec = await container.ReadItemAsync<hoa_assessments>(assessmentId, new PartitionKey(parcelId));

        assessmentRec.OwnerID = GetFieldValue<int>(formFields, "OwnerID", assessmentRec.OwnerID);
        assessmentRec.DuesAmt = GetFieldValueMoney(formFields, "DuesAmt").ToString("");
        //assessmentRec.DateDue = GetFieldValue<string>(formFields, "DateDue");  // Can't change this in update
        assessmentRec.Paid = GetFieldValueBool(formFields, "Paid");
        assessmentRec.NonCollectible = GetFieldValueBool(formFields, "NonCollectible");
        assessmentRec.DatePaid = GetFieldValue<string>(formFields, "DatePaid");
        assessmentRec.PaymentMethod = GetFieldValue<string>(formFields, "PaymentMethod");
        assessmentRec.Lien = GetFieldValueBool(formFields, "Lien");
        assessmentRec.LienRefNo = GetFieldValue<string>(formFields, "LienRefNo");
        assessmentRec.DateFiled = GetFieldValue<DateTime>(formFields, "DateFiled");
        assessmentRec.Disposition = GetFieldValue<string>(formFields, "Disposition");
        assessmentRec.FilingFee = GetFieldValueMoney(formFields, "FilingFee");
        assessmentRec.ReleaseFee = GetFieldValueMoney(formFields, "ReleaseFee");
        assessmentRec.DateReleased = GetFieldValue<DateTime>(formFields, "DateReleased");
        assessmentRec.LienDatePaid = GetFieldValue<DateTime>(formFields, "LienDatePaid");
        assessmentRec.AmountPaid = GetFieldValueMoney(formFields, "AmountPaid");
        assessmentRec.StopInterestCalc = GetFieldValueBool(formFields, "StopInterestCalc");
        //assessmentRec.FilingFeeInterest = GetFieldValueMoney(formFields, "FilingFeeInterest");
        assessmentRec.AssessmentInterest = GetFieldValueMoney(formFields, "AssessmentInterest");
        assessmentRec.InterestNotPaid = GetFieldValueBool(formFields, "InterestNotPaid");
        assessmentRec.BankFee = GetFieldValueMoney(formFields, "BankFee");
        assessmentRec.LienComment = GetFieldValue<string>(formFields, "LienComment");
        assessmentRec.Comments = GetFieldValue<string>(formFields, "Comments");
        assessmentRec.LastChangedBy = userName;
        assessmentRec.LastChangedTs = currDateTime;

        await container.ReplaceItemAsync(assessmentRec, assessmentRec.id, new PartitionKey(assessmentRec.Parcel_ID));

        return assessmentRec;
    }

    // Bulk add assessments for all properties for a given FiscalYear and DuesAmt
    public async Task<int> AddAssessmentsBulk(string userName, int fiscalYear, decimal duesAmt)
    {
        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container propContainer = db.GetContainer("hoa_properties");
        Container assessContainer = db.GetContainer("hoa_assessments");

        // Get all properties
        List<hoa_properties> propList = new List<hoa_properties>();
        var propQuery = new QueryDefinition("SELECT * FROM c ORDER BY c.Parcel_ID");
        var propFeed = propContainer.GetItemQueryIterator<hoa_properties>(propQuery);
        while (propFeed.HasMoreResults)
        {
            var response = await propFeed.ReadNextAsync();
            foreach (var item in response)
            {
                propList.Add(item);
            }
        }

        int count = 0;
        DateTime now = DateTime.Now;
        foreach (var prop in propList)
        {
            // Check if assessment already exists for this property and year
            var checkQuery = new QueryDefinition("SELECT * FROM c WHERE c.Parcel_ID = @parcelId AND c.FY = @fy")
                .WithParameter("@parcelId", prop.Parcel_ID)
                .WithParameter("@fy", fiscalYear);
            var checkFeed = assessContainer.GetItemQueryIterator<hoa_assessments>(checkQuery);
            bool exists = false;
            while (checkFeed.HasMoreResults)
            {
                var checkResp = await checkFeed.ReadNextAsync();
                if (checkResp.Count > 0)
                {
                    exists = true;
                    break;
                }
            }
            if (exists) continue;
            // >>>>> Maybe do an update/replace with new amt??? or just an Upsert

            DateTime currTs = now;
            // Create new assessment
            var assessment = new hoa_assessments
            {
                //id = Guid.NewGuid().ToString(),
                id = prop.OwnerID.ToString() + fiscalYear.ToString(),
                Parcel_ID = prop.Parcel_ID,
                FY = fiscalYear,
                OwnerID = prop.OwnerID,
                DuesAmt = duesAmt.ToString("F2"),
                DateDue = new DateTime(fiscalYear - 1, 10, 1).ToString("yyyy-MM-dd"),
                Paid = 0,
                NonCollectible = 0,
                DatePaid = "",
                PaymentMethod = "",
                Lien = 0,
                LienRefNo = "",
                FilingFee = 0,
                ReleaseFee = 0,
                AmountPaid = 0,
                StopInterestCalc = 0,
                FilingFeeInterest = 0,
                AssessmentInterest = 0,
                InterestNotPaid = 0,
                BankFee = 0,
                LienComment = "",
                Comments = "",
                LastChangedBy = userName,
                LastChangedTs = currTs
            };
            await assessContainer.CreateItemAsync(assessment, new PartitionKey(assessment.Parcel_ID));
            count++;
        }
        return count;
    }

    // Process uploaded sales file and update hoa_sales container
    public async Task<string> ProcessSalesUploadDB(string userName, Stream fileStream, string fileName)
    {
        if (fileStream == null || string.IsNullOrEmpty(fileName))
        {
            return "No file uploaded - file is empty or name is not set";
        }

        fileStream.Position = 0;
        Stream csvStream = null;
        if (fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            csvStream = fileStream;
        }
        else if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // Try to extract the first .csv file from the zip
            using (var archive = new System.IO.Compression.ZipArchive(fileStream, System.IO.Compression.ZipArchiveMode.Read, true))
            {
                var csvEntry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
                if (csvEntry == null)
                    return "No CSV file found in ZIP.";
                csvStream = csvEntry.Open();
            }
        }
        else
        {
            return "File Error - Only CSV or ZIP files are supported.";
        }

        using var reader = new StreamReader(csvStream);
        var header = await reader.ReadLineAsync();
        if (string.IsNullOrEmpty(header))
        {
            return "File Error - Empty file.";
        }

        var cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        var db = cosmosClient.GetDatabase(databaseId);
        var salesContainer = db.GetContainer("hoa_sales");
        var propertyContainer = db.GetContainer("hoa_properties");

        DateTime currDateTime = DateTime.Now;
        string LastChangedTs = currDateTime.ToString("o");
        string parcelId = "";
        string saleDate = "";
        bool exists;
        string line;
        int fileCnt = 0;
        int foundCnt = 0;
        int insertCnt = 0;
        // Loop through the lines in the CSV file
        while ((line = await reader.ReadLineAsync()) != null)
        {
            fileCnt++;
            //log.LogWarning($">>> Line {fileCnt}: {line}");
            var fields = line.Split(',');
            // Strip double quotes and trim each field
            for (int i = 0; i < fields.Length; i++)
            {
                fields[i] = fields[i].Trim().Trim('"');
            }
            parcelId = fields[0];
            saleDate = fields[2];

            // Skip parcels that are not part of the HOA (i.e., do not start with "R72617")
            if (!parcelId.StartsWith("R72617", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Check if the parcel exists in the hoa_properties container
            exists = false;
            var queryDefinition = new QueryDefinition(
                "SELECT * FROM c WHERE c.id = @parcelId ")
                    .WithParameter("@parcelId", parcelId);
            var checkFeed = propertyContainer.GetItemQueryIterator<hoa_properties>(queryDefinition);
            while (checkFeed.HasMoreResults)
            {
                var response = await checkFeed.ReadNextAsync();
                foreach (var item in response)
                {
                    exists = true;
                }
            }

            // Skip if the parcel is not found in the hoa_properties container
            if (!exists) continue;

            //log.LogWarning($">>> Line {fileCnt}: {line}");
            foundCnt++;

            // Check if the sales record already exists for this property and sale date
            var checkQuery = new QueryDefinition("SELECT * FROM c WHERE c.PARID = @parcelId AND c.SALEDT = @saleDate")
                .WithParameter("@parcelId", parcelId)
                .WithParameter("@saleDate", saleDate);

            var feed = salesContainer.GetItemQueryIterator<hoa_sales>(checkQuery);
            exists = false;
            while (feed.HasMoreResults)
            {
                var response = await feed.ReadNextAsync();
                foreach (var item in response)
                {
                    exists = true;
                }
            }

            // Skip the insert if the record already exists
            if (exists) continue;

            currDateTime = DateTime.Now;
            LastChangedTs = currDateTime.ToString("o");

            var salesRec = new Model.hoa_sales
            {
                id = parcelId,
                PARID = parcelId,
                CONVNUM = fields[1],
                SALEDT = saleDate,
                PRICE = fields[3],
                OLDOWN = fields[4],
                OWNERNAME1 = fields[5],
                PARCELLOCATION = fields[6],
                MAILINGNAME1 = fields[7],
                MAILINGNAME2 = fields[8],
                PADDR1 = fields[9],
                PADDR2 = fields[10],
                PADDR3 = fields[11],
                CreateTimestamp = LastChangedTs,
                NotificationFlag = "N",
                ProcessedFlag = "N",
                LastChangedBy = userName,
                LastChangedTs = currDateTime,
                WelcomeSent = "N"
            };

            await salesContainer.CreateItemAsync(salesRec, new PartitionKey(salesRec.SALEDT));
            insertCnt++;
        }

        return $"Sales records = {fileCnt}, found in HOA = {foundCnt}, NEW records inserted = {insertCnt}  (Check Sales Report)";
    }

    // Record PayPal payment in hoa_payments container
    public async Task RecordPayment(string parcelId, string fiscalYear, string transactionId,
                                    decimal totalAmount, decimal paymentAmt, decimal paymentFee, string paymentDate, string payerEmail, string payerName)
    {
        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container assessmentsContainer = db.GetContainer("hoa_assessments");
        Container paymentsContainer = db.GetContainer("hoa_payments");
        Container configContainer = db.GetContainer("hoa_config");
        DateTime currDateTime = DateTime.UtcNow;
        string LastChangedTs = currDateTime.ToString("o");
        var eventGridPublisherClient = new EventGridPublisherClient(
            new Uri(grhaSendEmailEventTopicEndpoint),
            new AzureKeyCredential(grhaSendEmailEventTopicKey)
        );

        //-------------------------------------------------------------------------------------------------------------------------------------------
        // 2026-03-17 JJK - Modified to handle multiple unpaid assessments
        // assuming that an online/electronic pay will always be for the full amount of all unpaid assessments
        //-------------------------------------------------------------------------------------------------------------------------------------------

        // First, get the Assessment record for this parcel and fiscal year to get the OwnerID (and double check info from payment source)
        /*
        var query = new QueryDefinition("SELECT * FROM c WHERE c.Parcel_ID = @parcelId AND c.FY = @fy")
            .WithParameter("@parcelId", parcelId)
            .WithParameter("@fy", int.Parse(fiscalYear));
        var feed = assessmentsContainer.GetItemQueryIterator<hoa_assessments>(query);
        bool exists = false;
        hoa_assessments assessmentRec = new hoa_assessments();
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            foreach (var item in response)
            {
                exists = true;
                assessmentRec = item;
            }
        }
        if (!exists)
        {
            throw new Exception("Assessment record not found for Parcel " + parcelId + ", FY = " + fiscalYear);
        }
        */

        // First, get the unpaid Assessment records for this parcel to get the OwnerID (and double check info from payment source)
        var query = new QueryDefinition("SELECT * FROM c WHERE c.Parcel_ID = @parcelId AND c.Paid != 1 AND c.NonCollectible != 1")
            .WithParameter("@parcelId", parcelId);
        var feed = assessmentsContainer.GetItemQueryIterator<hoa_assessments>(query);
        bool exists = false;
        hoa_assessments assessmentRec = new hoa_assessments();
        while (feed.HasMoreResults)
        {
            var response = await feed.ReadNextAsync();
            foreach (var item in response)
            {
                assessmentRec = item;

                if (!exists) {
                    // Record the payment in the hoa_payments container (only need to do this once even if multiple assessments)
                    var paymentRec = new Model.hoa_payments
                    {
                        id = transactionId,
                        Parcel_ID = parcelId,
                        OwnerID = assessmentRec.OwnerID,
                        FY = int.Parse(fiscalYear),
                        txn_id = transactionId,
                        payment_date = paymentDate,
                        payer_email = payerEmail,
                        payment_amt = paymentAmt,
                        payment_fee = paymentFee,
                        LastChangedTs = currDateTime,
                        paidEmailSent = "N"
                    };
                    await paymentsContainer.UpsertItemAsync(paymentRec, new PartitionKey(paymentRec.Parcel_ID));

                    // Only set the paypal transaction id in the Comments field of the assessment record for the first record - 
                    // this is just to have a reference to link the payment to the assessment(s) in case we need to research or troubleshoot later.  
                    // We don't want to overwrite any existing comments on subsequent records if there are multiple assessments.
                    assessmentRec.Comments = "TransId:" + transactionId;
                }

                exists = true;

                // Mark the assessment as paid and update relevant fields
                assessmentRec.Paid = 1;
                assessmentRec.DatePaid = paymentDate;
                assessmentRec.PaymentMethod = "Paypal";
                assessmentRec.LastChangedBy = "paypal";
                assessmentRec.LastChangedTs = currDateTime;
                await assessmentsContainer.ReplaceItemAsync(assessmentRec, assessmentRec.id, new PartitionKey(assessmentRec.Parcel_ID));
            }
        }
        if (!exists)
        {
            throw new Exception("Assessment record not found for Parcel " + parcelId + ", FY = " + fiscalYear);
        }

        // Queue up events to send payment confirmation emails to the payer and notification to treasurer
        string treasurerEmail = await getConfigVal(configContainer, "treasurerEmail");
        string paymentEmailList = await getConfigVal(configContainer, "paymentEmailList");
        string payorInfo = await getConfigVal(configContainer, "paymentEmailPayorInfo");
        string treasurerInfo = $"The following payment has been recorded and assessment(s) marked as PAID.  Payment fee was {paymentFee}";
        string paymentInfoStr = $"<br><br>Parcel Id: {parcelId}";
        paymentInfoStr += $"<br>Fiscal Year: {fiscalYear}";
        paymentInfoStr += $"<br>Transaction Id: {transactionId}";
        paymentInfoStr += $"<br>Payment Date: {paymentDate}";
        paymentInfoStr += $"<br>Payer Email: {payerEmail}";
        paymentInfoStr += $"<br>Payment Amount: {paymentAmt} (this includes the Paypal processing fee) <br>";

        // Create an object to send data values to the send email event
        DuesEmailEvent duesEmailEvent = new DuesEmailEvent();

        duesEmailEvent.id = transactionId;
        duesEmailEvent.parcelId = parcelId;
        duesEmailEvent.hoaName = await getConfigVal(configContainer, "hoaName");
        duesEmailEvent.hoaNameShort = await getConfigVal(configContainer, "hoaNameShort");

        duesEmailEvent.hoaAddress1 = await getConfigVal(configContainer, "hoaAddress1");
        duesEmailEvent.hoaAddress2 = await getConfigVal(configContainer, "hoaAddress2");
        duesEmailEvent.helpNotes = await getConfigVal(configContainer, "duesNotes");
        duesEmailEvent.duesUrl = await getConfigVal(configContainer, "duesUrl");

        duesEmailEvent.mailType = "Payment";

        duesEmailEvent.emailAddr = payerEmail;
        duesEmailEvent.mailSubject = "GRHA Payment Confirmation";
        duesEmailEvent.htmlMessage = "<h4>GRHA Payment Confirmation</h4>" + payorInfo + paymentInfoStr;
        
        await eventGridPublisherClient.SendEventAsync(
            new EventGridEvent(
                subject: "DuesEmailRequest",
                eventType: "SendMail",
                dataVersion: "1.0",
                data: BinaryData.FromObjectAsJson(duesEmailEvent)));

        duesEmailEvent.emailAddr = treasurerEmail;
        duesEmailEvent.mailSubject = "GRHA Payment Notification";
        duesEmailEvent.htmlMessage = "<h4>GRHA Payment Notification</h4>" + treasurerInfo + paymentInfoStr;
        await eventGridPublisherClient.SendEventAsync(
            new EventGridEvent(
                subject: "DuesEmailRequest",
                eventType: "SendMail",
                dataVersion: "1.0",
                data: BinaryData.FromObjectAsJson(duesEmailEvent)));

        if (!string.IsNullOrEmpty(paymentEmailList)) {
            duesEmailEvent.emailAddr = paymentEmailList;
            await eventGridPublisherClient.SendEventAsync(
                new EventGridEvent(
                    subject: "DuesEmailRequest",
                    eventType: "SendMail",
                    dataVersion: "1.0",
                    data: BinaryData.FromObjectAsJson(duesEmailEvent)));
        }
    }

    // Query Cosmos DB for MediaInfo records based on paramData
    public async Task<List<MediaInfo>> GetMediaInfoDB(Dictionary<string, object> paramData)
    {
        string databaseId = "hoadb";
        string containerId = "MediaInfoDoc";
        CosmosClient cosmosClient = new CosmosClient(apiCosmosDbConnStr);
        Database db = cosmosClient.GetDatabase(databaseId);
        Container container = db.GetContainer(containerId);

        //log.LogWarning("-------------------------------------------------------------------------------------------------------------------------------------------");
        //log.LogWarning($">>> GetMediaInfoDB paramData: {Newtonsoft.Json.JsonConvert.SerializeObject(paramData)}");

        //int mediaTypeId = paramData.ContainsKey("MediaTypeId") ? Convert.ToInt32(paramData["MediaTypeId"]) : 1;
        int mediaTypeId = paramData.ContainsKey("MediaFilterMediaType") ? Convert.ToInt32(paramData["MediaFilterMediaType"]) : 1;
        string category = paramData.ContainsKey("MediaFilterCategory") ? (paramData["MediaFilterCategory"]?.ToString() ?? "") : "";
        string startDate = paramData.ContainsKey("MediaFilterStartDate") ? (paramData["MediaFilterStartDate"]?.ToString() ?? "") : "";
        int maxRows = paramData.ContainsKey("maxRows") ? Convert.ToInt32(paramData["maxRows"]) : 300;

            //log.LogWarning($">>> Filter params: MediaTypeId: {mediaTypeId}, Category: {category}, StartDate: {startDate}, maxRows: {maxRows}");
            // Request options: MaxItemCount controls page size (not total rows)
            QueryRequestOptions queryRequestOptions = new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(mediaTypeId),
                MaxItemCount = maxRows // Page Count - each page will return up to rowMax items
            };

            // Build SQL query
            string sql = "SELECT TOP @maxRows * FROM c WHERE c.MediaTypeId = @mediaTypeId";
            if (!string.IsNullOrEmpty(category) && category != "ALL" && category != "0")
            {
                sql += " AND CONTAINS(c.CategoryTags, @category)";
                //log.LogWarning($">>> Adding category filter: {category}");
            }
            if (!string.IsNullOrEmpty(startDate))
            {
                // Expecting startDate as yyyy-MM-dd or yyyy-MM-ddTHH:mm:ss
                if (DateTime.TryParse(startDate, out DateTime dt))
                {
                    long dtVal = long.Parse(dt.ToString("yyyyMMddHH"));
                    sql += " AND c.MediaDateTimeVal >= @startDateVal";
                    //log.LogWarning($">>> Adding startDate filter: {startDate} ({dtVal})");
                }
            }
            sql += " ORDER BY c.MediaDateTimeVal DESC ";
            //log.LogWarning($"*** maxRows: {maxRows}, SQL: {sql}");

            var queryDef = new QueryDefinition(sql)
                .WithParameter("@maxRows", maxRows)
                .WithParameter("@mediaTypeId", mediaTypeId);

            if (!string.IsNullOrEmpty(category) && category != "ALL" && category != "0")
            {
                queryDef = queryDef.WithParameter("@category", category);
            }
            if (!string.IsNullOrEmpty(startDate) && DateTime.TryParse(startDate, out DateTime dt2))
            {
                long dtVal = long.Parse(dt2.ToString("yyyyMMddHH"));
                queryDef = queryDef.WithParameter("@startDateVal", dtVal);
            }

            var mediaInfoList = new List<MediaInfo>();
            var feed = container.GetItemQueryIterator<MediaInfo>(
                queryDef,
                requestOptions: queryRequestOptions);

            int pageCnt = 0;
            int rowCnt = 0;
            while (feed.HasMoreResults)
            {
                pageCnt++;
                //log.LogWarning($"------- Reading page {pageCnt} ...");
                var response = await feed.ReadNextAsync();
                foreach (var item in response)
                {
                    rowCnt++;
                    mediaInfoList.Add(item);
                    //log.LogWarning($">>> {rowCnt} {item.Name}, MediaDateTime: {item.MediaDateTime}, CategoryTags: {item.CategoryTags}");    
                }
            }

        return mediaInfoList;
    }


} // public class HoaDbCommon
} // namespace grhaWebFunctions

