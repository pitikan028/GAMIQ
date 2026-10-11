using GAMIQ.Api.Models.Mongo;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace GAMIQ.Api.Data.Mongo;

public class MongoOptions
{
    public string ConnectionString { get; set; } = null!;
    public string DatabaseName { get; set; } = null!;
}

public class MongoContext
{
    private readonly IMongoDatabase _database;

    public MongoContext(IOptions<MongoOptions> options)
    {
        var client = new MongoClient(options.Value.ConnectionString);
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
