using System.Text.Json;
using Npgsql;
using PowerSync.Domain.Enums;
using PowerSync.Domain.Interfaces;
using PowerSync.Domain.Records;

namespace PowerSync.Infrastructure.Persistence.Postgres
{
    /// <summary>
    /// Postgres implementation of the IPersister interface that handles data persistence
    /// operations for the PowerSync system using a PostgreSQL database.
    /// </summary>
    public class PostgresPersister : IPersister
    {
        private readonly NpgsqlDataSource _dataSource;

        /// <summary>
        /// Initializes a new instance of the PostgresPersister with a connection string or URI.
        /// </summary>
        /// <param name="uri">PostgreSQL connection string or URI (postgres://user:pass@host:port/database)</param>
        /// <exception cref="ArgumentException">Thrown when the URI format is invalid</exception>
        public PostgresPersister(string uri)
        {
            Console.WriteLine("Using Postgres Persister");

            try
            {
                // Check if the string is a URI format
                if (uri.StartsWith("postgres://") || uri.StartsWith("postgresql://"))
                {
                    // Manually parse the URI and build a connection string
                    var connString = ConvertUriToConnectionString(uri);
                    _dataSource = NpgsqlDataSource.Create(connString);
                }
                else
                {
                    // Assume it's already in the correct format
                    _dataSource = NpgsqlDataSource.Create(uri);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Connection string error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Converts a PostgreSQL URI (postgres://user:pass@host:port/database) to a standard connection string.
        /// </summary>
        /// <param name="uri">PostgreSQL URI to convert</param>
        /// <returns>A standard PostgreSQL connection string</returns>
        /// <exception cref="ArgumentException">Thrown when the URI cannot be parsed</exception>
        private static string ConvertUriToConnectionString(string uri)
        {
            try
            {
                Uri pgUri = new(uri);

                // Extract components
                string server = pgUri.Host;
                int port = pgUri.Port > 0 ? pgUri.Port : 5432; // Default to 5432 if not specified
                string database = pgUri.AbsolutePath.TrimStart('/');

                // Parse userinfo (username:password)
                string username = string.Empty;
                string password = string.Empty;

                if (!string.IsNullOrEmpty(pgUri.UserInfo))
                {
                    string[] userInfoParts = pgUri.UserInfo.Split(':');
                    username = userInfoParts[0];
                    password = userInfoParts.Length > 1 ? userInfoParts[1] : string.Empty;
                }

                // Build connection string
                return $"Host={server};Port={port};Database={database};Username={username};Password={password};";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to parse URI: {ex.Message}");
                throw new ArgumentException($"Invalid PostgreSQL URI format: {uri}", ex);
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
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                foreach (var op in batch)
                {
                    switch (op.Op)
                    {
                        case OperationType.PUT:
                            await HandlePutOperation(connection, op);
                            break;
                        case OperationType.PATCH:
                            await HandlePatchOperation(connection, op);
                            break;
                        case OperationType.DELETE:
                            await HandleDeleteOperation(connection, op);
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
        /// Uses PostgreSQL's UPSERT functionality (INSERT ... ON CONFLICT DO UPDATE).
        /// </summary>
        /// <param name="connection">The database connection</param>
        /// <param name="op">The operation details including table, ID, and data</param>
        /// <returns>Task representing the asynchronous operation</returns>
        /// <exception cref="ArgumentException">Thrown when required parameters are missing</exception>
        private static async Task HandlePutOperation(NpgsqlConnection connection, BatchOperation op)
        {
            if (string.IsNullOrWhiteSpace(op.Table) || string.IsNullOrWhiteSpace(op.Id))
                throw new ArgumentException("Table name and Id cannot be empty");

            if (op.Data is null || op.Data.Count == 0)
                throw new ArgumentException("Data is required for PUT operation");

            // Ensure 'id' is included in the data dictionary
            var dataDict = new Dictionary<string, object>(op.Data)
            {
                ["id"] = op.Id
            };

            var jsonData = JsonSerializer.Serialize(dataDict);

            // Use json_populate_record to convert JSON to a record of the target table type
            // Then perform an UPSERT (INSERT with ON CONFLICT DO UPDATE)
            var sql = $@"
                WITH data_row AS (
                    SELECT (json_populate_record(null::{op.Table}, @data::json)).*
                )
                INSERT INTO {op.Table} SELECT * FROM data_row
                ON CONFLICT(id) DO UPDATE SET {string.Join(", ", dataDict.Keys.Where(k => k != "id").Select(k => $"{k} = EXCLUDED.{k}"))}";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@data", jsonData);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Handles a PATCH operation by updating specified fields of an existing record.
        /// Only updates the columns provided in the data dictionary.
        /// </summary>
        /// <param name="connection">The database connection</param>
        /// <param name="op">The operation details including table, ID, and data to update</param>
        /// <returns>Task representing the asynchronous operation</returns>
        /// <exception cref="ArgumentException">Thrown when required parameters are missing</exception>
        private static async Task HandlePatchOperation(NpgsqlConnection connection, BatchOperation op)
        {
            if (string.IsNullOrWhiteSpace(op.Id))
                throw new ArgumentException("Id is required for PATCH operation");

            if (op.Data is null || op.Data.Count == 0)
                throw new ArgumentException("Data is required for PATCH operation");

            // Exclude 'id' from update columns
            var updateColumns = op.Data
                .Where(kvp => !kvp.Key.Equals("id", StringComparison.CurrentCultureIgnoreCase))
                .ToList();

            // Create update clauses dynamically
            var updateClauses = updateColumns
                .Select(kvp => $"{kvp.Key} = data_row.{kvp.Key}")
                .ToList();

            // If no updatable columns, throw an exception
            if (!updateClauses.Any())
                throw new ArgumentException("No updatable columns provided");

            // Prepare the data object with ID
            var dataWithId = new Dictionary<string, object>(op.Data);
            if (!dataWithId.ContainsKey("id"))
            {
                dataWithId["id"] = op.Id;
            }

            // Generate column definitions for jsonb_to_record
            var columnDefs = string.Join(", ", dataWithId.Select(kvp => $"{kvp.Key} text")); 

            // Update only specified columns using a CTE with json_populate_record
            var statement = $@"
                WITH data_row AS (
                    SELECT * FROM jsonb_to_record(@data::jsonb) AS data_row({columnDefs})
                )
                UPDATE {op.Table}
                SET {string.Join(", ", updateClauses)}
                FROM data_row
                WHERE {op.Table}.id = data_row.id";

            await using var cmd = new NpgsqlCommand(statement, connection);
            cmd.Parameters.AddWithValue("@data", JsonSerializer.Serialize(dataWithId));

            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Handles a DELETE operation by removing a record with the specified ID.
        /// </summary>
        /// <param name="connection">The database connection</param>
        /// <param name="op">The operation details including table and ID to delete</param>
        /// <returns>Task representing the asynchronous operation</returns>
        /// <exception cref="ArgumentException">Thrown when required parameters are missing</exception>
        private static async Task HandleDeleteOperation(NpgsqlConnection connection, BatchOperation op)
        {
            if (string.IsNullOrWhiteSpace(op.Id))
                throw new ArgumentException("Id is required for DELETE operation");

            // Delete using a CTE with json_populate_record for consistency with other operations
            var sql = $@"
                WITH data_row AS (
                    SELECT (json_populate_record(null::{op.Table}, @data::json)).*
                )
                DELETE FROM {op.Table}
                USING data_row
                WHERE {op.Table}.id = data_row.id";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@data", JsonSerializer.Serialize(new { id = op.Id }));
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Creates a checkpoint for a user and client combination.
        /// If a checkpoint already exists, it increments the existing checkpoint value.
        /// </summary>
        /// <param name="userId">User identifier</param>
        /// <param name="clientId">Client identifier</param>
        /// <returns>The new checkpoint value</returns>
        public async Task<long> CreateCheckpointAsync(string userId, string clientId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();

            // Insert new checkpoint or increment existing one using ON CONFLICT
            await using var cmd = new NpgsqlCommand(@"
            INSERT INTO checkpoints(user_id, client_id, checkpoint)
            VALUES (@userId, @clientId, '1')
            ON CONFLICT (user_id, client_id)
            DO UPDATE SET checkpoint = checkpoints.checkpoint + 1
            RETURNING checkpoint", connection);

            cmd.Parameters.AddWithValue("@userId", userId);
            cmd.Parameters.AddWithValue("@clientId", clientId);

            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt64(result);
        }
    }
}