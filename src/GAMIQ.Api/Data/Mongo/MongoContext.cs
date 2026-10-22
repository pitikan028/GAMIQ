using GAMIQ.Api.Models.Mongo;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;

namespace GAMIQ.Api.Data.Mongo;

public class MongoOptions
{
    public string ConnectionString { get; set; } = null!;
    public string DatabaseName { get; set; } = null!;
}

public class MongoContext
{
    private readonly IMongoDatabase _database;

    // commands the driver sends by itself to open and check connections, not part of our queries
    private static readonly HashSet<string> ConnectionCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "hello", "isMaster", "saslStart", "saslContinue", "buildInfo", "ping", "endSessions"
    };

    public MongoContext(IOptions<MongoOptions> options, QueryCounter counter)
    {
        // CP3: count every command an HTTP request sends to Mongo (see QueryCounter)
        var settings = MongoClientSettings.FromConnectionString(options.Value.ConnectionString);
        settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(e =>
        {
            if (!ConnectionCommands.Contains(e.CommandName))
            {
                counter.AddMongo();
            }
        });

        var client = new MongoClient(settings);
        _database = client.GetDatabase(options.Value.DatabaseName);
    }

    public IMongoCollection<Product> Products => _database.GetCollection<Product>("products");
    public IMongoCollection<Review> Reviews => _database.GetCollection<Review>("reviews");

    // createIndexes does nothing if the index already exists, so this is fine on every startup
    public async Task EnsureIndexesAsync()
    {
        await Products.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<Product>(Builders<Product>.IndexKeys.Descending(p => p.CreatedAt)),
            new CreateIndexModel<Product>(Builders<Product>.IndexKeys.Ascending(p => p.Genres)),
            new CreateIndexModel<Product>(Builders<Product>.IndexKeys.Text(p => p.Name))
        });

        await Reviews.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<Review>(Builders<Review>.IndexKeys
                .Ascending(r => r.ProductId)
                .Descending(r => r.CreatedAt)),
            new CreateIndexModel<Review>(Builders<Review>.IndexKeys.Ascending(r => r.UserId))
        });
    }
}
