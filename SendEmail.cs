/*==============================================================================
(C) Copyright 2024,2026 John J Kauflin, All rights reserved.
--------------------------------------------------------------------------------
DESCRIPTION:  Azure Function for sending emails using ACS - triggered by 
              messages published to an EventGrid
--------------------------------------------------------------------------------
Modification History
----------------------------------------------------------------------------------
2026-08-26 JJK  Migrating send email function to this new .net10 project
================================================================================*/

using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

using grhaWebFunctions.Model;
using System.Threading.Tasks;

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
        DuesEmailEvent duesEmailEvent = new DuesEmailEvent();
        try
        {
            log.LogInformation("Begin SendEmailTrigger2 function");
            string returnMessage = "";
            // De-serialize the JSON string from the Event into the DuesEmailEvent object
            duesEmailEvent = eventGridEvent.Data.ToObjectFromJson<DuesEmailEvent>();
            log.LogWarning($">>> duesEmailEvent = {duesEmailEvent.ToString()}");

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
                //returnMessage = await hoaDbCommon.SendPaymentEmail(duesEmailEvent);
            }
            else
            {
                //returnMessage = await hoaDbCommon.SendEmailandUpdateRecs(duesEmailEvent);
            }

            log.LogWarning(returnMessage+", email = "+duesEmailEvent.emailAddr);
        }
        catch (Exception ex)
        {
            log.LogError("---------- DUES EMAIL FAILED ------------");
            log.LogError($">>> {eventGridEvent.EventType}, parcelId: {duesEmailEvent.parcelId}, id: {duesEmailEvent.id}, email: {duesEmailEvent.emailAddr}, type: {duesEmailEvent.mailType}");
            log.LogError($"Exception, message: {ex.Message} {ex.StackTrace}");
            throw new Exception(ex.Message);
        }

    }
}
