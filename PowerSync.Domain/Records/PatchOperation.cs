namespace PowerSync.Domain.Records
{
    /// <summary>
    /// Represents a PATCH operation for a database record.
    /// </summary>
    public record PatchOp
    {
        public string Op => "PATCH";
        public required string Table { get; init; }
        public string? Id { get; init; }
        public required object Data { get; init; }
    }
}