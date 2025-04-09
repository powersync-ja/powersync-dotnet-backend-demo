namespace PowerSync.Domain.Records
{
    public record BatchRequest
    {
        public List<BatchOperation>? Batch { get; set; }
    }
}