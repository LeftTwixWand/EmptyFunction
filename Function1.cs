using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;

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
            await Task.Delay(5000); // 5 seconds delay to simulate work

            // Call back to update the task status
            await SendCallbackAsync(planUrl, projectId, hubName, planId, jobId, timelineId, taskInstanceId, authToken);

            return new OkObjectResult(new
            {
                message = "Function received successfully. Processing in background. Will update task when complete."
            });
        }

        // Normal synchronous response
        return new OkObjectResult("Welcome to Azure Functions!");
    }

    private async Task SendCallbackAsync(
        string planUrl, string projectId, string hubName,
        string planId, string jobId, string timelineId,
        string taskInstanceId, string authToken)
    {
        try
        {
            _logger.LogInformation($"Sending callback for task: {taskInstanceId}");

            // Create timeline record update
            var timelineRecord = new
            {
                id = taskInstanceId,
                state = "completed",
                result = "succeeded", // or "failed"
                resultCode = JsonSerializer.Serialize(new
                {
                    status = "success",
                    message = "Function completed successfully!",
                    completeTask = true  // Signal to explicitly complete the task
                }),
                // Include additional fields to ensure task completion
                percentComplete = 100,
                finishTime = DateTime.UtcNow.ToString("o") // ISO 8601 format
            };

            // Create the payload with the timeline record array
            var payload = new
            {
                value = new[] { timelineRecord },
                count = 1
            };

            // Convert to JSON string
            var jsonContent = JsonSerializer.Serialize(payload);
            _logger.LogInformation($"Callback payload: {jsonContent}");

            // Set up the request - using the records update API
            var url = $"{planUrl.TrimEnd('/')}/{projectId}/_apis/distributedtask/hubs/{hubName}/plans/{planId}/timelines/{timelineId}/records?api-version=7.1";

            _logger.LogInformation($"Callback URL: {url}");

            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            // Add authorization header
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

            // Send the request - using PATCH method
            var request = new HttpRequestMessage(new HttpMethod("PATCH"), url)
            {
                Content = content
            };

            var response = await httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Callback sent successfully!");

                // Now set the task variable for compatibility with the CallbackHandler
                var variableName = $"AZURE_FUNCTION_CALLBACK_{taskInstanceId}";
                var variableValue = JsonSerializer.Serialize(new
                {
                    result = "succeeded",
                    resultCode = JsonSerializer.Serialize(new
                    {
                        status = "success",
                        message = "Function completed successfully!"
                    })
                });

                await SetTaskVariable(planUrl, projectId, hubName, planId, jobId, variableName, variableValue, authToken);
            }
            else
            {
                _logger.LogError($"Callback failed with status code: {response.StatusCode}");
                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"Response content: {responseContent}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error sending callback: {ex.Message}");
            _logger.LogError(ex.StackTrace);
        }
    }

    private async Task SetTaskVariable(
        string planUrl, string projectId, string hubName,
        string planId, string jobId, string variableName,
        string variableValue, string authToken)
    {
        try
        {
            // Create patch document to update variable
            var patchDocument = new[]
            {
                new
                {
                    op = "add",
                    path = $"/variables/{variableName}",
                    value = variableValue
                }
            };

            // Set up the request for the variables API
            var url = $"{planUrl.TrimEnd('/')}/{projectId}/_apis/distributedtask/hubs/{hubName}/plans/{planId}/jobs/{jobId}/variables?api-version=7.1";

            _logger.LogInformation($"Variable URL: {url}");

            var content = new StringContent(JsonSerializer.Serialize(patchDocument), Encoding.UTF8, "application/json-patch+json");

            // Add authorization header
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

            // Send the PATCH request
            var request = new HttpRequestMessage(new HttpMethod("PATCH"), url)
            {
                Content = content
            };

            var response = await httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation($"Task variable '{variableName}' set successfully!");
            }
            else
            {
                _logger.LogWarning($"Failed to set task variable with status code: {response.StatusCode}");
                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning($"Response content: {responseContent}");
                _logger.LogInformation("This is expected if the variables API is not available, but the callback should still work through the timeline record update.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Error setting task variable: {ex.Message}");
            _logger.LogWarning("This is not critical as the main callback was already sent through timeline records update.");
        }
    }
}