namespace AnalyticsPlatform.Api.Visitors;

// An *interface* describes what something can do, not how. VisitorIdentifier
// depends on this instead of the concrete DailySaltStore, so:
//   * the real app plugs in DailySaltStore (Postgres-backed), and
//   * unit tests plug in a fake that returns a fixed salt, with no database.
// This is "dependency inversion" -- the main reason interfaces exist in most C# code.
public interface IDailySaltProvider
{
    Task<byte[]> GetTodaysSaltAsync(CancellationToken cancellationToken);
}
