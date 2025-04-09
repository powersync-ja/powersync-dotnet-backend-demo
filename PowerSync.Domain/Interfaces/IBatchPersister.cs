using PowerSync.Domain.Records;

namespace PowerSync.Domain.Interfaces
{
    /// <summary>
    /// Represents a database operation batch update.
    /// </summary>
    public interface IBatchPersister
    {
        /// <summary>
        /// Applies a batch of database operations.
        /// </summary>
        /// <param name="batch">A collection of delete, put, or patch operations</param>
        /// <returns>A task representing the asynchronous operation</returns>
        Task UpdateBatchAsync(List<BatchOperation> batch);
    }
}