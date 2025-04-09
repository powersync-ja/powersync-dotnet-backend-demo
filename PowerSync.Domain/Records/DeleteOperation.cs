namespace PowerSync.Domain.Records
{
    /// <summary>
    /// Represents a delete operation for a database record.
    /// </summary>
    public record DeleteOp
    {
        public string Op => "DELETE";
        public required string Table { get; init; }
        public string? Id { get; init; }
        public object? Data { get; init; }
    }
}