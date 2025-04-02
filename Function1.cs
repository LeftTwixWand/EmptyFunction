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

        // Check if we need to handle callback by reading headers
        var planUrl = req.Headers["PlanUrl"].ToString();
        var projectId = req.Headers["ProjectId"].ToString();
        var hubName = req.Headers["HubName"].ToString();
        var planId = req.Headers["PlanId"].ToString();
        var jobId = req.Headers["JobId"].ToString();
        var timelineId = req.Headers["TimelineId"].ToString();
        var taskInstanceId = req.Headers["TaskInstanceId"].ToString();
        var authToken = req.Headers["AuthToken"].ToString();

        // Log all headers for debugging
        foreach (var header in req.Headers)
        {
            _logger.LogInformation($"Header: {header.Key} = {header.Value}");
        }

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

            // We need to set a variable at the job level that follows the pattern:
            // AZURE_FUNCTION_CALLBACK_{taskInstanceId}
            
            // Format the callback variable content - EXACT format is critical!
            string variableValue = JsonSerializer.Serialize(new
            {
                result = "succeeded",
                resultCode = JsonSerializer.Serialize(new
                {
                    status = "success",
                    message = "Function completed successfully!"
                })
            });

            _logger.LogInformation($"Variable value: {variableValue}");
            
            // Create a patch operation for the timeline record
            var timelineRecord = new
            {
                // Use the job ID here since we need to add the variable to the job record
                id = jobId,
                variables = new Dictionary<string, object>
                {
                    {
                        $"AZURE_FUNCTION_CALLBACK_{taskInstanceId}",
                        new
                        {
                            value = variableValue,
                            isSecret = false
                        }
                    }
                }
            };

            var patchPayload = new
            {
                value = new[] { timelineRecord },
                count = 1
            };

            string jsonContent = JsonSerializer.Serialize(patchPayload);
            _logger.LogInformation($"Payload: {jsonContent}");

            // Construct the URL to update the timeline record
            string apiUrl = $"{planUrl.TrimEnd('/')}/{projectId}/_apis/distributedtask/hubs/{hubName}/plans/{planId}/timelines/{timelineId}/records?api-version=7.1";
            _logger.LogInformation($"API URL: {apiUrl}");

            // Create an HTTP request to update the timeline record
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
            
            // Use PATCH method for updating the timeline record
            var request = new HttpRequestMessage(new HttpMethod("PATCH"), apiUrl)
            {
                Content = content
            };

            var response = await httpClient.SendAsync(request);
            string responseContent = await response.Content.ReadAsStringAsync();
            
            _logger.LogInformation($"Callback response: {response.StatusCode} - {responseContent}");

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Failed to update timeline record. Status: {response.StatusCode}, Response: {responseContent}");
            }
            else
            {
                _logger.LogInformation("Successfully updated timeline record with callback variable");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error sending callback: {ex.Message}");
            _logger.LogError(ex.StackTrace);
        }
    }
}