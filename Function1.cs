using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace FunctionApp1;

public class Function1(ILogger<Function1> _logger)
{
    private static readonly HttpClient httpClient = new();

    [Function("FunctionAPIResponse")]
    public IActionResult RunApiResponse([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
    {
        _logger.LogInformation("C# HTTP trigger function processed a request.");
        return new OkObjectResult("Welcome to Azure Functions!");
    }

    [Function("FunctionCallback")]
    public async Task<IActionResult> RunCallback([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
    {
        _logger.LogInformation("C# HTTP trigger function processed a request.");

        // Read the request body for header information
        string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
        _logger.LogInformation($"Request body: {requestBody}");

        // Check if we need to handle callback
        var planUrl = req.Headers["PlanUrl"].ToString();
        var projectId = req.Headers["ProjectId"].ToString();
        var hubName = req.Headers["HubName"].ToString();
        var planId = req.Headers["PlanId"].ToString();
        var jobId = req.Headers["JobId"].ToString();
        var timelineId = req.Headers["TimelineId"].ToString();
        var taskInstanceId = req.Headers["TaskInstanceId"].ToString();
        var authToken = req.Headers["AuthToken"].ToString();

        // If all the required headers are present, we're in callback mode
        if (!string.IsNullOrEmpty(planUrl) &&
            !string.IsNullOrEmpty(taskInstanceId) &&
            !string.IsNullOrEmpty(authToken))
        {
            _logger.LogInformation("Starting callback process...");

            // Simulate some work
            await Task.Delay(3000); // 3 seconds delay to simulate work

            // Call back to update the task status
            await ProcessTaskCallbackAsync(planUrl, projectId, hubName, planId, jobId, timelineId, taskInstanceId, authToken);

            return new OkObjectResult(new
            {
                message = "Function received successfully. Processing in background. Will update task when complete."
            });
        }

        // Normal synchronous response
        return new OkObjectResult("Welcome to Azure Functions!");
    }

    private async Task ProcessTaskCallbackAsync(
        string planUrl, string projectId, string hubName,
        string planId, string jobId, string timelineId,
        string taskInstanceId, string authToken)
    {
        try
        {
            _logger.LogInformation($"Processing callback for task: {taskInstanceId}");

            // 1. First, send task started event
            await SendTaskStartedEventAsync(planUrl, projectId, hubName, planId, jobId, taskInstanceId, authToken);

            // 2. Send a log feed indicating we're starting
            await SendTaskLogFeedAsync(planUrl, projectId, hubName, planId, jobId, timelineId, "Function processing started", authToken);

            // 3. Do your work
            _logger.LogInformation("Performing function work...");
            for (int i = 0; i < 3; i++)
            {
                await SendTaskLogFeedAsync(planUrl, projectId, hubName, planId, jobId, timelineId, $"Processing step {i + 1}/3", authToken);
                await Task.Delay(1000); // Simulate work
            }

            // 4. Set the callback variable (optional but useful for backward compatibility)
            await SetCallbackVariableAsync(planUrl, projectId, hubName, planId, timelineId, taskInstanceId, jobId, authToken);

            // 5. Send task completed event (this is the critical part)
            await SendTaskCompletedEventAsync(planUrl, projectId, hubName, planId, jobId, taskInstanceId, "succeeded", authToken);

            // 6. Send a final log message
            await SendTaskLogFeedAsync(planUrl, projectId, hubName, planId, jobId, timelineId, "Function processing completed", authToken);

            _logger.LogInformation("Task callback processing finished successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error during callback processing: {ex.Message}");
            _logger.LogError(ex.StackTrace);

            // If there's an error, try to mark the task as failed
            try
            {
                await SendTaskCompletedEventAsync(planUrl, projectId, hubName, planId, jobId, taskInstanceId, "failed", authToken);
                await SendTaskLogFeedAsync(planUrl, projectId, hubName, planId, jobId, timelineId, $"Error: {ex.Message}", authToken);
            }
            catch
            {
                // Just log any errors from the failure notification
                _logger.LogError("Failed to send task failure notification");
            }
        }
    }

    private async Task SendTaskStartedEventAsync(
        string planUrl, string projectId, string hubName,
        string planId, string jobId, string taskInstanceId,
        string authToken)
    {
        _logger.LogInformation($"Sending TaskStarted event for task: {taskInstanceId}");

        // Task Event URL: {planUri}/{projectId}/_apis/distributedtask/hubs/{hubName}/plans/{planId}/events?api-version=2.0-preview.1
        string taskEventUrl = $"{planUrl.TrimEnd('/')}/{projectId}/_apis/distributedtask/hubs/{hubName}/plans/{planId}/events?api-version=2.0-preview.1";

        // Create the event payload
        var requestBodyJObject = new JObject(
            new JProperty("name", "TaskStarted"),
            new JProperty("jobId", jobId),
            new JProperty("taskId", taskInstanceId)
        );

        string requestBody = JsonConvert.SerializeObject(requestBodyJObject);
        _logger.LogInformation($"TaskStarted event payload: {requestBody}");

        await PostDataAsync(taskEventUrl, requestBody, authToken);
    }

    private async Task SendTaskCompletedEventAsync(
        string planUrl, string projectId, string hubName,
        string planId, string jobId, string taskInstanceId,
        string result, string authToken)
    {
        _logger.LogInformation($"Sending TaskCompleted event for task: {taskInstanceId}, result: {result}");

        // Task Event URL: {planUri}/{projectId}/_apis/distributedtask/hubs/{hubName}/plans/{planId}/events?api-version=2.0-preview.1 
        string taskEventUrl = $"{planUrl.TrimEnd('/')}/{projectId}/_apis/distributedtask/hubs/{hubName}/plans/{planId}/events?api-version=2.0-preview.1";

        // Create the event payload
        var requestBodyJObject = new JObject(
            new JProperty("name", "TaskCompleted"),
            new JProperty("jobId", jobId),
            new JProperty("taskId", taskInstanceId),
            new JProperty("result", result)  // "succeeded" or "failed"
        );

        string requestBody = JsonConvert.SerializeObject(requestBodyJObject);
        _logger.LogInformation($"TaskCompleted event payload: {requestBody}");

        await PostDataAsync(taskEventUrl, requestBody, authToken);
    }

    private async Task SendTaskLogFeedAsync(
        string planUrl, string projectId, string hubName,
        string planId, string jobId, string timelineId,
        string message, string authToken)
    {
        _logger.LogInformation($"Sending log: {message}");

        // Task feed URL: {planUri}/{projectId}/_apis/distributedtask/hubs/{hubName}/plans/{planId}/timelines/{timelineId}/records/{jobId}/feed?api-version=4.1
        string taskFeedUrl = $"{planUrl.TrimEnd('/')}/{projectId}/_apis/distributedtask/hubs/{hubName}/plans/{planId}/timelines/{timelineId}/records/{jobId}/feed?api-version=4.1";

        // Create the log message with timestamp
        string formattedMessage = $"{DateTime.UtcNow:O} {message}";

        // Create the feed payload
        var requestBodyJObject = new JObject(
            new JProperty("value", new JArray(formattedMessage)),
            new JProperty("count", 1)
        );

        string requestBody = JsonConvert.SerializeObject(requestBodyJObject);
        await PostDataAsync(taskFeedUrl, requestBody, authToken);
    }

    private async Task SetCallbackVariableAsync(
        string planUrl, string projectId, string hubName,
        string planId, string timelineId, string taskInstanceId,
        string jobId, string authToken)
    {
        try
        {
            _logger.LogInformation($"Setting callback variable for task: {taskInstanceId}");

            // Format the callback data
            string callbackData = JsonConvert.SerializeObject(new
            {
                result = "succeeded",
                resultCode = JsonConvert.SerializeObject(new
                {
                    status = "success",
                    message = "Function completed successfully!"
                })
            });

            // First, get the existing timeline records
            string timelineRecordsUrl = $"{planUrl.TrimEnd('/')}/{projectId}/_apis/distributedtask/hubs/{hubName}/plans/{planId}/timelines/{timelineId}/records?api-version=4.1";
            var response = await GetDataAsync(timelineRecordsUrl, authToken);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                // Parse the response
                dynamic timelineRecordsData = JsonConvert.DeserializeObject(responseContent);

                // Find the job record and update it
                foreach (var record in timelineRecordsData.value)
                {
                    string recordId = record.id.ToString();
                    if (string.Equals(recordId, jobId, StringComparison.OrdinalIgnoreCase))
                    {
                        // Create a JObject for the job record
                        JObject timelineRecord = JObject.FromObject(record);

                        // Add or update the variables property
                        if (timelineRecord["variables"] == null)
                        {
                            timelineRecord["variables"] = new JObject();
                        }

                        // Set our callback variable
                        JObject variables = (JObject)timelineRecord["variables"];
                        string varName = $"AZURE_FUNCTION_CALLBACK_{taskInstanceId}";
                        variables[varName] = new JObject(
                            new JProperty("value", callbackData),
                            new JProperty("isSecret", false)
                        );

                        // Create the update payload
                        JArray updatedRecords = new JArray();
                        updatedRecords.Add(timelineRecord);

                        var requestBodyJObject = new JObject(
                            new JProperty("value", updatedRecords),
                            new JProperty("count", 1)
                        );

                        string requestBody = JsonConvert.SerializeObject(requestBodyJObject);

                        // Send the update
                        await PatchDataAsync(timelineRecordsUrl, requestBody, authToken);
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error setting callback variable: {ex.Message}");
            // Continue processing even if this fails
        }
    }

    private async Task<HttpResponseMessage> PostDataAsync(string url, string requestBody, string authToken)
    {
        var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        // Important: Use Basic authentication with empty username and auth token as password
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($":{authToken}"))
        );

        var response = await httpClient.PostAsync(url, content);
        if (!response.IsSuccessStatusCode)
        {
            string responseContent = await response.Content.ReadAsStringAsync();
            _logger.LogError($"API call failed: {url}, Status: {response.StatusCode}, Response: {responseContent}");
        }

        return response;
    }

    private async Task<HttpResponseMessage> PatchDataAsync(string url, string requestBody, string authToken)
    {
        var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        // Important: Use Basic authentication with empty username and auth token as password
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($":{authToken}"))
        );

        var request = new HttpRequestMessage(new HttpMethod("PATCH"), url)
        {
            Content = content
        };

        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            string responseContent = await response.Content.ReadAsStringAsync();
            _logger.LogError($"API call failed: {url}, Status: {response.StatusCode}, Response: {responseContent}");
        }

        return response;
    }

    private async Task<HttpResponseMessage> GetDataAsync(string url, string authToken)
    {
        // Important: Use Basic authentication with empty username and auth token as password
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($":{authToken}"))
        );

        var response = await httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            string responseContent = await response.Content.ReadAsStringAsync();
            _logger.LogError($"API call failed: {url}, Status: {response.StatusCode}, Response: {responseContent}");
        }

        return response;
    }
}