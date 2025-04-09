using PowerSync.Domain.Enums;

namespace PowerSync.Domain.Records
{
    /// <summary>
    /// Represents a batch operation for a database record.
    /// </summary>
    public record BatchOperation
    {
        public OperationType Op { get; set; }
        public string? Table { get; init; }
        public string? Id { get; init; }
        public Dictionary<string, object>? Data { get; init; }
    }
}