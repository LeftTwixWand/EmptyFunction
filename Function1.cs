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

            // Create two separate timeline records:
            // 1. First, update a job-level record with our variable
            // 2. Then, update the task-level record to mark it as complete

            // First, update the job record with our variable
            await SetJobVariableAsync(planUrl, projectId, hubName, planId, jobId, timelineId, taskInstanceId, authToken);

            // Then, mark the task as complete
            await UpdateTaskRecordAsync(planUrl, projectId, hubName, planId, timelineId, taskInstanceId, authToken);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error sending callback: {ex.Message}");
            _logger.LogError(ex.StackTrace);
        }
    }

    private async Task SetJobVariableAsync(
        string planUrl, string projectId, string hubName,
        string planId, string jobId, string timelineId,
        string taskInstanceId, string authToken)
    {
        try
        {
            _logger.LogInformation($"Setting job-level variable for callback: {taskInstanceId}");

            // Format the variable value exactly as expected by CallbackHandler
            string variableValue = JsonSerializer.Serialize(new
            {
                result = "succeeded",
                resultCode = JsonSerializer.Serialize(new
                {
                    status = "success",
                    message = "Function completed successfully!"
                })
            });

            // Create a variables dictionary using the exact VariableValue structure
            // from the Azure DevOps API documentation
            var variables = new Dictionary<string, object>
            {
                {
                    $"AZURE_FUNCTION_CALLBACK_{taskInstanceId}",
                    new
                    {
                        value = variableValue,
                        isSecret = false
                    }
                }
            };

            // Create a record update for the job record
            var jobRecord = new
            {
                id = jobId, // Important! Use the jobId here, not taskInstanceId
                variables = variables
            };

            // Create the payload with just the variables update
            var payload = new
            {
                value = new[] { jobRecord },
                count = 1
            };

            // Convert to JSON string
            var jsonContent = JsonSerializer.Serialize(payload);
            _logger.LogInformation($"Job variable payload: {jsonContent}");

            // Set up the request - using the records update API
            var url = $"{planUrl.TrimEnd('/')}/{projectId}/_apis/distributedtask/hubs/{hubName}/plans/{planId}/timelines/{timelineId}/records?api-version=7.1";

            _logger.LogInformation($"Job variable URL: {url}");

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
                _logger.LogInformation("Job variable set successfully!");
            }
            else
            {
                _logger.LogError($"Job variable update failed with status code: {response.StatusCode}");
                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"Response content: {responseContent}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error setting job variable: {ex.Message}");
            _logger.LogError(ex.StackTrace);
        }
    }

    private async Task UpdateTaskRecordAsync(
        string planUrl, string projectId, string hubName,
        string planId, string timelineId,
        string taskInstanceId, string authToken)
    {
        try
        {
            _logger.LogInformation($"Updating task record as complete: {taskInstanceId}");

            // Create timeline record update for the task
            var taskRecord = new
            {
                id = taskInstanceId,
                state = "completed",
                result = "succeeded",
                resultCode = JsonSerializer.Serialize(new
                {
                    status = "success",
                    message = "Function completed successfully!",
                    completeTask = true
                }),
                percentComplete = 100,
                finishTime = DateTime.UtcNow.ToString("o")
            };

            // Create the payload with the task record update
            var payload = new
            {
                value = new[] { taskRecord },
                count = 1
            };

            // Convert to JSON string
            var jsonContent = JsonSerializer.Serialize(payload);
            _logger.LogInformation($"Task record payload: {jsonContent}");

            // Set up the request - using the records update API
            var url = $"{planUrl.TrimEnd('/')}/{projectId}/_apis/distributedtask/hubs/{hubName}/plans/{planId}/timelines/{timelineId}/records?api-version=7.1";

            _logger.LogInformation($"Task record URL: {url}");

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
                _logger.LogInformation("Task record updated successfully!");
            }
            else
            {
                _logger.LogError($"Task record update failed with status code: {response.StatusCode}");
                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"Response content: {responseContent}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error updating task record: {ex.Message}");
            _logger.LogError(ex.StackTrace);
        }
    }
}