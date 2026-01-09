using System.Data;
using System.Text.Json;
using MySqlConnector;
using PowerSync.Domain.Enums;
using PowerSync.Domain.Interfaces;
using PowerSync.Domain.Records;

namespace PowerSync.Infrastructure.Persistence.MySQL
{
    /// <summary>
    /// MySQL implementation of the IPersister interface that handles data persistence
    /// operations for the PowerSync system using a MySQL database.
    /// </summary>
    public class MySQLPersistence : IPersister
    {
        private readonly string _connectionString;

        /// <summary>
        /// Initializes a new instance of the MySQLPersistence with a connection string or URI.
        /// </summary>
        /// <param name="uri">MySQL connection string or URI (mysql://user:pass@host:port/database)</param>
        /// <exception cref="ArgumentException">Thrown when the URI format is invalid</exception>
        public MySQLPersistence(string uri)
        {
            Console.WriteLine("Using MySQL Persister");

            try
            {
                if (uri.StartsWith("mysql://") || uri.StartsWith("mysqlx://"))
                {
                    _connectionString = ConvertUriToConnectionString(uri);
                }
                else
                {
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
        /// Converts a MySQL URI (mysql://user:pass@host:port/database) to a standard connection string.
        /// </summary>
        private static string ConvertUriToConnectionString(string uri)
        {
            try
            {
                var uriObj = new Uri(uri);
                var server = uriObj.Host;
                var port = uriObj.Port > 0 ? uriObj.Port : 3306;
                var database = uriObj.AbsolutePath.TrimStart('/');

                string username = string.Empty;
                string password = string.Empty;

                if (!string.IsNullOrEmpty(uriObj.UserInfo))
                {
                    var userInfoParts = uriObj.UserInfo.Split(':');
                    username = Uri.UnescapeDataString(userInfoParts[0]);
                    password = userInfoParts.Length > 1 ? Uri.UnescapeDataString(userInfoParts[1]) : string.Empty;
                }

                var builder = new MySqlConnectionStringBuilder
                {
                    Server = server,
                    Port = (uint)port,
                    Database = database,
                    UserID = username,
                    Password = password
                };

                return builder.ConnectionString;
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Invalid MySQL URI format: {uri}", ex);
            }
        }

        /// <summary>
        /// Escapes a MySQL identifier by wrapping it in backticks.
        /// </summary>
        private static string EscapeIdentifier(string identifier)
        {
            return $"`{identifier}`";
        }

        /// <summary>
        /// Converts a JsonElement or object value to a native .NET type that MySQL can handle.
        /// </summary>
        private static object? ConvertToNativeType(object? value)
        {
            if (value == null)
                return DBNull.Value;

            if (value is JsonElement jsonElement)
            {
                switch (jsonElement.ValueKind)
                {
                    case JsonValueKind.String:
                        var str = jsonElement.GetString();
                        if (str != null && DateTime.TryParse(str, out var dateTime))
                            return dateTime;
                        return str;
                    case JsonValueKind.Number:
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
                        return jsonElement.GetRawText();
                    default:
                        return jsonElement.GetRawText();
                }
            }

            return value;
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
        /// </summary>
        private static string? GetStringValue(Dictionary<string, object>? data, string key)
        {
            if (data == null || !data.TryGetValue(key, out var value))
                return null;
            return ConvertToNativeType(value)?.ToString();
        }

        /// <summary>
        /// Adds parameters to a MySQL command from a dictionary.
        /// </summary>
        private static void AddParameters(MySqlCommand cmd, Dictionary<string, object> data)
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
        public async Task UpdateBatchAsync(List<BatchOperation> batch)
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

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
        /// Uses MySQL's INSERT ... ON DUPLICATE KEY UPDATE for UPSERT functionality.
        /// </summary>
        private static async Task HandlePutOperation(MySqlConnection connection, MySqlTransaction transaction, BatchOperation op)
        {
            if (string.IsNullOrWhiteSpace(op.Table) || op.Data is null || op.Data.Count == 0)
                throw new ArgumentException("Table name and data are required for PUT operation");

            var table = EscapeIdentifier(op.Table);
            var id = GetOperationId(op);
            var withId = new Dictionary<string, object>(op.Data) { ["id"] = id };

            var columnNames = withId.Keys.ToList();
            var columnsEscaped = columnNames.Select(EscapeIdentifier);
            var columns = string.Join(", ", columnsEscaped);
            var placeholders = string.Join(", ", columnNames.Select(c => $"@{c}"));

            var updateClauses = op.Data.Keys
                .Where(k => !k.Equals("id", StringComparison.OrdinalIgnoreCase))
                .Select(k => $"{EscapeIdentifier(k)} = @update_{k}")
                .ToList();

            // For PUT operations, always include ON DUPLICATE KEY UPDATE clause
            // If no fields to update, update id to itself (no-op) to ensure the clause exists
            if (updateClauses.Count == 0)
            {
                updateClauses.Add($"{EscapeIdentifier("id")} = {EscapeIdentifier("id")}");
            }

            var updateClause = $"ON DUPLICATE KEY UPDATE {string.Join(", ", updateClauses)}";

            var statement = $"INSERT INTO {table} ({columns}) VALUES ({placeholders}) {updateClause}";

            await using var cmd = new MySqlCommand(statement, connection, transaction);
            
            // Add INSERT parameters
            foreach (var key in columnNames)
            {
                cmd.Parameters.AddWithValue($"@{key}", ConvertToNativeType(withId[key]));
            }
            
            // Add UPDATE parameters
            foreach (var key in op.Data.Keys.Where(k => !k.Equals("id", StringComparison.OrdinalIgnoreCase)))
            {
                cmd.Parameters.AddWithValue($"@update_{key}", ConvertToNativeType(op.Data[key]));
            }

            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Handles a PATCH operation by updating specified fields of an existing record.
        /// </summary>
        private static async Task HandlePatchOperation(MySqlConnection connection, MySqlTransaction transaction, BatchOperation op)
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

            var statement = $"UPDATE {table} SET {string.Join(", ", updateClauses)} WHERE {EscapeIdentifier("id")} = @id";

            await using var cmd = new MySqlCommand(statement, connection, transaction);
            AddParameters(cmd, withId);

            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Handles a DELETE operation by removing a record with the specified ID.
        /// </summary>
        private static async Task HandleDeleteOperation(MySqlConnection connection, MySqlTransaction transaction, BatchOperation op)
        {
            if (string.IsNullOrWhiteSpace(op.Table))
                throw new ArgumentException("Table name is required");

            var table = EscapeIdentifier(op.Table);
            var id = GetOperationId(op);
            var statement = $"DELETE FROM {table} WHERE {EscapeIdentifier("id")} = @id";

            await using var cmd = new MySqlCommand(statement, connection, transaction);
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Creates a checkpoint for a user and client combination.
        /// If a checkpoint already exists, it increments the existing checkpoint value.
        /// </summary>
        public async Task<long> CreateCheckpointAsync(string userId, string clientId)
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var statement = @"
                    INSERT INTO checkpoints (user_id, client_id, checkpoint)
                    VALUES (@user_id, @client_id, 1)
                    ON DUPLICATE KEY UPDATE checkpoint = checkpoint + 1";

                await using var cmd = new MySqlCommand(statement, connection, transaction);
                cmd.Parameters.AddWithValue("@user_id", userId);
                cmd.Parameters.AddWithValue("@client_id", clientId);
                await cmd.ExecuteNonQueryAsync();

                var selectStatement = "SELECT checkpoint FROM checkpoints WHERE user_id = @user_id AND client_id = @client_id";
                await using var selectCmd = new MySqlCommand(selectStatement, connection, transaction);
                selectCmd.Parameters.AddWithValue("@user_id", userId);
                selectCmd.Parameters.AddWithValue("@client_id", clientId);

                var result = await selectCmd.ExecuteScalarAsync();
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

