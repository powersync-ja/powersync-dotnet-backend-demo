namespace PowerSync.Domain.Interfaces
{
    /// <summary>
    /// Represents a checkpoint creation mechanism.
    /// </summary>
    public interface ICheckpointCreator
    {
        /// <summary>
        /// Creates a checkpoint for a specific user and client.
        /// </summary>
        /// <param name="userId">The user identifier</param>
        /// <param name="clientId">The client identifier</param>
        /// <returns>A task containing the checkpoint value</returns>
        Task<long> CreateCheckpointAsync(string userId, string clientId);
    }
}