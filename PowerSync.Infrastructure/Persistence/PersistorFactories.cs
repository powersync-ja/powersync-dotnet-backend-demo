using PowerSync.Domain.Interfaces;
using PowerSync.Infrastructure.Persistence.Postgres;

namespace PowerSync.Infrastructure.Persistence
{
    /// <summary>
    /// Provides a registry of persister factories for different database types.
    /// </summary>
    public class PersisterFactoryRegistry
    {
        private readonly Dictionary<string, IPersisterFactory> _factories;

        public PersisterFactoryRegistry()
        {
            _factories = new Dictionary<string, IPersisterFactory>
            {
                { "mongodb", new MongoPersisterFactory() },
                { "postgres", new PostgresPersisterFactory() },
                { "mysql", new MySqlPersisterFactory() }
            };
        }

        /// <summary>
        /// Gets a persister factory for a specific database type.
        /// </summary>
        /// <param name="type">The database type</param>
        /// <returns>The corresponding persister factory</returns>
        /// <exception cref="ArgumentException">Thrown when an unsupported database type is provided</exception>
        public IPersisterFactory GetFactory(string type)
        {
            if (_factories.TryGetValue(type, out var factory))
            {
                return factory;
            }

            throw new ArgumentException($"Unsupported database type: {type}");
        }
    }

    public class MongoPersisterFactory : IPersisterFactory
    {
        public IPersister CreatePersisterAsync(string uri)
        {
            // Implement MongoDB persister creation
            throw new NotImplementedException();
        }
    }

    public class PostgresPersisterFactory : IPersisterFactory
    {
        public IPersister CreatePersisterAsync(string uri)
        {
            var persister = new PostgresPersister(uri);

            // Return the created persister
            return persister;
        }
    }

    public class MySqlPersisterFactory : IPersisterFactory
    {
        public IPersister CreatePersisterAsync(string uri)
        {
            // Implement MySQL persister creation
            throw new NotImplementedException();
        }
    }
}