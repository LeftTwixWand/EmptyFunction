using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;

namespace FunctionApp1;

public class Function1(ILogger<Function1> logger)
{
    private static readonly HttpClient httpClient = new();

    [Function("FunctionAPIResponse")]
    public IActionResult RunApiResponse([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
    {
        logger.LogInformation("C# HTTP trigger function processed a request.");
        return new OkObjectResult("Welcome to Azure Functions!");
    }

    [Function("FunctionCallback")]
    public async Task<IActionResult> RunCallback([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
    {
        logger.LogInformation("C# HTTP trigger function processed a request.");

        // Read the request body for header information
        string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
        logger.LogInformation("Request body: {requestBody}", requestBody);

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
            logger.LogInformation("Starting callback process...");

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
            logger.LogInformation($"Sending callback for task: {taskInstanceId}");

            // Create timeline record update
            var timelineRecord = new
            {
                result = "succeeded", // or "failed"
                resultCode = JsonSerializer.Serialize(new
                {
                    status = "success",
                    message = "Function completed successfully!"
                })
            };

            // Convert to JSON string
            var jsonContent = JsonSerializer.Serialize(timelineRecord);

            // Set timeline variable
            var variableName = $"AZURE_FUNCTION_CALLBACK_{taskInstanceId}";
            var variableValue = jsonContent;

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

            // Set up the request
            var url = $"{planUrl.TrimEnd('/')}/{projectId}/_apis/distributedtask/hubs/{hubName}/plans/{planId}/jobs/{jobId}/variables?api-version=5.1-preview.1";

            logger.LogInformation($"Callback URL: {url}");

            var content = new StringContent(JsonSerializer.Serialize(patchDocument), Encoding.UTF8, "application/json-patch+json");

            // Add authorization header
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

            // Send the request
            var response = await httpClient.PatchAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("Callback sent successfully!");
            }
            else
            {
                logger.LogError($"Callback failed with status code: {response.StatusCode}");
                var responseContent = await response.Content.ReadAsStringAsync();
                logger.LogError($"Response content: {responseContent}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError($"Error sending callback: {ex.Message}");
            logger.LogError(ex.StackTrace);
        }
    }
}