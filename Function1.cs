using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace DurableDemoFunction;

public static class Function1
{
    [Function(nameof(GreetingOrchestrator))]
    public static async Task<List<string>> GreetingOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        ILogger logger = context.CreateReplaySafeLogger(nameof(GreetingOrchestrator));
        logger.LogInformation("Starting greeting orchestrator.");
        var outputs = new List<string>();

        // Execute greeting activities for meaningful recipients
        outputs.Add(await context.CallActivityAsync<string>(nameof(CreateGreetingMessage), "Customer"));
        outputs.Add(await context.CallActivityAsync<string>(nameof(CreateGreetingMessage), "SupportAgent"));

        // Ask for human approval before continuing
        logger.LogInformation("Requesting human approval to continue the orchestration.");
        // The orchestration will pause here and wait for an external event named "Approval".
        bool approved = await context.WaitForExternalEvent<bool>("Approval");

        if (approved)
        {
            logger.LogInformation("Approval granted. Continuing orchestration.");
            outputs.Add(await context.CallActivityAsync<string>(nameof(CreateGreetingMessage), "Manager"));
            outputs.Add("Orchestration continued after approval.");
        }
        else
        {
            logger.LogInformation("Approval denied. Aborting further steps.");
            outputs.Add("Orchestration stopped by human rejection.");
        }

        // returns a list describing what happened
        return outputs;
    }

    [Function(nameof(CreateGreetingMessage))]
    public static string CreateGreetingMessage([ActivityTrigger] string recipient, FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(CreateGreetingMessage));
        logger.LogInformation("Creating greeting for {recipient}.", recipient);
        return $"Hello {recipient}!";
    }

    [Function("StartGreetingOrchestration")]
    public static async Task<HttpResponseData> StartGreetingOrchestration(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(StartGreetingOrchestration));

        // Start the orchestrator
        string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(GreetingOrchestrator));

        logger.LogInformation("Started orchestration with ID = '{instanceId}'.", instanceId);

        // Returns an HTTP 202 response with an instance management payload.
        return await client.CreateCheckStatusResponseAsync(req, instanceId);
    }

    // Simple HTTP endpoint that a human (or UI) can call to signal approval or rejection.
    // Example:
    //  GET /api/ApproveGreeting?instanceId={id}&approve=true
    [Function("ApproveGreeting")]
    public static async Task<HttpResponseData> ApproveGreeting(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(ApproveGreeting));

        string? instanceId = GetQueryValue(req.Url, "instanceId");
        string? approveValue = GetQueryValue(req.Url, "approve");

        // If the caller posted JSON instead of query params, try to read JSON body:
        if (string.IsNullOrEmpty(instanceId) || string.IsNullOrEmpty(approveValue))
        {
            try
            {
                using var sr = new StreamReader(req.Body, Encoding.UTF8);
                string body = await sr.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(body))
                {
                    var doc = JsonDocument.Parse(body);
                    if (string.IsNullOrEmpty(instanceId) && doc.RootElement.TryGetProperty("instanceId", out var idProp))
                    {
                        instanceId = idProp.GetString();
                    }

                    if (string.IsNullOrEmpty(approveValue) && doc.RootElement.TryGetProperty("approve", out var approveProp))
                    {
                        approveValue = approveProp.GetRawText().Trim('"');
                    }
                }
            }
            catch (JsonException)
            {
                // ignore parse errors; will validate below
            }
        }

        var response = req.CreateResponse();

        if (string.IsNullOrEmpty(instanceId))
        {
            response.StatusCode = System.Net.HttpStatusCode.BadRequest;
            await response.WriteStringAsync("Missing required query parameter or body property: 'instanceId'.");
            return response;
        }

        if (!bool.TryParse(approveValue, out bool approved))
        {
            response.StatusCode = System.Net.HttpStatusCode.BadRequest;
            await response.WriteStringAsync("Missing or invalid 'approve' value. Use 'true' or 'false' in query or JSON body.");
            return response;
        }

        logger.LogInformation("Raising approval event for instance '{instanceId}' with approved={approved}.", instanceId, approved);

        // Raise the external event named "Approval" to the orchestration instance.
        await client.RaiseEventAsync(instanceId, "Approval", approved);

        response.StatusCode = System.Net.HttpStatusCode.OK;
        await response.WriteStringAsync($"Raised approval={approved} for instance '{instanceId}'.");
        return response;
    }

    // HTTP endpoint to return orchestration status for a given instanceId.
    // Example:
    //  GET /api/GetOrchestrationStatus?instanceId={id}
    [Function("GetOrchestrationStatus")]
    public static async Task<HttpResponseData> GetOrchestrationStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger(nameof(GetOrchestrationStatus));

        string? instanceId = GetQueryValue(req.Url, "instanceId");
        var response = req.CreateResponse();

        if (string.IsNullOrEmpty(instanceId))
        {
            response.StatusCode = System.Net.HttpStatusCode.BadRequest;
            await response.WriteStringAsync("Missing required query parameter: 'instanceId'.");
            return response;
        }

        try
        {
            // Get orchestration state from DurableTaskClient.
            // Returns null if instance not found.
            var state = await client.GetInstanceAsync(instanceId, getInputsAndOutputs: true);

            if (state is null)
            {
                response.StatusCode = System.Net.HttpStatusCode.NotFound;
                await response.WriteStringAsync($"Instance '{instanceId}' not found.");
                return response;
            }

            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            var options = new JsonSerializerOptions { WriteIndented = true };
            await response.WriteStringAsync(JsonSerializer.Serialize(state, options));
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching status for instance '{instanceId}'.", instanceId);
            response.StatusCode = System.Net.HttpStatusCode.InternalServerError;
            await response.WriteStringAsync($"Error fetching status: {ex.Message}");
            return response;
        }
    }

    // Shared helper moved to class scope so multiple endpoints can reuse it.
    private static string? GetQueryValue(Uri url, string key)
    {
        if (string.IsNullOrEmpty(url.Query))
        {
            return null;
        }

        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length >= 1 && Uri.UnescapeDataString(kv[0]).Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Length == 2 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
            }
        }

        return null;
    }
}