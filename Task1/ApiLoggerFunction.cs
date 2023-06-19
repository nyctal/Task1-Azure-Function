using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;


public static class ApiLoggerFunction
{
    private static readonly HttpClient _httpClient = new HttpClient();
    private static readonly string _storageConnectionString = "UseDevelopmentStorage=true";
    private static readonly string _tableStorageName = "SuccessAndFailuresAttempts";
    private static readonly string _blobContainerName = "payloadforsuccess";

    [FunctionName("FetchAndStoreApiData")]
    public static async Task FetchAndStoreApiData(
        [TimerTrigger("0 */1 * * * *")] TimerInfo timer,
        ILogger log)
    {
        try
        {
            string apiUrl = "https://api.publicapis.org/random?auth=null";
            HttpResponseMessage response = await _httpClient.GetAsync(apiUrl);

            bool success = response.IsSuccessStatusCode;
            string responseBody = await response.Content.ReadAsStringAsync();

            await LogApiAttempt(success);

            if (success)
                await StorePayloadInBlob(responseBody);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error fetching and storing API data");
        }
    }

    private static async Task LogApiAttempt(bool success)
    {
        var tableClient = new TableClient(_storageConnectionString, _tableStorageName);
        await tableClient.CreateIfNotExistsAsync();

        var apiLog = new ApiLog
        {
            PartitionKey = DateTime.UtcNow.ToString("yyyyMMdd"),
            RowKey = Guid.NewGuid().ToString(),
            Success = success,
        };

        await tableClient.AddEntityAsync(apiLog);
    }

    private static async Task StorePayloadInBlob(string payload)
    {
        var blobClient = new BlobContainerClient(_storageConnectionString, _blobContainerName);
        await blobClient.CreateIfNotExistsAsync();

        string blobName = Guid.NewGuid().ToString();

        await blobClient.UploadBlobAsync(blobName, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload)));
    }

    [FunctionName("GetLogs")]
    public static async Task<IActionResult> GetLogs(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "logs")] HttpRequest req,
        ILogger log)
    {
        try
        {
            var tableConnectionString = "<your_storage_connection_string>";
            var tableName = "<your_table_name>";
            var tableClient = new TableClient(tableConnectionString, tableName);

            string fromParam = req.Query["from"];
            string toParam = req.Query["to"];

            DateTime from = DateTime.Parse(fromParam);
            DateTime to = DateTime.Parse(toParam).AddDays(1);

            string query = $"PartitionKey ge '{from.ToString("yyyyMMdd")}' and PartitionKey lt '{to.ToString("yyyyMMdd")}'";

            var logs = new List<ApiLog>();

            await foreach (ApiLog logEntry in tableClient.QueryAsync<ApiLog>(query))
            {
                logs.Add(logEntry);
            }

            return new OkObjectResult(logs);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error getting logs");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }


    [FunctionName("GetPayload")]
    public static async Task<IActionResult> GetPayload(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "logs/{logId}/payload")] HttpRequest req,
        string logId,
        ILogger log)
    {
        try
        {
            var blobClient = new BlobContainerClient(_storageConnectionString, _blobContainerName);
            await blobClient.CreateIfNotExistsAsync();

            BlobClient blob = blobClient.GetBlobClient(logId);

            if (!await blob.ExistsAsync())
                return new NotFoundResult();

            Response<BlobDownloadInfo> response = await blob.DownloadAsync();
            string payload = await new StreamReader(response.Value.Content).ReadToEndAsync();

            return new OkObjectResult(payload);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error getting payload");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }
}

public class ApiLog : ITableEntity
{
    public string PartitionKey { get; set; }
    public string RowKey { get; set; }
    public bool Success { get; set; }

    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
}

