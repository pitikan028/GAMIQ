namespace GAMIQ.Api.Config;

// CP2 expand-contract phases for Orders.TotalAmount -> Orders.PriceAmount + Orders.CurrencyCode.
// The order matters, the code compares them with >=.
public enum MigrationPhase
{
    Legacy,     // only TotalAmount exists / is used (CP1 behaviour, also safe right after 01_expand.sql)
    DualWrite,  // write TotalAmount AND PriceAmount + CurrencyCode, still read TotalAmount
    ReadNew,    // still write both, read PriceAmount + CurrencyCode
    Contract    // TotalAmount is not used anymore, so the column can be dropped
}

// Holds the phase the API is in right now. It starts from Migration:Phase in appsettings.json and
// can be changed while the API is running (PUT /api/v1/migration/phase), so we never have to
// restart the API in the middle of the migration. MigrationGuard checks the database is ready
// before a switch.
public class MigrationState
{
    private readonly ILogger<MigrationState> _logger;
    private volatile MigrationPhase _phase;

    public MigrationState(IConfiguration configuration, ILogger<MigrationState> logger)
    {
        _logger = logger;

        var configured = configuration["Migration:Phase"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            _phase = MigrationPhase.Legacy;
        }
        else if (TryParse(configured, out var phase))
        {
            _phase = phase;
        }
        else
        {
            throw new InvalidOperationException(
                $"Migration:Phase '{configured}' is not valid. Use one of: {string.Join(", ", Enum.GetNames<MigrationPhase>())}.");
        }

        _logger.LogInformation("Migration phase at startup: {Phase}", _phase);
    }

    public MigrationPhase Phase => _phase;

    public void Set(MigrationPhase phase)
    {
        var old = _phase;
        _phase = phase;
        _logger.LogWarning("Migration phase changed: {Old} -> {New}", old, phase);
    }

    // Enum.TryParse alone also accepts numbers like "7", so check the value is a real phase.
    public static bool TryParse(string? value, out MigrationPhase phase) =>
        Enum.TryParse(value, ignoreCase: true, out phase) && Enum.IsDefined(phase);
}
