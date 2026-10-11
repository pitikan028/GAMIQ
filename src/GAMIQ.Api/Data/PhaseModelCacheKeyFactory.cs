using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace GAMIQ.Api.Data;

// EF Core normally builds the model once and caches it per DbContext type. During the CP2
// migration the Orders table has different columns in different phases, so we add the phase to
// the cache key. EF then keeps one model per phase and picks the right one for each request.
public class PhaseModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        context is AppDbContext app
            ? (context.GetType(), app.Phase, designTime)
            : (object)(context.GetType(), designTime);
}
