using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
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
        _logger.LogInformation("C# HTTP trigger function processed a request with callback.");

        try 
        {
            // Read the request body for header information
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            _logger.LogInformation($"Request body: {requestBody}");

            // Extract all headers
            foreach (var header in req.Headers)
            {
                _logger.LogInformation($"Header: {header.Key} = {header.Value}");
            }

            // Get required headers
            var planUrl = req.Headers["PlanUrl"].FirstOrDefault() ?? "";
            var projectId = req.Headers["ProjectId"].FirstOrDefault() ?? "";
            var hubName = req.Headers["HubName"].FirstOrDefault() ?? "";
            var planId = req.Headers["PlanId"].FirstOrDefault() ?? "";
            var jobId = req.Headers["JobId"].FirstOrDefault() ?? "";
            var timelineId = req.Headers["TimelineId"].FirstOrDefault() ?? "";
            var taskInstanceId = req.Headers["TaskInstanceId"].FirstOrDefault() ?? "";
            var authToken = req.Headers["AuthToken"].FirstOrDefault() ?? "";

            // Validate required headers
            if (string.IsNullOrEmpty(planUrl) || 
                string.IsNullOrEmpty(projectId) || 
                string.IsNullOrEmpty(hubName) || 
                string.IsNullOrEmpty(planId) || 
                string.IsNullOrEmpty(jobId) || 
                string.IsNullOrEmpty(timelineId) || 
                string.IsNullOrEmpty(taskInstanceId) || 
                string.IsNullOrEmpty(authToken))
            {
                _logger.LogError("Missing required headers for callback");
                return new BadRequestObjectResult("Missing required headers for callback");
            }

            _logger.LogInformation("Starting callback processing with all required headers");
            _logger.LogInformation($"TaskInstanceId: {taskInstanceId}");

            // Simulate work
            await Task.Delay(2000);

            // Set the callback variable directly on the job record
            bool success = await SetCallbackVariableAsync(
                planUrl, projectId, hubName, planId, jobId, timelineId, taskInstanceId, authToken);

            if (success)
            {
                return new OkObjectResult(new { message = "Function executed and callback processed successfully" });
            }
            else
            {
                return new StatusCodeResult(500);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in function execution: {ex.Message}");
            _logger.LogError(ex.StackTrace);
            return new StatusCodeResult(500);
        }
    }

    private async Task<bool> SetCallbackVariableAsync(
        string planUrl, string projectId, string hubName, 
        string planId, string jobId, string timelineId, 
        string taskInstanceId, string authToken)
    {
        try
        {
            // Clear any existing headers
            httpClient.DefaultRequestHeaders.Clear();
            
            // Create the callback variable value - exactly as expected by the task
            var resultData = new 
            { 
                status = "success", 
                message = "Function completed successfully!" 
            };
            
            // Convert to JSON string
            string resultJson = JsonConvert.SerializeObject(resultData);
            
            // Create the callback value
            var callbackValue = new 
            { 
                result = "succeeded", 
                resultCode = resultJson 
            };
            
            // Convert to JSON string
            string callbackJson = JsonConvert.SerializeObject(callbackValue);
            
            _logger.LogInformation($"Callback value: {callbackJson}");
            
            // Create variable structure
            var variableValue = new 
            {
                value = callbackJson,
                isSecret = false
            };
            
            // Create variables dictionary with the specific naming pattern
            var variables = new Dictionary<string, object>
            {
                { $"AZURE_FUNCTION_CALLBACK_{taskInstanceId}", variableValue }
            };
            
            // Create the record update
            var record = new 
            {
                id = jobId, // Use the job ID, as we're updating the job-level record
                variables = variables
            };
            
            // Create the update payload
            var payload = new 
            {
                count = 1,
                value = new[] { record }
            };
            
            // Convert payload to JSON
            string jsonContent = JsonConvert.SerializeObject(payload);
            _logger.LogInformation($"Timeline update payload: {jsonContent}");
            
            // Set up the API URL
            string apiUrl = $"{planUrl.TrimEnd('/')}/{projectId}/_apis/distributedtask/hubs/{hubName}/plans/{planId}/timelines/{timelineId}/records?api-version=7.1";
            _logger.LogInformation($"Timeline API URL: {apiUrl}");
            
            // Set up the request
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            
            // Use PATCH method
            var request = new HttpRequestMessage(new HttpMethod("PATCH"), apiUrl)
            {
                Content = content
            };
            
            // Send the request
            var response = await httpClient.SendAsync(request);
            string responseContent = await response.Content.ReadAsStringAsync();
            
            _logger.LogInformation($"Timeline update response: {response.StatusCode} - {responseContent}");
            
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error setting callback variable: {ex.Message}");
            _logger.LogError(ex.StackTrace);
            return false;
        }
    }
}