using Microsoft.AspNetCore.Mvc;
using PowerSync.Domain.Interfaces;
using PowerSync.Domain.Records;

namespace PowerSync.Api.Controllers
{
    /// <summary>
    /// Controller responsible for handling data synchronization operations such as batch updates,
    /// individual record operations (PUT, PATCH, DELETE), and checkpoint creation.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class DataController(
        IPersister persister,
        ILogger<DataController> logger) : ControllerBase
    {
        private readonly IPersister _persister = persister;
        private readonly ILogger<DataController> _logger = logger;

        /// <summary>
        /// Processes a batch request containing multiple data operations.
        /// </summary>
        /// <param name="request">The batch request containing operations to be processed</param>
        /// <returns>
        /// 200 OK if batch completed successfully
        /// 400 Bad Request if request is invalid or processing fails
        /// </returns>
        [HttpPost]
        public async Task<IActionResult> Post([FromBody] BatchRequest request)
        {
            if (request is null || request.Batch is null)
            {
                return BadRequest(new { message = "Invalid body provided" });
            }

            try
            {
                await _persister.UpdateBatchAsync(request.Batch);
                return Ok(new { message = "Batch completed" });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Request failed");
                return BadRequest(new { message = $"Request failed: {e.Message}" });
            }
        }

        /// <summary>
        /// Creates or replaces a record in the specified table.
        /// </summary>
        /// <param name="batchOperation">Operation details including table name, data, and optional ID</param>
        /// <returns>
        /// 200 OK if PUT operation completed successfully
        /// 400 Bad Request if request is invalid or processing fails
        /// </returns>
        [HttpPut]
        public async Task<IActionResult> Put([FromBody] BatchOperation batchOperation)
        {
            if (batchOperation?.Table is null || batchOperation.Data is null)
            {
                return BadRequest(new { message = "Invalid body provided" });
            }

            try
            {
                // Force operation type to PUT regardless of what was provided
                batchOperation.Op = PowerSync.Domain.Enums.OperationType.PUT;
                await _persister.UpdateBatchAsync([batchOperation]);
                return Ok(new { message = $"PUT completed for {batchOperation.Table} {batchOperation.Data["id"]}" });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "PUT request failed");
                return BadRequest(new { message = $"Request failed: {e.Message}" });
            }
        }

        /// <summary>
        /// Creates a synchronization checkpoint for a specific user and client combination.
        /// Checkpoints are used to track data synchronization state.
        /// </summary>
        /// <param name="request">Request containing optional userId and clientId</param>
        /// <returns>
        /// 200 OK with the created checkpoint details
        /// 400 Bad Request if request is null
        /// </returns>
        [HttpPut("checkpoint")]
        public async Task<IActionResult> CreateCheckpoint([FromBody] CheckpointRequest request)
        {
            if (request == null)
            {
                return BadRequest(new { message = "Invalid body provided" });
            }

            // Use provided values or defaults if not specified
            var userId = request.UserId ?? "UserID";
            var clientId = request.ClientId ?? "1";

            var checkpoint = await _persister.CreateCheckpointAsync(userId, clientId);
            return Ok(new { checkpoint });
        }

        /// <summary>
        /// Partially updates an existing record in the specified table.
        /// </summary>
        /// <param name="batchOperation">Operation details including table name and data to update</param>
        /// <returns>
        /// 200 OK if PATCH operation completed successfully
        /// 400 Bad Request if request is invalid or processing fails
        /// </returns>
        [HttpPatch]
        public async Task<IActionResult> Patch([FromBody] BatchOperation batchOperation)
        {
            if (batchOperation?.Table == null || batchOperation.Data == null)
            {
                return BadRequest(new { message = "Invalid body provided" });
            }

            try
            {
                // Force operation type to PATCH regardless of what was provided
                batchOperation.Op = PowerSync.Domain.Enums.OperationType.PATCH;
                await _persister.UpdateBatchAsync([batchOperation]);
                return Ok(new { message = $"PATCH completed for {batchOperation.Table}" });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "PATCH request failed");
                return BadRequest(new { message = $"Request failed: {e.Message}" });
            }
        }

        /// <summary>
        /// Deletes a record from the specified table using its ID.
        /// </summary>
        /// <param name="batchOperation">Operation details including table name and ID of record to delete</param>
        /// <returns>
        /// 200 OK if DELETE operation completed successfully
        /// 400 Bad Request if request is invalid or processing fails
        /// </returns>
        [HttpDelete]
        public async Task<IActionResult> Delete([FromBody] BatchOperation batchOperation)
        {
            if (batchOperation?.Table == null || batchOperation.Id == null)
            {
                return BadRequest(new { message = "Invalid body provided, expected table and data" });
            }

            try
            {
                // Force operation type to DELETE regardless of what was provided
                batchOperation.Op = PowerSync.Domain.Enums.OperationType.DELETE;
                await _persister.UpdateBatchAsync([batchOperation]);
                return Ok(new { message = $"DELETE completed for {batchOperation.Table} {batchOperation.Id}" });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "DELETE request failed");
                return BadRequest(new { message = $"Request failed: {e.Message}" });
            }
        }
    }
}