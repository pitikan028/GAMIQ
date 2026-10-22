using GAMIQ.Api.Config;
using GAMIQ.Api.Data;
using GAMIQ.Api.Data.Mongo;
using GAMIQ.Api.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

// connection strings come from .env (see .env.example), not appsettings.json
DotEnv.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var postgresConnection = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(postgresConnection))
{
    throw new InvalidOperationException(
        "ConnectionStrings:Postgres is not set. Copy .env.example to .env in the repo root first.");
}

// CP2: the current expand-contract phase, and one EF model per phase (see PhaseModelCacheKeyFactory)
builder.Services.AddSingleton<MigrationState>();
builder.Services.AddSingleton<MigrationGuard>();
builder.Services.AddSingleton<BackfillJob>();
// CP3: counts SQL / Mongo round trips per request (X-Sql-Queries, X-Mongo-Commands headers)
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<QueryCounter>();
builder.Services.AddSingleton<SqlQueryCountInterceptor>();

builder.Services.AddDbContext<AppDbContext>((services, options) => options
    .UseNpgsql(postgresConnection)
    .ReplaceService<IModelCacheKeyFactory, PhaseModelCacheKeyFactory>()
    .AddInterceptors(services.GetRequiredService<SqlQueryCountInterceptor>()));

builder.Services.Configure<MongoOptions>(builder.Configuration.GetSection("MongoDb"));
builder.Services.AddSingleton<MongoContext>();

var app = builder.Build();

// migrate + seed both databases (skipped if data already exists)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var mongo = scope.ServiceProvider.GetRequiredService<MongoContext>();

    await mongo.EnsureIndexesAsync();
    var products = await MongoSeeder.SeedAsync(mongo);
    await DbInitializer.SeedAsync(db, products);
}

// CP2: after the contract phase the legacy column is gone, so don't start in Legacy again
await app.Services.GetRequiredService<MigrationGuard>().SyncPhaseWithSchemaAsync();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

    // CP3: tell the client how many database round trips its request needed (tools/check_queries.py).
    // Development only, a real API should not show this.
    var queryCounter = app.Services.GetRequiredService<QueryCounter>();
    app.Use(async (context, next) =>
    {
        queryCounter.StartRequest(context);
        context.Response.OnStarting(() =>
        {
            var counts = queryCounter.ForRequest(context)!;
            context.Response.Headers["X-Sql-Queries"] = counts.Sql.ToString();
            context.Response.Headers["X-Mongo-Commands"] = counts.Mongo.ToString();
            return Task.CompletedTask;
        });
        await next();
    });
}

app.MapControllers();

app.Run();
