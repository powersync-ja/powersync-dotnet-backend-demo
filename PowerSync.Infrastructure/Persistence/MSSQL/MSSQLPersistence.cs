using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using PowerSync.Domain.Enums;
using PowerSync.Domain.Interfaces;
using PowerSync.Domain.Records;

namespace PowerSync.Infrastructure.Persistence.MSSQL
{
    /// <summary>
    /// MSSQL implementation of the IPersister interface that handles data persistence
    /// operations for the PowerSync system using a Microsoft SQL Server database.
    /// </summary>
    public class MSSQLPersistence : IPersister
    {
        private readonly string _connectionString;

        /// <summary>
        /// Initializes a new instance of the MSSQLPersistence with a connection string or URI.
        /// </summary>
        /// <param name="uri">SQL Server connection string or URI (mssql://user:pass@host:port/database)</param>
        /// <exception cref="ArgumentException">Thrown when the URI format is invalid</exception>
        public MSSQLPersistence(string uri)
        {
            Console.WriteLine("Using MSSQL Persister");

            try
            {
                // Check if the string is a URI format
                if (uri.StartsWith("mssql://") || uri.StartsWith("sqlserver://") || uri.StartsWith("sql://"))
                {
                    // Manually parse the URI and build a connection string
                    _connectionString = ConvertUriToConnectionString(uri);
                }
                else
                {
                    // Assume it's already in the correct format
                    _connectionString = uri;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Connection string error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Converts a SQL Server URI (mssql://user:pass@host:port/database) to a standard connection string.
        /// </summary>
        /// <param name="uri">SQL Server URI to convert</param>
        /// <returns>A standard SQL Server connection string</returns>
        /// <exception cref="ArgumentException">Thrown when the URI cannot be parsed</exception>
        private static string ConvertUriToConnectionString(string uri)
        {
            try
            {
                Uri sqlUri = new(uri);

                // Extract components
                string server = sqlUri.Host;
                int port = sqlUri.Port > 0 ? sqlUri.Port : 1433; // Default to 1433 if not specified
                string database = sqlUri.AbsolutePath.TrimStart('/');

                // Parse userinfo (username:password)
                string username = string.Empty;
                string password = string.Empty;

                if (!string.IsNullOrEmpty(sqlUri.UserInfo))
                {
                    string[] userInfoParts = sqlUri.UserInfo.Split(':');
                    username = Uri.UnescapeDataString(userInfoParts[0]);
                    password = userInfoParts.Length > 1 ? Uri.UnescapeDataString(userInfoParts[1]) : string.Empty;
                }

                // Build connection string
                var builder = new SqlConnectionStringBuilder
                {
                    DataSource = $"{server},{port}",
                    InitialCatalog = database,
                    UserID = username,
                    Password = password,
                    Encrypt = true,
                    TrustServerCertificate = true
                };

                return builder.ConnectionString;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to parse URI: {ex.Message}");
                throw new ArgumentException($"Invalid SQL Server URI format: {uri}", ex);
            }
        }

        /// <summary>
        /// Escapes a SQL Server identifier by wrapping it in square brackets.
        /// </summary>
        /// <param name="identifier">The identifier to escape</param>
        /// <returns>The escaped identifier</returns>
        private static string EscapeIdentifier(string identifier)
        {
            return $"[{identifier}]";
        }

        /// <summary>
        /// Converts a JsonElement or object value to a native .NET type that SQL Server can handle.
        /// </summary>
        /// <param name="value">The value to convert (can be JsonElement, object, or null)</param>
        /// <returns>The converted value as a native type</returns>
        private static object? ConvertToNativeType(object? value)
        {
            if (value == null)
                return DBNull.Value;

            // If it's already a JsonElement, convert it to the appropriate type
            if (value is JsonElement jsonElement)
            {
                switch (jsonElement.ValueKind)
                {
                    case JsonValueKind.String:
                        var str = jsonElement.GetString();
                        // Try to parse as DateTime if it looks like a date string
                        if (str != null && DateTime.TryParse(str, out var dateTime))
                            return dateTime;
                        return str;
                    case JsonValueKind.Number:
                        // Try int first, then long, then double
                        if (jsonElement.TryGetInt32(out var intVal))
                            return intVal;
                        if (jsonElement.TryGetInt64(out var longVal))
                            return longVal;
                        return jsonElement.GetDouble();
                    case JsonValueKind.True:
                        return true;
                    case JsonValueKind.False:
                        return false;
                    case JsonValueKind.Null:
                        return DBNull.Value;
                    case JsonValueKind.Object:
                    case JsonValueKind.Array:
                        return jsonElement.GetRawText(); // Serialize objects/arrays as JSON string
                    default:
                        return jsonElement.GetRawText();
                }
            }

            // If it's already a native type, return as-is
            return value;
        }

        /// <summary>
        /// Extracts a string value from a dictionary, handling JsonElement values.
        /// </summary>
        /// <param name="data">The dictionary to extract from</param>
        /// <param name="key">The key to look up</param>
        /// <returns>The string value, or null if not found</returns>
        private static string? GetStringValue(Dictionary<string, object>? data, string key)
        {
            if (data == null || !data.TryGetValue(key, out var value))
                return null;
            return ConvertToNativeType(value)?.ToString();
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
        /// Adds parameters to a SQL command from a dictionary.
        /// </summary>
        private static void AddParameters(SqlCommand cmd, Dictionary<string, object> data)
        {
            foreach (var kvp in data)
            {
                cmd.Parameters.AddWithValue($"@{kvp.Key}", ConvertToNativeType(kvp.Value));
            }
        }

        /// <summary>
        /// Updates the database with a batch of operations (PUT, PATCH, DELETE).
        /// All operations in the batch are executed within a single transaction.
        /// </summary>
        /// <param name="batch">List of operations to perform</param>
        /// <returns>Task representing the asynchronous operation</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when an unknown operation type is encountered</exception>
        /// <exception cref="ArgumentException">Thrown when operation parameters are invalid</exception>
        public async Task UpdateBatchAsync(List<BatchOperation> batch)
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = connection.BeginTransaction();

            try
            {
                foreach (var op in batch)
                {
                    switch (op.Op)
                    {
                        case OperationType.PUT:
                            await HandlePutOperation(connection, transaction, op);
                            break;
                        case OperationType.PATCH:
                            await HandlePatchOperation(connection, transaction, op);
                            break;
                        case OperationType.DELETE:
                            await HandleDeleteOperation(connection, transaction, op);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(op.Op), $"Unknown operation type: {op.Op}");
                    }
                }
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Handles a PUT operation by inserting a new record or updating an existing one.
        /// Uses SQL Server's MERGE statement for UPSERT functionality.
        /// </summary>
        /// <param name="connection">The database connection</param>
        /// <param name="transaction">The database transaction</param>
        /// <param name="op">The operation details including table, ID, and data</param>
        /// <returns>Task representing the asynchronous operation</returns>
        /// <exception cref="ArgumentException">Thrown when required parameters are missing</exception>
        private static async Task HandlePutOperation(SqlConnection connection, SqlTransaction transaction, BatchOperation op)
        {
            if (string.IsNullOrWhiteSpace(op.Table) || op.Data is null || op.Data.Count == 0)
                throw new ArgumentException("Table name and data are required for PUT operation");

            var table = EscapeIdentifier(op.Table);
            var id = GetOperationId(op);
            var withId = new Dictionary<string, object>(op.Data) { ["id"] = id };

            var columnNames = withId.Keys.ToList();
            var columnsEscaped = columnNames.Select(EscapeIdentifier);
            var columns = string.Join(", ", columnsEscaped);
            var columnParams = string.Join(", ", columnNames.Select(c => $"@{c}"));
            var sourceColumns = string.Join(", ", columnsEscaped.Select(c => $"source.{c}"));

            var updateClauses = op.Data.Keys
                .Where(k => !k.Equals("id", StringComparison.OrdinalIgnoreCase))
                .Select(k => $"{EscapeIdentifier(k)} = source.{EscapeIdentifier(k)}")
                .ToList();

            var updateClause = updateClauses.Count > 0 
                ? $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", updateClauses)}" 
                : string.Empty;

            var statement = $@"
                MERGE INTO {table} AS t
                USING (VALUES ({columnParams})) AS source ({columns})
                  ON t.[id] = source.[id]
                {updateClause}
                WHEN NOT MATCHED THEN INSERT ({columns}) VALUES ({sourceColumns});";

            await using var cmd = new SqlCommand(statement, connection, transaction);
            AddParameters(cmd, withId);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Handles a PATCH operation by updating specified fields of an existing record.
        /// Only updates the columns provided in the data dictionary.
        /// </summary>
        /// <param name="connection">The database connection</param>
        /// <param name="transaction">The database transaction</param>
        /// <param name="op">The operation details including table, ID, and data to update</param>
        /// <returns>Task representing the asynchronous operation</returns>
        /// <exception cref="ArgumentException">Thrown when required parameters are missing</exception>
        private static async Task HandlePatchOperation(SqlConnection connection, SqlTransaction transaction, BatchOperation op)
        {
            if (op.Data is null || op.Data.Count == 0 || string.IsNullOrWhiteSpace(op.Table))
                throw new ArgumentException("Table name and data are required for PATCH operation");

            var table = EscapeIdentifier(op.Table);
            var id = GetOperationId(op);
            var withId = new Dictionary<string, object>(op.Data) { ["id"] = id };

            var updateClauses = op.Data.Keys
                .Where(k => !k.Equals("id", StringComparison.OrdinalIgnoreCase))
                .Select(k => $"{EscapeIdentifier(k)} = @{k}")
                .ToList();

            if (updateClauses.Count == 0)
                throw new ArgumentException("No updatable columns provided");

            var statement = $@"
                UPDATE {table}
                SET {string.Join(", ", updateClauses)}
                WHERE [id] = @id";

            await using var cmd = new SqlCommand(statement, connection, transaction);
            AddParameters(cmd, withId);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Handles a DELETE operation by removing a record with the specified ID.
        /// </summary>
        /// <param name="connection">The database connection</param>
        /// <param name="transaction">The database transaction</param>
        /// <param name="op">The operation details including table and ID to delete</param>
        /// <returns>Task representing the asynchronous operation</returns>
        /// <exception cref="ArgumentException">Thrown when required parameters are missing</exception>
        private static async Task HandleDeleteOperation(SqlConnection connection, SqlTransaction transaction, BatchOperation op)
        {
            if (string.IsNullOrWhiteSpace(op.Table))
                throw new ArgumentException("Table name is required");

            var table = EscapeIdentifier(op.Table);
            var id = GetOperationId(op);
            var statement = $"DELETE FROM {table} WHERE [id] = @id";
            
            await using var cmd = new SqlCommand(statement, connection, transaction);
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Creates a checkpoint for a user and client combination.
        /// If a checkpoint already exists, it increments the existing checkpoint value.
        /// Uses SQL Server's MERGE statement for UPSERT functionality.
        /// </summary>
        /// <param name="userId">User identifier</param>
        /// <param name="clientId">Client identifier</param>
        /// <returns>The new checkpoint value</returns>
        public async Task<long> CreateCheckpointAsync(string userId, string clientId)
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = connection.BeginTransaction();

            try
            {
                var statement = @"
                    MERGE INTO [checkpoints] AS t
                    USING (VALUES (@user_id, @client_id, @checkpoint)) AS source (user_id, client_id, checkpoint)
                      ON t.user_id = source.user_id AND t.client_id = source.client_id
                    WHEN MATCHED THEN 
                      UPDATE SET checkpoint = t.checkpoint + 1
                    WHEN NOT MATCHED THEN 
                      INSERT (user_id, client_id, checkpoint)
                      VALUES (source.user_id, source.client_id, source.checkpoint)
                    OUTPUT INSERTED.checkpoint;";

                await using var cmd = new SqlCommand(statement, connection, transaction);
                cmd.Parameters.AddWithValue("@user_id", userId);
                cmd.Parameters.AddWithValue("@client_id", clientId);
                cmd.Parameters.AddWithValue("@checkpoint", 1);

                var result = await cmd.ExecuteScalarAsync();
                await transaction.CommitAsync();

                return Convert.ToInt64(result);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Error creating checkpoint: {ex.Message}");
                throw;
            }
        }
    }
}

