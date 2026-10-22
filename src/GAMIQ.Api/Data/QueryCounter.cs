using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GAMIQ.Api.Data;

// CP3: counts how many database round trips one HTTP request makes, so we can prove an endpoint
// has no N+1 problem: the number has to stay the same whether a user has 1 order or 50.
//
// In Development every response gets two headers (added in Program.cs):
//   X-Sql-Queries:    SQL commands sent to PostgreSQL
//   X-Mongo-Commands: commands sent to MongoDB (find, count, update, ...)
//
// The numbers are kept in HttpContext.Items, so they belong to one request only. Work that is
// not part of a request (seeding, the backfill job) is not counted.
public class QueryCounter
{
    private const string ItemKey = "cp3.queryCount";

    private readonly IHttpContextAccessor _http;

    public QueryCounter(IHttpContextAccessor http)
    {
        _http = http;
    }

    public void StartRequest(HttpContext context) => context.Items[ItemKey] = new Counts();

    public Counts? ForRequest(HttpContext context) => context.Items[ItemKey] as Counts;

    public void AddSql() => Current()?.AddSql();

    public void AddMongo() => Current()?.AddMongo();

    private Counts? Current() => _http.HttpContext is { } context ? ForRequest(context) : null;

    public class Counts
    {
        private int _sql;
        private int _mongo;

        public int Sql => _sql;
        public int Mongo => _mongo;

        public void AddSql() => Interlocked.Increment(ref _sql);
        public void AddMongo() => Interlocked.Increment(ref _mongo);
    }
}

// EF Core calls this for every SQL command it sends (SELECT, INSERT, UPDATE, ...).
public class SqlQueryCountInterceptor : DbCommandInterceptor
{
    private readonly QueryCounter _counter;

    public SqlQueryCountInterceptor(QueryCounter counter)
    {
        _counter = counter;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        _counter.AddSql();
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        _counter.AddSql();
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        _counter.AddSql();
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        _counter.AddSql();
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        _counter.AddSql();
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        _counter.AddSql();
        return ValueTask.FromResult(result);
    }
}
