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
            await Task.Delay(2000); // 2 seconds delay to simulate work

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

            // 1. Send a log feed indicating we're starting
            await SendTaskLogFeedAsync(planUrl, projectId, hubName, planId, jobId, timelineId, "Function processing started", authToken);

            // 2. Do your work
            _logger.LogInformation("Performing function work...");
            for (int i = 0; i < 2; i++)
            {
                await SendTaskLogFeedAsync(planUrl, projectId, hubName, planId, jobId, timelineId, $"Processing step {i + 1}/2", authToken);
                await Task.Delay(1000); // Simulate work
            }

            // 3. Set the callback variable directly on the task instance
            string callbackData = JsonConvert.SerializeObject(new
            {
                result = "succeeded",
                resultCode = JsonConvert.SerializeObject(new
                {
                    status = "success",
                    message = "Function completed successfully!"
                })
            });

            // This is the variable the callback handler is looking for
            await SetTaskVariableAsync(
                planUrl, projectId, hubName, planId, timelineId, taskInstanceId,
                $"AZURE_FUNCTION_CALLBACK_{taskInstanceId}", callbackData, false, authToken);

            // Also set a system variable as a backup mechanism
            await SetTaskVariableAsync(
                planUrl, projectId, hubName, planId, timelineId, taskInstanceId,
                "SYSTEM_TASKISCOMPLETED", "true", false, authToken);

            // Set one more backup variable
            await SetTaskVariableAsync(
                planUrl, projectId, hubName, planId, timelineId, taskInstanceId,
                "VSTS_CALLBACK_COMPLETE", "true", false, authToken);

            // 4. Send a final log message
            await SendTaskLogFeedAsync(planUrl, projectId, hubName, planId, jobId, timelineId, "Function processing completed", authToken);

            _logger.LogInformation("Task callback processing finished successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error during callback processing: {ex.Message}");
            _logger.LogError(ex.StackTrace);

            // If there's an error, try to log it
            try
            {
                await SendTaskLogFeedAsync(planUrl, projectId, hubName, planId, jobId, timelineId, $"Error: {ex.Message}", authToken);
            }
            catch
            {
                // Just log if this also fails
                _logger.LogError("Failed to send error log");
            }
        }
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

    private async Task SetTaskVariableAsync(
        string planUrl, string projectId, string hubName,
        string planId, string timelineId, string taskInstanceId,
        string variableName, string variableValue, bool isSecret,
        string authToken)
    {
        try
        {
            _logger.LogInformation($"Setting task variable: {variableName}");

            // Get timeline records
            string timelineRecordsUrl = $"{planUrl.TrimEnd('/')}/{projectId}/_apis/distributedtask/hubs/{hubName}/plans/{planId}/timelines/{timelineId}/records?api-version=4.1";

            var response = await GetDataAsync(timelineRecordsUrl, authToken);
            if (response.IsSuccessStatusCode)
            {
                var timelineRecords = await response.Content.ReadAsStringAsync();
                dynamic timelineRecordsData = JsonConvert.DeserializeObject(timelineRecords);
                JObject taskRecord = null;

                // First get all the records
                foreach (var record in timelineRecordsData["value"])
                {
                    string recordId = record.id.ToString();
                    if (string.Equals(recordId, taskInstanceId, StringComparison.OrdinalIgnoreCase))
                    {
                        taskRecord = JObject.FromObject(record);
                        break;
                    }
                }

                if (taskRecord != null)
                {
                    _logger.LogInformation($"Found task record: {taskRecord["id"]}");

                    // Make sure variables property exists
                    if (taskRecord["variables"] == null)
                    {
                        taskRecord["variables"] = new JObject();
                    }

                    // Set the variable value
                    JObject variables = (JObject)taskRecord["variables"];
                    variables[variableName] = new JObject(
                        new JProperty("value", variableValue),
                        new JProperty("isSecret", isSecret)
                    );

                    // Update the record
                    var updatePayload = new JObject(
                        new JProperty("value", new JArray(taskRecord)),
                        new JProperty("count", 1)
                    );

                    string requestBody = JsonConvert.SerializeObject(updatePayload);
                    _logger.LogInformation($"Task variable update payload: {requestBody}");

                    var updateResponse = await PatchDataAsync(timelineRecordsUrl, requestBody, authToken);
                    if (updateResponse.IsSuccessStatusCode)
                    {
                        _logger.LogInformation($"Task variable {variableName} set successfully");
                    }
                    else
                    {
                        var errorContent = await updateResponse.Content.ReadAsStringAsync();
                        _logger.LogError($"Failed to update task record with variable: {updateResponse.StatusCode}, {errorContent}");
                    }
                }
                else
                {
                    _logger.LogError($"Task record not found for ID: {taskInstanceId}");
                }
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"Failed to get timeline records: {response.StatusCode}, {errorContent}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error setting task variable: {ex.Message}");
            _logger.LogError(ex.StackTrace);
        }
    }

    private async Task<HttpResponseMessage> PostDataAsync(string url, string requestBody, string authToken)
    {
        var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        // Note: Using Basic authentication with empty username and token as password
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

        // Note: Using Basic authentication with empty username and token as password
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
        // Note: Using Basic authentication with empty username and token as password
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