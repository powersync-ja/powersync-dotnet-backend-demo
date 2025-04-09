namespace PowerSync.Domain.Interfaces
{
    /// <summary>
    /// Represents a complete database persister with batch update and checkpoint capabilities.
    /// </summary>
    public interface IPersister : IBatchPersister, ICheckpointCreator
    {
    }
}