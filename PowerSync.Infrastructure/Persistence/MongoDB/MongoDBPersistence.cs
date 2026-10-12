using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using PowerSync.Domain.Enums;
using PowerSync.Domain.Interfaces;
using PowerSync.Domain.Records;

namespace PowerSync.Infrastructure.Persistence.MongoDB
{
    /// <summary>
    /// MongoDB implementation of the IPersister interface that handles data persistence
    /// operations for the PowerSync system using a MongoDB database.
    /// </summary>
    public class MongoDBPersistence : IPersister
    {
        private readonly IMongoDatabase _database;

        /// <summary>
        /// Initializes a new instance of the MongoDBPersistence with a connection string or URI.
        /// </summary>
        /// <param name="uri">MongoDB connection string or URI (mongodb://user:pass@host:port/database)</param>
        /// <exception cref="ArgumentException">Thrown when the URI format is invalid</exception>
        public MongoDBPersistence(string uri)
        {
            Console.WriteLine("Using MongoDB Persister");

            try
            {
                var client = new MongoClient(uri);
                var databaseName = GetDatabaseNameFromUri(uri);
                _database = client.GetDatabase(databaseName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Connection string error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Extracts the database name from a MongoDB URI.
        /// </summary>
        private static string GetDatabaseNameFromUri(string uri)
        {
            try
            {
                var uriObj = new Uri(uri);
                var path = uriObj.AbsolutePath.TrimStart('/');
                if (string.IsNullOrWhiteSpace(path))
                    throw new ArgumentException("Database name not found in MongoDB URI");
                return path;
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Invalid MongoDB URI format: {uri}", ex);
            }
        }

        /// <summary>
        /// Converts a string ID to a MongoDB ObjectID if valid, otherwise returns as BsonValue.
        /// </summary>
        private static BsonValue ToMongoId(string id)
        {
            if (ObjectId.TryParse(id, out var objectId))
                return objectId;
            return new BsonString(id);
        }

        /// <summary>
        /// Converts a JsonElement or object value to a BsonValue.
        /// </summary>
        private static BsonValue ConvertToBsonValue(object? value)
        {
            if (value == null)
                return BsonNull.Value;

            if (value is JsonElement jsonElement)
            {
                return jsonElement.ValueKind switch
                {
                    JsonValueKind.String => new BsonString(jsonElement.GetString() ?? ""),
                    JsonValueKind.Number => jsonElement.TryGetInt32(out var intVal) 
                        ? new BsonInt32(intVal)
                        : jsonElement.TryGetInt64(out var longVal)
                            ? new BsonInt64(longVal)
                            : new BsonDouble(jsonElement.GetDouble()),
                    JsonValueKind.True => new BsonBoolean(true),
                    JsonValueKind.False => new BsonBoolean(false),
                    JsonValueKind.Null => BsonNull.Value,
                    JsonValueKind.Object => BsonDocument.Parse(jsonElement.GetRawText()),
                    JsonValueKind.Array => BsonDocument.Parse($"{{\"array\": {jsonElement.GetRawText()}}}")["array"].AsBsonArray,
                    _ => new BsonString(jsonElement.GetRawText())
                };
            }

            // Convert common .NET types to BsonValue
            return value switch
            {
                string s => new BsonString(s),
                int i => new BsonInt32(i),
                long l => new BsonInt64(l),
                double d => new BsonDouble(d),
                bool b => new BsonBoolean(b),
                DateTime dt => new BsonDateTime(dt),
                _ => BsonValue.Create(value)
            };
        }

        /// <summary>
        /// Converts a dictionary to a BsonDocument.
        /// </summary>
        private static BsonDocument ToBsonDocument(Dictionary<string, object> data)
        {
            var doc = new BsonDocument();
            foreach (var kvp in data)
            {
                doc[kvp.Key] = ConvertToBsonValue(kvp.Value);
            }
            return doc;
        }

        /// <summary>
        /// Gets the ID from the operation, falling back to data dictionary if needed.
        /// </summary>
        private static string GetOperationId(BatchOperation op)
        {
            return op.Id ?? GetStringValue(op.Data, "id")
                ?? throw new ArgumentException("Id is required");
        }

        /// <summary>
        /// Extracts a string value from a dictionary, handling JsonElement values.
        /// Treats null values as if the key doesn't exist.
        /// </summary>
        private static string? GetStringValue(Dictionary<string, object>? data, string key)
        {
            if (data == null || !data.TryGetValue(key, out var value))
                return null;

            // Treat null values (including JsonElement with Null kind) as missing
            if (value == null)
                return null;

            if (value is JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == JsonValueKind.Null)
                    return null;
                
                return jsonElement.ValueKind == JsonValueKind.String 
                    ? jsonElement.GetString() 
                    : jsonElement.GetRawText();
            }

            return value?.ToString();
        }

        /// <summary>
        /// Updates the database with a batch of operations (PUT, PATCH, DELETE).
        /// All operations in the batch are executed sequentially.
        /// </summary>
        public async Task UpdateBatchAsync(List<BatchOperation> batch)
        {
            foreach (var op in batch)
            {
                if (string.IsNullOrWhiteSpace(op.Table))
                {
                    Console.WriteLine($"[MONGO] Skipping operation with no table name");
                    continue;
                }

                var collection = _database.GetCollection<BsonDocument>(op.Table);
                var id = GetOperationId(op);

                switch (op.Op)
                {
                    case OperationType.PUT:
                        await HandlePutOperation(collection, op, id);
                        break;
                    case OperationType.PATCH:
                        await HandlePatchOperation(collection, op, id);
                        break;
                    case OperationType.DELETE:
                        await HandleDeleteOperation(collection, op, id);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(op.Op), $"Unknown operation type: {op.Op}");
                }
            }
        }

        /// <summary>
        /// Handles a PUT operation by inserting a new record or replacing an existing one.
        /// </summary>
        private static async Task HandlePutOperation(IMongoCollection<BsonDocument> collection, BatchOperation op, string id)
        {
            if (op.Data is null || op.Data.Count == 0)
                throw new ArgumentException("Data is required for PUT operation");

            var mongoId = ToMongoId(id);
            var doc = ToBsonDocument(op.Data);
            doc["_id"] = mongoId;
            doc.Remove("id");

            var filter = new BsonDocument("_id", mongoId);
            var options = new ReplaceOptions { IsUpsert = true };

            var result = await collection.ReplaceOneAsync(filter, doc, options);
            Console.WriteLine($"[MONGO] PUT {op.Table} id={id} matched={result.MatchedCount} modified={result.ModifiedCount}");
        }

        /// <summary>
        /// Handles a PATCH operation by updating specified fields of an existing record.
        /// </summary>
        private static async Task HandlePatchOperation(IMongoCollection<BsonDocument> collection, BatchOperation op, string id)
        {
            if (op.Data is null || op.Data.Count == 0)
            {
                Console.WriteLine($"[MONGO] Skipping PATCH operation on {op.Table} with no data");
                return;
            }

            var mongoId = ToMongoId(id);
            var filter = new BsonDocument("_id", mongoId);
            var updateDoc = new BsonDocument();

            foreach (var kvp in op.Data)
            {
                if (!kvp.Key.Equals("id", StringComparison.OrdinalIgnoreCase))
                {
                    updateDoc[kvp.Key] = ConvertToBsonValue(kvp.Value);
                }
            }

            if (updateDoc.ElementCount == 0)
            {
                Console.WriteLine($"[MONGO] Skipping PATCH operation on {op.Table} with no updatable fields");
                return;
            }

            var update = new BsonDocument("$set", updateDoc);
            var result = await collection.UpdateOneAsync(filter, update);
            Console.WriteLine($"[MONGO] PATCH {op.Table} id={id} matched={result.MatchedCount} modified={result.ModifiedCount}");
        }

        /// <summary>
        /// Handles a DELETE operation by removing a record with the specified ID.
        /// </summary>
        private static async Task HandleDeleteOperation(IMongoCollection<BsonDocument> collection, BatchOperation op, string id)
        {
            var mongoId = ToMongoId(id);
            var filter = new BsonDocument("_id", mongoId);
            var result = await collection.DeleteOneAsync(filter);
            Console.WriteLine($"[MONGO] DELETE {op.Table} id={id} deleted={result.DeletedCount}");
        }

        /// <summary>
        /// Creates a checkpoint for a user and client combination.
        /// If a checkpoint already exists, it increments the existing checkpoint value.
        /// </summary>
        public async Task<long> CreateCheckpointAsync(string userId, string clientId)
        {
            var collection = _database.GetCollection<BsonDocument>("checkpoints");
            var filter = new BsonDocument
            {
                { "user_id", userId },
                { "client_id", clientId }
            };

            var update = new BsonDocument("$inc", new BsonDocument("checkpoint", 1));
            var options = new FindOneAndUpdateOptions<BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            };

            var result = await collection.FindOneAndUpdateAsync(filter, update, options);
            if (result == null)
                throw new InvalidOperationException("Failed to create or retrieve checkpoint: FindOneAndUpdateAsync returned null");
            
            return result["checkpoint"].AsInt64;
        }
    }
}

