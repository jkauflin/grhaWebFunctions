/*==============================================================================
(C) Copyright 2024,2026 John J Kauflin, All rights reserved.
--------------------------------------------------------------------------------
DESCRIPTION:  Azure Function for sending emails using ACS - triggered by 
              messages published to an EventGrid
--------------------------------------------------------------------------------
Modification History
----------------------------------------------------------------------------------
2026-08-26 JJK  Migrating send email function to this new .net10 project
2026-08-27 JJK  Update logging for better information using ilogger parameters
================================================================================*/

using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

using grhaWebFunctions.Model;
using System.Threading.Tasks;
using Azure;

namespace grhaWebFunctions;

public class SendEmail
{
    private readonly ILogger<SendEmail> log;
    private readonly IConfiguration config;
    private readonly CommonUtil util;
    private readonly HoaDbCommon hoaDbCommon;

    public SendEmail(ILogger<SendEmail> logger, IConfiguration configuration)
    {
        log = logger;
        config = configuration;
        util = new CommonUtil(log);
        hoaDbCommon = new HoaDbCommon(log, config);
    }

    [Function("SendEmailTrigger2")]
    public async Task Run([EventGridTrigger] EventGridEvent eventGridEvent)
    {
        string returnMessage = "";
        string functionName = "SendEmailTrigger2";
        DuesEmailEvent duesEmailEvent = new DuesEmailEvent();
        try
        {
            log.LogInformation("Begin {functionName} function",functionName);
            /*
            log.LogInformation(
                "Starting {Operation} for {EntityId}. User: {UserId}",
                "SendEmail",
                hoaMemberId,
                userId);
            And later:
            log.LogInformation(
                "Completed {Operation} for {EntityId}. Status: {Status}, DurationMs: {DurationMs}",
                "SendEmail",
                hoaMemberId,
                "Success",
                stopwatch.ElapsedMilliseconds);
            The big advantage is that you can query Application Insights for things like:
            Operation == "SendEmail"
            */

            // De-serialize the JSON string from the Event into the DuesEmailEvent object
            duesEmailEvent = eventGridEvent.Data.ToObjectFromJson<DuesEmailEvent>();
            log.LogInformation(">>> duesEmailEvent = {duesEmailEvent}",duesEmailEvent);

            bool paymentEmail = false;
            if (!string.IsNullOrEmpty(duesEmailEvent.mailType))
            {
                if (duesEmailEvent.mailType.Equals("Payment"))
                {
                    paymentEmail = true;
                }
            }

            if (paymentEmail)
            {
                returnMessage = await hoaDbCommon.SendPaymentEmail(duesEmailEvent);
            }
            else
            {
                //returnMessage = await hoaDbCommon.SendEmailandUpdateRecs(duesEmailEvent);
            }

            log.LogInformation("returnMessage = {returnMessage}", returnMessage);
        }
        catch (Exception ex)
        {
            log.LogError(ex,"Error in {functionName} sending email to {emailAddress} for {eventType}, parcelId: {parcelId}, eventId: {eventId}, type: {mailType}",
                functionName,
                duesEmailEvent.emailAddr,
                eventGridEvent.EventType,
                duesEmailEvent.parcelId,
                duesEmailEvent.id,
                duesEmailEvent.mailType);
            throw new Exception(ex.Message);
        }

    }
}
